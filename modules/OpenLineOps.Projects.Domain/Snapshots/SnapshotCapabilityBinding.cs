namespace OpenLineOps.Projects.Domain.Snapshots;

public sealed record SnapshotCapabilityBinding(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
    string OwnerSystemId,
    string OwnerStationSystemId)
{
    public string CapabilityId { get; } = string.IsNullOrWhiteSpace(CapabilityId)
        ? throw new ArgumentException("Capability id cannot be empty.", nameof(CapabilityId))
        : CapabilityId.Trim();

    public string BindingId { get; } = string.IsNullOrWhiteSpace(BindingId)
        ? throw new ArgumentException("Binding id cannot be empty.", nameof(BindingId))
        : BindingId.Trim();

    public string ProviderKind { get; } = string.IsNullOrWhiteSpace(ProviderKind)
        ? throw new ArgumentException("Provider kind cannot be empty.", nameof(ProviderKind))
        : ProviderKind.Trim();

    public string ProviderKey { get; } = string.IsNullOrWhiteSpace(ProviderKey)
        ? throw new ArgumentException("Provider key cannot be empty.", nameof(ProviderKey))
        : ProviderKey.Trim();

    public string OwnerSystemId { get; } = string.IsNullOrWhiteSpace(OwnerSystemId)
        ? throw new ArgumentException("Owner system id cannot be empty.", nameof(OwnerSystemId))
        : OwnerSystemId.Trim();

    public string OwnerStationSystemId { get; } = string.IsNullOrWhiteSpace(OwnerStationSystemId)
        ? throw new ArgumentException("Owner Station system id cannot be empty.", nameof(OwnerStationSystemId))
        : OwnerStationSystemId.Trim();
}
