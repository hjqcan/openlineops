using System.Runtime.ExceptionServices;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Events;

namespace OpenLineOps.Runtime.Application.Events;

public sealed class ProductionRunCreatedOutboxDispatcher :
    IProductionRunCreatedOutboxDispatcher,
    IDisposable
{
    private const int BatchSize = 32;
    private readonly IProductionRunRepository _repository;
    private readonly IRuntimeDomainEventPublisher _publisher;
    private readonly SemaphoreSlim _drainLock = new(1, 1);

    public ProductionRunCreatedOutboxDispatcher(
        IProductionRunRepository repository,
        IRuntimeDomainEventPublisher publisher)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public async ValueTask<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _drainLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processedCount = 0;
            while (true)
            {
                var pending = await _repository
                    .ListPendingCreatedOutboxAsync(BatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    return processedCount;
                }

                Exception? firstDeliveryFailure = null;
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var domainEvent = new ProductionRunCreatedDomainEvent(item.RunId)
                        {
                            EventId = item.EventId,
                            OccurredAtUtc = item.OccurredAtUtc
                        };
                        await _publisher
                            .PublishAsync([domainEvent], cancellationToken)
                            .ConfigureAwait(false);
                        await _repository
                            .MarkCreatedOutboxProcessedAsync(item.RunId, cancellationToken)
                            .ConfigureAwait(false);
                        processedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        var exceptionType = exception.GetType().FullName
                            ?? exception.GetType().Name;
                        var detail = exception.Message.Trim();
                        var error = detail.Length == 0
                            ? exceptionType
                            : $"{exceptionType}: {detail}";
                        await _repository
                            .RecordCreatedOutboxFailureAsync(
                                item.RunId,
                                error,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        firstDeliveryFailure ??= exception;
                    }
                }

                if (firstDeliveryFailure is not null)
                {
                    // Attempt every item in the selected batch so one event-specific poison
                    // subscriber cannot starve unrelated Production Runs behind it. Re-throw
                    // after durable accounting so the hosted service logs and backs off.
                    ExceptionDispatchInfo.Capture(firstDeliveryFailure).Throw();
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
