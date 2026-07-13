using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresProductionRunCreatedOutboxIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 4, 0, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task AdmissionAndCreatedEventFailureStateSurviveColdRestart()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var operationId = $"operation-created-{suffix}";
        var operation = new OperationExecutionPlan(
            operationId,
            $"station-created-{suffix}",
            new StationId($"station-created-{suffix}"),
            new ConfigurationSnapshotId($"configuration-created-{suffix}"),
            new RecipeSnapshotId($"recipe-created-{suffix}"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId($"process-created-{suffix}"),
                new ProcessVersionId($"process-version-created-{suffix}"),
                []));
        var unitId = ProductionUnitId.New();
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project-created-{suffix}",
            $"application-created-{suffix}",
            $"snapshot-created-{suffix}",
            $"topology-created-{suffix}",
            $"line-created-{suffix}",
            unitId,
            new ProductionUnitIdentity(
                $"product-created-{suffix}",
                "serialNumber",
                $"SN-{suffix}"),
            null,
            null,
            $"operator-created-{suffix}",
            operationId,
            Now,
            [operation.Definition],
            [new RouteTransitionDefinition(
                $"route-created-{suffix}",
                operationId,
                targetOperationId: null,
                RuntimeRouteTransitionKind.Sequence,
                terminalDisposition: ProductDisposition.Completed)]);
        var plan = new ProductionRunExecutionPlan(run.Id, [operation]);
        Guid eventId;

        using (var materials = new PostgreSqlProductionMaterialRepository(fixture.ConnectionString))
        using (var store = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
                unitId,
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value,
                null,
                run.ActorId,
                Now.AddMinutes(-1))));
            var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await materials.GetProductionUnitAsync(unitId));
            Assert.True(await store.TryAddAsync(
                run,
                plan,
                new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
            var pending = Assert.Single(
                await store.ListPendingCreatedOutboxAsync(256),
                item => item.RunId == run.Id);
            eventId = pending.EventId;
            Assert.Equal(Now, pending.OccurredAtUtc);
            Assert.Equal(0, pending.AttemptCount);
        }

        using (var restarted = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            var pending = Assert.Single(
                await restarted.ListPendingCreatedOutboxAsync(256),
                item => item.RunId == run.Id);
            Assert.Equal(eventId, pending.EventId);
            await restarted.RecordCreatedOutboxFailureAsync(
                run.Id,
                "System.InvalidOperationException: subscriber unavailable");
        }

        using (var restarted = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            var failed = Assert.Single(
                await restarted.ListPendingCreatedOutboxAsync(256),
                item => item.RunId == run.Id);
            Assert.Equal(eventId, failed.EventId);
            Assert.Equal(1, failed.AttemptCount);
            Assert.Equal(
                "System.InvalidOperationException: subscriber unavailable",
                failed.LastError);
            var publisher = new RecordingRuntimePublisher();
            using var dispatcher = new ProductionRunCreatedOutboxDispatcher(restarted, publisher);
            Assert.True(await dispatcher.DrainAsync() > 0);
            var delivered = Assert.Single(
                publisher.CreatedEvents,
                domainEvent => domainEvent.RunId == run.Id);
            Assert.Equal(eventId, delivered.EventId);
            Assert.Equal(Now, delivered.OccurredAtUtc);
        }

        using var finalRestart = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        Assert.DoesNotContain(
            await finalRestart.ListPendingCreatedOutboxAsync(256),
            item => item.RunId == run.Id);
        Assert.NotNull(await finalRestart.GetByIdAsync(run.Id));
    }

    private sealed class RecordingRuntimePublisher : IRuntimeDomainEventPublisher
    {
        public List<ProductionRunCreatedDomainEvent> CreatedEvents { get; } = [];

        public ValueTask PublishAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedEvents.AddRange(domainEvents.OfType<ProductionRunCreatedDomainEvent>());
            return ValueTask.CompletedTask;
        }
    }
}
