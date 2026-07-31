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
        LastChangedAtUtc = raisedAtUtc;
        Version = 1;
        SourceActive = true;
        IsLatching = false;
        RequiresBuzzer = severity is AlarmSeverity.Major or AlarmSeverity.Critical;
        MaximumShelfSeconds = 900;
        EscalationAction = AlarmPolicyAction.None;
    }

    private Alarm(
        AlarmId id,
        AlarmDefinition definition,
        string? sourceId,
        DateTimeOffset raisedAtUtc)
        : this(
            id,
            definition.StationId,
            definition.Source,
            sourceId,
            definition.Severity,
            definition.Title,
            definition.Description,
            raisedAtUtc)
    {
        DefinitionId = definition.Id;
        IsLatching = definition.IsLatching;
        RequiresBuzzer = definition.RequiresBuzzer;
        MaximumShelfSeconds = definition.MaximumShelfSeconds;
        EscalationDelaySeconds = definition.EscalationDelaySeconds;
        EscalationAction = definition.EscalationAction;
    }

    public string StationId { get; private set; }

    public string Source { get; private set; }

    public string? SourceId { get; private set; }

    public string? DefinitionId { get; private set; }

    public AlarmSeverity Severity { get; private set; }

    public AlarmStatus Status { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; private set; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool SourceActive { get; private set; }

    public string? SourceClearedBy { get; private set; }

    public DateTimeOffset? SourceClearedAtUtc { get; private set; }

    public string? SourceClearanceNote { get; private set; }

    public bool IsLatching { get; private set; }

    public bool RequiresBuzzer { get; private set; }

    public int MaximumShelfSeconds { get; private set; }

    public int? EscalationDelaySeconds { get; private set; }

    public AlarmPolicyAction EscalationAction { get; private set; }

    public string? AcknowledgedBy { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public string? AcknowledgementComment { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolutionNote { get; private set; }

    public string? ShelvedBy { get; private set; }

    public string? ShelfComment { get; private set; }

    public DateTimeOffset? ShelvedAtUtc { get; private set; }

    public DateTimeOffset? ShelvedUntilUtc { get; private set; }

    public string? SuppressionSource { get; private set; }

    public string? SuppressionReason { get; private set; }

    public string? SuppressedBy { get; private set; }

    public DateTimeOffset? SuppressedAtUtc { get; private set; }

    public DateTimeOffset? SuppressedUntilUtc { get; private set; }

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

    public static Alarm Raise(
        AlarmId id,
        AlarmDefinition definition,
        string? sourceId,
        DateTimeOffset raisedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateUtc(raisedAtUtc, nameof(raisedAtUtc));

        var aggregate = new Alarm(id, definition, sourceId, raisedAtUtc);
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
        return Acknowledge(
            acknowledgedBy,
            "Acknowledged.",
            acknowledgedAtUtc);
    }

    public OperationsOperationResult Acknowledge(
        string acknowledgedBy,
        string acknowledgementComment,
        DateTimeOffset acknowledgedAtUtc)
    {
        if (Status == AlarmStatus.Resolved)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.AlreadyResolved",
                "Resolved alarms cannot be acknowledged.");
        }

        if (AcknowledgedAtUtc.HasValue)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.AlreadyAcknowledged",
                "Alarm acknowledgement has already been recorded.");
        }

        var timestampValidation = ValidateTransitionTimestamp(acknowledgedAtUtc);
        if (!timestampValidation.Succeeded)
        {
            return timestampValidation;
        }

        AcknowledgedBy = RequiredText(acknowledgedBy, nameof(acknowledgedBy));
        AcknowledgementComment = RequiredText(
            acknowledgementComment,
            nameof(acknowledgementComment));
        AcknowledgedAtUtc = acknowledgedAtUtc;
        Status = SourceActive
            ? AlarmStatus.Acknowledged
            : AlarmStatus.Resolved;
        if (!SourceActive)
        {
            ResolvedBy = AcknowledgedBy;
            ResolutionNote = AcknowledgementComment;
            ResolvedAtUtc = acknowledgedAtUtc;
        }

        RecordTransition(acknowledgedAtUtc);

        return OperationsOperationResult.Accepted("Alarm acknowledged.");
    }

    public OperationsOperationResult ClearFromSource(
        string sourceActor,
        string clearanceNote,
        DateTimeOffset clearedAtUtc)
    {
        if (!SourceActive)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.SourceAlreadyClear",
                "Alarm source clearance has already been recorded.");
        }

        var timestampValidation = ValidateTransitionTimestamp(clearedAtUtc);
        if (!timestampValidation.Succeeded)
        {
            return timestampValidation;
        }

        SourceClearedBy = RequiredText(sourceActor, nameof(sourceActor));
        SourceClearanceNote = RequiredText(clearanceNote, nameof(clearanceNote));
        SourceClearedAtUtc = clearedAtUtc;
        SourceActive = false;
        if (!IsLatching || AcknowledgedAtUtc.HasValue)
        {
            Status = AlarmStatus.Resolved;
            ResolvedBy = SourceClearedBy;
            ResolutionNote = SourceClearanceNote;
            ResolvedAtUtc = clearedAtUtc;
        }

        RecordTransition(clearedAtUtc);

        return OperationsOperationResult.Accepted("Alarm source cleared.");
    }

    public OperationsOperationResult Shelf(
        string actorId,
        string comment,
        DateTimeOffset shelvedUntilUtc,
        DateTimeOffset shelvedAtUtc)
    {
        if (!IsOpen)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.AlreadyResolved",
                "Resolved alarms cannot be shelved.");
        }

        var timestampValidation = ValidateTransitionTimestamp(shelvedAtUtc);
        if (!timestampValidation.Succeeded)
        {
            return timestampValidation;
        }

        if (shelvedUntilUtc.Offset != TimeSpan.Zero
            || shelvedUntilUtc <= shelvedAtUtc
            || shelvedUntilUtc > shelvedAtUtc.AddSeconds(MaximumShelfSeconds))
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.InvalidShelfWindow",
                $"Shelving must expire within {MaximumShelfSeconds} seconds.");
        }

        ShelvedBy = RequiredText(actorId, nameof(actorId));
        ShelfComment = RequiredText(comment, nameof(comment));
        ShelvedAtUtc = shelvedAtUtc;
        ShelvedUntilUtc = shelvedUntilUtc;
        RecordTransition(shelvedAtUtc);

        return OperationsOperationResult.Accepted("Alarm shelved.");
    }

    public OperationsOperationResult Suppress(
        string actorId,
        string suppressionSource,
        string reason,
        DateTimeOffset suppressedUntilUtc,
        DateTimeOffset suppressedAtUtc)
    {
        if (!IsOpen)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.AlreadyResolved",
                "Resolved alarms cannot be suppressed.");
        }

        var timestampValidation = ValidateTransitionTimestamp(suppressedAtUtc);
        if (!timestampValidation.Succeeded)
        {
            return timestampValidation;
        }

        if (!string.Equals(Source, suppressionSource.Trim(), StringComparison.Ordinal))
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.SuppressionSourceMismatch",
                "Suppression must explicitly name the alarm source.");
        }

        if (suppressedUntilUtc.Offset != TimeSpan.Zero
            || suppressedUntilUtc <= suppressedAtUtc
            || suppressedUntilUtc > suppressedAtUtc.AddHours(24))
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.InvalidSuppressionWindow",
                "Suppression must have a positive window no longer than 24 hours.");
        }

        SuppressedBy = RequiredText(actorId, nameof(actorId));
        SuppressionSource = RequiredText(suppressionSource, nameof(suppressionSource));
        SuppressionReason = RequiredText(reason, nameof(reason));
        SuppressedAtUtc = suppressedAtUtc;
        SuppressedUntilUtc = suppressedUntilUtc;
        RecordTransition(suppressedAtUtc);

        return OperationsOperationResult.Accepted("Alarm suppressed.");
    }

    public bool IsShelvedAt(DateTimeOffset timestampUtc)
    {
        ValidateUtc(timestampUtc, nameof(timestampUtc));
        return IsOpen
            && ShelvedAtUtc.HasValue
            && ShelvedUntilUtc.HasValue
            && timestampUtc >= ShelvedAtUtc.Value
            && timestampUtc < ShelvedUntilUtc.Value;
    }

    public bool IsSuppressedAt(DateTimeOffset timestampUtc)
    {
        ValidateUtc(timestampUtc, nameof(timestampUtc));
        return IsOpen
            && SuppressedAtUtc.HasValue
            && SuppressedUntilUtc.HasValue
            && timestampUtc >= SuppressedAtUtc.Value
            && timestampUtc < SuppressedUntilUtc.Value;
    }

    public bool IsBuzzerActiveAt(DateTimeOffset timestampUtc)
    {
        return RequiresBuzzer
            && IsOpen
            && !AcknowledgedAtUtc.HasValue
            && !IsShelvedAt(timestampUtc)
            && !IsSuppressedAt(timestampUtc);
    }

    public bool IsEscalationDueAt(DateTimeOffset timestampUtc)
    {
        ValidateUtc(timestampUtc, nameof(timestampUtc));
        return EscalationAction != AlarmPolicyAction.None
            && EscalationDelaySeconds.HasValue
            && IsOpen
            && !AcknowledgedAtUtc.HasValue
            && !IsShelvedAt(timestampUtc)
            && !IsSuppressedAt(timestampUtc)
            && timestampUtc >= RaisedAtUtc.AddSeconds(EscalationDelaySeconds.Value);
    }

    private OperationsOperationResult ValidateTransitionTimestamp(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero || value < LastChangedAtUtc)
        {
            return OperationsOperationResult.Rejected(
                "Operations.Alarm.NonMonotonicTimestamp",
                "Alarm lifecycle timestamps must be monotonic UTC values.");
        }

        return OperationsOperationResult.Accepted();
    }

    private void RecordTransition(DateTimeOffset occurredAtUtc)
    {
        LastChangedAtUtc = occurredAtUtc;
        checked
        {
            Version++;
        }
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

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be non-default UTC.", parameterName);
        }
    }
}
