using OpenLineOps.Runtime.Domain.Identifiers;
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
        ProductionUnitIdentity productionUnitIdentity,
        string actorId,
        string entryOperationId,
        IReadOnlyList<OperationExecutionPlan> operations,
        IReadOnlyList<RouteTransitionDefinition> routeTransitions,
        string? lotId = null,
        string? carrierId = null)
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
        ProductionUnitIdentity = productionUnitIdentity
            ?? throw new ArgumentNullException(nameof(productionUnitIdentity));
        ActorId = Required(actorId, nameof(actorId));
        EntryOperationId = Required(entryOperationId, nameof(entryOperationId));
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(routeTransitions);
        Operations = operations.ToArray();
        RouteTransitions = routeTransitions.ToArray();
        LotId = Optional(lotId, nameof(lotId));
        CarrierId = Optional(carrierId, nameof(carrierId));
    }

    public ProductionRunId RunId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public string ProductionLineDefinitionId { get; }

    public ProductionUnitIdentity ProductionUnitIdentity { get; }

    public string ActorId { get; }

    public string EntryOperationId { get; }

    public IReadOnlyList<OperationExecutionPlan> Operations { get; }

    public IReadOnlyList<RouteTransitionDefinition> RouteTransitions { get; }

    public string? LotId { get; }

    public string? CarrierId { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;

    private static string? Optional(string? value, string parameterName) =>
        value is null ? null : Required(value, parameterName);
}
