using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Operations.Domain.Events;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Operations;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Aggregates;

public sealed class Alarm : AggregateRoot<AlarmId>
{
    private Alarm()
        : base(new AlarmId("__ef_materialization__"))
    {
        StationId = string.Empty;
        Source = string.Empty;
        Title = string.Empty;
        Description = string.Empty;
    }

    private Alarm(
        AlarmId id,
        string stationId,
        string source,
        string? sourceId,
        AlarmSeverity severity,
        string title,
        string description,
        DateTimeOffset raisedAtUtc)
        : base(id)
    {
        StationId = RequiredText(stationId, nameof(stationId));
        Source = RequiredText(source, nameof(source));
        SourceId = OptionalText(sourceId);
        Severity = severity;
        Status = AlarmStatus.Raised;
        Title = RequiredText(title, nameof(title));
        Description = RequiredText(description, nameof(description));
        RaisedAtUtc = raisedAtUtc;
    }

    public string StationId { get; private set; }

    public string Source { get; private set; }

    public string? SourceId { get; private set; }

    public AlarmSeverity Severity { get; private set; }

    public AlarmStatus Status { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; private set; }

    public string? AcknowledgedBy { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolutionNote { get; private set; }

    public bool IsOpen => Status is AlarmStatus.Raised or AlarmStatus.Acknowledged;

    public static Alarm Raise(
        AlarmId id,
        string stationId,
        string source,
        string? sourceId,
        AlarmSeverity severity,
        string title,
        string description,
        DateTimeOffset raisedAtUtc)
    {
        var aggregate = new Alarm(
            id,
            stationId,
            source,
            sourceId,
            severity,
            title,
            description,
            raisedAtUtc);
        aggregate.RaiseDomainEvent(new AlarmRaisedDomainEvent(
            id,
            aggregate.StationId,
            aggregate.Source,
            aggregate.SourceId,
            aggregate.Severity,
            aggregate.Title,
            aggregate.Description,
            raisedAtUtc));

        return aggregate;
    }

    public OperationsOperationResult Acknowledge(
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc)
    {
        if (Status == AlarmStatus.Resolved)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.AlreadyResolved",
                "Resolved alarms cannot be acknowledged.");
        }

        if (Status == AlarmStatus.Acknowledged)
        {
            return OperationsOperationResult.Accepted("Alarm already acknowledged.");
        }

        AcknowledgedBy = RequiredText(acknowledgedBy, nameof(acknowledgedBy));
        AcknowledgedAtUtc = acknowledgedAtUtc;
        Status = AlarmStatus.Acknowledged;

        RaiseDomainEvent(new AlarmAcknowledgedDomainEvent(
            Id,
            AcknowledgedBy,
            acknowledgedAtUtc));

        return OperationsOperationResult.Accepted("Alarm acknowledged.");
    }

    public OperationsOperationResult Resolve(
        string resolvedBy,
        string resolutionNote,
        DateTimeOffset resolvedAtUtc)
    {
        if (Status == AlarmStatus.Resolved)
        {
            return OperationsOperationResult.Accepted("Alarm already resolved.");
        }

        ResolvedBy = RequiredText(resolvedBy, nameof(resolvedBy));
        ResolutionNote = RequiredText(resolutionNote, nameof(resolutionNote));
        ResolvedAtUtc = resolvedAtUtc;
        Status = AlarmStatus.Resolved;

        RaiseDomainEvent(new AlarmResolvedDomainEvent(
            Id,
            ResolvedBy,
            resolvedAtUtc,
            ResolutionNote));

        return OperationsOperationResult.Accepted("Alarm resolved.");
    }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Text values cannot be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string? OptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
