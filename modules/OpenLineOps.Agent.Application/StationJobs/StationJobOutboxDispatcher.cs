using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed class StationJobOutboxDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IStationJobStore _store;
    private readonly IStationAgentMessagePublisher _publisher;
    private readonly IStationArtifactTransfer _artifacts;
    private readonly IClock _clock;

    public StationJobOutboxDispatcher(
        IStationJobStore store,
        IStationAgentMessagePublisher publisher,
        IStationArtifactTransfer artifacts,
        IClock clock)
    {
        _store = store;
        _publisher = publisher;
        _artifacts = artifacts;
        _clock = clock;
    }

    public async ValueTask<int> DispatchAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        await CleanupPublishedArtifactsAsync(maximumCount, cancellationToken)
            .ConfigureAwait(false);

        var messages = await _store
            .ListPendingOutboxAsync(maximumCount, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        var dispatched = 0;
        foreach (var message in messages)
        {
            PreparedPublication? publication = null;
            var acknowledged = false;
            try
            {
                publication = await PreparePublicationAsync(message, cancellationToken)
                    .ConfigureAwait(false);
                await _publisher.PublishAsync(
                        publication.Kind,
                        publication.PayloadJson,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.AcknowledgeOutboxAsync(message.MessageId, _clock.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
                acknowledged = true;
                dispatched++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var seconds = Math.Min(300, 1 << Math.Min(message.AttemptCount, 8));
                await _store.RecordOutboxFailureAsync(
                        message.MessageId,
                        _clock.UtcNow.AddSeconds(seconds),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (acknowledged && publication?.PendingCompletion is not null)
            {
                await TryCleanupAsync(
                        message,
                        publication.PendingCompletion,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return dispatched;
    }

    private async ValueTask<PreparedPublication> PreparePublicationAsync(
        StationJobOutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                message.Kind,
                StationAgentMessageKinds.JobCompletionPendingArtifactTransfer,
                StringComparison.Ordinal))
        {
            return new PreparedPublication(message.Kind, message.PayloadJson, null);
        }

        var pending = DeserializePendingCompletion(message);
        var publishedArtifacts = new List<StationJobArtifact>(pending.Artifacts.Count);
        foreach (var artifact in pending.Artifacts)
        {
            publishedArtifacts.Add(await _artifacts
                .PublishAsync(message.JobId, artifact, cancellationToken)
                .ConfigureAwait(false));
        }

        var completion = pending.Completion with { Artifacts = publishedArtifacts };
        return new PreparedPublication(
            StationAgentMessageKinds.JobCompleted,
            JsonSerializer.Serialize(completion, JsonOptions),
            pending);
    }

    private async ValueTask CleanupPublishedArtifactsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var messages = await _store
            .ListPendingArtifactCleanupAsync(maximumCount, cancellationToken)
            .ConfigureAwait(false);
        foreach (var message in messages)
        {
            await TryCleanupAsync(
                    message,
                    DeserializePendingCompletion(message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask TryCleanupAsync(
        StationJobOutboxMessage message,
        PendingStationJobCompletion pending,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var artifact in pending.Artifacts)
            {
                await _artifacts.ReleaseLocalAsync(message.JobId, artifact, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _store.DeleteAcknowledgedOutboxAsync(message.MessageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The acknowledged outbox row is the durable cleanup checkpoint. A later dispatch
            // retries local release without republishing the already acknowledged completion.
        }
    }

    private static PendingStationJobCompletion DeserializePendingCompletion(
        StationJobOutboxMessage message)
    {
        var pending = JsonSerializer.Deserialize<PendingStationJobCompletion>(
                message.PayloadJson,
                JsonOptions)
            ?? throw new InvalidDataException("Pending Station Job completion payload is null.");
        if (pending.Completion is null
            || pending.Artifacts is null
            || pending.Completion.Artifacts is null
            || pending.Completion.MessageId != message.MessageId
            || pending.Completion.JobId != message.JobId.Value
            || pending.Completion.Artifacts.Count != 0
            || pending.Artifacts.Count == 0
            || pending.Artifacts.Select(static artifact => artifact.LocalArtifactKey)
                .Distinct(StringComparer.Ordinal).Count() != pending.Artifacts.Count)
        {
            throw new InvalidDataException(
                $"Pending Station Job completion {message.MessageId:D} is inconsistent.");
        }

        return pending;
    }

    private sealed record PreparedPublication(
        string Kind,
        string PayloadJson,
        PendingStationJobCompletion? PendingCompletion);
}
