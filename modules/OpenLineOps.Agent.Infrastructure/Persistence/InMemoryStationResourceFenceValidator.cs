using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class InMemoryStationResourceFenceValidator(IClock clock) :
    IStationResourceFenceValidator,
    IStationResourceLeaseChangeInbox
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Kind, string Id), FenceOwner> _fences = [];
    private readonly Dictionary<Guid, ResourceLeaseChanged> _leaseMessages = [];
    private readonly Dictionary<string, Guid> _leaseIdempotency = new(StringComparer.Ordinal);

    public ValueTask ApplyAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(change);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_leaseMessages.TryGetValue(change.MessageId, out var existing))
            {
                if (existing != change)
                {
                    throw new InvalidOperationException(
                        $"Resource lease message {change.MessageId:D} was reused with different evidence.");
                }

                return ValueTask.CompletedTask;
            }

            if (_leaseIdempotency.TryGetValue(change.IdempotencyKey, out var existingMessageId))
            {
                throw new InvalidOperationException(
                    $"Resource lease idempotency key '{change.IdempotencyKey}' belongs to message {existingMessageId:D}.");
            }

            var key = (change.ResourceKind, change.ResourceId);
            if (_fences.TryGetValue(key, out var current))
            {
                if (current.FencingToken > change.FencingToken)
                {
                    throw new InvalidOperationException(
                        $"Resource {change.ResourceKind}/{change.ResourceId} already has newer fencing token {current.FencingToken}.");
                }

                if (current.FencingToken == change.FencingToken
                    && (current.JobId.Value != change.JobId
                        || current.ProductionRunId != change.ProductionRunId
                        || !string.Equals(
                            current.OperationRunId,
                            change.OperationRunId,
                            StringComparison.Ordinal)
                        || current.ExpiresAtUtc != change.ExpiresAtUtc))
                {
                    throw new InvalidOperationException(
                        $"Resource {change.ResourceKind}/{change.ResourceId} fencing token {change.FencingToken} was reused with a different owner.");
                }
            }

            _fences[key] = new FenceOwner(
                change.FencingToken,
                new StationJobId(change.JobId),
                change.ProductionRunId,
                change.OperationRunId,
                change.ExpiresAtUtc);
            _leaseMessages.Add(change.MessageId, change);
            _leaseIdempotency.Add(change.IdempotencyKey, change.MessageId);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<StationResourceFenceValidationResult> ValidateAndAdvanceAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        var invalid = ValidateCurrentFences(job, clock.UtcNow);
        if (invalid is not null)
        {
            return ValueTask.FromResult(StationResourceFenceValidationResult.Reject(invalid));
        }

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
                    job.ProductionRunId,
                    job.OperationRunId.Value,
                    fence.ExpiresAtUtc);
            }

            return ValueTask.FromResult(StationResourceFenceValidationResult.Accept());
        }
    }

    public ValueTask<StationResourceFenceValidationResult> ValidateCurrentAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        var invalid = ValidateCurrentFences(job, clock.UtcNow);
        if (invalid is not null)
        {
            return ValueTask.FromResult(StationResourceFenceValidationResult.Reject(invalid));
        }

        lock (_gate)
        {
            foreach (var fence in job.ResourceFences)
            {
                if (!_fences.TryGetValue((fence.ResourceKind, fence.ResourceId), out var current)
                    || current.FencingToken != fence.FencingToken
                    || current.JobId != job.JobId
                    || current.ExpiresAtUtc != fence.ExpiresAtUtc)
                {
                    return ValueTask.FromResult(StationResourceFenceValidationResult.Reject(
                        $"Resource {fence.ResourceKind}/{fence.ResourceId} token {fence.FencingToken} is no longer current for job {job.JobId}."));
                }
            }

            return ValueTask.FromResult(StationResourceFenceValidationResult.Accept());
        }
    }

    private static string? ValidateCurrentFences(
        StationJobSnapshot job,
        DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Agent clock must use UTC offset zero.");
        }

        if (job.ResourceFences
            .Select(static fence => (fence.ResourceKind, fence.ResourceId))
            .Distinct()
            .Count() != job.ResourceFences.Count)
        {
            return "Station job contains duplicate resource fences.";
        }

        var invalid = job.ResourceFences.FirstOrDefault(fence =>
            fence.ExpiresAtUtc.Offset != TimeSpan.Zero || fence.ExpiresAtUtc <= nowUtc);
        return invalid is null
            ? null
            : $"Resource {invalid.ResourceKind}/{invalid.ResourceId} fence expired before hardware start.";
    }

    private sealed record FenceOwner(
        long FencingToken,
        StationJobId JobId,
        Guid ProductionRunId,
        string OperationRunId,
        DateTimeOffset ExpiresAtUtc);
}
