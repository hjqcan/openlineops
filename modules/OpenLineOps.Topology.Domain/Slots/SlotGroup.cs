using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Operations;

namespace OpenLineOps.Topology.Domain.Slots;

public sealed class SlotGroup : Entity<SlotGroupId>
{
    private readonly List<SlotDefinitionId> _slotIds = [];

    private SlotGroup(
        SlotGroupId id,
        AutomationSystemId parentSystemId,
        string displayName,
        SlotGroupKind kind,
        int capacity)
        : base(id)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Slot group capacity must be positive.");
        }

        ParentSystemId = parentSystemId;
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        Kind = kind;
        Capacity = capacity;
    }

    public AutomationSystemId ParentSystemId { get; }

    public string DisplayName { get; private set; }

    public SlotGroupKind Kind { get; private set; }

    public int Capacity { get; private set; }

    public IReadOnlyCollection<SlotDefinitionId> SlotIds => _slotIds.AsReadOnly();

    public static SlotGroup Create(
        SlotGroupId id,
        AutomationSystemId parentSystemId,
        string displayName,
        SlotGroupKind kind,
        int capacity)
    {
        return new SlotGroup(id, parentSystemId, displayName, kind, capacity);
    }

    internal TopologyOperationResult AddSlot(SlotDefinitionId slotId)
    {
        if (_slotIds.Contains(slotId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAlreadyInGroup",
                $"Slot {slotId} already exists in slot group {Id}.");
        }

        if (_slotIds.Count >= Capacity)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupCapacityExceeded",
                $"Slot group {Id} capacity {Capacity} would be exceeded.");
        }

        _slotIds.Add(slotId);

        return TopologyOperationResult.Accepted("Slot added to group.");
    }

    internal TopologyOperationResult Update(
        string displayName,
        SlotGroupKind kind,
        int capacity)
    {
        if (capacity <= 0)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupCapacityInvalid",
                $"Slot group {Id} capacity must be positive.");
        }

        if (capacity < _slotIds.Count)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupCapacityBelowSlotCount",
                $"Slot group {Id} capacity {capacity} cannot be lower than its {_slotIds.Count} existing slots.");
        }

        var validatedDisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        DisplayName = validatedDisplayName;
        Kind = kind;
        Capacity = capacity;
        return TopologyOperationResult.Accepted("Slot group updated.");
    }

    internal void RemoveSlot(SlotDefinitionId slotId)
    {
        _slotIds.Remove(slotId);
    }
}
