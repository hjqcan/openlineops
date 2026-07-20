using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.ExternalPrograms;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class SignedVendorProgramStationE2ETests : IDisposable
{
    private const string AgentId = "agent.station-main";
    private const string ApplicationId = "application.main";
    private const string CapabilityId = "application.external-program";
    private const string CommandName = "Run";
    private const string FlowDefinitionId = "process.main";
    private const string FlowVersionId = "process.main@1";
    private const string LineDefinitionId = "line.main";
    private const string OperationId = "operation.main";
    private const string AppContainerProfileNamespace = "OpenLineOps.AgentE2E";
    private const string ResourceId = "program.vendor-helper";
    private const string StationId = "station.main.physical";
    private const string StationSystemId = "station.main";
    private const string TopologyId = "topology.main";
    private const long VendorTimeoutMilliseconds = 120_000;
    private static readonly string ArtifactUploadToken = Convert.ToBase64String(
            SHA256.HashData("signed-vendor-artifact-upload"u8))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    private const string StagedAgentBundleRootEnvironmentVariable =
        "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT";
    private const string StagedSamplePluginRootEnvironmentVariable =
        "OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT";
    private const string StagedPythonTokenEvidenceEnvironmentVariable =
        "OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH";

    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions MessageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"olo-ve2e-{Guid.NewGuid():N}");
    private readonly string _artifactRoot;
    private readonly string _cacheRoot;
    private readonly ReceiptUploadHandler _artifactUploadHandler = new();
    private readonly string _runtimeWorkRoot;
    private readonly HashSet<string> _appContainerProfiles = new(StringComparer.Ordinal);
    private PackageStationOperationExecutor? _executor;
    private ProcessStationRuntimeHost? _runtimeHost;
    private InMemoryStationResourceFenceValidator? _resourceFenceValidator;
    private string? _packageContentSha256;
    private long _nextFencingToken;

    public SignedVendorProgramStationE2ETests()
    {
        _artifactRoot = Path.Combine(_root, "agent-artifacts");
        _cacheRoot = Path.Combine(_root, "package-cache");
        _runtimeWorkRoot = Path.Combine(_root, "station-runtime-work");
    }

    [Fact]
    public async Task SignedFrozenVendorProgramProducesExactAxesEvidenceAndArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await InitializeAsync();

        await VerifyTerminalScenarioAsync(
            "Passed",
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            expectsTypedOutputs: true,
            expectsIncident: false);
        await VerifyTerminalScenarioAsync(
            "Failed",
            ExecutionStatus.Completed,
            ResultJudgement.Failed,
            expectsTypedOutputs: true,
            expectsIncident: false);
        await VerifyTerminalScenarioAsync(
            "Aborted",
            ExecutionStatus.Completed,
            ResultJudgement.Aborted,
            expectsTypedOutputs: true,
            expectsIncident: false);
        await VerifyTerminalScenarioAsync(
            "Crash",
            ExecutionStatus.Failed,
            ResultJudgement.Unknown,
            expectsTypedOutputs: false,
            expectsIncident: true);
        await VerifyTerminalScenarioAsync(
            "InvalidJson",
            ExecutionStatus.Failed,
            ResultJudgement.Unknown,
            expectsTypedOutputs: false,
            expectsIncident: true);
        await VerifyTerminalScenarioAsync(
            "UnknownToken",
            ExecutionStatus.Failed,
            ResultJudgement.Unknown,
            expectsTypedOutputs: false,
            expectsIncident: true);
    }

    [Fact]
    public async Task CancellationTerminatesDelayAndSpawnedVendorProcessTrees()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await InitializeAsync();

        await VerifyCancellationAsync("Delay", expectsChildProcess: false);
        await VerifyCancellationAsync("SpawnChildDelay", expectsChildProcess: true);
    }

    [Fact]
    public async Task SignedFrozenPythonFlowRunsThroughAgentStationRuntimeAndWorker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await InitializePythonAsync();
        var request = CreateRequest("python-e2e");
        var store = new InMemoryStationJobStore();
        var coordinator = CreateCoordinator(store);
        await ApplyLeaseAsync(request);

        var snapshot = await coordinator.HandleAsync(request);

        Assert.True(
            snapshot.ExecutionStatus == ExecutionStatus.Completed,
            $"Python Flow ended as {snapshot.ExecutionStatus}: "
            + $"{snapshot.FailureCode}: {snapshot.FailureReason}");
        Assert.Equal(ResultJudgement.NotApplicable, snapshot.Judgement);
        Assert.Equal(StationJobStatus.Completed, snapshot.Status);
        Assert.Equal(1, snapshot.CommandCount);
        Assert.Equal(1, snapshot.CompletedStepCount);
        using var outputs = JsonDocument.Parse(snapshot.OutputsJson!);
        AssertTypedOutput(outputs.RootElement, "python.input", "Text", "signed-package-input");
        var stagedBundleRoot = OptionalStagedArtifactRoot(
            StagedAgentBundleRootEnvironmentVariable);
        AssertTypedOutput(
            outputs.RootElement,
            "python.isolation",
            "Text",
            stagedBundleRoot is null ? "ExternalProcess" : "LeastPrivilegeIdentity");
        AssertTypedOutput(outputs.RootElement, "python.station", "Text", StationSystemId);
        if (stagedBundleRoot is not null)
        {
            AssertTypedOutput(outputs.RootElement, "python.tokenIsAppContainer", "Boolean", "true");
            var appContainerSid = TypedOutputValue(
                outputs.RootElement,
                "python.appContainerSid");
            Assert.StartsWith("S-1-15-2-", appContainerSid, StringComparison.Ordinal);
            AssertTypedOutput(outputs.RootElement, "python.integrityRid", "WholeNumber", "4096");
            var evidencePath = Environment.GetEnvironmentVariable(
                StagedPythonTokenEvidenceEnvironmentVariable);
            if (evidencePath is not null)
            {
                if (string.IsNullOrWhiteSpace(evidencePath)
                    || char.IsWhiteSpace(evidencePath[0])
                    || char.IsWhiteSpace(evidencePath[^1])
                    || !Path.IsPathFullyQualified(evidencePath))
                {
                    throw new InvalidDataException(
                        $"{StagedPythonTokenEvidenceEnvironmentVariable} must be a canonical absolute file path.");
                }

                var fullEvidencePath = Path.GetFullPath(evidencePath);
                var evidenceDirectory = Path.GetDirectoryName(fullEvidencePath)
                    ?? throw new InvalidDataException(
                        "Staged Python token evidence path has no parent directory.");
                Directory.CreateDirectory(evidenceDirectory);
                var evidence = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    isolationMode = TypedOutputValue(
                        outputs.RootElement,
                        "python.isolation"),
                    tokenIsAppContainer = bool.Parse(TypedOutputValue(
                        outputs.RootElement,
                        "python.tokenIsAppContainer")),
                    appContainerSid,
                    integrityRid = int.Parse(
                        TypedOutputValue(outputs.RootElement, "python.integrityRid"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture)
                });
                await File.WriteAllTextAsync(
                    fullEvidencePath,
                    evidence + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
    }

    [Fact]
    public async Task SignedFrozenPluginRunsThroughAgentStationRuntimeAndBundledHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await InitializePluginAsync();
        var request = CreateRequest("plugin-e2e");
        var store = new InMemoryStationJobStore();
        var coordinator = CreateCoordinator(store);
        await ApplyLeaseAsync(request);

        var snapshot = await coordinator.HandleAsync(request);

        Assert.True(
            snapshot.ExecutionStatus == ExecutionStatus.Completed,
            $"Plugin Flow ended as {snapshot.ExecutionStatus}: "
            + $"{snapshot.FailureCode}: {snapshot.FailureReason}");
        Assert.Equal(ResultJudgement.NotApplicable, snapshot.Judgement);
        Assert.Equal(StationJobStatus.Completed, snapshot.Status);
        var terminalOutbox = await AdvanceOutboxToAsync(
            store,
            request.JobId,
            StationAgentMessageKinds.JobCompleted);
        var completion = JsonSerializer.Deserialize<StationJobCompleted>(
            terminalOutbox.PayloadJson,
            MessageJsonOptions)
            ?? throw new InvalidDataException("Plugin E2E completion message is null.");
        Assert.Equal(request.JobId, completion.JobId);
        Assert.Equal(ExecutionStatus.Completed, completion.ExecutionStatus);
        Assert.Equal(ResultJudgement.NotApplicable, completion.Judgement);
        var command = Assert.Single(completion.Commands);
        Assert.Equal(ExecutionStatus.Completed, command.ExecutionStatus);
        Assert.Equal("Echo", command.CommandName);
        Assert.NotNull(command.ResultPayload);
        using var resultPayload = JsonDocument.Parse(command.ResultPayload);
        Assert.Equal("loopback-device-01", resultPayload.RootElement
            .GetProperty("deviceInstanceId")
            .GetString());
        Assert.Equal("frozen-plugin-payload", resultPayload.RootElement
            .GetProperty("echo")
            .GetString());
    }

    [SupportedOSPlatform("windows")]
    private async ValueTask InitializeAsync()
    {
        if (_executor is not null)
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var scope = CreateApplication();
        var resource = await CreateExternalProgramResourceAsync(scope);
        var flow = CreateFlow();
        var metadata = CreateMetadata(flow, resource);
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.main",
            Now,
            metadata);

        using var signingKey = RSA.Create(3072);
        var privateKeyPath = Path.Combine(_root, "station-signing-key.pem");
        await File.WriteAllTextAsync(privateKeyPath, signingKey.ExportRSAPrivateKeyPem());
        var distribution = Path.Combine(_root, "package-distribution");
        var catalog = Path.Combine(_root, "deployment-catalog");
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distribution,
                catalog,
                "station-release-signing",
                privateKeyPath));
        var packageSet = await publisher.PublishAsync(
            new ProjectReleaseStationPackageRequest(release, metadata, Now));
        var package = Assert.Single(packageSet.Packages);
        Assert.Equal(StationSystemId, package.StationSystemId);
        Assert.EndsWith(".olopkg", package.PackagePath, StringComparison.Ordinal);
        Assert.True(File.Exists(package.PackagePath));
        _packageContentSha256 = package.PackageContentSha256;

        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var hasRestrictedStationIdentity =
            AgentTestStationServiceIdentity.TryReadCurrent(out _);
        var installer = new SignedStationPackageInstaller(new StationPackageTrustOptions(
            _cacheRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["station-release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
            },
            ImmutableReaderSid: contentCapabilitySid,
            ImmutableStationServiceSid:
                AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            new InventoryOnlyTestContentProtector());
        _resourceFenceValidator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        _runtimeHost = new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                StationRuntimeExecutablePath(),
                PluginHostExecutablePath(),
                _runtimeWorkRoot,
                _artifactRoot,
                TimeSpan.FromMinutes(3),
                MaximumStandardOutputBytes: 2 * 1024 * 1024,
                MaximumProcessCount: 64,
                MaximumProcessMemoryBytes: 1024L * 1024 * 1024,
                MaximumJobMemoryBytes: 4L * 1024 * 1024 * 1024,
                MaximumCpuTime: TimeSpan.FromMinutes(3),
                RequireRestrictedExternalProgramHostIdentity: hasRestrictedStationIdentity,
                RestrictedServiceSid: hasRestrictedStationIdentity
                    ? AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()
                    : null,
                RequireExternalProgramAppContainerIsolation: true,
                ExternalProgramAppContainerProfileNamespace: AppContainerProfileNamespace,
                RequireImmutableExternalProgramContent: hasRestrictedStationIdentity,
                PythonScript: PythonScriptOptions()),
            _resourceFenceValidator,
            clock: new FixedClock(Now));
        _executor = new PackageStationOperationExecutor(
            new PackageStationOperationExecutorOptions(distribution),
            installer,
            _runtimeHost);
    }

    [SupportedOSPlatform("windows")]
    private async ValueTask InitializePythonAsync()
    {
        Directory.CreateDirectory(_root);
        var scope = CreateApplication();
        var flow = CreatePythonFlow();
        var metadata = CreatePythonMetadata(flow);
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.main",
            Now,
            metadata);

        using var signingKey = RSA.Create(3072);
        var privateKeyPath = Path.Combine(_root, "python-station-signing-key.pem");
        await File.WriteAllTextAsync(privateKeyPath, signingKey.ExportRSAPrivateKeyPem());
        var distribution = Path.Combine(_root, "package-distribution");
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distribution,
                Path.Combine(_root, "deployment-catalog"),
                "python-station-release-signing",
                privateKeyPath));
        var package = Assert.Single((await publisher.PublishAsync(
            new ProjectReleaseStationPackageRequest(release, metadata, Now))).Packages);
        _packageContentSha256 = package.PackageContentSha256;

        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var hasRestrictedStationIdentity =
            AgentTestStationServiceIdentity.TryReadCurrent(out _);
        var installer = new SignedStationPackageInstaller(new StationPackageTrustOptions(
            _cacheRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["python-station-release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
            },
            ImmutableReaderSid: contentCapabilitySid,
            ImmutableStationServiceSid:
                AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            new InventoryOnlyTestContentProtector());
        _resourceFenceValidator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        _runtimeHost = new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                StationRuntimeExecutablePath(),
                PluginHostExecutablePath(),
                _runtimeWorkRoot,
                _artifactRoot,
                TimeSpan.FromMinutes(1),
                MaximumStandardOutputBytes: 2 * 1024 * 1024,
                MaximumProcessCount: 16,
                MaximumProcessMemoryBytes: 1024L * 1024 * 1024,
                MaximumJobMemoryBytes: 2L * 1024 * 1024 * 1024,
                MaximumCpuTime: TimeSpan.FromMinutes(1),
                RequireRestrictedExternalProgramHostIdentity: hasRestrictedStationIdentity,
                RestrictedServiceSid: hasRestrictedStationIdentity
                    ? AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()
                    : null,
                RequireImmutableExternalProgramContent: hasRestrictedStationIdentity,
                PythonScript: PythonScriptOptions()),
            _resourceFenceValidator,
            clock: new FixedClock(Now));
        _executor = new PackageStationOperationExecutor(
            new PackageStationOperationExecutorOptions(distribution),
            installer,
            _runtimeHost);
    }

    [SupportedOSPlatform("windows")]
    private async ValueTask InitializePluginAsync()
    {
        Directory.CreateDirectory(_root);
        var scope = CreateApplication();
        var packageDependency = CreateSamplePluginPackageDependency();
        var flow = CreatePluginFlow();
        var metadata = CreatePluginMetadata(flow, packageDependency);
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.main",
            Now,
            metadata);

        using var signingKey = RSA.Create(3072);
        var privateKeyPath = Path.Combine(_root, "plugin-station-signing-key.pem");
        await File.WriteAllTextAsync(privateKeyPath, signingKey.ExportRSAPrivateKeyPem());
        var distribution = Path.Combine(_root, "package-distribution");
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distribution,
                Path.Combine(_root, "deployment-catalog"),
                "plugin-station-release-signing",
                privateKeyPath));
        var package = Assert.Single((await publisher.PublishAsync(
            new ProjectReleaseStationPackageRequest(release, metadata, Now))).Packages);
        _packageContentSha256 = package.PackageContentSha256;

        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var hasRestrictedStationIdentity =
            AgentTestStationServiceIdentity.TryReadCurrent(out _);
        var installer = new SignedStationPackageInstaller(new StationPackageTrustOptions(
            _cacheRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plugin-station-release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
            },
            ImmutableReaderSid: contentCapabilitySid,
            ImmutableStationServiceSid:
                AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            new InventoryOnlyTestContentProtector());
        _resourceFenceValidator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        _runtimeHost = new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                StationRuntimeExecutablePath(),
                PluginHostExecutablePath(),
                _runtimeWorkRoot,
                _artifactRoot,
                TimeSpan.FromMinutes(1),
                MaximumStandardOutputBytes: 2 * 1024 * 1024,
                MaximumProcessCount: 16,
                MaximumProcessMemoryBytes: 1024L * 1024 * 1024,
                MaximumJobMemoryBytes: 2L * 1024 * 1024 * 1024,
                MaximumCpuTime: TimeSpan.FromMinutes(1),
                RequireRestrictedExternalProgramHostIdentity: hasRestrictedStationIdentity,
                RestrictedServiceSid: hasRestrictedStationIdentity
                    ? AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()
                    : null,
                RequireImmutableExternalProgramContent: hasRestrictedStationIdentity,
                PythonScript: PythonScriptOptions()),
            _resourceFenceValidator,
            clock: new FixedClock(Now));
        _executor = new PackageStationOperationExecutor(
            new PackageStationOperationExecutorOptions(distribution),
            installer,
            _runtimeHost);
    }

    [SupportedOSPlatform("windows")]
    private async Task VerifyTerminalScenarioAsync(
        string mode,
        ExecutionStatus expectedExecutionStatus,
        ResultJudgement expectedJudgement,
        bool expectsTypedOutputs,
        bool expectsIncident)
    {
        var request = CreateRequest(mode);
        var store = new InMemoryStationJobStore();
        var coordinator = CreateCoordinator(store);
        await ApplyLeaseAsync(request);

        var snapshot = await coordinator.HandleAsync(request);

        Assert.False(WindowsAppContainerIdentity.ProfileExists(
            StationRuntimeIsolationProfile.CreateName(
                AppContainerProfileNamespace,
                request.AgentId,
                request.StationId,
                new StationJobId(request.JobId))));
        Assert.True(
            snapshot.ExecutionStatus == expectedExecutionStatus,
            $"{mode} ended as {snapshot.ExecutionStatus}: {snapshot.FailureCode}: {snapshot.FailureReason}");
        Assert.Equal(expectedJudgement, snapshot.Judgement);
        Assert.Equal(expectedExecutionStatus == ExecutionStatus.Completed
            ? StationJobStatus.Completed
            : StationJobStatus.Failed, snapshot.Status);
        Assert.Equal(expectedExecutionStatus == ExecutionStatus.Completed ? 1 : 0, snapshot.CompletedStepCount);
        Assert.True(
            snapshot.CommandCount == 1,
            $"{mode} produced {snapshot.CommandCount} command records: "
            + $"{snapshot.FailureCode}: {snapshot.FailureReason}");
        Assert.Equal(expectsIncident, snapshot.IncidentCount > 0);
        if (expectedExecutionStatus == ExecutionStatus.Completed)
        {
            Assert.Null(snapshot.FailureCode);
            Assert.Null(snapshot.FailureReason);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.FailureCode));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.FailureReason));
        }

        using (var outputs = JsonDocument.Parse(snapshot.OutputsJson!))
        {
            if (expectsTypedOutputs)
            {
                AssertTypedOutput(outputs.RootElement, "inspection.outcome", "Text", mode);
                AssertTypedOutput(outputs.RootElement, "inspection.voltage", "FixedPoint", "12.5");
                AssertTypedOutput(outputs.RootElement, "inspection.attempt", "WholeNumber", "1");
                AssertTypedOutput(outputs.RootElement, "sandbox.appContainer", "Boolean", "true");
                AssertTypedOutput(outputs.RootElement, "sandbox.internetClient", "Boolean", "false");
            }
            else
            {
                Assert.Empty(outputs.RootElement.EnumerateObject());
            }
        }

        var pendingMessage = await AdvanceOutboxToAsync(
            store,
            request.JobId,
            StationAgentMessageKinds.JobCompletionPendingArtifactTransfer);
        var pending = JsonSerializer.Deserialize<PendingStationJobCompletion>(
            pendingMessage.PayloadJson,
            MessageJsonOptions);
        Assert.NotNull(pending);
        Assert.Equal(request.RuntimeSessionId, pending.Completion.RuntimeSessionId);
        Assert.Equal(expectedExecutionStatus, pending.Completion.ExecutionStatus);
        Assert.Equal(expectedJudgement, pending.Completion.Judgement);
        Assert.Equal(
            pending.Completion.CompletedStepCount,
            pending.Completion.Steps.Count(step => string.Equals(
                step.Status,
                "Completed",
                StringComparison.Ordinal)));
        Assert.Equal(pending.Completion.CommandCount, pending.Completion.Commands.Count);
        Assert.Equal(pending.Completion.IncidentCount, pending.Completion.Incidents.Count);
        Assert.Equal(expectsIncident, pending.Completion.Incidents.Count > 0);
        var command = Assert.Single(pending.Completion.Commands);
        Assert.Equal(OperationId, command.NodeId);
        Assert.Equal($"{OperationId}:action:1", command.ActionId);
        Assert.Equal(CapabilityId, command.CapabilityId);
        Assert.Equal(CommandName, command.CommandName);
        Assert.Equal(expectedJudgement == ResultJudgement.Unknown
            ? ExecutionStatus.Failed
            : ExecutionStatus.Completed, command.ExecutionStatus);
        Assert.Equal(expectedJudgement == ResultJudgement.Unknown
            ? ResultJudgement.Unknown
            : expectedJudgement, command.ResultJudgement);

        var csv = Assert.Single(pending.Artifacts, artifact => artifact.Name == "measurements.csv");
        Assert.Equal("Csv", csv.Kind);
        Assert.Equal("text/csv", csv.MediaType);
        var image = Assert.Single(pending.Artifacts, artifact => artifact.Name == "inspection.png");
        Assert.Equal("Image", image.Kind);
        Assert.Equal("image/png", image.MediaType);
        var report = Assert.Single(pending.Artifacts, artifact => artifact.Name == "report.pdf");
        Assert.Equal("Report", report.Kind);
        Assert.Equal("application/pdf", report.MediaType);
        Assert.Contains(pending.Artifacts, artifact => artifact.Name == "stdout.log");
        Assert.Contains(pending.Artifacts, artifact => artifact.Name == "stderr.log");
        Assert.Contains(pending.Artifacts, artifact => artifact.Name == "vendor-process-id.txt");
        foreach (var artifact in pending.Artifacts)
        {
            Assert.StartsWith($"{request.JobId:N}/", artifact.LocalArtifactKey, StringComparison.Ordinal);
            await VerifyArtifactAsync(_artifactRoot, artifact.LocalArtifactKey, artifact.SizeBytes, artifact.Sha256);
        }

        var messages = new CapturingPublisher();
        var dispatcher = new StationJobOutboxDispatcher(
            store,
            messages,
            new HttpStationArtifactTransfer(
                new HttpStationArtifactTransferOptions(
                    _artifactRoot,
                    new Uri("http://127.0.0.1:51983/"),
                    ArtifactUploadToken,
                    AgentId,
                    StationId,
                    TimeSpan.FromSeconds(30)),
                new HttpClient(_artifactUploadHandler)),
            new FixedClock(Now));
        Assert.True(await dispatcher.DispatchAsync(100) > 0);
        var completion = Assert.Single(messages.Completions);
        Assert.Equal(request.RuntimeSessionId, completion.RuntimeSessionId);
        Assert.Equal(pending.Completion.Steps.ToArray(), completion.Steps.ToArray());
        Assert.Equal(pending.Completion.Commands.ToArray(), completion.Commands.ToArray());
        Assert.Equal(pending.Completion.Incidents.ToArray(), completion.Incidents.ToArray());
        Assert.Equal(pending.Artifacts.Count, completion.Artifacts.Count);
        foreach (var artifact in completion.Artifacts)
        {
            var expectedReceipt = StationArtifactReceiptIdentity.Create(
                AgentId,
                StationId,
                request.JobId,
                artifact.Name,
                artifact.Kind,
                artifact.MediaType,
                artifact.SizeBytes,
                artifact.Sha256);
            Assert.Equal(expectedReceipt.StorageKey, artifact.StorageKey);
            Assert.Equal(expectedReceipt.ReceiptId, artifact.ReceiptId);
            _artifactUploadHandler.Verify(
                artifact.StorageKey,
                artifact.SizeBytes,
                artifact.Sha256);
        }

        Assert.False(Directory.Exists(Path.Combine(_artifactRoot, request.JobId.ToString("N"))));
    }

    [SupportedOSPlatform("windows")]
    private async Task VerifyCancellationAsync(string mode, bool expectsChildProcess)
    {
        var request = CreateRequest(mode);
        var store = new InMemoryStationJobStore();
        var coordinator = CreateCoordinator(store);
        await ApplyLeaseAsync(request);
        var execution = coordinator.HandleAsync(request).AsTask();
        var vendorProcessId = await WaitForProcessEvidenceAsync(
            "vendor-process-id.txt",
            request.JobId,
            TimeSpan.FromSeconds(30),
            execution);
        int? childProcessId = expectsChildProcess
            ? await WaitForProcessEvidenceAsync(
                "child-process-id.txt",
                request.JobId,
                TimeSpan.FromSeconds(30),
                execution)
            : null;

        var cancellation = await coordinator.CancelAsync(new StationJobCancelRequested(
            Guid.NewGuid(),
            $"cancel/{request.JobId:N}",
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.StationSystemId,
            request.ProductionRunId,
            request.OperationRunId,
            "operator.e2e",
            $"Cancel {mode} vendor execution.",
            Now));
        Assert.True(cancellation.Accepted, cancellation.FailureReason);

        var snapshot = await execution.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(StationJobStatus.Canceled, snapshot.Status);
        Assert.Equal(ExecutionStatus.Canceled, snapshot.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, snapshot.Judgement);
        Assert.Equal(request.RuntimeSessionId, snapshot.RuntimeSessionId);
        Assert.False(WindowsAppContainerIdentity.ProfileExists(
            StationRuntimeIsolationProfile.CreateName(
                AppContainerProfileNamespace,
                request.AgentId,
                request.StationId,
                new StationJobId(request.JobId))));
        await AssertProcessExitedAsync(vendorProcessId);
        if (childProcessId is not null)
        {
            await AssertProcessExitedAsync(childProcessId.Value);
        }

        Assert.Empty(Directory.EnumerateDirectories(_runtimeWorkRoot));
        var completedMessage = await AdvanceOutboxToAsync(
            store,
            request.JobId,
            StationAgentMessageKinds.JobCompleted);
        var completion = JsonSerializer.Deserialize<StationJobCompleted>(
            completedMessage.PayloadJson,
            MessageJsonOptions);
        Assert.NotNull(completion);
        Assert.Equal(request.RuntimeSessionId, completion.RuntimeSessionId);
        Assert.Equal(ExecutionStatus.Canceled, completion.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, completion.Judgement);
        Assert.Empty(completion.Artifacts);
        Assert.Empty(completion.Steps);
        Assert.Empty(completion.Commands);
        Assert.Empty(completion.Incidents);
    }

    private StationJobCoordinator CreateCoordinator(InMemoryStationJobStore store) => new(
        store,
        _executor ?? throw new InvalidOperationException("The signed Station executor is not initialized."),
        _resourceFenceValidator
        ?? throw new InvalidOperationException("The Station resource fence validator is not initialized."),
        new EmptyCancellationStore(),
        new StationJobExecutionRegistry(),
        _runtimeHost ?? throw new InvalidOperationException("The Station runtime host is not initialized."),
        new FixedClock(Now));

    private async ValueTask ApplyLeaseAsync(StationJobRequested request)
    {
        var inbox = _resourceFenceValidator
            ?? throw new InvalidOperationException("The Station resource fence validator is not initialized.");
        foreach (var fence in request.ResourceFences)
        {
            await inbox.ApplyAsync(new ResourceLeaseChanged(
                Guid.NewGuid(),
                $"{request.IdempotencyKey}/lease/{fence.ResourceKind}/{fence.ResourceId}/{fence.FencingToken}",
                request.AgentId,
                request.StationId,
                request.StationSystemId,
                request.JobId,
                request.ProductionRunId,
                request.OperationRunId,
                fence.ResourceKind,
                fence.ResourceId,
                fence.FencingToken,
                StationResourceLeaseStatuses.Granted,
                request.RequestedAtUtc,
                fence.ExpiresAtUtc));
        }
    }

    private static async ValueTask<StationJobOutboxMessage> AdvanceOutboxToAsync(
        InMemoryStationJobStore store,
        Guid jobId,
        string terminalKind)
    {
        for (var index = 0; index < 100; index++)
        {
            var message = Assert.Single(await store.ListPendingOutboxAsync(100, Now));
            Assert.Equal(jobId, message.JobId.Value);
            if (string.Equals(message.Kind, terminalKind, StringComparison.Ordinal))
            {
                return message;
            }

            Assert.Contains(
                message.Kind,
                new[]
                {
                    StationAgentMessageKinds.JobAccepted,
                    StationAgentMessageKinds.JobProgressed
                },
                StringComparer.Ordinal);
            await store.AcknowledgeOutboxAsync(message.MessageId, Now);
        }

        throw new InvalidOperationException(
            $"Station job {jobId:D} did not expose terminal outbox message '{terminalKind}'.");
    }

    private StationJobRequested CreateRequest(string mode)
    {
        var jobId = Guid.NewGuid();
        _appContainerProfiles.Add(StationRuntimeIsolationProfile.CreateName(
            AppContainerProfileNamespace,
            AgentId,
            StationId,
            new StationJobId(jobId)));
        return new StationJobRequested(
            Guid.NewGuid(),
            jobId,
            $"production/{mode}/{jobId:N}",
            AgentId,
            StationId,
            StationSystemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"{OperationId}@0001",
            1,
            "product.main",
            "serialNumber",
            mode,
            "lot.e2e",
            "carrier.e2e",
            "project.main",
            ApplicationId,
            "snapshot.main",
            LineDefinitionId,
            TopologyId,
            "operator.e2e",
            _packageContentSha256
            ?? throw new InvalidOperationException("The signed Station package is not initialized."),
            OperationId,
            FlowDefinitionId,
            FlowVersionId,
            "configuration.main",
            "recipe.main@1",
            [new StationResourceFence(
                "Station",
                StationSystemId,
                Interlocked.Increment(ref _nextFencingToken),
                Now.AddHours(1))],
            JsonSerializer.SerializeToElement(new
            {
                mode = new
                {
                    kind = "Text",
                    value = mode
                }
            }),
            Now);
    }

    private ProjectApplicationWorkspaceScope CreateApplication()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            ApplicationId,
            Path.Combine(_root, "project"),
            $"applications/{ApplicationId}/application.oloapp");
        Write(scope, "application.oloapp", "{}");
        Write(
            scope,
            "topology/topology.json",
            """
            {"schemaVersion":"openlineops.automation-topology","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"application.main","topologyId":"topology.main"}
            """);
        Write(
            scope,
            "layouts/layout.main.json",
            """
            {"schemaVersion":"openlineops.site-layout","resourceKind":"OpenLineOps.SiteLayout","applicationId":"application.main","layoutId":"layout.main"}
            """);
        Write(
            scope,
            "production/lines/line.main/line.json",
            """
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"application.main","lineDefinitionId":"line.main"}
            """);
        Write(
            scope,
            $"configuration/projects/{ResourceFileName("project", "engineering.main")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"project","resourceId":"engineering.main","snapshot":{"projectId":"engineering.main","workspaceId":"workspace.main","displayName":"Main","createdAtUtc":"2026-07-11T12:00:00+00:00","activeSnapshotId":"configuration.main","snapshots":[{"snapshotId":"configuration.main","projectId":"engineering.main","processDefinitionId":"process.main","processVersionId":"process.main@1","recipeId":"recipe.main","recipeVersionId":"recipe.main@1","stationProfileId":"station.profile.main","status":"Published","publishedAtUtc":"2026-07-11T12:00:00+00:00","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.main","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}]}}
            """);
        Write(
            scope,
            $"configuration/station-profiles/{ResourceFileName("station-profile", "station.profile.main")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"station-profile","resourceId":"station.profile.main","snapshot":{"stationProfileId":"station.profile.main","stationSystemId":"station.main","displayName":"Main Station","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.main","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}}
            """);
        return scope;
    }

    private static async ValueTask<ExternalProgramResource> CreateExternalProgramResourceAsync(
        ProjectApplicationWorkspaceScope scope)
    {
        var helperDirectory = VendorHelperOutputDirectory();
        var fileNames = new[]
        {
            "OpenLineOps.VendorTestHelper.exe",
            "OpenLineOps.VendorTestHelper.dll",
            "OpenLineOps.VendorTestHelper.deps.json",
            "OpenLineOps.VendorTestHelper.runtimeconfig.json"
        };
        var uploads = new List<ExternalProgramFileUpload>(fileNames.Length);
        foreach (var fileName in fileNames)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(helperDirectory, fileName));
            uploads.Add(new ExternalProgramFileUpload(
                $"files/{fileName}",
                new MemoryStream(bytes, writable: false),
                bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        try
        {
            return await new FileSystemExternalProgramResourceRepository().ImportDirectoryAsync(
                scope,
                new SaveExternalProgramResourceRequest(
                    ResourceId,
                    "Vendor test program",
                    CapabilityId,
                    CommandName,
                    ExternalProgramLaunchKind.ApplicationExecutable,
                    "files/OpenLineOps.VendorTestHelper.exe",
                    ProviderKind: null,
                    ProviderKey: null,
                    [
                        "--mode",
                        "{{input.mode}}",
                        "--delay-milliseconds",
                        "60000"
                    ],
                    [
                        new ExternalProgramInputMapping("$product.identity", "mode"),
                        new ExternalProgramInputMapping("$product.model", "model")
                    ],
                    [
                        new ExternalProgramResultMapping(
                            "$.outcome",
                            "inspection.outcome",
                            ProductionContextValueKind.Text),
                        new ExternalProgramResultMapping(
                            "$.metrics.voltage",
                            "inspection.voltage",
                            ProductionContextValueKind.FixedPoint),
                        new ExternalProgramResultMapping(
                            "$.metrics.attempt",
                            "inspection.attempt",
                            ProductionContextValueKind.WholeNumber),
                        new ExternalProgramResultMapping(
                            "$.isAppContainer",
                            "sandbox.appContainer",
                            ProductionContextValueKind.Boolean),
                        new ExternalProgramResultMapping(
                            "$.internetClientCapability",
                            "sandbox.internetClient",
                            ProductionContextValueKind.Boolean)
                    ],
                    new ExternalProgramOutcomeMapping("$.outcome", "Passed", "Failed", "Aborted"),
                    new ExternalProgramPermissionProfile(
                        "Restricted",
                        NetworkAccessAllowed: false,
                        ["SystemRoot", "WINDIR"]),
                    new ExternalProgramExecutionLimits(
                        VendorTimeoutMilliseconds,
                        MaximumProcessCount: 8,
                        MaximumWorkingSetBytes: 512L * 1024 * 1024,
                        MaximumCpuTimeMilliseconds: VendorTimeoutMilliseconds,
                        MaximumStandardOutputBytes: 2 * 1024 * 1024,
                        MaximumStandardErrorBytes: 2 * 1024 * 1024,
                        MaximumArtifactCount: 32,
                        MaximumArtifactBytes: 16L * 1024 * 1024,
                        MaximumTotalArtifactBytes: 64L * 1024 * 1024)),
                uploads,
                Now);
        }
        finally
        {
            foreach (var upload in uploads)
            {
                await upload.Content.DisposeAsync();
            }
        }
    }

    private ProjectReleasePackageDependencyLock CreateSamplePluginPackageDependency()
    {
        var packagePath = Path.Combine(_root, "sample-plugin-package");
        Directory.CreateDirectory(packagePath);
        var stagedSamplePluginRoot = OptionalStagedArtifactRoot(
            StagedSamplePluginRootEnvironmentVariable);
        File.Copy(
            stagedSamplePluginRoot is null
                ? Path.Combine(
                    RepositoryRoot(),
                    "samples",
                    "plugins",
                    "OpenLineOps.SamplePlugins.LoopbackDevice",
                    "manifest.json")
                : RequiredDirectStagedFile(stagedSamplePluginRoot, "manifest.json"),
            Path.Combine(packagePath, "manifest.json"));
        File.Copy(
            stagedSamplePluginRoot is null
                ? Path.Combine(
                    RepositoryRoot(),
                    "samples",
                    "plugins",
                    "OpenLineOps.SamplePlugins.LoopbackDevice",
                    "bin",
                    BuildConfiguration(),
                    "net10.0",
                    "OpenLineOps.SamplePlugins.LoopbackDevice.dll")
                : RequiredDirectStagedFile(
                    stagedSamplePluginRoot,
                    "OpenLineOps.SamplePlugins.LoopbackDevice.dll"),
            Path.Combine(packagePath, "OpenLineOps.SamplePlugins.LoopbackDevice.dll"));

        var files = Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new ProjectReleasePackageFile(
                    Path.GetRelativePath(packagePath, path).Replace('\\', '/'),
                    bytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var canonical = new StringBuilder();
        foreach (var file in files)
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        var contentSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new ProjectReleasePackageDependencyLock(
            "device.loopback",
            "binding.loopback",
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "openlineops.samples.loopback-device",
            StationSystemId,
            StationSystemId,
            "openlineops.samples.loopback-device",
            "openlineops.samples.loopback-device",
            "0.1.0",
            contentSha256,
            files.Single(file => file.RelativePath == "manifest.json").Sha256,
            files.Single(file => file.RelativePath == "OpenLineOps.SamplePlugins.LoopbackDevice.dll").Sha256,
            "1.0.0",
            "any",
            "openlineops.plugin-abi/1",
            $"packages/{contentSha256}",
            "manifest.json",
            "OpenLineOps.SamplePlugins.LoopbackDevice.dll",
            [new ProjectReleasePackageCommandLock(
                "Device",
                "loopback.echo",
                "device.loopback",
                "Echo")],
            files,
            packagePath);
    }

    private static FlowIrCanonicalArtifact CreateFlow()
    {
        var source = new FlowIrSourceTrace(
            FlowDefinitionId,
            FlowVersionId,
            FlowIrSourceElementKind.ProcessNode,
            OperationId,
            null);
        var action = new FlowIrAction(
            $"{OperationId}:action:1",
            FlowIrActionKind.DeviceCommand,
            "Run vendor program",
            CapabilityId,
            CommandName,
            new FlowIrTargetReference(FlowIrTargetReferenceKind.System, StationSystemId),
            $"{{\"{ExternalProgramResourceContract.ResourceIdProperty}\":\"{ResourceId}\"}}",
            new FlowIrExecutionPolicy(
                VendorTimeoutMilliseconds,
                0,
                FlowIrCancellationMode.Cooperative),
            null,
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            FlowDefinitionId,
            FlowVersionId,
            "Vendor operation",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    OperationId,
                    FlowIrNodeKind.Command,
                    "Run vendor program",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-operation",
                    "start",
                    OperationId,
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-operation",
                        null)),
                new FlowIrTransition(
                    "operation-to-end",
                    OperationId,
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "operation-to-end",
                        null))
            ],
            ImmutableArray<FlowIrBlockDependency>.Empty);
        var result = new FlowIrCanonicalSerializer().Serialize(document);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Message);
    }

    private static FlowIrCanonicalArtifact CreatePythonFlow()
    {
        const string sourceCode = """
            import os
            import ctypes
            from ctypes import wintypes

            TOKEN_QUERY = 0x0008
            TOKEN_INTEGRITY_LEVEL = 25
            TOKEN_IS_APP_CONTAINER = 29
            TOKEN_APP_CONTAINER_SID = 31

            class SID_AND_ATTRIBUTES(ctypes.Structure):
                _fields_ = [('Sid', ctypes.c_void_p), ('Attributes', wintypes.DWORD)]

            class TOKEN_MANDATORY_LABEL(ctypes.Structure):
                _fields_ = [('Label', SID_AND_ATTRIBUTES)]

            kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
            advapi32 = ctypes.WinDLL('advapi32', use_last_error=True)
            kernel32.GetCurrentProcess.argtypes = []
            kernel32.GetCurrentProcess.restype = wintypes.HANDLE
            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel32.CloseHandle.restype = wintypes.BOOL
            kernel32.LocalFree.argtypes = [ctypes.c_void_p]
            kernel32.LocalFree.restype = ctypes.c_void_p
            advapi32.OpenProcessToken.argtypes = [
                wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
            advapi32.OpenProcessToken.restype = wintypes.BOOL
            advapi32.GetTokenInformation.argtypes = [
                wintypes.HANDLE,
                ctypes.c_int,
                ctypes.c_void_p,
                wintypes.DWORD,
                ctypes.POINTER(wintypes.DWORD)]
            advapi32.GetTokenInformation.restype = wintypes.BOOL
            advapi32.GetSidSubAuthorityCount.argtypes = [ctypes.c_void_p]
            advapi32.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
            advapi32.GetSidSubAuthority.argtypes = [ctypes.c_void_p, wintypes.DWORD]
            advapi32.GetSidSubAuthority.restype = ctypes.POINTER(wintypes.DWORD)
            advapi32.ConvertSidToStringSidW.argtypes = [
                ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar_p)]
            advapi32.ConvertSidToStringSidW.restype = wintypes.BOOL

            token = wintypes.HANDLE()
            if not advapi32.OpenProcessToken(
                    kernel32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)):
                raise ctypes.WinError(ctypes.get_last_error())
            try:
                required = wintypes.DWORD()
                advapi32.GetTokenInformation(
                    token, TOKEN_INTEGRITY_LEVEL, None, 0, ctypes.byref(required))
                buffer = ctypes.create_string_buffer(required.value)
                if not advapi32.GetTokenInformation(
                        token, TOKEN_INTEGRITY_LEVEL, buffer, required, ctypes.byref(required)):
                    raise ctypes.WinError(ctypes.get_last_error())
                label = ctypes.cast(
                    buffer, ctypes.POINTER(TOKEN_MANDATORY_LABEL)).contents
                count = advapi32.GetSidSubAuthorityCount(label.Label.Sid).contents.value
                integrity_rid = advapi32.GetSidSubAuthority(
                    label.Label.Sid, count - 1).contents.value

                is_app_container = wintypes.DWORD()
                required = wintypes.DWORD()
                if not advapi32.GetTokenInformation(
                        token,
                        TOKEN_IS_APP_CONTAINER,
                        ctypes.byref(is_app_container),
                        ctypes.sizeof(is_app_container),
                        ctypes.byref(required)):
                    raise ctypes.WinError(ctypes.get_last_error())

                advapi32.GetTokenInformation(
                    token, TOKEN_APP_CONTAINER_SID, None, 0, ctypes.byref(required))
                app_container_buffer = ctypes.create_string_buffer(required.value)
                if not advapi32.GetTokenInformation(
                        token,
                        TOKEN_APP_CONTAINER_SID,
                        app_container_buffer,
                        required,
                        ctypes.byref(required)):
                    raise ctypes.WinError(ctypes.get_last_error())
                app_container_sid_pointer = ctypes.cast(
                    app_container_buffer,
                    ctypes.POINTER(ctypes.c_void_p)).contents.value
                app_container_sid = 'NotApplicable'
                if app_container_sid_pointer:
                    sid_string_pointer = ctypes.c_wchar_p()
                    if not advapi32.ConvertSidToStringSidW(
                            app_container_sid_pointer,
                            ctypes.byref(sid_string_pointer)):
                        raise ctypes.WinError(ctypes.get_last_error())
                    try:
                        app_container_sid = sid_string_pointer.value
                    finally:
                        kernel32.LocalFree(sid_string_pointer)
            finally:
                kernel32.CloseHandle(token)

            result = {
                'python.input': {'kind': 'Text', 'value': input_payload},
                'python.isolation': {
                    'kind': 'Text',
                    'value': os.environ['OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE']
                },
                'python.station': {'kind': 'Text', 'value': station_system_id},
                'python.tokenIsAppContainer': {
                    'kind': 'Boolean', 'value': str(bool(is_app_container.value)).lower()
                },
                'python.appContainerSid': {
                    'kind': 'Text', 'value': app_container_sid
                },
                'python.integrityRid': {
                    'kind': 'WholeNumber', 'value': str(integrity_rid)
                }
            }
            """;
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCode)));
        var source = new FlowIrSourceTrace(
            FlowDefinitionId,
            FlowVersionId,
            FlowIrSourceElementKind.ProcessNode,
            OperationId,
            sourceHash);
        var action = new FlowIrAction(
            $"{OperationId}:action:1",
            FlowIrActionKind.PythonScript,
            "Run Python worker",
            RuntimeScriptCommand.PythonCapability,
            RuntimeScriptCommand.PythonCommandName,
            new FlowIrTargetReference(
                FlowIrTargetReferenceKind.Capability,
                RuntimeScriptCommand.PythonCapability),
            "signed-package-input",
            new FlowIrExecutionPolicy(30_000, 0, FlowIrCancellationMode.Cooperative),
            new FlowIrPythonScript("Python", sourceCode, sourceHash, "1"),
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            FlowDefinitionId,
            FlowVersionId,
            "Python operation",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    OperationId,
                    FlowIrNodeKind.PythonScript,
                    "Run Python worker",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-python",
                    "start",
                    OperationId,
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-python",
                        null)),
                new FlowIrTransition(
                    "python-to-end",
                    OperationId,
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "python-to-end",
                        null))
            ],
            ImmutableArray<FlowIrBlockDependency>.Empty);
        var result = new FlowIrCanonicalSerializer().Serialize(document);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Message);
    }

    private static FlowIrCanonicalArtifact CreatePluginFlow()
    {
        var source = new FlowIrSourceTrace(
            FlowDefinitionId,
            FlowVersionId,
            FlowIrSourceElementKind.ProcessNode,
            OperationId,
            null);
        var action = new FlowIrAction(
            $"{OperationId}:action:1",
            FlowIrActionKind.DeviceCommand,
            "Invoke frozen loopback plugin",
            "device.loopback",
            "Echo",
            new FlowIrTargetReference(FlowIrTargetReferenceKind.System, StationSystemId),
            "frozen-plugin-payload",
            new FlowIrExecutionPolicy(5_000, 0, FlowIrCancellationMode.Cooperative),
            null,
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            FlowDefinitionId,
            FlowVersionId,
            "Plugin operation",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    OperationId,
                    FlowIrNodeKind.Command,
                    "Invoke frozen loopback plugin",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-plugin",
                    "start",
                    OperationId,
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-plugin",
                        null)),
                new FlowIrTransition(
                    "plugin-to-end",
                    OperationId,
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "plugin-to-end",
                        null))
            ],
            ImmutableArray<FlowIrBlockDependency>.Empty);
        var result = new FlowIrCanonicalSerializer().Serialize(document);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Message);
    }

    private static ProjectReleaseSourceMetadata CreatePluginMetadata(
        FlowIrCanonicalArtifact flow,
        ProjectReleasePackageDependencyLock packageDependency) => new(
        TopologyId,
        ["layout.main"],
        new ProjectReleaseProductionLine(
            LineDefinitionId,
            "Main Line",
            TopologyId,
            new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
            OperationId,
            [new ProjectReleaseOperation(
                OperationId,
                "Plugin Operation",
                StationSystemId,
                FlowDefinitionId,
                "configuration.main",
                FlowVersionId,
                flow.SchemaVersion,
                flow.Sha256,
                flow.CanonicalJson,
                [],
                [new ProjectReleaseOperationResource(
                    "resource.station-main",
                    "Station",
                    StationSystemId,
                    "Fixed",
                    [])],
                [],
                [new ProjectReleaseAuthorizedAction(
                    $"{OperationId}:action:1",
                    OperationId,
                    "DeviceCommand",
                    "device.loopback",
                    "Echo",
                    "System",
                    StationSystemId,
                    5_000,
                    null)])],
            CompletedTerminalRoute(),
            []),
        [],
        [new ProjectReleaseCapabilityBinding(
            "device.loopback",
            "binding.loopback",
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "openlineops.samples.loopback-device",
            StationSystemId,
            StationSystemId)],
        [new ProjectReleaseTargetReference("System", StationSystemId)],
        [],
        [packageDependency]);

    private static ProjectReleaseSourceMetadata CreatePythonMetadata(
        FlowIrCanonicalArtifact flow) => new(
        TopologyId,
        ["layout.main"],
        new ProjectReleaseProductionLine(
            LineDefinitionId,
            "Main Line",
            TopologyId,
            new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
            OperationId,
            [new ProjectReleaseOperation(
                OperationId,
                "Python Operation",
                StationSystemId,
                FlowDefinitionId,
                "configuration.main",
                FlowVersionId,
                flow.SchemaVersion,
                flow.Sha256,
                flow.CanonicalJson,
                [],
                [new ProjectReleaseOperationResource(
                    "resource.station-main",
                    "Station",
                    StationSystemId,
                    "Fixed",
                    [])],
                [],
                [new ProjectReleaseAuthorizedAction(
                    $"{OperationId}:action:1",
                    OperationId,
                    "PythonScript",
                    RuntimeScriptCommand.PythonCapability,
                    RuntimeScriptCommand.PythonCommandName,
                    "Capability",
                    RuntimeScriptCommand.PythonCapability,
                    30_000,
                    null)])],
            CompletedTerminalRoute(),
            []),
        [],
        [],
        [
            new ProjectReleaseTargetReference("System", StationSystemId),
            new ProjectReleaseTargetReference(
                "Capability",
                RuntimeScriptCommand.PythonCapability)
        ],
        [],
        []);

    private static ProjectReleaseSourceMetadata CreateMetadata(
        FlowIrCanonicalArtifact flow,
        ExternalProgramResource resource)
    {
        var resourcePath = $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}";
        var frozenResource = new ProjectReleaseExternalProgramResource(
            resource.ResourceId,
            resource.DisplayName,
            resource.CapabilityId,
            resource.CommandName,
            resource.LaunchKind.ToString(),
            resource.EntryPoint,
            resource.ProviderKind,
            resource.ProviderKey,
            resource.ArgumentTemplates,
            resource.InputMappings.Select(mapping => new ProjectReleaseExternalProgramInputMapping(
                mapping.Source,
                mapping.Target)).ToArray(),
            resource.ResultMappings.Select(mapping => new ProjectReleaseExternalProgramResultMapping(
                mapping.SourcePath,
                mapping.TargetKey,
                mapping.ValueKind.ToString())).ToArray(),
            new ProjectReleaseExternalProgramOutcomeMapping(
                resource.OutcomeMapping.SourcePath,
                resource.OutcomeMapping.PassedToken,
                resource.OutcomeMapping.FailedToken,
                resource.OutcomeMapping.AbortedToken),
            new ProjectReleaseExternalProgramPermissionProfile(
                resource.PermissionProfile.ProfileName,
                resource.PermissionProfile.NetworkAccessAllowed,
                resource.PermissionProfile.AllowedEnvironmentVariables),
            new ProjectReleaseExternalProgramExecutionLimits(
                resource.ExecutionLimits.TimeoutMilliseconds,
                resource.ExecutionLimits.MaximumProcessCount,
                resource.ExecutionLimits.MaximumWorkingSetBytes,
                resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                resource.ExecutionLimits.MaximumStandardOutputBytes,
                resource.ExecutionLimits.MaximumStandardErrorBytes,
                resource.ExecutionLimits.MaximumArtifactCount,
                resource.ExecutionLimits.MaximumArtifactBytes,
                resource.ExecutionLimits.MaximumTotalArtifactBytes),
            resource.Files.Select(file => new ProjectReleaseExternalProgramFile(
                $"{resourcePath}/{file.RelativePath}",
                file.SizeBytes,
                file.Sha256)).ToArray(),
            resource.ContentSha256,
            resourcePath);
        return new ProjectReleaseSourceMetadata(
            TopologyId,
            ["layout.main"],
            new ProjectReleaseProductionLine(
                LineDefinitionId,
                "Main Line",
                TopologyId,
                new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
                OperationId,
                [new ProjectReleaseOperation(
                    OperationId,
                    "Vendor Operation",
                    StationSystemId,
                    FlowDefinitionId,
                    "configuration.main",
                    FlowVersionId,
                    flow.SchemaVersion,
                    flow.Sha256,
                    flow.CanonicalJson,
                    [],
                    [new ProjectReleaseOperationResource(
                        "resource.station-main",
                        "Station",
                        StationSystemId,
                        "Fixed",
                        [])],
                    [],
                    [new ProjectReleaseAuthorizedAction(
                        $"{OperationId}:action:1",
                        OperationId,
                        "DeviceCommand",
                        CapabilityId,
                        CommandName,
                        "System",
                        StationSystemId,
                        VendorTimeoutMilliseconds,
                        null)])],
                CompletedTerminalRoute(),
                []),
            [frozenResource],
            [new ProjectReleaseCapabilityBinding(
                CapabilityId,
                "binding.vendor-program",
                 ProjectReleaseRuntimeProviderKinds.ExternalSystem,
                 resource.ResourceId,
                 StationSystemId,
                 StationSystemId)],
            [new ProjectReleaseTargetReference("System", StationSystemId)],
            [],
            []);
    }

    private static ProjectReleaseRouteTransition[] CompletedTerminalRoute() =>
    [
        new ProjectReleaseRouteTransition(
            "operation-main-to-completed",
            OperationId,
            null,
            "Completed",
            "Sequence",
            null,
            null,
            null,
            null,
            null,
            null)
    ];

    private async Task<int> WaitForProcessEvidenceAsync(
        string fileName,
        Guid jobId,
        TimeSpan timeout,
        Task<StationJobSnapshot> execution)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (execution.IsCompleted)
            {
                var snapshot = await execution;
                throw new InvalidOperationException(
                    $"Station Job {jobId:D} ended before '{fileName}' was created: "
                    + $"{snapshot.ExecutionStatus}: {snapshot.FailureCode}: {snapshot.FailureReason}");
            }

            var jobPrefix = jobId.ToString("N");
            var path = Directory.Exists(_runtimeWorkRoot)
                ? Directory.EnumerateFiles(_runtimeWorkRoot, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(candidate => candidate.Contains(jobPrefix, StringComparison.OrdinalIgnoreCase))
                : null;
            if (path is not null)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (int.TryParse(text.Trim(), out var processId) && processId > 0)
                    {
                        return processId;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Vendor process evidence '{fileName}' was not created for Station Job {jobId:D}.");
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!IsProcessRunning(processId))
            {
                return;
            }

            await Task.Delay(50);
        }

        try
        {
            using var leaked = Process.GetProcessById(processId);
            leaked.Kill(entireProcessTree: true);
            await leaked.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            return;
        }

        Assert.Fail($"Vendor process {processId} survived Station Job cancellation.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async ValueTask VerifyArtifactAsync(
        string root,
        string relativePath,
        long expectedSize,
        string expectedSha256)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        Assert.True(info.Exists, $"Artifact '{path}' does not exist.");
        Assert.Equal(expectedSize, info.Length);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Assert.Equal(
            expectedSha256,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)));
    }

    private static void AssertTypedOutput(
        JsonElement outputs,
        string key,
        string expectedKind,
        string expectedValue)
    {
        var output = outputs.GetProperty(key);
        Assert.Equal(expectedKind, output.GetProperty("kind").GetString());
        Assert.Equal(expectedValue, output.GetProperty("value").GetString());
    }

    private static string TypedOutputValue(JsonElement outputs, string outputName) =>
        outputs.GetProperty(outputName).GetProperty("value").GetString()
        ?? throw new InvalidDataException($"Typed output '{outputName}' has no value.");

    private static string StationRuntimeExecutablePath() => StagedAgentExecutablePath(
        "OpenLineOps.StationRuntime.exe",
        Path.Combine(
            RepositoryRoot(),
            "src",
            "OpenLineOps.StationRuntime",
            "bin",
            BuildConfiguration(),
            "net10.0",
            "OpenLineOps.StationRuntime.exe"));

    private static string PluginHostExecutablePath() => StagedAgentExecutablePath(
        "OpenLineOps.PluginHost.exe",
        Path.Combine(
            RepositoryRoot(),
            "src",
            "OpenLineOps.PluginHost",
            "bin",
            BuildConfiguration(),
            "net10.0",
            "OpenLineOps.PluginHost.exe"));

    private static StationRuntimePythonScriptOptions PythonScriptOptions()
    {
        var stagedBundleRoot = OptionalStagedArtifactRoot(
            StagedAgentBundleRootEnvironmentVariable);
        var workerPath = StagedAgentExecutablePath(
            "OpenLineOps.ScriptWorker.exe",
            Path.Combine(
                RepositoryRoot(),
                "src",
                "OpenLineOps.ScriptWorker",
                "bin",
                BuildConfiguration(),
                "net10.0",
                "OpenLineOps.ScriptWorker.exe"));
        return stagedBundleRoot is null
            ? new StationRuntimePythonScriptOptions(
                workerPath,
                RequiredHostPythonRuntimeDllPath(),
                new StationRuntimePythonScriptSandboxOptions(
                    RequireLeastPrivilegeExecution: false,
                    IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess))
            : new StationRuntimePythonScriptOptions(
                workerPath,
                RequiredHostPythonRuntimeDllPath(),
                new StationRuntimePythonScriptSandboxOptions(
                    RequireLeastPrivilegeExecution: true,
                    IsolationMode:
                        StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
                    LeastPrivilegeIdentity: "PerExecutionAppContainer",
                    LeastPrivilegeLauncherExecutable: RequiredDirectStagedFile(
                        stagedBundleRoot,
                        "OpenLineOps.LeastPrivilegeLauncher.exe"),
                    LeastPrivilegeNoInteractivePrompt: true));
    }

    private static string RequiredHostPythonRuntimeDllPath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                StagedAgentBundleRootEnvironmentVariable)))
        {
            return AppContainerPythonRuntimeTestSupport.ResolveRuntimeDll();
        }

        var path = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-c \"import pathlib,sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR')).joinpath(sysconfig.get_config_var('LDLIBRARY')).resolve())\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Python runtime discovery process did not start.");
        if (!process.WaitForExit(2_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Signed Station E2E could not discover the Python runtime DLL.");
        }

        path = process.StandardOutput.ReadLine();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new InvalidOperationException(
                "Signed Station E2E requires an installed Python runtime DLL.");
    }

    private static string VendorHelperOutputDirectory() => Path.Combine(
        RepositoryRoot(),
        "tools",
        "OpenLineOps.VendorTestHelper",
        "bin",
        BuildConfiguration(),
        "net10.0");

    private static string BuildConfiguration() => AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase)
        ? "Release"
            : "Debug";

    private static string StagedAgentExecutablePath(
        string fileName,
        string buildOutputFallback)
    {
        var stagedRoot = OptionalStagedArtifactRoot(
            StagedAgentBundleRootEnvironmentVariable);
        return stagedRoot is null
            ? buildOutputFallback
            : RequiredDirectStagedFile(stagedRoot, fileName);
    }

    private static string? OptionalStagedArtifactRoot(string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (configured is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configured)
            || char.IsWhiteSpace(configured[0])
            || char.IsWhiteSpace(configured[^1])
            || !Path.IsPathFullyQualified(configured))
        {
            throw new InvalidDataException(
                $"{environmentVariable} must be a canonical absolute directory path.");
        }

        var fullPath = Path.GetFullPath(configured);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException(
                $"Staged artifact root does not exist: {fullPath}");
    }

    private static string RequiredDirectStagedFile(string stagedRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A staged E2E file must be one canonical direct-child file name.");
        }

        var path = Path.GetFullPath(fileName, stagedRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), stagedRoot, comparison))
        {
            throw new InvalidDataException(
                $"Staged E2E file '{fileName}' resolves outside '{stagedRoot}'.");
        }

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Required staged E2E file does not exist: {path}",
                path);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("OpenLineOps repository root could not be found.");
    }

    private static void Write(
        ProjectApplicationWorkspaceScope scope,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            scope.ApplicationRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ResourceFileName(string kind, string resourceId)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(resourceId)))[..12];
        return $"{kind}-{resourceId}--{hash}.json";
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var profileName in _appContainerProfiles)
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            }
        }

        if (!Directory.Exists(_root))
        {
            return;
        }

        if (Directory.Exists(_cacheRoot))
        {
            var policy = new ImmutableContentProtectionPolicy(
                WindowsAppContainerIdentity.EnsureCapabilitySid(
                    WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                AgentTestStationServiceIdentity.ConfiguredOrFixtureSid());
            AgentTestStationPackageCache.RemovePackageInstallations(
                _cacheRoot,
                new InventoryOnlyTestContentProtector(),
                policy);

            Directory.Delete(_cacheRoot);
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(_root, File.GetAttributes(_root) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class EmptyCancellationStore : IStationSafetyInboxStore
    {
        public ValueTask<StationSafetyInboxEntry?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
            StationJobId jobId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<bool> TryBeginAsync(
            StationSafetyInboxEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationSafetyInboxEntry> CompleteAsync(
            string idempotencyKey,
            StationSafetyCommandKind commandKind,
            string requestSha256,
            string acknowledgementJson,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReceiptUploadHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

        public void Verify(string storageKey, long sizeBytes, string sha256)
        {
            var content = _content[storageKey];
            Assert.Equal(sizeBytes, content.LongLength);
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(content)));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(ArtifactUploadToken, request.Headers.Authorization?.Parameter);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var agentId = Header(request, StationArtifactUploadProtocol.AgentIdHeader);
            var stationId = Header(request, StationArtifactUploadProtocol.StationIdHeader);
            var jobId = Guid.ParseExact(
                Header(request, StationArtifactUploadProtocol.JobIdHeader),
                "D");
            var name = StationArtifactUploadProtocol.DecodeArtifactName(
                Header(request, StationArtifactUploadProtocol.ArtifactNameHeader));
            var sizeBytes = long.Parse(
                Header(request, StationArtifactUploadProtocol.ArtifactSizeHeader),
                CultureInfo.InvariantCulture);
            var sha256 = Header(request, StationArtifactUploadProtocol.ArtifactSha256Header);
            var kind = StationArtifactUploadProtocol.DecodeArtifactKind(
                Header(request, StationArtifactUploadProtocol.ArtifactKindHeader));
            var mediaType = request.Headers.TryGetValues(
                    StationArtifactUploadProtocol.ArtifactMediaTypeHeader,
                    out var mediaTypeValues)
                ? StationArtifactUploadProtocol.DecodeMediaType(Assert.Single(mediaTypeValues))
                : null;
            Assert.Equal(sizeBytes, bytes.LongLength);
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var receipt = StationArtifactReceiptIdentity.Create(
                agentId,
                stationId,
                jobId,
                name,
                kind,
                mediaType,
                sizeBytes,
                sha256);
            if (_content.TryGetValue(receipt.StorageKey, out var existing))
            {
                Assert.Equal(existing, bytes);
            }
            else
            {
                _content.Add(receipt.StorageKey, bytes);
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(receipt, MessageJsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static string Header(HttpRequestMessage request, string name) =>
            Assert.Single(request.Headers.GetValues(name));
    }

    private sealed class CapturingPublisher : IStationAgentMessagePublisher
    {
        public List<StationJobCompleted> Completions { get; } = [];

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(kind, StationAgentMessageKinds.JobCompleted, StringComparison.Ordinal))
            {
                Completions.Add(JsonSerializer.Deserialize<StationJobCompleted>(
                    payloadJson,
                    MessageJsonOptions)
                    ?? throw new InvalidDataException("Published Station completion is null."));
            }

            return ValueTask.CompletedTask;
        }
    }
}
