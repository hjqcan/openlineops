using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed class StationJobOutboxDispatcher
{
    private readonly IStationJobStore _store;
    private readonly IStationAgentMessagePublisher _publisher;
    private readonly IClock _clock;

    public StationJobOutboxDispatcher(
        IStationJobStore store,
        IStationAgentMessagePublisher publisher,
        IClock clock)
    {
        _store = store;
        _publisher = publisher;
        _clock = clock;
    }

    public async ValueTask<int> DispatchAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var messages = await _store
            .ListPendingOutboxAsync(maximumCount, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        var dispatched = 0;
        foreach (var message in messages)
        {
            try
            {
                await _publisher.PublishAsync(message.Kind, message.PayloadJson, cancellationToken)
                    .ConfigureAwait(false);
                await _store.AcknowledgeOutboxAsync(message.MessageId, _clock.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
                dispatched++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var seconds = Math.Min(300, 1 << Math.Min(message.AttemptCount, 8));
                await _store.RecordOutboxFailureAsync(
                        message.MessageId,
                        _clock.UtcNow.AddSeconds(seconds),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return dispatched;
    }
}
