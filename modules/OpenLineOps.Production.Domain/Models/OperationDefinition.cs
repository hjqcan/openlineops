using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class OperationDefinition : Entity<OperationDefinitionId>
{
    private OperationDefinition(
        OperationDefinitionId id,
        string displayName,
        string stationSystemId,
        string flowDefinitionId,
        string configurationSnapshotId)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        StationSystemId = ProductionIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
        FlowDefinitionId = ProductionIdGuard.NotBlank(flowDefinitionId, nameof(flowDefinitionId));
        ConfigurationSnapshotId = ProductionIdGuard.NotBlank(
            configurationSnapshotId,
            nameof(configurationSnapshotId));
    }

    public string DisplayName { get; }

    public string StationSystemId { get; }

    public string FlowDefinitionId { get; }

    public string ConfigurationSnapshotId { get; }

    public static OperationDefinition Create(
        OperationDefinitionId id,
        string displayName,
        string stationSystemId,
        string flowDefinitionId,
        string configurationSnapshotId)
    {
        return new OperationDefinition(
            id,
            displayName,
            stationSystemId,
            flowDefinitionId,
            configurationSnapshotId);
    }
}
