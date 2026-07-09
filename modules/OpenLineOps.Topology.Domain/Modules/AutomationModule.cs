using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Modules;

public sealed class AutomationModule : Entity<AutomationModuleId>
{
    private readonly List<CapabilityContractId> _requiredCapabilities;
    private readonly List<CapabilityContractId> _providedCapabilities;

    private AutomationModule(
        AutomationModuleId id,
        EquipmentNodeId nodeId,
        string moduleKind,
        string displayName,
        IEnumerable<CapabilityContractId> requiredCapabilities,
        IEnumerable<CapabilityContractId> providedCapabilities)
        : base(id)
    {
        NodeId = nodeId;
        ModuleKind = TopologyIdGuard.NotBlank(moduleKind, nameof(moduleKind));
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        _requiredCapabilities = requiredCapabilities.Distinct().ToList();
        _providedCapabilities = providedCapabilities.Distinct().ToList();
    }

    public EquipmentNodeId NodeId { get; }

    public string ModuleKind { get; }

    public string DisplayName { get; }

    public IReadOnlyCollection<CapabilityContractId> RequiredCapabilities => _requiredCapabilities.AsReadOnly();

    public IReadOnlyCollection<CapabilityContractId> ProvidedCapabilities => _providedCapabilities.AsReadOnly();

    public static AutomationModule Create(
        AutomationModuleId id,
        EquipmentNodeId nodeId,
        string moduleKind,
        string displayName,
        IEnumerable<CapabilityContractId>? requiredCapabilities = null,
        IEnumerable<CapabilityContractId>? providedCapabilities = null)
    {
        return new AutomationModule(
            id,
            nodeId,
            moduleKind,
            displayName,
            requiredCapabilities ?? [],
            providedCapabilities ?? []);
    }
}
