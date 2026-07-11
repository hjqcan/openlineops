using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public enum ProductionOperationReadinessKind
{
    Ready,
    Waiting,
    RecoveryRequired
}

public sealed record ProductionOperationReadiness(
    ProductionOperationReadinessKind Kind,
    string Reason,
    IReadOnlyCollection<ResourceRequirement> MaterialResources,
    string? EvidenceKey)
{
    public static ProductionOperationReadiness Ready { get; } = new(
        ProductionOperationReadinessKind.Ready,
        "Production material satisfies the Station execution preconditions.",
        [],
        null);
}

public interface IProductionOperationReadiness
{
    ValueTask<ProductionOperationReadiness> EvaluateAsync(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        CancellationToken cancellationToken = default);
}
