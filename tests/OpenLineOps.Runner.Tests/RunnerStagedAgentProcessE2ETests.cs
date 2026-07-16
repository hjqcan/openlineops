using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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
        var root = Path.Combine(
            Path.GetTempPath(),
            "openlineops-runner-staged-agent-gates",
            suffix,
            "test-work");
        var projectRoot = Path.Combine(root, "project");
        var agentDataRoot = Path.Combine(root, "agent-data");
        var agentPackageCacheRoot = Path.Combine(root, "agent-package-cache");
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
        GateProcess? agent = null;
        GateProcessCapture? runnerCapture = null;

        EnsureNoReparseInPath(Path.GetDirectoryName(root)!);
        Directory.CreateDirectory(projectRoot);
        try
        {
            var runnerBundle = AttestBundle(
                prerequisites.RunnerBundleRoot,
                "runner",
                "headless-runner",
                "OpenLineOps.Runner.exe");
            var agentBundle = AttestBundle(
                prerequisites.AgentBundleRoot,
                "agent",
                "station-agent-service",
                "OpenLineOps.Agent.exe");
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
            agent = GateProcess.Start(
                Path.Combine(prerequisites.AgentBundleRoot, "OpenLineOps.Agent.exe"),
                prerequisites.AgentBundleRoot,
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
                expectedExecutableSha256: agentBundle.ExecutableSha256);
            await rabbitMq.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(45));

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

            await agent.StopAsync(TimeSpan.FromSeconds(15));
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
                agent.JobObjectBound,
                runnerCapture.ProcessTreeTerminated,
                agent.ProcessTreeTerminated,
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
                    await agent.StopAsync(TimeSpan.FromSeconds(15));
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
                if (agent is not null)
                {
                    await agent.StopAsync(TimeSpan.FromSeconds(15));
                }
            });
            CaptureCleanupFailure(cleanupFailures, () => agent?.Dispose());
            agent = null;

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

            CaptureCleanupFailure(
                cleanupFailures,
                () => DeleteGateRoot(root, agentPackageCacheRoot));
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
        Assert.True(observation.AgentProcessTreeTerminated);
        Assert.NotEqual(runnerCapture.ProcessId, observation.AgentProcessId);
        await WriteGateEvidenceAsync(
            prerequisites.EvidencePath,
            observation,
            postgres.SchemaDropped,
            rabbitMq.QueuesDeleted,
            runnerCapture.ProcessTreeTerminated,
            agentTreeTerminated: observation.AgentProcessTreeTerminated,
            temporaryRootDeleted: true);
    }

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
            [GateScopeIdVariable] = Environment.GetEnvironmentVariable(GateScopeIdVariable)
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

        return new StagedAgentPrerequisites(
            runnerBundleRoot,
            agentBundleRoot,
            postgres,
            rabbitUri,
            evidencePath,
            scopeId);
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
        var environment = CreateIsolatedProcessEnvironment(Path.Combine(root, "agent-temp"));
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
        Set("MaterialArrivalPipeName", $"olo-runner-agent-{suffix}");
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
        Set("AllowedRestrictedExternalProgramHostSids__0", "S-1-5-32-545");
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
        bool agentTreeTerminated,
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
                    jobObjectBound = observation.AgentJobObjectBound,
                    processTreeTerminated = observation.AgentProcessTreeTerminated
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
                agentTreeTerminated,
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

    private static void DeleteGateRoot(string root, string packageCacheRoot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var dedicatedBase = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "openlineops-runner-staged-agent-gates"));
        var relative = Path.GetRelativePath(dedicatedBase, Path.GetFullPath(root));
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || segments[0].Length != 32
            || segments[0].Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !string.Equals(segments[1], "test-work", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The gate refuses recursive cleanup outside its dedicated temporary scope.");
        }

        EnsureNoReparseTree(root);
        if (Directory.Exists(packageCacheRoot))
        {
            var protector = new ImmutableContentProtector();
            foreach (var contentDirectory in Directory.GetDirectories(packageCacheRoot))
            {
                var leaf = Path.GetFileName(contentDirectory);
                if (leaf.Length == 64
                    && leaf.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    protector.DeleteProtectedInstallation(packageCacheRoot, contentDirectory);
                }
            }

            if (Directory.Exists(packageCacheRoot))
            {
                Directory.Delete(packageCacheRoot, recursive: true);
            }
        }

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
        public async Task WaitForAgentConsumerAsync(GateProcess agent, TimeSpan timeout)
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
        string ScopeId);

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
        bool AgentJobObjectBound,
        bool RunnerProcessTreeTerminated,
        bool AgentProcessTreeTerminated,
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
