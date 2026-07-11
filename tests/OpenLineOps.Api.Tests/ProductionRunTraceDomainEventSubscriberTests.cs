using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;
using OpenLineOps.Traceability.Infrastructure.Time;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;
using StoredTraceRecordId = OpenLineOps.Traceability.Domain.Identifiers.TraceRecordId;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunTraceDomainEventSubscriberTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductFailureKeepsCompletedExecutionAndNonconformingDisposition()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.CompleteCommand(
            command.Id,
            "{\"outcome\":\"Failed\"}",
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Failed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Failed")
            },
            1,
            1,
            0,
            BaseTimeUtc.AddSeconds(7)).Succeeded);

        await subscriber.HandleAsync(run.ToSnapshot());
        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Completed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, trace.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, trace.Disposition);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Completed, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, storedOperation.Judgement);
        Assert.Empty(storedOperation.Incidents);
        Assert.Single(storedOperation.Outputs);
        var storedCommand = Assert.Single(storedOperation.Commands);
        Assert.Equal(TraceCommandStatus.Completed, storedCommand.Status);
        Assert.Equal(ResultJudgement.Failed, storedCommand.ResultJudgement);
        Assert.False(Assert.Single(storedOperation.Measurements).Passed);
        Assert.Equal(1, traceRepository.AddCount);
    }

    [Fact]
    public async Task SystemFailureKeepsUnknownJudgementAndIncident()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.FailCommand(
            command.Id,
            "Vendor process exited with code 7.",
            BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.Fail(
            BaseTimeUtc.AddSeconds(5),
            "Vendor.ProcessCrashed",
            "Vendor process exited with code 7.").Succeeded);
        await runtimeRepository.SaveAsync(session);
        Assert.True(run.FailOperation(
            operation.OperationRunId,
            ExecutionStatus.Failed,
            "Runtime.OperationSessionFailed",
            "Vendor process exited with code 7.",
            0,
            1,
            1,
            BaseTimeUtc.AddSeconds(6)).Succeeded);

        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Failed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Failed, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, storedOperation.Judgement);
        Assert.Single(storedOperation.Incidents);
        Assert.Equal(TraceCommandStatus.Failed, Assert.Single(storedOperation.Commands).Status);
    }

    [Fact]
    public async Task CommandEvidenceFreezesExternalProgramArtifactHash()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        var payload = RuntimeCommandEvidencePayload.Attach(
            "{\"outcome\":\"Passed\"}",
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            [
                new RuntimeCommandArtifactEvidence(
                    "vendor-report.pdf",
                    "Report",
                    $"external-programs/{run.Id.Value:N}/{command.Id.Value:N}/vendor-report.pdf",
                    "application/pdf",
                    512,
                    new string('a', 64))
            ]);
        Assert.True(session.CompleteCommand(
            command.Id,
            payload,
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Passed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            BaseTimeUtc.AddSeconds(7)).Succeeded);

        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var artifact = Assert.Single(Assert.Single(trace.Operations).Artifacts);
        Assert.Equal(ArtifactKind.Report, artifact.Kind);
        Assert.Equal("application/pdf", artifact.MediaType);
        Assert.Equal(new string('a', 64), artifact.Sha256);
        Assert.Contains(run.Id.Value.ToString("N"), artifact.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeDispatchCreatesHonestOperationWithoutSessionEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        Assert.True(run.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(run.Stop("Operator stopped before dispatch.", BaseTimeUtc.AddSeconds(2)).Succeeded);

        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Canceled, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        var operation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Canceled, operation.ExecutionStatus);
        Assert.Null(operation.RuntimeSessionId);
        Assert.Null(operation.StartedAtUtc);
        Assert.Empty(operation.Commands);
        Assert.Empty(operation.FencingTokens);
    }

    private static ProductionRunTraceDomainEventSubscriber CreateSubscriber(
        InMemoryRuntimeSessionRepository runtimeRepository,
        InMemoryTraceRecordRepository traceRepository)
    {
        var service = new TraceRecordService(
            traceRepository,
            new SystemClock());
        return new ProductionRunTraceDomainEventSubscriber(runtimeRepository, service);
    }

    private static ProductionRun CreateRun()
    {
        var operation = new OperationRunDefinition(
            "operation.board-test",
            "station.functional-test",
            new RuntimeStationId("station.functional-test"),
            new ProcessDefinitionId("process.board-test"),
            new ProcessVersionId("process.board-test@1.0.0"),
            new ConfigurationSnapshotId("configuration.board-test"),
            new RecipeSnapshotId("recipe.board-test"),
            [
                new ResourceRequirement(ResourceKind.Station, "station.functional-test"),
                new ResourceRequirement(ResourceKind.Fixture, "fixture.board-test"),
                new ResourceRequirement(ResourceKind.Device, "tester.board-test")
            ]);
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project-board",
            "application-board-test",
            "snapshot-board-test",
            "topology-board-test",
            "line-board-test",
            new ProductionUnitIdentity("board-model", "serialNumber", "UNIT-BOARD-0001"),
            "lot-board",
            "carrier-board",
            "operator-board",
            operation.OperationId,
            BaseTimeUtc,
            [operation],
            []);
    }

    private static OperationRun StartOperation(
        ProductionRun run,
        out RuntimeSessionId sessionId)
    {
        Assert.True(run.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        sessionId = RuntimeSessionId.New();
        var leases = operation.ResourceRequirements
            .Select((resource, index) => new ResourceLease(
                resource,
                run.Id,
                operation.OperationRunId,
                index + 1,
                BaseTimeUtc,
                BaseTimeUtc.AddMinutes(5)))
            .ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            sessionId,
            leases,
            BaseTimeUtc.AddSeconds(1)).Succeeded);
        return operation;
    }

    private static RuntimeSession CreateRunningSession(
        ProductionRun run,
        OperationRun operation,
        RuntimeSessionId sessionId)
    {
        var session = RuntimeSession.Create(
            sessionId,
            operation.StationId,
            operation.ProcessDefinitionId,
            operation.ProcessVersionId,
            operation.ConfigurationSnapshotId,
            operation.RecipeSnapshotId,
            BaseTimeUtc,
            new RuntimeSessionTraceMetadata(
                run.Id,
                run.ProductionLineDefinitionId,
                operation.OperationId,
                operation.Attempt,
                operation.StationSystemId,
                run.ProductionUnitIdentity,
                run.LotId,
                run.CarrierId,
                "fixture.board-test",
                "tester.board-test",
                run.ActorId,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId));
        Assert.True(session.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        return session;
    }

    private static OpenLineOps.Runtime.Domain.Commands.RuntimeCommand StartCommand(RuntimeSession session)
    {
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.board-test"),
            "Execute vendor program",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("action.board-test"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "station.functional-test"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("test.external"),
            "ExecuteProgram",
            BaseTimeUtc.AddSeconds(2),
            TimeSpan.FromSeconds(30));
        Assert.True(session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(3)).Succeeded);
        return command;
    }
}
