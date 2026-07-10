using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Application.Topologies;

internal sealed class ApplicationAutomationTopologyEditor
{
    private const string ProductionReferencePublicationImpact =
        "Topology deletion does not rewrite Production definitions. Publication fails closed when a Production definition still references a deleted System, SlotGroup, or Slot.";

    private readonly ProjectApplicationWorkspaceScope _scope;
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectSiteLayoutRepository _layoutRepository;
    private readonly IClock _clock;

    public ApplicationAutomationTopologyEditor(
        ProjectApplicationWorkspaceScope scope,
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectSiteLayoutRepository layoutRepository,
        IClock clock)
    {
        _scope = scope;
        _topologyRepository = topologyRepository;
        _layoutRepository = layoutRepository;
        _clock = clock;
    }

    public async Task<Result<AutomationTopologyDetails>> CreateAsync(
        CreateAutomationTopologyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TopologyId))
        {
            return Result.Failure<AutomationTopologyDetails>(Required("Topology.TopologyIdRequired", "TopologyId"));
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Result.Failure<AutomationTopologyDetails>(Required("Topology.DisplayNameRequired", "DisplayName"));
        }

        try
        {
            var topologyId = new AutomationTopologyId(request.TopologyId);
            var existing = await _topologyRepository
                .GetByIdAsync(_scope, topologyId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return Result.Failure<AutomationTopologyDetails>(ApplicationError.Conflict(
                    "Topology.TopologyAlreadyExists",
                    $"Automation topology {topologyId} already exists."));
            }

            var topology = AutomationTopology.Create(
                topologyId,
                request.DisplayName,
                _clock.UtcNow);

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationTopologyDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<AutomationTopologyDetails>> GetByIdAsync(
        string topologyId,
        CancellationToken cancellationToken = default)
    {
        var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);

        return topology is null
            ? Result.Failure<AutomationTopologyDetails>(TopologyNotFound(topologyId))
            : Result.Success(AutomationTopologyMapper.ToDetails(topology));
    }

    public async Task<Result<IReadOnlyCollection<AutomationTopologySummary>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var topologies = await _topologyRepository.ListAsync(_scope, cancellationToken).ConfigureAwait(false);
        var summaries = topologies
            .OrderBy(topology => topology.Id.Value, StringComparer.Ordinal)
            .Select(AutomationTopologyMapper.ToSummary)
            .ToArray();

        return Result.Success<IReadOnlyCollection<AutomationTopologySummary>>(summaries);
    }

    public async Task<Result<AutomationTopologyDetails>> AddSystemAsync(
        string topologyId,
        AddAutomationSystemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDefinedEnum<SystemKind>(request.Kind, out var kind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSystemKind",
                $"Automation system {request.SystemId} has unsupported kind {request.Kind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddSystem(AutomationSystem.Create(
                new AutomationSystemId(request.SystemId),
                string.IsNullOrWhiteSpace(request.ParentSystemId)
                    ? null
                    : new AutomationSystemId(request.ParentSystemId),
                kind,
                request.SystemType,
                request.DisplayName,
                request.RequiredCapabilityIds.Select(id => new CapabilityContractId(id)),
                request.ProvidedCapabilityIds.Select(id => new CapabilityContractId(id)),
                request.Metadata)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> UpdateSystemAsync(
        string topologyId,
        string systemId,
        UpdateAutomationSystemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SystemType is null && request.DisplayName is null && request.Metadata is null)
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.SystemUpdateRequired",
                "At least one of SystemType, DisplayName, or Metadata must be supplied."));
        }

        try
        {
            var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TopologyNotFound(topologyId));
            }

            var id = new AutomationSystemId(systemId);
            var system = topology.FindSystem(id);
            if (system is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TargetNotFound(
                    "Topology.SystemNotFound",
                    $"Automation system {systemId} was not found in topology {topologyId}."));
            }

            var result = topology.UpdateSystem(
                id,
                request.SystemType ?? system.SystemType,
                request.DisplayName ?? system.DisplayName,
                request.Metadata ?? system.Metadata);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationTopologyDetails>(ToApplicationError(result));
            }

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);
            return Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationTopologyDetails>(InvalidInput(exception));
        }
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSystemAsync(
        string topologyId,
        string systemId,
        CancellationToken cancellationToken = default)
    {
        return DeleteTargetAsync(
            topologyId,
            topology => CreateSystemDeletionTargets(topology, new AutomationSystemId(systemId)),
            topology => topology.RemoveSystem(new AutomationSystemId(systemId)),
            "Topology.SystemNotFound",
            $"Automation system {systemId} was not found in topology {topologyId}.",
            cancellationToken);
    }

    public async Task<Result<AutomationTopologyDetails>> AddCapabilityAsync(
        string topologyId,
        AddCapabilityContractRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Version.TryParse(request.Version, out var version))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidCapabilityVersion",
                $"Capability {request.CapabilityId} has unsupported version {request.Version}."));
        }

        if (!TryParseDefinedEnum<SafetyClass>(request.SafetyClass, out var safetyClass))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSafetyClass",
                $"Capability {request.CapabilityId} has unsupported safety class {request.SafetyClass}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddCapability(CapabilityContract.Create(
                new CapabilityContractId(request.CapabilityId),
                request.CommandName,
                version,
                request.InputSchema,
                request.OutputSchema,
                TimeSpan.FromSeconds(request.TimeoutSeconds),
                safetyClass)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> AddDriverBindingAsync(
        string topologyId,
        AddDriverBindingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDefinedEnum<DriverProviderKind>(request.ProviderKind, out var providerKind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidDriverProviderKind",
                $"Driver binding {request.BindingId} has unsupported provider kind {request.ProviderKind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddDriverBinding(DriverBinding.Create(
                new DriverBindingId(request.BindingId),
                new CapabilityContractId(request.CapabilityId),
                providerKind,
                request.ProviderKey)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> AddSlotGroupAsync(
        string topologyId,
        AddSlotGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDefinedEnum<SlotGroupKind>(request.Kind, out var kind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSlotGroupKind",
                $"Slot group {request.SlotGroupId} has unsupported kind {request.Kind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddSlotGroup(SlotGroup.Create(
                new SlotGroupId(request.SlotGroupId),
                new AutomationSystemId(request.ParentSystemId),
                request.DisplayName,
                kind,
                request.Capacity)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> UpdateSlotGroupAsync(
        string topologyId,
        string slotGroupId,
        UpdateSlotGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DisplayName is null && request.Kind is null && request.Capacity is null)
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.SlotGroupUpdateRequired",
                "At least one of DisplayName, Kind, or Capacity must be supplied."));
        }

        try
        {
            var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TopologyNotFound(topologyId));
            }

            var id = new SlotGroupId(slotGroupId);
            var group = topology.FindSlotGroup(id);
            if (group is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TargetNotFound(
                    "Topology.SlotGroupNotFound",
                    $"Slot group {slotGroupId} was not found in topology {topologyId}."));
            }

            var kind = group.Kind;
            if (request.Kind is not null && !TryParseDefinedEnum(request.Kind, out kind))
            {
                return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                    "Topology.InvalidSlotGroupKind",
                    $"Slot group {slotGroupId} has unsupported kind {request.Kind}."));
            }

            var result = topology.UpdateSlotGroup(
                id,
                request.DisplayName ?? group.DisplayName,
                kind,
                request.Capacity ?? group.Capacity);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationTopologyDetails>(ToApplicationError(result));
            }

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);
            return Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationTopologyDetails>(InvalidInput(exception));
        }
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSlotGroupAsync(
        string topologyId,
        string slotGroupId,
        CancellationToken cancellationToken = default)
    {
        return DeleteTargetAsync(
            topologyId,
            topology => CreateSlotGroupDeletionTargets(topology, new SlotGroupId(slotGroupId)),
            topology => topology.RemoveSlotGroup(new SlotGroupId(slotGroupId)),
            "Topology.SlotGroupNotFound",
            $"Slot group {slotGroupId} was not found in topology {topologyId}.",
            cancellationToken);
    }

    public async Task<Result<AutomationTopologyDetails>> AddSlotAsync(
        string topologyId,
        AddSlotDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDefinedEnum<SlotMaterialKind>(request.MaterialKind, out var materialKind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSlotMaterialKind",
                $"Slot {request.SlotId} has unsupported material kind {request.MaterialKind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddSlot(SlotDefinition.Create(
                new SlotDefinitionId(request.SlotId),
                new SlotGroupId(request.SlotGroupId),
                new AutomationSystemId(request.ParentSystemId),
                request.Address,
                request.DisplayName,
                materialKind,
                request.IsEnabled)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> UpdateSlotAsync(
        string topologyId,
        string slotId,
        UpdateSlotDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Address is null
            && request.DisplayName is null
            && request.MaterialKind is null
            && request.IsEnabled is null)
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.SlotUpdateRequired",
                "At least one of Address, DisplayName, MaterialKind, or IsEnabled must be supplied."));
        }

        try
        {
            var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TopologyNotFound(topologyId));
            }

            var id = new SlotDefinitionId(slotId);
            var slot = topology.FindSlot(id);
            if (slot is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TargetNotFound(
                    "Topology.SlotNotFound",
                    $"Slot {slotId} was not found in topology {topologyId}."));
            }

            var materialKind = slot.MaterialKind;
            if (request.MaterialKind is not null
                && !TryParseDefinedEnum(request.MaterialKind, out materialKind))
            {
                return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                    "Topology.InvalidSlotMaterialKind",
                    $"Slot {slotId} has unsupported material kind {request.MaterialKind}."));
            }

            var result = topology.UpdateSlot(
                id,
                request.Address ?? slot.Address,
                request.DisplayName ?? slot.DisplayName,
                materialKind,
                request.IsEnabled ?? slot.IsEnabled);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationTopologyDetails>(ToApplicationError(result));
            }

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);
            return Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationTopologyDetails>(InvalidInput(exception));
        }
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSlotAsync(
        string topologyId,
        string slotId,
        CancellationToken cancellationToken = default)
    {
        return DeleteTargetAsync(
            topologyId,
            topology => topology.FindSlot(new SlotDefinitionId(slotId)) is null
                ? null
                : new HashSet<LayoutTargetReference>
                {
                    new(LayoutTargetKind.Slot, slotId)
                },
            topology => topology.RemoveSlot(new SlotDefinitionId(slotId)),
            "Topology.SlotNotFound",
            $"Slot {slotId} was not found in topology {topologyId}.",
            cancellationToken);
    }

    public async Task<Result<SiteLayoutDetails>> CreateLayoutAsync(
        CreateSiteLayoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.LayoutId))
        {
            return Result.Failure<SiteLayoutDetails>(Required("Topology.LayoutIdRequired", "LayoutId"));
        }

        if (string.IsNullOrWhiteSpace(request.TopologyId))
        {
            return Result.Failure<SiteLayoutDetails>(Required("Topology.TopologyIdRequired", "TopologyId"));
        }

        try
        {
            var topology = await FindTopologyAsync(request.TopologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<SiteLayoutDetails>(TopologyNotFound(request.TopologyId));
            }

            var layoutId = new SiteLayoutId(request.LayoutId);
            var existing = await _layoutRepository.GetByIdAsync(_scope, layoutId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return Result.Failure<SiteLayoutDetails>(ApplicationError.Conflict(
                    "Topology.LayoutAlreadyExists",
                    $"Site layout {layoutId} already exists."));
            }

            var layout = SiteLayout.Create(
                layoutId,
                topology.Id,
                request.DisplayName,
                request.CanvasWidth,
                request.CanvasHeight,
                request.Units);

            await _layoutRepository.SaveAsync(_scope, layout, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<SiteLayoutDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<SiteLayoutDetails>> GetLayoutByIdAsync(
        string layoutId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layoutId))
        {
            return Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId));
        }

        var layout = await _layoutRepository
            .GetByIdAsync(_scope, new SiteLayoutId(layoutId), cancellationToken)
            .ConfigureAwait(false);

        return layout is null
            ? Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId))
            : Result.Success(AutomationTopologyMapper.ToDetails(layout));
    }

    public async Task<Result<SiteLayoutDetails>> AddLayoutElementAsync(
        string layoutId,
        AddSiteLayoutElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDefinedEnum<LayoutElementKind>(request.Kind, out var elementKind))
        {
            return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                "Topology.InvalidLayoutElementKind",
                $"Layout element {request.ElementId} has unsupported kind {request.Kind}."));
        }

        if (!TryParseDefinedEnum<LayoutTargetKind>(request.TargetKind, out var targetKind))
        {
            return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                "Topology.InvalidLayoutTargetKind",
                $"Layout element {request.ElementId} has unsupported target kind {request.TargetKind}."));
        }

        try
        {
            var layout = await _layoutRepository
                .GetByIdAsync(_scope, new SiteLayoutId(layoutId), cancellationToken)
                .ConfigureAwait(false);
            if (layout is null)
            {
                return Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId));
            }

            var topology = await _topologyRepository.GetByIdAsync(_scope, layout.TopologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<SiteLayoutDetails>(TopologyNotFound(layout.TopologyId.Value));
            }

            var parentElement = request.ParentElementId is null
                ? null
                : layout.Elements.SingleOrDefault(candidate => candidate.Id.Value == request.ParentElementId);
            var topologyRelationship = ValidateLayoutTopologyRelationship(
                topology,
                elementKind,
                targetKind,
                request.TargetId,
                parentElement);
            if (!topologyRelationship.Succeeded)
            {
                return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                    topologyRelationship.Code,
                    topologyRelationship.Message));
            }

            var addResult = layout.AddElement(SiteLayoutElement.Create(
                new LayoutElementId(request.ElementId),
                elementKind,
                new LayoutTargetReference(targetKind, request.TargetId),
                request.ParentElementId is null ? null : new LayoutElementId(request.ParentElementId),
                request.X,
                request.Y,
                request.Width,
                request.Height,
                request.RotationDegrees,
                request.ZIndex,
                request.Style));
            if (!addResult.Succeeded)
            {
                return Result.Failure<SiteLayoutDetails>(ToConflict(addResult));
            }

            await _layoutRepository.SaveAsync(_scope, layout, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<SiteLayoutDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<SiteLayoutDetails>> UpdateLayoutElementGeometryAsync(
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementGeometryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(elementId))
        {
            return Result.Failure<SiteLayoutDetails>(LayoutElementNotFound(elementId));
        }

        try
        {
            var layout = await _layoutRepository
                .GetByIdAsync(_scope, new SiteLayoutId(layoutId), cancellationToken)
                .ConfigureAwait(false);
            if (layout is null)
            {
                return Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId));
            }

            var updateResult = layout.UpdateElementGeometry(
                new LayoutElementId(elementId),
                request.X,
                request.Y,
                request.Width,
                request.Height,
                request.RotationDegrees);
            if (!updateResult.Succeeded)
            {
                return Result.Failure<SiteLayoutDetails>(ToApplicationError(updateResult));
            }

            await _layoutRepository.SaveAsync(_scope, layout, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<SiteLayoutDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<SiteLayoutDetails>> UpdateLayoutElementPresentationAsync(
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementPresentationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var layout = await _layoutRepository
                .GetByIdAsync(_scope, new SiteLayoutId(layoutId), cancellationToken)
                .ConfigureAwait(false);
            if (layout is null)
            {
                return Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId));
            }

            var updateResult = layout.UpdateElementPresentation(
                new LayoutElementId(elementId),
                request.ZIndex,
                request.Style);
            if (!updateResult.Succeeded)
            {
                return Result.Failure<SiteLayoutDetails>(ToApplicationError(updateResult));
            }

            await _layoutRepository.SaveAsync(_scope, layout, cancellationToken).ConfigureAwait(false);
            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<SiteLayoutDetails>(InvalidInput(exception));
        }
    }

    private async Task<Result<TopologyTargetDeletionDetails>> DeleteTargetAsync(
        string topologyId,
        Func<AutomationTopology, IReadOnlySet<LayoutTargetReference>?> createTargets,
        Func<AutomationTopology, TopologyOperationResult> removeTarget,
        string notFoundCode,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<TopologyTargetDeletionDetails>(TopologyNotFound(topologyId));
            }

            var targets = createTargets(topology);
            if (targets is null)
            {
                return Result.Failure<TopologyTargetDeletionDetails>(TargetNotFound(notFoundCode, notFoundMessage));
            }

            var updatedLayoutCount = 0;
            var removedLayoutElementCount = 0;
            var layouts = await _layoutRepository
                .ListByTopologyAsync(_scope, topology.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var layout in layouts)
            {
                var removed = layout.RemoveElementsByTargets(targets);
                if (removed == 0)
                {
                    continue;
                }

                await _layoutRepository.SaveAsync(_scope, layout, cancellationToken).ConfigureAwait(false);
                updatedLayoutCount++;
                removedLayoutElementCount += removed;
            }

            var result = removeTarget(topology);
            if (!result.Succeeded)
            {
                return Result.Failure<TopologyTargetDeletionDetails>(ToApplicationError(result));
            }

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);
            return Result.Success(new TopologyTargetDeletionDetails(
                AutomationTopologyMapper.ToDetails(topology),
                updatedLayoutCount,
                removedLayoutElementCount,
                ProductionReferencePublicationImpact));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<TopologyTargetDeletionDetails>(InvalidInput(exception));
        }
    }

    private static HashSet<LayoutTargetReference>? CreateSystemDeletionTargets(
        AutomationTopology topology,
        AutomationSystemId systemId)
    {
        if (topology.FindSystem(systemId) is null)
        {
            return null;
        }

        var systemIds = new HashSet<AutomationSystemId> { systemId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in topology.Systems.Where(candidate => candidate.ParentSystemId is not null
                         && systemIds.Contains(candidate.ParentSystemId)))
            {
                changed |= systemIds.Add(child.Id);
            }
        }

        var groupIds = topology.SlotGroups
            .Where(group => systemIds.Contains(group.ParentSystemId))
            .Select(group => group.Id)
            .ToHashSet();
        var targets = new HashSet<LayoutTargetReference>(
            systemIds.Select(id => new LayoutTargetReference(LayoutTargetKind.System, id.Value)));
        targets.UnionWith(groupIds.Select(id => new LayoutTargetReference(LayoutTargetKind.SlotGroup, id.Value)));
        targets.UnionWith(topology.Slots
            .Where(slot => systemIds.Contains(slot.ParentSystemId) || groupIds.Contains(slot.SlotGroupId))
            .Select(slot => new LayoutTargetReference(LayoutTargetKind.Slot, slot.Id.Value)));
        return targets;
    }

    private static HashSet<LayoutTargetReference>? CreateSlotGroupDeletionTargets(
        AutomationTopology topology,
        SlotGroupId slotGroupId)
    {
        if (topology.FindSlotGroup(slotGroupId) is null)
        {
            return null;
        }

        var targets = new HashSet<LayoutTargetReference>
        {
            new(LayoutTargetKind.SlotGroup, slotGroupId.Value)
        };
        targets.UnionWith(topology.Slots
            .Where(slot => slot.SlotGroupId == slotGroupId)
            .Select(slot => new LayoutTargetReference(LayoutTargetKind.Slot, slot.Id.Value)));
        return targets;
    }

    private async Task<Result<AutomationTopologyDetails>> MutateTopologyAsync(
        string topologyId,
        Func<AutomationTopology, TopologyOperationResult> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            var topology = await FindTopologyAsync(topologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<AutomationTopologyDetails>(TopologyNotFound(topologyId));
            }

            var result = mutate(topology);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationTopologyDetails>(ToConflict(result));
            }

            await _topologyRepository.SaveAsync(_scope, topology, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationTopologyDetails>(InvalidInput(exception));
        }
    }

    private async Task<AutomationTopology?> FindTopologyAsync(
        string topologyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topologyId))
        {
            return null;
        }

        return await _topologyRepository
            .GetByIdAsync(_scope, new AutomationTopologyId(topologyId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static TopologyOperationResult ValidateLayoutTopologyRelationship(
        AutomationTopology topology,
        LayoutElementKind elementKind,
        LayoutTargetKind targetKind,
        string targetId,
        SiteLayoutElement? parentElement)
    {
        if (elementKind == LayoutElementKind.SystemShape && targetKind == LayoutTargetKind.System)
        {
            var system = topology.FindSystem(new AutomationSystemId(targetId));
            if (system is null)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutTargetMissing",
                    $"System target {targetId} does not exist in topology {topology.Id}.");
            }

            if (parentElement is null)
            {
                return system.ParentSystemId is null
                    ? TopologyOperationResult.Accepted()
                    : TopologyOperationResult.Rejected(
                        "Topology.LayoutRootSystemHasParent",
                        $"System {targetId} has a parent and cannot be placed at the canvas root.");
            }

            var parentSystem = parentElement.Target.Kind == LayoutTargetKind.System
                ? topology.FindSystem(new AutomationSystemId(parentElement.Target.TargetId))
                : null;
            if (parentSystem is not StationSystem)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutChildRequiresStation",
                    $"Child system {targetId} must be placed inside a Station SystemShape.");
            }

            return system.ParentSystemId == parentSystem.Id
                ? TopologyOperationResult.Accepted()
                : TopologyOperationResult.Rejected(
                    "Topology.LayoutSystemParentMismatch",
                    $"System {targetId} does not belong to station {parentSystem.Id}.");
        }

        if (elementKind == LayoutElementKind.GroupRegion && targetKind == LayoutTargetKind.SlotGroup)
        {
            var group = topology.FindSlotGroup(new SlotGroupId(targetId));
            if (group is null)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutTargetMissing",
                    $"Slot group target {targetId} does not exist in topology {topology.Id}.");
            }

            var parentSystem = parentElement?.Target.Kind == LayoutTargetKind.System
                ? topology.FindSystem(new AutomationSystemId(parentElement.Target.TargetId))
                : null;
            return parentSystem is StationSystem && group.ParentSystemId == parentSystem.Id
                ? TopologyOperationResult.Accepted()
                : TopologyOperationResult.Rejected(
                    "Topology.LayoutGroupParentMismatch",
                    $"Slot group {targetId} must be placed inside its Station SystemShape.");
        }

        if (elementKind == LayoutElementKind.SlotShape && targetKind == LayoutTargetKind.Slot)
        {
            var slot = topology.FindSlot(new SlotDefinitionId(targetId));
            if (slot is null)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutTargetMissing",
                    $"Slot target {targetId} does not exist in topology {topology.Id}.");
            }

            var parentGroup = parentElement?.Target.Kind == LayoutTargetKind.SlotGroup
                ? topology.FindSlotGroup(new SlotGroupId(parentElement.Target.TargetId))
                : null;
            return parentGroup is not null
                && slot.SlotGroupId == parentGroup.Id
                && slot.ParentSystemId == parentGroup.ParentSystemId
                    ? TopologyOperationResult.Accepted()
                    : TopologyOperationResult.Rejected(
                        "Topology.LayoutSlotParentMismatch",
                        $"Slot {targetId} must be placed inside its own SlotGroup region.");
        }

        return TopologyOperationResult.Rejected(
            "Topology.LayoutKindTargetMismatch",
            $"Layout element kind {elementKind} cannot represent target kind {targetKind}.");
    }

    private static bool TryParseDefinedEnum<TEnum>(string value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        return CanonicalEnumToken.TryParse(value, out parsed);
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static ApplicationError ToConflict(TopologyOperationResult result)
    {
        return ApplicationError.Conflict(result.Code, result.Message);
    }

    private static ApplicationError ToApplicationError(TopologyOperationResult result)
    {
        return result.Code switch
        {
            "Topology.LayoutElementNotFound" => ApplicationError.NotFound(result.Code, result.Message),
            "Topology.SystemNotFound" => ApplicationError.NotFound(result.Code, result.Message),
            "Topology.SlotGroupNotFound" => ApplicationError.NotFound(result.Code, result.Message),
            "Topology.SlotNotFound" => ApplicationError.NotFound(result.Code, result.Message),
            "Topology.LayoutElementOutOfBounds" => ApplicationError.Validation(result.Code, result.Message),
            "Topology.LayoutGeometryInvalid" => ApplicationError.Validation(result.Code, result.Message),
            "Topology.LayoutChildOutOfBounds" => ApplicationError.Validation(result.Code, result.Message),
            "Topology.LayoutPresentationRequired" => ApplicationError.Validation(result.Code, result.Message),
            "Topology.SlotGroupCapacityInvalid" => ApplicationError.Validation(result.Code, result.Message),
            _ => ToConflict(result)
        };
    }

    private static ApplicationError InvalidInput(ArgumentException exception)
    {
        return ApplicationError.Validation(
            "Topology.InvalidTopologyInput",
            exception.Message);
    }

    private static ApplicationError TopologyNotFound(string topologyId)
    {
        return ApplicationError.NotFound(
            "Topology.TopologyNotFound",
            $"Automation topology {topologyId} was not found.");
    }

    private static ApplicationError LayoutNotFound(string layoutId)
    {
        return ApplicationError.NotFound(
            "Topology.LayoutNotFound",
            $"Site layout {layoutId} was not found.");
    }

    private static ApplicationError LayoutElementNotFound(string? elementId)
    {
        return ApplicationError.NotFound(
            "Topology.LayoutElementNotFound",
            $"Site layout element {elementId} was not found.");
    }

    private static ApplicationError TargetNotFound(string code, string message)
    {
        return ApplicationError.NotFound(code, message);
    }
}
