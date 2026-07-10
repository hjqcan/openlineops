using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StagesExecuteOneAtATimeInStrictSequenceAndCompleteRun()
    {
        var run = CreateRun();
        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);

        var firstSessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage("stage.scan", firstSessionId, CreatedAtUtc.AddSeconds(2)).Succeeded);
        var parallelStart = run.StartStage(
            "stage.test",
            RuntimeSessionId.New(),
            CreatedAtUtc.AddSeconds(3));
        Assert.False(parallelStart.Succeeded);
        Assert.Equal("Runtime.ProductionStageAlreadyRunning", parallelStart.Code);

        Assert.True(run.CompleteStage(
            "stage.scan",
            2,
            2,
            0,
            CreatedAtUtc.AddSeconds(4)).Succeeded);
        Assert.True(run.StartStage(
            "stage.test",
            RuntimeSessionId.New(),
            CreatedAtUtc.AddSeconds(5)).Succeeded);
        Assert.True(run.CompleteStage(
            "stage.test",
            4,
            5,
            1,
            CreatedAtUtc.AddSeconds(6)).Succeeded);
        Assert.True(run.StartStage(
            "stage.pack",
            RuntimeSessionId.New(),
            CreatedAtUtc.AddSeconds(7)).Succeeded);
        Assert.True(run.CompleteStage(
            "stage.pack",
            1,
            1,
            0,
            CreatedAtUtc.AddSeconds(8)).Succeeded);

        Assert.Equal(ProductionRunStatus.Completed, run.Status);
        Assert.All(run.Stages, stage => Assert.Equal(ProductionStageRunStatus.Completed, stage.Status));
        Assert.Equal(7, run.Stages.Sum(stage => stage.CompletedStepCount));
        Assert.Equal(8, run.Stages.Sum(stage => stage.CommandCount));
        Assert.Equal(1, run.Stages.Sum(stage => stage.IncidentCount));
        var terminalEvent = Assert.IsType<ProductionRunTerminalDomainEvent>(run.DomainEvents.Last());
        Assert.Equal(run.Id, terminalEvent.Run.RunId);
        Assert.Equal("dut.serial", terminalEvent.Run.DutIdentity.InputKey);
        Assert.Equal("operator.1", terminalEvent.Run.ActorId);
        Assert.Equal(3, terminalEvent.Run.Stages.Count);
        Assert.Equal(firstSessionId, terminalEvent.Run.Stages[0].RuntimeSessionId);
        Assert.Equal(2, terminalEvent.Run.Stages[0].CompletedStepCount);
    }

    [Fact]
    public void FailedStageFailsRunAndSkipsEveryRemainingStage()
    {
        var run = CreateRun();
        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        Assert.True(run.StartStage(
            "stage.scan",
            RuntimeSessionId.New(),
            CreatedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.CompleteStage(
            "stage.scan",
            1,
            1,
            0,
            CreatedAtUtc.AddSeconds(3)).Succeeded);
        var failedSessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage(
            "stage.test",
            failedSessionId,
            CreatedAtUtc.AddSeconds(4)).Succeeded);

        Assert.True(run.FailStage(
            "stage.test",
            "Runtime.MeasurementOutOfRange",
            "Voltage is outside tolerance.",
            3,
            4,
            1,
            CreatedAtUtc.AddSeconds(5)).Succeeded);

        Assert.Equal(ProductionRunStatus.Failed, run.Status);
        Assert.Collection(
            run.Stages,
            first => Assert.Equal(ProductionStageRunStatus.Completed, first.Status),
            second =>
            {
                Assert.Equal(ProductionStageRunStatus.Failed, second.Status);
                Assert.Equal(failedSessionId, second.RuntimeSessionId);
                Assert.Equal(3, second.CompletedStepCount);
                Assert.Equal(4, second.CommandCount);
                Assert.Equal(1, second.IncidentCount);
            },
            third =>
            {
                Assert.Equal(ProductionStageRunStatus.Skipped, third.Status);
                Assert.Null(third.RuntimeSessionId);
                Assert.Equal(0, third.CommandCount);
            });
        var terminalEvent = Assert.IsType<ProductionRunTerminalDomainEvent>(run.DomainEvents.Last());
        Assert.Equal(ProductionRunStatus.Failed, terminalEvent.Run.Status);
        Assert.Equal("Runtime.MeasurementOutOfRange", terminalEvent.Run.FailureCode);
        Assert.Equal(ProductionStageRunStatus.Skipped, terminalEvent.Run.Stages[2].Status);
    }

    [Fact]
    public void CancelCreatedRunSkipsAllStagesAndProducesCompleteTerminalSnapshot()
    {
        var run = CreateRun();

        var result = run.Cancel(
            "Operator canceled before execution.",
            0,
            0,
            0,
            CreatedAtUtc.AddSeconds(1));

        Assert.True(result.Succeeded);
        Assert.Equal(ProductionRunStatus.Canceled, run.Status);
        Assert.Null(run.StartedAtUtc);
        Assert.All(run.Stages, stage => Assert.Equal(ProductionStageRunStatus.Skipped, stage.Status));
        var terminalEvent = Assert.IsType<ProductionRunTerminalDomainEvent>(run.DomainEvents.Last());
        Assert.Equal(ProductionRunStatus.Canceled, terminalEvent.Run.Status);
        Assert.Equal(3, terminalEvent.Run.Stages.Count);
    }

    [Fact]
    public void ProductionIdentityRequiresInputKeyAndCanonicalActor()
    {
        Assert.Throws<ArgumentException>(() => new DutIdentity("dut.model", " ", "SN-001"));
        Assert.Throws<ArgumentException>(() => ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            new DutIdentity("dut.model", "dut.serial", "SN-001"),
            null,
            null,
            null,
            " ",
            CreatedAtUtc,
            CreateStageDefinitions()));
    }

    private static ProductionRun CreateRun()
    {
        return ProductionRun.Create(
            new ProductionRunId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
            "project.main",
            "application.main",
            "snapshot.release",
            "topology.main",
            "line.main",
            new DutIdentity("dut.model", "dut.serial", "SN-001"),
            "batch.1",
            "fixture.1",
            "device.1",
            "operator.1",
            CreatedAtUtc,
            CreateStageDefinitions());
    }

    private static ProductionStageRunDefinition[] CreateStageDefinitions()
    {
        return
        [
            Stage("stage.scan", 1, "workstation.scan"),
            Stage("stage.test", 2, "workstation.test"),
            Stage("stage.pack", 3, "workstation.pack")
        ];
    }

    private static ProductionStageRunDefinition Stage(
        string stageId,
        int sequence,
        string workstationId)
    {
        return new ProductionStageRunDefinition(
            stageId,
            sequence,
            workstationId,
            new StationId($"station.{sequence}"),
            new ProcessDefinitionId($"process.{sequence}"),
            new ProcessVersionId($"process.{sequence}@1.0.0"),
            new ConfigurationSnapshotId($"configuration.{sequence}"),
            new RecipeSnapshotId($"recipe.{sequence}"));
    }
}
