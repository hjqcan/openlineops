using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Slots;

public sealed class SlotDefinition : Entity<SlotDefinitionId>
{
    private SlotDefinition(
        SlotDefinitionId id,
        EquipmentNodeId parentNodeId,
        string address,
        string displayName,
        SlotMaterialKind materialKind,
        bool isEnabled)
        : base(id)
    {
        ParentNodeId = parentNodeId;
        Address = TopologyIdGuard.NotBlank(address, nameof(address));
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        MaterialKind = materialKind;
        IsEnabled = isEnabled;
    }

    public EquipmentNodeId ParentNodeId { get; }

    public string Address { get; }

    public string DisplayName { get; }

    public SlotMaterialKind MaterialKind { get; }

    public bool IsEnabled { get; }

    public static SlotDefinition Create(
        SlotDefinitionId id,
        EquipmentNodeId parentNodeId,
        string address,
        string displayName,
        SlotMaterialKind materialKind = SlotMaterialKind.Dut,
        bool isEnabled = true)
    {
        return new SlotDefinition(id, parentNodeId, address, displayName, materialKind, isEnabled);
    }
}
