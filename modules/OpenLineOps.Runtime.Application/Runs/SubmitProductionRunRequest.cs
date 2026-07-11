using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record SubmitProductionRunRequest
{
    public SubmitProductionRunRequest(
        ProductionRunId runId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        ProductionUnitId productionUnitId,
        string frozenProductModelId,
        string frozenIdentityInputKey,
        string actorId,
        string entryOperationId,
        IReadOnlyList<OperationExecutionPlan> operations,
        IReadOnlyList<RouteTransitionDefinition> routeTransitions)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production Run id cannot be empty.", nameof(runId));
        }

        RunId = runId;
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = Required(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        ProductionUnitId = productionUnitId.Value == Guid.Empty
            ? throw new ArgumentException("Production Unit id cannot be empty.", nameof(productionUnitId))
            : productionUnitId;
        FrozenProductModelId = Required(frozenProductModelId, nameof(frozenProductModelId));
        FrozenIdentityInputKey = Required(frozenIdentityInputKey, nameof(frozenIdentityInputKey));
        ActorId = Required(actorId, nameof(actorId));
        EntryOperationId = Required(entryOperationId, nameof(entryOperationId));
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(routeTransitions);
        Operations = operations.ToArray();
        RouteTransitions = routeTransitions.ToArray();
    }

    public ProductionRunId RunId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public string ProductionLineDefinitionId { get; }

    public ProductionUnitId ProductionUnitId { get; }

    public string FrozenProductModelId { get; }

    public string FrozenIdentityInputKey { get; }

    public string ActorId { get; }

    public string EntryOperationId { get; }

    public IReadOnlyList<OperationExecutionPlan> Operations { get; }

    public IReadOnlyList<RouteTransitionDefinition> RouteTransitions { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;

}
