using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Production.Api.Models;
using OpenLineOps.Production.Application.LineDefinitions;
using ApiAdapterRequest = OpenLineOps.Production.Api.Models.ExternalTestProgramAdapterRequest;
using ApiSaveRequest = OpenLineOps.Production.Api.Models.SaveProductionLineRequest;
using AppAdapterRequest = OpenLineOps.Production.Application.LineDefinitions.ExternalTestProgramAdapterRequest;
using AppSaveRequest = OpenLineOps.Production.Application.LineDefinitions.SaveProductionLineDefinitionRequest;

namespace OpenLineOps.Production.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProductionV1)]
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
            || request.DutModel is null
            || string.IsNullOrWhiteSpace(request.DutModel.DutModelId)
            || string.IsNullOrWhiteSpace(request.DutModel.ModelCode)
            || string.IsNullOrWhiteSpace(request.DutModel.IdentityInputKey)
            || request.Workstations is null
            || request.Stages is null
            || request.ExternalTestProgramAdapters is null)
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestIncomplete",
                "Line identity, topology, DUT model, workstations, stages and adapters are required."));
        }

        if (request.Workstations.Any(workstation =>
                workstation is null
                || string.IsNullOrWhiteSpace(workstation.WorkstationId)
                || string.IsNullOrWhiteSpace(workstation.DisplayName)
                || string.IsNullOrWhiteSpace(workstation.TopologyStationNodeId)
                || string.IsNullOrWhiteSpace(workstation.TopologySystemModuleId))
            || request.Stages.Any(stage =>
                stage is null
                || string.IsNullOrWhiteSpace(stage.StageId)
                || stage.Sequence is null
                || string.IsNullOrWhiteSpace(stage.DisplayName)
                || string.IsNullOrWhiteSpace(stage.WorkstationId)
                || string.IsNullOrWhiteSpace(stage.FlowDefinitionId))
            || request.ExternalTestProgramAdapters.Any(AdapterIsIncomplete))
        {
            return Result.Failure<AppSaveRequest>(ApplicationError.Validation(
                "Production.RequestItemIncomplete",
                "Every workstation, stage and external test adapter must contain its required contract fields."));
        }

        return Result.Success(new AppSaveRequest(
            request.LineDefinitionId,
            request.DisplayName,
            request.TopologyId,
            new OpenLineOps.Production.Application.LineDefinitions.DutModelRequest(
                request.DutModel.DutModelId,
                request.DutModel.ModelCode,
                request.DutModel.IdentityInputKey),
            request.Workstations.Select(workstation =>
                new OpenLineOps.Production.Application.LineDefinitions.WorkstationRequest(
                    workstation!.WorkstationId!,
                    workstation.DisplayName!,
                    workstation.TopologyStationNodeId!,
                    workstation.TopologySystemModuleId!)).ToArray(),
            request.Stages.Select(stage =>
                new OpenLineOps.Production.Application.LineDefinitions.ProcessStageRequest(
                    stage!.StageId!,
                    stage.Sequence!.Value,
                    stage.DisplayName!,
                    stage.WorkstationId!,
                    stage.FlowDefinitionId!,
                    stage.ExternalTestProgramAdapterId)).ToArray(),
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
                adapter.TimeoutMilliseconds!.Value)).ToArray()));
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
            new DutModelResponse(
                details.DutModel.DutModelId,
                details.DutModel.ModelCode,
                details.DutModel.IdentityInputKey),
            details.Workstations.Select(workstation => new WorkstationResponse(
                workstation.WorkstationId,
                workstation.DisplayName,
                workstation.TopologyStationNodeId,
                workstation.TopologySystemModuleId)).ToArray(),
            details.Stages.Select(stage => new ProcessStageResponse(
                stage.StageId,
                stage.Sequence,
                stage.DisplayName,
                stage.WorkstationId,
                stage.FlowDefinitionId,
                stage.ExternalTestProgramAdapterId,
                stage.NextStageId)).ToArray(),
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
            summary.DutModelCode,
            summary.StageCount,
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
