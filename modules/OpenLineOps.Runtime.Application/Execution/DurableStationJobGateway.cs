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

public interface IStationJobOutboxPublisher
{
    ValueTask PublishAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default);

    ValueTask PublishAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default);
}
