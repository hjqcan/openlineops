using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
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
    private readonly string _exchangeRoot;
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
        _exchangeRoot = Path.Combine(_root, "artifact-exchange");
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

        using var currentIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentSid = currentIdentity.User?.Value
            ?? throw new InvalidOperationException("The Windows test identity has no SID.");
        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var installer = new SignedStationPackageInstaller(new StationPackageTrustOptions(
            _cacheRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["station-release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
            },
            ImmutableReaderSid: contentCapabilitySid,
            ImmutableHostReaderSid: currentSid));
        _resourceFenceValidator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        _runtimeHost = new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                StationRuntimeExecutablePath(),
                _runtimeWorkRoot,
                _artifactRoot,
                TimeSpan.FromMinutes(3),
                MaximumStandardOutputBytes: 2 * 1024 * 1024,
                MaximumProcessCount: 64,
                MaximumProcessMemoryBytes: 1024L * 1024 * 1024,
                MaximumJobMemoryBytes: 4L * 1024 * 1024 * 1024,
                MaximumCpuTime: TimeSpan.FromMinutes(3),
                RequireRestrictedExternalProgramHostIdentity: false,
                RequireExternalProgramAppContainerIsolation: true,
                ExternalProgramAppContainerProfileNamespace: AppContainerProfileNamespace,
                RequireImmutableExternalProgramContent: true),
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

        var pendingOutbox = await store.ListPendingOutboxAsync(100, Now);
        var pendingMessage = Assert.Single(pendingOutbox, message => string.Equals(
            message.Kind,
            StationAgentMessageKinds.JobCompletionPendingArtifactTransfer,
            StringComparison.Ordinal));
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
            ? "Failed"
            : "Completed", command.Status);
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
            new FileSystemStationArtifactTransfer(
                new FileSystemStationArtifactTransferOptions(_artifactRoot, _exchangeRoot)),
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
            Assert.Equal($"sha256/{artifact.Sha256[..2]}/{artifact.Sha256}", artifact.StorageKey);
            await VerifyArtifactAsync(
                _exchangeRoot,
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
        var pending = await store.ListPendingOutboxAsync(100, Now);
        var completedMessage = Assert.Single(pending, message => string.Equals(
            message.Kind,
            StationAgentMessageKinds.JobCompleted,
            StringComparison.Ordinal));
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
            JsonSerializer.SerializeToElement(new { mode }),
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
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"project","resourceId":"engineering.main","snapshot":{"projectId":"engineering.main","workspaceId":"workspace.main","displayName":"Main","createdAtUtc":"2026-07-11T12:00:00+00:00","activeSnapshotId":"configuration.main","snapshots":[{"snapshotId":"configuration.main","projectId":"engineering.main","processDefinitionId":"process.main","processVersionId":"process.main@1","recipeId":"recipe.main","recipeVersionId":"recipe.main@1","stationProfileId":"station.profile.main","status":"Published","publishedAtUtc":"2026-07-11T12:00:00+00:00","deviceBindings":[]}]}}
            """);
        Write(
            scope,
            $"configuration/station-profiles/{ResourceFileName("station-profile", "station.profile.main")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"station-profile","resourceId":"station.profile.main","snapshot":{"stationProfileId":"station.profile.main","stationSystemId":"station.main","displayName":"Main Station","deviceBindings":[]}}
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
            return await new FileSystemExternalProgramResourceRepository().SaveAsync(
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
                [],
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

    private static string StationRuntimeExecutablePath() => Path.Combine(
        RepositoryRoot(),
        "src",
        "OpenLineOps.StationRuntime",
        "bin",
        BuildConfiguration(),
        "net10.0",
        "OpenLineOps.StationRuntime.exe");

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
            var protector = new ImmutableContentProtector();
            foreach (var contentDirectory in Directory.EnumerateDirectories(_cacheRoot).ToArray())
            {
                var leaf = Path.GetFileName(contentDirectory);
                if (leaf.Length == 64
                    && leaf.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    protector.DeleteProtectedInstallation(_cacheRoot, contentDirectory);
                }
            }
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
