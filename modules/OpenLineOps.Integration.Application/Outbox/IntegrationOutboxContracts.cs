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
}

public interface IIntegrationConnector
{
    ValueTask SendAsync(
        IntegrationOutboundMessage message,
        CancellationToken cancellationToken = default);
}

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
