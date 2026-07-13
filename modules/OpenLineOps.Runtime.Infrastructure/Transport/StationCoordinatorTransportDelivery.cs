using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed record StationCoordinatorTransportDelivery(
    ulong DeliveryTag,
    string? ContentType,
    string? ContentEncoding,
    string? Type,
    string? AppId,
    string? MessageId,
    string? CorrelationId,
    string RoutingKey,
    bool Redelivered,
    ReadOnlyMemory<byte> Body);

public interface IStationCoordinatorTransportSettlement
{
    ValueTask AcknowledgeAsync(ulong deliveryTag, CancellationToken cancellationToken = default);

    ValueTask RejectAsync(
        ulong deliveryTag,
        bool requeue,
        CancellationToken cancellationToken = default);
}

public sealed record StationCoordinatorPublication(
    string Exchange,
    string RoutingKey,
    string Type,
    string AppId,
    Guid MessageId,
    Guid CorrelationId,
    byte? Priority,
    ReadOnlyMemory<byte> Body);

public interface IStationCoordinatorConfirmedPublicationTransport
{
    ValueTask PublishAsync(
        StationCoordinatorPublication publication,
        CancellationToken cancellationToken = default);
}

public static class StationCoordinatorPublicationFactory
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationResultDeliveryProcessor.StrictJson();

    public static StationCoordinatorPublication Create(
        StationCoordinatorTransportOptions options,
        StationJobRequested request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        StationMessageContract.Validate(request);
        if (request.MessageId == Guid.Empty
            || request.JobId == Guid.Empty
            || request.ProductionRunId == Guid.Empty
            || request.ProductionUnitId == Guid.Empty
            || request.RuntimeSessionId == Guid.Empty)
        {
            throw new InvalidDataException("Station job publication identity is incomplete.");
        }

        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.AgentId, nameof(request.AgentId));
        _ = Required(request.StationId, nameof(request.StationId));
        if (request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Station job publication timestamp must use UTC offset zero.");
        }

        return new StationCoordinatorPublication(
            options.JobExchange,
            $"station.{request.AgentId}.{request.StationId}",
            nameof(StationJobRequested),
            options.CoordinatorId,
            request.MessageId,
            request.JobId,
            null,
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));
    }

    public static StationCoordinatorPublication Create(
        StationCoordinatorTransportOptions options,
        ResourceLeaseChanged change)
    {
        ArgumentNullException.ThrowIfNull(options);
        StationMessageContract.Validate(change);
        return new StationCoordinatorPublication(
            options.JobExchange,
            $"station.{change.AgentId}.{change.StationId}.resource-lease-changed",
            nameof(ResourceLeaseChanged),
            options.CoordinatorId,
            change.MessageId,
            change.JobId,
            null,
            JsonSerializer.SerializeToUtf8Bytes(change, JsonOptions));
    }

    public static StationCoordinatorPublication Create(
        StationCoordinatorTransportOptions options,
        EmergencyStopRequested request)
    {
        ValidateSafetyTarget(request.MessageId, request.AgentId, request.StationId, request.RequestedAtUtc);
        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.Reason, nameof(request.Reason));
        _ = Required(request.RequestedBy, nameof(request.RequestedBy));
        return Safety(
            options,
            nameof(EmergencyStopRequested),
            request.MessageId,
            request.MessageId,
            request.AgentId,
            request.StationId,
            "emergency-stop",
            priority: 10,
            request);
    }

    public static StationCoordinatorPublication Create(
        StationCoordinatorTransportOptions options,
        StationSafeStopRequested request)
    {
        ValidateSafetyTarget(request.MessageId, request.AgentId, request.StationId, request.RequestedAtUtc);
        if (request.ProductionRunId == Guid.Empty)
        {
            throw new InvalidDataException("Safe Stop Production Run id is empty.");
        }

        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.StationSystemId, nameof(request.StationSystemId));
        _ = Required(request.ActorId, nameof(request.ActorId));
        _ = Required(request.Reason, nameof(request.Reason));
        if (request.OperationRunId is not null)
        {
            _ = Required(request.OperationRunId, nameof(request.OperationRunId));
        }

        return Safety(
            options,
            nameof(StationSafeStopRequested),
            request.MessageId,
            request.ProductionRunId,
            request.AgentId,
            request.StationId,
            "safe-stop",
            priority: 8,
            request);
    }

    public static StationCoordinatorPublication Create(
        StationCoordinatorTransportOptions options,
        StationJobCancelRequested request)
    {
        ValidateSafetyTarget(request.MessageId, request.AgentId, request.StationId, request.RequestedAtUtc);
        if (request.JobId == Guid.Empty || request.ProductionRunId == Guid.Empty)
        {
            throw new InvalidDataException("Station job cancellation identity is incomplete.");
        }

        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.JobIdempotencyKey, nameof(request.JobIdempotencyKey));
        _ = Required(request.StationSystemId, nameof(request.StationSystemId));
        _ = Required(request.OperationRunId, nameof(request.OperationRunId));
        _ = Required(request.ActorId, nameof(request.ActorId));
        _ = Required(request.Reason, nameof(request.Reason));

        return Safety(
            options,
            nameof(StationJobCancelRequested),
            request.MessageId,
            request.JobId,
            request.AgentId,
            request.StationId,
            "job-cancel",
            priority: 7,
            request);
    }

    private static StationCoordinatorPublication Safety<T>(
        StationCoordinatorTransportOptions options,
        string type,
        Guid messageId,
        Guid correlationId,
        string agentId,
        string stationId,
        string routeSuffix,
        byte priority,
        T request) => new(
            options.SafetyCommandExchange,
            $"station.{agentId}.{stationId}.{routeSuffix}",
            type,
            options.CoordinatorId,
            messageId,
            correlationId,
            priority,
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));

    private static void ValidateSafetyTarget(
        Guid messageId,
        string agentId,
        string stationId,
        DateTimeOffset requestedAtUtc)
    {
        if (messageId == Guid.Empty || requestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Station safety publication identity is incomplete.");
        }

        _ = Required(agentId, nameof(agentId));
        _ = Required(stationId, nameof(stationId));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}

