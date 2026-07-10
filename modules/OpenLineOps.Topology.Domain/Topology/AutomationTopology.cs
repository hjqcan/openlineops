using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;

namespace OpenLineOps.Topology.Domain.Topology;

public sealed class AutomationTopology : AggregateRoot<AutomationTopologyId>
{
    private readonly List<AutomationSystem> _systems = [];
    private readonly List<CapabilityContract> _capabilities = [];
    private readonly List<DriverBinding> _driverBindings = [];
    private readonly List<SlotGroup> _slotGroups = [];
    private readonly List<SlotDefinition> _slots = [];

    private AutomationTopology(AutomationTopologyId id, string displayName, DateTimeOffset createdAtUtc)
        : base(id)
    {
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
    }

    public string DisplayName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyCollection<AutomationSystem> Systems => _systems.AsReadOnly();

    public IReadOnlyCollection<CapabilityContract> Capabilities => _capabilities.AsReadOnly();

    public IReadOnlyCollection<DriverBinding> DriverBindings => _driverBindings.AsReadOnly();

    public IReadOnlyCollection<SlotGroup> SlotGroups => _slotGroups.AsReadOnly();

    public IReadOnlyCollection<SlotDefinition> Slots => _slots.AsReadOnly();

    public static AutomationTopology Create(
        AutomationTopologyId id,
        string displayName,
        DateTimeOffset createdAtUtc)
    {
        return new AutomationTopology(id, displayName, createdAtUtc);
    }

    public TopologyOperationResult AddSystem(AutomationSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (_systems.Any(candidate => candidate.Id == system.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SystemAlreadyExists",
                $"Automation system {system.Id} already exists in topology {Id}.");
        }

        var parent = system.ParentSystemId is null
            ? null
            : _systems.SingleOrDefault(candidate => candidate.Id == system.ParentSystemId);
        if (system.ParentSystemId is not null && parent is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.ParentSystemMissing",
                $"Parent automation system {system.ParentSystemId} was not found in topology {Id}.");
        }

