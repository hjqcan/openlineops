using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Application.Queries;

public enum IntegrationInboxState
{
    Pending = 0,
    Completed = 1
}

public sealed record IntegrationWorkRequestSnapshot(
    WorkRequest Request,
    DateTimeOffset ReceivedAtUtc,
    IntegrationInboxState State,
    string? ResponseMessageId,
    DateTimeOffset? CompletedAtUtc);

public sealed record IntegrationWorkResponseSnapshot(
    WorkResponse Response,
    long Sequence,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastError,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? DeadLetteredAtUtc);

public sealed record IntegrationDeadLetterSnapshot(
    long Sequence,
    string MessageId,
    string CorrelationId,
    int AttemptCount,
    string LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadLetteredAtUtc);

public interface IIntegrationQueryStore
{
    ValueTask<IntegrationWorkRequestSnapshot?> GetWorkRequestAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default);

    ValueTask<IntegrationWorkResponseSnapshot?> GetWorkResponseAsync(
        WorkResponseId responseId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IntegrationDeadLetterSnapshot>> ListDeadLettersAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}
