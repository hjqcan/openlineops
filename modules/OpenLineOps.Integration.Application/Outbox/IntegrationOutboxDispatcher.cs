using OpenLineOps.Integration.Application.Serialization;

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
        cancellationToken.ThrowIfCancellationRequested();
        if (connector is IIntegrationConnectorReadiness { IsReady: false })
        {
            return 0;
        }

        var pending = await store.ListReadyAsync(maximumCount, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        var delivered = 0;
        foreach (var message in pending)
        {
            if (!string.Equals(
                    IntegrationMessageCodec.ComputeSha256(message.PayloadJson),
                    message.ContentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Outbox message '{message.MessageId}' failed its content hash check.");
            }

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
            catch (IntegrationConnectorMessageConflictException exception)
            {
                await store.RecordFailureAsync(
                        message.MessageId,
                        message.AttemptCount,
                        CanonicalFailure(exception.Message),
                        nowUtc,
                        nowUtc,
                        deadLetter: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                break;
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
                        nowUtc,
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
        var initialTicks = _options.InitialRetryDelay.Ticks;
        var maximumTicks = _options.MaximumRetryDelay.Ticks;
        var ticks = multiplier > maximumTicks / initialTicks
            ? maximumTicks
            : Math.Min(initialTicks * multiplier, maximumTicks);
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
