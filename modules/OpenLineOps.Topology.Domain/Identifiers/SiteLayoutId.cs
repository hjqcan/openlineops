namespace OpenLineOps.Topology.Domain.Identifiers;

public sealed record SiteLayoutId
{
    public SiteLayoutId(string value)
    {
        Value = TopologyIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
