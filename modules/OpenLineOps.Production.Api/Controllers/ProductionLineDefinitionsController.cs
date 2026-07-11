using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Production.Api.Models;
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;
using ApiAdapterRequest = OpenLineOps.Production.Api.Models.ExternalTestProgramAdapterRequest;
using ApiSaveRequest = OpenLineOps.Production.Api.Models.SaveProductionLineRequest;
using AppAdapterRequest = OpenLineOps.Production.Application.LineDefinitions.ExternalTestProgramAdapterRequest;
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

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
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

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
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
            || request.ExternalTestProgramAdapters is null)
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestIncomplete",
                "Line identity, topology, product model, entry operation, operations, transitions and adapters are required."));
        }

        if (request.Operations.Any(operation =>
                operation is null
                || string.IsNullOrWhiteSpace(operation.OperationId)
                || string.IsNullOrWhiteSpace(operation.DisplayName)
                || string.IsNullOrWhiteSpace(operation.StationSystemId)
                || string.IsNullOrWhiteSpace(operation.FlowDefinitionId)
                || string.IsNullOrWhiteSpace(operation.ConfigurationSnapshotId))
            || request.Transitions.Any(transition =>
                transition is null
                || string.IsNullOrWhiteSpace(transition.TransitionId)
                || string.IsNullOrWhiteSpace(transition.SourceOperationId)
                || string.IsNullOrWhiteSpace(transition.TargetOperationId)
                || string.IsNullOrWhiteSpace(transition.Kind))
            || request.ExternalTestProgramAdapters.Any(AdapterIsIncomplete))
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestItemIncomplete",
                "Every operation, transition and external test adapter must contain its required contract fields."));
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

        return Result.Success(new AppSaveRequest(
            request.LineDefinitionId,
            request.DisplayName,
            request.TopologyId,
            new OpenLineOps.Production.Application.LineDefinitions.ProductModelRequest(
                request.ProductModel.ProductModelId,
                request.ProductModel.ModelCode,
                request.ProductModel.IdentityInputKey),
            request.EntryOperationId,
            request.Operations.Select(operation =>
                new OpenLineOps.Production.Application.LineDefinitions.OperationDefinitionRequest(
                    operation!.OperationId!,
                    operation.DisplayName!,
                    operation.StationSystemId!,
                    operation.FlowDefinitionId!,
                    operation.ConfigurationSnapshotId!)).ToArray(),
            transitions,
            request.ExternalTestProgramAdapters.Select(adapter => new AppAdapterRequest(
                adapter!.AdapterId!,
                adapter.DisplayName!,
                adapter.CapabilityId!,
                adapter.CommandName!,
                adapter.Executable,
                adapter.ProviderKey,
                adapter.ArgumentTemplates!.Select(static argument => argument!).ToArray(),
                adapter.InputMappings!.Select(mapping =>
                    new OpenLineOps.Production.Application.LineDefinitions.ExternalTestProgramInputMappingRequest(
                        mapping!.Source!,
                        mapping.Target!)).ToArray(),
                adapter.ResultMappings!.Select(mapping =>
                    new OpenLineOps.Production.Application.LineDefinitions.ExternalTestProgramResultMappingRequest(
                        mapping!.SourcePath!,
                        mapping.TargetKey!)).ToArray(),
                new OpenLineOps.Production.Application.LineDefinitions.ExternalTestProgramOutcomeMappingRequest(
                    adapter.OutcomeMapping!.SourcePath!,
                    adapter.OutcomeMapping.PassedToken!,
                    adapter.OutcomeMapping.FailedToken!,
                    adapter.OutcomeMapping.AbortedToken!),
                adapter.TimeoutMilliseconds!.Value)).ToArray()));
    }

    private static bool TryParseExact<T>(string value, out T parsed)
        where T : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: false, out parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(value, parsed.ToString(), StringComparison.Ordinal);
    }

    private static bool AdapterIsIncomplete(ApiAdapterRequest? adapter)
    {
        var maximumTimeoutMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        return adapter is null
            || string.IsNullOrWhiteSpace(adapter.AdapterId)
            || string.IsNullOrWhiteSpace(adapter.DisplayName)
            || string.IsNullOrWhiteSpace(adapter.CapabilityId)
            || string.IsNullOrWhiteSpace(adapter.CommandName)
            || adapter.ArgumentTemplates is null
            || adapter.InputMappings is null
            || adapter.ResultMappings is null
            || adapter.OutcomeMapping is null
            || string.IsNullOrWhiteSpace(adapter.OutcomeMapping.SourcePath)
            || string.IsNullOrWhiteSpace(adapter.OutcomeMapping.PassedToken)
            || string.IsNullOrWhiteSpace(adapter.OutcomeMapping.FailedToken)
            || string.IsNullOrWhiteSpace(adapter.OutcomeMapping.AbortedToken)
            || adapter.TimeoutMilliseconds is null
            || adapter.TimeoutMilliseconds <= 0
            || adapter.TimeoutMilliseconds > maximumTimeoutMilliseconds
            || adapter.ArgumentTemplates.Any(static argument => string.IsNullOrWhiteSpace(argument))
            || adapter.InputMappings.Any(mapping =>
                mapping is null
                || string.IsNullOrWhiteSpace(mapping.Source)
                || string.IsNullOrWhiteSpace(mapping.Target))
            || adapter.ResultMappings.Any(mapping =>
                mapping is null
                || string.IsNullOrWhiteSpace(mapping.SourcePath)
                || string.IsNullOrWhiteSpace(mapping.TargetKey));
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
                operation.ConfigurationSnapshotId)).ToArray(),
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
            details.ExternalTestProgramAdapters.Select(adapter => new ExternalTestProgramAdapterResponse(
                adapter.AdapterId,
                adapter.DisplayName,
                adapter.CapabilityId,
                adapter.CommandName,
                adapter.LaunchKind,
                adapter.Executable,
                adapter.ProviderKey,
                adapter.ArgumentTemplates,
                adapter.InputMappings.Select(mapping =>
                    new ExternalTestProgramInputMappingResponse(mapping.Source, mapping.Target)).ToArray(),
                adapter.ResultMappings.Select(mapping =>
                    new ExternalTestProgramResultMappingResponse(mapping.SourcePath, mapping.TargetKey)).ToArray(),
                new ExternalTestProgramOutcomeMappingResponse(
                    adapter.OutcomeMapping.SourcePath,
                    adapter.OutcomeMapping.PassedToken,
                    adapter.OutcomeMapping.FailedToken,
                    adapter.OutcomeMapping.AbortedToken),
                adapter.TimeoutMilliseconds)).ToArray(),
            details.CreatedAtUtc,
            details.UpdatedAtUtc);
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
