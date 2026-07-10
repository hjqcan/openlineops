using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Events;

public interface IProductionRunTerminalOutboxHandler
{
    ValueTask HandleAsync(
        ProductionRunSnapshot run,
        CancellationToken cancellationToken = default);
}

public interface IProductionRunTerminalOutboxDispatcher
{
    ValueTask<int> DrainAsync(CancellationToken cancellationToken = default);
}
