using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Application.Events;

public interface IProductionRunTerminalOutboxHandler
{
    ValueTask HandleAsync(
        ProductionRunTerminalEvidence evidence,
        CancellationToken cancellationToken = default);
}

public interface IProductionRunTerminalOutboxDispatcher
{
    ValueTask<int> DrainAsync(CancellationToken cancellationToken = default);
}
