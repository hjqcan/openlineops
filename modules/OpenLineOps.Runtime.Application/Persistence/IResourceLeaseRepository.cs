using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IResourceLeaseRepository
{
    ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default);

    ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default);
}
