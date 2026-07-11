using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Materials;
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

public sealed class ProductionRunRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SubmissionIsAsynchronousAndRunnerDispatchesPersistedFencedStationJob()
    {
        var fixture = new Fixture(new StationOperationDispatchResult(
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            null,
            1,
            2,
            0,
            Now.AddSeconds(2)));
        var request = CreateRequest();

        var submitted = await fixture.SubmitAsync(request);
        Assert.True(submitted.IsSuccess);
        Assert.Equal(ExecutionStatus.Pending, submitted.Value.ExecutionStatus);
        Assert.Empty(fixture.Dispatcher.Requests);

        var executed = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(executed.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, executed.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, executed.Value.Run.Judgement);
        var dispatched = Assert.Single(fixture.Dispatcher.Requests);
        Assert.Equal($"{request.RunId.Value:D}/operation.main@0001", dispatched.IdempotencyKey);
        Assert.All(dispatched.ResourceLeases, lease => Assert.True(lease.FencingToken > 0));
    }

    [Fact]
    public async Task VendorProductFailureDoesNotBecomeSystemExecutionFailure()
    {
        var fixture = new Fixture(new StationOperationDispatchResult(
            ExecutionStatus.Completed,
            ResultJudgement.Failed,
            null,
            1,
            1,
            0,
            Now.AddSeconds(2)));
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);

        var result = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.Equal(ExecutionStatus.Completed, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, result.Value.Run.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, result.Value.Run.Disposition);
        Assert.Null(result.Value.Run.FailureCode);
    }

    [Fact]
    public async Task DispatcherBoundaryFaultRequiresRecoveryAndIsNotAutomaticallyReplayed()
    {
        var fixture = new Fixture(new InvalidOperationException("Broker disconnected."));
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);

        var result = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, result.Value.Run.ControlState);
        Assert.Equal(ExecutionStatus.Running, result.Value.Run.ExecutionStatus);
        Assert.Single(fixture.Dispatcher.Requests);
        _ = await fixture.Runner.ExecuteAsync(request.RunId);
        Assert.Single(fixture.Dispatcher.Requests);
    }

    [Fact]
    public async Task ReconcileUsesObservedEvidenceWithoutRedispatchAndReleasesOnlyResolvedLease()
    {
        var fixture = new Fixture(new InvalidOperationException("Broker disconnected after actuation."));
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);
        var interrupted = await fixture.Runner.ExecuteAsync(request.RunId);
        var operation = Assert.Single(interrupted.Value.Run.Operations);
        Assert.Single(await fixture.Leases.ListAsync());
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            ProductionRecoveryDecisionKind.Reconcile,
            "operator.recovery",
            "Inspection confirms completed output.",
            "inspection:runner-recovery-001",
            Now,
            operationRunId: operation.OperationRunId,
            observedJudgement: ResultJudgement.Passed,
            observedOutputs: new Dictionary<string, ProductionContextValue>
            {
                ["inspection"] = new(ProductionContextValueKind.Text, "confirmed")
            });

        var result = await fixture.Coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Reconcile,
                decision.ActorId,
                decision.Reason,
                recoveryDecision: decision));

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, result.Value.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, result.Value.Judgement);
        Assert.Single(fixture.Dispatcher.Requests);
        Assert.Empty(await fixture.Leases.ListAsync());
        var persistedRevision = (await fixture.Repository.GetByIdAsync(request.RunId))!.Revision;
        var duplicate = await fixture.Coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Reconcile,
                decision.ActorId,
                decision.Reason,
                recoveryDecision: decision));
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(
            persistedRevision,
            (await fixture.Repository.GetByIdAsync(request.RunId))!.Revision);
    }

    [Fact]
    public async Task SafeStopIsNotPersistedUntilIndependentSafetyChannelAcknowledges()
    {
        var fixture = new Fixture(
            new StationOperationDispatchResult(
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                null,
                0,
                0,
                0,
                Now),
            new RejectingSafetyController());
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);

        var result = await fixture.Coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.SafeStop,
                "operator.safety",
                "Guard door opened."));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ExecutionStatus.Pending,
            (await fixture.Repository.GetByIdAsync(request.RunId))!.Run.ExecutionStatus);
    }

    [Fact]
    public async Task StopWaitsForCurrentOperationBoundaryAndThenEndsCanceled()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository();
        var clock = new FixedClock(Now);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: true);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = await coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Stop,
                "operator.stop",
                "Finish the current station operation, then stop."));

        Assert.True(stop.IsSuccess);
        Assert.Equal(ExecutionStatus.Running, stop.Value.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.StopRequested, stop.Value.ControlState);
        Assert.False(dispatcher.ExecutionToken.IsCancellationRequested);
        dispatcher.Release();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, result.Value.Run.Judgement);
        Assert.Equal(ProductDisposition.Held, result.Value.Run.Disposition);
    }

    [Fact]
    public async Task OperatorCancelPropagatesToActiveOperationAndPersistsCanceledAxes()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository();
        var clock = new FixedClock(Now);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: false);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Cancel,
                "operator.cancel",
                "Terminate the vendor execution now."))
            .AsTask();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var command = await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(command.IsSuccess);
        Assert.True(result.IsSuccess);
        Assert.True(dispatcher.ExecutionToken.IsCancellationRequested);
        Assert.Equal(ExecutionStatus.Canceled, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, result.Value.Run.Judgement);
        Assert.Equal(ProductDisposition.Held, result.Value.Run.Disposition);
        Assert.Equal("Runtime.ProductionRunCanceled", result.Value.Run.FailureCode);
    }

    [Fact]
    public async Task SafeStopCancelsActiveExecutionAndPreventsNextOperationDispatch()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository();
        var clock = new FixedClock(Now);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new SafeStopDispatcher(registry);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateSequentialRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var safeStopTask = coordinator.CommandAsync(
                request.RunId,
                new ProductionRunCommandRequest(
                    ProductionRunCommand.SafeStop,
                    "operator.safety",
                    "Guard opened; stop the station and terminate active execution."))
            .AsTask();
        await dispatcher.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.ReleaseCancellationResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var safeStop = await safeStopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(safeStop.IsSuccess);
        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, result.Value.Run.Judgement);
        Assert.Equal(ProductDisposition.Held, result.Value.Run.Disposition);
        Assert.Equal(["operation.main"], dispatcher.OperationIds);
    }

    [Fact]
    public async Task TwoProductionUnitsDispatchAtIndependentStationsBeforeEitherCompletes()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository();
        var clock = new FixedClock(Now);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new ConcurrentRunDispatcher();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var requestA = CreateSingleStationRequest("a");
        var requestB = CreateSingleStationRequest("b");
        await RegisterRequestUnitAsync(materials, requestA);
        await RegisterRequestUnitAsync(materials, requestB);
        Assert.True((await coordinator.SubmitAsync(requestA)).IsSuccess);
        Assert.True((await coordinator.SubmitAsync(requestB)).IsSuccess);

        var executionA = runner.ExecuteAsync(requestA.RunId).AsTask();
        var executionB = runner.ExecuteAsync(requestB.RunId).AsTask();
        await dispatcher.BothRunsStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(executionA.IsCompleted);
        Assert.False(executionB.IsCompleted);
        Assert.Contains(dispatcher.StationIds, id => id == "station.a");
        Assert.Contains(dispatcher.StationIds, id => id == "station.b");
        dispatcher.ReleaseRuns();
        var results = await Task.WhenAll(executionA, executionB)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result =>
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(ExecutionStatus.Completed, result.Value.Run.ExecutionStatus);
            Assert.Equal(ResultJudgement.Passed, result.Value.Run.Judgement);
        });
    }

    private static async ValueTask RegisterRequestUnitAsync(
        InMemoryProductionMaterialRepository materials,
        SubmitProductionRunRequest request)
    {
        var entryOperation = request.Operations.Single(operation => string.Equals(
            operation.Definition.OperationId,
            request.EntryOperationId,
            StringComparison.Ordinal));
        var unit = ProductionUnit.Register(
            request.ProductionUnitId,
            request.FrozenProductModelId,
            request.FrozenIdentityInputKey,
            $"UNIT-{request.ProductionUnitId.Value:N}",
            null,
            request.ActorId,
            Now.AddTicks(-1));
        Assert.True(await materials.TryAddAsync(unit));
        var materialService = new ProductionMaterialService(
            materials,
            new InMemoryProductionRunRepository(materials));
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            MaterialReference.ForProductionUnit(unit.Id),
            MaterialLocation.AtStation(
                request.ProductionLineDefinitionId,
                entryOperation.Definition.StationSystemId),
            request.ActorId,
            Now))).Succeeded);
    }

    private static SubmitProductionRunRequest CreateRequest()
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process-version.main"),
            []);
        var operation = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            process,
            [new ResourceRequirement(ResourceKind.Station, "station.main")]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.main",
            operation.Definition.OperationId,
            [operation],
            []);
    }

    private static SubmitProductionRunRequest CreateSingleStationRequest(string suffix)
    {
        var stationId = $"station.{suffix}";
        var operationId = $"operation.{suffix}";
        var operation = new OperationExecutionPlan(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId($"configuration.{suffix}"),
            new RecipeSnapshotId($"recipe.{suffix}"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId($"process.{suffix}"),
                new ProcessVersionId($"process-version.{suffix}"),
                []),
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            $"project.{suffix}",
            $"application.{suffix}",
            $"snapshot.{suffix}",
            $"topology.{suffix}",
            $"line.{suffix}",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.parallel",
            operationId,
            [operation],
            []);
    }

    private static SubmitProductionRunRequest CreateParallelRequest()
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.parallel"),
            new ProcessVersionId("process-version.parallel"),
            []);
        OperationExecutionPlan Operation(string operationId, string stationId) => new(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            process,
            [new ResourceRequirement(ResourceKind.Device, $"device.{operationId}")]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.parallel",
            "application.parallel",
            "snapshot.parallel",
            "topology.parallel",
            "line.parallel",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.parallel",
            "operation.entry",
            [
                Operation("operation.entry", "station.entry"),
                Operation("operation.left", "station.entry"),
                Operation("operation.right", "station.entry"),
                Operation("operation.join", "station.entry")
            ],
            [
                new RouteTransitionDefinition(
                    "fork.left",
                    "operation.entry",
                    "operation.left",
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "fork.right",
                    "operation.entry",
                    "operation.right",
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "join.left",
                    "operation.left",
                    "operation.join",
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "join.right",
                    "operation.right",
                    "operation.join",
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work")
            ]);
    }

    private static SubmitProductionRunRequest CreateSequentialRequest()
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.sequential"),
            new ProcessVersionId("process-version.sequential"),
            []);
        OperationExecutionPlan Operation(string operationId, string stationId) => new(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            process,
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.sequential",
            "application.sequential",
            "snapshot.sequential",
            "topology.sequential",
            "line.sequential",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.sequential",
            "operation.main",
            [
                Operation("operation.main", "station.main"),
                Operation("operation.next", "station.next")
            ],
            [new RouteTransitionDefinition(
                "route.next",
                "operation.main",
                "operation.next",
                RuntimeRouteTransitionKind.Sequence)]);
    }

    private sealed class Fixture
    {
        private readonly InMemoryProductionMaterialRepository _materials;
        private readonly InMemoryProductionRunRepository _repository;
        private readonly InMemoryResourceLeaseRepository _leases = new();
        private readonly FixedClock _clock = new(Now);
        private readonly InMemoryRuntimeDomainEventPublisher _publisher = new();

        public Fixture(
            StationOperationDispatchResult result,
            IStationSafetyController? safetyController = null)
            : this(new RecordingDispatcher(result), safetyController ?? new AcceptingSafetyController())
        {
        }

        public Fixture(Exception exception)
            : this(new RecordingDispatcher(exception), new AcceptingSafetyController())
        {
        }

        private Fixture(
            RecordingDispatcher dispatcher,
            IStationSafetyController safetyController)
        {
            _materials = new InMemoryProductionMaterialRepository();
            _repository = new InMemoryProductionRunRepository(_materials);
            Dispatcher = dispatcher;
            Coordinator = new ProductionRunCoordinator(
                _repository,
                _materials,
                _leases,
                safetyController,
                new AcceptingCanceler(),
                _publisher,
                _clock);
            Runner = new ProductionRunRunner(
                _repository,
                _repository,
                _leases,
                new ProductionOperationReadinessEvaluator(_materials),
                dispatcher,
                _publisher,
                new GuidRuntimeIdProvider(),
                _clock);
        }

        public RecordingDispatcher Dispatcher { get; }

        public InMemoryProductionRunRepository Repository => _repository;

        public InMemoryResourceLeaseRepository Leases => _leases;

        public ProductionRunCoordinator Coordinator { get; }

        public ProductionRunRunner Runner { get; }

        public async ValueTask<OpenLineOps.Application.Abstractions.Results.Result<ProductionRunSnapshot>>
            SubmitAsync(SubmitProductionRunRequest request)
        {
            var entryOperation = request.Operations.Single(operation => string.Equals(
                operation.Definition.OperationId,
                request.EntryOperationId,
                StringComparison.Ordinal));
            var unit = ProductionUnit.Register(
                request.ProductionUnitId,
                request.FrozenProductModelId,
                request.FrozenIdentityInputKey,
                $"UNIT-{request.ProductionUnitId.Value:N}",
                null,
                request.ActorId,
                Now.AddTicks(-1));
            Assert.True(await _materials.TryAddAsync(unit));
            var materialService = new ProductionMaterialService(_materials, _repository);
            Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
                MaterialReference.ForProductionUnit(unit.Id),
                MaterialLocation.AtStation(
                    request.ProductionLineDefinitionId,
                    entryOperation.Definition.StationSystemId),
                request.ActorId,
                Now))).Succeeded);
            return await Coordinator.SubmitAsync(request);
        }
    }

    private sealed class RecordingDispatcher : IStationOperationDispatcher
    {
        private readonly StationOperationDispatchResult? _result;
        private readonly Exception? _exception;

        public RecordingDispatcher(StationOperationDispatchResult result) => _result = result;

        public RecordingDispatcher(Exception exception) => _exception = exception;

        public List<StationOperationDispatchRequest> Requests { get; } = [];

        public ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _exception is null
                ? ValueTask.FromResult(_result!)
                : ValueTask.FromException<StationOperationDispatchResult>(_exception);
        }
    }

    private sealed class ConcurrentRunDispatcher : IStationOperationDispatcher
    {
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource _bothRunsStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRuns = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public Task BothRunsStarted => _bothRunsStarted.Task;

        public List<string> StationIds { get; } = [];

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StationIds.Add(request.Operation.Definition.StationSystemId);
            }

            if (Interlocked.Increment(ref _runCount) == 2)
            {
                _bothRunsStarted.TrySetResult();
            }

            await _releaseRuns.Task.WaitAsync(cancellationToken);

            return new StationOperationDispatchResult(
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                null,
                0,
                0,
                0,
                Now.AddSeconds(1));
        }

        public void ReleaseRuns() => _releaseRuns.TrySetResult();
    }

    private sealed class BoundaryDispatcher(
        InProcessStationOperationRegistry registry,
        bool completeOnRelease) : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public CancellationToken ExecutionToken { get; private set; }

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            using var execution = registry.Register(request.IdempotencyKey, cancellationToken);
            ExecutionToken = execution.CancellationToken;
            _started.TrySetResult();
            if (completeOnRelease)
            {
                await _release.Task.WaitAsync(execution.CancellationToken);
                return new StationOperationDispatchResult(
                    ExecutionStatus.Completed,
                    ResultJudgement.Passed,
                    null,
                    1,
                    1,
                    0,
                    Now.AddSeconds(1));
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
            {
                return new StationOperationDispatchResult(
                    ExecutionStatus.Canceled,
                    ResultJudgement.Aborted,
                    null,
                    0,
                    0,
                    0,
                    Now.AddSeconds(1),
                    "Runtime.OperationCanceled",
                    "Station operation cancellation reached the active execution token.");
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class SafeStopDispatcher(InProcessStationOperationRegistry registry)
        : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellationResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public List<string> OperationIds { get; } = [];

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            OperationIds.Add(request.Operation.Definition.OperationId);
            using var execution = registry.Register(request.IdempotencyKey, cancellationToken);
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _releaseCancellationResult.Task;
                return new StationOperationDispatchResult(
                    ExecutionStatus.Canceled,
                    ResultJudgement.Aborted,
                    null,
                    0,
                    0,
                    0,
                    Now.AddSeconds(1),
                    "Runtime.OperationCanceled",
                    "Safe Stop canceled the active station operation.");
            }
        }

        public void ReleaseCancellationResult() => _releaseCancellationResult.TrySetResult();
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

    private sealed class RejectingSafetyController : IStationSafetyController
    {
        public ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationSafetyResult.Failure(
                "Safety.AgentUnavailable",
                "Station Agent did not acknowledge Safe Stop."));
    }

    private sealed class AcceptingCanceler : IStationOperationCanceler
    {
        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationOperationCancellationResult.Success());
    }
}
