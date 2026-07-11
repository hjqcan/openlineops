using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using RegisterApiBlockRequest = OpenLineOps.Processes.Api.Models.RegisterProcessBlocklyBlockDefinitionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Processes)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationProcessBlocklyBlocks)]
public sealed class ProjectApplicationProcessBlocklyBlocksController : ControllerBase
{
    private readonly IProjectProcessBlocklyBlockCatalog _catalog;

    public ProjectApplicationProcessBlocklyBlocksController(IProjectProcessBlocklyBlockCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _catalog
            .ListAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return Ok(result.Value.Select(ProcessBlocklyBlockApiContractMapper.ToResponse).ToArray());
    }

    [HttpGet("{blockType}/versions")]
    [ProducesResponseType<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>> ListVersionsAsync(
        string projectId,
        string applicationId,
        string blockType,
        CancellationToken cancellationToken)
    {
        var result = await _catalog
            .ListVersionsAsync(projectId, applicationId, blockType, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return Ok(result.Value.Select(ProcessBlocklyBlockApiContractMapper.ToResponse).ToArray());
    }

    [HttpPost]
    [ProducesResponseType<ProcessBlocklyBlockDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProcessBlocklyBlockDefinitionResponse>> RegisterAsync(
        string projectId,
        string applicationId,
        RegisterApiBlockRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = ProcessBlocklyBlockApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _catalog
            .RegisterAsync(
                projectId,
                applicationId,
                ProcessBlocklyBlockApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ProcessBlocklyBlockApiContractMapper.ToResponse(result.Value);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/process-blocks/{Uri.EscapeDataString(response.BlockType)}",
            response);
    }

    private ObjectResult ToProblem(ApplicationError error)
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
}
