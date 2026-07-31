namespace OpenLineOps.Maintenance.Domain.Identifiers;

public sealed record EquipmentAssetId
{
    public EquipmentAssetId(string value)
    {
        Value = MaintenanceGuard.RequireCanonicalText(
            value,
            nameof(value),
            maximumLength: 96);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record MaintenancePlanId
{
    public MaintenancePlanId(string value)
    {
        Value = MaintenanceGuard.RequireCanonicalText(
            value,
            nameof(value),
            maximumLength: 80);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record MaintenanceTaskId
{
    public MaintenanceTaskId(string value)
    {
        Value = MaintenanceGuard.RequireCanonicalText(
            value,
            nameof(value),
            maximumLength: 112);
    }

    public string Value { get; }

    public override string ToString() => Value;
}
