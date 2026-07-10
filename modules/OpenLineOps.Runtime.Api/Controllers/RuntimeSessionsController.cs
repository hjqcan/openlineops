using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.RuntimeV1)]
[Route(OpenLineOpsApiRoutes.RuntimeSessions)]
public sealed class RuntimeSessionsController : ControllerBase
{
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IRuntimeSessionRecoveryService _recoveryService;

    public RuntimeSessionsController(
        IRuntimeSessionRepository sessionRepository,
        IRuntimeSessionRecoveryService recoveryService)
    {
        _sessionRepository = sessionRepository;
        _recoveryService = recoveryService;
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
                    step.FailureReason,
                    step.ActionId.Value,
                    step.TargetKind,
                    step.TargetId))
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
                    command.FailureReason,
                    command.ActionId.Value,
                    command.TargetKind,
                    command.TargetId))
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

}
