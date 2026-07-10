using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Application.Validation;
using CreateApiDefinitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessDefinitionRequest;
using CreateApiNodeRequest = OpenLineOps.Processes.Api.Models.CreateProcessNodeRequest;
using CreateApiTransitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessTransitionRequest;
using CreateApplicationDefinitionRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessDefinitionRequest;
using CreateApplicationNodeRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessNodeRequest;
using CreateApplicationTransitionRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessTransitionRequest;
using StartApplicationRuntimeSessionRequest = OpenLineOps.Processes.Application.Runtime.StartProcessRuntimeSessionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProcessesV1)]
[Route(OpenLineOpsApiRoutes.ProcessDefinitions)]
public sealed class ProcessDefinitionsController : ControllerBase
{
    private readonly IProcessDefinitionService _definitionService;
    private readonly IProcessRuntimeSessionLauncher _runtimeSessionLauncher;

    public ProcessDefinitionsController(
        IProcessDefinitionService definitionService,
        IProcessRuntimeSessionLauncher runtimeSessionLauncher)
    {
        _definitionService = definitionService;
        _runtimeSessionLauncher = runtimeSessionLauncher;
    }

    [HttpPost]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProcessDefinitionResponse>> CreateAsync(
        CreateApiDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _definitionService
            .CreateAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/process-definitions/{response.ProcessDefinitionId}", response);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProcessDefinitionSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessDefinitionSummaryResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Value.Select(ToSummaryResponse).ToArray());
    }

    [HttpGet("{processDefinitionId}")]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessDefinitionResponse>> GetByIdAsync(
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .GetByIdAsync(processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpGet("{processDefinitionId}/validation")]
    [ProducesResponseType<ProcessGraphValidationReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessGraphValidationReportResponse>> ValidateAsync(
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .ValidateAsync(processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{processDefinitionId}/publish")]
    [ProducesResponseType<ProcessDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessDefinitionResponse>> PublishAsync(
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _definitionService
            .PublishAsync(processDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpPost("{processDefinitionId}/runtime-sessions")]
    [DevelopmentRuntimeStartOnly]
    [ProducesResponseType<StartedProcessRuntimeSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StartedProcessRuntimeSessionResponse>> StartRuntimeSessionAsync(
        string processDefinitionId,
        Models.StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _runtimeSessionLauncher
            .StartAsync(
                processDefinitionId,
                new StartApplicationRuntimeSessionRequest(
                    request.ConfigurationSnapshotId!,
                    request.SerialNumber,
                    request.BatchId,
                    request.FixtureId,
                    request.DeviceId,
                    request.ActorId),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = new StartedProcessRuntimeSessionResponse(
            result.Value.SessionId,
            result.Value.ConfigurationSnapshotId,
            result.Value.Status,
            result.Value.CompletedSteps,
            result.Value.CommandCount,
            result.Value.IncidentCount);

        return Created($"/api/runtime/sessions/{response.SessionId}", response);
    }

    internal static CreateApplicationDefinitionRequest ToApplicationRequest(
        CreateApiDefinitionRequest request)
    {
        return new CreateApplicationDefinitionRequest(
            request.ProcessDefinitionId!,
            request.VersionId!,
            request.DisplayName!,
            request.Nodes!
                .Select(node => new CreateApplicationNodeRequest(
                    node.NodeId!,
                    node.Kind!,
                    node.DisplayName!,
                    node.RequiredCapability,
                    node.CommandName,
                    node.TimeoutSeconds,
                    node.InputPayload,
                    node.BlocklyWorkspaceJson,
                    node.ScriptSourceCode,
                    node.ScriptVersion))
                .ToArray(),
            request.Transitions!
                .Select(transition => new CreateApplicationTransitionRequest(
                    transition.TransitionId!,
                    transition.FromNodeId!,
                    transition.ToNodeId!,
                    transition.Label,
                    transition.LoopPolicy,
                    transition.MaxTraversals))
                .ToArray());
    }

    internal static ProcessDefinitionResponse ToResponse(ProcessDefinitionDetails definition)
    {
        return new ProcessDefinitionResponse(
            definition.ProcessDefinitionId,
            definition.VersionId,
            definition.DisplayName,
            definition.Status,
            definition.CreatedAtUtc,
            definition.PublishedAtUtc,
            definition.Nodes.Select(ToNodeResponse).ToArray(),
            definition.Transitions.Select(ToTransitionResponse).ToArray());
    }

    internal static ProcessDefinitionSummaryResponse ToSummaryResponse(ProcessDefinitionSummary summary)
    {
        return new ProcessDefinitionSummaryResponse(
            summary.ProcessDefinitionId,
            summary.VersionId,
            summary.DisplayName,
            summary.Status,
            summary.CreatedAtUtc,
            summary.PublishedAtUtc);
    }

    private static ProcessNodeResponse ToNodeResponse(ProcessNodeDetails node)
    {
        return new ProcessNodeResponse(
            node.NodeId,
            node.Kind,
            node.DisplayName,
            node.RequiredCapability,
            node.CommandName,
            node.TimeoutSeconds,
            node.InputPayload,
            node.ScriptLanguage,
            node.BlocklyWorkspaceJson,
            node.ScriptSourceCode,
            node.ScriptSourceHash,
            node.ScriptVersion);
    }

    private static ProcessTransitionResponse ToTransitionResponse(ProcessTransitionDetails transition)
    {
        return new ProcessTransitionResponse(
            transition.TransitionId,
            transition.FromNodeId,
            transition.ToNodeId,
            transition.Label,
            transition.LoopPolicy,
            transition.MaxTraversals);
    }

    internal static ProcessGraphValidationReportResponse ToResponse(
        ProcessGraphValidationReportDetails report)
    {
        return new ProcessGraphValidationReportResponse(
            report.IsValid,
            report.Issues
                .Select(issue => new ProcessGraphValidationIssueResponse(
                    issue.Severity,
                    issue.Code,
                    issue.Message))
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

    internal static Dictionary<string, string[]> Validate(CreateApiDefinitionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.VersionId), request.VersionId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        if (request.Nodes is null)
        {
            errors[nameof(request.Nodes)] = ["Nodes collection is required."];
        }
        else
        {
            ValidateNodes(errors, request.Nodes);
        }

        if (request.Transitions is null)
        {
            errors[nameof(request.Transitions)] = ["Transitions collection is required."];
        }
        else
        {
            ValidateTransitions(errors, request.Transitions);
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(Models.StartProcessRuntimeSessionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ConfigurationSnapshotId), request.ConfigurationSnapshotId);

        return errors;
    }

    private static void ValidateNodes(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiNodeRequest> nodes)
    {
        var index = 0;
        foreach (var node in nodes)
        {
            var prefix = $"Nodes[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(node.NodeId)}", node.NodeId);
            AddRequired(errors, $"{prefix}.{nameof(node.Kind)}", node.Kind);
            AddRequired(errors, $"{prefix}.{nameof(node.DisplayName)}", node.DisplayName);
            index++;
        }
    }

    private static void ValidateTransitions(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiTransitionRequest> transitions)
    {
        var index = 0;
        foreach (var transition in transitions)
        {
            var prefix = $"Transitions[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(transition.TransitionId)}", transition.TransitionId);
            AddRequired(errors, $"{prefix}.{nameof(transition.FromNodeId)}", transition.FromNodeId);
            AddRequired(errors, $"{prefix}.{nameof(transition.ToNodeId)}", transition.ToNodeId);
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
