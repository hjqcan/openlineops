using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;

namespace OpenLineOps.Topology.Domain.Topology;

public sealed class AutomationTopology : AggregateRoot<AutomationTopologyId>
{
    private readonly List<EquipmentNode> _nodes = [];
    private readonly List<AutomationModule> _modules = [];
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

    public IReadOnlyCollection<EquipmentNode> Nodes => _nodes.AsReadOnly();

    public IReadOnlyCollection<AutomationModule> Modules => _modules.AsReadOnly();

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

    public TopologyOperationResult AddEquipmentNode(EquipmentNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_nodes.Any(candidate => candidate.Id == node.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.NodeAlreadyExists",
                $"Equipment node {node.Id} already exists in topology {Id}.");
        }

        if (node.ParentId is null)
        {
            if (_nodes.Any(candidate => candidate.ParentId is null))
            {
                return TopologyOperationResult.Rejected(
                    "Topology.RootAlreadyExists",
                    $"Topology {Id} already has a root equipment node.");
            }
        }
        else
        {
            var parent = _nodes.SingleOrDefault(candidate => candidate.Id == node.ParentId);
            if (parent is null)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.ParentNodeMissing",
                    $"Parent equipment node {node.ParentId} was not found in topology {Id}.");
            }

            if (!IsChildKindAllowed(parent.Kind, node.Kind))
            {
                return TopologyOperationResult.Rejected(
                    "Topology.NodeKindNotAllowed",
                    $"Equipment node kind {node.Kind} is not allowed under parent kind {parent.Kind}.");
            }
        }

        _nodes.Add(node);

        return TopologyOperationResult.Accepted("Equipment node added.");
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

    public TopologyOperationResult AddModule(AutomationModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (_modules.Any(candidate => candidate.Id == module.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.ModuleAlreadyExists",
                $"Automation module {module.Id} already exists in topology {Id}.");
        }

        if (_nodes.All(candidate => candidate.Id != module.NodeId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.ModuleNodeMissing",
                $"Equipment node {module.NodeId} must exist before module {module.Id} can be added.");
        }

        var missingCapability = module.RequiredCapabilities
            .Concat(module.ProvidedCapabilities)
            .FirstOrDefault(capabilityId => _capabilities.All(candidate => candidate.Id != capabilityId));

        if (missingCapability is not null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.ModuleCapabilityMissing",
                $"Capability contract {missingCapability} must exist before module {module.Id} can reference it.");
        }

        _modules.Add(module);

        return TopologyOperationResult.Accepted("Automation module added.");
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

        if (_nodes.All(candidate => candidate.Id != slotGroup.ParentNodeId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupNodeMissing",
                $"Equipment node {slotGroup.ParentNodeId} must exist before slot group {slotGroup.Id} can be added.");
        }

        _slotGroups.Add(slotGroup);

        return TopologyOperationResult.Accepted("Slot group added.");
    }

    public TopologyOperationResult AddSlotToGroup(SlotGroupId slotGroupId, SlotDefinition slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var slotGroup = _slotGroups.SingleOrDefault(candidate => candidate.Id == slotGroupId);
        if (slotGroup is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotGroupMissing",
                $"Slot group {slotGroupId} was not found in topology {Id}.");
        }

        if (_nodes.All(candidate => candidate.Id != slot.ParentNodeId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotNodeMissing",
                $"Equipment node {slot.ParentNodeId} must exist before slot {slot.Id} can be added.");
        }

        if (_slots.Any(candidate => candidate.Id == slot.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAlreadyExists",
                $"Slot {slot.Id} already exists in topology {Id}.");
        }

        if (_slots.Any(candidate => candidate.ParentNodeId == slot.ParentNodeId
            && string.Equals(candidate.Address, slot.Address, StringComparison.OrdinalIgnoreCase)))
        {
            return TopologyOperationResult.Rejected(
                "Topology.SlotAddressAlreadyExists",
                $"Slot address {slot.Address} already exists under equipment node {slot.ParentNodeId}.");
        }

        var addToGroup = slotGroup.AddSlot(slot.Id);
        if (!addToGroup.Succeeded)
        {
            return addToGroup;
        }

        _slots.Add(slot);

        return TopologyOperationResult.Accepted("Slot added.");
    }

    public bool HasLayoutTarget(string targetId)
    {
        return _nodes.Any(candidate => candidate.Id.Value == targetId)
            || _modules.Any(candidate => candidate.Id.Value == targetId)
            || _slotGroups.Any(candidate => candidate.Id.Value == targetId)
            || _slots.Any(candidate => candidate.Id.Value == targetId);
    }

    private static bool IsChildKindAllowed(EquipmentNodeKind parent, EquipmentNodeKind child)
    {
        return parent switch
        {
            EquipmentNodeKind.Site => child is not EquipmentNodeKind.Site,
            EquipmentNodeKind.Area => child is EquipmentNodeKind.Line
                or EquipmentNodeKind.Cell
                or EquipmentNodeKind.Station
                or EquipmentNodeKind.Unit
                or EquipmentNodeKind.Module
                or EquipmentNodeKind.Buffer
                or EquipmentNodeKind.Transport
                or EquipmentNodeKind.DeviceMount
                or EquipmentNodeKind.ExternalSystem
                or EquipmentNodeKind.LogicalGroup,
            EquipmentNodeKind.Line => child is EquipmentNodeKind.Cell
                or EquipmentNodeKind.Station
                or EquipmentNodeKind.Unit
                or EquipmentNodeKind.Module
                or EquipmentNodeKind.Buffer
                or EquipmentNodeKind.Transport
                or EquipmentNodeKind.DeviceMount
                or EquipmentNodeKind.ExternalSystem
                or EquipmentNodeKind.LogicalGroup,
            EquipmentNodeKind.Cell or EquipmentNodeKind.Station or EquipmentNodeKind.Unit => child is EquipmentNodeKind.Module
                or EquipmentNodeKind.Fixture
                or EquipmentNodeKind.Buffer
                or EquipmentNodeKind.Transport
                or EquipmentNodeKind.DeviceMount
                or EquipmentNodeKind.ExternalSystem
                or EquipmentNodeKind.LogicalGroup,
            EquipmentNodeKind.LogicalGroup => child is not EquipmentNodeKind.Site
                and not EquipmentNodeKind.Area
                and not EquipmentNodeKind.Line,
            _ => false
        };
    }
}
