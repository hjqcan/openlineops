using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Nodes;

public sealed class EquipmentNode : Entity<EquipmentNodeId>
{
    private EquipmentNode(
        EquipmentNodeId id,
        EquipmentNodeId? parentId,
        EquipmentNodeKind kind,
        string displayName)
        : base(id)
    {
        ParentId = parentId;
        Kind = kind;
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
    }

    public EquipmentNodeId? ParentId { get; }

    public EquipmentNodeKind Kind { get; }

    public string DisplayName { get; }

    public static EquipmentNode Create(
        EquipmentNodeId id,
        EquipmentNodeId? parentId,
        EquipmentNodeKind kind,
        string displayName)
    {
        return new EquipmentNode(id, parentId, kind, displayName);
    }
}
