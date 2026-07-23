using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Application.Commands;

public interface IRuntimeCommandResourceFenceValidator
{
    ValueTask<ResourceLeaseFenceValidationResult> ValidateAsync(
        ProductionRunId productionRunId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        CancellationToken cancellationToken = default);
}

public sealed class RuntimeCommandResourceFenceValidator(
    IResourceLeaseRepository resourceLeases) : IRuntimeCommandResourceFenceValidator
{
    public ValueTask<ResourceLeaseFenceValidationResult> ValidateAsync(
        ProductionRunId productionRunId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return resourceLeases.ValidateCurrentAsync(
            productionRunId,
            operationRunId,
            evidence,
            cancellationToken);
    }
}
