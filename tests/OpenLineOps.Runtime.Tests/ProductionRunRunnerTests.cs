using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
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

        var submitted = await fixture.Coordinator.SubmitAsync(request);
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
        Assert.True((await fixture.Coordinator.SubmitAsync(request)).IsSuccess);

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
        Assert.True((await fixture.Coordinator.SubmitAsync(request)).IsSuccess);

        var result = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, result.Value.Run.ControlState);
        Assert.Equal(ExecutionStatus.Running, result.Value.Run.ExecutionStatus);
        Assert.Single(fixture.Dispatcher.Requests);
        _ = await fixture.Runner.ExecuteAsync(request.RunId);
        Assert.Single(fixture.Dispatcher.Requests);
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
        Assert.True((await fixture.Coordinator.SubmitAsync(request)).IsSuccess);

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
    public async Task ParallelForkDispatchesIndependentStationsBeforeEitherBranchCompletes()
    {
        var repository = new InMemoryProductionRunRepository();
        var leases = new InMemoryResourceLeaseRepository();
        var clock = new FixedClock(Now);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new ParallelBranchDispatcher();
        var coordinator = new ProductionRunCoordinator(
            repository,
            leases,
            new AcceptingSafetyController(),
            publisher,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateParallelRequest();
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);

        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.BothBranchesStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(execution.IsCompleted);
        Assert.Contains(dispatcher.OperationIds, id => id == "operation.left");
        Assert.Contains(dispatcher.OperationIds, id => id == "operation.right");
        dispatcher.ReleaseBranches();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, result.Value.Run.Judgement);
        Assert.Equal(4, dispatcher.OperationIds.Count);
        Assert.Equal("operation.join", dispatcher.OperationIds[^1]);
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
            [
                new ResourceRequirement(ResourceKind.Station, "station.main"),
                new ResourceRequirement(ResourceKind.Slot, "slot.01"),
                new ResourceRequirement(ResourceKind.Device, "device.tester")
            ]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            "operator.main",
            operation.Definition.OperationId,
            [operation],
            [],
            "lot-001",
            "carrier-001");
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
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.parallel",
            "application.parallel",
            "snapshot.parallel",
            "topology.parallel",
            "line.parallel",
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-PARALLEL"),
            "operator.parallel",
            "operation.entry",
            [
                Operation("operation.entry", "station.entry"),
                Operation("operation.left", "station.left"),
                Operation("operation.right", "station.right"),
                Operation("operation.join", "station.join")
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

    private sealed class Fixture
    {
        private readonly InMemoryProductionRunRepository _repository = new();
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
            Dispatcher = dispatcher;
            Coordinator = new ProductionRunCoordinator(
                _repository,
                _leases,
                safetyController,
                _publisher,
                _clock);
            Runner = new ProductionRunRunner(
                _repository,
                _repository,
                _leases,
                dispatcher,
                _publisher,
                new GuidRuntimeIdProvider(),
                _clock);
        }

        public RecordingDispatcher Dispatcher { get; }

        public InMemoryProductionRunRepository Repository => _repository;

        public ProductionRunCoordinator Coordinator { get; }

        public ProductionRunRunner Runner { get; }
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

    private sealed class ParallelBranchDispatcher : IStationOperationDispatcher
    {
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource _bothBranchesStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBranches = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _branchCount;

        public Task BothBranchesStarted => _bothBranchesStarted.Task;

        public List<string> OperationIds { get; } = [];

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                OperationIds.Add(request.Operation.Definition.OperationId);
            }

            if (request.Operation.Definition.OperationId is "operation.left" or "operation.right")
            {
                if (Interlocked.Increment(ref _branchCount) == 2)
                {
                    _bothBranchesStarted.TrySetResult();
                }

                await _releaseBranches.Task.WaitAsync(cancellationToken);
            }

            return new StationOperationDispatchResult(
                ExecutionStatus.Completed,
                request.Operation.Definition.OperationId == "operation.entry"
                    ? ResultJudgement.NotApplicable
                    : ResultJudgement.Passed,
                null,
                0,
                0,
                0,
                Now.AddSeconds(1));
        }

        public void ReleaseBranches() => _releaseBranches.TrySetResult();
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
}
