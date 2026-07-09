using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;

namespace OpenLineOps.Operations.Api.RuntimeIntegration;

public sealed class RuntimeIncidentOperationsAlarmSubscriber : IRuntimeDomainEventSubscriber
{
    private const string RuntimeSource = "runtime";
    private readonly IRuntimeSessionRepository _runtimeSessionRepository;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public RuntimeIncidentOperationsAlarmSubscriber(
        IRuntimeSessionRepository runtimeSessionRepository,
        IServiceScopeFactory serviceScopeFactory)
    {
        _runtimeSessionRepository = runtimeSessionRepository;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var incidentEvent in domainEvents.OfType<RuntimeIncidentRecordedDomainEvent>())
        {
            await RaiseOperationsAlarmAsync(incidentEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RaiseOperationsAlarmAsync(
        RuntimeIncidentRecordedDomainEvent incidentEvent,
        CancellationToken cancellationToken)
    {
        var session = await _runtimeSessionRepository
            .GetByIdAsync(incidentEvent.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        var incident = session.Incidents.SingleOrDefault(candidate => candidate.Id == incidentEvent.IncidentId);
        if (incident is null)
        {
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var alarmAppService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();
        var alarmId = ToOperationsAlarmId(incident.Id);

        var existingAlarm = await alarmAppService
            .GetAsync(alarmId, cancellationToken)
            .ConfigureAwait(false);
        if (existingAlarm is not null)
        {
            return;
        }

        await alarmAppService
            .RaiseAsync(ToRaiseAlarmRequest(alarmId, session.Id, session.StationId.Value, incident), cancellationToken)
            .ConfigureAwait(false);
    }

    private static RaiseAlarmRequest ToRaiseAlarmRequest(
        string alarmId,
        RuntimeSessionId sessionId,
        string stationId,
        RuntimeIncident incident)
    {
        return new RaiseAlarmRequest(
            alarmId,
            stationId,
            RuntimeSource,
            sessionId.Value.ToString("D"),
            ToAlarmSeverity(incident.Severity),
            $"Runtime incident: {incident.Code}",
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static AlarmSeverity ToAlarmSeverity(RuntimeIncidentSeverity severity)
    {
        return severity switch
        {
            RuntimeIncidentSeverity.Information => AlarmSeverity.Info,
            RuntimeIncidentSeverity.Warning => AlarmSeverity.Warning,
            RuntimeIncidentSeverity.Error => AlarmSeverity.Major,
            RuntimeIncidentSeverity.Critical => AlarmSeverity.Critical,
            _ => AlarmSeverity.Major
        };
    }

    private static string ToOperationsAlarmId(RuntimeIncidentId incidentId)
    {
        return $"operations.alarm.runtime.incident.{incidentId.Value:N}";
    }
}
