using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordTests
{
    [Fact]
    public void CreateFreezesProductionUnitScopeAndNestedOperationEvidence()
    {
        var trace = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000001",
            "SMX-0001",
            TraceTestData.BaseTimeUtc.AddMinutes(2));

        Assert.Equal(trace.ProductionRunId.Value, trace.Id.Value);
        Assert.Equal("project-trace-a", trace.ProjectId);
        Assert.Equal("application-trace-a", trace.ApplicationId);
        Assert.Equal("project-snapshot-trace-a", trace.ProjectSnapshotId);
        Assert.Equal("topology-trace-a", trace.TopologyId);
        Assert.Equal("line-a", trace.ProductionLineDefinitionId);
        Assert.Equal("product-model-a", trace.ProductModelId);
        Assert.Equal("serialNumber", trace.ProductionUnitIdentityInputKey);
        Assert.Equal("SMX-0001", trace.ProductionUnitIdentityValue);
        Assert.Equal("lot-a", trace.LotId);
        Assert.Equal("carrier-a", trace.CarrierId);
        Assert.Equal(ExecutionStatus.Completed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, trace.Judgement);
        Assert.Equal(ProductDisposition.Completed, trace.Disposition);

        var operation = Assert.Single(trace.Operations);
        Assert.Equal("operation.inspect", operation.OperationId);
        Assert.Equal(1, operation.Attempt);
        Assert.Equal("station-a", operation.StationSystemId);
        Assert.Equal("station-a", operation.StationId.Value);
        Assert.Equal("process-packaging@2026.06.29", operation.ProcessVersionId.Value);
        Assert.Equal("config-snapshot-a", operation.ConfigurationSnapshotId.Value);
        Assert.Equal("recipe-snapshot-a", operation.RecipeSnapshotId.Value);
        var command = Assert.Single(operation.Commands);
        Assert.Equal(ResultJudgement.Passed, command.ResultJudgement);
        Assert.Single(operation.Measurements);
        Assert.Single(operation.Artifacts);
        Assert.Single(operation.Outputs);
        Assert.Equal(2, operation.FencingTokens.Count);
    }

    [Fact]
    public void CommandAcceptsFailedProductJudgementForCompletedExecution()
    {
        var command = new TraceCommandRecord(
            new RuntimeCommandId(Guid.NewGuid()),
            Guid.NewGuid(),
            "action.inspect",
            TraceTargetKind.System,
            "station-a",
            "capability.inspect",
            "Inspect",
            TraceCommandStatus.Completed,
            ResultJudgement.Failed,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            TraceTestData.BaseTimeUtc.AddSeconds(1),
            TraceTestData.BaseTimeUtc.AddSeconds(2),
            TraceTestData.BaseTimeUtc.AddSeconds(3),
            "ok",
            null);

        Assert.Equal(TraceCommandStatus.Completed, command.Status);
        Assert.Equal(ResultJudgement.Failed, command.ResultJudgement);
    }

    [Fact]
    public void CreateRejectsTraceIdThatDiffersFromProductionRunId()
    {
        var runId = new ProductionRunId(Guid.NewGuid());
        var exception = Assert.Throws<ArgumentException>(() => TraceRecord.Create(
            TraceRecordId.New(),
            runId,
            "project-a",
            "application-a",
            "snapshot-a",
            "topology-a",
            "line-a",
            "product-a",
            "serialNumber",
            "SMX-0002",
            null,
            null,
            new ActorId("operator-a"),
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            ProductDisposition.Completed,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            null,
            null,
            [],
            [],
            []));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void OperationRejectsEvidenceCountThatDiffersFromFrozenCollections()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TraceOperationExecution(
            "operation-a@0001",
            "operation-a",
            1,
            "station-a",
            new StationId("station-a"),
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId("process-a@1.0.0"),
            new ConfigurationSnapshotId("config-a"),
            new RecipeSnapshotId("recipe-a"),
            new RuntimeSessionId(Guid.NewGuid()),
            TraceRuntimeSessionStatus.Completed,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            null,
            null,
            0,
            1,
            0,
            [],
            [],
            [],
            [],
            [],
            []));

        Assert.Contains("counts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationCanceledBeforeDispatchKeepsHonestEmptyRuntimeEvidence()
    {
        var operation = new TraceOperationExecution(
            "operation-a@0001",
            "operation-a",
            1,
            "station-a",
            new StationId("station-a"),
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId("process-a@1.0.0"),
            new ConfigurationSnapshotId("config-a"),
            new RecipeSnapshotId("recipe-a"),
            null,
            null,
            ExecutionStatus.Canceled,
            ResultJudgement.Aborted,
            null,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            "Runtime.ProductionRunStopped",
            "Stopped before dispatch.",
            0,
            0,
            0,
            [],
            [],
            [],
            [],
            [],
            []);

        Assert.Null(operation.RuntimeSessionId);
        Assert.Null(operation.StartedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ArtifactRecordRejectsNonCanonicalSha256(string sha256)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ArtifactRecord(
            ArtifactRecordId.New(),
            "inspection-image",
            ArtifactKind.Image,
            "artifacts/inspection.png",
            "image/png",
            128,
            sha256,
            null,
            TraceTestData.BaseTimeUtc));

        Assert.Equal("sha256", exception.ParamName);
    }
}
