using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Application.Events;

public sealed class ProductionRunTerminalOutboxDispatcher :
    IProductionRunTerminalOutboxDispatcher,
    IDisposable
{
    private const int BatchSize = 32;
    private readonly IProductionRunRepository _repository;
    private readonly IProductionRunTerminalOutboxHandler[] _handlers;
    private readonly SemaphoreSlim _drainLock = new(1, 1);

    public ProductionRunTerminalOutboxDispatcher(
        IProductionRunRepository repository,
        IEnumerable<IProductionRunTerminalOutboxHandler> handlers)
    {
        _repository = repository;
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public async ValueTask<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_handlers.Length == 0)
        {
            throw new InvalidOperationException(
                "Production Run terminal outbox requires at least one registered handler.");
        }

        await _drainLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processedCount = 0;
            while (true)
            {
                var pending = await _repository
                    .ListPendingTerminalOutboxAsync(BatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    return processedCount;
                }

                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var handler in _handlers)
                        {
                            await handler.HandleAsync(item.Evidence, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await _repository
                            .MarkTerminalOutboxProcessedAsync(item.RunId, cancellationToken)
                            .ConfigureAwait(false);
                        processedCount++;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException
                                                       and not OutOfMemoryException)
                    {
                        var error = $"{exception.GetType().FullName ?? exception.GetType().Name}: {exception.Message}";
                        await _repository
                            .RecordTerminalOutboxFailureAsync(
                                item.RunId,
                                error,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        throw;
                    }
                }

                if (pending.Count < BatchSize)
                {
                    return processedCount;
                }
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    public void Dispose()
    {
        _drainLock.Dispose();
    }
}
