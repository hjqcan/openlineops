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

public sealed record IntegrationInboxProcessingOptions
{
    public IntegrationInboxProcessingOptions(TimeSpan? leaseDuration = null)
    {
        var duration = leaseDuration ?? TimeSpan.FromMinutes(5);
        if (duration < TimeSpan.FromSeconds(1)
            || duration > TimeSpan.FromHours(1)
            || duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "The Inbox processing lease must be a whole number of milliseconds from one second through one hour.");
        }

        LeaseDuration = duration;
    }

    public TimeSpan LeaseDuration { get; }
}

public sealed class IdempotentWorkRequestService
{
    private readonly IIntegrationInboxStore _inbox;
    private readonly IWorkRequestHandler _handler;
    private readonly IntegrationInboxProcessingOptions _options;

    public IdempotentWorkRequestService(
        IIntegrationInboxStore inbox,
        IWorkRequestHandler handler)
        : this(inbox, handler, new IntegrationInboxProcessingOptions())
    {
    }

    public IdempotentWorkRequestService(
        IIntegrationInboxStore inbox,
        IWorkRequestHandler handler,
        IntegrationInboxProcessingOptions options)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

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
        var processingToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAtUtc = receivedAtUtc.Add(_options.LeaseDuration);
        var begin = await _inbox.TryBeginAsync(
                new IntegrationInboundMessage(
                    request.Id.Value,
                    requestSha256,
                    requestJson,
                    receivedAtUtc,
                    request.SourceSystem,
                    processingToken,
                    leaseExpiresAtUtc),
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
            var replayResponseJson = begin.ResponseJson
                ?? throw new InvalidDataException(
                    "Completed Inbox entry has no persisted response.");
            var responseSha256 = begin.ResponseSha256
                ?? throw new InvalidDataException(
                    "Completed Inbox entry has no persisted response hash.");
            if (!string.Equals(
                    IntegrationMessageCodec.ComputeSha256(replayResponseJson),
                    responseSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Completed Inbox response failed its content hash check.");
            }

            var replay = IntegrationMessageCodec.DecodeResponse(replayResponseJson);
            if (!string.Equals(
                    IntegrationMessageCodec.Encode(replay),
                    replayResponseJson,
                    StringComparison.Ordinal)
                || !string.Equals(
                    replay.Id.Value,
                    begin.ResponseMessageId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Completed Inbox response identity or canonical content is invalid.");
            }

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

        if (!string.Equals(
                begin.ProcessingToken,
                processingToken,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A started Inbox claim did not return its processing token.");
        }

        var response = await _handler
            .HandleAsync(request, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Work request handler returned no response.");
        ValidateResponse(request, response);
        var responseJson = IntegrationMessageCodec.Encode(response);
        await _inbox.CompleteAndEnqueueResponseAsync(
                new IntegrationInboxCompletion(
                    request.Id.Value,
                    requestSha256,
                    response.Id.Value,
                    responseJson,
                    response.OccurredAtUtc,
                    processingToken),
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
