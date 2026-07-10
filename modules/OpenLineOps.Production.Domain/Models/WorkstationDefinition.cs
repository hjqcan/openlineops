using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class WorkstationDefinition : Entity<WorkstationId>
{
    private WorkstationDefinition(
        WorkstationId id,
        string displayName,
        string stationSystemId)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        StationSystemId = ProductionIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
    }

    public string DisplayName { get; }

    public string StationSystemId { get; }

    public static WorkstationDefinition Create(
        WorkstationId id,
        string displayName,
        string stationSystemId)
    {
        return new WorkstationDefinition(
            id,
            displayName,
            stationSystemId);
    }
}
