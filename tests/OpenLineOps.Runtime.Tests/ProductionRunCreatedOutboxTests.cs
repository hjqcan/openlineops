using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunCreatedOutboxTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CommittedSubmissionSurvivesSubscriberFailureAndRetryPublishesSameEvent()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var subscriber = new FailOnceCreatedSubscriber();
        var publisher = new RuntimeDomainEventPublisher([subscriber]);
        using var dispatcher = new ProductionRunCreatedOutboxDispatcher(repository, publisher);
        var clock = new FixedClock(Now);
        var coordinator = new ProductionRunCoordinator(
            repository,
            materials,
            new InMemoryResourceLeaseRepository(clock),
            new AcceptingSafetyController(),
            new AcceptingCanceler(),
            publisher,
            dispatcher,
            clock);
        var request = CreateRequest();
        Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
            request.ProductionUnitId,
            request.FrozenProductModelId,
            request.FrozenIdentityInputKey,
            "SN-CREATED-001",
            null,
            request.ActorId,
            Now.AddMinutes(-1))));

        var submitted = await coordinator.SubmitAsync(request);

        Assert.True(submitted.IsSuccess);
        Assert.NotNull(await repository.GetByIdAsync(request.RunId));
        var failedDelivery = Assert.Single(await repository.ListPendingCreatedOutboxAsync(10));
        Assert.Equal(1, failedDelivery.AttemptCount);
        Assert.Contains(nameof(InvalidOperationException), failedDelivery.LastError, StringComparison.Ordinal);
        var firstEvent = Assert.Single(subscriber.Events);
        Assert.Equal(failedDelivery.EventId, firstEvent.EventId);
        Assert.Equal(Now, firstEvent.OccurredAtUtc);

        var retry = await coordinator.SubmitAsync(request);

        Assert.True(retry.IsSuccess);
        Assert.Empty(await repository.ListPendingCreatedOutboxAsync(10));
        Assert.Equal(2, subscriber.Events.Count);
        Assert.All(subscriber.Events, domainEvent =>
        {
            Assert.Equal(firstEvent.EventId, domainEvent.EventId);
            Assert.Equal(firstEvent.OccurredAtUtc, domainEvent.OccurredAtUtc);
            Assert.Equal(request.RunId, domainEvent.RunId);
        });
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task SqliteAdmissionFailureAccountingAndDeliverySurviveColdRestart()
    {
        await using var database = new TemporaryDatabase();
        var (run, plan) = CreateRun();
        Guid eventId;
        using (var materials = new SqliteProductionMaterialRepository(database.ConnectionString))
        using (var repository = new SqliteProductionRunRepository(database.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(
                run,
                plan,
                await ProductionRunTestMaterials.RegisterAsync(materials, run)));
            var pending = Assert.Single(await repository.ListPendingCreatedOutboxAsync(10));
            eventId = pending.EventId;
            Assert.Equal(run.Id, pending.RunId);
            Assert.Equal(Now, pending.OccurredAtUtc);
            Assert.Equal(0, pending.AttemptCount);
            Assert.Null(pending.LastError);
        }

        using (var restarted = new SqliteProductionRunRepository(database.ConnectionString))
        {
            var pending = Assert.Single(await restarted.ListPendingCreatedOutboxAsync(10));
            Assert.Equal(eventId, pending.EventId);
            await restarted.RecordCreatedOutboxFailureAsync(
                run.Id,
                "System.InvalidOperationException: subscriber unavailable");
        }

        using (var restarted = new SqliteProductionRunRepository(database.ConnectionString))
        {
            var failed = Assert.Single(await restarted.ListPendingCreatedOutboxAsync(10));
            Assert.Equal(eventId, failed.EventId);
            Assert.Equal(1, failed.AttemptCount);
            Assert.Equal(
                "System.InvalidOperationException: subscriber unavailable",
                failed.LastError);
            var subscriber = new RecordingCreatedSubscriber();
            using var dispatcher = new ProductionRunCreatedOutboxDispatcher(
                restarted,
                new RuntimeDomainEventPublisher([subscriber]));
            Assert.Equal(1, await dispatcher.DrainAsync());
            var delivered = Assert.Single(subscriber.Events);
            Assert.Equal(eventId, delivered.EventId);
            Assert.Equal(run.Id, delivered.RunId);
            Assert.Equal(Now, delivered.OccurredAtUtc);
            Assert.Empty(await restarted.ListPendingCreatedOutboxAsync(10));
        }

        using var finalRestart = new SqliteProductionRunRepository(database.ConnectionString);
        Assert.Empty(await finalRestart.ListPendingCreatedOutboxAsync(10));
        Assert.NotNull(await finalRestart.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task EventSpecificFailureDoesNotStarveOtherAdmissionsInTheSameBatch()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (failingRun, failingPlan) = CreateRun("created-failing", "SN-CREATED-FAILING");
        var (successfulRun, successfulPlan) = CreateRun("created-successful", "SN-CREATED-SUCCESSFUL");
        Assert.True(await repository.TryAddAsync(
            failingRun,
            failingPlan,
            await ProductionRunTestMaterials.RegisterAsync(materials, failingRun)));
        Assert.True(await repository.TryAddAsync(
            successfulRun,
            successfulPlan,
            await ProductionRunTestMaterials.RegisterAsync(materials, successfulRun)));
        var publisher = new SelectiveFailingPublisher(failingRun.Id);
        using var dispatcher = new ProductionRunCreatedOutboxDispatcher(repository, publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DrainAsync().AsTask());

        var pending = Assert.Single(await repository.ListPendingCreatedOutboxAsync(10));
        Assert.Equal(failingRun.Id, pending.RunId);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Contains(publisher.Events, domainEvent => domainEvent.RunId == failingRun.Id);
        Assert.Contains(publisher.Events, domainEvent => domainEvent.RunId == successfulRun.Id);
    }

    private static SubmitProductionRunRequest CreateRequest()
    {
        var (run, plan) = CreateRun();
        return new SubmitProductionRunRequest(
            run.Id,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            run.ProductionLineDefinitionId,
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ActorId,
            run.EntryOperationId,
            plan.Operations,
            run.RouteTransitions);
    }

    private static (ProductionRun Run, ProductionRunExecutionPlan Plan) CreateRun(
        string suffix = "created",
        string serialNumber = "SN-CREATED-001")
    {
        var operationId = $"operation.{suffix}";
        var operation = new OperationExecutionPlan(
            operationId,
            $"station.{suffix}",
            new StationId($"station.{suffix}"),
            new ConfigurationSnapshotId($"configuration.{suffix}"),
            new RecipeSnapshotId($"recipe.{suffix}"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId($"process.{suffix}"),
                new ProcessVersionId($"process-version.{suffix}"),
                []));
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project.{suffix}",
            $"application.{suffix}",
            $"snapshot.{suffix}",
            $"topology.{suffix}",
            $"line.{suffix}",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", serialNumber),
            null,
            null,
            $"operator.{suffix}",
            operationId,
            Now,
            [operation.Definition],
            [new RouteTransitionDefinition(
                $"route.{suffix}.completed",
                operationId,
                targetOperationId: null,
                RuntimeRouteTransitionKind.Sequence,
                terminalDisposition: ProductDisposition.Completed)]);
        return (run, new ProductionRunExecutionPlan(run.Id, [operation]));
    }

    private sealed class FailOnceCreatedSubscriber : IRuntimeDomainEventSubscriber
    {
        private int _attemptCount;

        public List<ProductionRunCreatedDomainEvent> Events { get; } = [];

        public ValueTask HandleAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(Assert.Single(domainEvents.OfType<ProductionRunCreatedDomainEvent>()));
            return Interlocked.Increment(ref _attemptCount) == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("Created-event subscriber is unavailable."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCreatedSubscriber : IRuntimeDomainEventSubscriber
    {
        public List<ProductionRunCreatedDomainEvent> Events { get; } = [];

        public ValueTask HandleAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(Assert.Single(domainEvents.OfType<ProductionRunCreatedDomainEvent>()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelectiveFailingPublisher(ProductionRunId failingRunId) :
        IRuntimeDomainEventPublisher
    {
        public List<ProductionRunCreatedDomainEvent> Events { get; } = [];

        public ValueTask PublishAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domainEvent = Assert.Single(domainEvents.OfType<ProductionRunCreatedDomainEvent>());
            Events.Add(domainEvent);
            return domainEvent.RunId == failingRunId
                ? ValueTask.FromException(
                    new InvalidOperationException("This event is rejected by the subscriber."))
                : ValueTask.CompletedTask;
        }
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

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-created-outbox-{Guid.NewGuid():N}.sqlite");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
