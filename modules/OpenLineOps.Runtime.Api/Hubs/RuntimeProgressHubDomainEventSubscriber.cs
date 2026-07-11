using Microsoft.AspNetCore.SignalR;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Api.Hubs;

public sealed class RuntimeProgressHubDomainEventSubscriber : IRuntimeDomainEventSubscriber
{
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IHubContext<RuntimeProgressHub, IRuntimeProgressClient> _hubContext;
    private long _sequence;

    public RuntimeProgressHubDomainEventSubscriber(
        IRuntimeSessionRepository sessionRepository,
        IHubContext<RuntimeProgressHub, IRuntimeProgressClient> hubContext)
    {
        _sessionRepository = sessionRepository;
        _hubContext = hubContext;
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        var runtimeEvents = domainEvents
            .Select(domainEvent => new RuntimeEventEnvelope(domainEvent, TryGetSessionId(domainEvent)))
            .Where(envelope => envelope.SessionId is not null)
            .GroupBy(envelope => envelope.SessionId!.Value.Value);

        foreach (var eventGroup in runtimeEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = await _sessionRepository
                .GetByIdAsync(new RuntimeSessionId(eventGroup.Key), cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
            {
                continue;
            }

            var stationStatus = ToStationStatus(session);
            await BroadcastStationStatusAsync(stationStatus, session, cancellationToken).ConfigureAwait(false);

            foreach (var envelope in eventGroup)
            {
                var timelineEntry = ToTimelineEntry(envelope.DomainEvent, session);
                await BroadcastRuntimeEventAsync(timelineEntry, session, cancellationToken).ConfigureAwait(false);

                if (envelope.DomainEvent is RuntimeCommandStatusChangedDomainEvent commandStatusEvent)
                {
                    var targetStatus = ToTargetStatus(session, commandStatusEvent);
                    await BroadcastTargetStatusAsync(targetStatus, session, cancellationToken).ConfigureAwait(false);
                }

                if (envelope.DomainEvent is RuntimeIncidentRecordedDomainEvent incidentEvent)
                {
                    var alarm = ToAlarmResponse(session, incidentEvent);
                    if (alarm is not null)
                    {
                        await BroadcastAlarmAsync(alarm, session, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task BroadcastTargetStatusAsync(
        RuntimeTargetStatusResponse status,
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        await _hubContext.Clients.Group(ProductionRunGroup(session))
            .TargetStatusChanged(status)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(RuntimeProgressHub.SessionGroup(session.Id.Value))
            .TargetStatusChanged(status)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(StationSystemGroup(session))
            .TargetStatusChanged(status)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BroadcastStationStatusAsync(
        RuntimeStationStatusResponse status,
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        await _hubContext.Clients.Group(ProductionRunGroup(session))
            .StationStatusChanged(status)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(StationSystemGroup(session))
            .StationStatusChanged(status)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BroadcastRuntimeEventAsync(
        RuntimeTimelineEntryResponse entry,
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        await _hubContext.Clients.Group(ProductionRunGroup(session))
            .RuntimeEvent(entry)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(RuntimeProgressHub.SessionGroup(session.Id.Value))
            .RuntimeEvent(entry)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(StationSystemGroup(session))
            .RuntimeEvent(entry)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BroadcastAlarmAsync(
        RuntimeAlarmResponse alarm,
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        await _hubContext.Clients.All
            .AlarmRaised(alarm)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _hubContext.Clients.Group(StationSystemGroup(session))
            .AlarmRaised(alarm)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private RuntimeTimelineEntryResponse ToTimelineEntry(IDomainEvent domainEvent, RuntimeSession session)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var baseEntry = new RuntimeTimelineEntryResponse(
            sequence,
            domainEvent.EventId,
            domainEvent.OccurredAtUtc,
            domainEvent.EventName,
            session.Id.Value,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId.Value,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            new RuntimeProductionUnitIdentityResponse(
                session.TraceMetadata.ProductionUnitIdentity.ModelId,
                session.TraceMetadata.ProductionUnitIdentity.InputKey,
                session.TraceMetadata.ProductionUnitIdentity.Value),
            session.StationId.Value,
            "Unknown",
            null,
            null,
            null,
            null,
            null,
            null,
            session.Status.ToString());

        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent sessionCreated => baseEntry with
            {
                EntityKind = "Session",
                EntityId = sessionCreated.SessionId.Value.ToString("D")
            },
            RuntimeSessionStatusChangedDomainEvent sessionStatus => baseEntry with
            {
                EntityKind = "Session",
                EntityId = sessionStatus.SessionId.Value.ToString("D"),
                FromStatus = sessionStatus.FromStatus.ToString(),
                ToStatus = sessionStatus.ToStatus.ToString(),
                Reason = sessionStatus.Reason
            },
            RuntimeStepStatusChangedDomainEvent stepStatus => baseEntry with
            {
                EntityKind = "Step",
                EntityId = stepStatus.StepId.Value.ToString("D"),
                FromStatus = stepStatus.FromStatus.ToString(),
                ToStatus = stepStatus.ToStatus.ToString()
            },
            RuntimeCommandStatusChangedDomainEvent commandStatus => baseEntry with
            {
                EntityKind = "Command",
                EntityId = commandStatus.CommandId.Value.ToString("D"),
                FromStatus = commandStatus.FromStatus.ToString(),
                ToStatus = commandStatus.ToStatus.ToString(),
                Reason = commandStatus.Reason
            },
            RuntimeIncidentRecordedDomainEvent incidentRecorded => baseEntry with
            {
                EntityKind = "Incident",
                EntityId = incidentRecorded.IncidentId.Value.ToString("D"),
                Severity = incidentRecorded.Severity.ToString(),
                Code = incidentRecorded.Code
            },
            _ => baseEntry
        };
    }

    private static RuntimeStationStatusResponse ToStationStatus(RuntimeSession session)
    {
        return new RuntimeStationStatusResponse(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId.Value,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            new RuntimeProductionUnitIdentityResponse(
                session.TraceMetadata.ProductionUnitIdentity.ModelId,
                session.TraceMetadata.ProductionUnitIdentity.InputKey,
                session.TraceMetadata.ProductionUnitIdentity.Value),
            session.StationId.Value,
            session.Id.Value,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            session.TraceMetadata.LotId,
            session.TraceMetadata.CarrierId,
            session.TraceMetadata.FixtureId,
            session.TraceMetadata.DeviceId,
            session.Status.ToString(),
            session.Steps.Count,
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Running),
            session.Commands.Count,
            session.Incidents.Count,
            session.LastTransitionAtUtc,
            session.IsTerminal);
    }

    private static string StationSystemGroup(RuntimeSession session)
    {
        return RuntimeProgressHub.StationSystemGroup(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId.Value,
            session.TraceMetadata.StationSystemId);
    }

    private static string ProductionRunGroup(RuntimeSession session)
    {
        return RuntimeProgressHub.ProductionRunGroup(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId.Value);
    }

    private static RuntimeTargetStatusResponse ToTargetStatus(
        RuntimeSession session,
        RuntimeCommandStatusChangedDomainEvent commandStatusEvent)
    {
        return RuntimeMonitoringResponseMapper.ToResponse(
            RuntimeTargetStatusProjection.FromCommandStatusChanged(session, commandStatusEvent));
    }

    private static RuntimeAlarmResponse? ToAlarmResponse(
        RuntimeSession session,
        RuntimeIncidentRecordedDomainEvent incidentEvent)
    {
        var incident = session.Incidents.SingleOrDefault(candidate => candidate.Id == incidentEvent.IncidentId);
        if (incident is null)
        {
            return null;
        }

        return new RuntimeAlarmResponse(
            incident.Id.Value,
            session.Id.Value,
            session.StationId.Value,
            incident.Severity.ToString(),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc,
            IsAcknowledged: false,
            AcknowledgedBy: null,
            AcknowledgedAtUtc: null);
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

    private sealed record RuntimeEventEnvelope(IDomainEvent DomainEvent, RuntimeSessionId? SessionId);
}
