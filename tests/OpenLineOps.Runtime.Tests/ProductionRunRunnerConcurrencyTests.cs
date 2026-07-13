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

public sealed class ProductionRunRunnerConcurrencyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartOperationSaveConflictReleasesLeaseBeforeReturning()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new RejectUnexpectedDispatch();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var conflictingRepository = new StartOperationSaveConflictRepository(repository);
        var runner = new ProductionRunRunner(
            conflictingRepository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);

        await Assert.ThrowsAsync<ProductionRunConcurrencyException>(
            () => runner.ExecuteAsync(request.RunId).AsTask());

        Assert.True(conflictingRepository.ConflictRaised);
        Assert.Empty(await leases.ListAsync());
        Assert.Equal(0, dispatcher.DispatchCount);
        var durable = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.Equal(ExecutionStatus.Running, durable.Run.ExecutionStatus);
        Assert.Equal(ExecutionStatus.Pending, Assert.Single(durable.Run.Operations).ExecutionStatus);
    }

    [Fact]
    public async Task ConcurrentRunnersCannotShareAnActiveOwnerLeaseOrReleaseTheWinnerFence()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var innerLeases = new InMemoryResourceLeaseRepository(clock);
        var leases = new CoordinatedLeaseRepository(innerLeases);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new BlockingDispatcher();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var runnerA = CreateRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            clock);
        var runnerB = CreateRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            clock);

        var winningExecution = runnerA.ExecuteAsync(request.RunId).AsTask();
        await leases.FirstLeaseCreated.WaitAsync(TimeSpan.FromSeconds(10));
        var losingExecution = runnerB.ExecuteAsync(request.RunId).AsTask();
        var dispatched = await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var losingResult = await losingExecution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(losingResult.IsSuccess);
        Assert.Null(leases.SecondAcquisition);
        var activeLease = Assert.Single(await innerLeases.ListAsync());
        Assert.Equal(request.RunId, activeLease.ProductionRunId);
        Assert.Equal(dispatched.Operation.OperationRunId, activeLease.OperationRunId);
        Assert.Null(await innerLeases.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.other@0001",
            [activeLease.Resource],
            TimeSpan.FromMinutes(1)));

        dispatcher.Complete(new StationOperationDispatchResult(
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            null,
            0,
            0,
            0,
            Now.AddSeconds(1)));
        var winningResult = await winningExecution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(winningResult.IsSuccess);
        Assert.Empty(await innerLeases.ListAsync());
    }

    [Fact]
    public async Task CreatorReadinessAbortCannotLeaveAnotherRunnerWithAStaleFence()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var innerLeases = new InMemoryResourceLeaseRepository(clock);
        var leases = new CoordinatedLeaseRepository(innerLeases);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new RejectUnexpectedDispatch();
        var readiness = new AbortCreatorOnConfirmationReadiness();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var runnerA = CreateRunner(
            repository,
            repository,
            leases,
            readiness,
            dispatcher,
            publisher,
            clock);
        var runnerB = CreateRunner(
            repository,
            repository,
            leases,
            readiness,
            dispatcher,
            publisher,
            clock);

        var creatorExecution = runnerA.ExecuteAsync(request.RunId).AsTask();
        await leases.FirstLeaseCreated.WaitAsync(TimeSpan.FromSeconds(10));
        var observerExecution = runnerB.ExecuteAsync(request.RunId).AsTask();
        var results = await Task.WhenAll(creatorExecution, observerExecution)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, static result => Assert.True(result.IsSuccess));
        Assert.Null(leases.SecondAcquisition);
        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Empty(await innerLeases.ListAsync());
        var durable = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.Equal(ExecutionStatus.Pending, Assert.Single(durable.Run.Operations).ExecutionStatus);
        var replacement = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await innerLeases.TryAcquireAsync(
                request.RunId,
                "operation.concurrency@0001",
                [new ResourceRequirement(ResourceKind.Station, "station.concurrency")],
                TimeSpan.FromMinutes(1))));
        Assert.True(replacement.FencingToken > leases.FirstLease.FencingToken);
        await innerLeases.ReleaseAsync(
            request.RunId,
            replacement.OperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(replacement)]);
    }

    private static ProductionRunRunner CreateRunner(
        IProductionRunRepository repository,
        IProductionRunExecutionPlanRepository plans,
        IResourceLeaseRepository leases,
        IProductionOperationReadiness readiness,
        IStationOperationDispatcher dispatcher,
        IRuntimeDomainEventPublisher publisher,
        IClock clock) =>
        new(
            repository,
            plans,
            leases,
            readiness,
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);

    private static async ValueTask RegisterRequestUnitAsync(
        InMemoryProductionMaterialRepository materials,
        InMemoryProductionRunRepository repository,
        SubmitProductionRunRequest request)
    {
        var unit = ProductionUnit.Register(
            request.ProductionUnitId,
            request.FrozenProductModelId,
            request.FrozenIdentityInputKey,
            $"UNIT-{request.ProductionUnitId.Value:N}",
            null,
            request.ActorId,
            Now.AddTicks(-1));
        Assert.True(await materials.TryAddAsync(unit));
        var materialService = new ProductionMaterialService(materials, repository);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(unit.Id),
            MaterialLocation.AtStation(
                request.ProductionLineDefinitionId,
                request.Operations[0].Definition.StationSystemId),
            request.ActorId,
            Now))).Succeeded);
    }

    private static SubmitProductionRunRequest CreateRequest()
    {
        const string operationId = "operation.concurrency";
        const string stationId = "station.concurrency";
        var operation = new OperationExecutionPlan(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId("configuration.concurrency"),
            new RecipeSnapshotId("recipe.concurrency"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process.concurrency"),
                new ProcessVersionId("process-version.concurrency"),
                []),
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.concurrency",
            "application.concurrency",
            "snapshot.concurrency",
            "topology.concurrency",
            "line.concurrency",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.concurrency",
            operationId,
            [operation],
            [new RouteTransitionDefinition(
                "route.concurrency.completed",
                operationId,
                targetOperationId: null,
                RuntimeRouteTransitionKind.Sequence,
                terminalDisposition: ProductDisposition.Completed)]);
    }

    private sealed class StartOperationSaveConflictRepository(
        IProductionRunRepository inner) : IProductionRunRepository
    {
        public bool ConflictRaised { get; private set; }

        public ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(run, executionPlan, admission, cancellationToken);

        public ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (!ConflictRaised && expectedRevision == 1)
            {
                ConflictRaised = true;
                return ValueTask.FromException<long>(
                    new ProductionRunConcurrencyException(run.Id, expectedRevision));
            }

            return inner.SaveAsync(run, expectedRevision, cancellationToken);
        }

        public ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(runId, cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListRecoverableAsync(cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
            string? productionLineDefinitionId = null,
            string? stationSystemId = null,
            string? slotId = null,
            CancellationToken cancellationToken = default) =>
            inner.ListActiveAsync(
                productionLineDefinitionId,
                stationSystemId,
                slotId,
                cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
            ListPendingCreatedOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            inner.ListPendingCreatedOutboxAsync(maximumCount, cancellationToken);

        public ValueTask MarkCreatedOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            inner.MarkCreatedOutboxProcessedAsync(runId, cancellationToken);

        public ValueTask RecordCreatedOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            inner.RecordCreatedOutboxFailureAsync(runId, failureDescription, cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
            ListPendingTerminalOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            inner.ListPendingTerminalOutboxAsync(maximumCount, cancellationToken);

        public ValueTask MarkTerminalOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            inner.MarkTerminalOutboxProcessedAsync(runId, cancellationToken);

        public ValueTask RecordTerminalOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            inner.RecordTerminalOutboxFailureAsync(runId, failureDescription, cancellationToken);
    }

    private sealed class RejectUnexpectedDispatch : IStationOperationDispatcher
    {
        public int DispatchCount { get; private set; }

        public ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            DispatchCount++;
            return ValueTask.FromException<StationOperationDispatchResult>(
                new InvalidOperationException("The Station job must not be dispatched."));
        }
    }

    private sealed class BlockingDispatcher : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource<StationOperationDispatchRequest> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<StationOperationDispatchResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StationOperationDispatchRequest> Started => _started.Task;

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(request);
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(StationOperationDispatchResult result) =>
            _completion.TrySetResult(result);
    }

    private sealed class AbortCreatorOnConfirmationReadiness : IProductionOperationReadiness
    {
        private int _evaluationCount;

        public ValueTask<ProductionOperationReadiness> EvaluateAsync(
            ProductionRunSnapshot run,
            OperationRunSnapshot operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Interlocked.Increment(ref _evaluationCount) == 3
                ? new ProductionOperationReadiness(
                    ProductionOperationReadinessKind.Waiting,
                    "Material readiness changed after lease acquisition.",
                    [],
                    null)
                : ProductionOperationReadiness.Ready);
        }
    }

    private sealed class CoordinatedLeaseRepository(
        InMemoryResourceLeaseRepository inner) : IResourceLeaseRepository
    {
        private readonly TaskCompletionSource _firstLeaseCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acquisitionCount;

        public Task FirstLeaseCreated => _firstLeaseCreated.Task;

        public ResourceLease FirstLease { get; private set; } = null!;

        public IReadOnlyCollection<ResourceLease>? SecondAcquisition { get; private set; }

        public ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);

        public async ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceRequirement> resources,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _acquisitionCount);
            if (attempt == 1)
            {
                var acquired = await inner.TryAcquireAsync(
                        runId,
                        operationRunId,
                        resources,
                        duration,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The first lease attempt must succeed.");
                FirstLease = Assert.Single(acquired);
                _firstLeaseCreated.TrySetResult();
                await _secondAttempted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return acquired;
            }

            if (attempt == 2)
            {
                await _firstLeaseCreated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    SecondAcquisition = await inner.TryAcquireAsync(
                            runId,
                            operationRunId,
                            resources,
                            duration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return SecondAcquisition;
                }
                finally
                {
                    _secondAttempted.TrySetResult();
                }
            }

            return await inner.TryAcquireAsync(
                    runId,
                    operationRunId,
                    resources,
                    duration,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
            CancellationToken cancellationToken = default) =>
            inner.ValidateCurrentAsync(
                runId,
                operationRunId,
                evidence,
                cancellationToken);

        public ValueTask ReleaseAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(runId, operationRunId, claims, cancellationToken);

        public ValueTask HoldForRecoveryAsync(
            ProductionRunId runId,
            string operationRunId,
            CancellationToken cancellationToken = default) =>
            inner.HoldForRecoveryAsync(runId, operationRunId, cancellationToken);
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
