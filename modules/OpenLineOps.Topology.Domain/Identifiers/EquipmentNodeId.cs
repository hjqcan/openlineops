namespace OpenLineOps.Topology.Domain.Identifiers;

public sealed record EquipmentNodeId
{
    public EquipmentNodeId(string value)
    {
        Value = TopologyIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
