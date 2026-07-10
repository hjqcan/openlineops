using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Engineering.Api.Models;
using OpenLineOps.Engineering.Application.Configuration;
using CreateApiDeviceBindingRequest = OpenLineOps.Engineering.Api.Models.CreateDeviceBindingRequest;
using CreateApiEngineeringProjectRequest = OpenLineOps.Engineering.Api.Models.CreateEngineeringProjectRequest;
using CreateApiRecipeRequest = OpenLineOps.Engineering.Api.Models.CreateRecipeRequest;
using CreateApiStationProfileRequest = OpenLineOps.Engineering.Api.Models.CreateStationProfileRequest;
using CreateApiWorkspaceRequest = OpenLineOps.Engineering.Api.Models.CreateWorkspaceRequest;
using CreateApplicationDeviceBindingRequest = OpenLineOps.Engineering.Application.Configuration.CreateDeviceBindingRequest;
using CreateApplicationEngineeringProjectRequest = OpenLineOps.Engineering.Application.Configuration.CreateEngineeringProjectRequest;
using CreateApplicationRecipeRequest = OpenLineOps.Engineering.Application.Configuration.CreateRecipeRequest;
using CreateApplicationStationProfileRequest = OpenLineOps.Engineering.Application.Configuration.CreateStationProfileRequest;
using CreateApplicationWorkspaceRequest = OpenLineOps.Engineering.Application.Configuration.CreateWorkspaceRequest;
using PublishApiConfigurationSnapshotRequest = OpenLineOps.Engineering.Api.Models.PublishConfigurationSnapshotRequest;
using PublishApplicationConfigurationSnapshotRequest = OpenLineOps.Engineering.Application.Configuration.PublishConfigurationSnapshotRequest;
using RecipeApplicationParameterRequest = OpenLineOps.Engineering.Application.Configuration.RecipeParameterRequest;

namespace OpenLineOps.Engineering.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.EngineeringV1)]
[Route(OpenLineOpsApiRoutes.Engineering)]
public sealed class EngineeringConfigurationController : ControllerBase
{
    private readonly IEngineeringConfigurationService _configurationService;

