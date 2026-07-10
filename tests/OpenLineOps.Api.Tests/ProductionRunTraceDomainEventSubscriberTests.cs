using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Judgements;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;
using OpenLineOps.Traceability.Infrastructure.Time;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;
using StoredTraceRecordId = OpenLineOps.Traceability.Domain.Identifiers.TraceRecordId;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunTraceDomainEventSubscriberTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RuntimeCommandStatus.Failed)]
    [InlineData(RuntimeCommandStatus.TimedOut)]
    [InlineData(RuntimeCommandStatus.Rejected)]
    public async Task FailedRunCreatesOneTraceWithExactNestedStageCommandMeasurementAndIncident(
        RuntimeCommandStatus commandStatus)
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRunningRun(out var stageSessionId);
        var session = CreateRunningSession(run, stageSessionId);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.inspect.slot"),
            "Inspect slot",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("action.inspect.slot"),
            new RuntimeTargetReference(RuntimeTargetKinds.Slot, "slot.fixture.1"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("capability.inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(10));
        Assert.True(session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(5)).Succeeded);
        var terminalAtUtc = BaseTimeUtc.AddSeconds(6);
        var commandTransition = commandStatus switch
        {
            RuntimeCommandStatus.Failed => session.FailCommand(command.Id, "inspection failed", terminalAtUtc),
            RuntimeCommandStatus.TimedOut => session.TimeoutCommand(command.Id, terminalAtUtc),
            RuntimeCommandStatus.Rejected => session.RejectCommand(command.Id, "inspection rejected", terminalAtUtc),
            _ => throw new InvalidOperationException($"Unsupported command status {commandStatus}.")
        };
        Assert.True(commandTransition.Succeeded);
        Assert.True(session.Fail(BaseTimeUtc.AddSeconds(7), "Runtime.ActionFailed", "Action failed.").Succeeded);
        await runtimeRepository.SaveAsync(session);
        Assert.True(run.FailStage(
            "stage-board-test",
            "Runtime.ActionFailed",
            "Action failed.",
            0,
            1,
            1,
            BaseTimeUtc.AddSeconds(8)).Succeeded);
        var terminalEvents = run.DomainEvents.ToArray();

        await subscriber.HandleAsync(terminalEvents);
        await subscriber.HandleAsync(terminalEvents);
        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value));
        Assert.NotNull(trace);
        Assert.Equal(run.Id.Value, trace.ProductionRunId.Value);
        Assert.Equal(TraceProductionRunStatus.Failed, trace.RunStatus);
        Assert.Equal(ResultJudgement.Failed, trace.Judgement);
        Assert.Equal(1, traceRepository.AddCount);
        var stage = Assert.Single(trace.Stages);
        Assert.Equal(TraceStageStatus.Failed, stage.Status);
        Assert.Equal(session.Id.Value, stage.RuntimeSessionId?.Value);
        Assert.Equal(TraceRuntimeSessionStatus.Failed, stage.RuntimeSessionStatus);
        Assert.Single(stage.Incidents);
        var storedCommand = Assert.Single(stage.Commands);
        Assert.Equal(command.Id.Value, storedCommand.RuntimeCommandId.Value);
        Assert.Equal("action.inspect.slot", storedCommand.ActionId);
        Assert.Equal(TraceTargetKind.Slot, storedCommand.TargetKind);
        Assert.Equal("slot.fixture.1", storedCommand.TargetId);
        Assert.Equal(commandStatus.ToString(), storedCommand.Status.ToString());
        var measurement = Assert.Single(stage.Measurements);
        Assert.Null(measurement.Passed);
        Assert.Equal(command.Id.Value, measurement.RuntimeCommandId?.Value);
        Assert.Equal(session.CompletedAtUtc, stage.CompletedAtUtc);
        Assert.Contains(trace.AuditEntries, entry => entry.Id.Value == run.Id.Value);
        Assert.Contains(trace.AuditEntries, entry => entry.Action == "ProductionRun.Failed");
    }

    [Fact]
    public async Task CanceledRunPreservesInProgressStageEvidenceAsAbortedTrace()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRunningRun(out var stageSessionId);
        var session = CreateRunningSession(run, stageSessionId);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.external.test"),
            "External test",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("action.external.test"),
            new RuntimeTargetReference(RuntimeTargetKinds.Dut, "dut.board.current"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("capability.external.test"),
            "RunExternalTest",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromMinutes(2));
        Assert.True(session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Cancel(BaseTimeUtc.AddSeconds(6), "Operator canceled the DUT run.").Succeeded);
        await runtimeRepository.SaveAsync(session);
        Assert.True(run.Cancel("Operator canceled the DUT run.", 0, 1, 0, BaseTimeUtc.AddSeconds(7)).Succeeded);

        await subscriber.HandleAsync(run.DomainEvents.ToArray());

        var trace = await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value));
        Assert.NotNull(trace);
        Assert.Equal(TraceProductionRunStatus.Canceled, trace.RunStatus);
        Assert.Equal(ResultJudgement.Aborted, trace.Judgement);
        var stage = Assert.Single(trace.Stages);
        Assert.Equal(TraceStageStatus.Canceled, stage.Status);
        Assert.Equal(TraceRuntimeSessionStatus.Canceled, stage.RuntimeSessionStatus);
        var measurement = Assert.Single(stage.Measurements);
        Assert.Equal(TraceCommandStatus.InProgress, measurement.CommandStatus);
        Assert.Null(measurement.Passed);
        Assert.Equal("action.external.test", measurement.ActionId);
        Assert.Equal(TraceTargetKind.Dut, measurement.TargetKind);
    }

    [Theory]
    [InlineData(RuntimeCommandSemanticOutcome.Passed, true)]
    [InlineData(RuntimeCommandSemanticOutcome.Failed, false)]
    [InlineData(RuntimeCommandSemanticOutcome.Aborted, null)]
    public async Task ExplicitCommandSemanticOutcomeControlsTraceMeasurement(
        RuntimeCommandSemanticOutcome semanticOutcome,
        bool? expectedPassed)
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRunningRun(out var stageSessionId);
        var session = CreateRunningSession(run, stageSessionId);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.vendor.test"),
            "Vendor test",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("action.vendor.test"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "station.functional-test"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("capability.vendor.test"),
            "RunVendorTest",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(10));
        Assert.True(session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(5)).Succeeded);
        var vendorPayload = $"{{\"judgement\":\"{semanticOutcome}\"}}";
        switch (semanticOutcome)
        {
            case RuntimeCommandSemanticOutcome.Passed:
                Assert.True(session.CompleteCommand(
                    command.Id,
                    vendorPayload,
                    BaseTimeUtc.AddSeconds(6),
                    semanticOutcome).Succeeded);
                Assert.True(session.CompleteStep(step.Id, BaseTimeUtc.AddSeconds(7)).Succeeded);
                Assert.True(session.Complete(BaseTimeUtc.AddSeconds(8)).Succeeded);
                Assert.True(run.CompleteStage(
                    "stage-board-test",
                    1,
                    1,
                    0,
                    BaseTimeUtc.AddSeconds(9)).Succeeded);
                break;
            case RuntimeCommandSemanticOutcome.Failed:
                Assert.True(session.FailCommand(
                    command.Id,
                    "Vendor judgement failed.",
                    BaseTimeUtc.AddSeconds(6),
                    vendorPayload,
                    semanticOutcome).Succeeded);
                Assert.True(session.Fail(
                    BaseTimeUtc.AddSeconds(7),
                    "Runtime.CommandFailed",
                    "Vendor judgement failed.").Succeeded);
                Assert.True(run.FailStage(
                    "stage-board-test",
                    "Runtime.CommandFailed",
                    "Vendor judgement failed.",
                    0,
                    1,
                    1,
                    BaseTimeUtc.AddSeconds(8)).Succeeded);
                break;
            case RuntimeCommandSemanticOutcome.Aborted:
                Assert.True(session.CancelCommand(
                    command.Id,
                    BaseTimeUtc.AddSeconds(6),
                    "Vendor judgement aborted.",
                    vendorPayload,
                    semanticOutcome).Succeeded);
                Assert.True(session.Cancel(
                    BaseTimeUtc.AddSeconds(7),
                    "Vendor judgement aborted.").Succeeded);
                Assert.True(run.Cancel(
                    "Vendor judgement aborted.",
                    0,
                    1,
                    0,
                    BaseTimeUtc.AddSeconds(8)).Succeeded);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported semantic outcome {semanticOutcome}.");
        }

        await runtimeRepository.SaveAsync(session);
        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(await traceRepository.GetByIdAsync(
            new StoredTraceRecordId(run.Id.Value)));
        var stage = Assert.Single(trace.Stages);
        var storedCommand = Assert.Single(stage.Commands);
        var measurement = Assert.Single(stage.Measurements);
        Assert.Equal(semanticOutcome.ToString(), storedCommand.SemanticOutcome?.ToString());
        Assert.Equal(vendorPayload, storedCommand.ResultPayload);
        Assert.Equal(expectedPassed, measurement.Passed);
    }

    [Fact]
    public async Task CanceledBeforeExecutionCreatesTraceWithHonestSkippedStageAndNoSessionEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        Assert.True(run.Cancel("Canceled before execution.", 0, 0, 0, BaseTimeUtc.AddSeconds(1)).Succeeded);

        await subscriber.HandleAsync(run.DomainEvents.ToArray());

        var trace = await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value));
        Assert.NotNull(trace);
        Assert.Null(trace.StartedAtUtc);
        Assert.Equal(TraceProductionRunStatus.Canceled, trace.RunStatus);
        var stage = Assert.Single(trace.Stages);
        Assert.Equal(TraceStageStatus.Skipped, stage.Status);
        Assert.Null(stage.RuntimeSessionId);
        Assert.Empty(stage.Commands);
        Assert.Empty(stage.Incidents);
    }

    private static ProductionRunTraceDomainEventSubscriber CreateSubscriber(
        InMemoryRuntimeSessionRepository runtimeRepository,
        InMemoryTraceRecordRepository traceRepository)
    {
        var service = new TraceRecordService(
            traceRepository,
            new SystemClock(),
            new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions()));
        return new ProductionRunTraceDomainEventSubscriber(runtimeRepository, service);
    }

    private static ProductionRun CreateRun()
    {
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project-board",
            "application-board-test",
            "snapshot-board-test",
            "topology-board-test",
            "line-board-test",
            new DutIdentity("board-model", "serialNumber", "SN-BOARD-0001"),
            "batch-board",
            "fixture-board",
            "tester-board",
            "operator-board",
            BaseTimeUtc,
            [
                new ProductionStageRunDefinition(
                    "stage-board-test",
                    1,
                    "workstation-board-test",
                    new RuntimeStationId("station.functional-test"),
                    new ProcessDefinitionId("process.board-test"),
                    new ProcessVersionId("process.board-test@1.0.0"),
                    new ConfigurationSnapshotId("configuration.board-test"),
                    new RecipeSnapshotId("recipe.board-test"))
            ]);
    }

    private static ProductionRun CreateRunningRun(out RuntimeSessionId sessionId)
    {
        var run = CreateRun();
        Assert.True(run.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        sessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage("stage-board-test", sessionId, BaseTimeUtc.AddSeconds(1)).Succeeded);
        return run;
    }

    private static RuntimeSession CreateRunningSession(ProductionRun run, RuntimeSessionId sessionId)
    {
        var session = RuntimeSession.Create(
            sessionId,
            new RuntimeStationId("station.functional-test"),
            new ProcessDefinitionId("process.board-test"),
            new ProcessVersionId("process.board-test@1.0.0"),
            new ConfigurationSnapshotId("configuration.board-test"),
            new RecipeSnapshotId("recipe.board-test"),
            BaseTimeUtc,
            new RuntimeSessionTraceMetadata(
                run.Id,
                run.ProductionLineDefinitionId,
                "stage-board-test",
                1,
                "workstation-board-test",
                run.DutIdentity,
                run.BatchId,
                run.FixtureId,
                run.DeviceId,
                run.ActorId,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId));
        Assert.True(session.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        return session;
    }
}
