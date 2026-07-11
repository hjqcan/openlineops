using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Application.StationJobs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationMaterialArrivalSignal(
    [property: JsonConverter(typeof(CanonicalLowercaseGuidJsonConverter))] Guid MessageId,
    string IdempotencyKey,
    string MaterialKind,
    string MaterialId,
    string Source,
    string ActorId,
    DateTimeOffset ArrivedAtUtc);

public sealed class VerifiedStationMaterialArrivalDeployment
{
    public VerifiedStationMaterialArrivalDeployment(
        string agentId,
        string stationId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string productionLineDefinitionId,
        string stationSystemId,
        string packageContentSha256)
    {
        AgentId = Required(agentId, nameof(agentId));
        StationId = Required(stationId, nameof(stationId));
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        StationSystemId = Required(stationSystemId, nameof(stationSystemId));
        PackageContentSha256 = !string.IsNullOrWhiteSpace(packageContentSha256)
            && packageContentSha256.Length == 64
            && packageContentSha256.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? packageContentSha256
                : throw new ArgumentException(
                    "Package content SHA-256 must be lowercase hexadecimal.",
                    nameof(packageContentSha256));
    }

    public string AgentId { get; }

    public string StationId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string ProductionLineDefinitionId { get; }

    public string StationSystemId { get; }

    public string PackageContentSha256 { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}

public interface IStationMaterialArrivalDeploymentProvider
{
    ValueTask<VerifiedStationMaterialArrivalDeployment> GetCurrentAsync(
        CancellationToken cancellationToken = default);
}

public sealed record StationMaterialArrivalOutboxItem(
    long Sequence,
    Guid MessageId,
    string IdempotencyKey,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc);

public interface IStationMaterialArrivalOutboxStore
{
    ValueTask<bool> TryEnqueueAsync(
        MaterialArrived message,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationMaterialArrivalOutboxItem>> ListPendingAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask QuarantineAsync(
        Guid messageId,
        string failure,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class StationMaterialArrivalReporter(
    IStationMaterialArrivalDeploymentProvider deploymentProvider,
    IStationMaterialArrivalOutboxStore store,
    IClock clock)
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);
    private readonly IStationMaterialArrivalDeploymentProvider _deploymentProvider =
        deploymentProvider ?? throw new ArgumentNullException(nameof(deploymentProvider));
    private readonly IStationMaterialArrivalOutboxStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<bool> ReportAsync(
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

        var deployment = await _deploymentProvider.GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        var receivedAtUtc = RequireUtc(_clock.UtcNow);
        if (signal.ArrivedAtUtc > receivedAtUtc.Add(MaximumFutureClockSkew))
        {
            throw new InvalidDataException(
                "Station material arrival timestamp exceeds the allowed future clock skew.");
        }

        var message = new MaterialArrived(
            signal.MessageId,
            signal.IdempotencyKey,
            deployment.AgentId,
            deployment.StationId,
            deployment.ProjectId,
            deployment.ApplicationId,
            deployment.ProjectSnapshotId,
            deployment.PackageContentSha256,
            signal.MaterialKind,
            signal.MaterialId,
            deployment.ProductionLineDefinitionId,
            deployment.StationSystemId,
            signal.Source,
            signal.ActorId,
            signal.ArrivedAtUtc);
        StationMessageContract.Validate(message);
        return await _store.TryEnqueueAsync(message, receivedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value == default || value.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station material arrival reporter clock must return non-default UTC.")
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

        var pending = await store.ListPendingAsync(
                maximumCount,
                publishedAtUtc,
                cancellationToken)
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
            catch (Exception exception) when (IsPermanent(exception))
            {
                await store.QuarantineAsync(
                        item.MessageId,
                        CanonicalFailure(exception.Message),
                        publishedAtUtc,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await store.RecordPublishFailureAsync(
                        item.MessageId,
                        CanonicalFailure(exception.Message),
                        NextAttemptAtUtc(publishedAtUtc, item.AttemptCount + 1),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            }
        }

        return published;
    }

    private static bool IsPermanent(Exception exception) => exception is JsonException
        or InvalidDataException
        or ArgumentException;

    private static DateTimeOffset NextAttemptAtUtc(
        DateTimeOffset failedAtUtc,
        int attemptCount)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 8);
        var delayMilliseconds = 250 * (1 << exponent);
        return failedAtUtc.AddMilliseconds(Math.Min(delayMilliseconds, 60_000));
    }

    private static string CanonicalFailure(string? value)
    {
        var canonical = string.IsNullOrWhiteSpace(value)
            ? "Station material arrival publication failed without a description."
            : value.Trim();
        return canonical.Length <= 4096 ? canonical : canonical[..4096];
    }
}
