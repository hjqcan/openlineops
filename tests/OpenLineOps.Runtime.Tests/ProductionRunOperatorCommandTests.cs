using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunOperatorCommandTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PauseAndContinuePersistExactControlStateAndRejectIllegalRepeats()
    {
        var fixture = new Fixture();
        var request = CreateSingleOperationRequest();
        await fixture.SubmitAndStartAsync(request);
        var initialRevision = await fixture.RevisionAsync(request.RunId);

        var continueBeforePause = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Continue);

        Assert.True(continueBeforePause.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.ProductionRunContinueRejected",
            continueBeforePause.Error.Code);
        Assert.Equal(initialRevision, await fixture.RevisionAsync(request.RunId));

        var paused = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Pause);

        Assert.True(paused.IsSuccess);
        Assert.Equal(ProductionRunControlState.Paused, paused.Value.ControlState);
        Assert.Equal(
            ProductionRunControlState.Paused,
            (await fixture.EntryAsync(request.RunId)).Run.ControlState);
        var pausedRevision = await fixture.RevisionAsync(request.RunId);

        var duplicatePause = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Pause);

        Assert.True(duplicatePause.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunPauseRejected", duplicatePause.Error.Code);
        Assert.Equal(pausedRevision, await fixture.RevisionAsync(request.RunId));

        var continued = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Continue);

        Assert.True(continued.IsSuccess);
        Assert.Equal(ProductionRunControlState.Active, continued.Value.ControlState);
        Assert.Equal(
            ProductionRunControlState.Active,
            (await fixture.EntryAsync(request.RunId)).Run.ControlState);
        var continuedRevision = await fixture.RevisionAsync(request.RunId);

        var duplicateContinue = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Continue);

        Assert.True(duplicateContinue.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.ProductionRunContinueRejected",
            duplicateContinue.Error.Code);
        Assert.Equal(continuedRevision, await fixture.RevisionAsync(request.RunId));
    }

    [Fact]
    public async Task HoldAndReleaseSynchronizePersistedProductionUnitDisposition()
    {
        var fixture = new Fixture();
        var request = CreateSingleOperationRequest();
        await fixture.SubmitAndStartAsync(request);

        var held = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Hold,
            "Quality review required.");

        Assert.True(held.IsSuccess);
        Assert.Equal(ProductionRunControlState.Held, held.Value.ControlState);
        Assert.Equal(ProductDisposition.Held, held.Value.Disposition);
        var heldUnit = await fixture.UnitAsync(request.ProductionUnitId);
        Assert.Equal(ProductDisposition.Held, heldUnit.Disposition);
        Assert.Equal(request.RunId, heldUnit.ActiveProductionRunId);
        var heldRevision = await fixture.RevisionAsync(request.RunId);

        var duplicateHold = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Hold,
            "Quality review required.");

        Assert.True(duplicateHold.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunHoldRejected", duplicateHold.Error.Code);
        Assert.Equal(heldRevision, await fixture.RevisionAsync(request.RunId));

        var released = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Release);

        Assert.True(released.IsSuccess);
        Assert.Equal(ProductionRunControlState.Active, released.Value.ControlState);
        Assert.Equal(ProductDisposition.InProcess, released.Value.Disposition);
        var releasedUnit = await fixture.UnitAsync(request.ProductionUnitId);
        Assert.Equal(ProductDisposition.InProcess, releasedUnit.Disposition);
        Assert.Equal(request.RunId, releasedUnit.ActiveProductionRunId);
        var releasedRevision = await fixture.RevisionAsync(request.RunId);

        var duplicateRelease = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Release);

        Assert.True(duplicateRelease.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunReleaseRejected", duplicateRelease.Error.Code);
        Assert.Equal(releasedRevision, await fixture.RevisionAsync(request.RunId));
    }

    [Fact]
    public async Task ReworkCreatesOneNewAttemptAndRejectsTheCommandOutsideHold()
    {
        var fixture = new Fixture();
        var request = CreateSequentialRequest();
        await fixture.SubmitAndStartAsync(request);
        await fixture.CompleteFirstOperationAsync(request.RunId);

        var illegalRework = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Rework,
            operationId: "operation.main");

        Assert.True(illegalRework.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunReworkRejected", illegalRework.Error.Code);
        var revisionBeforeHold = await fixture.RevisionAsync(request.RunId);

        Assert.True((await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Hold,
            "Return to preparation.")).IsSuccess);
        var reworked = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Rework,
            operationId: "operation.main");

        Assert.True(reworked.IsSuccess);
        Assert.Equal(ProductionRunControlState.Active, reworked.Value.ControlState);
        Assert.Equal(ProductDisposition.InProcess, reworked.Value.Disposition);
        Assert.Equal(
            [1, 2],
            reworked.Value.Operations
                .Where(operation => operation.Definition.OperationId == "operation.main")
                .Select(operation => operation.Attempt)
                .ToArray());
        Assert.Equal(
            ExecutionStatus.Pending,
            reworked.Value.Operations.Single(operation =>
                operation.Definition.OperationId == "operation.main" && operation.Attempt == 2)
                .ExecutionStatus);
        Assert.True(await fixture.RevisionAsync(request.RunId) > revisionBeforeHold);
        var persisted = await fixture.EntryAsync(request.RunId);
        Assert.Equal(
            2,
            persisted.Run.Operations.Count(operation =>
                operation.OperationId == "operation.main"));

        var revisionAfterRework = persisted.Revision;
        var duplicateOutsideHold = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Rework,
            operationId: "operation.main");

        Assert.True(duplicateOutsideHold.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.ProductionRunReworkRejected",
            duplicateOutsideHold.Error.Code);
        Assert.Equal(revisionAfterRework, await fixture.RevisionAsync(request.RunId));
    }

    [Fact]
    public async Task ScrapTerminatesRunAndProductionUnitWithoutMutatingOnReplay()
    {
        var fixture = new Fixture();
        var request = CreateSingleOperationRequest();
        await fixture.SubmitAndStartAsync(request);

        var scrapped = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Scrap,
            "Physical damage confirmed.");

        Assert.True(scrapped.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, scrapped.Value.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, scrapped.Value.Judgement);
        Assert.Equal(ProductDisposition.Scrapped, scrapped.Value.Disposition);
        Assert.Equal("operator.main", scrapped.Value.ScrapRequestedBy);
        Assert.Equal("Physical damage confirmed.", scrapped.Value.ScrapReason);
        Assert.Equal(Now, scrapped.Value.ScrapRequestedAtUtc);
        Assert.NotNull(scrapped.Value.CompletedAtUtc);
        var reloaded = (await fixture.EntryAsync(request.RunId)).Run.ToSnapshot();
        var canceledBeforeExecution = Assert.Single(reloaded.Operations);
        Assert.Equal(ExecutionStatus.Canceled, canceledBeforeExecution.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, canceledBeforeExecution.Judgement);
        Assert.Null(canceledBeforeExecution.RuntimeSessionId);
        Assert.Null(canceledBeforeExecution.StartedAtUtc);
        Assert.NotNull(canceledBeforeExecution.CompletedAtUtc);
        var unit = await fixture.UnitAsync(request.ProductionUnitId);
        Assert.Equal(ProductDisposition.Scrapped, unit.Disposition);
        Assert.Null(unit.ActiveProductionRunId);
        var scrappedRevision = await fixture.RevisionAsync(request.RunId);

        var duplicateScrap = await fixture.CommandAsync(
            request.RunId,
            ProductionRunCommand.Scrap,
            "Physical damage confirmed.");

        Assert.True(duplicateScrap.IsSuccess);
        Assert.Equal(scrapped.Value.ScrapRequestedAtUtc, duplicateScrap.Value.ScrapRequestedAtUtc);
        Assert.Equal(scrappedRevision, await fixture.RevisionAsync(request.RunId));
    }

    [Fact]
    public async Task RetryRecoveryIsDurablyIdempotentAndRejectsDifferentDecisionAfterRecovery()
    {
        var fixture = new Fixture();
        var request = CreateSingleOperationRequest();
        await fixture.SubmitAndStartAsync(request);
        await fixture.EnterRecoveryAsync(request.RunId);
        var decision = RecoveryDecision(
            ProductionRecoveryDecisionKind.Retry,
            operationId: "operation.main");

        var retried = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Retry,
            decision);

        Assert.True(retried.IsSuccess);
        Assert.Equal(ProductionRunControlState.Active, retried.Value.ControlState);
        Assert.Equal(ProductDisposition.InProcess, retried.Value.Disposition);
        Assert.Equal(
            [ExecutionStatus.Canceled, ExecutionStatus.Pending],
            retried.Value.Operations
                .Where(operation => operation.Definition.OperationId == "operation.main")
                .OrderBy(operation => operation.Attempt)
                .Select(operation => operation.ExecutionStatus)
                .ToArray());
        Assert.Equal(decision.DecisionId, Assert.Single(retried.Value.RecoveryDecisions).DecisionId);
        var retryRevision = await fixture.RevisionAsync(request.RunId);

        var replay = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Retry,
            decision);

        Assert.True(replay.IsSuccess);
        Assert.Equal(retryRevision, await fixture.RevisionAsync(request.RunId));
        Assert.Equal(2, replay.Value.Operations.Count);

        var differentDecision = RecoveryDecision(
            ProductionRecoveryDecisionKind.Retry,
            operationId: "operation.main");
        var illegalAfterRecovery = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Retry,
            differentDecision);

        Assert.True(illegalAfterRecovery.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.ProductionRunRetryRejected",
            illegalAfterRecovery.Error.Code);
        Assert.Equal(retryRevision, await fixture.RevisionAsync(request.RunId));
    }

    [Fact]
    public async Task AbortRecoveryIsDurablyIdempotentAndRejectsDifferentTerminalDecision()
    {
        var fixture = new Fixture();
        var request = CreateSingleOperationRequest();
        await fixture.SubmitAndStartAsync(request);
        await fixture.EnterRecoveryAsync(request.RunId);
        var decision = RecoveryDecision(ProductionRecoveryDecisionKind.Abort);

        var aborted = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Abort,
            decision);

        Assert.True(aborted.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, aborted.Value.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, aborted.Value.Judgement);
        Assert.Equal(ProductDisposition.Held, aborted.Value.Disposition);
        Assert.NotNull(aborted.Value.CompletedAtUtc);
        Assert.Equal(decision.DecisionId, Assert.Single(aborted.Value.RecoveryDecisions).DecisionId);
        var unit = await fixture.UnitAsync(request.ProductionUnitId);
        Assert.Equal(ProductDisposition.Held, unit.Disposition);
        Assert.Null(unit.ActiveProductionRunId);
        var abortRevision = await fixture.RevisionAsync(request.RunId);

        var replay = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Abort,
            decision);

        Assert.True(replay.IsSuccess);
        Assert.Equal(abortRevision, await fixture.RevisionAsync(request.RunId));

        var differentDecision = RecoveryDecision(ProductionRecoveryDecisionKind.Abort);
        var illegalTerminalDecision = await fixture.RecoveryCommandAsync(
            request.RunId,
            ProductionRunCommand.Abort,
            differentDecision);

        Assert.True(illegalTerminalDecision.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.ProductionRunAbortRejected",
            illegalTerminalDecision.Error.Code);
        Assert.Equal(abortRevision, await fixture.RevisionAsync(request.RunId));
    }

    private static ProductionRecoveryDecision RecoveryDecision(
        ProductionRecoveryDecisionKind kind,
        string? operationId = null) => new(
        Guid.NewGuid(),
        kind,
        "operator.recovery",
        $"Operator selected {kind}.",
        $"urn:openlineops:test:recovery:{Guid.NewGuid():N}",
        Now,
        operationId: operationId);

    private static SubmitProductionRunRequest CreateSingleOperationRequest()
    {
        var operation = Operation("operation.main", "station.main");
        return Request(
            "single",
            operation.Definition.OperationId,
            [operation],
            [TerminalTransition("route.completed", operation.Definition.OperationId)]);
    }

    private static SubmitProductionRunRequest CreateSequentialRequest()
    {
        var first = Operation("operation.main", "station.main");
        var next = Operation("operation.next", "station.next");
        return Request(
            "sequential",
            first.Definition.OperationId,
            [first, next],
            [
                new RouteTransitionDefinition(
                    "route.next",
                    first.Definition.OperationId,
                    next.Definition.OperationId,
                    RuntimeRouteTransitionKind.Sequence),
                TerminalTransition("route.completed", next.Definition.OperationId)
            ]);
    }

    private static OperationExecutionPlan Operation(string operationId, string stationId) => new(
        operationId,
        stationId,
        new StationId(stationId),
        new ConfigurationSnapshotId($"configuration.{operationId}"),
        new RecipeSnapshotId($"recipe.{operationId}"),
        new ExecutableRuntimeProcess(
            new ProcessDefinitionId($"process.{operationId}"),
            new ProcessVersionId($"process-version.{operationId}"),
            []),
        [],
        [new ResourceRequirement(ResourceKind.Station, stationId)]);

    private static SubmitProductionRunRequest Request(
        string suffix,
        string entryOperationId,
        IReadOnlyList<OperationExecutionPlan> operations,
        IReadOnlyList<RouteTransitionDefinition> transitions) => new(
        ProductionRunId.New(),
        $"project.{suffix}",
        $"application.{suffix}",
        $"snapshot.{suffix}",
        $"topology.{suffix}",
        $"line.{suffix}",
        ProductionUnitId.New(),
        "product.board",
        "serialNumber",
        "operator.main",
        entryOperationId,
        operations,
        transitions);

    private static RouteTransitionDefinition TerminalTransition(
        string transitionId,
        string sourceOperationId) => new(
        transitionId,
        sourceOperationId,
        null,
        RuntimeRouteTransitionKind.Sequence,
        terminalDisposition: ProductDisposition.Completed);

    private sealed class Fixture
    {
        private readonly FixedClock _clock = new(Now);
        private readonly InMemoryProductionMaterialRepository _materials = new();
        private readonly InMemoryRuntimeDomainEventPublisher _publisher = new();

        public Fixture()
        {
            Repository = new InMemoryProductionRunRepository(_materials);
            Leases = new InMemoryResourceLeaseRepository(_clock);
            Coordinator = new ProductionRunCoordinator(
                Repository,
                _materials,
                Leases,
                new InMemoryProductionRunSafetyTransitionStore(Repository, Leases),
                new AcceptingSafetyController(),
                new AcceptingCanceler(),
                _publisher,
                new ProductionRunCreatedOutboxDispatcher(Repository, _publisher),
                _clock);
        }

        public InMemoryProductionRunRepository Repository { get; }

        public InMemoryResourceLeaseRepository Leases { get; }

        public ProductionRunCoordinator Coordinator { get; }

        public async Task SubmitAndStartAsync(SubmitProductionRunRequest request)
        {
            var entryOperation = request.Operations.Single(operation =>
                operation.Definition.OperationId == request.EntryOperationId);
            var unit = ProductionUnit.Register(
                request.ProductionUnitId,
                request.FrozenProductModelId,
                request.FrozenIdentityInputKey,
                $"UNIT-{request.ProductionUnitId.Value:N}",
                null,
                request.ActorId,
                Now.AddTicks(-1));
            Assert.True(await _materials.TryAddAsync(unit));
            var materialService = new ProductionMaterialService(_materials, Repository);
            Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                MaterialReference.ForProductionUnit(unit.Id),
                MaterialLocation.AtStation(
                    request.ProductionLineDefinitionId,
                    entryOperation.Definition.StationSystemId),
                request.ActorId,
                Now))).Succeeded);
            Assert.True((await Coordinator.SubmitAsync(request)).IsSuccess);

            var entry = await EntryAsync(request.RunId);
            Assert.True(entry.Run.Start(Now).Succeeded);
            await Repository.SaveAsync(entry.Run, entry.Revision);
        }

        public async Task CompleteFirstOperationAsync(ProductionRunId runId)
        {
            var entry = await EntryAsync(runId);
            var operation = entry.Run.Operations.Single(operation => operation.Attempt == 1);
            var leases = operation.ResourceRequirements
                .Select((requirement, index) => new ResourceLease(
                    requirement,
                    runId,
                    operation.OperationRunId,
                    index + 1,
                    Now,
                    Now.AddMinutes(1)))
                .ToArray();
            Assert.True(entry.Run.StartOperation(
                operation.OperationRunId,
                RuntimeSessionId.New(),
                leases,
                Now).Succeeded);
            Assert.True(entry.Run.CompleteOperation(
                operation.OperationRunId,
                ResultJudgement.Passed,
                outputs: null,
                completedStepCount: 1,
                commandCount: 1,
                incidentCount: 0,
                completedAtUtc: Now,
                executionEvidence: ProductionRunExecutionEvidenceTestFactory.Create(
                    entry.Run,
                    operation.OperationRunId,
                    ExecutionStatus.Completed,
                    ResultJudgement.Passed,
                    Now,
                    1,
                    1)).Succeeded);
            await Repository.SaveAsync(entry.Run, entry.Revision);
        }

        public async Task EnterRecoveryAsync(ProductionRunId runId)
        {
            var entry = await EntryAsync(runId);
            var operation = entry.Run.Operations.Single();
            var leases = operation.ResourceRequirements
                .Select((requirement, index) => new ResourceLease(
                    requirement,
                    runId,
                    operation.OperationRunId,
                    index + 1,
                    Now,
                    Now.AddMinutes(1)))
                .ToArray();
            Assert.True(entry.Run.StartOperation(
                operation.OperationRunId,
                RuntimeSessionId.New(),
                leases,
                Now).Succeeded);
            Assert.True(entry.Run.MarkRecoveryRequired(
                "Coordinator lost the Station execution boundary.",
                Now).Succeeded);
            await Repository.SaveAsync(entry.Run, entry.Revision);
        }

        public ValueTask<OpenLineOps.Application.Abstractions.Results.Result<ProductionRunSnapshot>>
            CommandAsync(
                ProductionRunId runId,
                ProductionRunCommand command,
                string? reason = null,
                string? operationId = null) =>
            Coordinator.CommandAsync(
                runId,
                new ProductionRunCommandRequest(
                    command,
                    "operator.main",
                    reason,
                    operationId));

        public ValueTask<OpenLineOps.Application.Abstractions.Results.Result<ProductionRunSnapshot>>
            RecoveryCommandAsync(
                ProductionRunId runId,
                ProductionRunCommand command,
                ProductionRecoveryDecision decision) =>
            Coordinator.CommandAsync(
                runId,
                new ProductionRunCommandRequest(
                    command,
                    decision.ActorId,
                    decision.Reason,
                    recoveryDecision: decision));

        public async Task<ProductionRunPersistenceEntry> EntryAsync(ProductionRunId runId) =>
            Assert.IsType<ProductionRunPersistenceEntry>(
                await Repository.GetByIdAsync(runId));

        public async Task<long> RevisionAsync(ProductionRunId runId) =>
            (await EntryAsync(runId)).Revision;

        public async Task<ProductionUnit> UnitAsync(ProductionUnitId productionUnitId) =>
            Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await _materials.GetProductionUnitAsync(productionUnitId))
                .Aggregate;
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }

    private sealed class AcceptingSafetyController : IStationSafetyController
    {
        public ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationSafetyResult.Success());
    }

    private sealed class AcceptingCanceler : IStationOperationCanceler
    {
        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationOperationCancellationResult.Success());
    }
}
