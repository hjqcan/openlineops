using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;

namespace OpenLineOps.Engineering.Domain.Stations;

public sealed class StationProfile : Entity<StationProfileId>
{
    private readonly List<DeviceBinding> _deviceBindings = [];

    private StationProfile(StationProfileId id, string displayName)
        : base(id)
    {
        DisplayName = EngineeringIdGuard.NotBlank(displayName, nameof(displayName));
    }

    public string DisplayName { get; }

    public IReadOnlyCollection<DeviceBinding> DeviceBindings => _deviceBindings.AsReadOnly();

    public static StationProfile Create(StationProfileId id, string displayName)
    {
        return new StationProfile(id, displayName);
    }

    public static StationProfile Restore(
        StationProfileId id,
        string displayName,
        IEnumerable<DeviceBinding> deviceBindings)
    {
        ArgumentNullException.ThrowIfNull(deviceBindings);

        var stationProfile = new StationProfile(id, displayName);
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

        if (_deviceBindings.Any(candidate => candidate.CapabilityId == binding.CapabilityId))
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.CapabilityAlreadyBound",
                $"Capability {binding.CapabilityId} is already bound in station profile {Id}.");
        }

        _deviceBindings.Add(binding);

        return EngineeringOperationResult.Accepted("Device binding added.");
    }
}
