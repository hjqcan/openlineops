using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.Repositories;
using NetDevPackDomainEvent = NetDevPack.Messaging.DomainEvent;
using NetDevPackDomainEventDispatcher = NetDevPack.Messaging.IDomainEventDispatcher;
using NetDevPackEvent = NetDevPack.Messaging.Event;
using OpenLineOpsDomainEvent = OpenLineOps.Domain.Abstractions.Events.DomainEvent;
using OpenLineOpsDomainEventContract = OpenLineOps.Domain.Abstractions.Events.IDomainEvent;
using OpenLineOpsDomainEventDispatcher =
    OpenLineOps.Domain.Abstractions.Events.IDomainEventDispatcher;

namespace OpenLineOps.Infrastructure.Data.Core.Tests;

public sealed class BaseDbContextTests
{
    [Fact]
    public async Task CommitDispatchesOpenLineOpsDomainEventsAndClearsThem()
    {
        var dispatcher = new CapturingOpenLineOpsDomainEventDispatcher();
        await using var context = CreateContext(openLineOpsDispatcher: dispatcher);
        var aggregate = TestAggregate.Create(
            new TestAggregateId(Guid.NewGuid()),
            "axis-x");

        context.Aggregates.Add(aggregate);

        var committed = await context.CommitAsync();

        Assert.True(committed);
        Assert.Empty(aggregate.DomainEvents);
        var domainEvent = Assert.IsType<TestAggregateRenamedDomainEvent>(
            Assert.Single(dispatcher.Dispatched));
        Assert.Equal(aggregate.Id, domainEvent.AggregateId);
    }

    [Fact]
    public async Task BaseRepositoryPersistsAndQueriesStronglyTypedAggregateIds()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var aggregateId = new TestAggregateId(Guid.NewGuid());

        await using (var writeContext = CreateContext(databaseName: databaseName))
        {
            var repository = new TestAggregateRepository(writeContext);
            repository.Add(TestAggregate.Create(aggregateId, "fixture-light"));

            var committed = await repository.UnitOfWork.Commit();

            Assert.True(committed);
            Assert.Same(writeContext, repository.UnitOfWork);
        }

        await using var readContext = CreateContext(databaseName: databaseName);
        var readRepository = new TestAggregateRepository(readContext);

        var restored = await readRepository.GetByIdAsync(aggregateId);

