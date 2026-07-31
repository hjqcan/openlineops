namespace OpenLineOps.Integration.Application.Outbox;

public sealed class IntegrationOutboxDispatcher(
    IIntegrationOutboxStore store,
    IIntegrationConnector connector,
    IntegrationOutboxDispatchOptions? options = null)
{
    private readonly IntegrationOutboxDispatchOptions _options =
        options ?? new IntegrationOutboxDispatchOptions();

    public async ValueTask<int> DispatchAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ApplicationGuard.Utc(nowUtc, nameof(nowUtc));
        var pending = await store.ListReadyAsync(maximumCount, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        var delivered = 0;
        foreach (var message in pending)
        {
            try
            {
                await connector.SendAsync(message, cancellationToken).ConfigureAwait(false);
                await store.MarkDeliveredAsync(message.MessageId, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var nextAttemptCount = checked(message.AttemptCount + 1);
                var deadLetter = nextAttemptCount >= _options.MaximumAttempts;
                var nextAttemptAtUtc = deadLetter
                    ? nowUtc
                    : nowUtc.Add(Backoff(nextAttemptCount));
                await store.RecordFailureAsync(
                        message.MessageId,
                        message.AttemptCount,
                        CanonicalFailure(exception.Message),
                        nextAttemptAtUtc,
                        deadLetter,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            }
        }

        return delivered;
    }

    private TimeSpan Backoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 20);
        var multiplier = 1L << exponent;
        var ticks = Math.Min(
            checked(_options.InitialRetryDelay.Ticks * multiplier),
            _options.MaximumRetryDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static string CanonicalFailure(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value)
            ? "Integration connector failed without a description."
            : value.Trim();
        return result.Length <= 4096 ? result : result[..4096];
    }
}
