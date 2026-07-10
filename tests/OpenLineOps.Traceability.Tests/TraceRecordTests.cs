using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordTests
{
    [Fact]
    public void CreateFreezesProductionRunDutScopeAndNestedStageEvidence()
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
        Assert.Equal("dut-model-a", trace.DutModelId);
        Assert.Equal("serialNumber", trace.DutIdentityInputKey);
        Assert.Equal("SMX-0001", trace.DutIdentityValue);
        Assert.Equal(TraceProductionRunStatus.Completed, trace.RunStatus);
        Assert.Equal(ResultJudgement.Passed, trace.Judgement);

        var stage = Assert.Single(trace.Stages);
        Assert.Equal("stage-inspect", stage.StageId);
        Assert.Equal("workstation-a", stage.WorkstationId);
        Assert.Equal("station-a", stage.StationId.Value);
        Assert.Equal("process-packaging@2026.06.29", stage.ProcessVersionId.Value);
        Assert.Equal("config-snapshot-a", stage.ConfigurationSnapshotId.Value);
        Assert.Equal("recipe-snapshot-a", stage.RecipeSnapshotId.Value);
        var command = Assert.Single(stage.Commands);
        Assert.Equal(TraceCommandSemanticOutcome.Passed, command.SemanticOutcome);
        Assert.Single(stage.Measurements);
        Assert.Single(stage.Artifacts);
    }

    [Fact]
    public void CommandRejectsSemanticOutcomeThatContradictsLifecycleStatus()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TraceCommandRecord(
            new RuntimeCommandId(Guid.NewGuid()),
            Guid.NewGuid(),
            "action.inspect",
            TraceTargetKind.System,
            "station-a",
            "capability.inspect",
            "Inspect",
            TraceCommandStatus.Completed,
            TraceCommandSemanticOutcome.Failed,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            TraceTestData.BaseTimeUtc.AddSeconds(1),
            TraceTestData.BaseTimeUtc.AddSeconds(2),
            TraceTestData.BaseTimeUtc.AddSeconds(3),
            "ok",
            null));

        Assert.Equal("semanticOutcome", exception.ParamName);
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
            "dut-a",
            "serialNumber",
            "SMX-0002",
            null,
            null,
            null,
            new ActorId("operator-a"),
            TraceProductionRunStatus.Completed,
            ResultJudgement.Passed,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            null,
            null,
            [],
            []));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void StageRejectsEvidenceCountThatDiffersFromFrozenCollections()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TraceStageExecution(
            "stage-a",
            1,
            "workstation-a",
            new StationId("station-a"),
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId("process-a@1.0.0"),
            new ConfigurationSnapshotId("config-a"),
            new RecipeSnapshotId("recipe-a"),
            new RuntimeSessionId(Guid.NewGuid()),
            TraceRuntimeSessionStatus.Completed,
            TraceStageStatus.Completed,
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
            []));

        Assert.Contains("counts", exception.Message, StringComparison.Ordinal);
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
