using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal static class ProjectTopologyResourceSnapshotMapper
{
    public static ProjectAutomationTopologyDocument FromTopology(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopology topology)
    {
        return new ProjectAutomationTopologyDocument(
            ProjectAutomationTopologyDocument.CurrentSchemaVersion,
            ProjectAutomationTopologyDocument.Kind,
            scope.ApplicationId,
            topology.Id.Value,
            topology.DisplayName,
            topology.CreatedAtUtc,
            topology.Systems.Select(system => new AutomationSystemDocument(
                system.Id.Value,
                system.ParentSystemId?.Value,
                system.Kind.ToString(),
                system.SystemType,
                system.DisplayName,
                system.RequiredCapabilities.Select(id => id.Value).ToArray(),
                system.ProvidedCapabilities.Select(id => id.Value).ToArray(),
                ToSortedDictionary(system.Metadata))).ToArray(),
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
                binding.OwnerSystemId.Value,
                binding.CapabilityId.Value,
                binding.ProviderKind.ToString(),
                binding.ProviderKey)).ToArray(),
            topology.SlotGroups.Select(group => new SlotGroupDocument(
                group.Id.Value,
                group.ParentSystemId.Value,
                group.DisplayName,
                group.Kind.ToString(),
                group.Capacity)).ToArray(),
            topology.Slots.Select(slot => new SlotDefinitionDocument(
                slot.Id.Value,
                slot.SlotGroupId.Value,
                slot.ParentSystemId.Value,
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
            document.SchemaVersion,
            ProjectAutomationTopologyDocument.CurrentSchemaVersion,
            document.ResourceKind,
            ProjectAutomationTopologyDocument.Kind,
            document.ApplicationId);

        var systems = Required(document.Systems, nameof(document.Systems));
        var capabilities = Required(document.Capabilities, nameof(document.Capabilities));
        var bindings = Required(document.DriverBindings, nameof(document.DriverBindings));
        var groups = Required(document.SlotGroups, nameof(document.SlotGroups));
        var slots = Required(document.Slots, nameof(document.Slots));

        var topology = AutomationTopology.Create(
            new AutomationTopologyId(document.TopologyId),
            document.DisplayName,
            document.CreatedAtUtc);

        foreach (var capability in capabilities)
        {
            if (!Version.TryParse(capability.Version, out var version))
            {
                throw new InvalidDataException(
                    $"Capability {capability.CapabilityId} has invalid version '{capability.Version}'.");
            }

            EnsureSucceeded(topology.AddCapability(CapabilityContract.Create(
                new CapabilityContractId(capability.CapabilityId),
                capability.CommandName,
                version,
                capability.InputSchema,
                capability.OutputSchema,
                TimeSpan.FromTicks(capability.TimeoutTicks),
                ParseEnum<SafetyClass>(capability.SafetyClass, "safety class"))));
        }

        RestoreSystems(topology, systems);

        foreach (var binding in bindings)
        {
            EnsureSucceeded(topology.AddDriverBinding(DriverBinding.Create(
                new DriverBindingId(binding.BindingId),
                new AutomationSystemId(binding.OwnerSystemId),
                new CapabilityContractId(binding.CapabilityId),
                ParseEnum<DriverProviderKind>(binding.ProviderKind, "driver provider kind"),
                binding.ProviderKey)));
        }

        foreach (var group in groups)
        {
            EnsureSucceeded(topology.AddSlotGroup(SlotGroup.Create(
                new SlotGroupId(group.SlotGroupId),
                new AutomationSystemId(group.ParentSystemId),
                group.DisplayName,
                ParseEnum<SlotGroupKind>(group.Kind, "slot group kind"),
                group.Capacity)));
        }

        foreach (var slot in slots)
        {
            EnsureSucceeded(topology.AddSlot(SlotDefinition.Create(
                new SlotDefinitionId(slot.SlotId),
                new SlotGroupId(slot.SlotGroupId),
                new AutomationSystemId(slot.ParentSystemId),
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
            ProjectSiteLayoutDocument.CurrentSchemaVersion,
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
                new LayoutTargetDocument(element.Target.Kind.ToString(), element.Target.TargetId),
                element.ParentElementId?.Value,
                element.X,
                element.Y,
                element.Width,
                element.Height,
                element.RotationDegrees,
                element.ZIndex,
                ToSortedDictionary(element.Style))).ToArray());
    }

    public static SiteLayout ToLayout(
        ProjectApplicationWorkspaceScope scope,
        ProjectSiteLayoutDocument document)
    {
        ValidateIdentity(
            scope,
            document.SchemaVersion,
            ProjectSiteLayoutDocument.CurrentSchemaVersion,
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

        var remaining = Required(document.Elements, nameof(document.Elements)).ToList();
        while (remaining.Count > 0)
        {
            var restored = 0;
            foreach (var element in remaining.ToArray())
            {
                if (element.ParentElementId is not null
                    && layout.Elements.All(candidate => candidate.Id.Value != element.ParentElementId))
                {
                    continue;
                }

                if (element.Target is null)
                {
                    throw new InvalidDataException($"Layout element {element.ElementId} has no target.");
                }

                EnsureSucceeded(layout.AddElement(SiteLayoutElement.Create(
                    new LayoutElementId(element.ElementId),
                    ParseEnum<LayoutElementKind>(element.Kind, "layout element kind"),
                    new LayoutTargetReference(
                        ParseEnum<LayoutTargetKind>(element.Target.Kind, "layout target kind"),
                        element.Target.TargetId),
                    element.ParentElementId is null ? null : new LayoutElementId(element.ParentElementId),
                    element.X,
                    element.Y,
                    element.Width,
                    element.Height,
                    element.RotationDegrees,
                    element.ZIndex,
                    Required(element.Style, $"{nameof(element.Style)} for {element.ElementId}"))));
                remaining.Remove(element);
                restored++;
            }

            if (restored == 0)
            {
                throw new InvalidDataException(
                    $"Layout {layout.Id} contains elements with missing or cyclic parents.");
            }
        }

        return layout;
    }

    private static void RestoreSystems(
        AutomationTopology topology,
        IReadOnlyCollection<AutomationSystemDocument> documents)
    {
        var remaining = documents.ToList();
        while (remaining.Count > 0)
        {
            var restored = 0;
            foreach (var system in remaining.ToArray())
            {
                if (system.ParentSystemId is not null
                    && topology.Systems.All(candidate => candidate.Id.Value != system.ParentSystemId))
                {
                    continue;
                }

                EnsureSucceeded(topology.AddSystem(AutomationSystem.Create(
                    new AutomationSystemId(system.SystemId),
                    system.ParentSystemId is null ? null : new AutomationSystemId(system.ParentSystemId),
                    ParseEnum<SystemKind>(system.Kind, "system kind"),
                    system.SystemType,
                    system.DisplayName,
                    Required(system.RequiredCapabilityIds, $"required capabilities for {system.SystemId}")
                        .Select(id => new CapabilityContractId(id)),
                    Required(system.ProvidedCapabilityIds, $"provided capabilities for {system.SystemId}")
                        .Select(id => new CapabilityContractId(id)),
                    Required(system.Metadata, $"metadata for {system.SystemId}"))));
                remaining.Remove(system);
                restored++;
            }

            if (restored == 0)
            {
                throw new InvalidDataException(
                    $"Topology {topology.Id} contains systems with missing or cyclic parents.");
            }
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, string field)
        where TEnum : struct, Enum
    {
        return CanonicalEnumToken.TryParse<TEnum>(value, out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Unsupported {field} '{value}'. Expected an exact, case-sensitive " +
                $"{typeof(TEnum).Name} token: {CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }

    private static T Required<T>(T? value, string field)
        where T : class
    {
        return value ?? throw new InvalidDataException($"Project resource field '{field}' is required.");
    }

    private static SortedDictionary<string, string> ToSortedDictionary(
        IReadOnlyDictionary<string, string> source)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            result.Add(pair.Key, pair.Value);
        }

        return result;
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
        string actualSchemaVersion,
        string expectedSchemaVersion,
        string actualResourceKind,
        string expectedResourceKind,
        string actualApplicationId)
    {
        if (!string.Equals(actualSchemaVersion, expectedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project resource schema '{actualSchemaVersion}' is not supported; expected '{expectedSchemaVersion}'.");
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
