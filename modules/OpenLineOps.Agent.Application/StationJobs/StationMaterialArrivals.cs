using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed record StationMaterialArrivalSignal(
    Guid MessageId,
    string IdempotencyKey,
    Guid ProductionUnitId,
    string LineId,
    string StationSystemId,
    string Source,
    string ActorId,
    DateTimeOffset ArrivedAtUtc);

public sealed record StationMaterialArrivalOutboxItem(
    Guid MessageId,
    string IdempotencyKey,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount);

public interface IStationMaterialArrivalOutboxStore
{
    ValueTask<bool> TryEnqueueAsync(
        MaterialArrived message,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationMaterialArrivalOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        CancellationToken cancellationToken = default);
}

public sealed class StationMaterialArrivalReporter(
    string agentId,
    string stationId,
    IStationMaterialArrivalOutboxStore store)
{
    private readonly string _agentId = Required(agentId, nameof(agentId));
    private readonly string _stationId = Required(stationId, nameof(stationId));
    private readonly IStationMaterialArrivalOutboxStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<bool> ReportAsync(
        StationMaterialArrivalSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Source is not (StationMaterialArrivalSources.Manual
            or StationMaterialArrivalSources.Plc))
        {
            throw new InvalidDataException(
                "A Station Agent material signal source must be exactly Manual or Plc.");
        }

        var message = new MaterialArrived(
            signal.MessageId,
            signal.IdempotencyKey,
            _agentId,
            _stationId,
            signal.ProductionUnitId,
            signal.LineId,
            signal.StationSystemId,
            signal.Source,
            signal.ActorId,
            signal.ArrivedAtUtc);
        StationMessageContract.Validate(message);
        return _store.TryEnqueueAsync(message, cancellationToken);
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

public sealed class StationMaterialArrivalOutboxDispatcher(
    IStationMaterialArrivalOutboxStore store,
    IStationAgentMessagePublisher publisher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask<int> DispatchPendingAsync(
        int maximumCount,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        if (publishedAtUtc == default || publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material outbox publication time must be a non-default UTC value.",
                nameof(publishedAtUtc));
        }

        var pending = await store.ListPendingAsync(maximumCount, cancellationToken)
            .ConfigureAwait(false);
        var published = 0;
        foreach (var item in pending)
        {
            try
            {
                var message = JsonSerializer.Deserialize<MaterialArrived>(
                    item.PayloadJson,
                    JsonOptions)
                    ?? throw new InvalidDataException("Material arrival outbox payload is empty.");
                StationMessageContract.Validate(message);
                await publisher.PublishAsync(
                        StationAgentMessageKinds.MaterialArrived,
                        item.PayloadJson,
                        cancellationToken)
                    .ConfigureAwait(false);
                await store.MarkPublishedAsync(
                        item.MessageId,
                        publishedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await store.RecordPublishFailureAsync(
                        item.MessageId,
                        exception.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return published;
    }
}
