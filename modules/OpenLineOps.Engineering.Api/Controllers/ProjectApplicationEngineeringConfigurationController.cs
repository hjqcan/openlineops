using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Engineering.Api.Models;
using OpenLineOps.Engineering.Application.ProjectWorkspaces;
using CreateApiEngineeringProjectRequest = OpenLineOps.Engineering.Api.Models.CreateEngineeringProjectRequest;
using CreateApiRecipeRequest = OpenLineOps.Engineering.Api.Models.CreateRecipeRequest;
using CreateApiStationProfileRequest = OpenLineOps.Engineering.Api.Models.CreateStationProfileRequest;
using CreateApiWorkspaceRequest = OpenLineOps.Engineering.Api.Models.CreateWorkspaceRequest;
using PublishApiConfigurationSnapshotRequest = OpenLineOps.Engineering.Api.Models.PublishConfigurationSnapshotRequest;

namespace OpenLineOps.Engineering.Api.Controllers;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Engineering)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationEngineering)]
public sealed class ProjectApplicationEngineeringConfigurationController : ControllerBase
{
    private readonly IProjectEngineeringConfigurationService _configurationService;

    public ProjectApplicationEngineeringConfigurationController(
        IProjectEngineeringConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    [HttpPost("workspaces")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkspaceResponse>> CreateWorkspaceAsync(
        string projectId,
        string applicationId,
        CreateApiWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = EngineeringApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateWorkspaceAsync(
                projectId,
                applicationId,
                EngineeringApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = EngineeringApiContractMapper.ToResponse(result.Value);
        return Created(
            $"{GetBasePath(projectId, applicationId)}/workspaces/{Uri.EscapeDataString(response.WorkspaceId)}",
            response);
    }

    [HttpGet("workspaces")]
    [ProducesResponseType<IReadOnlyCollection<WorkspaceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<WorkspaceResponse>>> ListWorkspacesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListWorkspacesAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(EngineeringApiContractMapper.ToResponse).ToArray());
    }

    [HttpGet("workspaces/{workspaceId}")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceResponse>> GetWorkspaceAsync(
        string projectId,
        string applicationId,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetWorkspaceAsync(projectId, applicationId, workspaceId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("projects")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> CreateProjectAsync(
        string projectId,
        string applicationId,
        CreateApiEngineeringProjectRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = EngineeringApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateProjectAsync(
                projectId,
                applicationId,
                EngineeringApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = EngineeringApiContractMapper.ToResponse(result.Value);
        return Created(
            $"{GetBasePath(projectId, applicationId)}/projects/{Uri.EscapeDataString(response.ProjectId)}",
            response);
    }

    [HttpGet("projects")]
    [ProducesResponseType<IReadOnlyCollection<EngineeringProjectResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<EngineeringProjectResponse>>> ListProjectsAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListProjectsAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(EngineeringApiContractMapper.ToResponse).ToArray());
    }

    [HttpGet("projects/{engineeringProjectId}")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EngineeringProjectResponse>> GetProjectAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetProjectAsync(projectId, applicationId, engineeringProjectId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("recipes")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeResponse>> CreateRecipeAsync(
        string projectId,
        string applicationId,
        CreateApiRecipeRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = EngineeringApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateRecipeAsync(
                projectId,
                applicationId,
                EngineeringApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = EngineeringApiContractMapper.ToResponse(result.Value);
        return Created(
            $"{GetBasePath(projectId, applicationId)}/recipes/{Uri.EscapeDataString(response.RecipeId)}",
            response);
    }

    [HttpGet("recipes")]
    [ProducesResponseType<IReadOnlyCollection<RecipeResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<RecipeResponse>>> ListRecipesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListRecipesAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(EngineeringApiContractMapper.ToResponse).ToArray());
    }

    [HttpGet("recipes/{recipeId}")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeResponse>> GetRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetRecipeAsync(projectId, applicationId, recipeId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("recipes/{recipeId}/publish")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeResponse>> PublishRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .PublishRecipeAsync(projectId, applicationId, recipeId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("station-profiles")]
    [ProducesResponseType<StationProfileResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationProfileResponse>> CreateStationProfileAsync(
        string projectId,
        string applicationId,
        CreateApiStationProfileRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = EngineeringApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateStationProfileAsync(
                projectId,
                applicationId,
                EngineeringApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = EngineeringApiContractMapper.ToResponse(result.Value);
        return Created(
            $"{GetBasePath(projectId, applicationId)}/station-profiles/{Uri.EscapeDataString(response.StationProfileId)}",
            response);
    }

    [HttpGet("station-profiles")]
    [ProducesResponseType<IReadOnlyCollection<StationProfileResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<StationProfileResponse>>> ListStationProfilesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListStationProfilesAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(EngineeringApiContractMapper.ToResponse).ToArray());
    }

    [HttpGet("station-profiles/{stationProfileId}")]
    [ProducesResponseType<StationProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationProfileResponse>> GetStationProfileAsync(
        string projectId,
        string applicationId,
        string stationProfileId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetStationProfileAsync(
                projectId,
                applicationId,
                stationProfileId,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpPost("projects/{engineeringProjectId}/configuration-snapshots")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> PublishConfigurationSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        PublishApiConfigurationSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = EngineeringApiContractMapper.Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .PublishSnapshotAsync(
                projectId,
                applicationId,
                engineeringProjectId,
                EngineeringApiContractMapper.ToApplicationRequest(request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = EngineeringApiContractMapper.ToResponse(result.Value);
        return Created(
            $"{GetBasePath(projectId, applicationId)}/projects/{Uri.EscapeDataString(response.ProjectId)}/configuration-snapshots/{Uri.EscapeDataString(request.SnapshotId!)}",
            response);
    }

    [HttpPost("projects/{engineeringProjectId}/configuration-snapshots/{snapshotId}/rollback")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> RollbackConfigurationSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .RollbackSnapshotAsync(
                projectId,
                applicationId,
                engineeringProjectId,
                snapshotId,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    [HttpGet("projects/{engineeringProjectId}/configuration-snapshots/{fromSnapshotId}/diff/{toSnapshotId}")]
    [ProducesResponseType<ConfigurationSnapshotDiffResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConfigurationSnapshotDiffResponse>> CompareConfigurationSnapshotsAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .CompareSnapshotsAsync(
                projectId,
                applicationId,
                engineeringProjectId,
                fromSnapshotId,
                toSnapshotId,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(EngineeringApiContractMapper.ToResponse(result.Value));
    }

    private static string GetBasePath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/engineering";
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
