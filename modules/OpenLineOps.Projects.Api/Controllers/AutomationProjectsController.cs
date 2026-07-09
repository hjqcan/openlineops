using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Projects;
using ApiAddApplicationRequest = OpenLineOps.Projects.Api.Models.AddProjectApplicationRequest;
using ApiCreateProjectRequest = OpenLineOps.Projects.Api.Models.CreateAutomationProjectRequest;
using ApiLinkProcessRequest = OpenLineOps.Projects.Api.Models.LinkProjectProcessDefinitionRequest;
using ApiLinkTopologyRequest = OpenLineOps.Projects.Api.Models.LinkProjectTopologyRequest;
using ApiPublishSnapshotRequest = OpenLineOps.Projects.Api.Models.PublishProjectSnapshotRequest;
using ApiSnapshotCapabilityBindingRequest = OpenLineOps.Projects.Api.Models.SnapshotCapabilityBindingRequest;
using ApiTargetReferenceRequest = OpenLineOps.Projects.Api.Models.ProjectTargetReferenceRequest;
using AppAddApplicationRequest = OpenLineOps.Projects.Application.Projects.AddProjectApplicationRequest;
using AppCreateProjectRequest = OpenLineOps.Projects.Application.Projects.CreateAutomationProjectRequest;
using AppLinkProcessRequest = OpenLineOps.Projects.Application.Projects.LinkProjectProcessDefinitionRequest;
using AppLinkTopologyRequest = OpenLineOps.Projects.Application.Projects.LinkProjectTopologyRequest;
using AppPublishSnapshotRequest = OpenLineOps.Projects.Application.Projects.PublishProjectSnapshotRequest;
using AppSnapshotCapabilityBindingRequest = OpenLineOps.Projects.Application.Projects.SnapshotCapabilityBindingRequest;
using AppTargetReferenceRequest = OpenLineOps.Projects.Application.Projects.ProjectTargetReferenceRequest;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProjectsV1)]
[Route(OpenLineOpsApiRoutes.AutomationProjects)]
public sealed class AutomationProjectsController : ControllerBase
{
    private readonly IAutomationProjectService _projectService;

    public AutomationProjectsController(IAutomationProjectService projectService)
    {
        _projectService = projectService;
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

        var result = await _projectService
            .PublishSnapshotAsync(projectId, ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/automation-projects/{response.ProjectId}/snapshots/{request.SnapshotId}", response);
    }

    private static AppPublishSnapshotRequest ToApplicationRequest(ApiPublishSnapshotRequest request)
    {
        return new AppPublishSnapshotRequest(
            request.SnapshotId!,
            request.ApplicationId!,
            request.TopologyId!,
            request.ProcessDefinitionId!,
            request.ProcessVersionId!,
            request.ConfigurationSnapshotId!,
            request.CapabilityBindings!
                .Select(binding => new AppSnapshotCapabilityBindingRequest(
                    binding.CapabilityId!,
                    binding.BindingId!,
                    binding.ProviderKind!,
                    binding.ProviderKey!))
                .ToArray(),
            request.TargetReferences!
                .Select(target => new AppTargetReferenceRequest(target.Kind!, target.TargetId!))
                .ToArray(),
            request.BlockVersionIds!);
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
            application.ProcessDefinitionIds);
    }

    private static PublishedProjectSnapshotResponse ToResponse(PublishedProjectSnapshotDetails snapshot)
    {
        return new PublishedProjectSnapshotResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
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
            snapshot.BlockVersionIds);
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
        AddRequired(errors, nameof(request.TopologyId), request.TopologyId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ProcessVersionId), request.ProcessVersionId);
        AddRequired(errors, nameof(request.ConfigurationSnapshotId), request.ConfigurationSnapshotId);

        ValidateCapabilityBindings(errors, request.CapabilityBindings);
        ValidateTargetReferences(errors, request.TargetReferences);

        if (request.BlockVersionIds is null)
        {
            errors[nameof(request.BlockVersionIds)] = ["BlockVersionIds collection is required."];
        }

        return errors;
    }

    private static void ValidateCapabilityBindings(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<ApiSnapshotCapabilityBindingRequest>? bindings)
    {
        if (bindings is null)
        {
            errors["CapabilityBindings"] = ["CapabilityBindings collection is required."];
            return;
        }

        var index = 0;
        foreach (var binding in bindings)
        {
            var prefix = $"CapabilityBindings[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(binding.CapabilityId)}", binding.CapabilityId);
            AddRequired(errors, $"{prefix}.{nameof(binding.BindingId)}", binding.BindingId);
            AddRequired(errors, $"{prefix}.{nameof(binding.ProviderKind)}", binding.ProviderKind);
            AddRequired(errors, $"{prefix}.{nameof(binding.ProviderKey)}", binding.ProviderKey);
            index++;
        }
    }

    private static void ValidateTargetReferences(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<ApiTargetReferenceRequest>? targets)
    {
        if (targets is null)
        {
            errors["TargetReferences"] = ["TargetReferences collection is required."];
            return;
        }

        var index = 0;
        foreach (var target in targets)
        {
            var prefix = $"TargetReferences[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(target.Kind)}", target.Kind);
            AddRequired(errors, $"{prefix}.{nameof(target.TargetId)}", target.TargetId);
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
