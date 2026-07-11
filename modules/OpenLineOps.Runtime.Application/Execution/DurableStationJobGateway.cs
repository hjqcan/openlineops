using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class DurableStationJobGateway(IStationJobCoordinationStore store) : IStationJobGateway
{
    private static readonly TimeSpan CompletionPollInterval = TimeSpan.FromMilliseconds(100);

    public async ValueTask<StationJobCompleted> DispatchAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resourceLeaseChanges = request.ResourceFences
            .OrderBy(static fence => fence.ResourceKind, StringComparer.Ordinal)
            .ThenBy(static fence => fence.ResourceId, StringComparer.Ordinal)
            .Select(fence => StationDispatchMessageIdentity.CreateLeaseGranted(request, fence))
            .ToArray();
        _ = await store.TryEnqueueAsync(request, resourceLeaseChanges, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            var recoveryRequired = await store.GetRecoveryRequiredAsync(
                    request.JobId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recoveryRequired is not null)
            {
                throw new StationJobRecoveryRequiredException(recoveryRequired);
            }

            var quarantined = await store.ListQuarantinedAsync(
                    request.JobId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (quarantined.Count > 0)
            {
                throw new StationJobDispatchQuarantinedException(
                    request,
                    quarantined);
            }

            var completion = await store.GetCompletionAsync(request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (completion is not null)
            {
                return completion;
            }

            await Task.Delay(CompletionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class StationJobRecoveryRequiredException : Exception
{
    public StationJobRecoveryRequiredException(StationJobRecoveryRequired evidence)
        : base(evidence?.Reason)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public StationJobRecoveryRequired Evidence { get; }
}

public sealed class StationJobDispatchQuarantinedException : Exception
{
    public StationJobDispatchQuarantinedException(
        StationJobRequested request,
        IReadOnlyCollection<StationJobQuarantineItem> evidence)
        : base(Describe(evidence))
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        Evidence = evidence.OrderBy(static item => item.Sequence).ToArray();
        NeverPublished = Evidence.Count == request.ResourceFences.Count + 1
            && Evidence.Select(static item => item.Sequence)
                .SequenceEqual(Enumerable.Range(0, Evidence.Count));
        QuarantinedAtUtc = Evidence.Max(static item => item.QuarantinedAtUtc);
    }

    public IReadOnlyList<StationJobQuarantineItem> Evidence { get; }

    public bool NeverPublished { get; }

    public DateTimeOffset QuarantinedAtUtc { get; }

    private static string Describe(IReadOnlyCollection<StationJobQuarantineItem> evidence) =>
        evidence is null || evidence.Count == 0
            ? throw new ArgumentException(
                "Station dispatch quarantine evidence is required.",
                nameof(evidence))
            : string.Join(
                "; ",
                evidence.Select(static item => item.Reason).Distinct(StringComparer.Ordinal));
}

public interface IStationJobOutboxPublisher
{
    ValueTask PublishAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default);

    ValueTask PublishAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default);
}
