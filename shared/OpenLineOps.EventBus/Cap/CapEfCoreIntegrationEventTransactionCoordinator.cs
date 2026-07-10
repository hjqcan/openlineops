using DotNetCore.CAP;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.EventBus.Cap;

public sealed class CapEfCoreIntegrationEventTransactionCoordinator(ICapPublisher capPublisher)
    : IIntegrationEventTransactionCoordinator
{
    public async Task<int> SaveChangesAndPublishAsync(
        DbContext dbContext,
        Func<CancellationToken, Task<int>> saveChangesAsync,
        Func<CancellationToken, Task> publishIntegrationEventsAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(saveChangesAsync);
        ArgumentNullException.ThrowIfNull(publishIntegrationEventsAsync);

        await using var transaction = dbContext.Database.BeginTransaction(
            capPublisher,
            autoCommit: false);
        try
        {
            var affectedRows = await saveChangesAsync(cancellationToken).ConfigureAwait(false);
            await publishIntegrationEventsAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return affectedRows;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
