namespace OpenLineOps.Integration.Application.Outbox;

public sealed record IntegrationOutboundMessage(
    long Sequence,
    string MessageId,
    string CorrelationId,
    string ContentSha256,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc);

public sealed record IntegrationReplayAudit(
    long Sequence,
    string MessageId,
    string ActorId,
    string Reason,
    DateTimeOffset ReplayedAtUtc,
    int PreviousAttemptCount);

public sealed record IntegrationOutboxFailureAudit(
    long Sequence,
    string MessageId,
    int AttemptCount,
    string Failure,
    DateTimeOffset FailedAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    bool DeadLettered);

public interface IIntegrationOutboxStore
{
    ValueTask<IReadOnlyList<IntegrationOutboundMessage>> ListReadyAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask MarkDeliveredAsync(
        string messageId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask RecordFailureAsync(
        string messageId,
        int expectedAttemptCount,
        string failure,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        bool deadLetter,
        CancellationToken cancellationToken = default);

    ValueTask RequeueDeadLetterAsync(
        string messageId,
        string actorId,
        string reason,
        DateTimeOffset replayedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IntegrationReplayAudit>> ListReplayAuditAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IntegrationOutboxFailureAudit>> ListFailureAuditAsync(
        string messageId,
        CancellationToken cancellationToken = default);
}

public interface IIntegrationConnector
{
    /// <summary>
    /// Sends one durable message with at-least-once transport semantics.
    /// Implementations must use <see cref="IntegrationOutboundMessage.MessageId"/>
    /// as the remote idempotency key, treat an exact duplicate as success, and
    /// reject reuse of that identity with different content by throwing
    /// <see cref="IntegrationConnectorMessageConflictException"/>.
    /// </summary>
    ValueTask SendAsync(
        IntegrationOutboundMessage message,
        CancellationToken cancellationToken = default);
}

public interface IIntegrationConnectorReadiness
{
    /// <summary>
    /// Gets whether a send can currently be attempted. A false value pauses
    /// dispatch without consuming retry attempts or changing durable messages.
    /// </summary>
    bool IsReady { get; }

    string? UnavailabilityReason { get; }
}

public sealed class IntegrationConnectorMessageConflictException(string message) :
    InvalidOperationException(message);

public sealed record IntegrationOutboxDispatchOptions
{
    public IntegrationOutboxDispatchOptions(
        int maximumAttempts = 5,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        var initial = initialRetryDelay ?? TimeSpan.FromSeconds(1);
        var maximum = maximumRetryDelay ?? TimeSpan.FromMinutes(1);
        if (initial <= TimeSpan.Zero
            || maximum < initial
            || initial.Ticks % TimeSpan.TicksPerMillisecond != 0
            || maximum.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException(
                "Retry delays must use positive whole milliseconds and maximum must not be smaller than initial.");
        }

        MaximumAttempts = maximumAttempts;
        InitialRetryDelay = initial;
        MaximumRetryDelay = maximum;
    }

    public int MaximumAttempts { get; }

    public TimeSpan InitialRetryDelay { get; }

    public TimeSpan MaximumRetryDelay { get; }
}

public sealed record ManualOutboxReplayRequest
{
    public ManualOutboxReplayRequest(
        string messageId,
        string actorId,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        MessageId = ApplicationGuard.RequiredCanonical(messageId, nameof(messageId));
        ActorId = ApplicationGuard.RequiredCanonical(actorId, nameof(actorId));
        Reason = ApplicationGuard.RequiredCanonical(reason, nameof(reason));
        RequestedAtUtc = ApplicationGuard.Utc(requestedAtUtc, nameof(requestedAtUtc));
    }

    public string MessageId { get; }

    public string ActorId { get; }

    public string Reason { get; }

    public DateTimeOffset RequestedAtUtc { get; }
}

public interface IIntegrationReplayAuthorizer
{
    ValueTask<bool> IsAuthorizedAsync(
        ManualOutboxReplayRequest request,
        CancellationToken cancellationToken = default);
}
