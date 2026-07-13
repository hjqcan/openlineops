using System.Collections.Concurrent;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class StationSafetyAcknowledgementCorrelator
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationResultDeliveryProcessor.StrictJson();
    private readonly ConcurrentDictionary<Guid, PendingEmergency> _emergency = [];
    private readonly ConcurrentDictionary<Guid, PendingSafeStop> _safeStops = [];
    private readonly ConcurrentDictionary<Guid, PendingCancellation> _cancellations = [];

    public StationSafetyAcknowledgementRegistration<EmergencyStopAcknowledged> Register(
        EmergencyStopRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = NewCompletion<EmergencyStopAcknowledged>();
        if (!_emergency.TryAdd(request.MessageId, new PendingEmergency(request, completion)))
        {
            throw new InvalidOperationException(
                $"Emergency Stop request {request.MessageId:D} is already pending.");
        }

        return new StationSafetyAcknowledgementRegistration<EmergencyStopAcknowledged>(
            completion.Task,
            () => _emergency.TryRemove(request.MessageId, out _));
    }

    public StationSafetyAcknowledgementRegistration<StationSafeStopAcknowledged> Register(
        StationSafeStopRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = NewCompletion<StationSafeStopAcknowledged>();
        if (!_safeStops.TryAdd(request.MessageId, new PendingSafeStop(request, completion)))
        {
            throw new InvalidOperationException(
                $"Safe Stop request {request.MessageId:D} is already pending.");
        }

        return new StationSafetyAcknowledgementRegistration<StationSafeStopAcknowledged>(
            completion.Task,
            () => _safeStops.TryRemove(request.MessageId, out _));
    }

    public StationSafetyAcknowledgementRegistration<StationJobCancelAcknowledged> Register(
        StationJobCancelRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = NewCompletion<StationJobCancelAcknowledged>();
        if (!_cancellations.TryAdd(
                request.MessageId,
                new PendingCancellation(request, completion)))
        {
            throw new InvalidOperationException(
                $"Station job cancellation request {request.MessageId:D} is already pending.");
        }

        return new StationSafetyAcknowledgementRegistration<StationJobCancelAcknowledged>(
            completion.Task,
            () => _cancellations.TryRemove(request.MessageId, out _));
    }

    public void Accept(StationCoordinatorTransportDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ValidateBaseEnvelope(delivery);
        switch (delivery.Type)
        {
            case nameof(EmergencyStopAcknowledged):
                {
                    var acknowledgement = Deserialize<EmergencyStopAcknowledged>(delivery.Body);
                    ValidateEnvelope(
                        delivery,
                        acknowledgement.MessageId,
                        acknowledgement.RequestMessageId,
                        acknowledgement.AgentId,
                        acknowledgement.StationId,
                        "emergency-stop-acknowledged");
                    ValidateCommon(
                        acknowledgement.IdempotencyKey,
                        acknowledgement.AcknowledgedAtUtc);
                    ValidateOutcome(
                        acknowledgement.Accepted,
                        acknowledgement.FailureCode,
                        acknowledgement.FailureReason);
                    if (_emergency.TryGetValue(
                            acknowledgement.RequestMessageId,
                            out var pending))
                    {
                        if (!Same(pending.Request, acknowledgement))
                        {
                            throw new InvalidDataException(
                                "Emergency Stop acknowledgement does not match its pending request.");
                        }

                        pending.Completion.TrySetResult(acknowledgement);
                    }

                    break;
                }
            case nameof(StationSafeStopAcknowledged):
                {
                    var acknowledgement = Deserialize<StationSafeStopAcknowledged>(delivery.Body);
                    ValidateEnvelope(
                        delivery,
                        acknowledgement.MessageId,
                        acknowledgement.RequestMessageId,
                        acknowledgement.AgentId,
                        acknowledgement.StationId,
                        "safe-stop-acknowledged");
                    ValidateCommon(
                        acknowledgement.IdempotencyKey,
                        acknowledgement.AcknowledgedAtUtc);
                    ValidateOutcome(
                        acknowledgement.Accepted,
                        acknowledgement.FailureCode,
                        acknowledgement.FailureReason);
                    if (_safeStops.TryGetValue(
                            acknowledgement.RequestMessageId,
                            out var pending))
                    {
                        if (!Same(pending.Request, acknowledgement))
                        {
                            throw new InvalidDataException(
                                "Safe Stop acknowledgement does not match its pending request.");
                        }

                        pending.Completion.TrySetResult(acknowledgement);
                    }

                    break;
                }
            case nameof(StationJobCancelAcknowledged):
                {
                    var acknowledgement = Deserialize<StationJobCancelAcknowledged>(delivery.Body);
                    ValidateEnvelope(
                        delivery,
                        acknowledgement.MessageId,
                        acknowledgement.RequestMessageId,
                        acknowledgement.AgentId,
                        acknowledgement.StationId,
                        "job-cancel-acknowledged");
                    ValidateCommon(
                        acknowledgement.IdempotencyKey,
                        acknowledgement.AcknowledgedAtUtc);
                    ValidateOutcome(
                        acknowledgement.Accepted,
                        acknowledgement.FailureCode,
                        acknowledgement.FailureReason);
                    if (_cancellations.TryGetValue(
                            acknowledgement.RequestMessageId,
                            out var pending))
                    {
                        if (!Same(pending.Request, acknowledgement))
                        {
                            throw new InvalidDataException(
                                "Station job cancellation acknowledgement does not match its pending request.");
                        }

                        pending.Completion.TrySetResult(acknowledgement);
                    }

                    break;
                }
            default:
                throw new InvalidDataException(
                    $"Unsupported Station safety acknowledgement type '{delivery.Type}'.");
        }
    }

    public void FailAll(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (var pending in _emergency.Values)
        {
            pending.Completion.TrySetException(exception);
        }

        foreach (var pending in _safeStops.Values)
        {
            pending.Completion.TrySetException(exception);
        }

        foreach (var pending in _cancellations.Values)
        {
            pending.Completion.TrySetException(exception);
        }

        _emergency.Clear();
        _safeStops.Clear();
        _cancellations.Clear();
    }

    private static bool Same(
        EmergencyStopRequested request,
        EmergencyStopAcknowledged acknowledgement) =>
        string.Equals(request.IdempotencyKey, acknowledgement.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(request.AgentId, acknowledgement.AgentId, StringComparison.Ordinal)
        && string.Equals(request.StationId, acknowledgement.StationId, StringComparison.Ordinal);

    private static bool Same(
        StationSafeStopRequested request,
        StationSafeStopAcknowledged acknowledgement) =>
        string.Equals(request.IdempotencyKey, acknowledgement.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(request.AgentId, acknowledgement.AgentId, StringComparison.Ordinal)
        && string.Equals(request.StationId, acknowledgement.StationId, StringComparison.Ordinal);

    private static bool Same(
        StationJobCancelRequested request,
        StationJobCancelAcknowledged acknowledgement) =>
        request.JobId == acknowledgement.JobId
        && string.Equals(request.IdempotencyKey, acknowledgement.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(request.AgentId, acknowledgement.AgentId, StringComparison.Ordinal)
        && string.Equals(request.StationId, acknowledgement.StationId, StringComparison.Ordinal);

    private static T Deserialize<T>(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<T>(body.Span, JsonOptions)
        ?? throw new InvalidDataException($"Station acknowledgement {typeof(T).Name} is null.");

    private static void ValidateBaseEnvelope(StationCoordinatorTransportDelivery delivery)
    {
        if (!string.Equals(delivery.ContentType, "application/json", StringComparison.Ordinal)
            || !string.Equals(delivery.ContentEncoding, "utf-8", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(delivery.Type)
            || string.IsNullOrWhiteSpace(delivery.AppId)
            || string.IsNullOrWhiteSpace(delivery.MessageId)
            || string.IsNullOrWhiteSpace(delivery.CorrelationId))
        {
            throw new InvalidDataException(
                "Station safety acknowledgement AMQP envelope is invalid.");
        }
    }

    private static void ValidateEnvelope(
        StationCoordinatorTransportDelivery delivery,
        Guid messageId,
        Guid requestMessageId,
        string agentId,
        string stationId,
        string routeSuffix)
    {
        if (messageId == Guid.Empty
            || requestMessageId == Guid.Empty
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(stationId)
            || !string.Equals(delivery.AppId, agentId, StringComparison.Ordinal)
            || !string.Equals(
                delivery.RoutingKey,
                StationTransportRoute.Event(stationId, routeSuffix),
                StringComparison.Ordinal)
            || !Guid.TryParseExact(delivery.MessageId, "D", out var envelopeMessageId)
            || envelopeMessageId != messageId
            || !Guid.TryParseExact(delivery.CorrelationId, "D", out var envelopeRequestId)
            || envelopeRequestId != requestMessageId)
        {
            throw new InvalidDataException(
                "Station safety acknowledgement AMQP identity does not match its body.");
        }
    }

    private static void ValidateCommon(string idempotencyKey, DateTimeOffset acknowledgedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || char.IsWhiteSpace(idempotencyKey[0])
            || char.IsWhiteSpace(idempotencyKey[^1])
            || acknowledgedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Station safety acknowledgement idempotency or timestamp is invalid.");
        }
    }

    private static void ValidateOutcome(
        bool accepted,
        string? failureCode,
        string? failureReason)
    {
        if ((accepted && (failureCode is not null || failureReason is not null))
            || (!accepted && (string.IsNullOrWhiteSpace(failureCode)
                              || string.IsNullOrWhiteSpace(failureReason))))
        {
            throw new InvalidDataException(
                "Station safety acknowledgement outcome evidence is invalid.");
        }
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PendingEmergency(
        EmergencyStopRequested Request,
        TaskCompletionSource<EmergencyStopAcknowledged> Completion);

    private sealed record PendingSafeStop(
        StationSafeStopRequested Request,
        TaskCompletionSource<StationSafeStopAcknowledged> Completion);

    private sealed record PendingCancellation(
        StationJobCancelRequested Request,
        TaskCompletionSource<StationJobCancelAcknowledged> Completion);
}

public sealed class StationSafetyAcknowledgementRegistration<TAcknowledgement> : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal StationSafetyAcknowledgementRegistration(
        Task<TAcknowledgement> task,
        Action release)
    {
        Task = task;
        _release = release;
    }

    public Task<TAcknowledgement> Task { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}
