using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Application.Topologies;

public static class AutomationTopologyMapper
{
    public static AutomationTopologyDetails ToDetails(AutomationTopology topology)
    {
        return new AutomationTopologyDetails(
            topology.Id.Value,
            topology.DisplayName,
            topology.CreatedAtUtc,
            topology.Nodes.OrderBy(node => node.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray(),
            topology.Modules.OrderBy(module => module.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray(),
            topology.Capabilities.OrderBy(capability => capability.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray(),
            topology.DriverBindings.OrderBy(binding => binding.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray(),
            topology.SlotGroups.OrderBy(group => group.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray(),
            topology.Slots.OrderBy(slot => slot.Id.Value, StringComparer.Ordinal).Select(ToDetails).ToArray());
    }

    public static AutomationTopologySummary ToSummary(AutomationTopology topology)
    {
        return new AutomationTopologySummary(
            topology.Id.Value,
            topology.DisplayName,
            topology.Nodes.Count,
            topology.Modules.Count,
            topology.Slots.Count);
    }

    public static SiteLayoutDetails ToDetails(SiteLayout layout)
    {
        return new SiteLayoutDetails(
            layout.Id.Value,
            layout.TopologyId.Value,
            layout.DisplayName,
            layout.CanvasWidth,
            layout.CanvasHeight,
            layout.Units,
            layout.Elements
                .OrderBy(element => element.Id.Value, StringComparer.Ordinal)
                .Select(ToDetails)
                .ToArray());
    }

    private static EquipmentNodeDetails ToDetails(EquipmentNode node)
    {
        return new EquipmentNodeDetails(
            node.Id.Value,
            node.ParentId?.Value,
            node.Kind.ToString(),
            node.DisplayName);
    }

    private static AutomationModuleDetails ToDetails(AutomationModule module)
    {
        return new AutomationModuleDetails(
            module.Id.Value,
            module.NodeId.Value,
            module.ModuleKind,
            module.DisplayName,
            module.RequiredCapabilities.Select(capabilityId => capabilityId.Value).ToArray(),
            module.ProvidedCapabilities.Select(capabilityId => capabilityId.Value).ToArray());
    }

    private static CapabilityContractDetails ToDetails(CapabilityContract capability)
    {
        return new CapabilityContractDetails(
            capability.Id.Value,
            capability.CommandName,
            capability.Version.ToString(),
            capability.InputSchema,
            capability.OutputSchema,
            (int)capability.Timeout.TotalSeconds,
            capability.SafetyClass.ToString());
    }

    private static DriverBindingDetails ToDetails(DriverBinding binding)
    {
        return new DriverBindingDetails(
            binding.Id.Value,
            binding.CapabilityId.Value,
            binding.ProviderKind.ToString(),
            binding.ProviderKey);
    }

    private static SlotGroupDetails ToDetails(SlotGroup group)
    {
        return new SlotGroupDetails(
            group.Id.Value,
            group.ParentNodeId.Value,
            group.DisplayName,
            group.Kind.ToString(),
            group.Capacity,
            group.SlotIds.Select(slotId => slotId.Value).ToArray());
    }

    private static SlotDefinitionDetails ToDetails(SlotDefinition slot)
    {
        return new SlotDefinitionDetails(
            slot.Id.Value,
            slot.ParentNodeId.Value,
            slot.Address,
            slot.DisplayName,
            slot.MaterialKind.ToString(),
            slot.IsEnabled);
    }

    private static SiteLayoutElementDetails ToDetails(SiteLayoutElement element)
    {
        return new SiteLayoutElementDetails(
            element.Id.Value,
            element.Kind.ToString(),
            element.Target.Kind.ToString(),
            element.Target.TargetId,
            element.X,
            element.Y,
            element.Width,
            element.Height,
            element.RotationDegrees,
            element.LayerId,
            element.Label);
    }
}
