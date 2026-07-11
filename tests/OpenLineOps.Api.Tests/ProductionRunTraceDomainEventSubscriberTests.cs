using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
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
        Assert.True(run.Cancel("Operator stopped before dispatch.", BaseTimeUtc.AddSeconds(2)).Succeeded);

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

    [Fact]
    public async Task ReconciledInterruptedOperationFreezesOperatorEvidenceWithoutRuntimeReplay()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out _);
        Assert.True(run.MarkRecoveryRequired(
            "Agent disappeared after device actuation.",
            BaseTimeUtc.AddSeconds(2)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            ProductionRecoveryDecisionKind.Reconcile,
            "operator-board",
            "Inspection confirms the vendor test completed and passed.",
            "inspection:report-0042",
            BaseTimeUtc.AddSeconds(3),
            operationRunId: operation.OperationRunId,
            observedJudgement: ResultJudgement.Passed,
            observedOutputs: new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
            });
        Assert.True(run.ReconcileRecovery(decision).Succeeded);

        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(TraceRuntimeSessionStatus.Reconciled, storedOperation.RuntimeSessionStatus);
        Assert.Equal(ResultJudgement.Passed, storedOperation.Judgement);
        Assert.Empty(storedOperation.Commands);
        Assert.Empty(storedOperation.Incidents);
        Assert.Equal("Passed", Assert.Single(storedOperation.Outputs).CanonicalJson.Trim('"'));
        var audit = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.Recovery.Reconcile");
        Assert.Equal("operator-board", audit.ActorId.Value);
        Assert.Contains("inspection:report-0042", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteStationCompletionFreezesDetailedEvidenceAndCentralArtifactKey()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materials = new InMemoryProductionMaterialRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var service = new TraceRecordService(traceRepository, new SystemClock());
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            runtimeRepository,
            stationJobs,
            materials,
            service);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var stepId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var outputs = TypedOutputs("Passed");
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
            },
            1,
            1,
            0,
            BaseTimeUtc.AddSeconds(7)).Succeeded);
        var idempotencyKey = $"{run.Id.Value:D}/{operation.OperationRunId}";
        await stationJobs.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(idempotencyKey),
            idempotencyKey,
            "agent.functional-test",
            operation.StationId.Value,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            outputs,
            1,
            1,
            0,
            [
                new StationJobStepEvidence(
                    stepId,
                    "node.board-test",
                    "action.board-test",
                    "System",
                    operation.StationSystemId,
                    "Execute vendor program",
                    "Completed",
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(6),
                    null)
            ],
            [
                new StationJobCommandEvidence(
                    commandId,
                    stepId,
                    "node.board-test",
                    "action.board-test",
                    "System",
                    operation.StationSystemId,
                    "test.external",
                    "ExecuteProgram",
                    "Completed",
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(32),
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(3),
                    BaseTimeUtc.AddSeconds(6),
                    "{\"outcome\":\"Passed\"}",
                    null,
                    ResultJudgement.Passed)
            ],
            [],
            [
                new StationJobArtifact(
                    "vendor-report.pdf",
                    "Report",
                    "sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "application/pdf",
                    512,
                    new string('a', 64))
            ],
            null,
            null,
            BaseTimeUtc.AddSeconds(7)));

        await subscriber.HandleAsync(run.ToSnapshot());

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(sessionId.Value, storedOperation.RuntimeSessionId?.Value);
        Assert.Equal(commandId, Assert.Single(storedOperation.Commands).RuntimeCommandId.Value);
        var artifact = Assert.Single(storedOperation.Artifacts);
        Assert.Equal(
            "sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            artifact.StorageKey);
        Assert.Equal(new string('a', 64), artifact.Sha256);
    }

    [Fact]
    public async Task RemoteStationCompletionRejectsTamperedIdentityAndCounts()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materials = new InMemoryProductionMaterialRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            runtimeRepository,
            stationJobs,
            materials,
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            0,
            0,
            0,
            BaseTimeUtc.AddSeconds(7)).Succeeded);
        var idempotencyKey = $"{run.Id.Value:D}/{operation.OperationRunId}";
        await stationJobs.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            Guid.NewGuid(),
            idempotencyKey,
            "agent.functional-test",
            operation.StationId.Value,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            TypedOutputs(),
            1,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            BaseTimeUtc.AddSeconds(7)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await subscriber.HandleAsync(run.ToSnapshot()));
        Assert.Contains("Station job id", exception.Message, StringComparison.Ordinal);
        Assert.Null(await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
    }

    [Fact]
    public async Task RemoteStationCompletionRejectsTamperedEvidenceCount()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            runtimeRepository,
            stationJobs,
            new InMemoryProductionMaterialRepository(),
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            0,
            0,
            0,
            BaseTimeUtc.AddSeconds(7)).Succeeded);
        var idempotencyKey = $"{run.Id.Value:D}/{operation.OperationRunId}";
        await stationJobs.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(idempotencyKey),
            idempotencyKey,
            "agent.functional-test",
            operation.StationId.Value,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            TypedOutputs(),
            1,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            BaseTimeUtc.AddSeconds(7)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await subscriber.HandleAsync(run.ToSnapshot()));
        Assert.Contains("completed step count", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
    }

    [Fact]
    public async Task TerminalTraceIncludesUnitCarrierSlotDispositionAndGenealogyTimeline()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materials = new InMemoryProductionMaterialRepository();
        var materialService = new ProductionMaterialService(
            materials,
            new InMemoryProductionRunRepository(materials));
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            runtimeRepository,
            stationJobs,
            materials,
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var parentId = OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New();
        var carrierId = new CarrierId(run.CarrierId!);
        var station = MaterialLocation.AtStation(
            run.ProductionLineDefinitionId,
            "station.functional-test");
        var slot = new SlotAddress(
            run.ProductionLineDefinitionId,
            "station.functional-test",
            "slot-01");
        Assert.True((await materialService.RegisterUnitAsync(new RegisterProductionUnitCommand(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterUnitAsync(new RegisterProductionUnitCommand(
            parentId,
            run.ProductionUnitIdentity.ModelId,
            "serialNumber",
            "UNIT-PARENT-0001",
            null,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterCarrierAsync(new RegisterCarrierCommand(
            carrierId,
            "tray-4",
            4,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterSlotAsync(new RegisterSlotCommand(
            slot,
            "engineer-a",
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            MaterialReference.ForProductionUnit(run.ProductionUnitId),
            station,
            "scanner-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            MaterialReference.ForCarrier(carrierId),
            station,
            "scanner-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.HoldAsync(new HoldProductionUnitCommand(
            run.ProductionUnitId,
            "quality gate",
            "quality-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ReleaseAsync(new ReleaseProductionUnitCommand(
            run.ProductionUnitId,
            "quality-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
            slot,
            MaterialReference.ForProductionUnit(run.ProductionUnitId),
            "coordinator-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.LinkGenealogyAsync(new LinkMaterialGenealogyCommand(
            MaterialGenealogyLinkId.New(),
            parentId,
            run.ProductionUnitId,
            "ComponentOf",
            "operation.board-test",
            "operator-a",
            BaseTimeUtc))).Succeeded);

        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.CompleteCommand(
            command.Id,
            "{\"outcome\":\"Passed\"}",
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
        Assert.Single(trace.Genealogy);
        Assert.Equal(2, trace.MaterialLocationTransitions.Count);
        Assert.Single(trace.SlotOccupancyTransitions);
        Assert.Equal(2, trace.DispositionTransitions.Count);
        Assert.Contains(trace.MaterialLocationTransitions, transition =>
            transition.MaterialKind == "Carrier" && transition.MaterialId == carrierId.Value);
        Assert.Equal("Reserved", Assert.Single(trace.SlotOccupancyTransitions).CurrentStatus);
    }

    private static JsonElement TypedOutputs(string? outcome = null)
    {
        var json = outcome is null
            ? "{}"
            : $"{{\"test.outcome\":{{\"kind\":\"Text\",\"value\":\"{outcome}\"}}}}";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ProductionRunTraceDomainEventSubscriber CreateSubscriber(
        InMemoryRuntimeSessionRepository runtimeRepository,
        InMemoryTraceRecordRepository traceRepository)
    {
        var service = new TraceRecordService(
            traceRepository,
            new SystemClock());
        return new ProductionRunTraceDomainEventSubscriber(
            runtimeRepository,
            new InMemoryStationJobCoordinationStore(),
            new InMemoryProductionMaterialRepository(),
            service);
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
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
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
                run.ProductionUnitId,
                run.ProductionLineDefinitionId,
                operation.OperationId,
                operation.OperationRunId,
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
                run.TopologyId,
                [new ResourceLeaseFenceEvidence(
                    new ResourceRequirement(ResourceKind.Station, operation.StationSystemId),
                    1,
                    BaseTimeUtc.AddHours(1))]));
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
