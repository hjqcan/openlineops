namespace OpenLineOps.Runtime.Domain.Targets;

public static class RuntimeTargetKinds
{
    public const string System = "System";
    public const string Capability = "Capability";
    public const string Driver = "Driver";
    public const string SlotGroup = "SlotGroup";
    public const string Slot = "Slot";
    public const string Dut = "Dut";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        System,
        Capability,
        Driver,
        SlotGroup,
        Slot,
        Dut
    };
}

public sealed record RuntimeTargetReference
{
    public RuntimeTargetReference(string kind, string targetId)
    {
        if (!RuntimeTargetKinds.All.Contains(kind))
        {
            throw new ArgumentException($"Runtime target kind '{kind}' is not supported.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(targetId)
            || !string.Equals(targetId, targetId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Runtime target id must be a non-empty canonical string.", nameof(targetId));
        }

        Kind = kind;
        TargetId = targetId;
    }

    public string Kind { get; }

    public string TargetId { get; }
}
