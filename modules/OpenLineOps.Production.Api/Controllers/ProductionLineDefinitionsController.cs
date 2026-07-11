using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Production.Api.Models;
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;
using ApiSaveRequest = OpenLineOps.Production.Api.Models.SaveProductionLineRequest;
using AppSaveRequest = OpenLineOps.Production.Application.LineDefinitions.SaveProductionLineDefinitionRequest;

namespace OpenLineOps.Production.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Production)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationProductionLines)]
public sealed class ProductionLineDefinitionsController : ControllerBase
{
    private readonly IProjectProductionLineDefinitionService _service;

    public ProductionLineDefinitionsController(IProjectProductionLineDefinitionService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProductionLineSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ProductionLineSummaryResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _service.ListAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(ToSummaryResponse).ToArray());
    }

    [HttpGet("{lineDefinitionId}")]
    [ProducesResponseType<ProductionLineResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductionLineResponse>> GetAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _service
            .GetByIdAsync(projectId, applicationId, lineDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        Response.SetEditorDocumentRevision(response.Revision);
        return Ok(response);
    }

    [HttpPost]
    [ProducesResponseType<ProductionLineResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductionLineResponse>> CreateAsync(
        string projectId,
        string applicationId,
        ApiSaveRequest request,
        CancellationToken cancellationToken)
    {
        var applicationRequest = ToApplicationRequest(request);
        if (applicationRequest.IsFailure)
        {
            return ToProblem(applicationRequest.Error);
        }

        var result = await _service
            .CreateAsync(projectId, applicationId, applicationRequest.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        Response.SetEditorDocumentRevision(response.Revision);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/production-lines/{Uri.EscapeDataString(response.LineDefinitionId)}",
            response);
    }

    [HttpPut("{lineDefinitionId}")]
    [ProducesResponseType<ProductionLineResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductionLineResponse>> ReplaceAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        ApiSaveRequest request,
        CancellationToken cancellationToken)
    {
        await using var lease = await EditorDocumentConcurrency.AcquireAsync(
                $"production-line:{projectId}:{applicationId}:{lineDefinitionId}",
                cancellationToken)
            .ConfigureAwait(false);
        var current = await _service
            .GetByIdAsync(projectId, applicationId, lineDefinitionId, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return ToProblem(current.Error);
        }

        var currentRevision = ToResponse(current.Value).Revision;
        var precondition = EditorDocumentConcurrency.Evaluate(
            Request.Headers[EditorDocumentConcurrency.IfMatchHeaderName].ToString(),
            Request.Headers[EditorDocumentConcurrency.ConflictResolutionHeaderName].ToString(),
            currentRevision);
        if (precondition != EditorDocumentPrecondition.Satisfied)
        {
            return this.EditorDocumentPreconditionProblem(precondition, currentRevision);
        }

        var applicationRequest = ToApplicationRequest(request);
        if (applicationRequest.IsFailure)
        {
            return ToProblem(applicationRequest.Error);
        }

        var result = await _service.ReplaceAsync(
                projectId,
                applicationId,
                lineDefinitionId,
                applicationRequest.Value,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        Response.SetEditorDocumentRevision(response.Revision);
        return Ok(response);
    }

    private static Result<AppSaveRequest> ToApplicationRequest(ApiSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LineDefinitionId)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.TopologyId)
            || request.ProductModel is null
            || string.IsNullOrWhiteSpace(request.ProductModel.ProductModelId)
            || string.IsNullOrWhiteSpace(request.ProductModel.ModelCode)
            || string.IsNullOrWhiteSpace(request.ProductModel.IdentityInputKey)
            || string.IsNullOrWhiteSpace(request.EntryOperationId)
            || request.Operations is null
            || request.Transitions is null
            || request.LineControllerAuthorizations is null)
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestIncomplete",
                "Line identity, topology, product model, entry operation, operations and transitions are required."));
        }

        if (request.Operations.Any(operation =>
                operation is null
                || string.IsNullOrWhiteSpace(operation.OperationId)
                || string.IsNullOrWhiteSpace(operation.DisplayName)
                || string.IsNullOrWhiteSpace(operation.StationSystemId)
                || string.IsNullOrWhiteSpace(operation.FlowDefinitionId)
                || string.IsNullOrWhiteSpace(operation.ConfigurationSnapshotId)
                || operation.Resources is null
                || operation.Resources.Count == 0
                || operation.Resources.Any(resource => resource is null
                    || string.IsNullOrWhiteSpace(resource.BindingId)
                    || string.IsNullOrWhiteSpace(resource.Kind)
                    || string.IsNullOrWhiteSpace(resource.TopologyTargetId)
                    || string.IsNullOrWhiteSpace(resource.Resolution)))
            || request.Transitions.Any(transition =>
                transition is null
                || string.IsNullOrWhiteSpace(transition.TransitionId)
                || string.IsNullOrWhiteSpace(transition.SourceOperationId)
                || string.IsNullOrWhiteSpace(transition.TargetOperationId)
                || string.IsNullOrWhiteSpace(transition.Kind))
            || request.LineControllerAuthorizations.Any(authorization =>
                authorization is null
                || string.IsNullOrWhiteSpace(authorization.AuthorizationId)
                || string.IsNullOrWhiteSpace(authorization.OperationId)
                || string.IsNullOrWhiteSpace(authorization.ActionId)
                || string.IsNullOrWhiteSpace(authorization.ControllerSystemId)
                || string.IsNullOrWhiteSpace(authorization.ControllerBindingId)
                || string.IsNullOrWhiteSpace(authorization.ControllerCapabilityId)
                || string.IsNullOrWhiteSpace(authorization.ControllerAction)
                || string.IsNullOrWhiteSpace(authorization.TargetStationSystemId)
                || string.IsNullOrWhiteSpace(authorization.TargetSystemId)
                || string.IsNullOrWhiteSpace(authorization.TargetBindingId)
                || string.IsNullOrWhiteSpace(authorization.TargetCapabilityId)
                || string.IsNullOrWhiteSpace(authorization.TargetAction)))
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestItemIncomplete",
                "Every operation and transition must contain its required contract fields."));
        }

        var transitions = new List<OpenLineOps.Production.Application.LineDefinitions.RouteTransitionRequest>();
        foreach (var transition in request.Transitions)
        {
            if (!TryParseExact(transition!.Kind!, out RouteTransitionKind kind)
                || (transition.RequiredJudgement is not null
                    && !TryParseExact(
                        transition.RequiredJudgement,
                        out RouteJudgement requiredJudgement))
                || (transition.ExpectedOutputKind is not null
                    && !TryParseExact(
                        transition.ExpectedOutputKind,
                        out ProductionContextValueKind expectedOutputKind)))
            {
                return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                    "Production.RouteTransitionTokenInvalid",
                    "Route transition kinds, judgements and Production Context value kinds must use exact supported tokens."));
            }

            var hasOutputCondition = transition.OutputKey is not null
                && transition.ExpectedOutputKind is not null
                && transition.ExpectedOutputValue is not null;
            var hasAnyOutputConditionField = transition.OutputKey is not null
                || transition.ExpectedOutputKind is not null
                || transition.ExpectedOutputValue is not null;
            if (hasAnyOutputConditionField != hasOutputCondition
                || (kind == RouteTransitionKind.Condition) != hasOutputCondition)
            {
                return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                    "Production.RouteOutputConditionInvalid",
                    "Condition transitions require outputKey, expectedOutputKind and expectedOutputValue; other transitions cannot define them."));
            }

            var judgement = transition.RequiredJudgement is null
                ? (RouteJudgement?)null
                : Enum.Parse<RouteJudgement>(transition.RequiredJudgement, ignoreCase: false);
            transitions.Add(new OpenLineOps.Production.Application.LineDefinitions.RouteTransitionRequest(
                transition.TransitionId!,
                transition.SourceOperationId!,
                transition.TargetOperationId!,
                kind,
                judgement,
                transition.MaxTraversals,
                transition.ParallelGroupId,
                transition.OutputKey,
                transition.ExpectedOutputKind is null
                    ? null
                    : Enum.Parse<ProductionContextValueKind>(
                        transition.ExpectedOutputKind,
                        ignoreCase: false),
                transition.ExpectedOutputValue));
        }

        var operations = new List<OpenLineOps.Production.Application.LineDefinitions.OperationDefinitionRequest>();
        foreach (var operation in request.Operations)
        {
            var resources = new List<OpenLineOps.Production.Application.LineDefinitions.OperationResourceBindingRequest>();
            foreach (var resource in operation!.Resources!)
            {
                if (!TryParseExact(resource!.Kind!, out OperationResourceKind kind)
                    || !TryParseExact(
                        resource.Resolution!,
                        out OperationResourceResolution resolution))
                {
                    return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                        "Production.OperationResourceTokenInvalid",
                        "Operation resource kinds and resolutions must use exact supported tokens."));
                }

                resources.Add(new OpenLineOps.Production.Application.LineDefinitions.OperationResourceBindingRequest(
                    resource.BindingId!,
                    kind,
                    resource.TopologyTargetId!,
                    resolution));
            }

            operations.Add(new OpenLineOps.Production.Application.LineDefinitions.OperationDefinitionRequest(
                operation.OperationId!,
                operation.DisplayName!,
                operation.StationSystemId!,
                operation.FlowDefinitionId!,
                operation.ConfigurationSnapshotId!,
                resources));
        }

        return Result.Success(new AppSaveRequest(
            request.LineDefinitionId,
            request.DisplayName,
            request.TopologyId,
            new OpenLineOps.Production.Application.LineDefinitions.ProductModelRequest(
                request.ProductModel.ProductModelId,
                request.ProductModel.ModelCode,
                request.ProductModel.IdentityInputKey),
            request.EntryOperationId,
            operations,
            transitions,
            request.LineControllerAuthorizations.Select(authorization =>
                new OpenLineOps.Production.Application.LineDefinitions.LineControllerAuthorizationRequest(
                    authorization!.AuthorizationId!,
                    authorization.OperationId!,
                    authorization.ActionId!,
                    authorization.ControllerSystemId!,
                    authorization.ControllerBindingId!,
                    authorization.ControllerCapabilityId!,
                    authorization.ControllerAction!,
                    authorization.TargetStationSystemId!,
                    authorization.TargetSystemId!,
                    authorization.TargetBindingId!,
                    authorization.TargetCapabilityId!,
                    authorization.TargetAction!))
                .ToArray()));
    }

    private static bool TryParseExact<T>(string value, out T parsed)
        where T : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: false, out parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(value, parsed.ToString(), StringComparison.Ordinal);
    }

    private static ProductionLineResponse ToResponse(ProductionLineDefinitionDetails details)
    {
        return new ProductionLineResponse(
            details.LineDefinitionId,
            details.DisplayName,
            details.TopologyId,
            new ProductModelResponse(
                details.ProductModel.ProductModelId,
                details.ProductModel.ModelCode,
                details.ProductModel.IdentityInputKey),
            details.EntryOperationId,
            details.Operations.Select(operation => new OperationDefinitionResponse(
                operation.OperationId,
                operation.DisplayName,
                operation.StationSystemId,
                operation.FlowDefinitionId,
                operation.ConfigurationSnapshotId,
                operation.Resources.Select(resource => new OperationResourceBindingResponse(
                    resource.BindingId,
                    resource.Kind,
                    resource.TopologyTargetId,
                    resource.Resolution)).ToArray())).ToArray(),
            details.Transitions.Select(transition => new RouteTransitionResponse(
                transition.TransitionId,
                transition.SourceOperationId,
                transition.TargetOperationId,
                transition.Kind,
                transition.RequiredJudgement,
                transition.MaxTraversals,
                transition.ParallelGroupId,
                transition.OutputKey,
                transition.ExpectedOutputKind,
                transition.ExpectedOutputValue)).ToArray(),
            details.LineControllerAuthorizations.Select(authorization =>
                new LineControllerAuthorizationResponse(
                    authorization.AuthorizationId,
                    authorization.OperationId,
                    authorization.ActionId,
                    authorization.ControllerSystemId,
                    authorization.ControllerBindingId,
                    authorization.ControllerCapabilityId,
                    authorization.ControllerAction,
                    authorization.TargetStationSystemId,
                    authorization.TargetSystemId,
                    authorization.TargetBindingId,
                    authorization.TargetCapabilityId,
                    authorization.TargetAction)).ToArray(),
            details.CreatedAtUtc,
            details.UpdatedAtUtc,
            EditorDocumentConcurrency.ComputeRevision(details));
    }

    private static ProductionLineSummaryResponse ToSummaryResponse(ProductionLineDefinitionSummary summary)
    {
        return new ProductionLineSummaryResponse(
            summary.LineDefinitionId,
            summary.DisplayName,
            summary.TopologyId,
            summary.ProductModelCode,
            summary.OperationCount,
            summary.UpdatedAtUtc);
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