        if (system is StationSystem && parent is not null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.StationMustBeRoot",
                $"Station system {system.Id} must be placed at the topology root.");
        }

        if (system is GeneralAutomationSystem && parent is not null and not StationSystem)
        {
            return TopologyOperationResult.Rejected(
                "Topology.ChildSystemRequiresStation",
                $"Child automation system {system.Id} must belong directly to a Station system.");
        }

        var missingCapability = system.RequiredCapabilities
            .Concat(system.ProvidedCapabilities)
            .FirstOrDefault(capabilityId => _capabilities.All(candidate => candidate.Id != capabilityId));
        if (missingCapability is not null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SystemCapabilityMissing",
                $"Capability contract {missingCapability} must exist before system {system.Id} can reference it.");
        }

        _systems.Add(system);
        return TopologyOperationResult.Accepted("Automation system added.");
    }

    public TopologyOperationResult AddCapability(CapabilityContract capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (_capabilities.Any(candidate => candidate.Id == capability.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.CapabilityAlreadyExists",
                $"Capability contract {capability.Id} already exists in topology {Id}.");
        }

        _capabilities.Add(capability);
        return TopologyOperationResult.Accepted("Capability contract added.");
    }

    public TopologyOperationResult AddDriverBinding(DriverBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_driverBindings.Any(candidate => candidate.Id == binding.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.DriverBindingAlreadyExists",
                $"Driver binding {binding.Id} already exists in topology {Id}.");
        }

        if (_capabilities.All(candidate => candidate.Id != binding.CapabilityId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.DriverBindingCapabilityMissing",
                $"Capability contract {binding.CapabilityId} must exist before driver binding {binding.Id} can be added.");
        }

        if (_driverBindings.Any(candidate => candidate.CapabilityId == binding.CapabilityId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.CapabilityAlreadyBound",
                $"Capability contract {binding.CapabilityId} is already bound in topology {Id}.");
        }

        _driverBindings.Add(binding);
        return TopologyOperationResult.Accepted("Driver binding added.");
    }

    public TopologyOperationResult AddSlotGroup(SlotGroup slotGroup)
    {
        ArgumentNullException.ThrowIfNull(slotGroup);

        if (_slotGroups.Any(candidate => candidate.Id == slotGroup.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupAlreadyExists",
                $"Slot group {slotGroup.Id} already exists in topology {Id}.");
        }

        var parent = _systems.SingleOrDefault(candidate => candidate.Id == slotGroup.ParentSystemId);
        if (parent is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupSystemMissing",
                $"Automation system {slotGroup.ParentSystemId} must exist before slot group {slotGroup.Id} can be added.");
        }

        if (parent is not StationSystem)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupRequiresStation",
                $"Slot group {slotGroup.Id} must belong to a Station system.");
        }

        _slotGroups.Add(slotGroup);
        return TopologyOperationResult.Accepted("Slot group added.");
    }

    public TopologyOperationResult AddSlot(SlotDefinition slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var slotGroup = _slotGroups.SingleOrDefault(candidate => candidate.Id == slot.SlotGroupId);
        if (slotGroup is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupMissing",
                $"Slot group {slot.SlotGroupId} was not found in topology {Id}.");
        }

        if (slotGroup.ParentSystemId != slot.ParentSystemId)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotSystemMismatch",
                $"Slot {slot.Id} and slot group {slotGroup.Id} must belong to the same Station system.");
        }

        if (_slots.Any(candidate => candidate.Id == slot.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAlreadyExists",
                $"Slot {slot.Id} already exists in topology {Id}.");
        }

        if (_slots.Any(candidate => candidate.ParentSystemId == slot.ParentSystemId
            && string.Equals(candidate.Address, slot.Address, StringComparison.Ordinal)))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAddressAlreadyExists",
                $"Slot address {slot.Address} already exists under system {slot.ParentSystemId}.");
        }

        var addToGroup = slotGroup.AddSlot(slot.Id);
        if (!addToGroup.Succeeded)
        {
            return addToGroup;
        }

        _slots.Add(slot);
        return TopologyOperationResult.Accepted("Slot added.");
    }

    public TopologyOperationResult UpdateSystem(
        AutomationSystemId systemId,
        string systemType,
        string displayName,
        IReadOnlyDictionary<string, string> metadata)
    {
        var system = FindSystem(systemId);
        if (system is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SystemNotFound",
                $"Automation system {systemId} was not found in topology {Id}.");
        }

        system.Update(systemType, displayName, metadata);
        return TopologyOperationResult.Accepted("Automation system updated.");
    }

    public TopologyOperationResult RemoveSystem(AutomationSystemId systemId)
    {
        if (FindSystem(systemId) is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SystemNotFound",
                $"Automation system {systemId} was not found in topology {Id}.");
        }

        var removedSystemIds = new HashSet<AutomationSystemId> { systemId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in _systems.Where(candidate => candidate.ParentSystemId is not null
                         && removedSystemIds.Contains(candidate.ParentSystemId)))
            {
                changed |= removedSystemIds.Add(child.Id);
            }
        }

        var removedGroupIds = _slotGroups
            .Where(group => removedSystemIds.Contains(group.ParentSystemId))
            .Select(group => group.Id)
            .ToHashSet();
        _slots.RemoveAll(slot => removedSystemIds.Contains(slot.ParentSystemId)
            || removedGroupIds.Contains(slot.SlotGroupId));
        _slotGroups.RemoveAll(group => removedGroupIds.Contains(group.Id));
        _systems.RemoveAll(system => removedSystemIds.Contains(system.Id));

        return TopologyOperationResult.Accepted("Automation system subtree removed.");
    }

    public TopologyOperationResult UpdateSlotGroup(
        SlotGroupId slotGroupId,
        string displayName,
        SlotGroupKind kind,
        int capacity)
    {
        var group = FindSlotGroup(slotGroupId);
        return group is null
            ? TopologyOperationResult.Rejected(
                "Topology.SlotGroupNotFound",
                $"Slot group {slotGroupId} was not found in topology {Id}.")
            : group.Update(displayName, kind, capacity);
    }

    public TopologyOperationResult RemoveSlotGroup(SlotGroupId slotGroupId)
    {
        var group = FindSlotGroup(slotGroupId);
        if (group is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupNotFound",
                $"Slot group {slotGroupId} was not found in topology {Id}.");
        }

        _slots.RemoveAll(slot => slot.SlotGroupId == slotGroupId);
        _slotGroups.Remove(group);
        return TopologyOperationResult.Accepted("Slot group and its slots removed.");
    }

    public TopologyOperationResult UpdateSlot(
        SlotDefinitionId slotId,
        string address,
        string displayName,
        SlotMaterialKind materialKind,
        bool isEnabled)
    {
        var slot = FindSlot(slotId);
        if (slot is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotNotFound",
                $"Slot {slotId} was not found in topology {Id}.");
        }

        var normalizedAddress = TopologyIdGuard.NotBlank(address, nameof(address));
        if (_slots.Any(candidate => candidate.Id != slotId
            && candidate.ParentSystemId == slot.ParentSystemId
            && string.Equals(candidate.Address, normalizedAddress, StringComparison.Ordinal)))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAddressAlreadyExists",
                $"Slot address {normalizedAddress} already exists under system {slot.ParentSystemId}.");
        }

        slot.Update(normalizedAddress, displayName, materialKind, isEnabled);
        return TopologyOperationResult.Accepted("Slot updated.");
    }

    public TopologyOperationResult RemoveSlot(SlotDefinitionId slotId)
    {
        var slot = FindSlot(slotId);
        if (slot is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotNotFound",
                $"Slot {slotId} was not found in topology {Id}.");
        }

        FindSlotGroup(slot.SlotGroupId)?.RemoveSlot(slotId);
        _slots.Remove(slot);
        return TopologyOperationResult.Accepted("Slot removed.");
    }

    public AutomationSystem? FindSystem(AutomationSystemId systemId) =>
        _systems.SingleOrDefault(candidate => candidate.Id == systemId);

    public SlotGroup? FindSlotGroup(SlotGroupId slotGroupId) =>
        _slotGroups.SingleOrDefault(candidate => candidate.Id == slotGroupId);

    public SlotDefinition? FindSlot(SlotDefinitionId slotId) =>
        _slots.SingleOrDefault(candidate => candidate.Id == slotId);
}