        Assert.NotNull(restored);
        Assert.Equal("fixture-light", restored.Name);
    }

    [Fact]
    public async Task BaseRepositoryRemovesAggregatesByStronglyTypedId()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var aggregateId = new TestAggregateId(Guid.NewGuid());

        await using (var writeContext = CreateContext(databaseName: databaseName))
        {
            var repository = new TestAggregateRepository(writeContext);
            repository.Add(TestAggregate.Create(aggregateId, "motor"));
            await repository.UnitOfWork.Commit();
        }

        await using (var deleteContext = CreateContext(databaseName: databaseName))
        {
            var repository = new TestAggregateRepository(deleteContext);
            await repository.RemoveByIdAsync(aggregateId);
            await repository.UnitOfWork.Commit();
        }

        await using var readContext = CreateContext(databaseName: databaseName);
        var readRepository = new TestAggregateRepository(readContext);

        Assert.Null(await readRepository.GetByIdAsync(aggregateId));
    }

    [Fact]
    public void StronglyTypedIdConversionIsRegisteredOnEfModel()
    {
        using var context = CreateContext();

        var entityType = context.Model.FindEntityType(typeof(TestAggregate));
        var idProperty = entityType?.FindProperty(nameof(TestAggregate.Id));

        Assert.NotNull(idProperty);
        Assert.IsType<StronglyTypedIdValueConverter<TestAggregateId, Guid>>(
            idProperty.GetValueConverter());
    }

    [Fact]
    public async Task CommitDispatchesNetDevPackDomainEventsAndPublishesIntegrationEvents()
    {
        var dispatcher = new CapturingNetDevPackDomainEventDispatcher();
        var publisher = new CapturingIntegrationEventPublisher();
        await using var context = CreateContext(
            netDevPackDispatcher: dispatcher,
            integrationEventPublisher: publisher);
        var aggregate = new TestNetDevPackAggregate("netdevpack-device");
        aggregate.MarkChanged();

        context.NetDevPackAggregates.Add(aggregate);

        var committed = await context.CommitAsync();

        Assert.True(committed);
        Assert.Empty(aggregate.DomainEvents ?? Array.Empty<NetDevPackEvent>());
        Assert.IsType<TestNetDevPackChangedEvent>(Assert.Single(dispatcher.Dispatched));
        Assert.IsType<TestNetDevPackChangedEvent>(Assert.Single(publisher.Published));
    }

    [Fact]
    public async Task CommitPublishesOpenLineOpsIntegrationEvents()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        await using var context = CreateContext(integrationEventPublisher: publisher);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-y");

        context.Aggregates.Add(aggregate);

        var committed = await context.CommitAsync();

        Assert.True(committed);
        Assert.Empty(aggregate.DomainEvents);
        var domainEvent = Assert.IsType<TestAggregateIntegratedDomainEvent>(
            Assert.Single(publisher.Published));
        Assert.Equal(aggregate.Id, domainEvent.AggregateId);
    }

    [Fact]
    public async Task CommitPublishesIntegrationEventsThroughTransactionCoordinatorWhenAvailable()
    {
        var publisher = new CapturingTransactionalIntegrationEventPublisher();
        var coordinator = new CapturingIntegrationEventTransactionCoordinator();
        await using var context = CreateContext(
            integrationEventPublisher: publisher,
            integrationEventTransactionCoordinator: coordinator);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-z");

        context.Aggregates.Add(aggregate);

        var committed = await context.CommitAsync();

        Assert.True(committed);
        Assert.Equal(1, coordinator.CallCount);
        Assert.Empty(publisher.Published);
        var domainEvent = Assert.IsType<TestAggregateIntegratedDomainEvent>(
            Assert.Single(publisher.TransactionalPublished));
        Assert.Equal(aggregate.Id, domainEvent.AggregateId);
    }

    private static TestDataContext CreateContext(
        string? databaseName = null,
        OpenLineOpsDomainEventDispatcher? openLineOpsDispatcher = null,
        NetDevPackDomainEventDispatcher? netDevPackDispatcher = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null)
    {
        var options = new DbContextOptionsBuilder<TestDataContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
            .Options;

        return new TestDataContext(
            options,
            openLineOpsDispatcher,
            netDevPackDispatcher,
            integrationEventPublisher,
            integrationEventTransactionCoordinator);
    }

    private sealed class TestDataContext(
        DbContextOptions<TestDataContext> options,
        OpenLineOpsDomainEventDispatcher? openLineOpsDispatcher = null,
        NetDevPackDomainEventDispatcher? netDevPackDispatcher = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null)
        : BaseDbContext(
            options,
            openLineOpsDispatcher,
            netDevPackDispatcher,
            integrationEventPublisher,
            integrationEventTransactionCoordinator)
    {
        public DbSet<TestAggregate> Aggregates => Set<TestAggregate>();

        public DbSet<TestNetDevPackAggregate> NetDevPackAggregates => Set<TestNetDevPackAggregate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TestAggregate>(builder =>
            {
                builder.HasKey(aggregate => aggregate.Id);
                builder.Property(aggregate => aggregate.Id)
                    .HasStronglyTypedIdConversion<TestAggregateId, Guid>();
                builder.Property(aggregate => aggregate.Name).IsRequired();
                builder.Ignore(aggregate => aggregate.DomainEvents);
            });

            modelBuilder.Entity<TestNetDevPackAggregate>(builder =>
            {
                builder.HasKey(aggregate => aggregate.Id);
                builder.Property(aggregate => aggregate.Name).IsRequired();
            });
        }
    }

    private sealed class TestAggregateRepository(TestDataContext context)
        : BaseRepository<TestDataContext, TestAggregate, TestAggregateId>(context);

    private sealed class TestAggregate : AggregateRoot<TestAggregateId>
    {
        private TestAggregate()
            : base(new TestAggregateId(Guid.Empty))
        {
            Name = string.Empty;
        }

        private TestAggregate(TestAggregateId id, string name)
            : base(id)
        {
            Name = name;
        }

        public string Name { get; private set; }

        public static TestAggregate Create(TestAggregateId id, string name)
        {
            var aggregate = new TestAggregate(id, string.Empty);
            aggregate.Rename(name);

            return aggregate;
        }

        public static TestAggregate CreateWithIntegrationEvent(TestAggregateId id, string name)
        {
            var aggregate = new TestAggregate(id, string.Empty);
            aggregate.Name = name;
            aggregate.RaiseDomainEvent(new TestAggregateIntegratedDomainEvent(id, name));

            return aggregate;
        }

        private void Rename(string name)
        {
            Name = name;
            RaiseDomainEvent(new TestAggregateRenamedDomainEvent(Id, name));
        }
    }

    private sealed record TestAggregateId(Guid Value);

    private sealed record TestAggregateRenamedDomainEvent(
        TestAggregateId AggregateId,
        string Name)
        : OpenLineOpsDomainEvent("test.aggregate.renamed");

    private sealed record TestAggregateIntegratedDomainEvent(
        TestAggregateId AggregateId,
        string Name)
        : OpenLineOpsDomainEvent("test.aggregate.integrated"),
            IIntegrationEvent
    {
        public string Version => "v1";
    }

    private sealed class TestNetDevPackAggregate : NetDevPack.Domain.Entity,
        NetDevPack.Domain.IAggregateRoot
    {
        private TestNetDevPackAggregate()
        {
            Name = string.Empty;
        }

        public TestNetDevPackAggregate(string name)
        {
            Name = name;
        }

        public string Name { get; private set; }

        public void MarkChanged()
        {
            AddDomainEvent(new TestNetDevPackChangedEvent(Id));
        }
    }

    private sealed class TestNetDevPackChangedEvent(Guid aggregateId) :
        NetDevPackDomainEvent(aggregateId),
        IIntegrationEvent
    {
        public string EventName => "test.netdevpack.changed";

        public string Version => "v1";
    }

    private sealed class CapturingOpenLineOpsDomainEventDispatcher :
        OpenLineOpsDomainEventDispatcher
    {
        public List<OpenLineOpsDomainEventContract> Dispatched { get; } = [];

        public Task DispatchAsync(
            IReadOnlyCollection<OpenLineOpsDomainEventContract> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Dispatched.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingNetDevPackDomainEventDispatcher :
        NetDevPackDomainEventDispatcher
    {
        public List<NetDevPackDomainEvent> Dispatched { get; } = [];

        public Task DispatchAsync(IEnumerable<NetDevPackDomainEvent> domainEvents)
        {
            Dispatched.AddRange(domainEvents);
            return Task.CompletedTask;
        }

        public void Dispatch(IEnumerable<NetDevPackDomainEvent> domainEvents)
        {
            Dispatched.AddRange(domainEvents);
        }
    }

    private sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingTransactionalIntegrationEventPublisher : ITransactionalIntegrationEventPublisher
    {
        public List<object> Published { get; } = [];

        public List<object> TransactionalPublished { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }

        public Task PublishTransactionalAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            TransactionalPublished.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingIntegrationEventTransactionCoordinator :
        IIntegrationEventTransactionCoordinator
    {
        public int CallCount { get; private set; }

        public async Task<int> SaveChangesAndPublishAsync(
            DbContext dbContext,
            Func<CancellationToken, Task<int>> saveChangesAsync,
            Func<CancellationToken, Task> publishIntegrationEventsAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dbContext);

            CallCount++;

            var affectedRows = await saveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (affectedRows > 0)
            {
                await publishIntegrationEventsAsync(cancellationToken).ConfigureAwait(false);
            }

            return affectedRows;
        }
    }
}
