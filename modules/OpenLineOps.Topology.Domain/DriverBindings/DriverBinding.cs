using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.DriverBindings;

public sealed class DriverBinding : Entity<DriverBindingId>
{
    private DriverBinding(
        DriverBindingId id,
        CapabilityContractId capabilityId,
        DriverProviderKind providerKind,
        string providerKey)
        : base(id)
    {
        CapabilityId = capabilityId;
        ProviderKind = providerKind;
        ProviderKey = TopologyIdGuard.NotBlank(providerKey, nameof(providerKey));
    }

    public CapabilityContractId CapabilityId { get; }

    public DriverProviderKind ProviderKind { get; }

    public string ProviderKey { get; }

    public static DriverBinding Create(
        DriverBindingId id,
        CapabilityContractId capabilityId,
        DriverProviderKind providerKind,
        string providerKey)
    {
        return new DriverBinding(id, capabilityId, providerKind, providerKey);
    }
}