public sealed class StationResultDeliveryProcessor(IStationJobCoordinationStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = StrictJson();

    public async ValueTask ProcessAsync(
        StationCoordinatorTransportDelivery delivery,
        IStationCoordinatorTransportSettlement settlement,
        Func<MaterialArrived, CancellationToken, ValueTask> materialArrivalHandler,
        Func<StationJobRecoveryRequired, CancellationToken, ValueTask> recoveryRequiredHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(materialArrivalHandler);
        ArgumentNullException.ThrowIfNull(recoveryRequiredHandler);

        try
        {
            ValidateBaseEnvelope(delivery);
            switch (delivery.Type)
            {
                case nameof(MaterialArrived):
                    {
                        var message = Deserialize<MaterialArrived>(delivery.Body);
                        StationMessageContract.Validate(message);
                        ValidateMaterialArrivalEnvelope(delivery, message);
                        await materialArrivalHandler(message, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case nameof(StationJobAccepted):
                    {
                        var message = Deserialize<StationJobAccepted>(delivery.Body);
                        StationMessageContract.Validate(message);
                        ValidateEnvelope(
                            delivery,
                            message.MessageId,
                            message.JobId,
                            message.AgentId,
                            message.StationId,
                            nameof(StationJobAccepted));
                        ValidateCommon(message.IdempotencyKey, message.AcceptedAtUtc);
                        await store.RecordAcceptedAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case nameof(StationJobProgressed):
                    {
                        var message = Deserialize<StationJobProgressed>(delivery.Body);
                        StationMessageContract.Validate(message);
                        ValidateEnvelope(
                            delivery,
                            message.MessageId,
                            message.JobId,
                            message.AgentId,
                            message.StationId,
                            nameof(StationJobProgressed));
                        ValidateCommon(message.IdempotencyKey, message.ProgressedAtUtc);
                        if (message.Percent is < 0 or > 100
                            || string.IsNullOrWhiteSpace(message.Phase))
                        {
                            throw new InvalidDataException("Station progress percent is outside 0..100.");
                        }

                        await store.RecordProgressAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case nameof(StationJobCompleted):
                    {
                        var message = Deserialize<StationJobCompleted>(delivery.Body);
                        StationMessageContract.Validate(message);
                        ValidateEnvelope(
                            delivery,
                            message.MessageId,
                            message.JobId,
                            message.AgentId,
                            message.StationId,
                            nameof(StationJobCompleted));
                        ValidateCommon(message.IdempotencyKey, message.CompletedAtUtc);
                        if (message.RuntimeSessionId == Guid.Empty
                            || message.Steps is null
                            || message.Commands is null
                            || message.Incidents is null
                            || message.Artifacts is null
                            || !Enum.IsDefined(message.ExecutionStatus)
                            || message.ExecutionStatus is OpenLineOps.Runtime.Contracts.ExecutionStatus.Pending
                                or OpenLineOps.Runtime.Contracts.ExecutionStatus.Running
                            || !Enum.IsDefined(message.Judgement)
                            || message.CompletedStepCount != message.Steps.Count(static step =>
                                string.Equals(step.Status, "Completed", StringComparison.Ordinal))
                            || message.CommandCount != message.Commands.Count
                            || message.IncidentCount != message.Incidents.Count)
                        {
                            throw new InvalidDataException(
                                "Station completion identity or evidence counts are inconsistent.");
                        }

                        await store.RecordCompletionAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case nameof(StationJobRecoveryRequired):
                    {
                        var message = Deserialize<StationJobRecoveryRequired>(delivery.Body);
                        StationMessageContract.Validate(message);
                        ValidateEnvelope(
                            delivery,
                            message.MessageId,
                            message.JobId,
                            message.AgentId,
                            message.StationId,
                            nameof(StationJobRecoveryRequired));
                        await store.RecordRecoveryRequiredAsync(message, cancellationToken)
                            .ConfigureAwait(false);
                        await recoveryRequiredHandler(message, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"Unsupported Station result message type '{delivery.Type}'.");
            }
        }
        catch (Exception exception) when (IsPermanent(exception))
        {
            await settlement.RejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await settlement.RejectAsync(
                    delivery.DeliveryTag,
                    requeue: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
        catch
        {
            await settlement.RejectAsync(
                    delivery.DeliveryTag,
                    requeue: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        // The durable Inbox write happens before broker acknowledgement. An ack crash is
        // recovered by broker redelivery and the store's exact-evidence idempotency check.
        await settlement.AcknowledgeAsync(delivery.DeliveryTag, CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static JsonSerializerOptions StrictJson() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static T Deserialize<T>(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<T>(body.Span, JsonOptions)
        ?? throw new InvalidDataException($"Station result {typeof(T).Name} body is null.");

    private static void ValidateBaseEnvelope(StationCoordinatorTransportDelivery delivery)
    {
        if (!string.Equals(delivery.ContentType, "application/json", StringComparison.Ordinal)
            || !string.Equals(delivery.ContentEncoding, "utf-8", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(delivery.Type)
            || string.IsNullOrWhiteSpace(delivery.AppId)
            || string.IsNullOrWhiteSpace(delivery.MessageId)
            || string.IsNullOrWhiteSpace(delivery.CorrelationId))
        {
            throw new InvalidDataException("Station result AMQP envelope is invalid.");
        }
    }

    private static void ValidateEnvelope(
        StationCoordinatorTransportDelivery delivery,
        Guid messageId,
        Guid jobId,
        string agentId,
        string stationId,
        string type)
    {
        if (messageId == Guid.Empty
            || jobId == Guid.Empty
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(stationId)
            || !string.Equals(delivery.Type, type, StringComparison.Ordinal)
            || !string.Equals(delivery.AppId, agentId, StringComparison.Ordinal)
            || !string.Equals(
                delivery.RoutingKey,
                StationTransportRoute.Event(stationId, type),
                StringComparison.Ordinal)
            || !Guid.TryParseExact(delivery.MessageId, "D", out var envelopeMessageId)
            || envelopeMessageId != messageId
            || !Guid.TryParseExact(delivery.CorrelationId, "D", out var envelopeCorrelationId)
            || envelopeCorrelationId != jobId)
        {
            throw new InvalidDataException(
                "Station result AMQP identity does not match its message body.");
        }
    }

    private static void ValidateCommon(string idempotencyKey, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || char.IsWhiteSpace(idempotencyKey[0])
            || char.IsWhiteSpace(idempotencyKey[^1])
            || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Station result idempotency or timestamp is invalid.");
        }
    }

    private static void ValidateMaterialArrivalEnvelope(
        StationCoordinatorTransportDelivery delivery,
        MaterialArrived message)
    {
        if (!string.Equals(delivery.Type, nameof(MaterialArrived), StringComparison.Ordinal)
            || !string.Equals(delivery.AppId, message.ProducerId, StringComparison.Ordinal)
            || !string.Equals(
                delivery.RoutingKey,
                StationTransportRoute.Event(
                    message.StationId,
                    nameof(MaterialArrived)),
                StringComparison.Ordinal)
            || !string.Equals(
                delivery.MessageId,
                message.MessageId.ToString("D"),
                StringComparison.Ordinal)
            || !string.Equals(
                delivery.CorrelationId,
                message.MessageId.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Material arrival AMQP identity does not match its message body.");
        }
    }

    private static bool IsPermanent(Exception exception) => exception is JsonException
        or InvalidDataException
        or ArgumentException
        or InvalidOperationException;
}
