using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Topology.Application.Topologies;

namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record OperationResourceValidationFailure(string Code, string Message);

public static class OperationResourceTopologyValidator
{
    public static OperationResourceValidationFailure? Validate(
        OperationDefinition operation,
        AutomationTopologyDetails topology)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(topology);

        var station = topology.Systems.SingleOrDefault(system => string.Equals(
            system.SystemId,
            operation.StationSystemId,
            StringComparison.Ordinal));
        if (station is null || !string.Equals(station.Kind, "Station", StringComparison.Ordinal))
        {
            return Failure(
                "OperationStationSystemInvalid",
                $"Operation {operation.Id} must reference an existing Station system.");
        }

        foreach (var resource in operation.Resources)
        {
            var failure = resource.Kind switch
            {
                OperationResourceKind.Station => ValidateStation(operation, resource),
                OperationResourceKind.Fixture => ValidateFixture(operation, resource, topology),
                OperationResourceKind.Device => ValidateDevice(operation, resource, topology),
                OperationResourceKind.SlotGroup => ValidateSlotGroup(operation, resource, topology),
                OperationResourceKind.Slot => ValidateSlot(operation, resource, topology),
                _ => Failure(
                    "OperationResourceKindInvalid",
                    $"Operation {operation.Id} resource {resource.Id} has unsupported kind {resource.Kind}.")
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    public static bool IsTargetWithinStation(
        string targetKind,
        string targetId,
        OperationDefinition operation,
        AutomationTopologyDetails topology)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(topology);
        return targetKind switch
        {
            "System" => topology.Systems.Any(system =>
                string.Equals(system.SystemId, targetId, StringComparison.Ordinal)
                && IsWithinStation(system.SystemId, operation.StationSystemId, topology)),
            "SlotGroup" => topology.SlotGroups.Any(group =>
                string.Equals(group.SlotGroupId, targetId, StringComparison.Ordinal)
                && string.Equals(group.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal)),
            "Slot" => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, targetId, StringComparison.Ordinal)
                && slot.IsEnabled
                && string.Equals(slot.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal)),
            "ProductionUnit" => true,
            "Capability" => topology.Systems.Any(system =>
                IsWithinStation(system.SystemId, operation.StationSystemId, topology)
                && system.ProvidedCapabilityIds.Contains(targetId, StringComparer.Ordinal)),
            "Driver" => topology.DriverBindings.Any(binding =>
                string.Equals(binding.BindingId, targetId, StringComparison.Ordinal)
                && topology.Systems.Any(system =>
                    string.Equals(system.SystemId, binding.OwnerSystemId, StringComparison.Ordinal)
                    && IsWithinStation(system.SystemId, operation.StationSystemId, topology))),
            _ => false
        };
    }

    public static bool IsTargetAuthorized(
        string targetKind,
        string targetId,
        OperationDefinition operation,
        AutomationTopologyDetails topology)
    {
        if (!IsTargetWithinStation(targetKind, targetId, operation, topology))
        {
            return false;
        }

        return targetKind switch
        {
            "System" when string.Equals(
                targetId,
                operation.StationSystemId,
                StringComparison.Ordinal) => true,
            "System" => operation.Resources.Any(resource =>
                resource.Kind == OperationResourceKind.Device
                && resource.Resolution == OperationResourceResolution.Fixed
                && string.Equals(
                    resource.TopologyTargetId,
                    targetId,
                    StringComparison.Ordinal)),
            "SlotGroup" => operation.Resources.Any(resource =>
                (resource.Kind is OperationResourceKind.SlotGroup
                    or OperationResourceKind.Fixture
                    && resource.Resolution == OperationResourceResolution.Fixed
                    || resource.Kind == OperationResourceKind.Slot
                    && resource.Resolution == OperationResourceResolution.AvailableSlotInGroup)
                && string.Equals(
                    resource.TopologyTargetId,
                    targetId,
                    StringComparison.Ordinal)),
            "Slot" => operation.Resources.Any(resource =>
                resource.Kind == OperationResourceKind.Slot
                && resource.Resolution == OperationResourceResolution.Fixed
                && string.Equals(
                    resource.TopologyTargetId,
                    targetId,
                    StringComparison.Ordinal)),
            "ProductionUnit" => true,
            "Capability" => CapabilityTargetAuthorized(targetId, operation, topology),
            "Driver" => operation.Resources.Any(resource =>
                resource.Kind == OperationResourceKind.Device
                && resource.Resolution == OperationResourceResolution.Fixed
                && string.Equals(
                    resource.TopologyTargetId,
                    targetId,
                    StringComparison.Ordinal)),
            _ => false
        };
    }

    public static OperationResourceValidationFailure? ValidateLineControllerAuthorization(
        OperationDefinition operation,
        LineControllerAuthorization authorization,
        AutomationTopologyDetails topology)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(topology);
        if (authorization.OperationId != operation.Id)
        {
            return Failure(
                "LineControllerOperationMismatch",
                $"Line Controller authorization {authorization.Id} belongs to another Operation.");
        }

        var controllerBinding = topology.DriverBindings.SingleOrDefault(binding =>
            string.Equals(binding.BindingId, authorization.ControllerBindingId, StringComparison.Ordinal));
        if (controllerBinding is null
            || !string.Equals(
                controllerBinding.OwnerSystemId,
                authorization.ControllerSystemId,
                StringComparison.Ordinal)
            || !string.Equals(
                controllerBinding.CapabilityId,
                authorization.ControllerCapabilityId,
                StringComparison.Ordinal)
            || !IsWithinStation(
                authorization.ControllerSystemId,
                operation.StationSystemId,
                topology)
            || controllerBinding.ProviderKind is not (
                "Simulator" or "DeviceInstance" or "PluginCommand" or "ExternalSystem"))
        {
            return Failure(
                "LineControllerProviderInvalid",
                $"Line Controller authorization {authorization.Id} must name one executable controller binding owned by the Operation Station subtree.");
        }

        if (!operation.Resources.Any(resource =>
                resource.Kind == OperationResourceKind.Device
                && resource.Resolution == OperationResourceResolution.Fixed
                && string.Equals(
                    resource.TopologyTargetId,
                    authorization.ControllerBindingId,
                    StringComparison.Ordinal)))
        {
            return Failure(
                "LineControllerResourceMissing",
                $"Line Controller authorization {authorization.Id} controller binding must be an exact Fixed Device resource of Operation {operation.Id}.");
        }

        if (!HasExactCapabilityAction(
                topology,
                authorization.ControllerCapabilityId,
                authorization.ControllerAction))
        {
            return Failure(
                "LineControllerCapabilityActionInvalid",
                $"Line Controller authorization {authorization.Id} controller capability/action does not match one topology capability contract.");
        }

        var targetStation = topology.Systems.SingleOrDefault(system => string.Equals(
            system.SystemId,
            authorization.TargetStationSystemId,
            StringComparison.Ordinal));
        var targetBinding = topology.DriverBindings.SingleOrDefault(binding => string.Equals(
            binding.BindingId,
            authorization.TargetBindingId,
            StringComparison.Ordinal));
        if (targetStation is null
            || !string.Equals(targetStation.Kind, "Station", StringComparison.Ordinal)
            || string.Equals(
                targetStation.SystemId,
                operation.StationSystemId,
                StringComparison.Ordinal)
            || targetBinding is null
            || !string.Equals(
                targetBinding.OwnerSystemId,
                authorization.TargetSystemId,
                StringComparison.Ordinal)
            || !string.Equals(
                targetBinding.CapabilityId,
                authorization.TargetCapabilityId,
                StringComparison.Ordinal)
            || !IsWithinStation(
                authorization.TargetSystemId,
                authorization.TargetStationSystemId,
                topology))
        {
            return Failure(
                "LineControllerRemoteTargetInvalid",
                $"Line Controller authorization {authorization.Id} must name one exact binding in a different declared target Station subtree.");
        }

        return HasExactCapabilityAction(
                topology,
                authorization.TargetCapabilityId,
                authorization.TargetAction)
            ? null
            : Failure(
                "LineControllerRemoteActionInvalid",
                $"Line Controller authorization {authorization.Id} remote capability/action does not match one topology capability contract.");
    }

    public static bool IsLineControllerActionAuthorized(
        FlowIrAction action,
        LineControllerAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(authorization);
        return action.Kind == FlowIrActionKind.DeviceCommand
            && string.Equals(action.ActionId, authorization.ActionId, StringComparison.Ordinal)
            && string.Equals(
                action.RequiredCapability,
                authorization.ControllerCapabilityId,
                StringComparison.Ordinal)
            && string.Equals(
                action.CommandName,
                authorization.ControllerAction,
                StringComparison.Ordinal)
            && action.Target.Kind == FlowIrTargetReferenceKind.Driver
            && string.Equals(
                action.Target.Reference,
                authorization.TargetBindingId,
                StringComparison.Ordinal);
    }

    private static OperationResourceValidationFailure? ValidateStation(
        OperationDefinition operation,
        OperationResourceBinding resource) =>
        resource.Resolution == OperationResourceResolution.Fixed
        && string.Equals(
            resource.TopologyTargetId,
            operation.StationSystemId,
            StringComparison.Ordinal)
            ? null
            : Failure(
                "OperationStationResourceInvalid",
                $"Operation {operation.Id} Station resource {resource.Id} must be Fixed to {operation.StationSystemId}.");

    private static OperationResourceValidationFailure? ValidateFixture(
        OperationDefinition operation,
        OperationResourceBinding resource,
        AutomationTopologyDetails topology)
    {
        var group = topology.SlotGroups.SingleOrDefault(candidate => string.Equals(
            candidate.SlotGroupId,
            resource.TopologyTargetId,
            StringComparison.Ordinal));
        if (resource.Resolution != OperationResourceResolution.Fixed
            || group is null
            || !string.Equals(group.Kind, "FixtureNest", StringComparison.Ordinal)
            || !string.Equals(group.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal))
        {
            return Failure(
                "OperationFixtureResourceInvalid",
                $"Operation {operation.Id} Fixture resource {resource.Id} must be Fixed to a FixtureNest in Station {operation.StationSystemId}.");
        }

        return HasEnabledSlot(group, topology)
            ? null
            : Failure(
                "OperationFixtureResourceUnavailable",
                $"Operation {operation.Id} Fixture resource {resource.Id} has no enabled Slot.");
    }

    private static OperationResourceValidationFailure? ValidateDevice(
        OperationDefinition operation,
        OperationResourceBinding resource,
        AutomationTopologyDetails topology)
    {
        if (resource.Resolution != OperationResourceResolution.Fixed)
        {
            return Failure(
                "OperationDeviceResourceResolutionInvalid",
                $"Operation {operation.Id} Device resource {resource.Id} must use Fixed resolution.");
        }

        var system = topology.Systems.SingleOrDefault(candidate => string.Equals(
            candidate.SystemId,
            resource.TopologyTargetId,
            StringComparison.Ordinal));
        if (system is not null)
        {
            return IsWithinStation(system.SystemId, operation.StationSystemId, topology)
                ? null
                : Failure(
                    "OperationDeviceResourceOutsideStation",
                    $"Operation {operation.Id} Device system {resource.TopologyTargetId} is outside Station subtree {operation.StationSystemId}.");
        }

        var binding = topology.DriverBindings.SingleOrDefault(candidate => string.Equals(
            candidate.BindingId,
            resource.TopologyTargetId,
            StringComparison.Ordinal));
        if (binding is null
            || binding.ProviderKind is not (
                "Simulator" or "DeviceInstance" or "PluginCommand" or "ExternalSystem")
            || !topology.Systems.Any(candidate =>
                string.Equals(candidate.SystemId, binding.OwnerSystemId, StringComparison.Ordinal)
                && IsWithinStation(candidate.SystemId, operation.StationSystemId, topology)))
        {
            return Failure(
                "OperationDeviceResourceInvalid",
                $"Operation {operation.Id} Device resource {resource.Id} must reference one executable device binding, or a System, inside Station subtree {operation.StationSystemId}.");
        }

        return null;
    }

    private static OperationResourceValidationFailure? ValidateSlotGroup(
        OperationDefinition operation,
        OperationResourceBinding resource,
        AutomationTopologyDetails topology)
    {
        var group = topology.SlotGroups.SingleOrDefault(candidate => string.Equals(
            candidate.SlotGroupId,
            resource.TopologyTargetId,
            StringComparison.Ordinal));
        if (resource.Resolution != OperationResourceResolution.Fixed
            || group is null
            || !string.Equals(group.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal))
        {
            return Failure(
                "OperationSlotGroupResourceInvalid",
                $"Operation {operation.Id} SlotGroup resource {resource.Id} must be Fixed to a group in Station {operation.StationSystemId}.");
        }

        return HasEnabledSlot(group, topology)
            ? null
            : Failure(
                "OperationSlotGroupResourceUnavailable",
                $"Operation {operation.Id} SlotGroup resource {resource.Id} has no enabled Slot.");
    }

    private static OperationResourceValidationFailure? ValidateSlot(
        OperationDefinition operation,
        OperationResourceBinding resource,
        AutomationTopologyDetails topology)
    {
        if (resource.Resolution == OperationResourceResolution.CurrentMaterialSlot)
        {
            return string.Equals(
                    resource.TopologyTargetId,
                    operation.StationSystemId,
                    StringComparison.Ordinal)
                ? null
                : Failure(
                    "OperationCurrentMaterialSlotAnchorInvalid",
                    $"Operation {operation.Id} CurrentMaterialSlot resource {resource.Id} must be anchored to Station {operation.StationSystemId}.");
        }

        if (resource.Resolution == OperationResourceResolution.AvailableSlotInGroup)
        {
            var group = topology.SlotGroups.SingleOrDefault(candidate => string.Equals(
                candidate.SlotGroupId,
                resource.TopologyTargetId,
                StringComparison.Ordinal));
            if (group is null
                || !string.Equals(group.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal))
            {
                return Failure(
                    "OperationAvailableSlotGroupInvalid",
                    $"Operation {operation.Id} AvailableSlotInGroup resource {resource.Id} must reference a group in Station {operation.StationSystemId}.");
            }

            return HasEnabledSlot(group, topology)
                ? null
                : Failure(
                    "OperationAvailableSlotGroupUnavailable",
                    $"Operation {operation.Id} AvailableSlotInGroup resource {resource.Id} has no enabled Slot.");
        }

        var slot = topology.Slots.SingleOrDefault(candidate => string.Equals(
            candidate.SlotId,
            resource.TopologyTargetId,
            StringComparison.Ordinal));
        return slot is not null
            && slot.IsEnabled
            && string.Equals(slot.ParentSystemId, operation.StationSystemId, StringComparison.Ordinal)
            ? null
            : Failure(
                "OperationFixedSlotResourceInvalid",
                $"Operation {operation.Id} Fixed Slot resource {resource.Id} must reference an enabled Slot in Station {operation.StationSystemId}.");
    }

    private static bool HasEnabledSlot(
        SlotGroupDetails group,
        AutomationTopologyDetails topology) =>
        topology.Slots.Any(slot =>
            slot.IsEnabled
            && string.Equals(slot.SlotGroupId, group.SlotGroupId, StringComparison.Ordinal)
            && string.Equals(slot.ParentSystemId, group.ParentSystemId, StringComparison.Ordinal));

    private static bool IsWithinStation(
        string systemId,
        string stationSystemId,
        AutomationTopologyDetails topology)
    {
        var currentId = systemId;
        for (var depth = 0; depth <= topology.Systems.Count; depth++)
        {
            if (string.Equals(currentId, stationSystemId, StringComparison.Ordinal))
            {
                return true;
            }

            var current = topology.Systems.SingleOrDefault(system => string.Equals(
                system.SystemId,
                currentId,
                StringComparison.Ordinal));
            if (current?.ParentSystemId is null)
            {
                return false;
            }

            currentId = current.ParentSystemId;
        }

        return false;
    }

    private static bool HasExactCapabilityAction(
        AutomationTopologyDetails topology,
        string capabilityId,
        string action) =>
        topology.Capabilities.Count(capability =>
            string.Equals(capability.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(capability.CommandName, action, StringComparison.Ordinal)) == 1;

    private static bool CapabilityTargetAuthorized(
        string capabilityId,
        OperationDefinition operation,
        AutomationTopologyDetails topology)
    {
        var bindings = topology.DriverBindings
            .Where(binding => string.Equals(
                binding.CapabilityId,
                capabilityId,
                StringComparison.Ordinal)
                && topology.Systems.Any(system =>
                    string.Equals(system.SystemId, binding.OwnerSystemId, StringComparison.Ordinal)
                    && IsWithinStation(system.SystemId, operation.StationSystemId, topology)))
            .ToArray();
        if (bindings.Length == 0
            || bindings.All(binding => binding.ProviderKind is not "DeviceInstance" and not "PluginCommand"))
        {
            return true;
        }

        return bindings.Any(binding => operation.Resources.Any(resource =>
            resource.Kind == OperationResourceKind.Device
            && resource.Resolution == OperationResourceResolution.Fixed
            && string.Equals(
                resource.TopologyTargetId,
                binding.BindingId,
                StringComparison.Ordinal)));
    }

    private static OperationResourceValidationFailure Failure(string code, string message) =>
        new(code, message);
}
