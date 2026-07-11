using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public enum OperationResourceKind
{
    Station = 1,
    Fixture = 2,
    Device = 3,
    SlotGroup = 4,
    Slot = 5
}

public enum OperationResourceResolution
{
    Fixed = 1,
    CurrentMaterialSlot = 2,
    AvailableSlotInGroup = 3
}

public sealed record OperationResourceBinding
{
    public OperationResourceBinding(
        OperationResourceBindingId id,
        OperationResourceKind kind,
        string topologyTargetId,
        OperationResourceResolution resolution)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Operation resource kind.");
        }

        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution,
                "Unsupported Operation resource resolution.");
        }

        if (kind != OperationResourceKind.Slot
            && resolution != OperationResourceResolution.Fixed)
        {
            throw new ArgumentException(
                $"Operation resource kind {kind} only supports Fixed resolution.",
                nameof(resolution));
        }

        Kind = kind;
        TopologyTargetId = ProductionIdGuard.NotBlank(
            topologyTargetId,
            nameof(topologyTargetId));
        Resolution = resolution;
    }

    public OperationResourceBindingId Id { get; }

    public OperationResourceKind Kind { get; }

    public string TopologyTargetId { get; }

    public OperationResourceResolution Resolution { get; }
}
