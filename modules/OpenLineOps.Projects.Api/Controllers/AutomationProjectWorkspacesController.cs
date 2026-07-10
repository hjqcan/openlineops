using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using ApiCreateWorkspaceRequest = OpenLineOps.Projects.Api.Models.CreateAutomationProjectWorkspaceRequest;
using ApiOpenWorkspaceRequest = OpenLineOps.Projects.Api.Models.OpenAutomationProjectWorkspaceRequest;
using AppCreateWorkspaceRequest = OpenLineOps.Projects.Application.ProjectWorkspaces.CreateAutomationProjectWorkspaceRequest;
using AppOpenWorkspaceRequest = OpenLineOps.Projects.Application.ProjectWorkspaces.OpenAutomationProjectWorkspaceRequest;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProjectsV1)]
[Route(OpenLineOpsApiRoutes.AutomationProjectWorkspaces)]
public sealed class AutomationProjectWorkspacesController : ControllerBase
{
    private readonly IAutomationProjectWorkspaceService _workspaceService;

    public AutomationProjectWorkspacesController(IAutomationProjectWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [HttpPost]
    [ProducesResponseType<AutomationProjectWorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectWorkspaceResponse>> CreateAsync(
        ApiCreateWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _workspaceService
            .CreateAsync(
                new AppCreateWorkspaceRequest(
                    request.ProjectId!,
                    request.DisplayName!,
                    request.ProjectPath!,
                    request.DefaultApplicationId,
                    request.DefaultApplicationName),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/automation-project-workspaces/open", response);
    }

    [HttpPost("open")]
    [ProducesResponseType<AutomationProjectWorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutomationProjectWorkspaceResponse>> OpenAsync(
        ApiOpenWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _workspaceService
            .OpenAsync(new AppOpenWorkspaceRequest(request.ProjectPath!), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPut("~/" + OpenLineOpsApiRoutes.AutomationProjects + "/{projectId}/manifest")]
    [ProducesResponseType<AutomationProjectWorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutomationProjectWorkspaceResponse>> SaveManifestAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var result = await _workspaceService
            .SaveManifestAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("~/" + OpenLineOpsApiRoutes.AutomationProjects + "/{projectId}/applications/import")]
    [ProducesResponseType<AutomationProjectWorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectWorkspaceResponse>> ImportApplicationAsync(
        string projectId,
        ImportProjectApplicationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProjectFilePath))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(request.ProjectFilePath)] = ["Value is required."]
            }));
        }

        var result = await _workspaceService
            .ImportApplicationAsync(
                projectId,
                new ImportAutomationProjectApplicationRequest(request.ProjectFilePath),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    private static AutomationProjectWorkspaceResponse ToResponse(AutomationProjectWorkspaceDetails workspace)
    {
        return new AutomationProjectWorkspaceResponse(
            ToResponse(workspace.Project),
            workspace.ManifestPath,
            ToResponse(workspace.Manifest));
    }

    private static AutomationProjectManifestResponse ToResponse(AutomationProjectManifest manifest)
    {
        return new AutomationProjectManifestResponse(
            manifest.FormatVersion,
            manifest.Product,
            manifest.ProjectId,
            manifest.DisplayName,
            manifest.ProjectPath,
            manifest.CreatedAtUtc,
            manifest.UpdatedAtUtc,
            manifest.ActiveSnapshotId,
            manifest.Applications
                .Select(ToResponse)
                .ToArray(),
            manifest.Snapshots
                .Select(ToResponse)
                .ToArray());
    }

    private static ProjectApplicationManifestResponse ToResponse(ProjectApplicationManifest application)
    {
        return new ProjectApplicationManifestResponse(
            application.ApplicationId,
            application.DisplayName,
            application.TopologyId,
            application.ProcessDefinitionIds,
            application.ProjectFilePath);
    }

    private static PublishedProjectSnapshotManifestResponse ToResponse(PublishedProjectSnapshotManifest snapshot)
    {
        return new PublishedProjectSnapshotManifestResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            snapshot.LayoutIds ?? [],
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.ConfigurationSnapshotId,
            snapshot.PublishedAtUtc,
            (snapshot.CapabilityBindings ?? []).Select(ToResponse).ToArray(),
            (snapshot.TargetReferences ?? []).Select(ToResponse).ToArray(),
            snapshot.BlockVersionIds ?? [],
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
    }

    private static AutomationProjectResponse ToResponse(AutomationProjectDetails project)
    {
        return new AutomationProjectResponse(
            project.ProjectId,
            project.DisplayName,
            project.ProjectPath,
            project.CreatedAtUtc,
            project.ActiveSnapshotId,
            project.Applications.Select(ToResponse).ToArray(),
            project.Snapshots.Select(ToResponse).ToArray());
    }

    private static ProjectApplicationResponse ToResponse(ProjectApplicationDetails application)
    {
        return new ProjectApplicationResponse(
            application.ApplicationId,
            application.DisplayName,
            application.TopologyId,
            application.ProcessDefinitionIds,
            application.ProjectFilePath);
    }

    private static PublishedProjectSnapshotResponse ToResponse(PublishedProjectSnapshotDetails snapshot)
    {
        return new PublishedProjectSnapshotResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            snapshot.LayoutIds,
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.ConfigurationSnapshotId,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings.Select(ToResponse).ToArray(),
            snapshot.TargetReferences.Select(ToResponse).ToArray(),
            snapshot.BlockVersionIds,
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
    }

    private static SnapshotCapabilityBindingResponse ToResponse(SnapshotCapabilityBindingDetails binding)
    {
        return new SnapshotCapabilityBindingResponse(
            binding.CapabilityId,
            binding.BindingId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static SnapshotCapabilityBindingResponse ToResponse(SnapshotCapabilityBindingManifest binding)
    {
        return new SnapshotCapabilityBindingResponse(
            binding.CapabilityId,
            binding.BindingId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static ProjectTargetReferenceResponse ToResponse(ProjectTargetReferenceDetails target)
    {
        return new ProjectTargetReferenceResponse(target.Kind, target.TargetId);
    }

    private static ProjectTargetReferenceResponse ToResponse(ProjectTargetReferenceManifest target)
    {
        return new ProjectTargetReferenceResponse(target.Kind, target.TargetId);
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

    private static Dictionary<string, string[]> Validate(ApiCreateWorkspaceRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProjectId), request.ProjectId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.ProjectPath), request.ProjectPath);

        if (!string.IsNullOrWhiteSpace(request.DefaultApplicationId))
        {
            AddRequired(errors, nameof(request.DefaultApplicationName), request.DefaultApplicationName);
        }

        if (!string.IsNullOrWhiteSpace(request.DefaultApplicationName))
        {
            AddRequired(errors, nameof(request.DefaultApplicationId), request.DefaultApplicationId);
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiOpenWorkspaceRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProjectPath), request.ProjectPath);

        return errors;
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
