using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed class StationSafetyDeliveryProcessor(
    RabbitMqStationSafetyOptions options,
    StationSafetyCommandCoordinator coordinator)
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationJobDeliveryProcessor.StrictJson();

    public ValueTask ProcessEmergencyStopAsync(
        StationTransportDelivery delivery,
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        IStationSafetyAcknowledgementPublisher publisher,
        IStationTransportSettlement settlement,
        CancellationToken cancellationToken = default) => ProcessAsync(
            delivery,
            nameof(EmergencyStopRequested),
            StationTransportRoute.Safety(
                options.AgentId,
                options.StationId,
                "emergency-stop"),
            static body => Deserialize<EmergencyStopRequested>(body),
            request => Validate(request, delivery),
            request => coordinator.HandleEmergencyStopAsync(request, handler, cancellationToken),
            acknowledgement => StationAgentEventPublicationFactory.Create(options, acknowledgement),
            publisher,
            settlement,
            cancellationToken);

    public ValueTask ProcessSafeStopAsync(
        StationTransportDelivery delivery,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        IStationSafetyAcknowledgementPublisher publisher,
        IStationTransportSettlement settlement,
        CancellationToken cancellationToken = default) => ProcessAsync(
            delivery,
            nameof(StationSafeStopRequested),
            StationTransportRoute.Safety(
                options.AgentId,
                options.StationId,
                "safe-stop"),
            static body => Deserialize<StationSafeStopRequested>(body),
            request => Validate(request, delivery),
            request => coordinator.HandleSafeStopAsync(request, handler, cancellationToken),
            acknowledgement => StationAgentEventPublicationFactory.Create(options, acknowledgement),
            publisher,
            settlement,
            cancellationToken);

    public ValueTask ProcessJobCancelAsync(
        StationTransportDelivery delivery,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> handler,
        IStationSafetyAcknowledgementPublisher publisher,
        IStationTransportSettlement settlement,
        CancellationToken cancellationToken = default) => ProcessAsync(
            delivery,
            nameof(StationJobCancelRequested),
            StationTransportRoute.Safety(
                options.AgentId,
                options.StationId,
                "job-cancel"),
            static body => Deserialize<StationJobCancelRequested>(body),
            request => Validate(request, delivery),
            request => coordinator.HandleJobCancelAsync(request, handler, cancellationToken),
            acknowledgement => StationAgentEventPublicationFactory.Create(options, acknowledgement),
            publisher,
            settlement,
            cancellationToken);

    private static async ValueTask ProcessAsync<TRequest, TAcknowledgement>(
        StationTransportDelivery delivery,
        string expectedType,
        string expectedRoutingKey,
        Func<ReadOnlyMemory<byte>, TRequest> deserialize,
        Action<TRequest> validate,
        Func<TRequest, ValueTask<TAcknowledgement>> handle,
        Func<TAcknowledgement, StationAgentEventPublication> createPublication,
        IStationSafetyAcknowledgementPublisher publisher,
        IStationTransportSettlement settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(settlement);

        StationAgentEventPublication publication;
        try
        {
            StationJobDeliveryProcessor.ValidateEnvelope(
                delivery,
                expectedType,
                expectedRoutingKey);
            var request = deserialize(delivery.Body);
            validate(request);
            var acknowledgement = await handle(request).ConfigureAwait(false);
            publication = createPublication(acknowledgement);
        }
        catch (Exception exception) when (StationJobDeliveryProcessor.IsPermanent(exception))
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

        try
        {
            await publisher.PublishAsync(publication, cancellationToken)
                .ConfigureAwait(false);
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
            // A closed channel, publisher nack, mandatory return, or unknown confirm
            // outcome must redeliver the command. The durable safety Inbox replays the
            // exact persisted acknowledgement without invoking the actuator again.
            await settlement.RejectAsync(
                    delivery.DeliveryTag,
                    requeue: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        // The acknowledgement is publisher-confirmed before the input delivery is acked.
        // If this final ack fails, broker redelivery is safe because the durable safety
        // Inbox replays the persisted acknowledgement without invoking the actuator again.
        await settlement.AcknowledgeAsync(delivery.DeliveryTag, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<T>(body.Span, JsonOptions)
        ?? throw new InvalidDataException($"Station safety {typeof(T).Name} body is null.");

    private void Validate(EmergencyStopRequested request, StationTransportDelivery delivery)
    {
        ValidateTarget(
            request.MessageId,
            request.MessageId,
            request.AgentId,
            request.StationId,
            request.IdempotencyKey,
            request.RequestedAtUtc,
            delivery);
        _ = Required(request.Reason, nameof(request.Reason));
        _ = Required(request.RequestedBy, nameof(request.RequestedBy));
    }

    private void Validate(StationSafeStopRequested request, StationTransportDelivery delivery)
    {
        ValidateTarget(
            request.MessageId,
            request.ProductionRunId,
            request.AgentId,
            request.StationId,
            request.IdempotencyKey,
            request.RequestedAtUtc,
            delivery);
        _ = Required(request.StationSystemId, nameof(request.StationSystemId));
        _ = Required(request.ActorId, nameof(request.ActorId));
        _ = Required(request.Reason, nameof(request.Reason));
        if (request.OperationRunId is not null)
        {
            _ = Required(request.OperationRunId, nameof(request.OperationRunId));
        }
    }

    private void Validate(StationJobCancelRequested request, StationTransportDelivery delivery)
    {
        ValidateTarget(
            request.MessageId,
            request.JobId,
            request.AgentId,
            request.StationId,
            request.IdempotencyKey,
            request.RequestedAtUtc,
            delivery);
        if (request.ProductionRunId == Guid.Empty)
        {
            throw new InvalidDataException("Station job cancellation Production Run id is empty.");
        }

        _ = Required(request.JobIdempotencyKey, nameof(request.JobIdempotencyKey));
        _ = Required(request.StationSystemId, nameof(request.StationSystemId));
        _ = Required(request.OperationRunId, nameof(request.OperationRunId));
        _ = Required(request.ActorId, nameof(request.ActorId));
        _ = Required(request.Reason, nameof(request.Reason));
    }

    private void ValidateTarget(
        Guid messageId,
        Guid correlationId,
        string agentId,
        string stationId,
        string idempotencyKey,
        DateTimeOffset requestedAtUtc,
        StationTransportDelivery delivery)
    {
        if (messageId == Guid.Empty
            || correlationId == Guid.Empty
            || !string.Equals(agentId, options.AgentId, StringComparison.Ordinal)
            || !string.Equals(stationId, options.StationId, StringComparison.Ordinal)
            || !Guid.TryParseExact(delivery.MessageId, "D", out var envelopeMessageId)
            || envelopeMessageId != messageId
            || !Guid.TryParseExact(delivery.CorrelationId, "D", out var envelopeCorrelationId)
            || envelopeCorrelationId != correlationId
            || requestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Station safety AMQP identity does not match its body or target Agent/Station.");
        }

        _ = Required(idempotencyKey, nameof(idempotencyKey));
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
