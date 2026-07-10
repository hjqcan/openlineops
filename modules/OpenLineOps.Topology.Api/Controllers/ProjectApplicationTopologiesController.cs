using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.ProjectWorkspaces;
using ApiAddCapabilityRequest = OpenLineOps.Topology.Api.Models.AddCapabilityContractRequest;
using ApiAddDriverBindingRequest = OpenLineOps.Topology.Api.Models.AddDriverBindingRequest;
using ApiAddSystemRequest = OpenLineOps.Topology.Api.Models.AddAutomationSystemRequest;
using ApiAddSlotGroupRequest = OpenLineOps.Topology.Api.Models.AddSlotGroupRequest;
using ApiAddSlotRequest = OpenLineOps.Topology.Api.Models.AddSlotDefinitionRequest;
using ApiCreateTopologyRequest = OpenLineOps.Topology.Api.Models.CreateAutomationTopologyRequest;
using ApiUpdateSystemRequest = OpenLineOps.Topology.Api.Models.UpdateAutomationSystemRequest;
using ApiUpdateSlotGroupRequest = OpenLineOps.Topology.Api.Models.UpdateSlotGroupRequest;
using ApiUpdateSlotRequest = OpenLineOps.Topology.Api.Models.UpdateSlotDefinitionRequest;
using AppAddCapabilityRequest = OpenLineOps.Topology.Application.Topologies.AddCapabilityContractRequest;
using AppAddDriverBindingRequest = OpenLineOps.Topology.Application.Topologies.AddDriverBindingRequest;
using AppAddSystemRequest = OpenLineOps.Topology.Application.Topologies.AddAutomationSystemRequest;
using AppAddSlotGroupRequest = OpenLineOps.Topology.Application.Topologies.AddSlotGroupRequest;
using AppAddSlotRequest = OpenLineOps.Topology.Application.Topologies.AddSlotDefinitionRequest;
using AppCreateTopologyRequest = OpenLineOps.Topology.Application.Topologies.CreateAutomationTopologyRequest;
using AppUpdateSystemRequest = OpenLineOps.Topology.Application.Topologies.UpdateAutomationSystemRequest;
using AppUpdateSlotGroupRequest = OpenLineOps.Topology.Application.Topologies.UpdateSlotGroupRequest;
using AppUpdateSlotRequest = OpenLineOps.Topology.Application.Topologies.UpdateSlotDefinitionRequest;

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
        var validationErrors = AutomationTopologyApiContract.Validate(request);
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

        var response = AutomationTopologyApiContract.ToResponse(result.Value);
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
            topology.SystemCount,
            topology.StationCount,
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
            : Ok(AutomationTopologyApiContract.ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/systems")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddSystemRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddSystemAsync(
                projectId,
                applicationId,
                topologyId,
                new AppAddSystemRequest(
                    request.SystemId!,
                    request.ParentSystemId,
                    request.Kind!,
                    request.SystemType!,
                    request.DisplayName!,
                    request.RequiredCapabilityIds!,
                    request.ProvidedCapabilityIds!,
                    request.Metadata!),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPatch("{topologyId}/systems/{systemId}")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> UpdateSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        ApiUpdateSystemRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService.UpdateSystemAsync(
            projectId,
            applicationId,
            topologyId,
            systemId,
            new AppUpdateSystemRequest(request.SystemType, request.DisplayName, request.Metadata),
            cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    [HttpDelete("{topologyId}/systems/{systemId}")]
    [ProducesResponseType<TopologyTargetDeletionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopologyTargetDeletionResponse>> DeleteSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.DeleteSystemAsync(
            projectId,
            applicationId,
            topologyId,
            systemId,
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologyApiContract.ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/capabilities")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddCapabilityAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
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

    [HttpPost("{topologyId}/driver-bindings")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddDriverBindingRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
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
        var validationErrors = AutomationTopologyApiContract.Validate(request);
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
                    request.ParentSystemId!,
                    request.DisplayName!,
                    request.Kind!,
                    request.Capacity!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPatch("{topologyId}/slot-groups/{slotGroupId}")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> UpdateSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        ApiUpdateSlotGroupRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService.UpdateSlotGroupAsync(
            projectId,
            applicationId,
            topologyId,
            slotGroupId,
            new AppUpdateSlotGroupRequest(request.DisplayName, request.Kind, request.Capacity),
            cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    [HttpDelete("{topologyId}/slot-groups/{slotGroupId}")]
    [ProducesResponseType<TopologyTargetDeletionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopologyTargetDeletionResponse>> DeleteSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.DeleteSlotGroupAsync(
            projectId,
            applicationId,
            topologyId,
            slotGroupId,
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologyApiContract.ToResponse(result.Value));
    }

    [HttpPost("{topologyId}/slots")]
    public async Task<ActionResult<AutomationTopologyResponse>> AddSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        ApiAddSlotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
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
                    request.ParentSystemId!,
                    request.Address!,
                    request.DisplayName!,
                    request.MaterialKind!,
                    request.IsEnabled),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPatch("{topologyId}/slots/{slotId}")]
    [ProducesResponseType<AutomationTopologyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationTopologyResponse>> UpdateSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        ApiUpdateSlotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = AutomationTopologyApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService.UpdateSlotAsync(
            projectId,
            applicationId,
            topologyId,
            slotId,
            new AppUpdateSlotRequest(
                request.Address,
                request.DisplayName,
                request.MaterialKind,
                request.IsEnabled),
            cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    [HttpDelete("{topologyId}/slots/{slotId}")]
    [ProducesResponseType<TopologyTargetDeletionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopologyTargetDeletionResponse>> DeleteSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.DeleteSlotAsync(
            projectId,
            applicationId,
            topologyId,
            slotId,
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologyApiContract.ToResponse(result.Value));
    }

    private ActionResult<AutomationTopologyResponse> ToActionResult(
        Result<OpenLineOps.Topology.Application.Topologies.AutomationTopologyDetails> result)
    {
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(AutomationTopologyApiContract.ToResponse(result.Value));
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
