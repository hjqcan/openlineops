using System.Security.Cryptography;
using System.Text;
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

public sealed class ProductionRunRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SubmissionIsAsynchronousAndRunnerDispatchesPersistedFencedStationJob()
    {
        var fixture = new Fixture(new StationOperationResultSpec(
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
    public async Task CompletedRunSubmissionRetryReturnsExistingRunWithoutReAdmissionOrRedispatch()
    {
        var fixture = new Fixture(new StationOperationResultSpec(
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            Now.AddSeconds(2)));
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);
        var completed = await fixture.Runner.ExecuteAsync(request.RunId);
        Assert.True(completed.IsSuccess);
        var completedRevision = (await fixture.Repository.GetByIdAsync(request.RunId))!.Revision;

        var retry = await fixture.Coordinator.SubmitAsync(request);
        var retryExecution = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(retry.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, retry.Value.ExecutionStatus);
        Assert.True(retryExecution.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, retryExecution.Value.Run.ExecutionStatus);
        Assert.Equal(completedRevision, (await fixture.Repository.GetByIdAsync(request.RunId))!.Revision);
        Assert.Single(fixture.Dispatcher.Requests);

        var mismatchedTopology = new SubmitProductionRunRequest(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            "topology.other",
            request.ProductionLineDefinitionId,
            request.ProductionUnitId,
            request.FrozenProductModelId,
            request.FrozenIdentityInputKey,
            request.ActorId,
            request.EntryOperationId,
            request.Operations,
            request.RouteTransitions);
        var rejected = await fixture.Coordinator.SubmitAsync(mismatchedTopology);
        Assert.True(rejected.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunIdentityMismatch", rejected.Error.Code);
    }

    [Fact]
    public async Task VendorProductFailureDoesNotBecomeSystemExecutionFailure()
    {
        var fixture = new Fixture(new StationOperationResultSpec(
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
    public async Task CompletedOperationOutputIsMappedIntoNextStationDispatchInputs()
    {
        var fixture = new Fixture(new StationOperationResultSpec(
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            new Dictionary<string, ProductionContextValue>
            {
                ["measurement.voltage"] = new(ProductionContextValueKind.FixedPoint, "3.3")
            },
            1,
            1,
            0,
            Now));
        var request = CreateSequentialRequest(
            [
                new OperationInputMappingPlan(
                    "inspection.voltage",
                    "operation.main",
                    "measurement.voltage",
                    ProductionContextValueKind.FixedPoint)
            ],
            nextStationSystemId: "station.main");
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);

        Assert.True((await fixture.Runner.ExecuteAsync(request.RunId)).IsSuccess);
        var result = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, result.Value.Run.ExecutionStatus);
        Assert.Equal(2, fixture.Dispatcher.Requests.Count);
        var next = fixture.Dispatcher.Requests.Single(dispatch => string.Equals(
            dispatch.Operation.Definition.OperationId,
            "operation.next",
            StringComparison.Ordinal));
        Assert.Equal(
            new ProductionContextValue(ProductionContextValueKind.FixedPoint, "3.3"),
            next.Inputs["inspection.voltage"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongKindProductionOutputFailsBeforeNextStationDispatch(
        bool wrongKind)
    {
        var sourceOutputs = wrongKind
            ? new Dictionary<string, ProductionContextValue>
            {
                ["measurement.voltage"] = new(ProductionContextValueKind.Text, "3.30")
            }
            : new Dictionary<string, ProductionContextValue>();
        var fixture = new Fixture(new StationOperationResultSpec(
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            sourceOutputs,
            1,
            1,
            0,
            Now));
        var request = CreateSequentialRequest(
            [
                new OperationInputMappingPlan(
                    "inspection.voltage",
                    "operation.main",
                    "measurement.voltage",
                    ProductionContextValueKind.FixedPoint)
            ],
            nextStationSystemId: "station.main");
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);

        Assert.True((await fixture.Runner.ExecuteAsync(request.RunId)).IsSuccess);
        var result = await fixture.Runner.ExecuteAsync(request.RunId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Failed, result.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, result.Value.Run.Judgement);
        Assert.Single(fixture.Dispatcher.Requests);
        var target = result.Value.Run.Operations.Single(operation => string.Equals(
            operation.Definition.OperationId,
            "operation.next",
            StringComparison.Ordinal));
        Assert.Equal(ExecutionStatus.Failed, target.ExecutionStatus);
        Assert.Equal("Runtime.ProductionContextInputInvalid", target.FailureCode);
        Assert.Equal(1, target.IncidentCount);
        Assert.NotNull(target.ExecutionEvidence);
        Assert.Equal("Runtime.ProductionContextInputInvalid", Assert.Single(
            target.ExecutionEvidence.Incidents).Code);
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
    public async Task SafeStopBeforeDispatchPersistsAsNoOpWithoutCallingPhysicalSafety()
    {
        var fixture = new Fixture(
            new StationOperationResultSpec(
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

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, result.Value.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.SafeStopped, result.Value.ControlState);
        Assert.NotNull(result.Value.SafeStopAcknowledgedAtUtc);
    }

    [Fact]
    public async Task SafeStopRejectsTerminalRunWithoutDispatchingStationSafety()
    {
        var safety = new RecordingSafetyController();
        var fixture = new Fixture(
            new StationOperationResultSpec(
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                null,
                0,
                0,
                0,
                Now),
            safety);
        var request = CreateRequest();
        Assert.True((await fixture.SubmitAsync(request)).IsSuccess);
        Assert.True((await fixture.Runner.ExecuteAsync(request.RunId)).IsSuccess);

        var result = await fixture.Coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.SafeStop,
                "operator.safety",
                "Late duplicate Safe Stop."));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunSafeStopRejected", result.Error.Code);
        Assert.Equal(0, safety.RequestCount);
    }

    [Fact]
    public async Task SubmissionCompletesDurablyWhenCallerCancelsAfterAtomicAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(
            new StationOperationResultSpec(
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                null,
                0,
                0,
                0,
                Now),
            repositoryDecorator: repository =>
                new CancelAfterAdmissionRepository(repository, cancellation));
        var request = CreateRequest();

        var result = await fixture.SubmitAsync(request, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.IsSuccess);
        Assert.NotNull(await fixture.Repository.GetByIdAsync(request.RunId));
    }

    [Fact]
    public async Task StopWaitsForCurrentOperationBoundaryAndThenEndsCanceled()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: true);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: false);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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
        var canceledOperation = Assert.Single(result.Value.Run.Operations);
        Assert.Equal(ExecutionStatus.Canceled, canceledOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, canceledOperation.Judgement);
        Assert.Equal(0, canceledOperation.CompletedStepCount);
        Assert.Equal(1, canceledOperation.CommandCount);
        Assert.Equal(0, canceledOperation.IncidentCount);
    }

    [Fact]
    public async Task OperatorCancelBarrierWinsNaturalCompletionAndPreventsNextOperationDispatch()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new NaturalCompletionDuringCancelDispatcher();
        var canceler = new BlockingStationOperationCanceler();
        var safetyTransitions = new InMemoryProductionRunSafetyTransitionStore(repository, leases);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            safetyTransitions,
            new AcceptingSafetyController(),
            canceler,
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            safetyTransitions,
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

        var cancellation = coordinator.CommandAsync(
                request.RunId,
                new ProductionRunCommandRequest(
                    ProductionRunCommand.Cancel,
                    "operator.cancel",
                    "Cancel while the station is about to report natural completion."))
            .AsTask();
        await canceler.Requested.WaitAsync(TimeSpan.FromSeconds(5));
        var barrier = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.Equal(ProductionRunControlState.StopRequested, barrier.Run.ControlState);
        Assert.Equal("Runtime.ProductionRunCancelRequested", barrier.Run.FailureCode);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await leases.ListAsync()).ExpiresAtUtc);

        dispatcher.ReleaseNaturalCompletion();
        var executed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(executed.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, executed.Value.Run.ExecutionStatus);
        Assert.Equal(["operation.main"], dispatcher.OperationIds);

        canceler.ReleaseAcknowledgement();
        var command = await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(command.IsSuccess);
        Assert.Equal(ExecutionStatus.Canceled, command.Value.ExecutionStatus);
        Assert.Empty(await leases.ListAsync());
    }

    [Fact]
    public async Task ActiveScrapWaitsForCanceledEvidenceAndRestartRetryDoesNotRepeatHardwareCancel()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new ScrapDispatcher(registry);
        var canceler = new IdempotentStationCanceler(registry);
        var coordinator = CreateCoordinator(
            repository,
            materials,
            leases,
            publisher,
            canceler,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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

        var scrapRequest = new ProductionRunCommandRequest(
            ProductionRunCommand.Scrap,
            "operator.scrap",
            "Board was physically damaged during execution.");
        var firstScrap = coordinator.CommandAsync(request.RunId, scrapRequest).AsTask();
        await dispatcher.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));

        var barrier = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.False(barrier.Run.IsTerminal);
        Assert.Equal(ProductionRunControlState.StopRequested, barrier.Run.ControlState);
        Assert.Equal("operator.scrap", barrier.Run.ScrapRequestedBy);
        Assert.Equal("Board was physically damaged during execution.", barrier.Run.ScrapReason);
        Assert.Equal(Now, barrier.Run.ScrapRequestedAtUtc);
        Assert.Equal(ExecutionStatus.Running, Assert.Single(barrier.Run.Operations).ExecutionStatus);
        var heldLease = Assert.Single(await leases.ListAsync());
        Assert.Equal(DateTimeOffset.MaxValue, heldLease.ExpiresAtUtc);
        Assert.False(firstScrap.IsCompleted);

        var restartedCoordinator = CreateCoordinator(
            repository,
            materials,
            leases,
            publisher,
            canceler,
            clock);
        var replayedScrap = restartedCoordinator.CommandAsync(request.RunId, scrapRequest).AsTask();
        await canceler.SecondTransportRequest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, canceler.TransportRequestCount);
        Assert.Equal(1, canceler.HardwareCancellationCount);

        dispatcher.ReleaseCancellationResult();
        var executed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var firstResult = await firstScrap.WaitAsync(TimeSpan.FromSeconds(5));
        var replayResult = await replayedScrap.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executed.IsSuccess);
        Assert.True(firstResult.IsSuccess);
        Assert.True(replayResult.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, executed.Value.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, executed.Value.Run.Judgement);
        Assert.Equal(ProductDisposition.Scrapped, executed.Value.Run.Disposition);
        var canceled = Assert.Single(executed.Value.Run.Operations);
        Assert.Equal(ExecutionStatus.Canceled, canceled.ExecutionStatus);
        var evidence = Assert.IsType<OperationExecutionEvidence>(canceled.ExecutionEvidence);
        Assert.Single(evidence.Artifacts);
        Assert.Equal("scrap-result.json", Assert.Single(evidence.Artifacts).Name);
        Assert.Empty(await leases.ListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrapCancelTransportUncertaintyRequiresRecoveryAndKeepsLease(
        bool throws)
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: false);
        var rejectingCanceler = new RejectingCanceler();
        var throwingCanceler = new ThrowingCanceler();
        IStationOperationCanceler canceler = throws
            ? throwingCanceler
            : rejectingCanceler;
        var coordinator = CreateCoordinator(
            repository,
            materials,
            leases,
            publisher,
            canceler,
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        using var executionCancellation = new CancellationTokenSource();
        var execution = runner.ExecuteAsync(request.RunId, executionCancellation.Token).AsTask();
        await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var scrap = await coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Scrap,
                "operator.scrap",
                "Scrap requires a confirmed process-tree termination."));

        Assert.True(scrap.IsFailure);
        Assert.Equal(
            throws
                ? "Conflict.Runtime.ScrapExecutionCancelFailed"
                : "Conflict.Station.CancelRejected",
            scrap.Error.Code);
        var recovery = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.False(recovery.Run.IsTerminal);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, recovery.Run.ControlState);
        Assert.Equal(ProductDisposition.Held, recovery.Run.Disposition);
        Assert.NotNull(recovery.Run.ScrapRequestedAtUtc);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await leases.ListAsync()).ExpiresAtUtc);

        var retry = await coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Scrap,
                "operator.scrap",
                "Scrap requires a confirmed process-tree termination."));
        Assert.True(retry.IsFailure);
        Assert.Equal("Conflict.Runtime.RecoveryDecisionRequired", retry.Error.Code);
        Assert.Equal(
            1,
            throws ? throwingCanceler.InvocationCount : rejectingCanceler.InvocationCount);

        executionCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await leases.ListAsync()).ExpiresAtUtc);
    }

    [Fact]
    public async Task AcceptedScrapWithoutTerminalEvidenceReturnsPendingAndKeepsRunOpen()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new BoundaryDispatcher(registry, completeOnRelease: false);
        var coordinator = CreateCoordinator(
            repository,
            materials,
            leases,
            publisher,
            new AcceptingCanceler(),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new ProductionOperationReadinessEvaluator(materials),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        using var executionCancellation = new CancellationTokenSource();
        var execution = runner.ExecuteAsync(request.RunId, executionCancellation.Token).AsTask();
        await dispatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var scrap = await coordinator.CommandAsync(
            request.RunId,
            new ProductionRunCommandRequest(
                ProductionRunCommand.Scrap,
                "operator.scrap",
                "Wait for real Station cancellation evidence."));

        Assert.True(scrap.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunScrapPending", scrap.Error.Code);
        var pending = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.False(pending.Run.IsTerminal);
        Assert.Equal(ProductionRunControlState.StopRequested, pending.Run.ControlState);
        Assert.Equal(ExecutionStatus.Running, Assert.Single(pending.Run.Operations).ExecutionStatus);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await leases.ListAsync()).ExpiresAtUtc);

        executionCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ParallelActiveScrapSettlesEveryCanceledBranchBeforeTerminalAndReleasesAllLeases()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new ParallelScrapDispatcher(registry);
        var coordinator = CreateCoordinator(
            repository,
            materials,
            leases,
            publisher,
            new InProcessStationOperationCanceler(registry),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AlwaysReadyOperationReadiness(),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateParallelRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);
        var execution = runner.ExecuteAsync(request.RunId).AsTask();
        await dispatcher.BothBranchesArmed.WaitAsync(TimeSpan.FromSeconds(5));

        var scrapTask = coordinator.CommandAsync(
                request.RunId,
                new ProductionRunCommandRequest(
                    ProductionRunCommand.Scrap,
                    "operator.scrap",
                    "Scrap both active parallel stations."))
            .AsTask();
        await dispatcher.BothCancellationsObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var settling = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId));
        Assert.False(settling.Run.IsTerminal);
        Assert.Equal(
            2,
            settling.Run.Operations.Count(static operation =>
                operation.ExecutionStatus == ExecutionStatus.Running));
        Assert.All(await leases.ListAsync(), lease =>
            Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));

        dispatcher.ReleaseCancellationResults();
        var executed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var scrap = await scrapTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executed.IsSuccess);
        Assert.True(scrap.IsSuccess);
        Assert.Equal(ProductDisposition.Scrapped, executed.Value.Run.Disposition);
        var branches = executed.Value.Run.Operations
            .Where(static operation => operation.Definition.OperationId is
                "operation.left" or "operation.right")
            .ToArray();
        Assert.Equal(2, branches.Length);
        Assert.All(branches, branch =>
        {
            Assert.Equal(ExecutionStatus.Canceled, branch.ExecutionStatus);
            Assert.NotNull(branch.ExecutionEvidence);
            Assert.Single(branch.ExecutionEvidence!.Artifacts);
        });
        Assert.DoesNotContain(
            executed.Value.Run.Operations,
            static operation => operation.Definition.OperationId == "operation.join");
        Assert.Empty(await leases.ListAsync());
    }

    [Fact]
    public async Task SafeStopCancelsActiveExecutionAndPreventsNextOperationDispatch()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var registry = new InProcessStationOperationRegistry();
        var dispatcher = new SafeStopDispatcher(registry);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AcceptingSafetyController(),
            new InProcessStationOperationCanceler(registry),
            publisher,
            new ProductionRunCreatedOutboxDispatcher(repository, publisher),
            clock);
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var dispatcher = new ConcurrentRunDispatcher();
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
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
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

    [Theory]
    [InlineData("operation.left", "operation.right")]
    [InlineData("operation.right", "operation.left")]
    public async Task ParallelDispatchWaveFreezesCompletedAndFailedEvidenceBeforeTerminalFailure(
        string failedOperationId,
        string firstCompletedOperationId)
    {
        var specifications = new Dictionary<string, StationOperationResultSpec>(StringComparer.Ordinal)
        {
            ["operation.entry"] = PassedResult("operation.entry", Now, '1'),
            ["operation.left"] = failedOperationId == "operation.left"
                ? FailedResult("operation.left", Now, 'a')
                : PassedResult("operation.left", Now, 'b'),
            ["operation.right"] = failedOperationId == "operation.right"
                ? FailedResult("operation.right", Now, 'c')
                : PassedResult("operation.right", Now, 'd')
        };
        var dispatcher = new DispatchWaveDispatcher(specifications, firstCompletedOperationId);

        var execution = await ExecuteParallelWaveAsync(dispatcher);

        Assert.Equal(ExecutionStatus.Failed, execution.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, execution.Run.Judgement);
        Assert.Equal(ProductDisposition.Held, execution.Run.Disposition);
        Assert.Equal($"Runtime.{failedOperationId}.Failed", execution.Run.FailureCode);
        var branches = execution.Run.Operations
            .Where(operation => operation.Definition.OperationId is "operation.left" or "operation.right")
            .OrderBy(operation => operation.Definition.OperationId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Equal(
            failedOperationId,
            branches.Single(operation => operation.ExecutionStatus == ExecutionStatus.Failed)
                .Definition.OperationId);
        Assert.Equal(
            failedOperationId == "operation.left" ? "operation.right" : "operation.left",
            branches.Single(operation => operation.ExecutionStatus == ExecutionStatus.Completed)
                .Definition.OperationId);
        Assert.All(branches, AssertFrozenBranchEvidence);
        Assert.DoesNotContain(execution.Run.RouteDecisions, decision =>
            decision.TransitionId is "join.left" or "join.right");
        Assert.Empty(await execution.Leases.ListAsync());

        var terminalPage = await execution.Repository.ListTerminalAsync(
            new ProductionRunTerminalPageRequest(10));
        var terminalEvidence = Assert.Single(terminalPage.Items);
        Assert.Equal(
            branches.Select(operation => operation.OperationRunId),
            terminalEvidence.Run.Operations
                .Where(operation => operation.Definition.OperationId is "operation.left" or "operation.right")
                .OrderBy(operation => operation.Definition.OperationId, StringComparer.Ordinal)
                .Select(operation => operation.OperationRunId));
        Assert.All(
            terminalEvidence.Run.Operations.Where(operation =>
                operation.Definition.OperationId is "operation.left" or "operation.right"),
            operation => Assert.NotNull(operation.ExecutionEvidence));
    }

    [Theory]
    [InlineData("operation.left")]
    [InlineData("operation.right")]
    public async Task ParallelDispatchWaveFreezesBothFailuresBeforeChoosingDeterministicPrimary(
        string firstCompletedOperationId)
    {
        var specifications = new Dictionary<string, StationOperationResultSpec>(StringComparer.Ordinal)
        {
            ["operation.entry"] = PassedResult("operation.entry", Now, '1'),
            ["operation.left"] = FailedResult("operation.left", Now, 'a'),
            ["operation.right"] = FailedResult("operation.right", Now, 'b')
        };
        var dispatcher = new DispatchWaveDispatcher(specifications, firstCompletedOperationId);

        var execution = await ExecuteParallelWaveAsync(dispatcher);

        Assert.Equal(ExecutionStatus.Failed, execution.Run.ExecutionStatus);
        Assert.Equal("Runtime.operation.left.Failed", execution.Run.FailureCode);
        Assert.Equal(Now, execution.Run.CompletedAtUtc);
        var branches = execution.Run.Operations
            .Where(operation => operation.Definition.OperationId is "operation.left" or "operation.right")
            .OrderBy(operation => operation.Definition.OperationId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, branches.Length);
        Assert.All(branches, operation => Assert.Equal(ExecutionStatus.Failed, operation.ExecutionStatus));
        Assert.All(branches, AssertFrozenBranchEvidence);
        Assert.Empty(await execution.Leases.ListAsync());

        var terminalPage = await execution.Repository.ListTerminalAsync(
            new ProductionRunTerminalPageRequest(10));
        var frozenBranches = Assert.Single(terminalPage.Items).Run.Operations
            .Where(operation => operation.Definition.OperationId is "operation.left" or "operation.right")
            .ToArray();
        Assert.Equal(2, frozenBranches.Length);
        Assert.All(frozenBranches, operation =>
        {
            Assert.Equal(ExecutionStatus.Failed, operation.ExecutionStatus);
            Assert.NotNull(operation.ExecutionEvidence);
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
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(unit.Id),
            MaterialLocation.AtStation(
                request.ProductionLineDefinitionId,
                entryOperation.Definition.StationSystemId),
            request.ActorId,
            Now))).Succeeded);
    }

    private static async Task<ParallelWaveExecution> ExecuteParallelWaveAsync(
        DispatchWaveDispatcher dispatcher)
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
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
        var runner = new ProductionRunRunner(
            repository,
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new AlwaysReadyOperationReadiness(),
            dispatcher,
            publisher,
            new GuidRuntimeIdProvider(),
            clock);
        var request = CreateParallelRequest();
        await RegisterRequestUnitAsync(materials, request);
        Assert.True((await coordinator.SubmitAsync(request)).IsSuccess);

        var result = await runner.ExecuteAsync(request.RunId);

        Assert.True(result.IsSuccess);
        return new ParallelWaveExecution(result.Value.Run, repository, leases);
    }

    private static StationOperationResultSpec PassedResult(
        string operationId,
        DateTimeOffset completedAtUtc,
        char hashCharacter) => new(
        ExecutionStatus.Completed,
        ResultJudgement.Passed,
        new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal)
        {
            [$"{operationId}.result"] = new(ProductionContextValueKind.Text, "passed")
        },
        1,
        1,
        0,
        completedAtUtc,
        Artifacts: [Artifact(operationId, hashCharacter)]);

    private static StationOperationResultSpec FailedResult(
        string operationId,
        DateTimeOffset completedAtUtc,
        char hashCharacter) => new(
        ExecutionStatus.Failed,
        ResultJudgement.Unknown,
        null,
        0,
        1,
        1,
        completedAtUtc,
        $"Runtime.{operationId}.Failed",
        $"{operationId} infrastructure failed.",
        [Artifact(operationId, hashCharacter)]);

    private static OperationArtifactExecutionEvidence Artifact(
        string operationId,
        char hashCharacter) => new(
        $"{operationId}.json",
        "Report",
        $"production-runs/{operationId}/report.json",
        "application/json",
        16,
        new string(hashCharacter, 64));

    private static void AssertFrozenBranchEvidence(OperationRunSnapshot operation)
    {
        var evidence = Assert.IsType<OperationExecutionEvidence>(operation.ExecutionEvidence);
        Assert.Equal(operation.CompletedAtUtc, evidence.CompletedAtUtc);
        Assert.Equal(operation.CompletedStepCount, evidence.Steps.Count(step => step.Status == "Completed"));
        Assert.Equal(operation.CommandCount, evidence.Commands.Count);
        Assert.Equal(operation.IncidentCount, evidence.Incidents.Count);
        Assert.Single(evidence.Artifacts);
        Assert.Equal(operation.FencingTokens.Count, evidence.ResourceFences.Count);
        Assert.All(operation.FencingTokens, fence =>
        {
            var evidenceFence = Assert.Single(evidence.ResourceFences, candidate =>
                string.Equals(candidate.ResourceKind, fence.Key.Kind.ToString(), StringComparison.Ordinal)
                && string.Equals(candidate.ResourceId, fence.Key.ResourceId, StringComparison.Ordinal));
            Assert.Equal(fence.Value, evidenceFence.FencingToken);
        });
    }

    private sealed record ParallelWaveExecution(
        ProductionRunSnapshot Run,
        InMemoryProductionRunRepository Repository,
        InMemoryResourceLeaseRepository Leases);

    private static ProductionRunCoordinator CreateCoordinator(
        InMemoryProductionRunRepository repository,
        InMemoryProductionMaterialRepository materials,
        InMemoryResourceLeaseRepository leases,
        InMemoryRuntimeDomainEventPublisher publisher,
        IStationOperationCanceler canceler,
        IClock clock) => new(
        repository,
        materials,
        leases,
        new InMemoryProductionRunSafetyTransitionStore(repository, leases),
        new AcceptingSafetyController(),
        canceler,
        publisher,
        new ProductionRunCreatedOutboxDispatcher(repository, publisher),
        clock);

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
            [],
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
            [
                TerminalTransition(
                    "route.main-failed",
                    operation.Definition.OperationId,
                    ProductDisposition.Nonconforming,
                    RuntimeRouteTransitionKind.Judgement,
                    ResultJudgement.Failed),
                TerminalTransition(
                    "route.main-default",
                    operation.Definition.OperationId,
                    ProductDisposition.Completed)
            ]);
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
            [],
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
            [TerminalTransition($"route.{suffix}.completed", operationId, ProductDisposition.Completed)]);
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
            [],
            [
                new ResourceRequirement(ResourceKind.Station, stationId),
                new ResourceRequirement(ResourceKind.Device, $"device.{operationId}")
            ]);
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
                    parallelGroupId: "parallel.work"),
                TerminalTransition(
                    "route.join.completed",
                    "operation.join",
                    ProductDisposition.Completed)
            ]);
    }

    private static SubmitProductionRunRequest CreateSequentialRequest(
        IReadOnlyCollection<OperationInputMappingPlan>? nextInputMappings = null,
        string nextStationSystemId = "station.next")
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.sequential"),
            new ProcessVersionId("process-version.sequential"),
            []);
        OperationExecutionPlan Operation(
            string operationId,
            string stationId,
            IReadOnlyCollection<OperationInputMappingPlan>? inputMappings = null) => new(
            operationId,
            stationId,
            new StationId(stationId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            process,
            inputMappings ?? [],
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
                Operation("operation.next", nextStationSystemId, nextInputMappings)
            ],
            [
                new RouteTransitionDefinition(
                    "route.next",
                    "operation.main",
                    "operation.next",
                    RuntimeRouteTransitionKind.Sequence),
                TerminalTransition(
                    "route.next.completed",
                    "operation.next",
                    ProductDisposition.Completed)
            ]);
    }

    private static RouteTransitionDefinition TerminalTransition(
        string transitionId,
        string sourceOperationId,
        ProductDisposition disposition,
        RuntimeRouteTransitionKind kind = RuntimeRouteTransitionKind.Sequence,
        ResultJudgement? judgement = null) => new(
            transitionId,
            sourceOperationId,
            null,
            kind,
        requiredJudgement: judgement,
        terminalDisposition: disposition);

    private sealed record StationOperationResultSpec(
        ExecutionStatus ExecutionStatus,
        ResultJudgement Judgement,
        IReadOnlyDictionary<string, ProductionContextValue>? Outputs,
        int CompletedStepCount,
        int CommandCount,
        int IncidentCount,
        DateTimeOffset CompletedAtUtc,
        string? FailureCode = null,
        string? FailureReason = null,
        IReadOnlyCollection<OperationArtifactExecutionEvidence>? Artifacts = null);

    private static StationOperationDispatchResult CreateDispatchResult(
        StationOperationDispatchRequest request,
        StationOperationResultSpec result)
    {
        var stepCount = Math.Max(result.CompletedStepCount, result.CommandCount == 0 ? 0 : 1);
        var steps = Enumerable.Range(0, stepCount)
            .Select(index =>
            {
                var completed = index < result.CompletedStepCount;
                var status = completed
                    ? "Completed"
                    : result.ExecutionStatus == ExecutionStatus.Canceled ? "Canceled" : "Failed";
                return new OperationStepExecutionEvidence(
                    DeterministicId(request.IdempotencyKey, "step", index),
                    $"node.{index:D4}",
                    $"action.{index:D4}",
                    "Station",
                    request.Operation.Definition.StationSystemId,
                    $"Test step {index:D4}",
                    status,
                    result.CompletedAtUtc,
                    result.CompletedAtUtc,
                    status == "Failed" ? result.FailureReason ?? "Test step failed." : null);
            })
            .ToArray();
        var commands = Enumerable.Range(0, result.CommandCount)
            .Select(index =>
            {
                var step = steps[Math.Min(index, steps.Length - 1)];
                var commandStatus = result.ExecutionStatus;
                var accepted = commandStatus is ExecutionStatus.Completed
                    or ExecutionStatus.Failed
                    or ExecutionStatus.TimedOut
                    ? result.CompletedAtUtc
                    : (DateTimeOffset?)null;
                return new OperationCommandExecutionEvidence(
                    DeterministicId(request.IdempotencyKey, "command", index),
                    step.StepId,
                    step.NodeId,
                    step.ActionId,
                    step.TargetKind,
                    step.TargetId,
                    "capability.test",
                    "Execute",
                    commandStatus,
                    result.CompletedAtUtc,
                    result.CompletedAtUtc,
                    accepted,
                    accepted,
                    result.CompletedAtUtc,
                    commandStatus == ExecutionStatus.Completed ? "{}" : null,
                    commandStatus == ExecutionStatus.Completed
                        ? null
                        : result.FailureReason ?? "Test command failed.",
                    commandStatus switch
                    {
                        ExecutionStatus.Completed => result.Judgement,
                        ExecutionStatus.Canceled => ResultJudgement.Aborted,
                        _ => ResultJudgement.Unknown
                    });
            })
            .ToArray();
        var incidents = Enumerable.Range(0, result.IncidentCount)
            .Select(index => new OperationIncidentExecutionEvidence(
                DeterministicId(request.IdempotencyKey, "incident", index),
                "Error",
                result.FailureCode ?? "Runtime.TestIncident",
                result.FailureReason ?? "Test incident.",
                result.CompletedAtUtc))
            .ToArray();
        var run = request.Run;
        var operation = request.Operation;
        var evidence = new OperationExecutionEvidence(
            OperationExecutionEvidenceOrigin.StationAgent,
            request.RuntimeSessionId.Value,
            run.RunId.Value,
            run.ProductionUnitId.Value,
            run.ProductionLineDefinitionId,
            operation.Definition.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            run.LotId,
            run.CarrierId,
            operation.Definition.ResourceRequirements
                .FirstOrDefault(requirement => requirement.Kind == ResourceKind.Fixture)?.ResourceId,
            operation.Definition.ResourceRequirements
                .FirstOrDefault(requirement => requirement.Kind == ResourceKind.Device)?.ResourceId,
            run.ActorId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            result.ExecutionStatus switch
            {
                ExecutionStatus.Completed => "Completed",
                ExecutionStatus.Canceled => "Canceled",
                _ => "Failed"
            },
            result.CompletedAtUtc,
            request.ResourceLeases.Select(lease => new OperationResourceFenceEvidence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            steps,
            commands,
            incidents,
            result.Artifacts?.ToArray() ?? []);
        return new StationOperationDispatchResult(
            result.ExecutionStatus,
            result.Judgement,
            evidence,
            result.Outputs,
            result.CompletedStepCount,
            result.CommandCount,
            result.IncidentCount,
            result.CompletedAtUtc,
            result.FailureCode,
            result.FailureReason);
    }

    private static Guid DeterministicId(string key, string kind, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}/{kind}/{index}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class Fixture
    {
        private readonly InMemoryProductionMaterialRepository _materials;
        private readonly InMemoryProductionRunRepository _repository;
        private readonly FixedClock _clock = new(Now);
        private readonly InMemoryResourceLeaseRepository _leases;
        private readonly InMemoryRuntimeDomainEventPublisher _publisher = new();

        public Fixture(
            StationOperationResultSpec result,
            IStationSafetyController? safetyController = null,
            Func<InMemoryProductionRunRepository, IProductionRunRepository>? repositoryDecorator = null)
            : this(
                new RecordingDispatcher(result),
                safetyController ?? new AcceptingSafetyController(),
                repositoryDecorator)
        {
        }

        public Fixture(Exception exception)
            : this(new RecordingDispatcher(exception), new AcceptingSafetyController(), null)
        {
        }

        private Fixture(
            RecordingDispatcher dispatcher,
            IStationSafetyController safetyController,
            Func<InMemoryProductionRunRepository, IProductionRunRepository>? repositoryDecorator)
        {
            _leases = new InMemoryResourceLeaseRepository(_clock);
            _materials = new InMemoryProductionMaterialRepository();
            _repository = new InMemoryProductionRunRepository(_materials);
            Dispatcher = dispatcher;
            var coordinatorRepository = repositoryDecorator?.Invoke(_repository) ?? _repository;
            Coordinator = new ProductionRunCoordinator(
                coordinatorRepository,
                _materials,
                _leases,
                new InMemoryProductionRunSafetyTransitionStore(_repository, _leases),
                safetyController,
                new AcceptingCanceler(),
                _publisher,
                new ProductionRunCreatedOutboxDispatcher(coordinatorRepository, _publisher),
                _clock);
            Runner = new ProductionRunRunner(
                _repository,
                _repository,
                _leases,
                new InMemoryProductionRunSafetyTransitionStore(_repository, _leases),
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
            SubmitAsync(
                SubmitProductionRunRequest request,
                CancellationToken cancellationToken = default)
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
            Assert.True(await _materials.TryAddAsync(unit, cancellationToken));
            var materialService = new ProductionMaterialService(_materials, _repository);
            Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                MaterialReference.ForProductionUnit(unit.Id),
                MaterialLocation.AtStation(
                    request.ProductionLineDefinitionId,
                    entryOperation.Definition.StationSystemId),
                request.ActorId,
                Now), cancellationToken)).Succeeded);
            return await Coordinator.SubmitAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingDispatcher : IStationOperationDispatcher
    {
        private readonly StationOperationResultSpec? _result;
        private readonly Exception? _exception;

        public RecordingDispatcher(StationOperationResultSpec result) => _result = result;

        public RecordingDispatcher(Exception exception) => _exception = exception;

        public List<StationOperationDispatchRequest> Requests { get; } = [];

        public ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _exception is null
                ? ValueTask.FromResult(CreateDispatchResult(request, _result!))
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

            return CreateDispatchResult(
                request,
                new StationOperationResultSpec(
                    ExecutionStatus.Completed,
                    ResultJudgement.Passed,
                    null,
                    0,
                    0,
                    0,
                    Now.AddSeconds(1)));
        }

        public void ReleaseRuns() => _releaseRuns.TrySetResult();
    }

    private sealed class DispatchWaveDispatcher(
        IReadOnlyDictionary<string, StationOperationResultSpec> specifications,
        string firstCompletedOperationId) : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _bothBranchesStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstBranchCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedBranchCount;

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var operationId = request.Operation.Definition.OperationId;
            var specification = specifications[operationId];
            if (operationId == "operation.entry")
            {
                return CreateDispatchResult(request, specification);
            }

            if (operationId is not ("operation.left" or "operation.right"))
            {
                throw new InvalidOperationException(
                    $"Operation {operationId} must not dispatch after a failed parallel wave.");
            }

            if (Interlocked.Increment(ref _startedBranchCount) == 2)
            {
                _bothBranchesStarted.TrySetResult();
            }

            await _bothBranchesStarted.Task.WaitAsync(cancellationToken);
            if (string.Equals(operationId, firstCompletedOperationId, StringComparison.Ordinal))
            {
                _firstBranchCompleted.TrySetResult();
                return CreateDispatchResult(request, specification);
            }

            await _firstBranchCompleted.Task.WaitAsync(cancellationToken);
            return CreateDispatchResult(request, specification);
        }
    }

    private sealed class AlwaysReadyOperationReadiness : IProductionOperationReadiness
    {
        public ValueTask<ProductionOperationReadiness> EvaluateAsync(
            ProductionRunSnapshot run,
            OperationRunSnapshot operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ProductionOperationReadiness.Ready);
        }
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
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Completed,
                        ResultJudgement.Passed,
                        null,
                        1,
                        1,
                        0,
                        Now.AddSeconds(1)));
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
            {
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Canceled,
                        ResultJudgement.Aborted,
                        null,
                        0,
                        1,
                        0,
                        Now.AddSeconds(1),
                        "Runtime.OperationCanceled",
                        "Station operation cancellation reached the active execution token."));
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
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Canceled,
                        ResultJudgement.Aborted,
                        null,
                        0,
                        0,
                        0,
                        Now.AddSeconds(1),
                        "Runtime.OperationCanceled",
                        "Safe Stop canceled the active station operation."));
            }
        }

        public void ReleaseCancellationResult() => _releaseCancellationResult.TrySetResult();
    }

    private sealed class NaturalCompletionDuringCancelDispatcher : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseNaturalCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public List<string> OperationIds { get; } = [];

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            OperationIds.Add(request.Operation.Definition.OperationId);
            if (string.Equals(
                request.Operation.Definition.OperationId,
                "operation.main",
                StringComparison.Ordinal))
            {
                _started.TrySetResult();
                await _releaseNaturalCompletion.Task.WaitAsync(cancellationToken);
            }

            return CreateDispatchResult(
                request,
                new StationOperationResultSpec(
                    ExecutionStatus.Completed,
                    ResultJudgement.Passed,
                    null,
                    0,
                    0,
                    0,
                    Now.AddSeconds(1)));
        }

        public void ReleaseNaturalCompletion() => _releaseNaturalCompletion.TrySetResult();
    }

    private sealed class ScrapDispatcher(InProcessStationOperationRegistry registry)
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

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
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
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Canceled,
                        ResultJudgement.Aborted,
                        null,
                        0,
                        1,
                        0,
                        Now.AddSeconds(1),
                        "Runtime.OperationCanceled",
                        "Scrap canceled the active Station operation.",
                        [Artifact("scrap-result", 'a')]));
            }
        }

        public void ReleaseCancellationResult() => _releaseCancellationResult.TrySetResult();
    }

    private sealed class ParallelScrapDispatcher(InProcessStationOperationRegistry registry)
        : IStationOperationDispatcher
    {
        private readonly TaskCompletionSource _bothBranchesRegistered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bothBranchesArmed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bothCancellationsObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellationResults =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _registeredCount;
        private int _armedCount;
        private int _canceledCount;

        public Task BothBranchesArmed => _bothBranchesArmed.Task;

        public Task BothCancellationsObserved => _bothCancellationsObserved.Task;

        public async ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var operationId = request.Operation.Definition.OperationId;
            if (operationId == "operation.entry")
            {
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Completed,
                        ResultJudgement.NotApplicable,
                        null,
                        0,
                        0,
                        0,
                        Now));
            }

            if (operationId is not ("operation.left" or "operation.right"))
            {
                throw new InvalidOperationException(
                    $"Operation {operationId} must not dispatch during active Scrap.");
            }

            using var execution = registry.Register(request.IdempotencyKey, cancellationToken);
            if (Interlocked.Increment(ref _registeredCount) == 2)
            {
                _bothBranchesRegistered.TrySetResult();
            }

            // Do not let Scrap cancellation escape through the registration barrier. Both
            // dispatches must first cross this non-cancelable test latch so cancellation is
            // observed inside the evidence-producing boundary below.
            await _bothBranchesRegistered.Task;
            if (Interlocked.Increment(ref _armedCount) == 2)
            {
                _bothBranchesArmed.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref _canceledCount) == 2)
                {
                    _bothCancellationsObserved.TrySetResult();
                }

                await _releaseCancellationResults.Task;
                return CreateDispatchResult(
                    request,
                    new StationOperationResultSpec(
                        ExecutionStatus.Canceled,
                        ResultJudgement.Aborted,
                        null,
                        0,
                        1,
                        0,
                        Now.AddSeconds(1),
                        "Runtime.OperationCanceled",
                        $"Scrap canceled {operationId}.",
                        [Artifact(operationId, operationId.EndsWith("left", StringComparison.Ordinal)
                            ? 'b'
                            : 'c')]));
            }
        }

        public void ReleaseCancellationResults() => _releaseCancellationResults.TrySetResult();
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

    private sealed class RecordingSafetyController : IStationSafetyController
    {
        public int RequestCount { get; private set; }

        public ValueTask<StationSafetyResult> RequestSafeStopAsync(
            StationSafetyRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return ValueTask.FromResult(StationSafetyResult.Success());
        }
    }

    private sealed class CancelAfterAdmissionRepository(
        IProductionRunRepository inner,
        CancellationTokenSource cancellation) : IProductionRunRepository
    {
        public async ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default)
        {
            var added = await inner.TryAddAsync(
                run,
                executionPlan,
                admission,
                cancellationToken);
            cancellation.Cancel();
            return added;
        }

        public ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            inner.SaveAsync(run, expectedRevision, cancellationToken);

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

    private sealed class AcceptingCanceler : IStationOperationCanceler
    {
        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationOperationCancellationResult.Success());
    }

    private sealed class BlockingStationOperationCanceler : IStationOperationCanceler
    {
        private readonly TaskCompletionSource _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAcknowledgement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Requested => _requested.Task;

        public async ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            _requested.TrySetResult();
            await _releaseAcknowledgement.Task.WaitAsync(cancellationToken);
            return StationOperationCancellationResult.Success();
        }

        public void ReleaseAcknowledgement() => _releaseAcknowledgement.TrySetResult();
    }

    private sealed class IdempotentStationCanceler(InProcessStationOperationRegistry registry)
        : IStationOperationCanceler
    {
        private readonly Lock _sync = new();
        private readonly HashSet<string> _hardwareCancellations = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _secondTransportRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _transportRequestCount;
        private int _hardwareCancellationCount;

        public int TransportRequestCount => Volatile.Read(ref _transportRequestCount);

        public int HardwareCancellationCount => Volatile.Read(ref _hardwareCancellationCount);

        public Task SecondTransportRequest => _secondTransportRequest.Task;

        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _transportRequestCount) == 2)
            {
                _secondTransportRequest.TrySetResult();
            }

            lock (_sync)
            {
                if (_hardwareCancellations.Add(request.JobIdempotencyKey))
                {
                    registry.RequestCancel(request.JobIdempotencyKey);
                    Interlocked.Increment(ref _hardwareCancellationCount);
                }
            }

            return ValueTask.FromResult(StationOperationCancellationResult.Success());
        }
    }

    private sealed class RejectingCanceler : IStationOperationCanceler
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(StationOperationCancellationResult.Failure(
                "Station.CancelRejected",
                "Station rejected process-tree cancellation."));
        }
    }

    private sealed class ThrowingCanceler : IStationOperationCanceler
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<StationOperationCancellationResult> CancelAsync(
            StationOperationCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromException<StationOperationCancellationResult>(
                new InvalidOperationException("Cancellation transport disconnected."));
        }
    }
}
