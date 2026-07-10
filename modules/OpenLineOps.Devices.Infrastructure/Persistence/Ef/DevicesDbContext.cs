using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public sealed class DevicesDbContext(
    DbContextOptions<DevicesDbContext> options,
    IntegrationEventPublicationPolicy? integrationEventPublicationPolicy = null,
    IIntegrationEventPublisher? integrationEventPublisher = null,
    ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null,
    IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null,
    ILogger<BaseDbContext>? logger = null)
    : BaseDbContext(
        options,
        integrationEventPublicationPolicy,
        integrationEventPublisher: integrationEventPublisher,
        transactionalIntegrationEventPublisher: transactionalIntegrationEventPublisher,
        integrationEventTransactionCoordinator: integrationEventTransactionCoordinator,
        logger: logger)
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
