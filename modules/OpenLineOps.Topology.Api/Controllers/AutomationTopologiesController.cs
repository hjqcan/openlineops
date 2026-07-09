using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.Topologies;
using ApiAddCapabilityRequest = OpenLineOps.Topology.Api.Models.AddCapabilityContractRequest;
using ApiAddDriverBindingRequest = OpenLineOps.Topology.Api.Models.AddDriverBindingRequest;
using ApiAddModuleRequest = OpenLineOps.Topology.Api.Models.AddAutomationModuleRequest;
using ApiAddNodeRequest = OpenLineOps.Topology.Api.Models.AddEquipmentNodeRequest;
using ApiAddSlotGroupRequest = OpenLineOps.Topology.Api.Models.AddSlotGroupRequest;
using ApiAddSlotRequest = OpenLineOps.Topology.Api.Models.AddSlotDefinitionRequest;
using ApiCreateTopologyRequest = OpenLineOps.Topology.Api.Models.CreateAutomationTopologyRequest;
using AppAddCapabilityRequest = OpenLineOps.Topology.Application.Topologies.AddCapabilityContractRequest;
using AppAddDriverBindingRequest = OpenLineOps.Topology.Application.Topologies.AddDriverBindingRequest;
using AppAddModuleRequest = OpenLineOps.Topology.Application.Topologies.AddAutomationModuleRequest;
using AppAddNodeRequest = OpenLineOps.Topology.Application.Topologies.AddEquipmentNodeRequest;
using AppAddSlotGroupRequest = OpenLineOps.Topology.Application.Topologies.AddSlotGroupRequest;
using AppAddSlotRequest = OpenLineOps.Topology.Application.Topologies.AddSlotDefinitionRequest;
using AppCreateTopologyRequest = OpenLineOps.Topology.Application.Topologies.CreateAutomationTopologyRequest;

namespace OpenLineOps.Topology.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TopologyV1)]
[Route(OpenLineOpsApiRoutes.AutomationTopologies)]
public sealed class AutomationTopologiesController : ControllerBase
{
    private readonly IAutomationTopologyService _topologyService;

    public AutomationTopologiesController(IAutomationTopologyService topologyService)
    {
        _topologyService = topologyService;
    }

