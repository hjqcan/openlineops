using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.ContentProtection;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.StationRuntime.Tests;

[SupportedOSPlatform("windows")]
public sealed class StationRuntimeExecutionTests : IDisposable
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-station-runtime-{Guid.NewGuid():N}");

    [Fact]
    public async Task FrozenOperationExecutesAndReturnsTypedEvidence()
    {
        var scope = CreateApplication();
        var flow = CreateFlow();
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.main",
            PublishedAtUtc,
            Metadata(flow));
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var requestPath = Path.Combine(work, "request.json");
        var resultPath = Path.Combine(work, "result.json");
        using var inputs = JsonDocument.Parse("{}");
        var fenceAuthority = new StationResourceFenceAuthorityDescriptor(
            $"openlineops-test-{Guid.NewGuid():N}",
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            CurrentUserSid());
        using var fenceAuthorityCancellation = new CancellationTokenSource();
        var fenceAuthorityTask = RunFenceAuthorityAsync(
            fenceAuthority,
            fenceAuthorityCancellation.Token);
        var request = new StationOperationRequestDocument(
            StationOperationDocumentContract.RequestSchema,
            Guid.NewGuid(),
            "run/operation.main/1",
            "agent.station-main",
            "station.main",
            "station.main",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "line.main",
            "topology.main",
            "station-runtime-test",
            "operation.main@0001",
            1,
            "product.main",
            "serialNumber",
            "UNIT-001",
            null,
            null,
            scope.ProjectId,
            scope.ApplicationId,
            release.SnapshotId,
            new string('a', 64),
            release.ReleaseRootPath,
            "operation.main",
            "process.main",
            "process.main@1",
            "configuration.main",
            "recipe.main@1",
            fenceAuthority,
            [new StationOperationResourceFence(
                "Station",
                "station.main",
                1,
                PublishedAtUtc.AddHours(1))],
            inputs.RootElement.Clone(),
            PublishedAtUtc);
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, StationOperationDocumentJson.CreateOptions()));

        int exitCode;
        try
        {
            exitCode = await StationRuntimeEntrypoint.RunAsync(
                ["execute-operation", "--request-file", requestPath, "--result-file", resultPath],
                TestHostOptions());
        }
        finally
        {
            await fenceAuthorityCancellation.CancelAsync();
            await fenceAuthorityTask;
        }

        Assert.Equal(0, exitCode);
        var result = JsonSerializer.Deserialize<StationOperationResultDocument>(
            await File.ReadAllTextAsync(resultPath),
            StationOperationDocumentJson.CreateOptions());
        Assert.NotNull(result);
        StationOperationDocumentJson.Validate(result);
        Assert.True(
            result.ExecutionStatus == ExecutionStatus.Completed,
            $"Station Runtime failed: {result.FailureCode}: {result.FailureReason}");
        Assert.Equal(ResultJudgement.NotApplicable, result.Judgement);
        Assert.Equal(1, result.CompletedStepCount);
        Assert.Equal(1, result.CommandCount);
        Assert.Empty(result.Incidents);
        Assert.Empty(result.Artifacts);
        Assert.Equal(
            "Text",
            result.Outputs.GetProperty("inspection.result").GetProperty("kind").GetString());
        Assert.Equal(
            "passed",
            result.Outputs.GetProperty("inspection.result").GetProperty("value").GetString());
        Assert.Equal(ExecutionStatus.Completed, Assert.Single(result.Commands).ExecutionStatus);
    }

    [Fact]
    public async Task MissingFenceAuthorityRejectsWithinItsIndependentDeadline()
    {
        var productionRunId = Guid.NewGuid();
        const string operationRunId = "operation.main@0001";
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        using var inputs = JsonDocument.Parse("{}");
        var request = new StationOperationRequestDocument(
            StationOperationDocumentContract.RequestSchema,
            Guid.NewGuid(),
            "run/operation.main/1",
            "agent.station-main",
            "station.main",
            "station.main",
            productionRunId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "line.main",
            "topology.main",
            "station-runtime-test",
            operationRunId,
            1,
            "product.main",
            "serialNumber",
            "UNIT-001",
            null,
            null,
            "project.main",
            "application.main",
            "snapshot.main",
            new string('a', 64),
            Path.GetFullPath(_root),
            "operation.main",
            "process.main",
            "process.main@1",
            "configuration.main",
            "recipe.main@1",
            new StationResourceFenceAuthorityDescriptor(
                $"openlineops-missing-{Guid.NewGuid():N}",
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                CurrentUserSid()),
            [new StationOperationResourceFence(
                "Station",
                "station.main",
                1,
                expiresAtUtc)],
            inputs.RootElement.Clone(),
            DateTimeOffset.UtcNow);
        var repository = new AgentResourceLeaseFenceRepository(request);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var stopwatch = Stopwatch.StartNew();

        var result = await repository.ValidateCurrentAsync(
            new ProductionRunId(productionRunId),
            operationRunId,
            [new ResourceLeaseFenceEvidence(
                new ResourceRequirement(ResourceKind.Station, "station.main"),
                1,
                expiresAtUtc)],
            testDeadline.Token);

        Assert.False(result.Accepted);
        Assert.Contains(
            "did not complete within",
            result.RejectionReason,
            StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Fence authority rejection took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ScatteredOptionsAreRejectedWithoutWritingResult()
    {
        var resultPath = Path.Combine(_root, "scattered-options-result.json");
        var exitCode = await StationRuntimeEntrypoint.RunAsync(
            ["execute-operation", "--package", _root, "--result-file", resultPath],
            TestHostOptions());

        Assert.Equal(64, exitCode);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task UsageErrorDoesNotRequirePluginHostConfiguration()
    {
        var exitCode = await StationRuntimeEntrypoint.RunAsync(
            [],
            new StationRuntimeHostOptions(string.Empty));

        Assert.Equal(64, exitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ProjectApplicationWorkspaceScope CreateApplication()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            Path.Combine(_root, "project"),
            "applications/application.main/application.oloapp");
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
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"project","resourceId":"engineering.main","snapshot":{"projectId":"engineering.main","workspaceId":"workspace.main","displayName":"Main","createdAtUtc":"2026-07-11T08:00:00+00:00","activeSnapshotId":"configuration.main","snapshots":[{"snapshotId":"configuration.main","projectId":"engineering.main","processDefinitionId":"process.main","processVersionId":"process.main@1","recipeId":"recipe.main","recipeVersionId":"recipe.main@1","stationProfileId":"station.profile.main","status":"Published","publishedAtUtc":"2026-07-11T08:00:00+00:00","deviceBindings":[]}]}}
            """);
        Write(
            scope,
            $"configuration/station-profiles/{ResourceFileName("station-profile", "station.profile.main")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.main","resourceKind":"station-profile","resourceId":"station.profile.main","snapshot":{"stationProfileId":"station.profile.main","stationSystemId":"station.main","displayName":"Main Station","deviceBindings":[]}}
            """);
        return scope;
    }

    private static FlowIrCanonicalArtifact CreateFlow()
    {
        var source = new FlowIrSourceTrace(
            "process.main",
            "process.main@1",
            FlowIrSourceElementKind.ProcessNode,
            "operation.main",
            null);
        var action = new FlowIrAction(
            "operation.main:action:1",
            FlowIrActionKind.DeviceCommand,
            "Record result",
            "runtime.flow",
            "ResultPatch",
            new FlowIrTargetReference(FlowIrTargetReferenceKind.Capability, "runtime.flow"),
            """
            {"assignments":[{"key":"inspection.result","value":{"kind":"Text","value":"passed"}}]}
            """,
            new FlowIrExecutionPolicy(30_000, 0, FlowIrCancellationMode.Cooperative),
            null,
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            "process.main",
            "process.main@1",
            "Main Flow",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        "process.main",
                        "process.main@1",
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    "operation.main",
                    FlowIrNodeKind.Command,
                    "Record result",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        "process.main",
                        "process.main@1",
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-operation",
                    "start",
                    "operation.main",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        "process.main",
                        "process.main@1",
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-operation",
                        null)),
                new FlowIrTransition(
                    "operation-to-end",
                    "operation.main",
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        "process.main",
                        "process.main@1",
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

    private static ProjectReleaseSourceMetadata Metadata(FlowIrCanonicalArtifact flow) => new(
        "topology.main",
        ["layout.main"],
        new ProjectReleaseProductionLine(
            "line.main",
            "Main Line",
            "topology.main",
            new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
            "operation.main",
            [new ProjectReleaseOperation(
                "operation.main",
                "Main Operation",
                "station.main",
                "process.main",
                "configuration.main",
                "process.main@1",
                flow.SchemaVersion,
                flow.Sha256,
                flow.CanonicalJson,
                [],
                [new ProjectReleaseOperationResource(
                    "resource.station",
                    "Station",
                    "station.main",
                    "Fixed",
                    [])],
                [],
                [new ProjectReleaseAuthorizedAction(
                    "operation.main:action:1",
                    "operation.main",
                    "DeviceCommand",
                    "runtime.flow",
                    "ResultPatch",
                    "Capability",
                    "runtime.flow",
                    30_000,
                    null)])],
            [new ProjectReleaseRouteTransition(
                "transition.complete",
                "operation.main",
                null,
                "Completed",
                "Sequence",
                null,
                null,
                null,
                null,
                null,
                null)],
            []),
        [],
        [new ProjectReleaseCapabilityBinding(
            "runtime.flow",
            "binding.runtime-flow",
            "ProcessCommandProvider",
            "runtime.flow",
            "station.main",
            "station.main")],
        [
            new ProjectReleaseTargetReference("Capability", "runtime.flow"),
            new ProjectReleaseTargetReference("System", "station.main")
        ],
        [],
        []);

    private static async Task RunFenceAuthorityAsync(
        StationResourceFenceAuthorityDescriptor authority,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = WindowsIdentityBoundNamedPipe.CreateServer(
                authority.PipeName,
                authority.AuthorizedPrincipalSid,
                maximumServerInstances: 1,
                inputBufferSize: 1024 * 1024 + sizeof(int),
                outputBufferSize: 1024 * 1024 + sizeof(int));
            while (true)
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                try
                {
                    var request = await StationResourceFenceAuthorityWire
                        .ReadAsync<StationResourceFenceValidationRequest>(pipe, cancellationToken);
                    StationOperationDocumentJson.Validate(request);
                    var response = new StationResourceFenceValidationResponse(
                        StationOperationDocumentContract.ResourceFenceValidationResponseSchema,
                        request.AccessToken == authority.AccessToken,
                        request.AccessToken == authority.AccessToken
                            ? null
                            : "Fence authority access token does not match.");
                    await StationResourceFenceAuthorityWire.WriteAsync(
                        pipe,
                        response,
                        cancellationToken);
                    await StationResourceFenceAuthorityWire.ReadResponseReceiptAsync(
                        pipe,
                        cancellationToken);
                }
                finally
                {
                    if (pipe.IsConnected)
                    {
                        pipe.Disconnect();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value
        ?? throw new InvalidOperationException("Current test token has no user SID.");

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

    private static StationRuntimeHostOptions TestHostOptions()
    {
        return new StationRuntimeHostOptions(
            Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process executable path is unavailable."));
    }
}
