using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class RuntimeMonitoringDomainEventMapper
{
    public static PersistedRuntimeMonitoringEvent ToDocument(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent created => new PersistedRuntimeMonitoringEvent(
                created.EventId,
                created.OccurredAtUtc,
                created.EventName,
                created.SessionId.Value,
                null,
                null,
                null,
                null,
                null,
                null),
            RuntimeSessionStatusChangedDomainEvent status => new PersistedRuntimeMonitoringEvent(
                status.EventId,
                status.OccurredAtUtc,
                status.EventName,
                status.SessionId.Value,
                null,
                status.FromStatus.ToString(),
                status.ToStatus.ToString(),
                status.Reason,
                null,
                null),
            RuntimeStepStatusChangedDomainEvent status => new PersistedRuntimeMonitoringEvent(
                status.EventId,
                status.OccurredAtUtc,
                status.EventName,
                status.SessionId.Value,
                status.StepId.Value,
                status.FromStatus.ToString(),
                status.ToStatus.ToString(),
                null,
                null,
                null),
            RuntimeCommandStatusChangedDomainEvent status => new PersistedRuntimeMonitoringEvent(
                status.EventId,
                status.OccurredAtUtc,
                status.EventName,
                status.SessionId.Value,
                status.CommandId.Value,
                status.FromStatus.ToString(),
                status.ToStatus.ToString(),
                status.Reason,
                null,
                null),
            RuntimeIncidentRecordedDomainEvent incident => new PersistedRuntimeMonitoringEvent(
                incident.EventId,
                incident.OccurredAtUtc,
                incident.EventName,
                incident.SessionId.Value,
                incident.IncidentId.Value,
                null,
                null,
                null,
                incident.Severity.ToString(),
                incident.Code),
            _ => throw new InvalidOperationException(
                $"Domain event {domainEvent.GetType().FullName} is not a Runtime Session event.")
        };
    }

    public static IDomainEvent ToDomainEvent(PersistedRuntimeMonitoringEvent document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.EventId == Guid.Empty || document.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted Runtime monitoring event has an empty identity.");
        }

        var sessionId = new RuntimeSessionId(document.SessionId);
        DomainEvent domainEvent = document.EventName switch
        {
            "RuntimeSession.Created" => CreateSessionCreated(document, sessionId),
            "RuntimeSession.StatusChanged" => CreateSessionStatusChanged(document, sessionId),
            "RuntimeStep.StatusChanged" => CreateStepStatusChanged(document, sessionId),
            "RuntimeCommand.StatusChanged" => CreateCommandStatusChanged(document, sessionId),
            "RuntimeIncident.Recorded" => CreateIncidentRecorded(document, sessionId),
            _ => throw new InvalidDataException(
                $"Persisted Runtime monitoring event name '{document.EventName}' is invalid.")
        };

        return domainEvent with
        {
            EventId = document.EventId,
            OccurredAtUtc = document.OccurredAtUtc
        };
    }

    private static RuntimeSessionCreatedDomainEvent CreateSessionCreated(
        PersistedRuntimeMonitoringEvent document,
        RuntimeSessionId sessionId)
    {
        RequireAbsent(document);
        return new RuntimeSessionCreatedDomainEvent(sessionId);
    }

    private static RuntimeSessionStatusChangedDomainEvent CreateSessionStatusChanged(
        PersistedRuntimeMonitoringEvent document,
        RuntimeSessionId sessionId)
    {
        RequireNoEntitySeverityOrCode(document);
        return new RuntimeSessionStatusChangedDomainEvent(
            sessionId,
            ParseEnum<RuntimeSessionStatus>(document.FromStatus, nameof(document.FromStatus)),
            ParseEnum<RuntimeSessionStatus>(document.ToStatus, nameof(document.ToStatus)),
            RequireText(document.Reason, nameof(document.Reason)));
    }

    private static RuntimeStepStatusChangedDomainEvent CreateStepStatusChanged(
        PersistedRuntimeMonitoringEvent document,
        RuntimeSessionId sessionId)
    {
        RequireEntity(document);
        if (document.Reason is not null || document.Severity is not null || document.Code is not null)
        {
            throw InvalidShape(document);
        }

        return new RuntimeStepStatusChangedDomainEvent(
            sessionId,
            new RuntimeStepId(document.EntityId!.Value),
            ParseEnum<RuntimeStepStatus>(document.FromStatus, nameof(document.FromStatus)),
            ParseEnum<RuntimeStepStatus>(document.ToStatus, nameof(document.ToStatus)));
    }

    private static RuntimeCommandStatusChangedDomainEvent CreateCommandStatusChanged(
        PersistedRuntimeMonitoringEvent document,
        RuntimeSessionId sessionId)
    {
        RequireEntity(document);
        if (document.Severity is not null || document.Code is not null)
        {
            throw InvalidShape(document);
        }

        return new RuntimeCommandStatusChangedDomainEvent(
            sessionId,
            new RuntimeCommandId(document.EntityId!.Value),
            ParseEnum<ExecutionStatus>(document.FromStatus, nameof(document.FromStatus)),
            ParseEnum<ExecutionStatus>(document.ToStatus, nameof(document.ToStatus)),
            RequireText(document.Reason, nameof(document.Reason)));
    }

    private static RuntimeIncidentRecordedDomainEvent CreateIncidentRecorded(
        PersistedRuntimeMonitoringEvent document,
        RuntimeSessionId sessionId)
    {
        RequireEntity(document);
        if (document.FromStatus is not null || document.ToStatus is not null || document.Reason is not null)
        {
            throw InvalidShape(document);
        }

        return new RuntimeIncidentRecordedDomainEvent(
            sessionId,
            new RuntimeIncidentId(document.EntityId!.Value),
            ParseEnum<RuntimeIncidentSeverity>(document.Severity, nameof(document.Severity)),
            RequireText(document.Code, nameof(document.Code)));
    }

    private static void RequireAbsent(PersistedRuntimeMonitoringEvent document)
    {
        if (document.EntityId is not null
            || document.FromStatus is not null
            || document.ToStatus is not null
            || document.Reason is not null
            || document.Severity is not null
            || document.Code is not null)
        {
            throw InvalidShape(document);
        }
    }

    private static void RequireNoEntitySeverityOrCode(PersistedRuntimeMonitoringEvent document)
    {
        if (document.EntityId is not null || document.Severity is not null || document.Code is not null)
        {
            throw InvalidShape(document);
        }
    }

    private static void RequireEntity(PersistedRuntimeMonitoringEvent document)
    {
        if (document.EntityId is null || document.EntityId == Guid.Empty)
        {
            throw InvalidShape(document);
        }
    }

    private static InvalidDataException InvalidShape(PersistedRuntimeMonitoringEvent document)
    {
        return new InvalidDataException(
            $"Persisted Runtime monitoring event {document.EventId:D} has an invalid {document.EventName} shape.");
    }

    private static TEnum ParseEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (value is not null && CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Persisted Runtime monitoring {fieldName} value '{value}' is invalid. Expected "
            + CanonicalEnumToken.ExpectedTokens<TEnum>() + ".");
    }

    private static string RequireText(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Persisted Runtime monitoring {fieldName} is required.")
            : value;
    }
}

internal sealed record PersistedRuntimeMonitoringEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventName,
    Guid SessionId,
    Guid? EntityId,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    string? Severity,
    string? Code);
