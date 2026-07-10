using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Topologies;
using ApiAddElementRequest = OpenLineOps.Topology.Api.Models.AddSiteLayoutElementRequest;
using ApiCreateLayoutRequest = OpenLineOps.Topology.Api.Models.CreateSiteLayoutRequest;
using ApiUpdateElementGeometryRequest = OpenLineOps.Topology.Api.Models.UpdateSiteLayoutElementGeometryRequest;
using AppAddElementRequest = OpenLineOps.Topology.Application.Layouts.AddSiteLayoutElementRequest;
using AppCreateLayoutRequest = OpenLineOps.Topology.Application.Layouts.CreateSiteLayoutRequest;
using AppUpdateElementGeometryRequest = OpenLineOps.Topology.Application.Layouts.UpdateSiteLayoutElementGeometryRequest;

namespace OpenLineOps.Topology.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TopologyV1)]
[Route(OpenLineOpsApiRoutes.SiteLayouts)]
public sealed class SiteLayoutsController : ControllerBase
{
    private readonly IAutomationTopologyService _topologyService;

    public SiteLayoutsController(IAutomationTopologyService topologyService)
    {
        _topologyService = topologyService;
    }

    [HttpPost]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SiteLayoutResponse>> CreateAsync(
        ApiCreateLayoutRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .CreateLayoutAsync(
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

        var response = ToResponse(result.Value);

        return Created($"/api/site-layouts/{response.LayoutId}", response);
    }

    [HttpGet("{layoutId}")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SiteLayoutResponse>> GetByIdAsync(
        string layoutId,
        CancellationToken cancellationToken)
    {
        var result = await _topologyService.GetLayoutByIdAsync(layoutId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{layoutId}/elements")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SiteLayoutResponse>> AddElementAsync(
        string layoutId,
        ApiAddElementRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .AddLayoutElementAsync(
                layoutId,
                new AppAddElementRequest(
                    request.ElementId!,
                    request.Kind!,
                    request.TargetKind!,
                    request.TargetId!,
                    request.X!.Value,
                    request.Y!.Value,
                    request.Width!.Value,
                    request.Height!.Value,
                    request.RotationDegrees!.Value,
                    request.LayerId!,
                    request.Label!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPut("{layoutId}/elements/{elementId}/geometry")]
    [ProducesResponseType<SiteLayoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SiteLayoutResponse>> UpdateElementGeometryAsync(
        string layoutId,
        string elementId,
        ApiUpdateElementGeometryRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _topologyService
            .UpdateLayoutElementGeometryAsync(
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
            : Ok(ToResponse(result.Value));
    }

    private static SiteLayoutResponse ToResponse(SiteLayoutDetails layout)
    {
        return new SiteLayoutResponse(
            layout.LayoutId,
            layout.TopologyId,
            layout.DisplayName,
            layout.CanvasWidth,
            layout.CanvasHeight,
            layout.Units,
            layout.Elements
                .Select(element => new SiteLayoutElementResponse(
                    element.ElementId,
                    element.Kind,
                    element.TargetKind,
                    element.TargetId,
                    element.X,
                    element.Y,
                    element.Width,
                    element.Height,
                    element.RotationDegrees,
                    element.LayerId,
                    element.Label))
                .ToArray());
    }

    private ObjectResult ToProblem(OpenLineOps.Application.Abstractions.Results.ApplicationError error)
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

    private static Dictionary<string, string[]> Validate(ApiCreateLayoutRequest? request)
    {
        var errors = AutomationTopologiesController.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AutomationTopologiesController.AddRequired(errors, nameof(request.LayoutId), request.LayoutId);
        AutomationTopologiesController.AddRequired(errors, nameof(request.TopologyId), request.TopologyId);
        AutomationTopologiesController.AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddPositive(errors, nameof(request.CanvasWidth), request.CanvasWidth);
        AddPositive(errors, nameof(request.CanvasHeight), request.CanvasHeight);
        AutomationTopologiesController.AddRequired(errors, nameof(request.Units), request.Units);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddElementRequest? request)
    {
        var errors = AutomationTopologiesController.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AutomationTopologiesController.AddRequired(errors, nameof(request.ElementId), request.ElementId);
        AutomationTopologiesController.AddRequired(errors, nameof(request.Kind), request.Kind);
        AutomationTopologiesController.AddRequired(errors, nameof(request.TargetKind), request.TargetKind);
        AutomationTopologiesController.AddRequired(errors, nameof(request.TargetId), request.TargetId);
        AddNumber(errors, nameof(request.X), request.X);
        AddNumber(errors, nameof(request.Y), request.Y);
        AddPositive(errors, nameof(request.Width), request.Width);
        AddPositive(errors, nameof(request.Height), request.Height);
        AddNumber(errors, nameof(request.RotationDegrees), request.RotationDegrees);
        AutomationTopologiesController.AddRequired(errors, nameof(request.LayerId), request.LayerId);
        AutomationTopologiesController.AddRequired(errors, nameof(request.Label), request.Label);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiUpdateElementGeometryRequest? request)
    {
        var errors = AutomationTopologiesController.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddNumber(errors, nameof(request.X), request.X);
        AddNumber(errors, nameof(request.Y), request.Y);
        AddPositive(errors, nameof(request.Width), request.Width);
        AddPositive(errors, nameof(request.Height), request.Height);
        AddNumber(errors, nameof(request.RotationDegrees), request.RotationDegrees);

        return errors;
    }

    private static void AddNumber(
        Dictionary<string, string[]> errors,
        string key,
        double? value)
    {
        if (value is null)
        {
            errors[key] = ["Value is required."];
        }
    }

    private static void AddPositive(
        Dictionary<string, string[]> errors,
        string key,
        double? value)
    {
        if (value is null or <= 0)
        {
            errors[key] = ["Value must be positive."];
        }
    }
}
