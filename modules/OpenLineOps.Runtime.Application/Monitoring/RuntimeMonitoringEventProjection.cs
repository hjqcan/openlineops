using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeMonitoringEventProjection(
    long Sequence,
    RuntimeTimelineEntry TimelineEntry,
    RuntimeStationStatusProjection StationStatus,
    bool ResetStationTargets,
    RuntimeTargetStatusProjection? TargetStatus,
    RuntimeAlarmProjection? Alarm)
{
    public static RuntimeMonitoringEventProjection Create(
        long sequence,
        RuntimeSession session,
        IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence must be positive.");
        }

        if (TryGetSessionId(domainEvent) != session.Id)
        {
            throw new InvalidOperationException(
                $"Runtime monitoring event {domainEvent.EventId:D} does not belong to session {session.Id}.");
        }

        var alarm = domainEvent is RuntimeIncidentRecordedDomainEvent incidentEvent
            ? ToAlarm(session, incidentEvent)
            : null;
        var targetStatus = domainEvent is RuntimeCommandStatusChangedDomainEvent commandStatusEvent
            ? RuntimeTargetStatusProjection.FromCommandStatusChanged(session, commandStatusEvent)
            : null;

        return new RuntimeMonitoringEventProjection(
            sequence,
            ToTimelineEntry(sequence, domainEvent, session),
            ToStationStatus(session),
            domainEvent is RuntimeSessionCreatedDomainEvent,
            targetStatus,
            alarm);
    }

    public static bool IsRuntimeSessionEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return TryGetSessionId(domainEvent) is not null;
    }

    private static RuntimeAlarmProjection ToAlarm(
        RuntimeSession session,
        RuntimeIncidentRecordedDomainEvent incidentEvent)
    {
        var incident = session.Incidents.SingleOrDefault(candidate => candidate.Id == incidentEvent.IncidentId)
            ?? throw new InvalidOperationException(
                $"Runtime incident {incidentEvent.IncidentId} was not found in session {session.Id}.");

        return new RuntimeAlarmProjection(
            incident.Id,
            session.Id,
            session.StationId.Value,
            incident.Severity,
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc,
            IsAcknowledged: false,
            AcknowledgedBy: null,
            AcknowledgedAtUtc: null);
    }

    private static RuntimeTimelineEntry ToTimelineEntry(
        long sequence,
        IDomainEvent domainEvent,
        RuntimeSession session)
    {
        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent sessionCreated => CreateTimelineEntry(
                sequence,
                sessionCreated,
                session,
                entityKind: "Session",
                entityId: sessionCreated.SessionId.Value.ToString("D")),
            RuntimeSessionStatusChangedDomainEvent sessionStatus => CreateTimelineEntry(
                sequence,
                sessionStatus,
                session,
                entityKind: "Session",
                entityId: sessionStatus.SessionId.Value.ToString("D"),
                fromStatus: sessionStatus.FromStatus.ToString(),
                toStatus: sessionStatus.ToStatus.ToString(),
                reason: sessionStatus.Reason),
            RuntimeStepStatusChangedDomainEvent stepStatus => CreateTimelineEntry(
                sequence,
                stepStatus,
                session,
                entityKind: "Step",
                entityId: stepStatus.StepId.Value.ToString("D"),
                fromStatus: stepStatus.FromStatus.ToString(),
                toStatus: stepStatus.ToStatus.ToString()),
            RuntimeCommandStatusChangedDomainEvent commandStatus => CreateTimelineEntry(
                sequence,
                commandStatus,
                session,
                entityKind: "Command",
                entityId: commandStatus.CommandId.Value.ToString("D"),
                fromStatus: commandStatus.FromStatus.ToString(),
                toStatus: commandStatus.ToStatus.ToString(),
                reason: commandStatus.Reason),
            RuntimeIncidentRecordedDomainEvent incidentRecorded => CreateTimelineEntry(
                sequence,
                incidentRecorded,
                session,
                entityKind: "Incident",
                entityId: incidentRecorded.IncidentId.Value.ToString("D"),
                severity: incidentRecorded.Severity.ToString(),
                code: incidentRecorded.Code),
            _ => throw new InvalidOperationException(
                $"Domain event {domainEvent.GetType().FullName} is not a Runtime Session event.")
        };
    }

    private static RuntimeTimelineEntry CreateTimelineEntry(
        long sequence,
        IDomainEvent domainEvent,
        RuntimeSession session,
        string entityKind,
        string? entityId = null,
        string? fromStatus = null,
        string? toStatus = null,
        string? reason = null,
        string? severity = null,
        string? code = null)
    {
        return new RuntimeTimelineEntry(
            sequence,
            domainEvent.EventId,
            domainEvent.OccurredAtUtc,
            domainEvent.EventName,
            session.Id,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            session.TraceMetadata.ProductionUnitIdentity,
            session.StationId.Value,
            entityKind,
            entityId,
            fromStatus,
            toStatus,
            reason,
            severity,
            code,
            session.Status);
    }

    private static RuntimeStationStatusProjection ToStationStatus(RuntimeSession session)
    {
        return new RuntimeStationStatusProjection(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            session.TraceMetadata.ProductionUnitIdentity,
            session.StationId.Value,
            session.Id,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            session.TraceMetadata.LotId,
            session.TraceMetadata.CarrierId,
            session.TraceMetadata.FixtureId,
            session.TraceMetadata.DeviceId,
            session.Status,
            session.Steps.Count,
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Running),
            session.Commands.Count,
            session.Incidents.Count,
            session.LastTransitionAtUtc,
            session.IsTerminal);
    }

    private static RuntimeSessionId? TryGetSessionId(IDomainEvent domainEvent)
    {
        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeSessionStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeStepStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeCommandStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeIncidentRecordedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            _ => null
        };
    }
}
