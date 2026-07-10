using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class WorkstationDefinition : Entity<WorkstationId>
{
    private WorkstationDefinition(
        WorkstationId id,
        string displayName,
        string topologyStationNodeId,
        string topologySystemModuleId)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        TopologyStationNodeId = ProductionIdGuard.NotBlank(
            topologyStationNodeId,
            nameof(topologyStationNodeId));
        TopologySystemModuleId = ProductionIdGuard.NotBlank(
            topologySystemModuleId,
            nameof(topologySystemModuleId));
    }

    public string DisplayName { get; }

    public string TopologyStationNodeId { get; }

    public string TopologySystemModuleId { get; }

    public static WorkstationDefinition Create(
        WorkstationId id,
        string displayName,
        string topologyStationNodeId,
        string topologySystemModuleId)
    {
        return new WorkstationDefinition(
            id,
            displayName,
            topologyStationNodeId,
            topologySystemModuleId);
    }
}
