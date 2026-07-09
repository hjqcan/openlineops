using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public sealed class DevicesDbContext(
    DbContextOptions<DevicesDbContext> options,
    IDomainEventDispatcher? domainEventDispatcher = null,
    IIntegrationEventPublisher? integrationEventPublisher = null,
    IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null)
    : BaseDbContext(
        options,
        domainEventDispatcher,
        integrationEventPublisher: integrationEventPublisher,
        integrationEventTransactionCoordinator: integrationEventTransactionCoordinator)
{
    public DbSet<DeviceDefinition> DeviceDefinitions => Set<DeviceDefinition>();

    public DbSet<DeviceInstance> DeviceInstances => Set<DeviceInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DevicesDbContext).Assembly);
    }
}