    public EngineeringConfigurationController(IEngineeringConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    [HttpPost("workspaces")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkspaceResponse>> CreateWorkspaceAsync(
        CreateApiWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateWorkspaceAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/engineering/workspaces/{response.WorkspaceId}", response);
    }

    [HttpGet("workspaces")]
    [ProducesResponseType<IReadOnlyCollection<WorkspaceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<WorkspaceResponse>>> ListWorkspacesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("workspaces/{workspaceId}")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceResponse>> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetWorkspaceAsync(workspaceId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("projects")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> CreateProjectAsync(
        CreateApiEngineeringProjectRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateProjectAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/engineering/projects/{response.ProjectId}", response);
    }

    [HttpGet("projects")]
    [ProducesResponseType<IReadOnlyCollection<EngineeringProjectResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<EngineeringProjectResponse>>> ListProjectsAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListProjectsAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("projects/{projectId}")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EngineeringProjectResponse>> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("recipes")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeResponse>> CreateRecipeAsync(
        CreateApiRecipeRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateRecipeAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/engineering/recipes/{response.RecipeId}", response);
    }

    [HttpGet("recipes")]
    [ProducesResponseType<IReadOnlyCollection<RecipeResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RecipeResponse>>> ListRecipesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListRecipesAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("recipes/{recipeId}")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeResponse>> GetRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetRecipeAsync(recipeId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("recipes/{recipeId}/publish")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeResponse>> PublishRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .PublishRecipeAsync(recipeId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("station-profiles")]
    [ProducesResponseType<StationProfileResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationProfileResponse>> CreateStationProfileAsync(
        CreateApiStationProfileRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateStationProfileAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/engineering/station-profiles/{response.StationProfileId}", response);
    }

    [HttpGet("station-profiles")]
    [ProducesResponseType<IReadOnlyCollection<StationProfileResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<StationProfileResponse>>> ListStationProfilesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ListStationProfilesAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("station-profiles/{stationProfileId}")]
    [ProducesResponseType<StationProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationProfileResponse>> GetStationProfileAsync(
        string stationProfileId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetStationProfileAsync(stationProfileId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("projects/{projectId}/configuration-snapshots")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> PublishConfigurationSnapshotAsync(
        string projectId,
        PublishApiConfigurationSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .PublishSnapshotAsync(projectId, ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created(
            $"/api/engineering/projects/{response.ProjectId}/configuration-snapshots/{request.SnapshotId}",
            response);
    }

    [HttpPost("projects/{projectId}/configuration-snapshots/{snapshotId}/rollback")]
    [ProducesResponseType<EngineeringProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringProjectResponse>> RollbackConfigurationSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .RollbackSnapshotAsync(projectId, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpGet("projects/{projectId}/configuration-snapshots/{fromSnapshotId}/diff/{toSnapshotId}")]
    [ProducesResponseType<ConfigurationSnapshotDiffResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConfigurationSnapshotDiffResponse>> CompareConfigurationSnapshotsAsync(
        string projectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .CompareSnapshotsAsync(projectId, fromSnapshotId, toSnapshotId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    internal static CreateApplicationEngineeringProjectRequest ToApplicationRequest(
        CreateApiEngineeringProjectRequest request)
    {
        return new CreateApplicationEngineeringProjectRequest(
            request.ProjectId!,
            request.WorkspaceId!,
            request.DisplayName!);
    }

    internal static CreateApplicationWorkspaceRequest ToApplicationRequest(CreateApiWorkspaceRequest request)
    {
        return new CreateApplicationWorkspaceRequest(
            request.WorkspaceId!,
            request.DisplayName!);
    }

    internal static CreateApplicationRecipeRequest ToApplicationRequest(CreateApiRecipeRequest request)
    {
        return new CreateApplicationRecipeRequest(
            request.RecipeId!,
            request.VersionId!,
            request.DisplayName!,
            request.Parameters!
                .Select(parameter => new RecipeApplicationParameterRequest(
                    parameter.Key!,
                    parameter.Value!))
                .ToArray());
    }

    internal static CreateApplicationStationProfileRequest ToApplicationRequest(
        CreateApiStationProfileRequest request)
    {
        return new CreateApplicationStationProfileRequest(
            request.StationProfileId!,
            request.DisplayName!,
            request.DeviceBindings!
                .Select(binding => new CreateApplicationDeviceBindingRequest(
                    binding.DeviceBindingId!,
                    binding.CapabilityId!,
                    binding.DeviceKey!))
                .ToArray());
    }

    internal static PublishApplicationConfigurationSnapshotRequest ToApplicationRequest(
        PublishApiConfigurationSnapshotRequest request)
    {
        return new PublishApplicationConfigurationSnapshotRequest(
            request.SnapshotId!,
            request.ProcessDefinitionId!,
            request.ProcessVersionId!,
            request.RecipeId!,
            request.StationProfileId!);
    }

    internal static EngineeringProjectResponse ToResponse(EngineeringProjectDetails project)
    {
        return new EngineeringProjectResponse(
            project.ProjectId,
            project.WorkspaceId,
            project.DisplayName,
            project.CreatedAtUtc,
            project.ActiveSnapshotId,
            project.Snapshots.Select(ToResponse).ToArray());
    }

    internal static WorkspaceResponse ToResponse(WorkspaceDetails workspace)
    {
        return new WorkspaceResponse(
            workspace.WorkspaceId,
            workspace.DisplayName,
            workspace.CreatedAtUtc);
    }

    internal static ConfigurationSnapshotResponse ToResponse(ConfigurationSnapshotDetails snapshot)
    {
        return new ConfigurationSnapshotResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.RecipeId,
            snapshot.RecipeVersionId,
            snapshot.StationProfileId,
            snapshot.Status,
            snapshot.PublishedAtUtc,
            snapshot.DeviceBindings
                .Select(binding => new DeviceBindingSnapshotResponse(
                    binding.DeviceBindingId,
                    binding.CapabilityId,
                    binding.DeviceKey))
                .ToArray());
    }

    internal static RecipeResponse ToResponse(RecipeDetails recipe)
    {
        return new RecipeResponse(
            recipe.RecipeId,
            recipe.VersionId,
            recipe.DisplayName,
            recipe.Status,
            recipe.CreatedAtUtc,
            recipe.PublishedAtUtc,
            recipe.Parameters
                .Select(parameter => new RecipeParameterResponse(parameter.Key, parameter.Value))
                .ToArray());
    }

    internal static StationProfileResponse ToResponse(StationProfileDetails stationProfile)
    {
        return new StationProfileResponse(
            stationProfile.StationProfileId,
            stationProfile.DisplayName,
            stationProfile.DeviceBindings
                .Select(binding => new DeviceBindingResponse(
                    binding.DeviceBindingId,
                    binding.CapabilityId,
                    binding.DeviceKey))
                .ToArray());
    }

    internal static ConfigurationSnapshotDiffResponse ToResponse(
        ConfigurationSnapshotDiffDetails diff)
    {
        return new ConfigurationSnapshotDiffResponse(
            diff.ProjectId,
            diff.FromSnapshotId,
            diff.ToSnapshotId,
            diff.Changes
                .Select(change => new ConfigurationSnapshotDiffItemResponse(
                    change.Area,
                    change.Field,
                    change.FromValue,
                    change.ToValue,
                    change.ChangeType))
                .ToArray());
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

    internal static Dictionary<string, string[]> Validate(CreateApiEngineeringProjectRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProjectId), request.ProjectId);
        AddRequired(errors, nameof(request.WorkspaceId), request.WorkspaceId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        return errors;
    }

    internal static Dictionary<string, string[]> Validate(CreateApiWorkspaceRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.WorkspaceId), request.WorkspaceId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        return errors;
    }

    internal static Dictionary<string, string[]> Validate(CreateApiRecipeRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.RecipeId), request.RecipeId);
        AddRequired(errors, nameof(request.VersionId), request.VersionId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        if (request.Parameters is null)
        {
            errors[nameof(request.Parameters)] = ["Parameters collection is required."];
        }
        else
        {
            ValidateRecipeParameters(errors, request.Parameters);
        }

        return errors;
    }

    internal static Dictionary<string, string[]> Validate(CreateApiStationProfileRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.StationProfileId), request.StationProfileId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        if (request.DeviceBindings is null)
        {
            errors[nameof(request.DeviceBindings)] = ["DeviceBindings collection is required."];
        }
        else
        {
            ValidateDeviceBindings(errors, request.DeviceBindings);
        }

        return errors;
    }

    internal static Dictionary<string, string[]> Validate(
        PublishApiConfigurationSnapshotRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.SnapshotId), request.SnapshotId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ProcessVersionId), request.ProcessVersionId);
        AddRequired(errors, nameof(request.RecipeId), request.RecipeId);
        AddRequired(errors, nameof(request.StationProfileId), request.StationProfileId);

        return errors;
    }

    private static void ValidateRecipeParameters(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<Models.RecipeParameterRequest> parameters)
    {
        var index = 0;
        foreach (var parameter in parameters)
        {
            var prefix = $"Parameters[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(parameter.Key)}", parameter.Key);
            AddRequired(errors, $"{prefix}.{nameof(parameter.Value)}", parameter.Value);
            index++;
        }
    }

    private static void ValidateDeviceBindings(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiDeviceBindingRequest> deviceBindings)
    {
        var index = 0;
        foreach (var binding in deviceBindings)
        {
            var prefix = $"DeviceBindings[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(binding.DeviceBindingId)}", binding.DeviceBindingId);
            AddRequired(errors, $"{prefix}.{nameof(binding.CapabilityId)}", binding.CapabilityId);
            AddRequired(errors, $"{prefix}.{nameof(binding.DeviceKey)}", binding.DeviceKey);
            index++;
        }
    }

    private static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }
}
