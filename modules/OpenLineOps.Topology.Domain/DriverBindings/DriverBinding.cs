using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.DriverBindings;

public sealed class DriverBinding : Entity<DriverBindingId>
{
    private DriverBinding(
        DriverBindingId id,
        AutomationSystemId ownerSystemId,
        CapabilityContractId capabilityId,
        DriverProviderKind providerKind,
        string providerKey)
        : base(id)
    {
        OwnerSystemId = ownerSystemId ?? throw new ArgumentNullException(nameof(ownerSystemId));
        CapabilityId = capabilityId;
        ProviderKind = providerKind;
        ProviderKey = TopologyIdGuard.NotBlank(providerKey, nameof(providerKey));
    }

    public CapabilityContractId CapabilityId { get; private set; }

    public AutomationSystemId OwnerSystemId { get; private set; }

    public DriverProviderKind ProviderKind { get; private set; }

    public string ProviderKey { get; private set; }

    public static DriverBinding Create(
        DriverBindingId id,
        AutomationSystemId ownerSystemId,
        CapabilityContractId capabilityId,
        DriverProviderKind providerKind,
        string providerKey)
    {
        return new DriverBinding(id, ownerSystemId, capabilityId, providerKind, providerKey);
    }

    internal void Update(
        AutomationSystemId ownerSystemId,
        CapabilityContractId capabilityId,
        DriverProviderKind providerKind,
        string providerKey)
    {
        OwnerSystemId = ownerSystemId ?? throw new ArgumentNullException(nameof(ownerSystemId));
        CapabilityId = capabilityId ?? throw new ArgumentNullException(nameof(capabilityId));
        ProviderKind = providerKind;
        ProviderKey = TopologyIdGuard.NotBlank(providerKey, nameof(providerKey));
    }
}
