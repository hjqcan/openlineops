using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.RuntimeV1)]
[Route(OpenLineOpsApiRoutes.RuntimeSessions)]
public sealed class RuntimeSessionsController : ControllerBase
{
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IRuntimeSessionRecoveryService _recoveryService;

    public RuntimeSessionsController(
        IRuntimeSessionRunner sessionRunner,
        IRuntimeSessionRepository sessionRepository,
        IRuntimeSessionRecoveryService recoveryService)
    {
        _sessionRunner = sessionRunner;
        _sessionRepository = sessionRepository;
        _recoveryService = recoveryService;
    }

    [HttpPost("simulated")]
    [ProducesResponseType<RuntimeSessionRunResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeSessionRunResponse>> StartSimulatedAsync(
        StartSimulatedRuntimeSessionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var startRequest = ToApplicationRequest(request);
        var result = await _sessionRunner
            .RunAsync(startRequest, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToRunResponse(result.Value);

        return Created($"/api/runtime/sessions/{response.SessionId}", response);
    }

    [HttpGet("{sessionId:guid}")]
    [ProducesResponseType<RuntimeSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeSessionResponse>> GetByIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest();
        }

        var session = await _sessionRepository
            .GetByIdAsync(new RuntimeSessionId(sessionId), cancellationToken)
            .ConfigureAwait(false);

        return session is null
            ? NotFound()
            : Ok(ToSessionResponse(session));
    }

    [HttpGet("recovery-plan")]
    [ProducesResponseType<RuntimeRecoveryPlanResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeRecoveryPlanResponse>> GetRecoveryPlanAsync(
        CancellationToken cancellationToken)
    {
        var plan = await _recoveryService
            .CreateRecoveryPlanAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeRecoveryPlanResponse(
            plan.Count,
            plan.Candidates
                .Select(candidate => new RuntimeRecoveryCandidateResponse(
                    candidate.SessionId.Value,
                    candidate.StationId.Value,
                    candidate.ProcessVersionId.Value,
                    candidate.ConfigurationSnapshotId.Value,
                    candidate.RecipeSnapshotId.Value,
                    candidate.Status.ToString(),
                    candidate.LastTransitionAtUtc,
                    candidate.RecoveryReason))
                .ToArray()));
    }

    private static StartRuntimeSessionRequest ToApplicationRequest(StartSimulatedRuntimeSessionRequest request)
    {
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId(request.ProcessDefinitionId),
            new ProcessVersionId(request.ProcessVersionId),
            request.Nodes
                .Select(node => new ExecutableRuntimeNode(
                    new RuntimeNodeId(node.NodeId),
                    node.DisplayName,
                    new RuntimeCapabilityId(node.TargetCapability),
                    node.CommandName,
                    TimeSpan.FromSeconds(node.TimeoutSeconds),
                    node.InputPayload))
                .ToArray());

        return new StartRuntimeSessionRequest(
            new StationId(request.StationId),
            new ConfigurationSnapshotId(request.ConfigurationSnapshotId),
            new RecipeSnapshotId(request.RecipeSnapshotId),
            process,
            new RuntimeSessionTraceMetadata(
                request.SerialNumber,
                request.BatchId,
                request.FixtureId,
                request.DeviceId,
                request.ActorId));
    }

    private static RuntimeSessionRunResponse ToRunResponse(RuntimeSessionRunResult result)
    {
        return new RuntimeSessionRunResponse(
            result.SessionId.Value,
            result.ConfigurationSnapshotId.Value,
            result.Status.ToString(),
            result.CompletedSteps,
            result.CommandCount,
            result.IncidentCount);
    }

    private static RuntimeSessionResponse ToSessionResponse(RuntimeSession session)
    {
        return new RuntimeSessionResponse(
            session.Id.Value,
            session.StationId.Value,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.SerialNumber,
            session.TraceMetadata.BatchId,
            session.TraceMetadata.FixtureId,
            session.TraceMetadata.DeviceId,
            session.TraceMetadata.ActorId,
            session.Status.ToString(),
            session.CreatedAtUtc,
            session.LastTransitionAtUtc,
            session.StartedAtUtc,
            session.CompletedAtUtc,
            session.Steps
                .Select(step => new RuntimeStepResponse(
                    step.Id.Value,
                    step.NodeId.Value,
                    step.DisplayName,
                    step.Status.ToString(),
                    step.StartedAtUtc,
                    step.CompletedAtUtc,
                    step.FailureReason))
                .ToArray(),
            session.Commands
                .Select(command => new RuntimeCommandResponse(
                    command.Id.Value,
                    command.StepId.Value,
                    command.TargetCapability.Value,
                    command.CommandName,
                    command.Status.ToString(),
                    command.CreatedAtUtc,
                    command.DeadlineAtUtc,
                    command.CompletedAtUtc,
                    command.ResultPayload,
                    command.FailureReason))
                .ToArray(),
            session.Incidents
                .Select(incident => new RuntimeIncidentResponse(
                    incident.Id.Value,
                    incident.Severity.ToString(),
                    incident.Code,
                    incident.Message,
                    incident.OccurredAtUtc))
                .ToArray());
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.StartsWith("Validation.", StringComparison.Ordinal)
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }

    private static Dictionary<string, string[]> Validate(StartSimulatedRuntimeSessionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.StationId), request.StationId);
        AddRequired(errors, nameof(request.ConfigurationSnapshotId), request.ConfigurationSnapshotId);
        AddRequired(errors, nameof(request.RecipeSnapshotId), request.RecipeSnapshotId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ProcessVersionId), request.ProcessVersionId);

        if (request.Nodes is null || request.Nodes.Count == 0)
        {
            errors[nameof(request.Nodes)] = ["At least one runtime node is required."];
            return errors;
        }

        for (var index = 0; index < request.Nodes.Count; index++)
        {
            var node = request.Nodes[index];
            var prefix = $"{nameof(request.Nodes)}[{index}]";

            AddRequired(errors, $"{prefix}.{nameof(node.NodeId)}", node.NodeId);
            AddRequired(errors, $"{prefix}.{nameof(node.DisplayName)}", node.DisplayName);
            AddRequired(errors, $"{prefix}.{nameof(node.TargetCapability)}", node.TargetCapability);
            AddRequired(errors, $"{prefix}.{nameof(node.CommandName)}", node.CommandName);

            if (node.TimeoutSeconds <= 0)
            {
                errors[$"{prefix}.{nameof(node.TimeoutSeconds)}"] = ["TimeoutSeconds must be greater than zero."];
            }
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
