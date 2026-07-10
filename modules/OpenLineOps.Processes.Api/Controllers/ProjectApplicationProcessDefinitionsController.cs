using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using CreateApiDefinitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessDefinitionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProcessesV1)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationProcesses)]
public sealed class ProjectApplicationProcessDefinitionsController : ControllerBase
{
    private readonly IProjectProcessDefinitionService _definitionService;

    public ProjectApplicationProcessDefinitionsController(IProjectProcessDefinitionService definitionService)
    {
        _definitionService = definitionService;
    }

    [HttpPost]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProcessDefinitionResponse>> CreateAsync(
        string projectId,
        string applicationId,
        CreateApiDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = ProcessDefinitionApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _definitionService
            .CreateAsync(
                projectId,
                applicationId,
                ProcessDefinitionApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ProcessDefinitionApiContractMapper.ToResponse(result.Value);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/processes/{Uri.EscapeDataString(response.ProcessDefinitionId)}",
            response);
    }

    [HttpPut("{processDefinitionId}")]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProcessDefinitionResponse>> ReplaceDraftAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CreateApiDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = ProcessDefinitionApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _definitionService
            .ReplaceDraftAsync(
                projectId,
                applicationId,
                processDefinitionId,
                ProcessDefinitionApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ProcessDefinitionApiContractMapper.ToResponse(result.Value));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProcessDefinitionSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessDefinitionSummaryResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .ListAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return Ok(result.Value.Select(ProcessDefinitionApiContractMapper.ToSummaryResponse).ToArray());
    }

    [HttpGet("{processDefinitionId}")]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessDefinitionResponse>> GetByIdAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .GetByIdAsync(projectId, applicationId, processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ProcessDefinitionApiContractMapper.ToResponse(result.Value));
    }

    [HttpGet("{processDefinitionId}/validation")]
    [ProducesResponseType<ProcessGraphValidationReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessGraphValidationReportResponse>> ValidateAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .ValidateAsync(projectId, applicationId, processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ProcessDefinitionApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("{processDefinitionId}/publish")]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessDefinitionResponse>> PublishAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .PublishAsync(projectId, applicationId, processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ProcessDefinitionApiContractMapper.ToResponse(result.Value));
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
