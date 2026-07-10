using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Systems;

public sealed class StationSystem : AutomationSystem
{
    internal StationSystem(
        AutomationSystemId id,
        AutomationSystemId? parentSystemId,
        string systemType,
        string displayName,
        IEnumerable<CapabilityContractId> requiredCapabilities,
        IEnumerable<CapabilityContractId> providedCapabilities,
        IReadOnlyDictionary<string, string> metadata)
        : base(
            id,
            parentSystemId,
            systemType,
            displayName,
            requiredCapabilities,
            providedCapabilities,
            metadata)
    {
    }

    public override SystemKind Kind => SystemKind.Station;
}
