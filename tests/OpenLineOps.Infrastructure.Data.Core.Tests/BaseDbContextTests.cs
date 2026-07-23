using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.Repositories;
using NetDevPackEvent = NetDevPack.Messaging.Event;
using OpenLineOpsDomainEvent = OpenLineOps.Domain.Abstractions.Events.DomainEvent;

namespace OpenLineOps.Infrastructure.Data.Core.Tests;

public sealed class BaseDbContextTests
{
    private static readonly IntegrationEventPublicationPolicy PostCommitPolicy =
        new(IntegrationEventPublicationMode.PostCommit);

    private static readonly IntegrationEventPublicationPolicy TransactionalPolicy =
        new(IntegrationEventPublicationMode.Transactional);

    [Fact]
    public async Task LocalOnlyEventsFailBeforePersistenceBecauseNoDispatcherPipelineExists()
    {
        await using var context = CreateContext();
        var aggregate = TestAggregate.CreateWithLocalEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-x");

        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Contains("has no dispatch pipeline", exception.Message, StringComparison.Ordinal);
        Assert.Single(aggregate.DomainEvents);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
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

            Assert.True(await repository.UnitOfWork.Commit());
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
    public async Task PostCommitPublishesOpenLineOpsIntegrationEventsAndThenClearsThem()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        await using var context = CreateContext(
            publicationPolicy: PostCommitPolicy,
            integrationEventPublisher: publisher);
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
    public async Task IntegrationEventsWithoutExplicitPolicyFailBeforePersistence()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        await using var context = CreateContext(integrationEventPublisher: publisher);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-policy");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Contains("explicit integration event publication policy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
        Assert.Empty(publisher.Published);
        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public async Task PostCommitWithoutPublisherFailsBeforePersistence()
    {
        await using var context = CreateContext(publicationPolicy: PostCommitPolicy);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-publisher");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Contains(nameof(IIntegrationEventPublisher), exception.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public async Task PostCommitFailureRethrowsAndRetriesUnpublishedEventWithoutResaving()
    {
        var publisher = new FailOnceIntegrationEventPublisher();
        await using var context = CreateContext(
            publicationPolicy: PostCommitPolicy,
            integrationEventPublisher: publisher);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-retry");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Equal("publisher unavailable", exception.Message);
        Assert.Equal(EntityState.Unchanged, context.Entry(aggregate).State);
        Assert.Single(aggregate.DomainEvents);
        Assert.Equal(1, await context.Aggregates.CountAsync());

        var retried = await context.CommitAsync();

        Assert.True(retried);
        Assert.Empty(aggregate.DomainEvents);
        Assert.Equal(2, publisher.AttemptCount);
        Assert.Single(publisher.Published);
        Assert.Equal(1, await context.Aggregates.CountAsync());
    }

    [Fact]
    public async Task PostCommitClearsEachPublishedEventSoRetryDoesNotDuplicateEarlierSuccesses()
    {
        var publisher = new FailOnSecondAttemptIntegrationEventPublisher();
        await using var context = CreateContext(
            publicationPolicy: PostCommitPolicy,
            integrationEventPublisher: publisher);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "first");
        aggregate.AddIntegrationEvent("second");
        context.Aggregates.Add(aggregate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        var remaining = Assert.IsType<TestAggregateIntegratedDomainEvent>(
            Assert.Single(aggregate.DomainEvents));
        Assert.Equal("second", remaining.Name);

        Assert.True(await context.CommitAsync());

        Assert.Empty(aggregate.DomainEvents);
        Assert.Equal(
            ["first", "second"],
            publisher.Published
                .Cast<TestAggregateIntegratedDomainEvent>()
                .Select(domainEvent => domainEvent.Name)
                .ToArray());
    }

    [Fact]
    public async Task TransactionalWithoutTransactionalPublisherFailsBeforePersistence()
    {
        await using var context = CreateContext(
            publicationPolicy: TransactionalPolicy,
            integrationEventPublisher: new CapturingIntegrationEventPublisher(),
            integrationEventTransactionCoordinator: new CapturingIntegrationEventTransactionCoordinator());
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-transactional-publisher");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Contains(nameof(ITransactionalIntegrationEventPublisher), exception.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public async Task TransactionalWithoutCoordinatorFailsBeforePersistence()
    {
        var publisher = new CapturingTransactionalIntegrationEventPublisher();
        await using var context = CreateContext(
            publicationPolicy: TransactionalPolicy,
            transactionalIntegrationEventPublisher: publisher);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-coordinator");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Contains(nameof(IIntegrationEventTransactionCoordinator), exception.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
        Assert.Empty(publisher.TransactionalPublished);
        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public async Task TransactionalModePublishesThroughCoordinatorAndClearsAfterSuccess()
    {
        var publisher = new CapturingTransactionalIntegrationEventPublisher();
        var coordinator = new CapturingIntegrationEventTransactionCoordinator();
        await using var context = CreateContext(
            publicationPolicy: TransactionalPolicy,
            transactionalIntegrationEventPublisher: publisher,
            integrationEventTransactionCoordinator: coordinator);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-z");
        context.Aggregates.Add(aggregate);

        var committed = await context.CommitAsync();

        Assert.True(committed);
        Assert.Equal(1, coordinator.CallCount);
        Assert.Empty(aggregate.DomainEvents);
        var domainEvent = Assert.IsType<TestAggregateIntegratedDomainEvent>(
            Assert.Single(publisher.TransactionalPublished));
        Assert.Equal(aggregate.Id, domainEvent.AggregateId);
    }

    [Fact]
    public async Task TransactionalPublishFailureRethrowsAndKeepsEventsAndEfStateRetryable()
    {
        var publisher = new ThrowingTransactionalIntegrationEventPublisher();
        var coordinator = new CapturingIntegrationEventTransactionCoordinator();
        await using var context = CreateContext(
            publicationPolicy: TransactionalPolicy,
            transactionalIntegrationEventPublisher: publisher,
            integrationEventTransactionCoordinator: coordinator);
        var aggregate = TestAggregate.CreateWithIntegrationEvent(
            new TestAggregateId(Guid.NewGuid()),
            "axis-transaction-failure");
        context.Aggregates.Add(aggregate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.CommitAsync());

        Assert.Equal("transactional publisher unavailable", exception.Message);
        Assert.Equal(1, coordinator.CallCount);
        Assert.Equal(EntityState.Added, context.Entry(aggregate).State);
        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public async Task NetDevPackIntegrationEventsUseTheSameExplicitPostCommitPolicy()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        await using var context = CreateContext(
            publicationPolicy: PostCommitPolicy,
            integrationEventPublisher: publisher);
        var aggregate = new TestNetDevPackAggregate("netdevpack-device");
        aggregate.MarkChanged();
        context.NetDevPackAggregates.Add(aggregate);

        Assert.True(await context.CommitAsync());

        Assert.Empty(aggregate.DomainEvents ?? Array.Empty<NetDevPackEvent>());
        Assert.IsType<TestNetDevPackChangedEvent>(Assert.Single(publisher.Published));
    }

    private static TestDataContext CreateContext(
        string? databaseName = null,
        IntegrationEventPublicationPolicy? publicationPolicy = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null)
    {
        var options = new DbContextOptionsBuilder<TestDataContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
            .Options;

        return new TestDataContext(
            options,
            publicationPolicy,
            integrationEventPublisher,
            transactionalIntegrationEventPublisher,
            integrationEventTransactionCoordinator);
    }

    private sealed class TestDataContext(
        DbContextOptions<TestDataContext> options,
        IntegrationEventPublicationPolicy? publicationPolicy = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null)
        : BaseDbContext(
            options,
            publicationPolicy,
            integrationEventPublisher,
            transactionalIntegrationEventPublisher,
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
            return new TestAggregate(id, name);
        }

        public static TestAggregate CreateWithLocalEvent(TestAggregateId id, string name)
        {
            var aggregate = new TestAggregate(id, name);
            aggregate.RaiseDomainEvent(new TestAggregateRenamedDomainEvent(id, name));

            return aggregate;
        }

        public static TestAggregate CreateWithIntegrationEvent(TestAggregateId id, string name)
        {
            var aggregate = new TestAggregate(id, name);
            aggregate.AddIntegrationEvent(name);

            return aggregate;
        }

        public void AddIntegrationEvent(string name)
        {
            RaiseDomainEvent(new TestAggregateIntegratedDomainEvent(Id, name));
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
            IIntegrationEvent;

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
        NetDevPack.Messaging.DomainEvent(aggregateId),
        IIntegrationEvent
    {
        public string EventName => "test.netdevpack.changed";
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

    private sealed class FailOnceIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public int AttemptCount { get; private set; }

        public List<object> Published { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                throw new InvalidOperationException("publisher unavailable");
            }

            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnSecondAttemptIntegrationEventPublisher : IIntegrationEventPublisher
    {
        private int _attemptCount;

        public List<object> Published { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            _attemptCount++;
            if (_attemptCount == 2)
            {
                throw new InvalidOperationException("second event failed");
            }

            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingTransactionalIntegrationEventPublisher : ITransactionalIntegrationEventPublisher
    {
        public List<object> TransactionalPublished { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Transactional mode must not use the PostCommit publisher path.");
        }

        public Task PublishTransactionalAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            TransactionalPublished.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTransactionalIntegrationEventPublisher : ITransactionalIntegrationEventPublisher
    {
        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Transactional mode must not use the PostCommit publisher path.");
        }

        public Task PublishTransactionalAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("transactional publisher unavailable");
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
            await publishIntegrationEventsAsync(cancellationToken).ConfigureAwait(false);

            return affectedRows;
        }
    }
}
