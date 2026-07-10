using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal static class ProjectTopologyResourceSnapshotMapper
{
    public static ProjectAutomationTopologyDocument FromTopology(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopology topology)
    {
        var slotGroupsBySlotId = topology.SlotGroups
            .SelectMany(group => group.SlotIds.Select(slotId => (slotId, group.Id)))
            .ToDictionary(candidate => candidate.slotId, candidate => candidate.Id);

        return new ProjectAutomationTopologyDocument(
            ProjectAutomationTopologyDocument.CurrentFormatVersion,
            ProjectAutomationTopologyDocument.Kind,
            scope.ApplicationId,
            topology.Id.Value,
            topology.DisplayName,
            topology.CreatedAtUtc,
            topology.Nodes.Select(node => new EquipmentNodeDocument(
                node.Id.Value,
                node.ParentId?.Value,
                node.Kind.ToString(),
                node.DisplayName)).ToArray(),
            topology.Modules.Select(module => new AutomationModuleDocument(
                module.Id.Value,
                module.NodeId.Value,
                module.ModuleKind,
                module.DisplayName,
                module.RequiredCapabilities.Select(capabilityId => capabilityId.Value).ToArray(),
                module.ProvidedCapabilities.Select(capabilityId => capabilityId.Value).ToArray())).ToArray(),
            topology.Capabilities.Select(capability => new CapabilityContractDocument(
                capability.Id.Value,
                capability.CommandName,
                capability.Version.ToString(),
                capability.InputSchema,
                capability.OutputSchema,
                capability.Timeout.Ticks,
                capability.SafetyClass.ToString())).ToArray(),
            topology.DriverBindings.Select(binding => new DriverBindingDocument(
                binding.Id.Value,
                binding.CapabilityId.Value,
                binding.ProviderKind.ToString(),
                binding.ProviderKey)).ToArray(),
            topology.SlotGroups.Select(group => new SlotGroupDocument(
                group.Id.Value,
                group.ParentNodeId.Value,
                group.DisplayName,
                group.Kind.ToString(),
                group.Capacity)).ToArray(),
            topology.Slots.Select(slot => new SlotDefinitionDocument(
                slotGroupsBySlotId.TryGetValue(slot.Id, out var slotGroupId)
                    ? slotGroupId.Value
                    : throw new InvalidDataException($"Slot {slot.Id} is not assigned to a slot group."),
                slot.Id.Value,
                slot.ParentNodeId.Value,
                slot.Address,
                slot.DisplayName,
                slot.MaterialKind.ToString(),
                slot.IsEnabled)).ToArray());
    }

    public static AutomationTopology ToTopology(
        ProjectApplicationWorkspaceScope scope,
        ProjectAutomationTopologyDocument document)
    {
        ValidateIdentity(
            scope,
            document.FormatVersion,
            ProjectAutomationTopologyDocument.CurrentFormatVersion,
            document.ResourceKind,
            ProjectAutomationTopologyDocument.Kind,
            document.ApplicationId);

        var topology = AutomationTopology.Create(
            new AutomationTopologyId(document.TopologyId),
            document.DisplayName,
            document.CreatedAtUtc);

        RestoreNodes(topology, document.Nodes ?? []);

        foreach (var capability in document.Capabilities ?? [])
        {
            if (!Version.TryParse(capability.Version, out var version))
            {
                throw new InvalidDataException(
                    $"Capability {capability.CapabilityId} has invalid version '{capability.Version}'.");
            }

            EnsureSucceeded(
                topology.AddCapability(CapabilityContract.Create(
                    new CapabilityContractId(capability.CapabilityId),
                    capability.CommandName,
                    version,
                    capability.InputSchema,
                    capability.OutputSchema,
                    TimeSpan.FromTicks(capability.TimeoutTicks),
                    ParseEnum<SafetyClass>(capability.SafetyClass, "safety class"))));
        }

        foreach (var module in document.Modules ?? [])
        {
            EnsureSucceeded(
                topology.AddModule(AutomationModule.Create(
                    new AutomationModuleId(module.ModuleId),
                    new EquipmentNodeId(module.NodeId),
                    module.ModuleKind,
                    module.DisplayName,
                    (module.RequiredCapabilityIds ?? []).Select(id => new CapabilityContractId(id)),
                    (module.ProvidedCapabilityIds ?? []).Select(id => new CapabilityContractId(id)))));
        }

        foreach (var binding in document.DriverBindings ?? [])
        {
            EnsureSucceeded(
                topology.AddDriverBinding(DriverBinding.Create(
                    new DriverBindingId(binding.BindingId),
                    new CapabilityContractId(binding.CapabilityId),
                    ParseEnum<DriverProviderKind>(binding.ProviderKind, "driver provider kind"),
                    binding.ProviderKey)));
        }

        foreach (var group in document.SlotGroups ?? [])
        {
            EnsureSucceeded(
                topology.AddSlotGroup(SlotGroup.Create(
                    new SlotGroupId(group.SlotGroupId),
                    new EquipmentNodeId(group.ParentNodeId),
                    group.DisplayName,
                    ParseEnum<SlotGroupKind>(group.Kind, "slot group kind"),
                    group.Capacity)));
        }

        foreach (var slot in document.Slots ?? [])
        {
            EnsureSucceeded(
                topology.AddSlotToGroup(
                    new SlotGroupId(slot.SlotGroupId),
                    SlotDefinition.Create(
                        new SlotDefinitionId(slot.SlotId),
                        new EquipmentNodeId(slot.ParentNodeId),
                        slot.Address,
                        slot.DisplayName,
                        ParseEnum<SlotMaterialKind>(slot.MaterialKind, "slot material kind"),
                        slot.IsEnabled)));
        }

        return topology;
    }

    public static ProjectSiteLayoutDocument FromLayout(
        ProjectApplicationWorkspaceScope scope,
        SiteLayout layout)
    {
        return new ProjectSiteLayoutDocument(
            ProjectSiteLayoutDocument.CurrentFormatVersion,
            ProjectSiteLayoutDocument.Kind,
            scope.ApplicationId,
            layout.Id.Value,
            layout.TopologyId.Value,
            layout.DisplayName,
            layout.CanvasWidth,
            layout.CanvasHeight,
            layout.Units,
            layout.Elements.Select(element => new SiteLayoutElementDocument(
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
                element.Label)).ToArray());
    }

    public static SiteLayout ToLayout(
        ProjectApplicationWorkspaceScope scope,
        ProjectSiteLayoutDocument document)
    {
        ValidateIdentity(
            scope,
            document.FormatVersion,
            ProjectSiteLayoutDocument.CurrentFormatVersion,
            document.ResourceKind,
            ProjectSiteLayoutDocument.Kind,
            document.ApplicationId);

        var layout = SiteLayout.Create(
            new SiteLayoutId(document.LayoutId),
            new AutomationTopologyId(document.TopologyId),
            document.DisplayName,
            document.CanvasWidth,
            document.CanvasHeight,
            document.Units);

        foreach (var element in document.Elements ?? [])
        {
            EnsureSucceeded(
                layout.AddElement(SiteLayoutElement.Create(
                    new LayoutElementId(element.ElementId),
                    ParseEnum<LayoutElementKind>(element.Kind, "layout element kind"),
                    new LayoutTargetReference(
                        ParseEnum<LayoutTargetKind>(element.TargetKind, "layout target kind"),
                        element.TargetId),
                    element.X,
                    element.Y,
                    element.Width,
                    element.Height,
                    element.RotationDegrees,
                    element.LayerId,
                    element.Label)));
        }

        return layout;
    }

    private static void RestoreNodes(
        AutomationTopology topology,
        IReadOnlyCollection<EquipmentNodeDocument> nodeDocuments)
    {
        var remaining = nodeDocuments.ToList();
        while (remaining.Count > 0)
        {
            var restoredInPass = 0;
            foreach (var node in remaining.ToArray())
            {
                if (node.ParentNodeId is not null
                    && topology.Nodes.All(candidate => candidate.Id.Value != node.ParentNodeId))
                {
                    continue;
                }

                EnsureSucceeded(
                    topology.AddEquipmentNode(EquipmentNode.Create(
                        new EquipmentNodeId(node.NodeId),
                        node.ParentNodeId is null ? null : new EquipmentNodeId(node.ParentNodeId),
                        ParseEnum<EquipmentNodeKind>(node.Kind, "equipment node kind"),
                        node.DisplayName)));
                remaining.Remove(node);
                restoredInPass++;
            }

            if (restoredInPass == 0)
            {
                throw new InvalidDataException(
                    $"Topology {topology.Id} contains nodes with missing or cyclic parents.");
            }
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, string field)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unsupported {field} '{value}'.");
    }

    private static void EnsureSucceeded(TopologyOperationResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidDataException($"{result.Code}: {result.Message}");
        }
    }

    private static void ValidateIdentity(
        ProjectApplicationWorkspaceScope scope,
        int actualFormatVersion,
        int currentFormatVersion,
        string actualResourceKind,
        string expectedResourceKind,
        string actualApplicationId)
    {
        if (actualFormatVersion != currentFormatVersion)
        {
            throw new InvalidDataException(
                $"Project resource format version {actualFormatVersion} is not supported.");
        }

        if (!string.Equals(actualResourceKind, expectedResourceKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Project resource kind '{actualResourceKind}' is not supported.");
        }

        if (!string.Equals(actualApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project resource belongs to application {actualApplicationId}, not {scope.ApplicationId}.");
        }
    }
}
