using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Slots;

public sealed class SlotDefinition : Entity<SlotDefinitionId>
{
    private SlotDefinition(
        SlotDefinitionId id,
        SlotGroupId slotGroupId,
        AutomationSystemId parentSystemId,
        string address,
        string displayName,
        SlotMaterialKind materialKind,
        bool isEnabled)
        : base(id)
    {
        SlotGroupId = slotGroupId;
        ParentSystemId = parentSystemId;
        Address = TopologyIdGuard.NotBlank(address, nameof(address));
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        MaterialKind = materialKind;
        IsEnabled = isEnabled;
    }

    public SlotGroupId SlotGroupId { get; }

    public AutomationSystemId ParentSystemId { get; }

    public string Address { get; private set; }

    public string DisplayName { get; private set; }

    public SlotMaterialKind MaterialKind { get; private set; }

    public bool IsEnabled { get; private set; }

    public static SlotDefinition Create(
        SlotDefinitionId id,
        SlotGroupId slotGroupId,
        AutomationSystemId parentSystemId,
        string address,
        string displayName,
        SlotMaterialKind materialKind = SlotMaterialKind.Dut,
        bool isEnabled = true)
    {
        return new SlotDefinition(
            id,
            slotGroupId,
            parentSystemId,
            address,
            displayName,
            materialKind,
            isEnabled);
    }

    internal void Update(
        string address,
        string displayName,
        SlotMaterialKind materialKind,
        bool isEnabled)
    {
        var validatedAddress = TopologyIdGuard.NotBlank(address, nameof(address));
        var validatedDisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));

        Address = validatedAddress;
        DisplayName = validatedDisplayName;
        MaterialKind = materialKind;
        IsEnabled = isEnabled;
    }
}
