using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Operations;

namespace OpenLineOps.Topology.Domain.Slots;

public sealed class SlotGroup : Entity<SlotGroupId>
{
    private readonly List<SlotDefinitionId> _slotIds = [];

    private SlotGroup(
        SlotGroupId id,
        EquipmentNodeId parentNodeId,
        string displayName,
        SlotGroupKind kind,
        int capacity)
        : base(id)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Slot group capacity must be positive.");
        }

        ParentNodeId = parentNodeId;
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        Kind = kind;
        Capacity = capacity;
    }

    public EquipmentNodeId ParentNodeId { get; }

    public string DisplayName { get; }

    public SlotGroupKind Kind { get; }

    public int Capacity { get; }

    public IReadOnlyCollection<SlotDefinitionId> SlotIds => _slotIds.AsReadOnly();

    public static SlotGroup Create(
        SlotGroupId id,
        EquipmentNodeId parentNodeId,
        string displayName,
        SlotGroupKind kind,
        int capacity)
    {
        return new SlotGroup(id, parentNodeId, displayName, kind, capacity);
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
}
