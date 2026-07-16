using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.Domain.Abstractions.ValueObjects;

namespace OpenLineOps.Domain.Abstractions.Tests;

public sealed class AggregateRootTests
{
    [Fact]
    public void RaiseDomainEventAddsEventToAggregate()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        aggregate.MarkChanged();

        var domainEvent = Assert.Single(aggregate.DomainEvents);
        Assert.Equal("TestAggregate.Changed", domainEvent.EventName);
    }

    [Fact]
    public void ClearDomainEventsRemovesPendingEvents()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.MarkChanged();

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void AggregateRootImplementsNetDevPackAggregateRootContract()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        Assert.IsAssignableFrom<NetDevPack.Domain.IAggregateRoot>(aggregate);
    }

    [Fact]
    public async Task RepositoryContractExposesNetDevPackUnitOfWork()
    {
        using var repository = new TestRepository(new TestAggregate(Guid.NewGuid()));

        var committed = await repository.UnitOfWork.Commit();

        Assert.True(committed);
        Assert.IsAssignableFrom<IAggregateRepository<TestAggregate, Guid>>(repository);
    }

    [Fact]
    public void IntegrationEventDescriptorFactoryRecognizesInterfaceMarkedNetDevPackEvents()
    {
        var domainEvent = new InterfaceMarkedIntegrationEvent(Guid.NewGuid());

        var descriptor = Assert.Single(IntegrationEventDescriptorFactory.Create([domainEvent]));

        Assert.True(IntegrationEventDescriptorFactory.IsIntegrationEvent(domainEvent));
        Assert.Equal("Tests.InterfaceMarked", descriptor.EventName);
        Assert.Same(domainEvent, descriptor.Payload);
        Assert.Equal(domainEvent.AggregateId.ToString(), descriptor.BuildHeaders()["aggregate-id"]);
    }

    [Fact]
    public void IntegrationEventDescriptorFactoryRecognizesAttributeMarkedNetDevPackEvents()
    {
        var domainEvent = new AttributeMarkedIntegrationEvent(Guid.NewGuid());

        var descriptor = Assert.Single(IntegrationEventDescriptorFactory.Create([domainEvent]));

        Assert.True(IntegrationEventDescriptorFactory.IsIntegrationEvent(domainEvent));
        Assert.Equal("Tests.AttributeMarked", descriptor.EventName);
        Assert.Same(domainEvent, descriptor.Payload);
    }

    [Fact]
    public void IntegrationEventDescriptorFactoryRecognizesOpenLineOpsEvents()
    {
        var aggregateId = Guid.NewGuid();
        var domainEvent = new OpenLineOpsIntegrationEvent(aggregateId);

        var descriptor = Assert.Single(IntegrationEventDescriptorFactory.Create([domainEvent]));

        Assert.True(IntegrationEventDescriptorFactory.IsIntegrationEvent(domainEvent));
        Assert.Equal("Tests.OpenLineOps", descriptor.EventName);
        Assert.Same(domainEvent, descriptor.Payload);
        Assert.Equal(aggregateId.ToString(), descriptor.BuildHeaders()["aggregate-id"]);
    }

    [Fact]
    public void IntegrationDtoConverterRegistryConvertsRegisteredDomainEvents()
    {
        var aggregateId = Guid.NewGuid();
        var domainEvent = new OpenLineOpsIntegrationEvent(aggregateId);
        var registry = new IntegrationDtoConverterRegistry([new TestIntegrationDtoConverter()]);

        var payload = Assert.IsType<TestIntegrationDto>(registry.ConvertOrOriginal(domainEvent));

        Assert.Equal(aggregateId, payload.AggregateId);
    }

    [Fact]
    public void ValueObjectUsesNetDevPackStructuralEquality()
    {
        var left = new TestValueObject("axis-x", 10);
        var right = new TestValueObject("axis-x", 10);
        var other = new TestValueObject("axis-y", 10);

        Assert.Equal(left, right);
        Assert.NotEqual(left, other);
    }

    private sealed class TestAggregate(Guid id) : AggregateRoot<Guid>(id)
    {
        public void MarkChanged()
        {
            RaiseDomainEvent(new TestAggregateChanged(Id));
        }
    }

    private sealed record TestAggregateChanged(Guid AggregateId)
        : DomainEvent("TestAggregate.Changed");

    private sealed class TestRepository(TestAggregate aggregate) : IAggregateRepository<TestAggregate, Guid>
    {
        public NetDevPack.Data.IUnitOfWork UnitOfWork { get; } = new TestUnitOfWork();

        public Task<TestAggregate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(aggregate.Id == id ? aggregate : null);
        }

        public Task<IReadOnlyCollection<TestAggregate>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<TestAggregate>>([aggregate]);
        }

        public IQueryable<TestAggregate> GetQueryable()
        {
            return new[] { aggregate }.AsQueryable();
        }

        public void Add(TestAggregate model)
        {
        }

        public void Update(TestAggregate model)
        {
        }

        public void Remove(TestAggregate model)
        {
        }

        public Task RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public Task<bool> Commit()
        {
            return Task.FromResult(true);
        }
    }

    private sealed class InterfaceMarkedIntegrationEvent(Guid aggregateId)
        : NetDevPack.Messaging.DomainEvent(aggregateId), IIntegrationEvent
    {
        public string EventName => "Tests.InterfaceMarked";
    }

    [IntegrationEvent("Tests.AttributeMarked")]
    private sealed class AttributeMarkedIntegrationEvent(Guid aggregateId)
        : NetDevPack.Messaging.DomainEvent(aggregateId);

    private sealed record OpenLineOpsIntegrationEvent(Guid AggregateId)
        : DomainEvent("Tests.OpenLineOps"),
            IIntegrationEvent;

    private sealed record TestIntegrationDto(Guid AggregateId);

    private sealed class TestIntegrationDtoConverter : IIntegrationDtoConverter
    {
        public bool CanConvert(object domainEvent)
        {
            return domainEvent is OpenLineOpsIntegrationEvent;
        }

        public object Convert(object domainEvent)
        {
            return domainEvent switch
            {
                OpenLineOpsIntegrationEvent integrationEvent => new TestIntegrationDto(
                    integrationEvent.AggregateId),
                _ => throw new NotSupportedException()
            };
        }
    }

    private sealed class TestValueObject(string name, int value) : ValueObject
    {
        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return name;
            yield return value;
        }
    }
}
