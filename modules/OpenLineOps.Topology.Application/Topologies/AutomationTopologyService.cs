using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Operations;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Application.Topologies;

public sealed class AutomationTopologyService : IAutomationTopologyService
{
    private readonly IAutomationTopologyRepository _topologyRepository;
    private readonly ISiteLayoutRepository _layoutRepository;
    private readonly IClock _clock;

    public AutomationTopologyService(
        IAutomationTopologyRepository topologyRepository,
        ISiteLayoutRepository layoutRepository,
        IClock clock)
    {
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
                .GetByIdAsync(topologyId, cancellationToken)
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

            await _topologyRepository.SaveAsync(topology, cancellationToken).ConfigureAwait(false);

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
        var topologies = await _topologyRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = topologies
            .OrderBy(topology => topology.Id.Value, StringComparer.Ordinal)
            .Select(AutomationTopologyMapper.ToSummary)
            .ToArray();

        return Result.Success<IReadOnlyCollection<AutomationTopologySummary>>(summaries);
    }

    public async Task<Result<AutomationTopologyDetails>> AddEquipmentNodeAsync(
        string topologyId,
        AddEquipmentNodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<EquipmentNodeKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidEquipmentNodeKind",
                $"Equipment node {request.NodeId} has unsupported kind {request.Kind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddEquipmentNode(EquipmentNode.Create(
                new EquipmentNodeId(request.NodeId),
                string.IsNullOrWhiteSpace(request.ParentNodeId) ? null : new EquipmentNodeId(request.ParentNodeId),
                kind,
                request.DisplayName)),
            cancellationToken).ConfigureAwait(false);
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

        if (!Enum.TryParse<SafetyClass>(request.SafetyClass, ignoreCase: true, out var safetyClass))
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

    public async Task<Result<AutomationTopologyDetails>> AddModuleAsync(
        string topologyId,
        AddAutomationModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddModule(AutomationModule.Create(
                new AutomationModuleId(request.ModuleId),
                new EquipmentNodeId(request.NodeId),
                request.ModuleKind,
                request.DisplayName,
                (request.RequiredCapabilityIds ?? []).Select(capabilityId => new CapabilityContractId(capabilityId)),
                (request.ProvidedCapabilityIds ?? []).Select(capabilityId => new CapabilityContractId(capabilityId)))),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> AddDriverBindingAsync(
        string topologyId,
        AddDriverBindingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<DriverProviderKind>(request.ProviderKind, ignoreCase: true, out var providerKind))
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

        if (!Enum.TryParse<SlotGroupKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSlotGroupKind",
                $"Slot group {request.SlotGroupId} has unsupported kind {request.Kind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddSlotGroup(SlotGroup.Create(
                new SlotGroupId(request.SlotGroupId),
                new EquipmentNodeId(request.ParentNodeId),
                request.DisplayName,
                kind,
                request.Capacity)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationTopologyDetails>> AddSlotAsync(
        string topologyId,
        AddSlotDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<SlotMaterialKind>(request.MaterialKind, ignoreCase: true, out var materialKind))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Validation(
                "Topology.InvalidSlotMaterialKind",
                $"Slot {request.SlotId} has unsupported material kind {request.MaterialKind}."));
        }

        return await MutateTopologyAsync(
            topologyId,
            topology => topology.AddSlotToGroup(
                new SlotGroupId(request.SlotGroupId),
                SlotDefinition.Create(
                    new SlotDefinitionId(request.SlotId),
                    new EquipmentNodeId(request.ParentNodeId),
                    request.Address,
                    request.DisplayName,
                    materialKind,
                    request.IsEnabled)),
            cancellationToken).ConfigureAwait(false);
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
            var existing = await _layoutRepository.GetByIdAsync(layoutId, cancellationToken).ConfigureAwait(false);
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

            await _layoutRepository.SaveAsync(layout, cancellationToken).ConfigureAwait(false);

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
            .GetByIdAsync(new SiteLayoutId(layoutId), cancellationToken)
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

        if (!Enum.TryParse<LayoutElementKind>(request.Kind, ignoreCase: true, out var elementKind))
        {
            return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                "Topology.InvalidLayoutElementKind",
                $"Layout element {request.ElementId} has unsupported kind {request.Kind}."));
        }

        if (!Enum.TryParse<LayoutTargetKind>(request.TargetKind, ignoreCase: true, out var targetKind))
        {
            return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                "Topology.InvalidLayoutTargetKind",
                $"Layout element {request.ElementId} has unsupported target kind {request.TargetKind}."));
        }

        try
        {
            var layout = await _layoutRepository
                .GetByIdAsync(new SiteLayoutId(layoutId), cancellationToken)
                .ConfigureAwait(false);
            if (layout is null)
            {
                return Result.Failure<SiteLayoutDetails>(LayoutNotFound(layoutId));
            }

            var topology = await _topologyRepository.GetByIdAsync(layout.TopologyId, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                return Result.Failure<SiteLayoutDetails>(TopologyNotFound(layout.TopologyId.Value));
            }

            if (RequiresTopologyTarget(targetKind) && !topology.HasLayoutTarget(request.TargetId))
            {
                return Result.Failure<SiteLayoutDetails>(ApplicationError.Validation(
                    "Topology.LayoutTargetMissing",
                    $"Layout target {request.TargetId} does not exist in topology {topology.Id}."));
            }

            var addResult = layout.AddElement(SiteLayoutElement.Create(
                new LayoutElementId(request.ElementId),
                elementKind,
                new LayoutTargetReference(targetKind, request.TargetId),
                request.X,
                request.Y,
                request.Width,
                request.Height,
                request.RotationDegrees,
                request.LayerId,
                request.Label));
            if (!addResult.Succeeded)
            {
                return Result.Failure<SiteLayoutDetails>(ToConflict(addResult));
            }

            await _layoutRepository.SaveAsync(layout, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<SiteLayoutDetails>(InvalidInput(exception));
        }
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

            await _topologyRepository.SaveAsync(topology, cancellationToken).ConfigureAwait(false);

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
            .GetByIdAsync(new AutomationTopologyId(topologyId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool RequiresTopologyTarget(LayoutTargetKind targetKind)
    {
        return targetKind is LayoutTargetKind.EquipmentNode
            or LayoutTargetKind.AutomationModule
            or LayoutTargetKind.SlotGroup
            or LayoutTargetKind.Slot;
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static ApplicationError ToConflict(TopologyOperationResult result)
    {
        return ApplicationError.Conflict(result.Code, result.Message);
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
}
