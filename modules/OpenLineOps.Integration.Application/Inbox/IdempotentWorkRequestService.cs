using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Application.Inbox;

public enum WorkRequestProcessingOutcome
{
    Processed = 0,
    Replayed = 1,
    InProgress = 2
}

public sealed record WorkRequestProcessingResult(
    WorkRequestProcessingOutcome Outcome,
    WorkResponse? Response);

public sealed class IdempotentWorkRequestService(
    IIntegrationInboxStore inbox,
    IWorkRequestHandler handler)
{
    public async ValueTask<WorkRequestProcessingResult> ProcessAsync(
        WorkRequest request,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status != WorkRequestStatus.Received)
        {
            throw new ArgumentException(
                "Only a Received work request can enter the integration Inbox.",
                nameof(request));
        }

        ApplicationGuard.Utc(receivedAtUtc, nameof(receivedAtUtc));
        var requestJson = IntegrationMessageCodec.Encode(request);
        var requestSha256 = IntegrationMessageCodec.ComputeSha256(requestJson);
        var begin = await inbox.TryBeginAsync(
                new IntegrationInboundMessage(
                    request.Id.Value,
                    requestSha256,
                    requestJson,
                    receivedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        if (begin.Disposition == IntegrationInboxDisposition.InProgress)
        {
            return new WorkRequestProcessingResult(
                WorkRequestProcessingOutcome.InProgress,
                null);
        }

        if (begin.Disposition == IntegrationInboxDisposition.Replayed)
        {
            var replay = IntegrationMessageCodec.DecodeResponse(
                begin.ResponseJson
                ?? throw new InvalidDataException(
                    "Completed Inbox entry has no persisted response."));
            ValidateResponse(request, replay);
            return new WorkRequestProcessingResult(
                WorkRequestProcessingOutcome.Replayed,
                replay);
        }

        if (begin.Disposition != IntegrationInboxDisposition.Started)
        {
            throw new InvalidDataException(
                $"Inbox returned unsupported disposition '{begin.Disposition}'.");
        }

        var response = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Work request handler returned no response.");
        ValidateResponse(request, response);
        var responseJson = IntegrationMessageCodec.Encode(response);
        await inbox.CompleteAndEnqueueResponseAsync(
                new IntegrationInboxCompletion(
                    request.Id.Value,
                    requestSha256,
                    response.Id.Value,
                    responseJson,
                    response.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkRequestProcessingResult(
            WorkRequestProcessingOutcome.Processed,
            response);
    }

    private static void ValidateResponse(WorkRequest request, WorkResponse response)
    {
        if (response.RequestId != request.Id
            || response.WorkOrderId != request.WorkOrderId)
        {
            throw new InvalidDataException(
                "Work response request and work order identities must match the Inbox request.");
        }
    }
}
