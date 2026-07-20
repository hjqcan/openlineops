using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Npgsql;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Projects.Api.Integrations;
using RabbitMQ.Client;

namespace OpenLineOps.Runner.Tests;

public sealed partial class RunnerPublishedProjectProcessE2ETests
{
    private const string RunnerBundleRootVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_RUNNER_BUNDLE_ROOT";
    private const string AgentBundleRootVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_AGENT_BUNDLE_ROOT";
    private const string PostgreSqlConnectionStringVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_POSTGRES_CONNECTION_STRING";
    private const string RabbitMqUriVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_RABBITMQ_URI";
    private const string EvidencePathVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_EVIDENCE_PATH";
    private const string GateEnabledVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_ENABLED";
    private const string GateScopeIdVariable =
        "OPENLINEOPS_RUNNER_AGENT_GATE_SCOPE_ID";
    private const string RunnerServiceScopeVariable =
        "OPENLINEOPS_RUNNER_STAGED_AGENT_SERVICE_SCOPE";
    private const string AgentServiceCleanupManifestPathVariable =
        "OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH";
    private const string StationSystemId = "station.main";
    private const string SigningKeyId = "runner-process-e2e-signing";
    private const string ExactTestName =
        "OpenLineOps.Runner.Tests.RunnerPublishedProjectProcessE2ETests."
        + "PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent";
    private static readonly JsonSerializerOptions GateEvidenceJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void CleanupDiscoveryIncludesStagingOnlyPackageTransactions()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-runner-cleanup-discovery-{Guid.NewGuid():N}");
        var contentSha256 = new string('a', 64);
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(
                root,
                $".{contentSha256}.{Guid.NewGuid():N}.committing"));

            Assert.Equal(
                contentSha256,
                Assert.Single(ImmutableContentCacheCleanupDiscovery
                    .DiscoverPackageContentHashes(root)));

            Directory.CreateDirectory(Path.Combine(root, "unexpected"));
            Assert.Throws<InvalidDataException>(() =>
                ImmutableContentCacheCleanupDiscovery
                    .DiscoverPackageContentHashes(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent()
    {
        var prerequisites = ResolveStagedAgentPrerequisites();
        if (prerequisites is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The staged Runner-to-Agent process gate requires Windows executables.");
        }

        var suffix = prerequisites.ScopeId;
        var root = prerequisites.Service.OwnedRoot;
        var stagedAgentBundleRoot = Path.GetDirectoryName(
            prerequisites.Service.ExecutablePath)
            ?? throw new InvalidDataException(
                "Runner Agent cleanup contract executable has no parent directory.");
        var projectRoot = Path.Combine(root, "project");
        var agentDataRoot = Path.Combine(root, "agent-data");
        var agentPackageCacheRoot = Path.Combine(
            Path.GetDirectoryName(root)
            ?? throw new InvalidDataException("Runner Agent owned root has no parent."),
            $"olo-runner-staged-agent-content-{suffix}",
            "content");
        var agentRuntimeRoot = Path.Combine(root, "agent-runtime");
        var agentArtifactRoot = Path.Combine(root, "agent-artifacts");
        var coordinatorId = $"runner-agent-{suffix}";
        var agentId = $"agent-{suffix}";
        var stationId = $"station-{suffix}";
        var productionRunId = Guid.NewGuid();
        var productionUnitId = Guid.NewGuid();
        var cleanupFailures = new List<Exception>();
        Exception? executionFailure = null;
        GateObservation? observation = null;
        PostgreSqlGateScope? postgres = null;
        RabbitMqGateTopology? rabbitMq = null;
        RunnerAgentWindowsService? agent = null;
        GateProcessCapture? runnerCapture = null;
        var serviceInstallationAttempted = false;
        var serviceDeletionProven = false;

        EnsureNoReparseInPath(Path.GetDirectoryName(root)!);
        Directory.CreateDirectory(root);
        ProtectRunnerServiceRoot(root);
        Directory.CreateDirectory(projectRoot);
        try
        {
            var runnerBundle = AttestBundle(
                prerequisites.RunnerBundleRoot,
                "runner",
                "headless-runner",
                "OpenLineOps.Runner.exe");
            var releaseAgentBundle = AttestBundle(
                prerequisites.AgentBundleRoot,
                "agent",
                "station-agent-service",
                "OpenLineOps.Agent.exe");
            CopyFrozenBundle(prerequisites.AgentBundleRoot, stagedAgentBundleRoot);
            var agentBundle = AttestBundle(
                stagedAgentBundleRoot,
                "agent",
                "station-agent-service",
                "OpenLineOps.Agent.exe");
            if (!string.Equals(
                    agentBundle.ExecutableSha256,
                    releaseAgentBundle.ExecutableSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    agentBundle.ManifestSha256,
                    releaseAgentBundle.ManifestSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    agentBundle.ChecksumsSha256,
                    releaseAgentBundle.ChecksumsSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Runner Agent service copy differs from its staged release bundle.");
            }

            if (!string.Equals(
                    agentBundle.ExecutablePath,
                    prerequisites.Service.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    agentBundle.ExecutableSha256,
                    prerequisites.Service.ExecutableSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Runner Agent service copy differs from its strict cleanup contract.");
            }
            var safetyExecutable = AttestIndependentSafetyExecutable();

            PublishedRunnerProject published;
            using (var factory = CreateApiFactory(root))
            using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            }))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiToken);
                published = await PublishRunnableProjectAsync(client, projectRoot, suffix);
            }

            var package = AttestPublishedPackage(root, published);
            var publicKeyPath = ExportPublishedPackagePublicKey(root);
            postgres = await PostgreSqlGateScope.CreateAsync(
                prerequisites.PostgreSqlConnectionString,
                $"olo_runner_agent_{suffix}");
            rabbitMq = await RabbitMqGateTopology.CreateAsync(
                prerequisites.RabbitMqUri,
                coordinatorId,
                agentId,
                stationId);

            var closedArtifactPort = FindClosedLoopbackPort();
            Assert.False(await CanConnectToLoopbackPortAsync(closedArtifactPort));
            serviceInstallationAttempted = true;
            agent = RunnerAgentWindowsService.InstallAndStart(
                prerequisites.Service,
                stagedAgentBundleRoot,
                CreateAgentEnvironment(
                    root,
                    agentDataRoot,
                    agentPackageCacheRoot,
                    agentRuntimeRoot,
                    agentArtifactRoot,
                    Path.Combine(root, "station-packages", "distribution"),
                    package.ContentSha256,
                    publicKeyPath,
                    prerequisites.RabbitMqUri,
                    agentId,
                    stationId,
                    suffix,
                    closedArtifactPort,
                    safetyExecutable.ExecutablePath),
                agentPackageCacheRoot,
                [
                    agentDataRoot,
                    agentRuntimeRoot,
                    agentArtifactRoot,
                    Path.Combine(root, "agent-temp"),
                    Path.Combine(root, "safety-work")
                ]);
            await rabbitMq.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(45));
            agent.VerifyProvisionedPackageCache();

            runnerCapture = await RunStagedRunnerAsync(
                runnerBundle.ExecutablePath,
                runnerBundle.ExecutableSha256,
                projectRoot,
                published,
                postgres.ConnectionString,
                prerequisites.RabbitMqUri,
                coordinatorId,
                agentId,
                stationId,
                productionRunId,
                productionUnitId,
                suffix);
            Assert.True(
                runnerCapture.ExitCode == RunnerExitCodes.Success,
                $"The staged Runner exited with {runnerCapture.ExitCode}. "
                + $"stdout: {runnerCapture.StandardOutput} stderr: {runnerCapture.StandardError}");
            using var runnerJson = ParseSingleJsonOutput(new RunnerProcessResult(
                runnerCapture.ExitCode,
                runnerCapture.StandardOutput,
                runnerCapture.StandardError));
            var terminal = VerifyRunnerTerminal(
                runnerJson.RootElement,
                published,
                productionRunId);

            await agent.StopAsync(TimeSpan.FromSeconds(45));
            agent.Dispose();
            serviceDeletionProven = agent.ServiceDeletionVerified;
            Assert.True(serviceDeletionProven);
            Assert.True(agent.ServiceLifecycleVerified);
            Assert.False(await CanConnectToLoopbackPortAsync(closedArtifactPort));

            var postgresEvidence = await ReadPostgreSqlEvidenceAsync(
                postgres.ConnectionString,
                productionRunId);
            var agentEvidence = await ReadAgentSqliteEvidenceAsync(
                Path.Combine(agentDataRoot, "station-agent.sqlite"),
                productionRunId);
            var rabbitEvidence = await rabbitMq.ReadDrainStateAsync();
            var traceEvidence = await ReadTraceEvidenceAsync(projectRoot, productionRunId);
            Assert.Empty(Directory.Exists(agentArtifactRoot)
                ? Directory.EnumerateFiles(agentArtifactRoot, "*", SearchOption.AllDirectories)
                : Array.Empty<string>());

            observation = new GateObservation(
                published,
                productionRunId,
                productionUnitId,
                runnerBundle,
                agentBundle,
                package,
                safetyExecutable,
                runnerCapture.ProcessId,
                agent.ProcessId,
                runnerCapture.RunningImageSha256,
                agent.RunningImageSha256,
                runnerCapture.MainModuleBound,
                agent.MainModuleBound,
                runnerCapture.JobObjectBound,
                runnerCapture.ProcessTreeTerminated,
                agent.ServiceName,
                agent.ServiceLifecycleVerified,
                agent.ServiceAccountName,
                agent.ServiceAccountSid,
                agent.ServiceSidSha256,
                agent.TokenEvidence.HasRestrictions,
                agent.TokenEvidence.ServiceLogonSidPresent,
                agent.TokenEvidence.ServiceLogonSidEnabled,
                agent.TokenEvidence.ExactServiceSidPresent,
                agent.TokenEvidence.ExactServiceSidEnabled,
                agent.TokenEvidence.ExactServiceSidRestricted,
                agent.TokenEvidence.NonAdministrative,
                terminal,
                postgresEvidence,
                agentEvidence,
                rabbitEvidence,
                traceEvidence,
                ArtifactEndpointContactCount: 0);
        }
        catch (Exception exception)
        {
            if (agent is null)
            {
                executionFailure = exception;
            }
            else
            {
                try
                {
                    if (!agent.ServiceLifecycleVerified)
                    {
                        await agent.StopAsync(TimeSpan.FromSeconds(45));
                    }

                    var agentOutput = await agent.GetCapturedOutputAsync();
                    executionFailure = new InvalidOperationException(
                        $"{exception.Message} Agent stdout: {agentOutput.StandardOutput} "
                        + $"Agent stderr: {agentOutput.StandardError}",
                        exception);
                }
                catch (Exception diagnosticException)
                {
                    executionFailure = new AggregateException(
                        "The staged gate failed and Agent diagnostics could not be captured.",
                        exception,
                        diagnosticException);
                }
            }
        }
        finally
        {
            await CaptureCleanupFailureAsync(cleanupFailures, async () =>
            {
                if (agent is not null && !agent.ServiceLifecycleVerified)
                {
                    await agent.StopAsync(TimeSpan.FromSeconds(45));
                }
            });
            CaptureCleanupFailure(cleanupFailures, () =>
            {
                agent?.Dispose();
                if (agent is not null)
                {
                    serviceDeletionProven = agent.ServiceDeletionVerified;
                }
            });

            await CaptureCleanupFailureAsync(cleanupFailures, async () =>
            {
                if (rabbitMq is not null)
                {
                    await rabbitMq.DeleteQueuesAsync();
                }
            });
            await CaptureCleanupFailureAsync(cleanupFailures, async () =>
            {
                if (rabbitMq is not null)
                {
                    await rabbitMq.DisposeAsync();
                }
            });

            await CaptureCleanupFailureAsync(cleanupFailures, async () =>
            {
                if (postgres is not null)
                {
                    await postgres.DisposeAsync();
                }
            });

            if (!serviceInstallationAttempted || serviceDeletionProven)
            {
                CaptureCleanupFailure(
                    cleanupFailures,
                    () => DeleteGateRoot(
                        root,
                        agentPackageCacheRoot,
                        suffix,
                        prerequisites.Service.ServiceSid));
            }
            else
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Runner Agent SCM cleanup is unproven; preserving its owned root for the independent cleanup gate."));
            }
        }

        if (executionFailure is not null)
        {
            cleanupFailures.Insert(0, executionFailure);
        }

        if (cleanupFailures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(cleanupFailures[0])
                .Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(
                "The staged Runner-to-Agent gate failed and cleanup was not complete.",
                cleanupFailures);
        }

        Assert.NotNull(observation);
        Assert.NotNull(postgres);
        Assert.NotNull(rabbitMq);
        Assert.NotNull(runnerCapture);
        Assert.True(postgres.SchemaDropped);
        Assert.True(rabbitMq.QueuesDeleted);
        Assert.False(Directory.Exists(root));
        Assert.True(runnerCapture.ProcessTreeTerminated);
        Assert.True(observation.AgentServiceLifecycleVerified);
        Assert.NotEqual(runnerCapture.ProcessId, observation.AgentProcessId);
        await WriteGateEvidenceAsync(
            prerequisites.EvidencePath,
            observation,
            postgres.SchemaDropped,
            rabbitMq.QueuesDeleted,
            runnerCapture.ProcessTreeTerminated,
            agentServiceDeleted: observation.AgentServiceLifecycleVerified,
            temporaryRootDeleted: true);
    }

    [SupportedOSPlatform("windows")]
    private static StagedAgentPrerequisites? ResolveStagedAgentPrerequisites()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RunnerBundleRootVariable] = Environment.GetEnvironmentVariable(RunnerBundleRootVariable),
            [AgentBundleRootVariable] = Environment.GetEnvironmentVariable(AgentBundleRootVariable),
            [PostgreSqlConnectionStringVariable] =
                Environment.GetEnvironmentVariable(PostgreSqlConnectionStringVariable),
            [RabbitMqUriVariable] = Environment.GetEnvironmentVariable(RabbitMqUriVariable),
            [EvidencePathVariable] = Environment.GetEnvironmentVariable(EvidencePathVariable),
            [GateEnabledVariable] = Environment.GetEnvironmentVariable(GateEnabledVariable),
            [GateScopeIdVariable] = Environment.GetEnvironmentVariable(GateScopeIdVariable),
            [RunnerServiceScopeVariable] =
                Environment.GetEnvironmentVariable(RunnerServiceScopeVariable),
            [AgentServiceCleanupManifestPathVariable] =
                Environment.GetEnvironmentVariable(AgentServiceCleanupManifestPathVariable)
        };
        if (values.Values.All(static value => value is null))
        {
            return null;
        }

        var runnerBundleRoot = RequiredGateDirectory(
            values[RunnerBundleRootVariable],
            RunnerBundleRootVariable);
        var agentBundleRoot = RequiredGateDirectory(
            values[AgentBundleRootVariable],
            AgentBundleRootVariable);
        var postgres = RequiredGateText(
            values[PostgreSqlConnectionStringVariable],
            PostgreSqlConnectionStringVariable);
        var postgresBuilder = new NpgsqlConnectionStringBuilder(postgres);
        if (string.IsNullOrWhiteSpace(postgresBuilder.Host))
        {
            throw new InvalidDataException(
                $"{PostgreSqlConnectionStringVariable} must identify at least one PostgreSQL host.");
        }

        var postgresHosts = postgresBuilder.Host.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var postgresIsLoopback = postgresHosts.Length > 0
                                 && postgresHosts.All(host =>
                                 {
                                     var addresses = Dns.GetHostAddresses(host);
                                     return addresses.Length > 0
                                            && addresses.All(IPAddress.IsLoopback);
                                 });
        var trustsUnverifiedServerCertificate = postgres
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment.Split('=', 2, StringSplitOptions.TrimEntries))
            .Any(static pair =>
                pair.Length == 2
                && string.Equals(
                    pair[0].Replace(" ", string.Empty, StringComparison.Ordinal),
                    "TrustServerCertificate",
                    StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(pair[1], out var enabled)
                && enabled);
        if (!postgresIsLoopback
            && (postgresBuilder.SslMode != SslMode.VerifyFull
                || trustsUnverifiedServerCertificate))
        {
            throw new InvalidDataException(
                $"{PostgreSqlConnectionStringVariable} must use VerifyFull certificate validation outside loopback.");
        }
        var rabbitText = RequiredGateText(values[RabbitMqUriVariable], RabbitMqUriVariable);
        if (!Uri.TryCreate(rabbitText, UriKind.Absolute, out var rabbitUri)
            || rabbitUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                $"{RabbitMqUriVariable} must be an absolute amqp or amqps URI.");
        }

        var brokerAddresses = Dns.GetHostAddresses(rabbitUri.Host);
        if (rabbitUri.Scheme == "amqp"
            && (brokerAddresses.Length == 0
                || brokerAddresses.Any(address => !IPAddress.IsLoopback(address))))
        {
            throw new InvalidDataException(
                "Cleartext RabbitMQ is permitted only on one loopback address in this gate.");
        }

        var evidencePath = RequiredGateAbsoluteFile(
            values[EvidencePathVariable],
            EvidencePathVariable,
            ".json");
        if (!string.Equals(
                RequiredGateText(values[GateEnabledVariable], GateEnabledVariable),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{GateEnabledVariable} must be exactly '1'.");
        }

        var scopeId = RequiredGateText(values[GateScopeIdVariable], GateScopeIdVariable);
        if (scopeId.Length != 32
            || scopeId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{GateScopeIdVariable} must be 32 lowercase hexadecimal characters.");
        }

        var serviceScope = RequiredGateText(
            values[RunnerServiceScopeVariable],
            RunnerServiceScopeVariable);
        if (!string.Equals(serviceScope, scopeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{RunnerServiceScopeVariable} must equal {GateScopeIdVariable}.");
        }

        var service = ReadRunnerServiceContract(
            RequiredGateText(
                values[AgentServiceCleanupManifestPathVariable],
                AgentServiceCleanupManifestPathVariable),
            serviceScope,
            agentBundleRoot);

        return new StagedAgentPrerequisites(
            runnerBundleRoot,
            agentBundleRoot,
            postgres,
            rabbitUri,
            evidencePath,
            scopeId,
            service);
    }

    [SupportedOSPlatform("windows")]
    private static RunnerAgentServiceContract ReadRunnerServiceContract(
        string manifestValue,
        string expectedScope,
        string releaseAgentBundleRoot)
    {
        if (!Path.IsPathFullyQualified(manifestValue))
        {
            throw new InvalidDataException(
                $"{AgentServiceCleanupManifestPathVariable} must be a canonical absolute file path.");
        }

        var manifestPath = Path.GetFullPath(manifestValue);
        if (!string.Equals(manifestPath, manifestValue, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"{AgentServiceCleanupManifestPathVariable} must identify an existing canonical file.");
        }

        EnsureNoReparseInPath(manifestPath);
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        var root = document.RootElement;
        RunnerRequireExactJsonProperties(
            root,
            "Runner Agent service cleanup manifest",
            "schema",
            "schemaVersion",
            "kind",
            "scope",
            "entries");
        RequireJsonString(root, "schema", "openlineops-agent-service-cleanup");
        RequireJsonInt(root, "schemaVersion", 1);
        RequireJsonString(root, "kind", "runner");
        RequireJsonString(root, "scope", expectedScope);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1)
        {
            throw new InvalidDataException(
                "Runner Agent service cleanup manifest must contain exactly one entry.");
        }

        var entry = entries[0];
        RunnerRequireExactJsonProperties(
            entry,
            "Runner Agent service cleanup entry",
            "role",
            "serviceSuffix",
            "serviceName",
            "serviceAccountName",
            "serviceAccountSid",
            "serviceSid",
            "serviceSidType",
            "executablePath",
            "executableSha256",
            "ownedRoot");
        RequireJsonString(entry, "role", "runner");
        RequireJsonString(entry, "serviceSuffix", expectedScope);
        var expectedServiceName = $"OpenLineOpsAgentE2E-{expectedScope}";
        RequireJsonString(entry, "serviceName", expectedServiceName);
        RequireJsonString(entry, "serviceAccountName", @"NT AUTHORITY\LocalService");
        RequireJsonString(entry, "serviceAccountSid", "S-1-5-19");
        RequireJsonString(entry, "serviceSidType", "Restricted");
        var serviceSid = RequiredJsonText(entry, "serviceSid");
        var expectedServiceSid = WindowsStationServiceIdentityReader
            .ServiceSidFromNameRequired(expectedServiceName);
        if (!string.Equals(serviceSid, expectedServiceSid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Runner Agent cleanup contract service SID does not match its exact service name.");
        }

        var windowsTemp = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp"));
        var expectedOwnedRoot = Path.Combine(
            windowsTemp,
            $"olo-runner-staged-agent-{expectedScope}");
        var ownedRoot = Path.GetFullPath(RequiredJsonText(entry, "ownedRoot"));
        if (!string.Equals(ownedRoot, expectedOwnedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Runner Agent cleanup contract owned root is outside its exact Windows Temp scope.");
        }

        var executablePath = Path.GetFullPath(RequiredJsonText(entry, "executablePath"));
        var expectedExecutablePath = Path.Combine(
            ownedRoot,
            "agent-bundle",
            "OpenLineOps.Agent.exe");
        if (!string.Equals(
                executablePath,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Runner Agent cleanup contract executable path differs from its exact owned bundle path.");
        }

        var executableSha256 = RequiredSha256(
            RequiredJsonText(entry, "executableSha256"),
            "Runner Agent cleanup contract executable SHA-256");
        var releaseExecutable = RequiredDirectFile(
            releaseAgentBundleRoot,
            "OpenLineOps.Agent.exe");
        if (!string.Equals(
                executableSha256,
                FileSha256(releaseExecutable),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Runner Agent cleanup contract executable hash differs from the staged release Agent.");
        }

        return new RunnerAgentServiceContract(
            manifestPath,
            expectedScope,
            expectedServiceName,
            @"NT AUTHORITY\LocalService",
            "S-1-5-19",
            serviceSid,
            executablePath,
            executableSha256,
            ownedRoot);
    }

    private static void RunnerRequireExactJsonProperties(
        JsonElement element,
        string description,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be an object.");
        }

        var expected = names.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name) || !expected.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"{description} contains a duplicate or unexpected property '{property.Name}'.");
            }
        }

        if (!observed.SetEquals(expected))
        {
            throw new InvalidDataException($"{description} is missing a required property.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectRunnerServiceRoot(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Runner Agent service root '{directory.FullName}' does not exist.");
        }

        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "Runner gate identity has no Windows SID.");
        var security = new DirectorySecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     currentSid
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(directory, security);
    }

    private static void CopyFrozenBundle(string sourceRoot, string destinationRoot)
    {
        var source = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(destinationRoot);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidDataException(
                "Runner Agent service bundle destination must not pre-exist.");
        }

        EnsureNoReparseTree(source);
        Directory.CreateDirectory(destination);
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceDirectory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var destinationFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
            File.SetAttributes(
                destinationFile,
                File.GetAttributes(destinationFile) | FileAttributes.ReadOnly);
        }

        EnsureNoReparseTree(destination);
    }

    private static BundleAttestation AttestBundle(
        string bundleRoot,
        string expectedKind,
        string expectedRole,
        string executableFileName)
    {
        EnsureNoReparseInPath(bundleRoot);
        var manifestPath = RequiredDirectFile(bundleRoot, "bundle-manifest.json");
        var checksumsPath = RequiredDirectFile(bundleRoot, "bundle-checksums.sha256");
        var executablePath = RequiredDirectFile(bundleRoot, executableFileName);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        RequireJsonInt(root, "schemaVersion", 1);
        RequireJsonString(root, "product", "OpenLineOps");
        RequireJsonString(root, "artifactKind", expectedKind);
        RequireJsonString(root, "runtimeIdentifier", "win-x64");
        if (!root.GetProperty("selfContained").GetBoolean())
        {
            throw new InvalidDataException("The staged bundle must be self-contained.");
        }

        var entryPoint = root.GetProperty("entryPoints")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("role").GetString(),
                expectedRole,
                StringComparison.Ordinal));
        RequireJsonString(entryPoint, "relativePath", executableFileName);
        var executableEntry = root.GetProperty("files")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("relativePath").GetString(),
                executableFileName,
                StringComparison.Ordinal));
        var expectedSize = executableEntry.GetProperty("sizeBytes").GetInt64();
        var expectedSha256 = RequiredSha256(
            executableEntry.GetProperty("sha256").GetString(),
            $"{expectedKind} executable manifest SHA-256");
        var actualSize = new FileInfo(executablePath).Length;
        var actualSha256 = FileSha256(executablePath);
        if (actualSize != expectedSize
            || !string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The staged {expectedKind} executable is not bound to bundle-manifest.json.");
        }

        var expectedChecksumLine = $"{expectedSha256}  {executableFileName}";
        if (!File.ReadAllLines(checksumsPath).Contains(expectedChecksumLine, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The staged {expectedKind} executable is not bound to bundle-checksums.sha256.");
        }

        return new BundleAttestation(
            expectedKind,
            executableFileName,
            executablePath,
            actualSha256,
            FileSha256(manifestPath),
            FileSha256(checksumsPath),
            ManifestBound: true);
    }

    private static PublishedPackageAttestation AttestPublishedPackage(
        string root,
        PublishedRunnerProject published)
    {
        var packagePath = Assert.Single(Directory.GetFiles(
            Path.Combine(root, "station-packages", "distribution"),
            "*.olopkg",
            SearchOption.TopDirectoryOnly));
        var contentSha256 = RequiredSha256(
            Path.GetFileNameWithoutExtension(packagePath),
            "Published package file name");
        var catalogPath = Assert.Single(Directory.GetFiles(
            Path.Combine(root, "station-packages", "deployment-catalog"),
            "*.json",
            SearchOption.AllDirectories));
        EnsureNoReparseInPath(packagePath);
        EnsureNoReparseInPath(catalogPath);

        using var archive = ZipFile.OpenRead(packagePath);
        RejectUnsafeArchive(archive);
        using var manifest = ReadArchiveJson(archive, "package.manifest.json");
        using var signature = ReadArchiveJson(archive, "package.signature.json");
        RequireJsonString(manifest.RootElement, "format", "openlineops.station-package");
        RequireJsonString(manifest.RootElement, "projectId", published.ProjectId);
        RequireJsonString(manifest.RootElement, "applicationId", published.ApplicationId);
        RequireJsonString(manifest.RootElement, "projectSnapshotId", published.SnapshotId);
        RequireJsonString(manifest.RootElement, "stationSystemId", StationSystemId);
        RequireJsonString(manifest.RootElement, "contentSha256", contentSha256);
        RequireJsonString(signature.RootElement, "algorithm", "RSA-PSS-SHA256");
        RequireJsonString(signature.RootElement, "keyId", SigningKeyId);
        _ = Convert.FromBase64String(
            RequiredJsonText(signature.RootElement, "signature"));

        using var catalog = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        RequireJsonString(catalog.RootElement, "schema", "openlineops.station-package-deployment");
        RequireJsonString(catalog.RootElement, "projectId", published.ProjectId);
        RequireJsonString(catalog.RootElement, "applicationId", published.ApplicationId);
        RequireJsonString(catalog.RootElement, "projectSnapshotId", published.SnapshotId);
        RequireJsonString(catalog.RootElement, "stationSystemId", StationSystemId);
        RequireJsonString(catalog.RootElement, "packageContentSha256", contentSha256);

        return new PublishedPackageAttestation(
            Path.GetFileName(packagePath),
            contentSha256,
            FileSha256(packagePath),
            Path.GetFileName(catalogPath),
            FileSha256(catalogPath),
            SigningKeyId,
            "RSA-PSS-SHA256",
            ManifestBound: true,
            DeploymentBound: true);
    }

    [SupportedOSPlatform("windows")]
    private static string ExportPublishedPackagePublicKey(string root)
    {
        var privateKeyPath = RequiredDirectFile(
            Path.Combine(root, "station-packages", "keys"),
            "release-private.pem");
        var publicKeyPath = Path.Combine(
            Path.GetDirectoryName(privateKeyPath)!,
            "release-public.pem");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        File.WriteAllText(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
        return publicKeyPath;
    }

    [SupportedOSPlatform("windows")]
    private static SafetyExecutableAttestation AttestIndependentSafetyExecutable()
    {
        var path = Path.Combine(Environment.SystemDirectory, "where.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The gate requires the independent Windows where.exe no-op safety placeholder.",
                path);
        }

        EnsureNoReparseInPath(path);
        return new SafetyExecutableAttestation(
            Path.GetFileName(path),
            path,
            FileSha256(path),
            IndependentFromStationRuntime: true);
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> CreateAgentEnvironment(
        string root,
        string dataRoot,
        string packageCacheRoot,
        string runtimeRoot,
        string artifactRoot,
        string distributionRoot,
        string packageContentSha256,
        string publicKeyPath,
        Uri rabbitMqUri,
        string agentId,
        string stationId,
        string suffix,
        int closedArtifactPort,
        string safetyExecutablePath)
    {
        var environment = CreateWindowsServiceEnvironment(Path.Combine(root, "agent-temp"));
        Set("AgentId", agentId);
        Set("StationId", stationId);
        Set("StationSystemId", StationSystemId);
        Set("HeartbeatInterval", "00:00:10");
        Set("DataDirectory", dataRoot);
        Set("BrokerUri", rabbitMqUri.AbsoluteUri);
        Set("RequireBrokerTls", (rabbitMqUri.Scheme == "amqps").ToString());
        Set("PrefetchCount", "1");
        Set("MaximumConcurrentJobs", "1");
        Set("PackageDistributionDirectory", distributionRoot);
        Set("PackageCacheDirectory", packageCacheRoot);
        Set("MaterialArrivalPackageContentSha256", packageContentSha256);
        Set($"TrustedPackagePublicKeyFiles__{SigningKeyId}", publicKeyPath);
        Set("RuntimeExecutablePath", "OpenLineOps.StationRuntime.exe");
        Set("PluginHostExecutablePath", "OpenLineOps.PluginHost.exe");
        Set("PythonScript__WorkerExecutablePath", "OpenLineOps.ScriptWorker.exe");
        Set("PythonScript__HostPythonRuntimeDllPath", RequiredHostPythonRuntimeDllPath());
        Set("PythonScript__Sandbox__RequireLeastPrivilegeExecution", "true");
        Set("PythonScript__Sandbox__IsolationMode", "LeastPrivilegeIdentity");
        Set("PythonScript__Sandbox__LeastPrivilegeIdentity", "PerExecutionAppContainer");
        Set(
            "PythonScript__Sandbox__LeastPrivilegeLauncherExecutable",
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Set("PythonScript__Sandbox__LeastPrivilegeNoInteractivePrompt", "true");
        Set("PythonScript__Sandbox__LeastPrivilegeArgumentsTemplate", string.Empty);
        Set("RuntimeWorkingDirectory", runtimeRoot);
        Set("ArtifactDirectory", artifactRoot);
        Set("CoordinatorBaseUri", $"http://127.0.0.1:{closedArtifactPort}/");
        Set("ArtifactUploadBearerToken", Base64Url(RandomNumberGenerator.GetBytes(32)));
        Set("ArtifactUploadTimeout", "00:00:05");
        Set("RuntimeTimeout", "00:00:30");
        Set("MaximumRuntimeOutputBytes", (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        Set("ExternalProgramAppContainerProfileNamespace", $"OpenLineOps.RunnerAgent.{suffix}");
        Set("SafetyExecutablePath", safetyExecutablePath);
        Set("SafetyWorkingDirectory", Path.Combine(root, "safety-work"));
        Set("SafetyTimeout", "00:00:05");
        environment["Logging__LogLevel__Default"] = "Warning";
        environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Warning";
        return environment;

        void Set(string key, string value) =>
            environment[$"OpenLineOps__Agent__{key}"] = value;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<GateProcessCapture> RunStagedRunnerAsync(
        string executablePath,
        string expectedExecutableSha256,
        string projectRoot,
        PublishedRunnerProject published,
        string postgresConnectionString,
        Uri rabbitMqUri,
        string coordinatorId,
        string agentId,
        string stationId,
        Guid productionRunId,
        Guid productionUnitId,
        string suffix)
    {
        var environment = CreateIsolatedProcessEnvironment(
            Path.Combine(projectRoot, ".runner-temp"));
        environment["OpenLineOps__Runtime__Persistence__Provider"] = "InMemory";
        environment["OpenLineOps__Runtime__Coordination__Provider"] = "PostgreSql";
        environment["OpenLineOps__Runtime__Coordination__ConnectionString"] =
            postgresConnectionString;
        environment["OpenLineOps__Runtime__AgentTransport__Provider"] = "RabbitMq";
        environment["OpenLineOps__Runtime__AgentTransport__BrokerUri"] = rabbitMqUri.AbsoluteUri;
        environment["OpenLineOps__Runtime__AgentTransport__RequireTls"] =
            (rabbitMqUri.Scheme == "amqps").ToString();
        environment["OpenLineOps__Runtime__AgentTransport__CoordinatorId"] = coordinatorId;
        environment["OpenLineOps__Runtime__AgentTransport__DeploymentCatalogDirectory"] =
            Path.Combine(Path.GetDirectoryName(projectRoot)!, "station-packages", "deployment-catalog");
        var deploymentPrefix = "OpenLineOps__Runtime__AgentTransport__Deployments__0";
        environment[$"{deploymentPrefix}__ProjectId"] = published.ProjectId;
        environment[$"{deploymentPrefix}__ApplicationId"] = published.ApplicationId;
        environment[$"{deploymentPrefix}__StationSystemId"] = StationSystemId;
        environment[$"{deploymentPrefix}__AgentId"] = agentId;
        environment[$"{deploymentPrefix}__StationId"] = stationId;
        environment["OpenLineOps__Runtime__StationExecution__Provider"] = "Agent";
        environment["OpenLineOps__Devices__Persistence__Provider"] = "InMemory";
        environment["OpenLineOps__Plugins__EventLog__Provider"] = "Sqlite";
        environment["OpenLineOps__Plugins__EventLog__DatabasePath"] =
            Path.Combine(projectRoot, "runner-plugin-events.sqlite");
        environment["Logging__LogLevel__Default"] = "Warning";
        environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Warning";

        var arguments = new[]
        {
            "run",
            published.ProjectFilePath,
            "--snapshot",
            "active",
            "--production-unit-id",
            productionUnitId.ToString("D", CultureInfo.InvariantCulture),
            "--identity",
            $"UNIT-{suffix}",
            "--actor",
            "runner-staged-agent-gate",
            "--run-id",
            productionRunId.ToString("D", CultureInfo.InvariantCulture)
        };
        using var runner = GateProcess.Start(
            executablePath,
            projectRoot,
            environment,
            arguments,
            expectedExecutableSha256);
        return await runner.WaitForExitAsync(TimeSpan.FromSeconds(90));
    }

    private static RunnerTerminalEvidence VerifyRunnerTerminal(
        JsonElement root,
        PublishedRunnerProject published,
        Guid productionRunId)
    {
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(RunnerExitCodes.Success, root.GetProperty("exitCode").GetInt32());
        var project = root.GetProperty("project");
        Assert.Equal(published.ProjectId, project.GetProperty("projectId").GetString());
        Assert.Equal(published.ApplicationId, project.GetProperty("applicationId").GetString());
        Assert.Equal(published.SnapshotId, project.GetProperty("snapshotId").GetString());
        Assert.Equal(
            published.ReleaseContentSha256,
            project.GetProperty("releaseContentSha256").GetString());
        var run = root.GetProperty("productionRun");
        Assert.Equal(productionRunId, run.GetProperty("productionRunId").GetGuid());
        Assert.Equal("Completed", run.GetProperty("executionStatus").GetString());
        Assert.Equal("NotApplicable", run.GetProperty("resultJudgement").GetString());
        Assert.Equal(1, run.GetProperty("operationCount").GetInt32());
        Assert.Equal(1, run.GetProperty("completedOperationCount").GetInt32());
        Assert.Equal(0, run.GetProperty("incidentCount").GetInt32());
        Assert.Equal(1, run.GetProperty("commandCount").GetInt32());
        return new RunnerTerminalEvidence(
            "Completed",
            "NotApplicable",
            run.GetProperty("operationCount").GetInt32(),
            run.GetProperty("completedOperationCount").GetInt32(),
            run.GetProperty("completedStepCount").GetInt32(),
            run.GetProperty("commandCount").GetInt32(),
            run.GetProperty("incidentCount").GetInt32());
    }

    private static async Task<PostgreSqlGateEvidence> ReadPostgreSqlEvidenceAsync(
        string connectionString,
        Guid productionRunId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var runCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_production_runs WHERE run_id = @run_id;",
            productionRunId);
        var terminalEvidenceCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_production_terminal_evidence WHERE run_id = @run_id;",
            productionRunId);
        var stationJobCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(DISTINCT job_id) FROM olo_station_job_outbox;",
            productionRunId: null);
        var stationResultCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_station_job_result_inbox;",
            productionRunId: null);
        var unpublishedJobCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_station_job_outbox "
            + "WHERE published_at_utc IS NULL OR quarantined_at_utc IS NOT NULL;",
            productionRunId: null);
        var terminalOutboxCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_production_terminal_outbox;",
            productionRunId: null);
        var createdOutboxCount = await PostgreSqlCountAsync(
            connection,
            "SELECT COUNT(*) FROM olo_production_created_outbox;",
            productionRunId: null);
        var status = await PostgreSqlTextAsync(
            connection,
            "SELECT execution_status FROM olo_production_runs WHERE run_id = @run_id;",
            productionRunId);
        var resultJson = await PostgreSqlTextAsync(
            connection,
            "SELECT payload_json::text FROM olo_station_job_result_inbox LIMIT 1;",
            productionRunId: null);
        var stationJobId = await PostgreSqlTextAsync(
            connection,
            "SELECT job_id::text FROM olo_station_job_outbox LIMIT 1;",
            productionRunId: null);
        var stationResultMessageId = await PostgreSqlTextAsync(
            connection,
            "SELECT message_id::text FROM olo_station_job_result_inbox LIMIT 1;",
            productionRunId: null);
        using var result = JsonDocument.Parse(resultJson);
        var resultArtifactCount = result.RootElement.GetProperty("artifacts").GetArrayLength();

        Assert.Equal(1, runCount);
        Assert.Equal(1, terminalEvidenceCount);
        Assert.Equal(1, stationJobCount);
        Assert.Equal(1, stationResultCount);
        Assert.Equal(0, unpublishedJobCount);
        Assert.Equal(0, terminalOutboxCount);
        Assert.Equal(0, createdOutboxCount);
        Assert.Equal("Completed", status);
        Assert.Equal(0, resultArtifactCount);
        _ = Guid.ParseExact(stationJobId, "D");
        _ = Guid.ParseExact(stationResultMessageId, "D");
        var snapshotSha256 = Utf8Sha256(string.Join(
            '\n',
            $"productionRunId={productionRunId:D}",
            $"productionRunCount={runCount}",
            $"terminalEvidenceCount={terminalEvidenceCount}",
            $"stationJobId={stationJobId}",
            $"stationJobCount={stationJobCount}",
            $"stationResultMessageId={stationResultMessageId}",
            $"stationResultCount={stationResultCount}",
            $"executionStatus={status}",
            $"resultArtifactCount={resultArtifactCount}"));
        return new PostgreSqlGateEvidence(
            runCount,
            terminalEvidenceCount,
            stationJobCount,
            stationResultCount,
            unpublishedJobCount,
            terminalOutboxCount,
            createdOutboxCount,
            status,
            resultArtifactCount,
            stationJobId,
            stationResultMessageId,
            snapshotSha256);
    }

    private static async Task<long> PostgreSqlCountAsync(
        NpgsqlConnection connection,
        string sql,
        Guid? productionRunId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (productionRunId is not null)
        {
            command.Parameters.AddWithValue("run_id", productionRunId.Value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> PostgreSqlTextAsync(
        NpgsqlConnection connection,
        string sql,
        Guid? productionRunId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (productionRunId is not null)
        {
            command.Parameters.AddWithValue("run_id", productionRunId.Value);
        }

        return Convert.ToString(
                   await command.ExecuteScalarAsync(),
                   CultureInfo.InvariantCulture)
               ?? throw new InvalidDataException("Expected PostgreSQL text evidence is absent.");
    }

    private static async Task<AgentSqliteGateEvidence> ReadAgentSqliteEvidenceAsync(
        string databasePath,
        Guid productionRunId)
    {
        Assert.True(File.Exists(databasePath));
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var jobCount = await SqliteCountAsync(connection, "SELECT COUNT(*) FROM station_jobs;");
        var inboxCount = await SqliteCountAsync(connection, "SELECT COUNT(*) FROM station_job_inbox;");
        var completionOutboxCount = await SqliteCountAsync(
            connection,
            "SELECT COUNT(*) FROM station_job_outbox WHERE kind = 'StationJobCompleted';");
        var acknowledgedCompletionCount = await SqliteCountAsync(
            connection,
            "SELECT COUNT(*) FROM station_job_outbox "
            + "WHERE kind = 'StationJobCompleted' AND acknowledged_at_utc IS NOT NULL;");
        var pendingOutboxCount = await SqliteCountAsync(
            connection,
            "SELECT COUNT(*) FROM station_job_outbox WHERE acknowledged_at_utc IS NULL;");
        var safetyTableCount = await SqliteCountAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_master "
            + "WHERE type = 'table' AND name = 'station_safety_inbox';");
        var safetyInboxCount = safetyTableCount == 0
            ? 0
            : await SqliteCountAsync(connection, "SELECT COUNT(*) FROM station_safety_inbox;");

        await using var jobCommand = connection.CreateCommand();
        jobCommand.CommandText =
            "SELECT status, document_json, revision FROM station_jobs LIMIT 1;";
        await using var reader = await jobCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var status = reader.GetString(0);
        var documentJson = reader.GetString(1);
        var revision = reader.GetInt64(2);
        await reader.DisposeAsync();
        using var document = JsonDocument.Parse(documentJson);
        Assert.Equal(
            productionRunId,
            document.RootElement.GetProperty("productionRunId").GetGuid());
        var commandCount = document.RootElement.GetProperty("commandCount").GetInt32();

        Assert.Equal(1, jobCount);
        Assert.Equal(1, inboxCount);
        Assert.Equal(1, completionOutboxCount);
        Assert.Equal(1, acknowledgedCompletionCount);
        Assert.Equal(0, pendingOutboxCount);
        Assert.Equal(0, safetyInboxCount);
        Assert.Equal("Completed", status);
        Assert.Equal(1, commandCount);
        Assert.True(revision > 0);
        return new AgentSqliteGateEvidence(
            jobCount,
            inboxCount,
            completionOutboxCount,
            acknowledgedCompletionCount,
            pendingOutboxCount,
            safetyInboxCount,
            status,
            revision,
            commandCount,
            FileSha256(databasePath));
    }

    private static async Task<long> SqliteCountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<TraceGateEvidence> ReadTraceEvidenceAsync(
        string projectRoot,
        Guid productionRunId)
    {
        var path = Path.Combine(
            ProjectExecutionDataDirectory.ForProjectDirectory(projectRoot),
            "openlineops-traceability.sqlite");
        Assert.True(File.Exists(path));
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_status, judgement, disposition, document_json
            FROM trace_records
            WHERE production_run_id = $production_run_id;
            """;
        command.Parameters.AddWithValue("$production_run_id", productionRunId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var executionStatus = reader.GetString(0);
        var judgement = reader.GetString(1);
        var disposition = reader.GetString(2);
        var documentJson = reader.GetString(3);
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        using var document = JsonDocument.Parse(documentJson);
        var operations = document.RootElement.GetProperty("operations");
        var operation = Assert.Single(operations.EnumerateArray());
        var commandCount = operation.GetProperty("commands").GetArrayLength();
        var artifactCount = operation.GetProperty("artifacts").GetArrayLength();
        Assert.Equal("Completed", executionStatus);
        Assert.Equal("NotApplicable", judgement);
        Assert.Equal("Completed", disposition);
        Assert.Equal(1, commandCount);
        Assert.Equal(0, artifactCount);
        return new TraceGateEvidence(
            1,
            operations.GetArrayLength(),
            commandCount,
            artifactCount,
            executionStatus,
            judgement,
            disposition,
            FileSha256(path));
    }

    private static async Task WriteGateEvidenceAsync(
        string evidencePath,
        GateObservation observation,
        bool postgresSchemaDropped,
        bool rabbitQueuesDeleted,
        bool runnerTreeTerminated,
        bool agentServiceDeleted,
        bool temporaryRootDeleted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        EnsureNoReparseInPath(Path.GetDirectoryName(evidencePath)!);
        var evidence = new
        {
            schema = "openlineops.runner-staged-agent-gate-evidence",
            schemaVersion = 1,
            outcome = "Passed",
            testName = ExactTestName,
            verifiedAtUtc = DateTimeOffset.UtcNow,
            project = new
            {
                observation.Published.ProjectId,
                observation.Published.ApplicationId,
                observation.Published.SnapshotId,
                observation.Published.ReleaseContentSha256,
                observation.ProductionRunId,
                observation.ProductionUnitId
            },
            execution = new
            {
                stationExecutionProvider = "Agent",
                coordinatorProcess = "OpenLineOps.Runner.exe",
                runner = new
                {
                    processId = observation.RunnerProcessId,
                    executableFileName = observation.RunnerBundle.ExecutableFileName,
                    executableSha256 = observation.RunnerBundle.ExecutableSha256,
                    runningImageSha256 = observation.RunnerRunningImageSha256,
                    bundleManifestSha256 = observation.RunnerBundle.ManifestSha256,
                    bundleChecksumsSha256 = observation.RunnerBundle.ChecksumsSha256,
                    observation.RunnerBundle.ManifestBound,
                    mainModuleBound = observation.RunnerMainModuleBound,
                    jobObjectBound = observation.RunnerJobObjectBound,
                    processTreeTerminated = observation.RunnerProcessTreeTerminated,
                    exitCode = RunnerExitCodes.Success
                },
                agent = new
                {
                    processId = observation.AgentProcessId,
                    executableFileName = observation.AgentBundle.ExecutableFileName,
                    executableSha256 = observation.AgentBundle.ExecutableSha256,
                    runningImageSha256 = observation.AgentRunningImageSha256,
                    bundleManifestSha256 = observation.AgentBundle.ManifestSha256,
                    bundleChecksumsSha256 = observation.AgentBundle.ChecksumsSha256,
                    observation.AgentBundle.ManifestBound,
                    mainModuleBound = observation.AgentMainModuleBound,
                    serviceName = observation.AgentServiceName,
                    serviceLifecycleVerified = observation.AgentServiceLifecycleVerified,
                    serviceAccountName = observation.AgentServiceAccountName,
                    serviceAccountSid = observation.AgentServiceAccountSid,
                    serviceSidSha256 = observation.AgentServiceSidSha256,
                    hasRestrictions = observation.AgentHasRestrictions,
                    serviceLogonSidPresent = observation.AgentServiceLogonSidPresent,
                    serviceLogonSidEnabled = observation.AgentServiceLogonSidEnabled,
                    exactServiceSidPresent = observation.AgentExactServiceSidPresent,
                    exactServiceSidEnabled = observation.AgentExactServiceSidEnabled,
                    exactServiceSidRestricted = observation.AgentExactServiceSidRestricted,
                    nonAdministrative = observation.AgentNonAdministrative
                },
                terminal = new
                {
                    observation.RunnerTerminal.ExecutionStatus,
                    observation.RunnerTerminal.ResultJudgement,
                    observation.RunnerTerminal.OperationCount,
                    observation.RunnerTerminal.CompletedOperationCount,
                    observation.RunnerTerminal.CompletedStepCount,
                    observation.RunnerTerminal.CommandCount,
                    observation.RunnerTerminal.IncidentCount
                }
            },
            stationPackage = new
            {
                stationSystemId = StationSystemId,
                observation.Package.PackageFileName,
                packageContentSha256 = observation.Package.ContentSha256,
                observation.Package.PackageFileSha256,
                observation.Package.CatalogFileName,
                observation.Package.CatalogFileSha256,
                observation.Package.SigningKeyId,
                observation.Package.SignatureAlgorithm,
                observation.Package.ManifestBound,
                observation.Package.DeploymentBound
            },
            postgresql = new
            {
                isolatedSchema = true,
                observation.PostgreSql.ProductionRunCount,
                observation.PostgreSql.TerminalEvidenceCount,
                observation.PostgreSql.StationJobCount,
                observation.PostgreSql.StationResultCount,
                observation.PostgreSql.UnpublishedJobCount,
                observation.PostgreSql.TerminalOutboxCount,
                observation.PostgreSql.CreatedOutboxCount,
                observation.PostgreSql.ExecutionStatus,
                observation.PostgreSql.ResultArtifactCount,
                rawSnapshot = new
                {
                    observation.ProductionRunId,
                    observation.PostgreSql.ProductionRunCount,
                    observation.PostgreSql.TerminalEvidenceCount,
                    observation.PostgreSql.StationJobId,
                    observation.PostgreSql.StationJobCount,
                    observation.PostgreSql.StationResultMessageId,
                    observation.PostgreSql.StationResultCount,
                    observation.PostgreSql.ExecutionStatus,
                    observation.PostgreSql.ResultArtifactCount
                },
                rawSnapshotSha256 = observation.PostgreSql.SnapshotSha256
            },
            agentSqlite = new
            {
                observation.AgentSqlite.JobCount,
                observation.AgentSqlite.InboxCount,
                observation.AgentSqlite.CompletionOutboxCount,
                observation.AgentSqlite.AcknowledgedCompletionCount,
                observation.AgentSqlite.PendingOutboxCount,
                observation.AgentSqlite.SafetyInboxCount,
                observation.AgentSqlite.Status,
                terminalCheckpointRevision = observation.AgentSqlite.Revision,
                observation.AgentSqlite.CommandCount,
                databaseSha256 = observation.AgentSqlite.DatabaseSha256,
                onceOnly = true
            },
            rabbitMq = new
            {
                observation.RabbitMq.JobQueueMessageCount,
                observation.RabbitMq.ResultQueueMessageCount,
                observation.RabbitMq.SafetyQueueMessageCount,
                observation.RabbitMq.JobQueueConsumerCount,
                observation.RabbitMq.ResultQueueConsumerCount,
                observation.RabbitMq.QueueIdentitySha256,
                rawSnapshotSha256 = observation.RabbitMq.SnapshotSha256,
                drained = true
            },
            trace = new
            {
                observation.Trace.RecordCount,
                observation.Trace.OperationCount,
                observation.Trace.CommandCount,
                observation.Trace.ArtifactCount,
                observation.Trace.ExecutionStatus,
                observation.Trace.Judgement,
                observation.Trace.Disposition,
                databaseSha256 = observation.Trace.DatabaseSha256
            },
            artifactTransport = new
            {
                endpointClass = "closed-loopback-http",
                endpointReachable = false,
                artifactCount = 0,
                artifactUploadAttemptCount = observation.ArtifactEndpointContactCount
            },
            safetyChannel = new
            {
                configured = true,
                commandCount = observation.AgentSqlite.SafetyInboxCount,
                queueMessageCount = observation.RabbitMq.SafetyQueueMessageCount,
                actuatorInvoked = false,
                observation.SafetyExecutable.ExecutableFileName,
                observation.SafetyExecutable.ExecutableSha256,
                observation.SafetyExecutable.IndependentFromStationRuntime
            },
            cleanup = new
            {
                postgresSchemaDropped,
                rabbitQueuesDeleted,
                runnerTreeTerminated,
                agentServiceDeleted,
                temporaryRootDeleted,
                reparsePointsTraversed = false
            }
        };
        await using var stream = new FileStream(
            evidencePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, evidence, GateEvidenceJsonOptions);
        await stream.WriteAsync("\n"u8.ToArray());
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> CreateIsolatedProcessEnvironment(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "ALLUSERSPROFILE", "APPDATA", "COMSPEC", "CommonProgramFiles",
                     "CommonProgramFiles(x86)", "CommonProgramW6432", "LOCALAPPDATA",
                     "NUMBER_OF_PROCESSORS", "OS", "PATH", "PATHEXT",
                     "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "ProgramData",
                     "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "SystemDrive",
                     "SystemRoot", "USERDOMAIN", "USERNAME", "USERPROFILE", "WINDIR"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        environment["TEMP"] = tempRoot;
        environment["TMP"] = tempRoot;
        environment["DOTNET_ENVIRONMENT"] = "Production";
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["ASPNETCORE_PREVENTHOSTINGSTARTUP"] = "true";
        environment["DOTNET_EnableDiagnostics"] = "0";
        return environment;
    }

    private static Dictionary<string, string> CreateWindowsServiceEnvironment(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "ALLUSERSPROFILE", "COMSPEC", "CommonProgramFiles",
                     "CommonProgramFiles(x86)", "CommonProgramW6432", "DOTNET_ROOT",
                     "DOTNET_ROOT_X64", "NUMBER_OF_PROCESSORS", "OS", "PATH", "PATHEXT",
                     "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL",
                     "PROCESSOR_REVISION", "ProgramData", "ProgramFiles",
                     "ProgramFiles(x86)", "ProgramW6432", "PYTHONNET_PYDLL", "SystemDrive",
                     "SystemRoot", "WINDIR"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        environment["TEMP"] = tempRoot;
        environment["TMP"] = tempRoot;
        return environment;
    }

    private static int FindClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> CanConnectToLoopbackPortAsync(int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string RequiredHostPythonRuntimeDllPath()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments =
                "-c \"import os,sys; print(os.path.join(sys.base_prefix, "
                + "f'python{sys.version_info.major}{sys.version_info.minor}.dll'))\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Python runtime discovery did not start.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Python runtime discovery exceeded ten seconds.");
        }

        var path = process.StandardOutput.ReadToEnd().Trim();
        if (process.ExitCode != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "The staged Agent gate requires an installed host Python runtime DLL.");
        }

        return Path.GetFullPath(path);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteGateRoot(
        string root,
        string packageCacheRoot,
        string serviceScope,
        string stationServiceSid)
    {
        var dedicatedBase = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp"));
        var expectedRoot = Path.Combine(
            dedicatedBase,
            $"olo-runner-staged-agent-{serviceScope}");
        if (!string.Equals(
                Path.GetFullPath(root),
                expectedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The gate refuses recursive cleanup outside its exact Windows Temp service scope.");
        }

        DeleteProvisionedCacheNamespace(packageCacheRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        EnsureNoReparseTree(root);
        RemoveRunnerServiceSidAccess(root, stationServiceSid);

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }

    private static void DeleteProvisionedCacheNamespace(string packageCacheRoot)
    {
        if (!Directory.Exists(packageCacheRoot))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(packageCacheRoot).Any())
        {
            throw new InvalidOperationException(
                "Runner provisioned package cache is not empty after paired removal.");
        }

        var anchor = Directory.GetParent(packageCacheRoot)?.FullName
                     ?? throw new InvalidDataException(
                         "Runner provisioned package cache has no dedicated anchor.");
        EnsureNoReparseInPath(packageCacheRoot);
        if (Directory.EnumerateFileSystemEntries(anchor).Count() != 1)
        {
            throw new InvalidOperationException(
                "Runner package cache anchor is not dedicated to its content root.");
        }

        Directory.Delete(packageCacheRoot);
        Directory.Delete(anchor);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveProtectedPackageInstallations(
        string packageCacheRoot,
        string stationServiceName,
        string stationServiceSid)
    {
        if (!Directory.Exists(packageCacheRoot))
        {
            return;
        }

        var protector = new ImmutableContentProtector();
        var protectionPolicy = new ImmutableContentProtectionPolicy(
            WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
            stationServiceSid);
        IReadOnlyList<string> contentHashes = ImmutableContentCacheCleanupDiscovery
            .DiscoverPackageContentHashes(packageCacheRoot);
        foreach (var contentHash in contentHashes)
        {
            protector.RemoveProtectedPackageInstallationAsync(
                    packageCacheRoot,
                    contentHash,
                    stationServiceName,
                    protectionPolicy)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveRunnerServiceSidAccess(
        string root,
        string serviceSidValue)
    {
        var serviceSid = new SecurityIdentifier(serviceSidValue);
        var paths = Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length)
            .Append(root)
            .ToArray();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var security = FileSystemAclExtensions.GetAccessControl(directory);
                security.PurgeAccessRules(serviceSid);
                FileSystemAclExtensions.SetAccessControl(directory, security);
            }
            else if (File.Exists(path))
            {
                var file = new FileInfo(path);
                var security = FileSystemAclExtensions.GetAccessControl(file);
                security.PurgeAccessRules(serviceSid);
                FileSystemAclExtensions.SetAccessControl(file, security);
            }
        }

        foreach (var path in paths)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
                : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            var hasServiceSid = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference is SecurityIdentifier identity
                             && string.Equals(
                                 identity.Value,
                                 serviceSid.Value,
                                 StringComparison.Ordinal));
            if (hasServiceSid)
            {
                throw new InvalidOperationException(
                    $"Runner Agent service SID remained on '{path}' after ACL cleanup.");
            }
        }
    }

    private static void CaptureCleanupFailure(List<Exception> failures, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureCleanupFailureAsync(
        List<Exception> failures,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static string RequiredGateDirectory(string? value, string variableName)
    {
        var text = RequiredGateText(value, variableName);
        if (!Path.IsPathFullyQualified(text))
        {
            throw new InvalidDataException($"{variableName} must be an absolute path.");
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{variableName} directory does not exist.");
        }

        EnsureNoReparseInPath(path);
        return path;
    }

    private static string RequiredGateAbsoluteFile(
        string? value,
        string variableName,
        string extension)
    {
        var text = RequiredGateText(value, variableName);
        if (!Path.IsPathFullyQualified(text))
        {
            throw new InvalidDataException($"{variableName} must be an absolute file path.");
        }

        var path = Path.GetFullPath(text);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{variableName} must use the {extension} extension.");
        }

        if (File.Exists(path))
        {
            throw new InvalidDataException($"{variableName} refuses to overwrite stale evidence.");
        }

        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidDataException($"{variableName} has no parent directory.");
        if (Directory.Exists(parent))
        {
            EnsureNoReparseInPath(parent);
        }

        return path;
    }

    private static string RequiredGateText(string? value, string variableName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"{variableName} is required when any staged Runner-Agent gate variable is present.")
            : value;

    private static string RequiredDirectFile(string root, string fileName)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(fileName, canonicalRoot);
        if (!string.Equals(
                Path.GetDirectoryName(path),
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required staged file '{fileName}' is absent or escapes its bundle root.",
                path);
        }

        EnsureNoReparseInPath(path, canonicalRoot);
        return path;
    }

    private static void EnsureNoReparseTree(string root)
    {
        EnsureNoReparseInPath(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The gate refuses to traverse a reparse point during cleanup.");
            }
        }
    }

    private static void EnsureNoReparseInPath(string path, string? stopAt = null)
    {
        var stop = stopAt is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists
                && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The gate refuses symbolic links and junctions.");
            }

            if (stop is not null
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    stop,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    private static void RejectUnsafeArchive(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name)
                || name.Contains('\\', StringComparison.Ordinal)
                || name[0] == '/'
                || name.Split('/').Any(segment => segment is "" or "." or "..")
                || !names.Add(name)
                || (entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
            {
                throw new InvalidDataException("The signed Station package has an unsafe entry.");
            }
        }
    }

    private static JsonDocument ReadArchiveJson(ZipArchive archive, string entryName)
    {
        var entry = archive.Entries.Single(item =>
            string.Equals(item.FullName, entryName, StringComparison.Ordinal));
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static void RequireJsonString(
        JsonElement element,
        string propertyName,
        string expected)
    {
        if (!string.Equals(
                RequiredJsonText(element, propertyName),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"JSON property '{propertyName}' does not match its required value.");
        }
    }

    private static void RequireJsonInt(JsonElement element, string propertyName, int expected)
    {
        if (element.GetProperty(propertyName).GetInt32() != expected)
        {
            throw new InvalidDataException(
                $"JSON property '{propertyName}' does not match its required value.");
        }
    }

    private static string RequiredJsonText(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"JSON property '{propertyName}' is required.");

    private static string RequiredSha256(string? value, string name) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException($"{name} must be lowercase hexadecimal SHA-256.");

    private static string FileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Utf8Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class PostgreSqlGateScope : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _schema;

        private PostgreSqlGateScope(
            string adminConnectionString,
            string connectionString,
            string schema)
        {
            _adminConnectionString = adminConnectionString;
            ConnectionString = connectionString;
            _schema = schema;
        }

        public string ConnectionString { get; }

        public bool SchemaDropped { get; private set; }

        public static async Task<PostgreSqlGateScope> CreateAsync(
            string baseConnectionString,
            string schema)
        {
            if (schema.Length is < 1 or > 63
                || schema.Any(character =>
                    character is not (>= 'a' and <= 'z')
                    && character is not (>= '0' and <= '9')
                    && character != '_'))
            {
                throw new InvalidDataException("PostgreSQL gate schema name is invalid.");
            }

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = string.Empty,
                Pooling = false
            };
            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    $"CREATE SCHEMA \"{schema}\";",
                    connection);
                await command.ExecuteNonQueryAsync();
            }

            var scopedBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                Pooling = false
            };
            return new PostgreSqlGateScope(
                adminBuilder.ConnectionString,
                scopedBuilder.ConnectionString,
                schema);
        }

        public async ValueTask DisposeAsync()
        {
            if (SchemaDropped)
            {
                return;
            }

            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA \"{_schema}\" CASCADE;",
                connection);
            await command.ExecuteNonQueryAsync();
            SchemaDropped = true;
        }
    }

    private sealed class RabbitMqGateTopology : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly string _jobQueue;
        private readonly string _resultQueue;
        private readonly string[] _safetyQueues;

        private RabbitMqGateTopology(
            IConnection connection,
            IChannel channel,
            string jobQueue,
            string resultQueue,
            string[] safetyQueues)
        {
            _connection = connection;
            _channel = channel;
            _jobQueue = jobQueue;
            _resultQueue = resultQueue;
            _safetyQueues = safetyQueues;
        }

        public bool QueuesDeleted { get; private set; }

        public static async Task<RabbitMqGateTopology> CreateAsync(
            Uri brokerUri,
            string coordinatorId,
            string agentId,
            string stationId)
        {
            var factory = new ConnectionFactory
            {
                Uri = brokerUri,
                ClientProvidedName = $"openlineops-runner-agent-gate-{coordinatorId}",
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false
            };
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            var createdQueues = new List<string>();
            try
            {
                await channel.ExchangeDeclareAsync(
                    "openlineops.station.jobs",
                    ExchangeType.Direct,
                    durable: true,
                    autoDelete: false);
                await channel.ExchangeDeclareAsync(
                    "openlineops.station.events",
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false);
                var jobQueue = StationTransportRoute.JobQueue(agentId, stationId);
                await channel.QueueDeclareAsync(jobQueue, true, false, false);
                createdQueues.Add(jobQueue);
                await channel.QueueBindAsync(
                    jobQueue,
                    "openlineops.station.jobs",
                    StationTransportRoute.Job(agentId, stationId));
                await channel.QueueBindAsync(
                    jobQueue,
                    "openlineops.station.jobs",
                    StationTransportRoute.ResourceLeaseChanged(agentId, stationId));
                var resultQueue = $"openlineops.coordinator.{coordinatorId}.station-results";
                await channel.QueueDeclareAsync(resultQueue, true, false, false);
                createdQueues.Add(resultQueue);
                foreach (var kind in new[]
                         {
                             nameof(StationJobAccepted),
                             nameof(StationJobProgressed),
                             nameof(StationJobCompleted),
                             nameof(StationJobRecoveryRequired),
                             nameof(MaterialArrived),
                             nameof(AgentPresenceReported)
                         })
                {
                    await channel.QueueBindAsync(
                        resultQueue,
                        "openlineops.station.events",
                        StationTransportRoute.EventPattern(kind));
                }

                var safetyQueues = new[]
                {
                    StationTransportRoute.SafetyQueue(agentId, stationId, "emergency-stop"),
                    StationTransportRoute.SafetyQueue(agentId, stationId, "safe-stop"),
                    StationTransportRoute.SafetyQueue(agentId, stationId, "job-cancel")
                };
                var safetyQueueArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-max-priority"] = (byte)10
                };
                foreach (var queue in safetyQueues)
                {
                    await channel.QueueDeclareAsync(
                        queue,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        safetyQueueArguments);
                    createdQueues.Add(queue);
                }

                return new RabbitMqGateTopology(
                    connection,
                    channel,
                    jobQueue,
                    resultQueue,
                    safetyQueues);
            }
            catch
            {
                foreach (var queue in createdQueues.AsEnumerable().Reverse())
                {
                    try
                    {
                        await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
                    }
                    catch
                    {
                    }
                }
                await channel.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task WaitForAgentConsumerAsync(
            RunnerAgentWindowsService agent,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (agent.HasExited)
                {
                    throw new InvalidOperationException(
                        "The staged Agent exited before attaching its RabbitMQ job consumer.");
                }

                var state = await _channel.QueueDeclarePassiveAsync(_jobQueue);
                if (state.ConsumerCount == 1)
                {
                    return;
                }

                if (state.ConsumerCount > 1)
                {
                    throw new InvalidOperationException(
                        "The isolated staged Agent queue has more than one consumer.");
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                "The staged Agent did not attach its RabbitMQ job consumer within the bound.");
        }

        public async Task<RabbitMqGateEvidence> ReadDrainStateAsync()
        {
            var job = await _channel.QueueDeclarePassiveAsync(_jobQueue);
            var result = await _channel.QueueDeclarePassiveAsync(_resultQueue);
            uint safetyMessageCount = 0;
            foreach (var queue in _safetyQueues)
            {
                safetyMessageCount = checked(
                    safetyMessageCount
                    + (await _channel.QueueDeclarePassiveAsync(queue)).MessageCount);
            }
            Assert.Equal(0u, job.MessageCount);
            Assert.Equal(0u, result.MessageCount);
            Assert.Equal(0u, safetyMessageCount);
            var queueIdentitySha256 = Utf8Sha256(string.Join(
                '\n',
                _jobQueue,
                _resultQueue,
                string.Join('\n', _safetyQueues.OrderBy(
                    static value => value,
                    StringComparer.Ordinal))));
            var snapshotSha256 = Utf8Sha256(string.Join(
                '\n',
                $"jobQueueMessageCount={job.MessageCount}",
                $"jobQueueConsumerCount={job.ConsumerCount}",
                $"resultQueueMessageCount={result.MessageCount}",
                $"resultQueueConsumerCount={result.ConsumerCount}",
                $"safetyQueueMessageCount={safetyMessageCount}",
                $"queueIdentitySha256={queueIdentitySha256}"));
            return new RabbitMqGateEvidence(
                job.MessageCount,
                result.MessageCount,
                safetyMessageCount,
                job.ConsumerCount,
                result.ConsumerCount,
                queueIdentitySha256,
                snapshotSha256);
        }

        public async Task DeleteQueuesAsync()
        {
            if (QueuesDeleted)
            {
                return;
            }

            foreach (var queue in _safetyQueues.Append(_jobQueue).Append(_resultQueue))
            {
                await _channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }

            QueuesDeleted = true;
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class RunnerAgentWindowsService : IDisposable
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ScManagerCreateService = 0x0002;
        private const uint DeleteAccess = 0x00010000;
        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ServiceWin32OwnProcess = 0x00000010;
        private const uint ServiceDemandStart = 0x00000003;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceControlStop = 0x00000001;
        private const uint ScStatusProcessInfo = 0;
        private const uint ServiceConfigServiceSidInfo = 5;
        private const uint ServiceSidTypeRestricted = 3;
        private const uint ServiceStopped = 0x00000001;
        private const uint ServiceStartPending = 0x00000002;
        private const uint ServiceStopPending = 0x00000003;
        private const uint ServiceRunning = 0x00000004;
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint TokenQuery = 0x0008;
        private const uint GroupEnabled = 0x00000004;
        private const uint GroupUseForDenyOnly = 0x00000010;
        private const uint GroupLogonId = 0xC0000000;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const uint WaitFailed = uint.MaxValue;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceNotActive = 1062;
        private const int ErrorServiceMarkedForDelete = 1072;
        private const string ServiceRegistryPrefix =
            @"SYSTEM\CurrentControlSet\Services\";
        private const string EventLogSourceRegistryPrefix =
            @"SYSTEM\CurrentControlSet\Services\EventLog\Application\";

        private readonly RunnerAgentServiceContract _contract;
        private readonly string[] _environmentEntries;
        private readonly string _packageCacheRoot;
        private readonly SafeServiceHandle _manager;
        private SafeServiceHandle? _service;
        private readonly SafeProcessHandle _processHandle;
        private bool _stopped;
        private bool _cleanStopVerified;
        private bool _forcedCleanupUsed;
        private bool _disposed;

        private RunnerAgentWindowsService(
            RunnerAgentServiceContract contract,
            string[] environmentEntries,
            string packageCacheRoot,
            SafeServiceHandle manager,
            SafeServiceHandle service,
            SafeProcessHandle processHandle,
            int processId,
            string runningImageSha256,
            RunnerAgentTokenEvidence tokenEvidence)
        {
            _contract = contract;
            _environmentEntries = environmentEntries;
            _packageCacheRoot = packageCacheRoot;
            _manager = manager;
            _service = service;
            _processHandle = processHandle;
            ProcessId = processId;
            RunningImageSha256 = runningImageSha256;
            TokenEvidence = tokenEvidence;
        }

        public int ProcessId { get; }

        public bool HasExited => WaitForProcessExit(_processHandle, TimeSpan.Zero);

        public string RunningImageSha256 { get; }

        public bool MainModuleBound { get; } = true;

        public string ServiceName => _contract.ServiceName;

        public string ServiceAccountName => _contract.ServiceAccountName;

        public string ServiceAccountSid => _contract.ServiceAccountSid;

        public string ServiceSidSha256 => Utf8Sha256(_contract.ServiceSid);

        public RunnerAgentTokenEvidence TokenEvidence { get; }

        public bool ServiceDeletionVerified { get; private set; }

        public bool ServiceLifecycleVerified =>
            ServiceDeletionVerified
            && _cleanStopVerified
            && !_forcedCleanupUsed;

        public void VerifyProvisionedPackageCache() =>
            VerifyProvisionedPackageCache(
                _packageCacheRoot,
                _contract.ServiceSid);

        public static RunnerAgentWindowsService InstallAndStart(
            RunnerAgentServiceContract contract,
            string contentRoot,
            Dictionary<string, string> environment,
            string packageCacheRoot,
            IReadOnlyCollection<string> writableRoots)
        {
            ArgumentNullException.ThrowIfNull(contract);
            ArgumentNullException.ThrowIfNull(environment);
            ArgumentException.ThrowIfNullOrWhiteSpace(packageCacheRoot);
            ArgumentNullException.ThrowIfNull(writableRoots);
            var canonicalContentRoot = Path.GetFullPath(contentRoot);
            var canonicalPackageCacheRoot = Path.GetFullPath(packageCacheRoot);
            if (!string.Equals(
                    Path.GetDirectoryName(contract.ExecutablePath),
                    canonicalContentRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(contract.ExecutablePath)
                || !string.Equals(
                    FileSha256(contract.ExecutablePath),
                    contract.ExecutableSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runner Agent service executable is not the cleanup-contract-bound frozen image.");
            }

            var expectedPackageCacheRoot = Path.Combine(
                Path.GetDirectoryName(contract.OwnedRoot)
                ?? throw new InvalidDataException("Runner Agent owned root has no parent."),
                $"olo-runner-staged-agent-content-{contract.Scope}",
                "content");
            if (!string.Equals(
                    canonicalPackageCacheRoot,
                    expectedPackageCacheRoot,
                    StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(canonicalPackageCacheRoot)
                || File.Exists(canonicalPackageCacheRoot))
            {
                throw new InvalidDataException(
                    "Runner Agent package cache must be the absent content root of its dedicated administrative namespace.");
            }

            var serviceSid = new SecurityIdentifier(contract.ServiceSid);
            var aclRoots = new List<string>();
            SafeServiceHandle? manager = null;
            SafeServiceHandle? service = null;
            SafeProcessHandle? processHandle = null;
            var serviceCreated = false;
            var eventSourceCreated = false;
            var serviceEnvironmentProtected = false;
            string[]? environmentEntries = null;
            try
            {
                GrantDirectoryAccess(
                    contract.OwnedRoot,
                    serviceSid,
                    FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
                aclRoots.Add(contract.OwnedRoot);
                foreach (var writableRoot in writableRoots
                             .Select(Path.GetFullPath)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsRunnerOwnedPath(writableRoot, contract.OwnedRoot))
                    {
                        throw new InvalidDataException(
                            "Runner Agent writable root escaped its strict owned service root.");
                    }

                    Directory.CreateDirectory(writableRoot);
                    GrantDirectoryAccess(
                        writableRoot,
                        serviceSid,
                        FileSystemRights.Modify | FileSystemRights.Synchronize,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
                    aclRoots.Add(writableRoot);
                }

                var serviceEnvironment = environment.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                serviceEnvironment.Add("OpenLineOps__WindowsServiceName", contract.ServiceName);
                serviceEnvironment.Add("DOTNET_CONTENTROOT", canonicalContentRoot);
                environmentEntries = CreateServiceEnvironmentEntries(serviceEnvironment);

                manager = OpenSCManager(
                    machineName: null,
                    databaseName: null,
                    ScManagerConnect | ScManagerCreateService);
                if (manager.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not open SCM for the Runner staged Agent service.");
                }

                service = CreateService(
                    manager,
                    contract.ServiceName,
                    contract.ServiceName,
                    DeleteAccess
                    | ServiceQueryConfig
                    | ServiceChangeConfig
                    | ServiceQueryStatus
                    | ServiceStart
                    | ServiceStop,
                    ServiceWin32OwnProcess,
                    ServiceDemandStart,
                    ServiceErrorNormal,
                    QuoteServiceBinaryPath(contract.ExecutablePath),
                    loadOrderGroup: null,
                    IntPtr.Zero,
                    dependencies: null,
                    contract.ServiceAccountName,
                    password: null);
                if (service.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not install Runner Agent service '{contract.ServiceName}'.");
                }

                serviceCreated = true;
                var sidInfo = new ServiceSidInfo
                {
                    ServiceSidType = ServiceSidTypeRestricted
                };
                if (!ChangeServiceConfig2(
                        service,
                        ServiceConfigServiceSidInfo,
                        ref sidInfo))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not restrict Runner Agent service SID for '{contract.ServiceName}'.");
                }

                VerifyServiceConfiguration(service, contract);
                RegisterEventLogSource(contract.ServiceName, serviceSid);
                eventSourceCreated = true;
                ProtectAndWriteServiceEnvironment(contract.ServiceName, environmentEntries);
                serviceEnvironmentProtected = true;
                VerifyServiceEnvironment(contract.ServiceName, environmentEntries);
                new ImmutableContentProtector().ProvisionCacheNamespace(
                    canonicalPackageCacheRoot,
                    contract.ServiceName,
                    new ImmutableContentProtectionPolicy(
                        WindowsAppContainerIdentity.EnsureCapabilitySid(
                            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                        contract.ServiceSid));

                if (!StartService(service, 0, IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not start Runner Agent service '{contract.ServiceName}'.");
                }

                var running = WaitForServiceState(
                    service,
                    contract.ServiceName,
                    ServiceRunning,
                    TimeSpan.FromSeconds(45));
                if (running.ProcessId == 0)
                {
                    throw new InvalidOperationException(
                        "Runner Agent SCM service reached Running without a PID.");
                }

                processHandle = OpenProcess(
                    ProcessTerminate | ProcessQueryLimitedInformation | Synchronize,
                    inheritHandle: false,
                    running.ProcessId);
                if (processHandle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not open the Runner Agent SCM process.");
                }

                var confirmed = QueryServiceStatus(service, contract.ServiceName);
                if (confirmed.CurrentState != ServiceRunning
                    || confirmed.ProcessId != running.ProcessId)
                {
                    throw new InvalidOperationException(
                        "Runner Agent SCM identity changed while securing its process handle.");
                }

                var executablePath = ReadProcessExecutablePath(processHandle);
                if (!string.Equals(
                        executablePath,
                        contract.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Runner Agent SCM process image differs from its strict cleanup contract.");
                }

                var runningImageSha256 = FileSha256(executablePath);
                if (!string.Equals(
                        runningImageSha256,
                        contract.ExecutableSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Runner Agent SCM process image hash differs from its release attestation.");
                }

                var tokenEvidence = ReadRequiredTokenEvidence(
                    processHandle,
                    contract.ServiceAccountSid,
                    contract.ServiceSid);
                var result = new RunnerAgentWindowsService(
                    contract,
                    environmentEntries,
                    canonicalPackageCacheRoot,
                    manager,
                    service,
                    processHandle,
                    checked((int)running.ProcessId),
                    runningImageSha256,
                    tokenEvidence);
                manager = null;
                service = null;
                processHandle = null;
                return result;
            }
            catch (Exception exception)
            {
                var failures = new List<Exception> { exception };
                if (service is { IsInvalid: false, IsClosed: false })
                {
                    var cleanupFailureCount = failures.Count;
                    CaptureCleanupFailure(
                        failures,
                        () => StopServiceForCleanup(
                            service,
                            contract.ServiceName,
                            processHandle,
                            TimeSpan.FromSeconds(45)));
                    if (failures.Count == cleanupFailureCount)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => RemoveProtectedPackageInstallations(
                                canonicalPackageCacheRoot,
                                contract.ServiceName,
                                contract.ServiceSid));
                    }

                    if (failures.Count == cleanupFailureCount)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteProvisionedCacheNamespace(canonicalPackageCacheRoot));
                    }

                    if (failures.Count == cleanupFailureCount
                        && serviceEnvironmentProtected
                        && environmentEntries is not null)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteServiceEnvironment(
                                contract.ServiceName,
                                environmentEntries));
                    }
                    if (failures.Count == cleanupFailureCount && eventSourceCreated)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteEventLogSource(
                                contract.ServiceName,
                                serviceSid));
                    }

                    if (failures.Count == cleanupFailureCount && serviceCreated)
                    {
                        CaptureCleanupFailure(failures, () => DeleteServiceRequired(
                            service,
                            contract.ServiceName));
                    }
                }

                CaptureCleanupFailure(failures, () => processHandle?.Dispose());
                CaptureCleanupFailure(failures, () => service?.Dispose());
                if (manager is not null)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => WaitForServiceDeletion(
                            contract.ServiceName,
                            TimeSpan.FromSeconds(45)));
                }

                CaptureCleanupFailure(failures, () => manager?.Dispose());
                if (failures.Count == 1)
                {
                    foreach (var aclRoot in aclRoots.AsEnumerable().Reverse())
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => RemoveDirectoryAccess(aclRoot, serviceSid));
                    }
                }

                if (failures.Count == 1)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(exception)
                        .Throw();
                }

                throw new AggregateException(
                    "Runner Agent SCM installation failed and cleanup was incomplete.",
                    failures);
            }
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped)
            {
                return;
            }

            await Task.Run(() =>
            {
                StopServiceCleanly(
                    RequiredService(),
                    ServiceName,
                    _processHandle,
                    timeout);
                _stopped = true;
                _cleanStopVerified = true;
            });
        }

        public Task<ProcessOutput> GetCapturedOutputAsync()
        {
            var status = _service is { IsInvalid: false, IsClosed: false }
                ? QueryServiceStatus(_service, ServiceName)
                : default;
            return Task.FromResult(new ProcessOutput(
                string.Empty,
                $"service={ServiceName};state={status.CurrentState};win32Exit={status.Win32ExitCode};serviceExit={status.ServiceSpecificExitCode}"));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            var failures = new List<Exception>();
            if (!_stopped)
            {
                CaptureCleanupFailure(
                    failures,
                    () =>
                    {
                        _forcedCleanupUsed |= StopServiceForCleanup(
                            RequiredService(),
                            ServiceName,
                            _processHandle,
                            TimeSpan.FromSeconds(45));
                        _stopped = true;
                    });
            }

            if (_stopped)
            {
                var transitionFailureCount = failures.Count;
                CaptureCleanupFailure(failures, () =>
                {
                    if (!WaitForProcessExit(_processHandle, TimeSpan.FromSeconds(45)))
                    {
                        throw new TimeoutException(
                            "Runner Agent retained process handle did not signal after SCM stop.");
                    }
                });
                if (failures.Count == transitionFailureCount)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => RemoveProtectedPackageInstallations(
                            _packageCacheRoot,
                            ServiceName,
                            _contract.ServiceSid));
                }

                if (failures.Count == transitionFailureCount)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteProvisionedCacheNamespace(_packageCacheRoot));
                }

                if (failures.Count == transitionFailureCount)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteServiceEnvironment(ServiceName, _environmentEntries));
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteEventLogSource(
                            ServiceName,
                            new SecurityIdentifier(_contract.ServiceSid)));
                    var service = _service;
                    if (service is not null)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteServiceRequired(service, ServiceName));
                        CaptureCleanupFailure(failures, service.Dispose);
                        _service = null;
                    }

                    CaptureCleanupFailure(
                        failures,
                        () => WaitForServiceDeletion(
                            ServiceName,
                            TimeSpan.FromSeconds(45)));
                }
            }

            CaptureCleanupFailure(failures, _processHandle.Dispose);
            CaptureCleanupFailure(failures, _manager.Dispose);
            ServiceDeletionVerified = _stopped && failures.Count == 0;
            _disposed = true;
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    $"Runner Agent service '{ServiceName}' cleanup was incomplete.",
                    failures);
            }
        }

        private SafeServiceHandle RequiredService() =>
            _service is { IsInvalid: false, IsClosed: false } service
                ? service
                : throw new ObjectDisposedException(nameof(RunnerAgentWindowsService));

        private static string[] CreateServiceEnvironmentEntries(
            Dictionary<string, string> environment)
        {
            if (environment.Count == 0)
            {
                throw new InvalidDataException(
                    "Runner Agent service environment cannot be empty.");
            }

            return environment
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair =>
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)
                        || pair.Key.Contains('=')
                        || pair.Key.Contains('\0')
                        || pair.Value.Contains('\0')
                        || pair.Value.Contains('\r')
                        || pair.Value.Contains('\n'))
                    {
                        throw new InvalidDataException(
                            "Runner Agent service environment contains an invalid name or value.");
                    }

                    return $"{pair.Key}={pair.Value}";
                })
                .ToArray();
        }

        private static void ProtectAndWriteServiceEnvironment(
            string serviceName,
            string[] environmentEntries)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.FullControl)
                ?? throw new InvalidOperationException(
                    "Runner Agent SCM registry key is missing after service creation.");
            var currentSid = WindowsIdentity.GetCurrent().User
                             ?? throw new InvalidOperationException(
                                 "Runner gate identity has no SID for service-key ACL protection.");
            var allowedSids = ServiceRegistryFullControlSids(currentSid);
            var security = new RegistrySecurity();
            security.SetOwner(currentSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in allowedSids)
            {
                security.AddAccessRule(new RegistryAccessRule(
                    sid,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            key.SetAccessControl(security);
            key.SetValue("Environment", environmentEntries, RegistryValueKind.MultiString);
            key.Flush();
            VerifyServiceRegistryAcl(key, allowedSids, currentSid);
        }

        private static void VerifyServiceEnvironment(
            string serviceName,
            string[] expected)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                writable: false)
                ?? throw new InvalidOperationException(
                    "Runner Agent SCM registry key is missing during environment verification.");
            var currentSid = WindowsIdentity.GetCurrent().User
                             ?? throw new InvalidOperationException(
                                 "Runner gate identity has no SID for service-key ACL verification.");
            VerifyServiceRegistryAcl(
                key,
                ServiceRegistryFullControlSids(currentSid),
                currentSid);
            var actual = key.GetValue(
                "Environment",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string[];
            if (actual is null || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "Runner Agent SCM environment differs from the exact in-memory launch contract.");
            }
        }

        private static void DeleteServiceEnvironment(
            string serviceName,
            string[] expectedEnvironmentEntries)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.FullControl)
                ?? throw new InvalidOperationException(
                    "Runner Agent SCM registry key is missing before environment cleanup.");
            var currentSid = WindowsIdentity.GetCurrent().User
                             ?? throw new InvalidOperationException(
                                 "Runner gate identity has no SID for service-key cleanup.");
            VerifyServiceRegistryAcl(
                key,
                ServiceRegistryFullControlSids(currentSid),
                currentSid);
            var actual = key.GetValue(
                "Environment",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string[];
            if (actual is null
                || !actual.SequenceEqual(expectedEnvironmentEntries, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "Runner Agent SCM environment changed before cleanup.");
            }

            key.DeleteValue("Environment", throwOnMissingValue: false);
            key.Flush();
            if (key.GetValue("Environment", null) is not null)
            {
                throw new InvalidOperationException(
                    "Runner Agent SCM environment remained after cleanup.");
            }
        }

        private static SecurityIdentifier[] ServiceRegistryFullControlSids(
            SecurityIdentifier currentSid) =>
            new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                currentSid
            }.Distinct().ToArray();

        private static void VerifyServiceRegistryAcl(
            RegistryKey key,
            IReadOnlyCollection<SecurityIdentifier> allowedSids,
            SecurityIdentifier expectedOwner)
        {
            var security = key.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected
                || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(owner.Value, expectedOwner.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runner Agent service registry key inherits access or has an unexpected owner.");
            }

            var allowed = allowedSids
                .Select(static sid => sid.Value)
                .ToHashSet(StringComparer.Ordinal);
            var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<RegistryAccessRule>()
                .ToArray();
            if (rules.Length != allowed.Count
                || rules.Any(rule => rule.IsInherited
                                     || rule.AccessControlType != AccessControlType.Allow
                                     || rule.IdentityReference is not SecurityIdentifier sid
                                     || !allowed.Contains(sid.Value)
                                     || rule.RegistryRights != RegistryRights.FullControl
                                     || rule.InheritanceFlags != InheritanceFlags.ContainerInherit
                                     || rule.PropagationFlags != PropagationFlags.None))
            {
                throw new InvalidOperationException(
                    "Runner Agent service registry ACL is not the exact protected privileged-principal contract.");
            }

            foreach (var sid in allowed)
            {
                if (rules.Count(rule =>
                        rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(identity.Value, sid, StringComparison.Ordinal)) != 1)
                {
                    throw new InvalidOperationException(
                        $"Runner Agent service registry ACL lacks one exact FullControl rule for '{sid}'.");
                }
            }
        }

        private static void RegisterEventLogSource(
            string serviceName,
            SecurityIdentifier serviceSid)
        {
            if (EventLog.SourceExists(serviceName))
            {
                throw new InvalidOperationException(
                    $"Runner Agent EventLog source '{serviceName}' already exists.");
            }

            var created = false;
            try
            {
                EventLog.CreateEventSource(serviceName, "Application");
                created = true;
                using var key = Registry.LocalMachine.OpenSubKey(
                    EventLogSourceRegistryPrefix + serviceName,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.FullControl)
                    ?? throw new InvalidOperationException(
                        "Runner Agent EventLog source registry key is missing after creation.");
                var currentSid = WindowsIdentity.GetCurrent().User
                                 ?? throw new InvalidOperationException(
                                     "Runner gate identity has no SID for EventLog ownership.");
                var security = new RegistrySecurity();
                security.SetOwner(currentSid);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[]
                         {
                             new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                             new SecurityIdentifier(
                                 WellKnownSidType.BuiltinAdministratorsSid,
                                 null),
                             currentSid
                         }.Distinct())
                {
                    security.AddAccessRule(new RegistryAccessRule(
                        sid,
                        RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                }

                security.AddAccessRule(new RegistryAccessRule(
                    serviceSid,
                    RegistryRights.ReadKey,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                key.SetAccessControl(security);
                key.Flush();
                VerifyEventLogSource(serviceName, serviceSid, currentSid);
            }
            catch (Exception exception)
            {
                if (!created)
                {
                    throw;
                }

                try
                {
                    EventLog.DeleteEventSource(serviceName);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Runner Agent EventLog registration failed and rollback was incomplete.",
                        exception,
                        cleanupException);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(exception)
                    .Throw();
                throw;
            }
        }

        private static void VerifyEventLogSource(
            string serviceName,
            SecurityIdentifier serviceSid,
            SecurityIdentifier currentSid)
        {
            if (!EventLog.SourceExists(serviceName)
                || !string.Equals(
                    EventLog.LogNameFromSourceName(serviceName, "."),
                    "Application",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runner Agent EventLog source '{serviceName}' is not bound to Application.");
            }

            using var key = Registry.LocalMachine.OpenSubKey(
                EventLogSourceRegistryPrefix + serviceName,
                writable: false)
                ?? throw new InvalidOperationException(
                    "Runner Agent EventLog source registry key is missing.");
            var security = key.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected
                || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(owner.Value, currentSid.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runner Agent EventLog source inherits access or has an unexpected owner.");
            }

            var fullControlSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                currentSid.Value
            }.ToHashSet(StringComparer.Ordinal);
            var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<RegistryAccessRule>()
                .ToArray();
            if (rules.Any(rule => rule.IsInherited
                                  || rule.AccessControlType != AccessControlType.Allow
                                  || rule.IdentityReference is not SecurityIdentifier sid
                                  || !fullControlSids.Contains(sid.Value)
                                  && !string.Equals(
                                      sid.Value,
                                      serviceSid.Value,
                                      StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Runner Agent EventLog source ACL contains an unexpected rule.");
            }

            foreach (var sid in fullControlSids)
            {
                if (!rules.Any(rule =>
                        rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(identity.Value, sid, StringComparison.Ordinal)
                        && rule.RegistryRights == RegistryRights.FullControl))
                {
                    throw new InvalidOperationException(
                        $"Runner Agent EventLog source lacks exact FullControl for '{sid}'.");
                }
            }

            var serviceRules = rules.Where(rule =>
                    rule.IdentityReference is SecurityIdentifier identity
                    && string.Equals(
                        identity.Value,
                        serviceSid.Value,
                        StringComparison.Ordinal))
                .ToArray();
            if (serviceRules.Length != 1
                || serviceRules[0].RegistryRights != RegistryRights.ReadKey)
            {
                throw new InvalidOperationException(
                    "Runner Agent EventLog source must grant its exact service SID one ReadKey rule.");
            }
        }

        private static void DeleteEventLogSource(
            string serviceName,
            SecurityIdentifier serviceSid)
        {
            if (EventLog.SourceExists(serviceName))
            {
                var currentSid = WindowsIdentity.GetCurrent().User
                                 ?? throw new InvalidOperationException(
                                     "Runner gate identity has no SID for EventLog cleanup.");
                VerifyEventLogSource(serviceName, serviceSid, currentSid);
                EventLog.DeleteEventSource(serviceName);
            }

            using var key = Registry.LocalMachine.OpenSubKey(
                EventLogSourceRegistryPrefix + serviceName,
                writable: false);
            if (key is not null)
            {
                throw new InvalidOperationException(
                    $"Runner Agent EventLog source '{serviceName}' remained after cleanup.");
            }
        }

        private static void VerifyServiceConfiguration(
            SafeServiceHandle service,
            RunnerAgentServiceContract contract)
        {
            if (!QueryServiceConfig(
                    service,
                    IntPtr.Zero,
                    0,
                    out var requiredBytes)
                && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not size Runner Agent SCM configuration.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!QueryServiceConfig(
                        service,
                        buffer,
                        requiredBytes,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read Runner Agent SCM configuration.");
                }

                var config = Marshal.PtrToStructure<QueryServiceConfiguration>(buffer);
                var binaryPath = Marshal.PtrToStringUni(config.BinaryPathName);
                var accountName = Marshal.PtrToStringUni(config.ServiceStartName);
                if (config.ServiceType != ServiceWin32OwnProcess
                    || config.StartType != ServiceDemandStart
                    || !string.Equals(
                        binaryPath,
                        QuoteServiceBinaryPath(contract.ExecutablePath),
                        StringComparison.Ordinal)
                    || !string.Equals(
                        accountName,
                        contract.ServiceAccountName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Runner Agent SCM configuration differs from its exact service contract.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var sidInfo = default(ServiceSidInfo);
            if (!QueryServiceConfig2(
                    service,
                    ServiceConfigServiceSidInfo,
                    ref sidInfo,
                    checked((uint)Marshal.SizeOf<ServiceSidInfo>()),
                    out _)
                || sidInfo.ServiceSidType != ServiceSidTypeRestricted)
            {
                throw new InvalidDataException(
                    "Runner Agent SCM service SID type is not Restricted.");
            }
        }

        private static void StopServiceCleanly(
            SafeServiceHandle service,
            string serviceName,
            SafeProcessHandle processHandle,
            TimeSpan timeout)
        {
            ValidateServiceTransitionTimeout(timeout);
            var status = QueryServiceStatus(service, serviceName);
            if (status.CurrentState != ServiceRunning)
            {
                throw new InvalidOperationException(
                    $"Runner Agent service '{serviceName}' was not Running when its clean SCM stop was requested; state was {status.CurrentState}.");
            }

            if (!ControlService(service, ServiceControlStop, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not cleanly stop Runner Agent service '{serviceName}'.");
            }

            var stopped = WaitForServiceState(
                service,
                serviceName,
                ServiceStopped,
                timeout);
            if (!WaitForProcessExit(processHandle, timeout))
            {
                throw new TimeoutException(
                    "Runner Agent SCM process remained alive after its clean service stop.");
            }

            VerifyCleanServiceExit(stopped, processHandle, serviceName);
        }

        private static bool StopServiceForCleanup(
            SafeServiceHandle service,
            string serviceName,
            SafeProcessHandle? processHandle,
            TimeSpan timeout)
        {
            ValidateServiceTransitionTimeout(timeout);
            var forcedTerminationUsed = false;
            var status = QueryServiceStatus(service, serviceName);
            if (status.CurrentState != ServiceStopped
                && status.CurrentState != ServiceStopPending
                && !ControlService(service, ServiceControlStop, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not stop Runner Agent service '{serviceName}'.");
                }
            }

            try
            {
                _ = WaitForServiceState(
                    service,
                    serviceName,
                    ServiceStopped,
                    timeout);
            }
            catch (TimeoutException) when (processHandle is not null)
            {
                if (!WaitForProcessExit(processHandle, TimeSpan.Zero))
                {
                    if (!TerminateProcess(processHandle, 1))
                    {
                        if (!WaitForProcessExit(processHandle, TimeSpan.Zero))
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "Could not terminate the Runner Agent SCM process after a stop timeout.");
                        }
                    }
                    else
                    {
                        forcedTerminationUsed = true;
                    }
                }

                if (!WaitForProcessExit(processHandle, timeout))
                {
                    throw new TimeoutException(
                        "Runner Agent SCM process did not exit after forced termination.");
                }

                _ = WaitForServiceState(
                    service,
                    serviceName,
                    ServiceStopped,
                    timeout);
            }

            if (processHandle is not null
                && !WaitForProcessExit(processHandle, timeout))
            {
                throw new TimeoutException(
                    "Runner Agent SCM process remained alive after service stop.");
            }

            return forcedTerminationUsed;
        }

        private static void VerifyCleanServiceExit(
            ServiceStatusProcess status,
            SafeProcessHandle processHandle,
            string serviceName)
        {
            if (status.CurrentState != ServiceStopped
                || status.Win32ExitCode != 0
                || status.ServiceSpecificExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Runner Agent service '{serviceName}' did not report a zero clean SCM exit "
                    + $"(state {status.CurrentState}, Win32 {status.Win32ExitCode}, service {status.ServiceSpecificExitCode}).");
            }

            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the Runner Agent SCM process exit code after clean stop.");
            }

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Runner Agent service '{serviceName}' process exited with {exitCode}, not zero.");
            }
        }

        private static void ValidateServiceTransitionTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero
                || timeout.TotalMilliseconds > uint.MaxValue - 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    "Runner Agent service transition timeout must be positive and representable by Win32.");
            }
        }

        private static ServiceStatusProcess WaitForServiceState(
            SafeServiceHandle service,
            string serviceName,
            uint requiredState,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                var status = QueryServiceStatus(service, serviceName);
                if (status.CurrentState == requiredState)
                {
                    return status;
                }

                if (requiredState == ServiceRunning
                    && status.CurrentState == ServiceStopped)
                {
                    throw new InvalidOperationException(
                        $"Runner Agent service '{serviceName}' stopped during startup "
                        + $"(Win32 {status.Win32ExitCode}, service {status.ServiceSpecificExitCode}).");
                }

                Thread.Sleep(100);
            }

            throw new TimeoutException(
                $"Runner Agent service '{serviceName}' did not reach state {requiredState} within {timeout}.");
        }

        private static ServiceStatusProcess QueryServiceStatus(
            SafeServiceHandle service,
            string serviceName)
        {
            var status = default(ServiceStatusProcess);
            if (!QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    ref status,
                    checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not query Runner Agent service '{serviceName}'.");
            }

            return status;
        }

        private static void DeleteServiceRequired(
            SafeServiceHandle service,
            string serviceName)
        {
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceMarkedForDelete)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not delete Runner Agent service '{serviceName}'.");
                }
            }
        }

        private static void WaitForServiceDeletion(
            string serviceName,
            TimeSpan timeout)
        {
            using var manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect);
            if (manager.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not open a fresh SCM handle to prove Runner Agent deletion.");
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                using var service = OpenService(manager, serviceName, ServiceQueryStatus);
                if (service.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist)
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(
                            ServiceRegistryPrefix + serviceName,
                            writable: false);
                        if (key is null)
                        {
                            return;
                        }
                    }
                    else if (error != ErrorServiceMarkedForDelete)
                    {
                        throw new Win32Exception(
                            error,
                            $"Could not prove Runner Agent service '{serviceName}' deletion.");
                    }
                }

                Thread.Sleep(100);
            }

            throw new TimeoutException(
                $"Runner Agent service '{serviceName}' was not fully deleted within {timeout}.");
        }

        private static string ReadProcessExecutablePath(SafeProcessHandle processHandle)
        {
            const int maximumWindowsPathLength = 32_768;
            var path = new StringBuilder(maximumWindowsPathLength);
            var length = checked((uint)path.Capacity);
            if (!QueryFullProcessImageName(
                    processHandle,
                    flags: 0,
                    path,
                    ref length)
                || length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read Runner Agent SCM process image path.");
            }

            return Path.GetFullPath(path.ToString(0, checked((int)length)));
        }

        private static RunnerAgentTokenEvidence ReadRequiredTokenEvidence(
            SafeProcessHandle processHandle,
            string expectedAccountSid,
            string expectedServiceSid)
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var token))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not open Runner Agent SCM process token.");
            }

            using (token)
            {
                var userSid = ReadTokenUserSid(token);
                var groups = ReadTokenGroups(token, TokenInformationClass.TokenGroups);
                var restricted = ReadTokenGroups(
                    token,
                    TokenInformationClass.TokenRestrictedSids);
                var administratorSid = new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null).Value;
                var administrator = groups.SingleOrDefault(group => string.Equals(
                    group.Sid,
                    administratorSid,
                    StringComparison.Ordinal));
                var serviceLogon = groups.SingleOrDefault(group => string.Equals(
                    group.Sid,
                    "S-1-5-6",
                    StringComparison.Ordinal));
                var exactService = groups.SingleOrDefault(group => string.Equals(
                    group.Sid,
                    expectedServiceSid,
                    StringComparison.Ordinal));
                var exactRestricted = restricted.Any(group => string.Equals(
                    group.Sid,
                    expectedServiceSid,
                    StringComparison.Ordinal));
                var logonSid = groups.SingleOrDefault(group =>
                    (group.Attributes & GroupLogonId) == GroupLogonId);
                var requiredRestrictedSids = logonSid is null
                    ? []
                    : new[] { "S-1-1-0", "S-1-5-33", logonSid.Sid };
                var evidence = new RunnerAgentTokenEvidence(
                    userSid,
                    IsPrimaryToken: ReadTokenInt32(token, TokenInformationClass.TokenType) == 1,
                    IsElevated: ReadTokenInt32(token, TokenInformationClass.TokenElevation) != 0,
                    HasRestrictions: ReadTokenBoolean(
                        token,
                        TokenInformationClass.TokenHasRestrictions)
                        && IsTokenRestricted(token),
                    AdministratorGroupPresent: administrator is not null,
                    AdministratorGroupEnabled: IsEnabled(administrator),
                    AdministratorGroupDenyOnly: administrator is not null
                                                && (administrator.Attributes
                                                    & GroupUseForDenyOnly) != 0,
                    ServiceLogonSidPresent: serviceLogon is not null,
                    ServiceLogonSidEnabled: IsEnabled(serviceLogon),
                    ExactServiceSidPresent: exactService is not null,
                    ExactServiceSidEnabled: IsEnabled(exactService),
                    ExactServiceSidRestricted: exactRestricted,
                    RequiredRestrictedSidsPresent: logonSid is not null
                                                   && requiredRestrictedSids.All(sid =>
                                                       restricted.Any(group => string.Equals(
                                                           group.Sid,
                                                           sid,
                                                           StringComparison.Ordinal))),
                    IsSystem: string.Equals(userSid, "S-1-5-18", StringComparison.Ordinal));
                if (!string.Equals(userSid, expectedAccountSid, StringComparison.Ordinal)
                    || !evidence.NonAdministrative)
                {
                    throw new InvalidOperationException(
                        "Runner Agent SCM token did not prove LocalService plus its exact enabled restricted service SID. "
                        + JsonSerializer.Serialize(evidence));
                }

                return evidence;
            }

            static bool IsEnabled(TokenGroup? group) => group is not null
                                                        && (group.Attributes & GroupEnabled) != 0
                                                        && (group.Attributes
                                                            & GroupUseForDenyOnly) == 0;
        }

        private static string ReadTokenUserSid(SafeAccessTokenHandle token)
        {
            using var buffer = ReadTokenBuffer(token, TokenInformationClass.TokenUser);
            var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer.DangerousGetHandle());
            return new SecurityIdentifier(tokenUser.User.Sid).Value;
        }

        private static List<TokenGroup> ReadTokenGroups(
            SafeAccessTokenHandle token,
            TokenInformationClass informationClass)
        {
            using var buffer = ReadTokenBuffer(token, informationClass);
            var pointer = buffer.DangerousGetHandle();
            var count = Marshal.ReadInt32(pointer);
            var offset = Marshal.OffsetOf<TokenGroupsHeader>(
                nameof(TokenGroupsHeader.Groups)).ToInt32();
            var size = Marshal.SizeOf<SidAndAttributes>();
            var result = new List<TokenGroup>(count);
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<SidAndAttributes>(
                    IntPtr.Add(pointer, checked(offset + index * size)));
                result.Add(new TokenGroup(
                    new SecurityIdentifier(item.Sid).Value,
                    item.Attributes));
            }

            return result;
        }

        private static int ReadTokenInt32(
            SafeAccessTokenHandle token,
            TokenInformationClass informationClass)
        {
            using var buffer = ReadTokenBuffer(token, informationClass);
            if (buffer.Size != sizeof(int))
            {
                throw new InvalidDataException(
                    $"Runner Agent token information {informationClass} requires an exact {sizeof(int)}-byte integer, not {buffer.Size} bytes.");
            }

            return Marshal.ReadInt32(buffer.DangerousGetHandle());
        }

        private static bool ReadTokenBoolean(
            SafeAccessTokenHandle token,
            TokenInformationClass informationClass)
        {
            using var buffer = ReadTokenBuffer(token, informationClass);
            return buffer.Size switch
            {
                sizeof(byte) => Marshal.ReadByte(buffer.DangerousGetHandle()) != 0,
                sizeof(int) => Marshal.ReadInt32(buffer.DangerousGetHandle()) != 0,
                _ => throw new InvalidDataException(
                    $"Runner Agent token information {informationClass} requires an unsupported Boolean length of {buffer.Size} bytes.")
            };
        }

        private static SafeHGlobalHandle ReadTokenBuffer(
            SafeAccessTokenHandle token,
            TokenInformationClass informationClass)
        {
            _ = GetTokenInformation(
                token,
                informationClass,
                IntPtr.Zero,
                0,
                out var requiredBytes);
            if (requiredBytes == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not size Runner Agent token information {informationClass}.");
            }

            var buffer = new SafeHGlobalHandle(checked((int)requiredBytes));
            if (!GetTokenInformation(
                    token,
                    informationClass,
                    buffer.DangerousGetHandle(),
                    requiredBytes,
                    out var returnedBytes))
            {
                var error = Marshal.GetLastWin32Error();
                buffer.Dispose();
                throw new Win32Exception(
                    error,
                    $"Could not read Runner Agent token information {informationClass}.");
            }

            if (returnedBytes != requiredBytes)
            {
                buffer.Dispose();
                throw new InvalidDataException(
                    $"Runner Agent token information {informationClass} returned {returnedBytes} bytes after requiring {requiredBytes} bytes.");
            }

            return buffer;
        }

        private static bool WaitForProcessExit(
            SafeProcessHandle processHandle,
            TimeSpan timeout)
        {
            var milliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
            return WaitForSingleObject(processHandle, milliseconds) switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not wait for Runner Agent SCM process exit."),
                var result => throw new InvalidOperationException(
                    $"Unexpected Runner Agent process wait result 0x{result:x8}.")
            };
        }

        private static bool IsRunnerOwnedPath(string path, string ownedRoot)
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot))
                         + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectRunnerOwnedPath(string path, string ownedRoot)
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
            var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return string.Equals(
                Path.GetDirectoryName(canonicalPath),
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void VerifyProvisionedPackageCache(
            string packageCacheRoot,
            string stationServiceSid)
        {
            if (!Directory.Exists(packageCacheRoot) || File.Exists(packageCacheRoot))
            {
                throw new InvalidOperationException(
                    "Runner Agent administrative package cache provisioning is missing.");
            }

            EnsureNoReparseInPath(packageCacheRoot);
            new ImmutableContentProtector().VerifyCacheBoundary(
                packageCacheRoot,
                new ImmutableContentProtectionPolicy(
                    WindowsAppContainerIdentity.EnsureCapabilitySid(
                        WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                    stationServiceSid));
        }

        private static void GrantDirectoryAccess(
            string path,
            SecurityIdentifier sid,
            FileSystemRights rights,
            InheritanceFlags inheritanceFlags)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            var security = FileSystemAclExtensions.GetAccessControl(directory);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                rights,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(directory, security);
        }

        private static void RemoveDirectoryAccess(
            string path,
            SecurityIdentifier sid)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(path));
            var security = FileSystemAclExtensions.GetAccessControl(directory);
            security.PurgeAccessRules(sid);
            FileSystemAclExtensions.SetAccessControl(directory, security);
        }

        private static string QuoteServiceBinaryPath(string executablePath)
        {
            if (executablePath.Contains('"') || executablePath.Contains('\0'))
            {
                throw new InvalidDataException(
                    "Runner Agent service executable path cannot be represented by SCM.");
            }

            return $"\"{executablePath}\"";
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle CreateService(
            SafeServiceHandle serviceControlManager,
            string serviceName,
            string displayName,
            uint desiredAccess,
            uint serviceType,
            uint startType,
            uint errorControl,
            string binaryPathName,
            string? loadOrderGroup,
            IntPtr tagId,
            string? dependencies,
            string serviceStartName,
            string? password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle OpenService(
            SafeServiceHandle serviceControlManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(
            SafeServiceHandle service,
            uint argumentCount,
            IntPtr argumentVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(
            SafeServiceHandle service,
            uint control,
            out ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(
            SafeServiceHandle service,
            uint infoLevel,
            ref ServiceStatusProcess serviceStatus,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig(
            SafeServiceHandle service,
            IntPtr queryServiceConfig,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2(
            SafeServiceHandle service,
            uint infoLevel,
            ref ServiceSidInfo info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig2(
            SafeServiceHandle service,
            uint infoLevel,
            ref ServiceSidInfo buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(SafeServiceHandle service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            SafeProcessHandle processHandle,
            uint desiredAccess,
            out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            SafeAccessTokenHandle tokenHandle,
            TokenInformationClass tokenInformationClass,
            IntPtr tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsTokenRestricted(SafeAccessTokenHandle tokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "QueryFullProcessImageNameW writes into a caller-owned Win32 buffer.")]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle processHandle,
            uint flags,
            StringBuilder executablePath,
            ref uint executablePathLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            SafeProcessHandle processHandle,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            SafeProcessHandle processHandle,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeProcessHandle handle,
            uint milliseconds);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryServiceConfiguration
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceSidInfo
        {
            public uint ServiceSidType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenUser
        {
            public SidAndAttributes User;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenGroupsHeader
        {
            public uint GroupCount;
            public SidAndAttributes Groups;
        }

        private enum TokenInformationClass
        {
            TokenUser = 1,
            TokenGroups = 2,
            TokenType = 8,
            TokenRestrictedSids = 11,
            TokenElevation = 20,
            TokenHasRestrictions = 21
        }

        private sealed record TokenGroup(string Sid, uint Attributes);

        private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeServiceHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);

            [DllImport("advapi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseServiceHandle(IntPtr serviceHandle);
        }

        private sealed class SafeHGlobalHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeHGlobalHandle(int size)
                : base(ownsHandle: true)
            {
                if (size <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(size),
                        "Native token buffers must have a positive size.");
                }

                Size = size;
                SetHandle(Marshal.AllocHGlobal(size));
                for (var offset = 0; offset < size; offset++)
                {
                    Marshal.WriteByte(handle, offset, 0);
                }
            }

            public int Size { get; }

            protected override bool ReleaseHandle()
            {
                Marshal.FreeHGlobal(handle);
                return true;
            }
        }
    }

    private sealed record RunnerAgentTokenEvidence(
        string UserSid,
        bool IsPrimaryToken,
        bool IsElevated,
        bool HasRestrictions,
        bool AdministratorGroupPresent,
        bool AdministratorGroupEnabled,
        bool AdministratorGroupDenyOnly,
        bool ServiceLogonSidPresent,
        bool ServiceLogonSidEnabled,
        bool ExactServiceSidPresent,
        bool ExactServiceSidEnabled,
        bool ExactServiceSidRestricted,
        bool RequiredRestrictedSidsPresent,
        bool IsSystem)
    {
        public bool NonAdministrative =>
            IsPrimaryToken
            && !IsElevated
            && HasRestrictions
            && !AdministratorGroupEnabled
            && (!AdministratorGroupPresent || AdministratorGroupDenyOnly)
            && ServiceLogonSidPresent
            && ServiceLogonSidEnabled
            && ExactServiceSidPresent
            && ExactServiceSidEnabled
            && ExactServiceSidRestricted
            && RequiredRestrictedSidsPresent
            && !IsSystem;
    }

    [SupportedOSPlatform("windows")]
    private sealed class GateProcess : IDisposable
    {
        private const int MaximumCapturedCharacters = 2 * 1024 * 1024;
        private readonly WindowsIsolatedProcess _isolatedProcess;
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;
        private bool _disposed;

        private GateProcess(
            WindowsIsolatedProcess isolatedProcess,
            Process process,
            string runningImageSha256)
        {
            _isolatedProcess = isolatedProcess;
            _process = process;
            ProcessId = process.Id;
            RunningImageSha256 = runningImageSha256;
            MainModuleBound = true;
            JobObjectBound = true;
            _isolatedProcess.StandardInput.Dispose();
            _standardOutput = CaptureOutputAsync(_isolatedProcess.StandardOutput);
            _standardError = CaptureOutputAsync(_isolatedProcess.StandardError);
        }

        public int ProcessId { get; }

        public bool HasExited => _process.HasExited;

        public bool ProcessTreeTerminated { get; private set; }

        public string RunningImageSha256 { get; }

        public bool MainModuleBound { get; }

        public bool JobObjectBound { get; }

        public static GateProcess Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyCollection<string>? arguments = null,
            string? expectedExecutableSha256 = null)
        {
            var expectedImageSha256 = RequiredSha256(
                expectedExecutableSha256,
                "Expected staged process image SHA-256");
            WindowsIsolatedProcess? isolatedProcess = null;
            Process? process = null;
            try
            {
                isolatedProcess = new WindowsProcessLauncher().Launch(
                    new IsolatedProcessStartRequest(
                        Path.GetFullPath(executablePath),
                        arguments ?? [],
                        Path.GetFullPath(workingDirectory),
                        environment,
                        new WindowsProcessLimits(
                            ActiveProcessLimit: 64,
                            ProcessMemoryLimitBytes: 2L * 1024 * 1024 * 1024,
                            JobMemoryLimitBytes: 4L * 1024 * 1024 * 1024,
                            CpuTimeLimit: TimeSpan.FromMinutes(5))));
                process = Process.GetProcessById(isolatedProcess.Id);
                if (process.Id <= 0 || process.Id == Environment.ProcessId)
                {
                    throw new InvalidOperationException("A staged process returned an invalid PID.");
                }

                if (isolatedProcess.ActiveProcessCount == 0)
                {
                    throw new InvalidOperationException(
                        "The suspended staged process was not observable in its Job Object.");
                }

                var runningPath = ReadRequiredExecutablePath(process);
                if (!string.Equals(
                        Path.GetFullPath(runningPath),
                        Path.GetFullPath(executablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The running staged process image path differs from the attested executable.");
                }

                var runningImageSha256 = FileSha256(runningPath);
                if (!string.Equals(
                        runningImageSha256,
                        expectedImageSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The running staged process image hash differs from its bundle attestation.");
                }

                return new GateProcess(isolatedProcess, process, runningImageSha256);
            }
            catch (Exception exception)
            {
                var failures = new List<Exception> { exception };
                CaptureCleanupFailure(
                    failures,
                    () => isolatedProcess?.TerminateProcessTree());
                if (process is not null)
                {
                    CaptureCleanupFailure(failures, () => KillProcessTreeFallback(process));
                }

                CaptureCleanupFailure(failures, () => isolatedProcess?.Dispose());
                if (process is not null)
                {
                    CaptureCleanupFailure(failures, () =>
                    {
                        if (!process.HasExited && !process.WaitForExit(10_000))
                        {
                            throw new TimeoutException(
                                "The failed staged-process launch did not terminate within the cleanup bound.");
                        }
                    });
                    CaptureCleanupFailure(failures, process.Dispose);
                }

                ThrowFailures(
                    "The staged process failed to launch and cleanup was not complete.",
                    failures);
                throw new UnreachableException();
            }
        }

        private static string ReadRequiredExecutablePath(Process process)
        {
            const int maximumWindowsPathLength = 32_768;
            var executablePath = new StringBuilder(maximumWindowsPathLength);
            var executablePathLength = checked((uint)executablePath.Capacity);
            if (!QueryFullProcessImageName(
                    process.Handle,
                    flags: 0,
                    executablePath,
                    ref executablePathLength))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect the staged process image path.");
            }

            if (executablePathLength == 0)
            {
                throw new InvalidOperationException(
                    "The staged process image path is empty.");
            }

            return Path.GetFullPath(
                executablePath.ToString(0, checked((int)executablePathLength)));
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "QueryFullProcessImageNameW",
            SetLastError = true,
            CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "QueryFullProcessImageNameW writes the image path into a caller-owned buffer.")]
        private static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            uint flags,
            StringBuilder executablePath,
            ref uint executablePathLength);

        public async Task<GateProcessCapture> WaitForExitAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                try
                {
                    await KillTreeAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception cleanupException)
                {
                    var timeoutException = new TimeoutException(
                        "The staged process exceeded its bounded execution time.");
                    throw new AggregateException(
                        "The staged process timed out and process-tree cleanup failed.",
                        timeoutException,
                        cleanupException);
                }

                var timeoutStandardOutput = await _standardOutput;
                var timeoutStandardError = await _standardError;
                throw new TimeoutException(
                    "The staged process exceeded its bounded execution time and its process tree was terminated. "
                    + $"stdout: {timeoutStandardOutput} stderr: {timeoutStandardError}");
            }

            await WaitForJobEmptyAsync(TimeSpan.FromSeconds(10));
            var stdout = await _standardOutput;
            var stderr = await _standardError;
            return new GateProcessCapture(
                ProcessId,
                _isolatedProcess.ExitCode,
                stdout,
                stderr,
                ProcessTreeTerminated,
                RunningImageSha256,
                MainModuleBound,
                JobObjectBound);
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            if (!_process.HasExited || _isolatedProcess.ActiveProcessCount != 0)
            {
                await KillTreeAsync(timeout);
            }
            else
            {
                await WaitForJobEmptyAsync(timeout);
            }

            _ = await _standardOutput;
            _ = await _standardError;
        }

        public async Task<ProcessOutput> GetCapturedOutputAsync() =>
            new(await _standardOutput, await _standardError);

        private async Task KillTreeAsync(TimeSpan timeout)
        {
            var failures = new List<Exception>();
            CaptureCleanupFailure(failures, _isolatedProcess.TerminateProcessTree);
            CaptureCleanupFailure(failures, () => KillProcessTreeFallback(_process));

            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await _isolatedProcess.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                CaptureCleanupFailure(failures, () => KillProcessTreeFallback(_process));
                failures.Add(new TimeoutException(
                    "The staged process tree did not terminate within the cleanup bound."));
            }

            try
            {
                await WaitForJobEmptyAsync(timeout);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            ThrowFailures(
                "The staged process tree could not be terminated cleanly.",
                failures);
        }

        private async Task WaitForJobEmptyAsync(TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (_isolatedProcess.ActiveProcessCount == 0)
                {
                    ProcessTreeTerminated = true;
                    return;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException(
                "The staged process Job Object still owns live descendants after cleanup.");
        }

        private static async Task<string> CaptureOutputAsync(Stream stream)
        {
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);
            var result = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory());
                if (read == 0)
                {
                    return result.ToString();
                }

                if (result.Length + read > MaximumCapturedCharacters)
                {
                    throw new InvalidDataException(
                        "A staged process exceeded the bounded output capture limit.");
                }

                result.Append(buffer, 0, read);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            var failures = new List<Exception>();
            CaptureCleanupFailure(failures, () =>
            {
                if (_isolatedProcess.ActiveProcessCount != 0)
                {
                    _isolatedProcess.TerminateProcessTree();
                }
            });
            CaptureCleanupFailure(failures, () => KillProcessTreeFallback(_process));
            CaptureCleanupFailure(failures, () =>
            {
                if (!_process.HasExited && !_process.WaitForExit(10_000))
                {
                    throw new TimeoutException(
                        "The staged process did not exit within the disposal bound.");
                }
            });
            CaptureCleanupFailure(failures, () =>
            {
                ProcessTreeTerminated = _process.HasExited
                                        && _isolatedProcess.ActiveProcessCount == 0;
                if (!ProcessTreeTerminated)
                {
                    throw new InvalidOperationException(
                        "The staged process Job Object still owns live processes during disposal.");
                }
            });

            CaptureCleanupFailure(failures, _isolatedProcess.Dispose);
            CaptureCleanupFailure(failures, _process.Dispose);
            _disposed = true;
            ThrowFailures(
                "The staged process could not be disposed cleanly.",
                failures);
        }

        private static void KillProcessTreeFallback(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        private static void ThrowFailures(string message, List<Exception> failures)
        {
            if (failures.Count == 0)
            {
                return;
            }

            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }

            throw new AggregateException(message, failures);
        }
    }

    private sealed record StagedAgentPrerequisites(
        string RunnerBundleRoot,
        string AgentBundleRoot,
        string PostgreSqlConnectionString,
        Uri RabbitMqUri,
        string EvidencePath,
        string ScopeId,
        RunnerAgentServiceContract Service);

    private sealed record RunnerAgentServiceContract(
        string ManifestPath,
        string Scope,
        string ServiceName,
        string ServiceAccountName,
        string ServiceAccountSid,
        string ServiceSid,
        string ExecutablePath,
        string ExecutableSha256,
        string OwnedRoot);

    private sealed record BundleAttestation(
        string Kind,
        string ExecutableFileName,
        string ExecutablePath,
        string ExecutableSha256,
        string ManifestSha256,
        string ChecksumsSha256,
        bool ManifestBound);

    private sealed record PublishedPackageAttestation(
        string PackageFileName,
        string ContentSha256,
        string PackageFileSha256,
        string CatalogFileName,
        string CatalogFileSha256,
        string SigningKeyId,
        string SignatureAlgorithm,
        bool ManifestBound,
        bool DeploymentBound);

    private sealed record SafetyExecutableAttestation(
        string ExecutableFileName,
        string ExecutablePath,
        string ExecutableSha256,
        bool IndependentFromStationRuntime);

    private sealed record RunnerTerminalEvidence(
        string ExecutionStatus,
        string ResultJudgement,
        int OperationCount,
        int CompletedOperationCount,
        int CompletedStepCount,
        int CommandCount,
        int IncidentCount);

    private sealed record PostgreSqlGateEvidence(
        long ProductionRunCount,
        long TerminalEvidenceCount,
        long StationJobCount,
        long StationResultCount,
        long UnpublishedJobCount,
        long TerminalOutboxCount,
        long CreatedOutboxCount,
        string ExecutionStatus,
        int ResultArtifactCount,
        string StationJobId,
        string StationResultMessageId,
        string SnapshotSha256);

    private sealed record AgentSqliteGateEvidence(
        long JobCount,
        long InboxCount,
        long CompletionOutboxCount,
        long AcknowledgedCompletionCount,
        long PendingOutboxCount,
        long SafetyInboxCount,
        string Status,
        long Revision,
        int CommandCount,
        string DatabaseSha256);

    private sealed record RabbitMqGateEvidence(
        uint JobQueueMessageCount,
        uint ResultQueueMessageCount,
        uint SafetyQueueMessageCount,
        uint JobQueueConsumerCount,
        uint ResultQueueConsumerCount,
        string QueueIdentitySha256,
        string SnapshotSha256);

    private sealed record TraceGateEvidence(
        int RecordCount,
        int OperationCount,
        int CommandCount,
        int ArtifactCount,
        string ExecutionStatus,
        string Judgement,
        string Disposition,
        string DatabaseSha256);

    private sealed record GateObservation(
        PublishedRunnerProject Published,
        Guid ProductionRunId,
        Guid ProductionUnitId,
        BundleAttestation RunnerBundle,
        BundleAttestation AgentBundle,
        PublishedPackageAttestation Package,
        SafetyExecutableAttestation SafetyExecutable,
        int RunnerProcessId,
        int AgentProcessId,
        string RunnerRunningImageSha256,
        string AgentRunningImageSha256,
        bool RunnerMainModuleBound,
        bool AgentMainModuleBound,
        bool RunnerJobObjectBound,
        bool RunnerProcessTreeTerminated,
        string AgentServiceName,
        bool AgentServiceLifecycleVerified,
        string AgentServiceAccountName,
        string AgentServiceAccountSid,
        string AgentServiceSidSha256,
        bool AgentHasRestrictions,
        bool AgentServiceLogonSidPresent,
        bool AgentServiceLogonSidEnabled,
        bool AgentExactServiceSidPresent,
        bool AgentExactServiceSidEnabled,
        bool AgentExactServiceSidRestricted,
        bool AgentNonAdministrative,
        RunnerTerminalEvidence RunnerTerminal,
        PostgreSqlGateEvidence PostgreSql,
        AgentSqliteGateEvidence AgentSqlite,
        RabbitMqGateEvidence RabbitMq,
        TraceGateEvidence Trace,
        int ArtifactEndpointContactCount);

    private sealed record GateProcessCapture(
        int ProcessId,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool ProcessTreeTerminated,
        string RunningImageSha256,
        bool MainModuleBound,
        bool JobObjectBound);

    private sealed record ProcessOutput(
        string StandardOutput,
        string StandardError);
}
