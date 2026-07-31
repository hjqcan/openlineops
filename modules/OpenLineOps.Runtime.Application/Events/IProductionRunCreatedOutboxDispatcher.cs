namespace OpenLineOps.Runtime.Application.Events;

public interface IProductionRunCreatedOutboxDispatcher
{
    ValueTask<int> DrainAsync(CancellationToken cancellationToken = default);
}
