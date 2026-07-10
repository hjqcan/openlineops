using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using ApiAddApplicationRequest = OpenLineOps.Projects.Api.Models.AddProjectApplicationRequest;
using ApiCreateProjectRequest = OpenLineOps.Projects.Api.Models.CreateAutomationProjectRequest;
using ApiLinkProcessRequest = OpenLineOps.Projects.Api.Models.LinkProjectProcessDefinitionRequest;
using ApiLinkTopologyRequest = OpenLineOps.Projects.Api.Models.LinkProjectTopologyRequest;
using ApiPublishSnapshotRequest = OpenLineOps.Projects.Api.Models.PublishProjectSnapshotRequest;
using AppAddApplicationRequest = OpenLineOps.Projects.Application.Projects.AddProjectApplicationRequest;
using AppCreateProjectRequest = OpenLineOps.Projects.Application.Projects.CreateAutomationProjectRequest;
using AppLinkProcessRequest = OpenLineOps.Projects.Application.Projects.LinkProjectProcessDefinitionRequest;
using AppLinkTopologyRequest = OpenLineOps.Projects.Application.Projects.LinkProjectTopologyRequest;
using AppStartProcessRuntimeSessionRequest = OpenLineOps.Processes.Application.Runtime.StartProcessRuntimeSessionRequest;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProjectsV1)]
[Route(OpenLineOpsApiRoutes.AutomationProjects)]
public sealed class AutomationProjectsController : ControllerBase
{
    private readonly IAutomationProjectService _projectService;
    private readonly IProjectReleasePublisher _releasePublisher;
    private readonly IProjectReleaseRuntimeSessionLauncher _runtimeSessionLauncher;

    public AutomationProjectsController(
        IAutomationProjectService projectService,
        IProjectReleasePublisher releasePublisher,
        IProjectReleaseRuntimeSessionLauncher runtimeSessionLauncher)
    {
        _projectService = projectService;
        _releasePublisher = releasePublisher;
        _runtimeSessionLauncher = runtimeSessionLauncher;
    }

    [HttpPost]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectResponse>> CreateAsync(
        ApiCreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _projectService
            .CreateAsync(
                new AppCreateProjectRequest(request.ProjectId!, request.DisplayName!, request.ProjectPath!),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/automation-projects/{response.ProjectId}", response);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<AutomationProjectSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<AutomationProjectSummaryResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await _projectService.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result.Value.Select(ToSummaryResponse).ToArray());
    }

    [HttpGet("{projectId}")]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutomationProjectResponse>> GetByIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var result = await _projectService.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{projectId}/applications")]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectResponse>> AddApplicationAsync(
        string projectId,
        ApiAddApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _projectService
            .AddApplicationAsync(
                projectId,
                new AppAddApplicationRequest(request.ApplicationId!, request.DisplayName!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPut("{projectId}/applications/{applicationId}/topology")]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectResponse>> LinkTopologyAsync(
        string projectId,
        string applicationId,
        ApiLinkTopologyRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _projectService
            .LinkTopologyAsync(
                projectId,
                new AppLinkTopologyRequest(applicationId, request.TopologyId!),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPut("{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}")]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectResponse>> LinkProcessDefinitionAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _projectService
            .LinkProcessDefinitionAsync(
                projectId,
                new AppLinkProcessRequest(applicationId, processDefinitionId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{projectId}/snapshots")]
    [ProducesResponseType<AutomationProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutomationProjectResponse>> PublishSnapshotAsync(
        string projectId,
        ApiPublishSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _releasePublisher
            .PublishAsync(
                projectId,
                new PublishProjectReleaseRequest(
                    request.SnapshotId!,
                    request.ApplicationId!,
                    request.ProcessDefinitionId!,
                    request.ConfigurationSnapshotId!),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/automation-projects/{response.ProjectId}/snapshots/{request.SnapshotId}", response);
    }

    [HttpPost("{projectId}/snapshots/{snapshotId}/runtime-sessions")]
    [ProducesResponseType<StartedProjectSnapshotRuntimeSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StartedProjectSnapshotRuntimeSessionResponse>> StartSnapshotRuntimeSessionAsync(
        string projectId,
        string snapshotId,
        StartProjectSnapshotRuntimeSessionRequest? request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var projectResult = await _projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return ToProblem(projectResult.Error);
        }

        var snapshot = projectResult.Value.Snapshots
            .SingleOrDefault(candidate => string.Equals(
                candidate.SnapshotId,
                snapshotId,
                StringComparison.Ordinal));
        if (snapshot is null)
        {
            return ToProblem(ApplicationError.NotFound(
                "Projects.ProjectSnapshotNotFound",
                $"Project snapshot {snapshotId} was not found in automation project {projectId}."));
        }

        var startRequest = request!;
        var startResult = await _runtimeSessionLauncher
            .StartAsync(
                snapshot,
                new AppStartProcessRuntimeSessionRequest(
                    snapshot.ConfigurationSnapshotId,
                    startRequest.SerialNumber,
                    startRequest.BatchId,
                    startRequest.FixtureId,
                    startRequest.DeviceId,
                    startRequest.ActorId,
                    snapshot.ProjectId,
                    snapshot.ApplicationId,
                    snapshot.SnapshotId,
                    snapshot.TopologyId),
                cancellationToken)
            .ConfigureAwait(false);

        if (startResult.IsFailure)
        {
            return ToProblem(startResult.Error);
        }

        var response = new StartedProjectSnapshotRuntimeSessionResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            startResult.Value.SessionId,
            startResult.Value.ConfigurationSnapshotId,
            startResult.Value.Status,
            startResult.Value.CompletedSteps,
            startResult.Value.CommandCount,
            startResult.Value.IncidentCount);

        return Created($"/api/runtime/sessions/{response.SessionId}", response);
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

    private static AutomationProjectSummaryResponse ToSummaryResponse(AutomationProjectSummary project)
    {
        return new AutomationProjectSummaryResponse(
            project.ProjectId,
            project.DisplayName,
            project.ProjectPath,
            project.ActiveSnapshotId);
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
            snapshot.CapabilityBindings
                .Select(binding => new SnapshotCapabilityBindingResponse(
                    binding.CapabilityId,
                    binding.BindingId,
                    binding.ProviderKind,
                    binding.ProviderKey))
                .ToArray(),
            snapshot.TargetReferences
                .Select(target => new ProjectTargetReferenceResponse(target.Kind, target.TargetId))
                .ToArray(),
            snapshot.BlockVersionIds,
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
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

    private static Dictionary<string, string[]> Validate(ApiCreateProjectRequest? request)
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

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiAddApplicationRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ApplicationId), request.ApplicationId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiLinkTopologyRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.TopologyId), request.TopologyId);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiPublishSnapshotRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.SnapshotId), request.SnapshotId);
        AddRequired(errors, nameof(request.ApplicationId), request.ApplicationId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ConfigurationSnapshotId), request.ConfigurationSnapshotId);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(StartProjectSnapshotRuntimeSessionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
        }

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
