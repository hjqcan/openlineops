using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ParallelRunningOperationsEnterRecoveryAndHoldEveryExactLeaseWithoutReplay()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var sessionsBeforeRecovery = fixture.Run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .ToDictionary(
                static operation => operation.OperationRunId,
                static operation => operation.RuntimeSessionId,
                StringComparer.Ordinal);
        var service = fixture.CreateRecoveryService();

        var result = await service.RecoverAsync();
        var restored = (await fixture.Repository.GetByIdAsync(fixture.Run.Id))!.Run;

        Assert.Equal(1, result.RecoveryRequiredRunCount);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, restored.ControlState);
        Assert.Equal(ExecutionStatus.Running, restored.ExecutionStatus);
        Assert.Equal(ProductDisposition.Held, restored.Disposition);
        Assert.Contains("operation.left@0001", restored.FailureReason, StringComparison.Ordinal);
        Assert.Contains("operation.right@0001", restored.FailureReason, StringComparison.Ordinal);
        var running = restored.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, running.Length);
        Assert.All(running, operation => Assert.Equal(
            sessionsBeforeRecovery[operation.OperationRunId],
            operation.RuntimeSessionId));
        await AssertEveryParallelLeaseIsHeldAsync(fixture);
        await AssertRunnerDoesNotDispatchAsync(fixture);

        var saveCountAfterFirstRecovery = fixture.Repository.SaveCount;
        var leasesAfterFirstRecovery = await fixture.Leases.ListAsync();
        var repeated = await service.RecoverAsync();

        Assert.Equal(1, repeated.RecoveryRequiredRunCount);
        Assert.Equal(saveCountAfterFirstRecovery, fixture.Repository.SaveCount);
        Assert.Equal(
            leasesAfterFirstRecovery.OrderBy(LeaseIdentity).ToArray(),
            (await fixture.Leases.ListAsync()).OrderBy(LeaseIdentity).ToArray());
    }

    [Fact]
    public async Task ProtectedTransitionHoldsOnlyRunningOperationWhenSiblingAlreadyHasEvidence()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var left = fixture.Run.Operations.Single(operation => string.Equals(
            operation.OperationRunId,
            "operation.left@0001",
            StringComparison.Ordinal));
        var right = fixture.Run.Operations.Single(operation => string.Equals(
            operation.OperationRunId,
            "operation.right@0001",
            StringComparison.Ordinal));
        var completedAtUtc = Now.AddSeconds(4);
        Assert.True(fixture.Run.RecordOperationCompletion(
            left.OperationRunId,
            ResultJudgement.Passed,
            null,
            0,
            0,
            0,
            completedAtUtc,
            ProductionRunExecutionEvidenceTestFactory.Create(
                fixture.Run,
                left.OperationRunId,
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                completedAtUtc)).Succeeded);
        Assert.True(fixture.Run.MarkRecoveryRequired(
            "The sibling Station remains uncertain.",
            Now.AddSeconds(5)).Succeeded);

        var rightHold = LeaseHold(right);
        var canonical = ProductionRunLeaseHold.RequireExactFor(fixture.Run, [rightHold]);
        Assert.Equal(right.OperationRunId, Assert.Single(canonical).OperationRunId);
        Assert.Throws<ArgumentException>(() => ProductionRunLeaseHold.RequireExactFor(
            fixture.Run,
            [LeaseHold(left), rightHold]));
    }

    [Fact]
    public async Task ExactHoldFailureLeavesRunAndLeaseSetUnchangedThenColdRecoveryRetriesWholeSet()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var safetyTransitions = new FailOnceProductionRunSafetyTransitionStore(
            new InMemoryProductionRunSafetyTransitionStore(
                fixture.Repository,
                fixture.Leases),
            "operation.right@0001");
        var service = fixture.CreateRecoveryService(safetyTransitions);

        var failure = await Assert.ThrowsAsync<IOException>(
            () => service.RecoverAsync().AsTask());

        Assert.Contains("operation.right@0001", failure.Message, StringComparison.Ordinal);
        var protectedRun = (await fixture.Repository.GetByIdAsync(fixture.Run.Id))!.Run;
        Assert.Equal(ProductionRunControlState.Active, protectedRun.ControlState);
        var afterFailure = (await fixture.Leases.ListAsync())
            .ToDictionary(static lease => lease.OperationRunId, StringComparer.Ordinal);
        Assert.NotEqual(DateTimeOffset.MaxValue, afterFailure["operation.left@0001"].ExpiresAtUtc);
        Assert.NotEqual(DateTimeOffset.MaxValue, afterFailure["operation.right@0001"].ExpiresAtUtc);

        var recovered = await service.RecoverAsync();

        Assert.Equal(1, recovered.RecoveryRequiredRunCount);
        await AssertEveryParallelLeaseIsHeldAsync(fixture);
        Assert.Equal(4, safetyTransitions.HoldAttempts.Count);
        Assert.Equal(
            [
                "operation.left@0001",
                "operation.right@0001",
                "operation.left@0001",
                "operation.right@0001"
            ],
            safetyTransitions.HoldAttempts);
    }

    [Fact]
    public async Task SafetyTransitionFailureLeavesRunAndEveryLeaseUnchanged()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var safetyTransitions = new FailingProductionRunSafetyTransitionStore();
        var service = fixture.CreateRecoveryService(safetyTransitions);

        var failure = await Assert.ThrowsAsync<IOException>(
            () => service.RecoverAsync().AsTask());

        Assert.Contains("persist recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProductionRunControlState.Active,
            (await fixture.Repository.GetByIdAsync(fixture.Run.Id))!.Run.ControlState);
        Assert.Equal(1, safetyTransitions.SaveAttempts);
        Assert.All(await fixture.Leases.ListAsync(), static lease =>
            Assert.NotEqual(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));
    }

    [Fact]
    public async Task CancellationAfterRecoveryScanStillProtectsEveryRunningOperationThenPropagates()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var repository = new CancelAfterListingProductionRunRepository(
            fixture.Repository,
            cancellation);
        var service = new ProductionRunRecoveryService(
            repository,
            fixture.Leases,
            new InMemoryProductionRunSafetyTransitionStore(
                fixture.Repository,
                fixture.Leases),
            fixture.Publisher,
            fixture.Clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RecoverAsync(cancellation.Token).AsTask());

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ProductionRunControlState.RecoveryRequired,
            (await fixture.Repository.GetByIdAsync(fixture.Run.Id))!.Run.ControlState);
        await AssertEveryParallelLeaseIsHeldAsync(fixture);
        await AssertRunnerDoesNotDispatchAsync(fixture);
    }

    [Fact]
    public async Task PendingRunRemainsDispatchableAfterRestart()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (run, plan) = CreatePendingRun();
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        var clock = new FixedClock(Now);
        var leases = new InMemoryResourceLeaseRepository(clock);
        var service = new ProductionRunRecoveryService(
            repository,
            leases,
            new InMemoryProductionRunSafetyTransitionStore(repository, leases),
            new InMemoryRuntimeDomainEventPublisher(),
            clock);

        var result = await service.RecoverAsync();

        Assert.Equal(1, result.PendingRunCount);
        Assert.Equal(ExecutionStatus.Pending, (await repository.GetByIdAsync(run.Id))!.Run.ExecutionStatus);
    }

    [Fact]
    public async Task StationRecoveryMessageImmediatelyProtectsEveryRunningLeaseIdempotently()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var operation = fixture.Run.Operations.Single(item => string.Equals(
            item.OperationRunId,
            "operation.left@0001",
            StringComparison.Ordinal));
        var message = new OpenLineOps.Agent.Contracts.StationJobRecoveryRequired(
            Guid.NewGuid(),
            "station-recovery/job-left/0001",
            Guid.NewGuid(),
            "station-job/left/0001",
            "agent.left",
            "station.left",
            fixture.Run.Id.Value,
            operation.OperationRunId,
            operation.RuntimeSessionId!.Value.Value,
            "Agent process terminated during non-idempotent hardware execution.",
            Now.AddSeconds(-10));
        var ingress = new StationJobRecoveryRequiredIngress(
            fixture.Repository,
            fixture.Leases,
            new InMemoryProductionRunSafetyTransitionStore(
                fixture.Repository,
                fixture.Leases),
            fixture.Publisher,
            fixture.Clock);

        await ingress.HandleAsync(message);
        await ingress.HandleAsync(message);

        var persisted = (await fixture.Repository.GetByIdAsync(fixture.Run.Id))!.Run;
        Assert.Equal(ProductionRunControlState.RecoveryRequired, persisted.ControlState);
        Assert.Equal(ProductDisposition.Held, persisted.Disposition);
        Assert.Equal(fixture.Clock.UtcNow, persisted.LastTransitionAtUtc);
        var leases = (await fixture.Leases.ListAsync())
            .ToDictionary(static lease => lease.OperationRunId, StringComparer.Ordinal);
        Assert.Equal(DateTimeOffset.MaxValue, leases[operation.OperationRunId].ExpiresAtUtc);
        Assert.Equal(
            DateTimeOffset.MaxValue,
            leases["operation.right@0001"].ExpiresAtUtc);
    }

    [Fact]
    public async Task ColdRecoveryQuarantinesNeverPublishedDispatchAndGatewayTerminates()
    {
        var fixture = await ParallelRecoveryFixture.CreateAsync();
        var snapshot = fixture.Run.ToSnapshot();
        var operation = snapshot.Operations.Single(item => string.Equals(
            item.OperationRunId,
            "operation.left@0001",
            StringComparison.Ordinal));
        var lease = (await fixture.Leases.ListAsync()).Single(item => string.Equals(
            item.OperationRunId,
            operation.OperationRunId,
            StringComparison.Ordinal));
        var idempotencyKey = $"job/{snapshot.RunId.Value:D}/{operation.OperationRunId}";
        var request = new StationJobRequested(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(idempotencyKey),
            idempotencyKey,
            "agent.left",
            "station.left",
            operation.Definition.StationSystemId,
            snapshot.RunId.Value,
            snapshot.ProductionUnitId.Value,
            operation.RuntimeSessionId!.Value.Value,
            operation.OperationRunId,
            operation.Attempt,
            snapshot.ProductionUnitIdentity.ModelId,
            snapshot.ProductionUnitIdentity.InputKey,
            snapshot.ProductionUnitIdentity.Value,
            snapshot.LotId,
            snapshot.CarrierId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.ProductionLineDefinitionId,
            snapshot.TopologyId,
            snapshot.ActorId,
            new string('a', 64),
            operation.Definition.OperationId,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            [new StationResourceFence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)],
            System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Now.AddSeconds(3));
        var dispatchStore = new InMemoryStationJobCoordinationStore();
        var leaseChange = StationDispatchMessageIdentity.CreateLeaseGranted(
            request,
            Assert.Single(request.ResourceFences));
        Assert.True(await dispatchStore.TryEnqueueAsync(request, [leaseChange]));

        _ = await fixture.CreateRecoveryService().RecoverAsync();
        var authorization = await new StationDispatchPublicationAuthorizer(
                fixture.Repository,
                fixture.Leases,
                new FixedDeploymentResolver(request))
            .AuthorizeAsync(request);
        Assert.False(authorization.Allowed);
        await dispatchStore.QuarantineJobAsync(
            request.JobId,
            authorization.RejectionReason!,
            fixture.Clock.UtcNow);

        var exception = await Assert.ThrowsAsync<StationJobDispatchQuarantinedException>(async () =>
            await new DurableStationJobGateway(dispatchStore)
                .DispatchAsync(request)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(exception.NeverPublished);
        Assert.Equal(2, exception.Evidence.Count);
        Assert.Empty(await dispatchStore.ListPendingAsync(10));
    }

    private static string LeaseIdentity(ResourceLease lease) =>
        $"{lease.Resource.Kind}/{lease.Resource.ResourceId}/{lease.OperationRunId}";

    private static ProductionRunLeaseHold LeaseHold(OperationRun operation) =>
        new(
            operation.OperationRunId,
            operation.FencingTokens
                .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                .ToArray());

    private static async Task AssertEveryParallelLeaseIsHeldAsync(ParallelRecoveryFixture fixture)
    {
        var leases = (await fixture.Leases.ListAsync())
            .OrderBy(static lease => lease.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, leases.Length);
        Assert.Equal(
            ["operation.left@0001", "operation.right@0001"],
            leases.Select(static lease => lease.OperationRunId).ToArray());
        Assert.Equal(
            ["station.left", "station.right"],
            leases.Select(static lease => lease.Resource.ResourceId).ToArray());
        Assert.All(leases, static lease => Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));
    }

    private static async Task AssertRunnerDoesNotDispatchAsync(ParallelRecoveryFixture fixture)
    {
        var dispatcher = new RecordingDispatcher();
        var runner = new ProductionRunRunner(
            fixture.Repository,
            fixture.Repository,
            fixture.Leases,
            new InMemoryProductionRunSafetyTransitionStore(
                fixture.Repository,
                fixture.Leases),
            new ProductionOperationReadinessEvaluator(fixture.Materials),
            dispatcher,
            fixture.Publisher,
            new GuidRuntimeIdProvider(),
            fixture.Clock);

        var result = await runner.ExecuteAsync(fixture.Run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, result.Value.Run.ControlState);
        Assert.Equal(0, dispatcher.DispatchCount);
    }

    private static (ProductionRun Run, ProductionRunExecutionPlan Plan) CreatePendingRun()
    {
        var runId = ProductionRunId.New();
        var operation = Operation("operation.main", "station.main");
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            null,
            null,
            "operator.main",
            operation.Definition.OperationId,
            Now,
            [operation.Definition],
            []);
        return (run, new ProductionRunExecutionPlan(runId, [operation]));
    }

    private static OperationExecutionPlan Operation(string operationId, string stationSystemId) => new(
        operationId,
        stationSystemId,
        new StationId(stationSystemId),
        new ConfigurationSnapshotId($"configuration.{operationId}"),
        new RecipeSnapshotId($"recipe.{operationId}"),
        new ExecutableRuntimeProcess(
            new ProcessDefinitionId($"process.{operationId}"),
            new ProcessVersionId($"process-version.{operationId}"),
            []),
        []);

    private sealed class ParallelRecoveryFixture(
        InMemoryProductionMaterialRepository materials,
        InMemoryProductionRunRepository repository,
        InMemoryResourceLeaseRepository leases,
        InMemoryRuntimeDomainEventPublisher publisher,
        FixedClock clock,
        ProductionRun run)
    {
        public InMemoryProductionMaterialRepository Materials { get; } = materials;

        public InMemoryProductionRunRepository Repository { get; } = repository;

        public InMemoryResourceLeaseRepository Leases { get; } = leases;

        public InMemoryRuntimeDomainEventPublisher Publisher { get; } = publisher;

        public FixedClock Clock { get; } = clock;

        public ProductionRun Run { get; } = run;

        public static async Task<ParallelRecoveryFixture> CreateAsync()
        {
            var materials = new InMemoryProductionMaterialRepository();
            var repository = new InMemoryProductionRunRepository(materials);
            var publisher = new InMemoryRuntimeDomainEventPublisher();
            var clock = new FixedClock(Now.AddMinutes(1));
            var leases = new InMemoryResourceLeaseRepository(clock);
            var runId = ProductionRunId.New();
            var entry = Operation("operation.entry", "station.entry");
            var left = Operation("operation.left", "station.left");
            var right = Operation("operation.right", "station.right");
            var join = Operation("operation.join", "station.join");
            var transitions = new RouteTransitionDefinition[]
            {
                new(
                    "fork.left",
                    entry.Definition.OperationId,
                    left.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new(
                    "fork.right",
                    entry.Definition.OperationId,
                    right.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new(
                    "join.left",
                    left.Definition.OperationId,
                    join.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work"),
                new(
                    "join.right",
                    right.Definition.OperationId,
                    join.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work")
            };
            var operations = new[] { entry, left, right, join };
            var run = ProductionRun.Create(
                runId,
                "project.parallel",
                "application.parallel",
                "snapshot.parallel",
                "topology.parallel",
                "line.parallel",
                ProductionUnitId.New(),
                new ProductionUnitIdentity("product.board", "serialNumber", "SN-PARALLEL"),
                null,
                null,
                "operator.parallel",
                entry.Definition.OperationId,
                Now,
                operations.Select(static operation => operation.Definition).ToArray(),
                transitions);
            var plan = new ProductionRunExecutionPlan(runId, operations);
            Assert.True(await repository.TryAddAsync(
                run,
                plan,
                await ProductionRunTestMaterials.RegisterAsync(materials, run)));
            Assert.True(run.Start(Now).Succeeded);
            StartWithSyntheticLease(run, "operation.entry@0001", Now.AddSeconds(1));
            Assert.True(run.CompleteOperation(
                "operation.entry@0001",
                ResultJudgement.NotApplicable,
                null,
                1,
                1,
                0,
                Now.AddSeconds(2),
                ProductionRunExecutionEvidenceTestFactory.Create(
                    run,
                    "operation.entry@0001",
                    ExecutionStatus.Completed,
                    ResultJudgement.NotApplicable,
                    Now.AddSeconds(2),
                    1,
                    1)).Succeeded);

            foreach (var operation in run.Operations
                         .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Pending)
                         .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal))
            {
                var acquired = await leases.TryAcquireAsync(
                    run.Id,
                    operation.OperationRunId,
                    operation.ResourceRequirements,
                    TimeSpan.FromHours(1));
                Assert.NotNull(acquired);
                Assert.True(run.StartOperation(
                    operation.OperationRunId,
                    RuntimeSessionId.New(),
                    acquired,
                    Now.AddSeconds(3)).Succeeded);
            }

            _ = await repository.SaveAsync(run, 0);
            return new ParallelRecoveryFixture(materials, repository, leases, publisher, clock, run);
        }

        public ProductionRunRecoveryService CreateRecoveryService(
            IProductionRunSafetyTransitionStore? safetyTransitions = null) =>
            new(
                Repository,
                Leases,
                safetyTransitions ?? new InMemoryProductionRunSafetyTransitionStore(
                    Repository,
                    Leases),
                Publisher,
                Clock);

        private static void StartWithSyntheticLease(
            ProductionRun run,
            string operationRunId,
            DateTimeOffset startedAtUtc)
        {
            var operation = run.Operations.Single(candidate => string.Equals(
                candidate.OperationRunId,
                operationRunId,
                StringComparison.Ordinal));
            var operationLeases = operation.ResourceRequirements
                .Select((resource, index) => new ResourceLease(
                    resource,
                    run.Id,
                    operation.OperationRunId,
                    index + 1,
                    startedAtUtc,
                    startedAtUtc.AddHours(1)))
                .ToArray();
            Assert.True(run.StartOperation(
                operation.OperationRunId,
                RuntimeSessionId.New(),
                operationLeases,
                startedAtUtc).Succeeded);
        }
    }

    private sealed class RecordingDispatcher : IStationOperationDispatcher
    {
        private int _dispatchCount;

        public int DispatchCount => Volatile.Read(ref _dispatchCount);

        public ValueTask<StationOperationDispatchResult> DispatchAsync(
            StationOperationDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCount);
            return ValueTask.FromException<StationOperationDispatchResult>(
                new InvalidOperationException("Recovery must never replay a station operation."));
        }
    }

    private class RecordingProductionRunSafetyTransitionStore(
        IProductionRunSafetyTransitionStore inner) : IProductionRunSafetyTransitionStore
    {
        protected IProductionRunSafetyTransitionStore Inner { get; } =
            inner ?? throw new ArgumentNullException(nameof(inner));

        public List<string> HoldAttempts { get; } = [];

        public virtual ValueTask<long> SaveWithLeaseHoldsAsync(
            ProductionRun run,
            long expectedRevision,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default)
        {
            HoldAttempts.AddRange(leaseHolds.Select(static hold => hold.OperationRunId));
            return Inner.SaveWithLeaseHoldsAsync(
                run,
                expectedRevision,
                leaseHolds,
                cancellationToken);
        }
    }

    private sealed class FailOnceProductionRunSafetyTransitionStore(
        IProductionRunSafetyTransitionStore inner,
        string operationRunIdToFail) : RecordingProductionRunSafetyTransitionStore(inner)
    {
        private int _remainingFailures = 1;

        public override ValueTask<long> SaveWithLeaseHoldsAsync(
            ProductionRun run,
            long expectedRevision,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default)
        {
            HoldAttempts.AddRange(leaseHolds.Select(static hold => hold.OperationRunId));
            if (leaseHolds.Any(hold => string.Equals(
                    hold.OperationRunId,
                    operationRunIdToFail,
                    StringComparison.Ordinal))
                && Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                return ValueTask.FromException<long>(new IOException(
                    $"Could not hold {operationRunIdToFail} for recovery."));
            }

            return Inner.SaveWithLeaseHoldsAsync(
                run,
                expectedRevision,
                leaseHolds,
                cancellationToken);
        }
    }

    private sealed class FailingProductionRunSafetyTransitionStore
        : IProductionRunSafetyTransitionStore
    {
        private int _saveAttempts;

        public int SaveAttempts => Volatile.Read(ref _saveAttempts);

        public ValueTask<long> SaveWithLeaseHoldsAsync(
            ProductionRun run,
            long expectedRevision,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default)
        {
            _ = run;
            _ = expectedRevision;
            _ = leaseHolds;
            _ = cancellationToken;
            Interlocked.Increment(ref _saveAttempts);
            return ValueTask.FromException<long>(
                new IOException("Could not persist recovery state."));
        }
    }

    private abstract class DelegatingProductionRunRepository(IProductionRunRepository inner)
        : IProductionRunRepository
    {
        protected IProductionRunRepository Inner { get; } =
            inner ?? throw new ArgumentNullException(nameof(inner));

        public virtual ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) =>
            Inner.TryAddAsync(run, executionPlan, admission, cancellationToken);

        public virtual ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            Inner.SaveAsync(run, expectedRevision, cancellationToken);

        public virtual ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            Inner.GetByIdAsync(runId, cancellationToken);

        public virtual ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default) =>
            Inner.ListRecoverableAsync(cancellationToken);

        public virtual ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
            string? productionLineDefinitionId = null,
            string? stationSystemId = null,
            string? slotId = null,
            CancellationToken cancellationToken = default) =>
            Inner.ListActiveAsync(
                productionLineDefinitionId,
                stationSystemId,
                slotId,
                cancellationToken);

        public virtual ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
            ProductionRunTerminalPageRequest request,
            CancellationToken cancellationToken = default) =>
            Inner.ListTerminalAsync(request, cancellationToken);

        public virtual ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
            ListPendingCreatedOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            Inner.ListPendingCreatedOutboxAsync(maximumCount, cancellationToken);

        public virtual ValueTask MarkCreatedOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            Inner.MarkCreatedOutboxProcessedAsync(runId, cancellationToken);

        public virtual ValueTask RecordCreatedOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            Inner.RecordCreatedOutboxFailureAsync(runId, failureDescription, cancellationToken);

        public virtual ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
            ListPendingTerminalOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            Inner.ListPendingTerminalOutboxAsync(maximumCount, cancellationToken);

        public virtual ValueTask MarkTerminalOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            Inner.MarkTerminalOutboxProcessedAsync(runId, cancellationToken);

        public virtual ValueTask RecordTerminalOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            Inner.RecordTerminalOutboxFailureAsync(runId, failureDescription, cancellationToken);
    }

    private sealed class CancelAfterListingProductionRunRepository(
        IProductionRunRepository inner,
        CancellationTokenSource cancellation) : DelegatingProductionRunRepository(inner)
    {
        public override async ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>>
            ListRecoverableAsync(CancellationToken cancellationToken = default)
        {
            var entries = await Inner.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
            cancellation.Cancel();
            return entries;
        }
    }

    public sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedDeploymentResolver(StationJobRequested request)
        : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest deployment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = deployment;
            return ValueTask.FromResult(new StationDeploymentRoute(
                request.AgentId,
                request.StationId,
                request.PackageContentSha256,
                request.ProductionLineDefinitionId));
        }
    }
}
