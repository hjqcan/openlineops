using Microsoft.EntityFrameworkCore;

namespace OpenLineOps.Infrastructure.Data.Core.EventBus;

public interface IIntegrationEventTransactionCoordinator
{
    Task<int> SaveChangesAndPublishAsync(
        DbContext dbContext,
        Func<CancellationToken, Task<int>> saveChangesAsync,
        Func<CancellationToken, Task> publishIntegrationEventsAsync,
        CancellationToken cancellationToken = default);
}
