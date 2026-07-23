using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record StationTransportDelivery(
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

public interface IStationTransportSettlement
{
    ValueTask AcknowledgeAsync(ulong deliveryTag, CancellationToken cancellationToken = default);

    ValueTask RejectAsync(
        ulong deliveryTag,
        bool requeue,
        CancellationToken cancellationToken = default);
}

public sealed record StationAgentEventPublication(
    string Exchange,
    string RoutingKey,
    string Type,
    string AppId,
    Guid MessageId,
    Guid CorrelationId,
    ReadOnlyMemory<byte> Body);

public interface IStationAgentConfirmedPublicationTransport
{
    ValueTask PublishAsync(
        StationAgentEventPublication publication,
        CancellationToken cancellationToken = default);
}

public interface IStationSafetyAcknowledgementPublisher
{
    ValueTask PublishAsync(
        StationAgentEventPublication publication,
        CancellationToken cancellationToken = default);
}

public sealed class StationJobDeliveryProcessor(
    RabbitMqStationTransportOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = StrictJson();

    public async ValueTask ProcessAsync(
        StationTransportDelivery delivery,
        Func<StationJobRequested, CancellationToken, ValueTask> jobHandler,
        Func<ResourceLeaseChanged, CancellationToken, ValueTask> resourceLeaseHandler,
        IStationTransportSettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(jobHandler);
        ArgumentNullException.ThrowIfNull(resourceLeaseHandler);
        ArgumentNullException.ThrowIfNull(settlement);

        try
        {
            switch (delivery.Type)
            {
                case nameof(ResourceLeaseChanged):
                    {
                        ValidateEnvelope(
                            delivery,
                            nameof(ResourceLeaseChanged),
                            StationTransportRoute.ResourceLeaseChanged(
                                options.AgentId,
                                options.StationId));
                        var change = JsonSerializer.Deserialize<ResourceLeaseChanged>(
                            delivery.Body.Span,
                            JsonOptions)
                            ?? throw new InvalidDataException("Resource lease change message is null.");
                        ValidateResourceLeaseEnvelope(delivery, change);
                        await resourceLeaseHandler(change, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case nameof(StationJobRequested):
                    {
                        ValidateEnvelope(
                            delivery,
                            nameof(StationJobRequested),
                            StationTransportRoute.Job(
                                options.AgentId,
                                options.StationId));
                        var request = JsonSerializer.Deserialize<StationJobRequested>(
                            delivery.Body.Span,
                            JsonOptions)
                            ?? throw new InvalidDataException("Station job message is null.");
                        StationMessageContract.Validate(request);
                        ValidateRequestEnvelope(delivery, request);
                        await jobHandler(request, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"Unsupported Station command message type '{delivery.Type}'.");
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

        // A failed acknowledgement is deliberately allowed to escape. The broker then
        // redelivers the durable message and the Agent Inbox idempotency boundary returns
        // the already persisted checkpoint without replaying the hardware command.
        await settlement.AcknowledgeAsync(delivery.DeliveryTag, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void ValidateRequestEnvelope(
        StationTransportDelivery delivery,
        StationJobRequested request)
    {
        if (request.MessageId == Guid.Empty
            || request.JobId == Guid.Empty
            || request.ProductionRunId == Guid.Empty
            || request.ProductionUnitId == Guid.Empty
            || request.RuntimeSessionId == Guid.Empty
            || !string.Equals(request.AgentId, options.AgentId, StringComparison.Ordinal)
            || !string.Equals(request.StationId, options.StationId, StringComparison.Ordinal)
            || !string.Equals(
                request.StationSystemId,
                options.StationSystemId,
                StringComparison.Ordinal)
            || !Guid.TryParseExact(delivery.MessageId, "D", out var messageId)
            || messageId != request.MessageId
            || !Guid.TryParseExact(delivery.CorrelationId, "D", out var correlationId)
            || correlationId != request.JobId)
        {
            throw new InvalidDataException(
                "Station job AMQP identity does not match its body or target Agent/Station.");
        }
    }

    private void ValidateResourceLeaseEnvelope(
        StationTransportDelivery delivery,
        ResourceLeaseChanged change)
    {
        StationMessageContract.Validate(change);
        if (!string.Equals(change.AgentId, options.AgentId, StringComparison.Ordinal)
            || !string.Equals(change.StationId, options.StationId, StringComparison.Ordinal)
            || !string.Equals(
                change.StationSystemId,
                options.StationSystemId,
                StringComparison.Ordinal)
            || !Guid.TryParseExact(delivery.MessageId, "D", out var messageId)
            || messageId != change.MessageId
            || !Guid.TryParseExact(delivery.CorrelationId, "D", out var correlationId)
            || correlationId != change.JobId)
        {
            throw new InvalidDataException(
                "Resource lease AMQP identity does not match its body or target Agent/Station.");
        }
    }

    internal static void ValidateEnvelope(
        StationTransportDelivery delivery,
        string expectedType,
        string expectedRoutingKey)
    {
        if (!string.Equals(delivery.ContentType, "application/json", StringComparison.Ordinal)
            || !string.Equals(delivery.ContentEncoding, "utf-8", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(delivery.Type, expectedType, StringComparison.Ordinal)
            || !string.Equals(delivery.RoutingKey, expectedRoutingKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(delivery.AppId)
            || string.IsNullOrWhiteSpace(delivery.MessageId)
            || string.IsNullOrWhiteSpace(delivery.CorrelationId))
        {
            throw new InvalidDataException("Station AMQP envelope is invalid.");
        }
    }

    internal static bool IsPermanent(Exception exception) => exception is JsonException
        or InvalidDataException
        or ArgumentException
        or InvalidOperationException;

    internal static JsonSerializerOptions StrictJson() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public static class StationAgentEventPublicationFactory
{
    private static readonly JsonSerializerOptions JsonOptions = StationJobDeliveryProcessor.StrictJson();

    public static StationAgentEventPublication Create(
        RabbitMqStationTransportOptions options,
        string kind,
        string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        return kind switch
        {
            nameof(MaterialArrived) => CreateMaterial(
                options,
                Deserialize<MaterialArrived>(payloadJson)),
            nameof(StationJobAccepted) => Create(
                options,
                kind,
                Deserialize<StationJobAccepted>(payloadJson),
                static message => message.MessageId,
                static message => message.JobId,
                static message => message.AgentId,
                static message => message.StationId),
            nameof(StationJobProgressed) => Create(
                options,
                kind,
                Deserialize<StationJobProgressed>(payloadJson),
                static message => message.MessageId,
                static message => message.JobId,
                static message => message.AgentId,
                static message => message.StationId),
            nameof(StationJobCompleted) => Create(
                options,
                kind,
                Deserialize<StationJobCompleted>(payloadJson),
                static message => message.MessageId,
                static message => message.JobId,
                static message => message.AgentId,
                static message => message.StationId),
            nameof(StationJobRecoveryRequired) => Create(
                options,
                kind,
                Deserialize<StationJobRecoveryRequired>(payloadJson),
                static message => message.MessageId,
                static message => message.JobId,
                static message => message.AgentId,
                static message => message.StationId),
            nameof(AgentPresenceReported) => CreatePresence(
                options,
                Deserialize<AgentPresenceReported>(payloadJson)),
            _ => throw new InvalidDataException(
                $"Unsupported Station Agent event kind '{kind}'.")
        };
    }

    public static StationAgentEventPublication Create(
        RabbitMqStationSafetyOptions options,
        EmergencyStopAcknowledged acknowledgement)
    {
        ValidateAcknowledgement(
            acknowledgement.IdempotencyKey,
            acknowledgement.Accepted,
            acknowledgement.FailureCode,
            acknowledgement.FailureReason,
            acknowledgement.AcknowledgedAtUtc);
        return CreateSafety(
            options,
            nameof(EmergencyStopAcknowledged),
            acknowledgement,
            acknowledgement.MessageId,
            acknowledgement.RequestMessageId,
            acknowledgement.AgentId,
            acknowledgement.StationId,
            "emergency-stop-acknowledged");
    }

    public static StationAgentEventPublication Create(
        RabbitMqStationSafetyOptions options,
        StationSafeStopAcknowledged acknowledgement)
    {
        ValidateAcknowledgement(
            acknowledgement.IdempotencyKey,
            acknowledgement.Accepted,
            acknowledgement.FailureCode,
            acknowledgement.FailureReason,
            acknowledgement.AcknowledgedAtUtc);
        return CreateSafety(
            options,
            nameof(StationSafeStopAcknowledged),
            acknowledgement,
            acknowledgement.MessageId,
            acknowledgement.RequestMessageId,
            acknowledgement.AgentId,
            acknowledgement.StationId,
            "safe-stop-acknowledged");
    }

    public static StationAgentEventPublication Create(
        RabbitMqStationSafetyOptions options,
        StationJobCancelAcknowledged acknowledgement)
    {
        ValidateAcknowledgement(
            acknowledgement.IdempotencyKey,
            acknowledgement.Accepted,
            acknowledgement.FailureCode,
            acknowledgement.FailureReason,
            acknowledgement.AcknowledgedAtUtc);
        return CreateSafety(
            options,
            nameof(StationJobCancelAcknowledged),
            acknowledgement,
            acknowledgement.MessageId,
            acknowledgement.RequestMessageId,
            acknowledgement.AgentId,
            acknowledgement.StationId,
            "job-cancel-acknowledged");
    }

    private static StationAgentEventPublication Create<T>(
        RabbitMqStationTransportOptions options,
        string kind,
        T message,
        Func<T, Guid> messageId,
        Func<T, Guid> correlationId,
        Func<T, string> agentId,
        Func<T, string> stationId)
    {
        var id = messageId(message);
        var correlation = correlationId(message);
        var agent = agentId(message);
        var station = stationId(message);
        switch (message)
        {
            case StationJobAccepted accepted:
                StationMessageContract.Validate(accepted);
                break;
            case StationJobProgressed progressed:
                StationMessageContract.Validate(progressed);
                break;
            case StationJobCompleted completed:
                StationMessageContract.Validate(completed);
                break;
            case StationJobRecoveryRequired recoveryRequired:
                StationMessageContract.Validate(recoveryRequired);
                break;
        }
        ValidateIdentity(id, correlation, agent, station, options.AgentId, options.StationId);
        return new StationAgentEventPublication(
            options.EventExchange,
            StationTransportRoute.Event(agent, station, kind),
            kind,
            agent,
            id,
            correlation,
            JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions));
    }

    private static StationAgentEventPublication CreateMaterial(
        RabbitMqStationTransportOptions options,
        MaterialArrived message)
    {
        StationMessageContract.Validate(message);
        return Create(
            options,
            nameof(MaterialArrived),
            message,
            static value => value.MessageId,
            static value => value.MessageId,
            static value => value.ProducerId,
            static value => value.StationId);
    }

    private static StationAgentEventPublication CreatePresence(
        RabbitMqStationTransportOptions options,
        AgentPresenceReported message)
    {
        AgentPresenceContract.Validate(message);
        if (!string.Equals(message.AgentId, options.AgentId, StringComparison.Ordinal)
            || !string.Equals(message.StationId, options.StationId, StringComparison.Ordinal)
            || !string.Equals(
                message.StationSystemId,
                options.StationSystemId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Agent presence identity does not match the configured Agent/Station target.");
        }

        return new StationAgentEventPublication(
            options.EventExchange,
            StationTransportRoute.Event(
                message.AgentId,
                message.StationId,
                nameof(AgentPresenceReported)),
            nameof(AgentPresenceReported),
            message.AgentId,
            AgentPresenceContract.MessageId(message),
            message.SessionId,
            JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions));
    }

    private static StationAgentEventPublication CreateSafety<T>(
        RabbitMqStationSafetyOptions options,
        string kind,
        T acknowledgement,
        Guid messageId,
        Guid requestMessageId,
        string agentId,
        string stationId,
        string routeSuffix)
    {
        ValidateIdentity(
            messageId,
            requestMessageId,
            agentId,
            stationId,
            options.AgentId,
            options.StationId);
        return new StationAgentEventPublication(
            options.EventExchange,
            StationTransportRoute.Event(agentId, stationId, routeSuffix),
            kind,
            agentId,
            messageId,
            requestMessageId,
            JsonSerializer.SerializeToUtf8Bytes(acknowledgement, JsonOptions));
    }

    private static T Deserialize<T>(string payloadJson) =>
        JsonSerializer.Deserialize<T>(payloadJson, JsonOptions)
        ?? throw new InvalidDataException($"Station Agent {typeof(T).Name} payload is null.");

    private static void ValidateIdentity(
        Guid messageId,
        Guid correlationId,
        string agentId,
        string stationId,
        string expectedAgentId,
        string expectedStationId)
    {
        if (messageId == Guid.Empty
            || correlationId == Guid.Empty
            || !string.Equals(agentId, expectedAgentId, StringComparison.Ordinal)
            || !string.Equals(stationId, expectedStationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station Agent event identity does not match its configured Agent/Station.");
        }
    }

    private static void ValidateAcknowledgement(
        string idempotencyKey,
        bool accepted,
        string? failureCode,
        string? failureReason,
        DateTimeOffset acknowledgedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || char.IsWhiteSpace(idempotencyKey[0])
            || char.IsWhiteSpace(idempotencyKey[^1])
            || acknowledgedAtUtc.Offset != TimeSpan.Zero
            || (accepted && (failureCode is not null || failureReason is not null))
            || (!accepted && (string.IsNullOrWhiteSpace(failureCode)
                              || string.IsNullOrWhiteSpace(failureReason))))
        {
            throw new InvalidDataException(
                "Station safety acknowledgement outcome evidence is invalid.");
        }
    }
}
