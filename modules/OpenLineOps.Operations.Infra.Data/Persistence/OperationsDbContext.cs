using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Operations.Domain.Aggregates;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class OperationsDbContext(
    DbContextOptions<OperationsDbContext> options,
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
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";
    private bool _schemaReady;

    public DbSet<Alarm> Alarms => Set<Alarm>();

    public void EnsureSchemaReady()
    {
        if (_schemaReady || !IsMigrationManagedProvider())
        {
            return;
        }

        EnsureProviderStorageReady();
        Database.Migrate();
        _schemaReady = true;
    }

    public async Task EnsureSchemaReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady || !IsMigrationManagedProvider())
        {
            return;
        }

        EnsureProviderStorageReady();
        await Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OperationsDbContext).Assembly);
    }

    private void EnsureProviderStorageReady()
    {
        if (IsSqliteProvider())
        {
            SqliteOperationsStorage.EnsureDatabaseDirectory(Database.GetDbConnection().ConnectionString);
        }
    }

    private bool IsMigrationManagedProvider()
    {
        return IsSqliteProvider()
            || string.Equals(Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);
    }

    private bool IsSqliteProvider()
    {
        return string.Equals(Database.ProviderName, SqliteProviderName, StringComparison.Ordinal);
    }
}
