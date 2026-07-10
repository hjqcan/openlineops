namespace OpenLineOps.Topology.Domain.Identifiers;

public sealed record AutomationSystemId
{
    public AutomationSystemId(string value)
    {
        Value = TopologyIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}