    [HttpPost]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> CreateAsync(
        ApiCreateTopologyRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .CreateAsync(new AppCreateTopologyRequest(request.TopologyId!, request.DisplayName!), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/automation-topologies/{response.TopologyId}", response);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<AutomationTopologySummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<AutomationTopologySummaryResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result.Value.Select(ToSummaryResponse).ToArray());
    }

    [HttpGet("{topologyId}")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutomationTopologyResponse>> GetByIdAsync(
        string topologyId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.GetByIdAsync(topologyId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/nodes")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddEquipmentNodeAsync(
        string topologyId,
        ApiAddNodeRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddEquipmentNodeAsync(
                topologyId,
                new AppAddNodeRequest(request.NodeId!, request.ParentNodeId, request.Kind!, request.DisplayName!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/capabilities")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddCapabilityAsync(
        string topologyId,
        ApiAddCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddCapabilityAsync(
                topologyId,
                new AppAddCapabilityRequest(
                    request.CapabilityId!,
                    request.CommandName!,
                    request.Version!,
                    request.InputSchema,
                    request.OutputSchema,
                    request.TimeoutSeconds!.Value,
                    request.SafetyClass!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/modules")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddModuleAsync(
        string topologyId,
        ApiAddModuleRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddModuleAsync(
                topologyId,
                new AppAddModuleRequest(
                    request.ModuleId!,
                    request.NodeId!,
                    request.ModuleKind!,
                    request.DisplayName!,
                    request.RequiredCapabilityIds!,
                    request.ProvidedCapabilityIds!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/driver-bindings")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddDriverBindingAsync(
        string topologyId,
        ApiAddDriverBindingRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddDriverBindingAsync(
                topologyId,
                new AppAddDriverBindingRequest(
                    request.BindingId!,
                    request.CapabilityId!,
                    request.ProviderKind!,
                    request.ProviderKey!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/slot-groups")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSlotGroupAsync(
        string topologyId,
        ApiAddSlotGroupRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddSlotGroupAsync(
                topologyId,
                new AppAddSlotGroupRequest(
                    request.SlotGroupId!,
                    request.ParentNodeId!,
                    request.DisplayName!,
                    request.Kind!,
                    request.Capacity!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/slots")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSlotAsync(
        string topologyId,
        ApiAddSlotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddSlotAsync(
                topologyId,
                new AppAddSlotRequest(
                    request.SlotGroupId!,
                    request.SlotId!,
                    request.ParentNodeId!,
                    request.Address!,
                    request.DisplayName!,
                    request.MaterialKind!,
                    request.IsEnabled),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    internal static AutomationTopologyResponse ToResponse(AutomationTopologyDetails topology)
    {
        return new AutomationTopologyResponse(
            topology.TopologyId,
            topology.DisplayName,
            topology.CreatedAtUtc,
            topology.Nodes.Select(ToResponse).ToArray(),
            topology.Modules.Select(ToResponse).ToArray(),
            topology.Capabilities.Select(ToResponse).ToArray(),
            topology.DriverBindings.Select(ToResponse).ToArray(),
            topology.SlotGroups.Select(ToResponse).ToArray(),
            topology.Slots.Select(ToResponse).ToArray());
    }

    private static AutomationTopologySummaryResponse ToSummaryResponse(AutomationTopologySummary topology)
    {
        return new AutomationTopologySummaryResponse(
            topology.TopologyId,
            topology.DisplayName,
            topology.NodeCount,
            topology.ModuleCount,
            topology.SlotCount);
    }

    private static EquipmentNodeResponse ToResponse(EquipmentNodeDetails node)
    {
        return new EquipmentNodeResponse(node.NodeId, node.ParentNodeId, node.Kind, node.DisplayName);
    }

    private static AutomationModuleResponse ToResponse(AutomationModuleDetails module)
    {
        return new AutomationModuleResponse(
            module.ModuleId,
            module.NodeId,
            module.ModuleKind,
            module.DisplayName,
            module.RequiredCapabilityIds,
            module.ProvidedCapabilityIds);
    }

    private static CapabilityContractResponse ToResponse(CapabilityContractDetails capability)
    {
        return new CapabilityContractResponse(
            capability.CapabilityId,
            capability.CommandName,
            capability.Version,
            capability.InputSchema,
            capability.OutputSchema,
            capability.TimeoutSeconds,
            capability.SafetyClass);
    }

    private static DriverBindingResponse ToResponse(DriverBindingDetails binding)
    {
        return new DriverBindingResponse(
            binding.BindingId,
            binding.CapabilityId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static SlotGroupResponse ToResponse(SlotGroupDetails group)
    {
        return new SlotGroupResponse(
            group.SlotGroupId,
            group.ParentNodeId,
            group.DisplayName,
            group.Kind,
            group.Capacity,
            group.SlotIds);
    }

    private static SlotDefinitionResponse ToResponse(SlotDefinitionDetails slot)
    {
        return new SlotDefinitionResponse(
            slot.SlotId,
            slot.ParentNodeId,
            slot.Address,
            slot.DisplayName,
            slot.MaterialKind,
            slot.IsEnabled);
    }

    internal ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }

    private static Dictionary<string, string[]> Validate(ApiCreateTopologyRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.TopologyId), request.TopologyId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddNodeRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.NodeId), request.NodeId);
        AddRequired(errors, nameof(request.Kind), request.Kind);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddCapabilityRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.CapabilityId), request.CapabilityId);
        AddRequired(errors, nameof(request.CommandName), request.CommandName);
        AddRequired(errors, nameof(request.Version), request.Version);
        AddRequired(errors, nameof(request.SafetyClass), request.SafetyClass);
        AddPositive(errors, nameof(request.TimeoutSeconds), request.TimeoutSeconds);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddModuleRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.ModuleId), request.ModuleId);
        AddRequired(errors, nameof(request.NodeId), request.NodeId);
        AddRequired(errors, nameof(request.ModuleKind), request.ModuleKind);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequiredCollection(errors, nameof(request.RequiredCapabilityIds), request.RequiredCapabilityIds);
        AddRequiredCollection(errors, nameof(request.ProvidedCapabilityIds), request.ProvidedCapabilityIds);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddDriverBindingRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.BindingId), request.BindingId);
        AddRequired(errors, nameof(request.CapabilityId), request.CapabilityId);
        AddRequired(errors, nameof(request.ProviderKind), request.ProviderKind);
        AddRequired(errors, nameof(request.ProviderKey), request.ProviderKey);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddSlotGroupRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.SlotGroupId), request.SlotGroupId);
        AddRequired(errors, nameof(request.ParentNodeId), request.ParentNodeId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.Kind), request.Kind);
        AddPositive(errors, nameof(request.Capacity), request.Capacity);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddSlotRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.SlotGroupId), request.SlotGroupId);
        AddRequired(errors, nameof(request.SlotId), request.SlotId);
        AddRequired(errors, nameof(request.ParentNodeId), request.ParentNodeId);
        AddRequired(errors, nameof(request.Address), request.Address);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.MaterialKind), request.MaterialKind);

        return errors;
    }

    internal static Dictionary<string, string[]> NewErrors(object? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
        }

        return errors;
    }

    internal static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }

    internal static void AddPositive(
        Dictionary<string, string[]> errors,
        string key,
        int? value)
    {
        if (value is null or <= 0)
        {
            errors[key] = ["Value must be positive."];
        }
    }

    private static void AddRequiredCollection(
        Dictionary<string, string[]> errors,
        string key,
        IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            errors[key] = ["Collection is required."];
        }
    }
}
