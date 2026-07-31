using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Application.Inbox;

public enum IntegrationInboxDisposition
{
    Started = 0,
    InProgress = 1,
    Replayed = 2
}

public sealed record IntegrationInboundMessage
{
    public IntegrationInboundMessage(
        string messageId,
        string contentSha256,
        string canonicalContent,
        DateTimeOffset receivedAtUtc,
        string actorId,
        string processingToken,
        DateTimeOffset processingLeaseExpiresAtUtc)
    {
        MessageId = ApplicationGuard.RequiredCanonical(messageId, nameof(messageId));
        ContentSha256 = ApplicationGuard.Sha256(contentSha256, nameof(contentSha256));
        CanonicalContent = ApplicationGuard.RequiredCanonical(
            canonicalContent,
            nameof(canonicalContent));
        ReceivedAtUtc = ApplicationGuard.Utc(receivedAtUtc, nameof(receivedAtUtc));
        ActorId = ApplicationGuard.RequiredCanonical(actorId, nameof(actorId));
        ProcessingToken = ApplicationGuard.RequiredCanonical(
            processingToken,
            nameof(processingToken));
        ProcessingLeaseExpiresAtUtc = ApplicationGuard.Utc(
            processingLeaseExpiresAtUtc,
            nameof(processingLeaseExpiresAtUtc));
        if (ProcessingLeaseExpiresAtUtc <= ReceivedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processingLeaseExpiresAtUtc),
                "The Inbox processing lease must expire after it is acquired.");
        }
    }

    public string MessageId { get; }

    public string ContentSha256 { get; }

    public string CanonicalContent { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public string ActorId { get; }

    public string ProcessingToken { get; }

    public DateTimeOffset ProcessingLeaseExpiresAtUtc { get; }
}

public sealed record IntegrationInboxBeginResult(
    IntegrationInboxDisposition Disposition,
    string? ResponseJson,
    string? ProcessingToken)
{
    public string? ResponseMessageId { get; init; }

    public string? ResponseSha256 { get; init; }
}

public sealed record IntegrationInboxClaimAudit(
    long Sequence,
    string MessageId,
    string ActorId,
    string ProcessingToken,
    DateTimeOffset ClaimedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc,
    bool Reclaimed);

public sealed record IntegrationInboxCompletion
{
    public IntegrationInboxCompletion(
        string messageId,
        string contentSha256,
        string responseMessageId,
        string responseJson,
        DateTimeOffset completedAtUtc,
        string processingToken)
    {
        MessageId = ApplicationGuard.RequiredCanonical(messageId, nameof(messageId));
        ContentSha256 = ApplicationGuard.Sha256(contentSha256, nameof(contentSha256));
        ResponseMessageId = ApplicationGuard.RequiredCanonical(
            responseMessageId,
            nameof(responseMessageId));
        ResponseJson = ApplicationGuard.RequiredCanonical(responseJson, nameof(responseJson));
        ResponseSha256 = IntegrationMessageCodec.ComputeSha256(ResponseJson);
        CompletedAtUtc = ApplicationGuard.Utc(completedAtUtc, nameof(completedAtUtc));
        ProcessingToken = ApplicationGuard.RequiredCanonical(
            processingToken,
            nameof(processingToken));
    }

    public string MessageId { get; }

    public string ContentSha256 { get; }

    public string ResponseMessageId { get; }

    public string ResponseJson { get; }

    public string ResponseSha256 { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public string ProcessingToken { get; }
}

public interface IIntegrationInboxStore
{
    ValueTask<IntegrationInboxBeginResult> TryBeginAsync(
        IntegrationInboundMessage message,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAndEnqueueResponseAsync(
        IntegrationInboxCompletion completion,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IntegrationInboxClaimAudit>> ListClaimAuditAsync(
        string messageId,
        CancellationToken cancellationToken = default);
}

public interface IWorkRequestHandler
{
    /// <summary>
    /// Applies one request using <see cref="WorkRequest.Id"/> as the business
    /// idempotency key. A processing lease can be reclaimed after a crash, so
    /// implementations must return the same response for an exact replay and
    /// reject reuse of the request identity with different content.
    /// </summary>
    ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorkRequestHandlerReadiness
{
    bool IsReady { get; }

    string? UnavailabilityReason { get; }
}

public sealed class IntegrationMessageConflictException(string message) :
    InvalidOperationException(message);

public sealed class IntegrationInboxLeaseLostException(string message) :
    InvalidOperationException(message);
