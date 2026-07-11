using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;

namespace OpenLineOps.Engineering.Domain.Stations;

public sealed class StationProfile : Entity<StationProfileId>
{
    private readonly List<DeviceBinding> _deviceBindings = [];

    private StationProfile(StationProfileId id, string stationSystemId, string displayName)
        : base(id)
    {
        StationSystemId = EngineeringIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
        DisplayName = EngineeringIdGuard.NotBlank(displayName, nameof(displayName));
    }

    public string StationSystemId { get; }

    public string DisplayName { get; }

    public IReadOnlyCollection<DeviceBinding> DeviceBindings => _deviceBindings.AsReadOnly();

    public static StationProfile Create(StationProfileId id, string stationSystemId, string displayName)
    {
        return new StationProfile(id, stationSystemId, displayName);
    }

    public static StationProfile Restore(
        StationProfileId id,
        string stationSystemId,
        string displayName,
        IEnumerable<DeviceBinding> deviceBindings)
    {
        ArgumentNullException.ThrowIfNull(deviceBindings);

        var stationProfile = new StationProfile(id, stationSystemId, displayName);
        stationProfile._deviceBindings.AddRange(deviceBindings);

        return stationProfile;
    }

    public EngineeringOperationResult AddDeviceBinding(DeviceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_deviceBindings.Any(candidate => candidate.Id == binding.Id))
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.DeviceBindingAlreadyExists",
                $"Device binding {binding.Id} already exists.");
        }

        if (_deviceBindings.Any(candidate =>
                string.Equals(
                    candidate.OwnerSystemId,
                    binding.OwnerSystemId,
                    StringComparison.Ordinal)
                && candidate.CapabilityId == binding.CapabilityId))
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.CapabilityAlreadyBound",
                $"Capability {binding.CapabilityId} is already bound for System {binding.OwnerSystemId} in station profile {Id}.");
        }

        _deviceBindings.Add(binding);

        return EngineeringOperationResult.Accepted("Device binding added.");
    }
}
