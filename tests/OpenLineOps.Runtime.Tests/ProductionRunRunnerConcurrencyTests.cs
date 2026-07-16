using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Targets;
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
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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
    public async Task CancelAfterDurableStartBeforeInProcessRegistrationExecutesNoCommandAndEndsCanceled()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var canceler = new RecordingInProcessCanceler(
            new InProcessStationOperationCanceler(registry));
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AcceptingSafetyController(),
            canceler,
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateCommandRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var blockingRepository = new BlockAfterStartedOperationSaveRepository(repository);
        var sessions = new InMemoryRuntimeSessionRepository();
        var executor = new CountingCommandExecutor();
        var sessionRunner = new RuntimeSessionRunner(
            sessions,
            publisher,
            executor,
            new GuidRuntimeIdProvider(),
            clock);
        var dispatcher = new InProcessStationOperationDispatcher(
            sessionRunner,
            sessions,
            registry);
        var runner = new ProductionRunRunner(
            blockingRepository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);

        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await blockingRepository.StartOperationPersisted.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = coordinator.CommandAsync(
                request.RunId,
                new ProductionRunCommandRequest(
                    ProductionRunCommand.Cancel,
                    "operator.cancel",
                    "Cancel before the in-process dispatcher registers execution."))
            .AsTask();
        await canceler.Requested.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, executor.InvocationCount);

        blockingRepository.ReleaseSaveResult();
        var executed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var canceled = await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executed.IsSuccess);
        Assert.True(canceled.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, executed.Value.Run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.Active, executed.Value.Run.ControlState);
        Assert.Equal(ExecutionStatus.Canceled, canceled.Value.ExecutionStatus);
        Assert.NotEqual(
            ProductionRunControlState.RecoveryRequired,
            executed.Value.Run.ControlState);
        Assert.Equal(0, executor.InvocationCount);
        Assert.Empty(await leases.ListAsync());
        Assert.Null(await sessions.GetByIdAsync(
            Assert.Single(executed.Value.Run.Operations).RuntimeSessionId!.Value));
    }

    [Fact]
    public async Task SafeStopPreCancelsUnregisteredDispatchWhilePhysicalAcknowledgementIsDelayed()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var canceler = new RecordingInProcessCanceler(
            new InProcessStationOperationCanceler(registry));
        var safety = new DelayedSafetyController();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            safety,
            canceler,
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateCommandRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var blockingRepository = new BlockAfterStartedOperationSaveRepository(repository);
        var sessions = new InMemoryRuntimeSessionRepository();
        var executor = new CountingCommandExecutor();
        var sessionRunner = new RuntimeSessionRunner(
            sessions,
            publisher,
            executor,
            new GuidRuntimeIdProvider(),
            clock);
        var dispatcher = new InProcessStationOperationDispatcher(
            sessionRunner,
            sessions,
            registry);
        var runner = new ProductionRunRunner(
            blockingRepository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);

        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await blockingRepository.StartOperationPersisted.WaitAsync(TimeSpan.FromSeconds(5));
        var safeStopTask = coordinator.CommandAsync(
                request.RunId,
                new ProductionRunCommandRequest(
                    ProductionRunCommand.SafeStop,
                    "operator.safety",
                    "Pre-cancel dispatch before the physical Safe Stop acknowledgement."))
            .AsTask();
        await canceler.Requested.WaitAsync(TimeSpan.FromSeconds(5));
        await safety.Requested.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(safeStopTask.IsCompleted);
        Assert.Equal(0, executor.InvocationCount);
        blockingRepository.ReleaseSaveResult();

        var executed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(executed.IsSuccess);
        Assert.Equal(ExecutionStatus.Running, executed.Value.Run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.StopRequested, executed.Value.Run.ControlState);
        Assert.Null(executed.Value.Run.SafeStopAcknowledgedAtUtc);
        Assert.Equal(
            ExecutionStatus.Canceled,
            Assert.Single(executed.Value.Run.Operations).ExecutionStatus);
        Assert.False(safeStopTask.IsCompleted);
        Assert.Equal(0, executor.InvocationCount);
        Assert.Empty(await leases.ListAsync());
        Assert.Null(await sessions.GetByIdAsync(
            Assert.Single(executed.Value.Run.Operations).RuntimeSessionId!.Value));

        safety.Acknowledge();
        var safeStopped = await safeStopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(safeStopped.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, safeStopped.Value.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.SafeStopped, safeStopped.Value.ControlState);
        Assert.NotNull(safeStopped.Value.SafeStopAcknowledgedAtUtc);
        Assert.Equal(0, executor.InvocationCount);
    }

    [Fact]
    public async Task SafeStopStillRequestsPhysicalSafetyWhenExecutionCancellationIsRejected()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var safety = new RecordingSafetyController();
        var canceler = new RejectingSafeStopCanceler();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            safety,
            canceler,
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);

        var entry = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.True(entry.Run.Start(Now).Succeeded);
        await repository.SaveAsync(entry.Run, entry.Revision);
        entry = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        var operation = Assert.Single(entry.Run.Operations);
        var acquired = await leases.TryAcquireAsync(
            request.RunId,
            operation.OperationRunId,
            operation.ResourceRequirements,
            TimeSpan.FromMinutes(1));
        Assert.NotNull(acquired);
        Assert.True(entry.Run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            acquired,
            Now).Succeeded);
        await repository.SaveAsync(entry.Run, entry.Revision);

        var result = await coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.SafeStop,
                "operator.safety",
                "Physical safety must still run when execution cancellation is rejected."));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Station.SafeStopCancelRejected", result.Error.Code);
        Assert.Equal(1, canceler.InvocationCount);
        Assert.Equal(1, safety.RequestCount);
        var recovery = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.False(recovery.Run.IsTerminal);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, recovery.Run.ControlState);
        Assert.NotNull(recovery.Run.SafeStopAcknowledgedAtUtc);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await leases.ListAsync()).ExpiresAtUtc);
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
            new InMemoryProductionRunSafetyTransitionStore(repository, innerLeases),
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
            innerLeases,
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            clock);
        var runnerB = CreateRunner(
            repository,
            repository,
            leases,
            innerLeases,
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
            ProductionRunExecutionEvidenceTestFactory.Create(
                dispatched,
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                Now.AddSeconds(1)),
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
    public async Task UncertainParallelRunnerHoldsEveryDurableRunningBranchAcrossRunners()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var innerLeases = new InMemoryResourceLeaseRepository(clock);
        var leases = new CoordinatedParallelLeaseRepository(innerLeases);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new ParallelUncertainDispatcher();
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, innerLeases),
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var request = CreateParallelRequest();
        await RegisterRequestUnitAsync(materials, repository, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var runnerA = CreateRunner(
            repository,
            repository,
            leases,
            innerLeases,
            new AlwaysReadyProductionOperationReadiness(),
            dispatcher,
            publisher,
            clock);
        var runnerB = CreateRunner(
            repository,
            repository,
            leases,
            innerLeases,
            new AlwaysReadyProductionOperationReadiness(),
            dispatcher,
            publisher,
            clock);

        var executionA = runnerA.ExecuteAsync(request.RunId).AsTask();
        var firstRightOrCompletion = await Task.WhenAny(
            leases.FirstRightAttempted,
            executionA,
            Task.Delay(TimeSpan.FromSeconds(5)));
        if (ReferenceEquals(firstRightOrCompletion, executionA))
        {
            var early = await executionA;
            var operationDetail = early.IsSuccess
                ? string.Join(",", early.Value.Run.Operations.Select(operation =>
                    $"{operation.OperationRunId}:{operation.ExecutionStatus}"))
                : "no snapshot";
            throw new InvalidOperationException(
                $"First Runner completed before the right lease attempt: success={early.IsSuccess}, "
                + $"detail={(early.IsSuccess ? early.Value.Run.ControlState : early.Error.Code)}, "
                + $"operations={operationDetail}.");
        }

        Assert.Same(leases.FirstRightAttempted, firstRightOrCompletion);
        var executionB = runnerB.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.RightStarted.WaitAsync(TimeSpan.FromSeconds(5));
        leases.ReleaseFirstRightAttempt();

        var uncertain = await executionA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(uncertain.IsSuccess);
        Assert.Equal(
            ProductionRunControlState.RecoveryRequired,
            uncertain.Value.Run.ControlState);
        var durable = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        var running = durable.Run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["operation.left@0001", "operation.right@0001"],
            running.Select(static operation => operation.OperationRunId).ToArray());
        var held = (await innerLeases.ListAsync())
            .OrderBy(static lease => lease.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, held.Length);
        Assert.All(held, static lease =>
            Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));

        dispatcher.CompleteRight();
        var observer = await executionB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observer.IsSuccess);
        Assert.Equal(
            ProductionRunControlState.RecoveryRequired,
            observer.Value.Run.ControlState);
        var settled = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.Equal(
            ExecutionStatus.Running,
            settled.Run.Operations.Single(operation => string.Equals(
                operation.OperationRunId,
                "operation.left@0001",
                StringComparison.Ordinal)).ExecutionStatus);
        var completedRight = settled.Run.Operations.Single(operation => string.Equals(
            operation.OperationRunId,
            "operation.right@0001",
            StringComparison.Ordinal));
        Assert.Equal(ExecutionStatus.Completed, completedRight.ExecutionStatus);
        Assert.NotNull(completedRight.ExecutionEvidence);
        var remaining = Assert.Single(await innerLeases.ListAsync());
        Assert.Equal("operation.left@0001", remaining.OperationRunId);
        Assert.Equal(DateTimeOffset.MaxValue, remaining.ExpiresAtUtc);
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
            new InMemoryProductionRunSafetyTransitionStore(repository, innerLeases),
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
            innerLeases,
            readiness,
            dispatcher,
            publisher,
            clock);
        var runnerB = CreateRunner(
            repository,
            repository,
            leases,
            innerLeases,
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
        InMemoryProductionRunRepository repository,
        IProductionRunExecutionPlanRepository plans,
        IResourceLeaseRepository leases,
        InMemoryResourceLeaseRepository safetyLeases,
        IProductionOperationReadiness readiness,
        IStationOperationDispatcher dispatcher,
        IRuntimeDomainEventPublisher publisher,
        IClock clock) =>
        new(
            repository,
            plans,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, safetyLeases),
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
            [],
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

    private static SubmitProductionRunRequest CreateCommandRequest()
    {
        const string operationId = "operation.pre-dispatch-cancel";
        const string stationId = "station.pre-dispatch-cancel";
        var capability = new RuntimeCapabilityId("capability.pre-dispatch-cancel");
        var nodeId = new RuntimeNodeId("node.pre-dispatch-cancel");
        var operation = new OperationExecutionPlan(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId("configuration.pre-dispatch-cancel"),
            new RecipeSnapshotId("recipe.pre-dispatch-cancel"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process.pre-dispatch-cancel"),
                new ProcessVersionId("process-version.pre-dispatch-cancel"),
                [
                    new ExecutableRuntimeNode(
                        nodeId,
                        "Must not execute",
                        capability,
                        "execute",
                        TimeSpan.FromSeconds(5),
                        null,
                        new RuntimeActionId("action.pre-dispatch-cancel"),
                        new RuntimeTargetReference(
                            RuntimeTargetKinds.Capability,
                            capability.Value))
                ]),
            [],
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.pre-dispatch-cancel",
            "application.pre-dispatch-cancel",
            "snapshot.pre-dispatch-cancel",
            "topology.pre-dispatch-cancel",
            "line.pre-dispatch-cancel",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.pre-dispatch-cancel",
            operationId,
            [operation],
            [new RouteTransitionDefinition(
                "route.pre-dispatch-cancel.completed",
                operationId,
                targetOperationId: null,
                RuntimeRouteTransitionKind.Sequence,
                terminalDisposition: ProductDisposition.Completed)]);
    }

    private static SubmitProductionRunRequest CreateParallelRequest()
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.parallel-concurrency"),
            new ProcessVersionId("process-version.parallel-concurrency"),
            []);
        OperationExecutionPlan Operation(string operationId, string stationId) => new(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            process,
            [],
            [new ResourceRequirement(ResourceKind.Station, stationId)]);
        var operations = new[]
        {
            Operation("operation.entry", "station.entry"),
            Operation("operation.left", "station.left"),
            Operation("operation.right", "station.right"),
            Operation("operation.join", "station.join")
        };
        return new SubmitProductionRunRequest(
            ProductionRunId.New(),
            "project.parallel-concurrency",
            "application.parallel-concurrency",
            "snapshot.parallel-concurrency",
            "topology.parallel-concurrency",
            "line.parallel-concurrency",
            ProductionUnitId.New(),
            "product.board",
            "serialNumber",
            "operator.parallel-concurrency",
            "operation.entry",
            operations,
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
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "route.join.completed",
                    "operation.join",
                    targetOperationId: null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            ]);
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

        public ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
            ProductionRunTerminalPageRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ListTerminalAsync(request, cancellationToken);

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

    private sealed class BlockAfterStartedOperationSaveRepository(
        IProductionRunRepository inner) : IProductionRunRepository
    {
        private readonly TaskCompletionSource _startOperationPersisted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSaveResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task StartOperationPersisted => _startOperationPersisted.Task;

        public ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(run, executionPlan, admission, cancellationToken);

        public async ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            var revision = await inner.SaveAsync(run, expectedRevision, cancellationToken);
            if (run.Operations.Any(static operation =>
                    operation.ExecutionStatus == ExecutionStatus.Running)
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _startOperationPersisted.TrySetResult();
                await _releaseSaveResult.Task;
            }

            return revision;
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

        public ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
            ProductionRunTerminalPageRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ListTerminalAsync(request, cancellationToken);

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

        public void ReleaseSaveResult() => _releaseSaveResult.TrySetResult();
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

    private sealed class ParallelUncertainDispatcher : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _rightStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeRight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RightStarted => _rightStarted.Task;

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var operationId = request.Operation.Definition.OperationId;
            if (string.Equals(operationId, "operation.left", StringComparison.Ordinal))
            {
                throw new IOException("The left Station result boundary became uncertain.");
            }

            if (string.Equals(operationId, "operation.right", StringComparison.Ordinal))
            {
                _rightStarted.TrySetResult();
                await _completeRight.Task.WaitAsync(cancellationToken);
            }

            var completedAtUtc = Now;
            var judgement = string.Equals(operationId, "operation.entry", StringComparison.Ordinal)
                ? ResultJudgement.NotApplicable
                : ResultJudgement.Passed;
            return new StationOperationDispatchResult(
                ExecutionStatus.Completed,
                judgement,
                ProductionRunExecutionEvidenceTestFactory.Create(
                    request,
                    ExecutionStatus.Completed,
                    judgement,
                    completedAtUtc),
                null,
                0,
                0,
                0,
                completedAtUtc);
        }

        public void CompleteRight() => _completeRight.TrySetResult();
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

    private sealed class AlwaysReadyProductionOperationReadiness : IProductionOperationReadiness
    {
        public ValueTask<ProductionOperationReadiness> EvaluateAsync(
            ProductionRunSnapshot run,
            OperationRunSnapshot operation,
            CancellationToken cancellationToken = default)
        {
            _ = run;
            _ = operation;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ProductionOperationReadiness.Ready);
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
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            inner.HoldForRecoveryAsync(runId, leaseHolds, cancellationToken);

        public ValueTask ReleaseRecoveryHoldAsync(
            ProductionRunId runId,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseRecoveryHoldAsync(runId, leaseHolds, cancellationToken);
    }

    private sealed class CoordinatedParallelLeaseRepository(
        InMemoryResourceLeaseRepository inner) : IResourceLeaseRepository
    {
        private readonly TaskCompletionSource _firstRightAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRightAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rightAttemptCount;

        public Task FirstRightAttempted => _firstRightAttempted.Task;

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
            if (string.Equals(
                operationRunId,
                "operation.right@0001",
                StringComparison.Ordinal)
                && Interlocked.Increment(ref _rightAttemptCount) == 1)
            {
                _firstRightAttempted.TrySetResult();
                await _releaseFirstRightAttempt.Task.WaitAsync(cancellationToken);
            }

            return await inner.TryAcquireAsync(
                runId,
                operationRunId,
                resources,
                duration,
                cancellationToken);
        }

        public ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
            CancellationToken cancellationToken = default) =>
            inner.ValidateCurrentAsync(runId, operationRunId, evidence, cancellationToken);

        public ValueTask ReleaseAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(runId, operationRunId, claims, cancellationToken);

        public ValueTask HoldForRecoveryAsync(
            ProductionRunId runId,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            inner.HoldForRecoveryAsync(runId, leaseHolds, cancellationToken);

        public ValueTask ReleaseRecoveryHoldAsync(
            ProductionRunId runId,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseRecoveryHoldAsync(runId, leaseHolds, cancellationToken);

        public void ReleaseFirstRightAttempt() => _releaseFirstRightAttempt.TrySetResult();
    }

    private sealed class AcceptingSafetyController : IStationSafetyController
    {
        public ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationSafetyResult.Success());
    }

    private sealed class DelayedSafetyController : IStationSafetyController
    {
        private readonly TaskCompletionSource<StationSafetyRequest> _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StationSafetyRequest> Requested => _requested.Task;

        public async ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default)
        {
            _requested.TrySetResult(request);
            await _acknowledged.Task.WaitAsync(cancellationToken);
            return StationSafetyResult.Success(request.RequestedAtUtc);
        }

        public void Acknowledge() => _acknowledged.TrySetResult();
    }

    private sealed class RecordingSafetyController : IStationSafetyController
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            return ValueTask.FromResult(StationSafetyResult.Success(request.RequestedAtUtc));
        }
    }

    private sealed class AcceptingCanceler : IStationOperationCanceler
    {
        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationOperationCancellationResult.Success());
    }

    private sealed class RejectingSafeStopCanceler : IStationOperationCanceler
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(StationOperationCancellationResult.Failure(
                "Station.SafeStopCancelRejected",
                "Station rejected active execution cancellation."));
        }
    }

    private sealed class RecordingInProcessCanceler(IStationOperationCanceler inner)
        : IStationOperationCanceler
    {
        private readonly TaskCompletionSource _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Requested => _requested.Task;

        public async ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.CancelAsync(request, cancellationToken);
            _requested.TrySetResult();
            return result;
        }
    }

    private sealed class CountingCommandExecutor : IRuntimeCommandExecutor
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
