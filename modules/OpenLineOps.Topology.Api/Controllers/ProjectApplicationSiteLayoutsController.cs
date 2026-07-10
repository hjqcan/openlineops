using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Api.ReleaseReading;
using OpenLineOps.Topology.Application.ProjectWorkspaces;
using ApiAddElementRequest = OpenLineOps.Topology.Api.Models.AddSiteLayoutElementRequest;
using ApiCreateLayoutRequest = OpenLineOps.Topology.Api.Models.CreateSiteLayoutRequest;
using ApiUpdateElementGeometryRequest = OpenLineOps.Topology.Api.Models.UpdateSiteLayoutElementGeometryRequest;
using ApiUpdateElementPresentationRequest = OpenLineOps.Topology.Api.Models.UpdateSiteLayoutElementPresentationRequest;
using AppAddElementRequest = OpenLineOps.Topology.Application.Layouts.AddSiteLayoutElementRequest;
using AppCreateLayoutRequest = OpenLineOps.Topology.Application.Layouts.CreateSiteLayoutRequest;
using AppUpdateElementGeometryRequest = OpenLineOps.Topology.Application.Layouts.UpdateSiteLayoutElementGeometryRequest;
using AppUpdateElementPresentationRequest = OpenLineOps.Topology.Application.Layouts.UpdateSiteLayoutElementPresentationRequest;

namespace OpenLineOps.Topology.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TopologyV1)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationSiteLayouts)]
public sealed class ProjectApplicationSiteLayoutsController : ControllerBase
{
    private readonly IProjectAutomationTopologyService _topologyService;
    private readonly ProjectReleaseTopologyReader _releaseReader;

    public ProjectApplicationSiteLayoutsController(
        IProjectAutomationTopologyService topologyService,
        ProjectReleaseTopologyReader releaseReader)
    {
        _topologyService = topologyService;
        _releaseReader = releaseReader;
    }

    [HttpPost]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SiteLayoutResponse>> CreateAsync(
        string projectId,
        string applicationId,
        ApiCreateLayoutRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = SiteLayoutApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .CreateLayoutAsync(
                projectId,
                applicationId,
                new AppCreateLayoutRequest(
                    request.LayoutId!,
                    request.TopologyId!,
                    request.DisplayName!,
                    request.CanvasWidth!.Value,
                    request.CanvasHeight!.Value,
                    request.Units!),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = SiteLayoutApiContract.ToResponse(result.Value);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/layouts/{Uri.EscapeDataString(response.LayoutId)}",
            response);
    }

    [HttpGet("{layoutId}")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SiteLayoutResponse>> GetByIdAsync(
        string projectId,
        string applicationId,
        string layoutId,
        [FromQuery] string? snapshotId,
        CancellationToken cancellationToken)
    {
        var result = snapshotId is null
            ? await _topologyService
                .GetLayoutByIdAsync(projectId, applicationId, layoutId, cancellationToken)
                .ConfigureAwait(false)
            : await _releaseReader
                .GetLayoutAsync(
                    projectId,
                    applicationId,
                    snapshotId,
                    layoutId,
                    cancellationToken)
                .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(SiteLayoutApiContract.ToResponse(result.Value));
    }

    [HttpPost("{layoutId}/elements")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SiteLayoutResponse>> AddElementAsync(
        string projectId,
        string applicationId,
        string layoutId,
        ApiAddElementRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = SiteLayoutApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddLayoutElementAsync(
                projectId,
                applicationId,
                layoutId,
                new AppAddElementRequest(
                    request.ElementId!,
                    request.Kind!,
                    request.Target!.Kind!,
                    request.Target.TargetId!,
                    request.ParentElementId,
                    request.X!.Value,
                    request.Y!.Value,
                    request.Width!.Value,
                    request.Height!.Value,
                    request.RotationDegrees!.Value,
                    request.ZIndex!.Value,
                    request.Style!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(SiteLayoutApiContract.ToResponse(result.Value));
    }

    [HttpPut("{layoutId}/elements/{elementId}/geometry")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SiteLayoutResponse>> UpdateElementGeometryAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        ApiUpdateElementGeometryRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = SiteLayoutApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .UpdateLayoutElementGeometryAsync(
                projectId,
                applicationId,
                layoutId,
                elementId,
                new AppUpdateElementGeometryRequest(
                    request.X!.Value,
                    request.Y!.Value,
                    request.Width!.Value,
                    request.Height!.Value,
                    request.RotationDegrees!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(SiteLayoutApiContract.ToResponse(result.Value));
    }

    [HttpPatch("{layoutId}/elements/{elementId}")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SiteLayoutResponse>> UpdateElementPresentationAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        ApiUpdateElementPresentationRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = SiteLayoutApiContract.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService.UpdateLayoutElementPresentationAsync(
            projectId,
            applicationId,
            layoutId,
            elementId,
            new AppUpdateElementPresentationRequest(request.ZIndex, request.Style),
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(SiteLayoutApiContract.ToResponse(result.Value));
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
