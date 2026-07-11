using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class InMemoryStationResourceFenceValidator : IStationResourceFenceValidator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Kind, string Id), FenceOwner> _fences = [];

    public ValueTask<StationResourceFenceValidationResult> ValidateAndAdvanceAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var fence in job.ResourceFences)
            {
                if (_fences.TryGetValue((fence.ResourceKind, fence.ResourceId), out var current)
                    && (current.FencingToken > fence.FencingToken
                        || (current.FencingToken == fence.FencingToken && current.JobId != job.JobId)))
                {
                    return ValueTask.FromResult(StationResourceFenceValidationResult.Reject(
                        $"Resource {fence.ResourceKind}/{fence.ResourceId} has fencing token "
                        + $"{current.FencingToken}; job token {fence.FencingToken} is stale."));
                }
            }

            foreach (var fence in job.ResourceFences)
            {
                _fences[(fence.ResourceKind, fence.ResourceId)] = new FenceOwner(
                    fence.FencingToken,
                    job.JobId,
                    fence.ExpiresAtUtc);
            }

            return ValueTask.FromResult(StationResourceFenceValidationResult.Accept());
        }
    }

    private sealed record FenceOwner(
        long FencingToken,
        StationJobId JobId,
        DateTimeOffset ExpiresAtUtc);
}
