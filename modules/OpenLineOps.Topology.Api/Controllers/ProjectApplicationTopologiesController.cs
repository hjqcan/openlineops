using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.ProjectWorkspaces;
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
[Route(OpenLineOpsApiRoutes.ProjectApplicationTopologies)]
public sealed class ProjectApplicationTopologiesController : ControllerBase
{
    private readonly IProjectAutomationTopologyService _topologyService;

    public ProjectApplicationTopologiesController(IProjectAutomationTopologyService topologyService)
    {
        _topologyService = topologyService;
    }

    [HttpPost]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> CreateAsync(
        string projectId,
        string applicationId,
        ApiCreateTopologyRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .CreateAsync(
                projectId,
                applicationId,
                new AppCreateTopologyRequest(request.TopologyId!, request.DisplayName!),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = AutomationTopologiesController.ToResponse(result.Value);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/topologies/{Uri.EscapeDataString(response.TopologyId)}",
            response);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<AutomationTopologySummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<AutomationTopologySummaryResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService
            .ListAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return Ok(result.Value.Select(topology => new AutomationTopologySummaryResponse(
            topology.TopologyId,
            topology.DisplayName,
            topology.NodeCount,
            topology.ModuleCount,
            topology.SlotCount)).ToArray());
    }

    [HttpGet("{topologyId}")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutomationTopologyResponse>> GetByIdAsync(
        string projectId,
        string applicationId,
        string topologyId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService
            .GetByIdAsync(projectId, applicationId, topologyId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologiesController.ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/nodes")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddEquipmentNodeAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddNodeRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddEquipmentNodeAsync(
                projectId,
                applicationId,
                topologyId,
                new AppAddNodeRequest(request.NodeId!, request.ParentNodeId, request.Kind!, request.DisplayName!),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{topologyId}/capabilities")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddCapabilityAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddCapabilityAsync(
                projectId,
                applicationId,
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

        return ToActionResult(result);
    }

    [HttpPost("{topologyId}/modules")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddModuleAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddModuleRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddModuleAsync(
                projectId,
                applicationId,
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

        return ToActionResult(result);
    }

    [HttpPost("{topologyId}/driver-bindings")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddDriverBindingRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddDriverBindingAsync(
                projectId,
                applicationId,
                topologyId,
                new AppAddDriverBindingRequest(
                    request.BindingId!,
                    request.CapabilityId!,
                    request.ProviderKind!,
                    request.ProviderKey!),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{topologyId}/slot-groups")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddSlotGroupRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddSlotGroupAsync(
                projectId,
                applicationId,
                topologyId,
                new AppAddSlotGroupRequest(
                    request.SlotGroupId!,
                    request.ParentNodeId!,
                    request.DisplayName!,
                    request.Kind!,
                    request.Capacity!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{topologyId}/slots")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddSlotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologiesController.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddSlotAsync(
                projectId,
                applicationId,
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

        return ToActionResult(result);
    }

    private ActionResult<AutomationTopologyResponse> ToActionResult(
        Result<OpenLineOps.Topology.Application.Topologies.AutomationTopologyDetails> result)
    {
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologiesController.ToResponse(result.Value));
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(title: error.Code, detail: error.Message, statusCode: statusCode);
    }
}
