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
        DateTimeOffset receivedAtUtc)
    {
        MessageId = ApplicationGuard.RequiredCanonical(messageId, nameof(messageId));
        ContentSha256 = ApplicationGuard.Sha256(contentSha256, nameof(contentSha256));
        CanonicalContent = ApplicationGuard.RequiredCanonical(
            canonicalContent,
            nameof(canonicalContent));
        ReceivedAtUtc = ApplicationGuard.Utc(receivedAtUtc, nameof(receivedAtUtc));
    }

    public string MessageId { get; }

    public string ContentSha256 { get; }

    public string CanonicalContent { get; }

    public DateTimeOffset ReceivedAtUtc { get; }
}

public sealed record IntegrationInboxBeginResult(
    IntegrationInboxDisposition Disposition,
    string? ResponseJson);

public sealed record IntegrationInboxCompletion
{
    public IntegrationInboxCompletion(
        string messageId,
        string contentSha256,
        string responseMessageId,
        string responseJson,
        DateTimeOffset completedAtUtc)
    {
        MessageId = ApplicationGuard.RequiredCanonical(messageId, nameof(messageId));
        ContentSha256 = ApplicationGuard.Sha256(contentSha256, nameof(contentSha256));
        ResponseMessageId = ApplicationGuard.RequiredCanonical(
            responseMessageId,
            nameof(responseMessageId));
        ResponseJson = ApplicationGuard.RequiredCanonical(responseJson, nameof(responseJson));
        CompletedAtUtc = ApplicationGuard.Utc(completedAtUtc, nameof(completedAtUtc));
    }

    public string MessageId { get; }

    public string ContentSha256 { get; }

    public string ResponseMessageId { get; }

    public string ResponseJson { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

public interface IIntegrationInboxStore
{
    ValueTask<IntegrationInboxBeginResult> TryBeginAsync(
        IntegrationInboundMessage message,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAndEnqueueResponseAsync(
        IntegrationInboxCompletion completion,
        CancellationToken cancellationToken = default);
}

public interface IWorkRequestHandler
{
    ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class IntegrationMessageConflictException(string message) :
    InvalidOperationException(message);
