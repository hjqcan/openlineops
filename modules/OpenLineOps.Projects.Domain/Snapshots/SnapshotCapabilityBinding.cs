namespace OpenLineOps.Projects.Domain.Snapshots;

public sealed record SnapshotCapabilityBinding(string CapabilityId, string BindingId, string ProviderKind, string ProviderKey)
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
}
