namespace OpenLineOps.Operations.Metrics.Domain.Downtime;

public enum DowntimeFactKind
{
    Opened = 0,
    SourceCleared = 1,
    ReasonAttributed = 2
}

public sealed record DowntimeFact
{
    public DowntimeFact(
        string factId,
        string downtimeId,
        int revision,
        DowntimeFactKind kind,
        string stationId,
        string sourceId,
        DateTimeOffset occurredAtUtc,
        string actorId,
        string? reasonCode = null,
        string? reasonComment = null)
    {
        FactId = OperationsMetricsGuard.RequiredCanonical(factId, nameof(factId));
        DowntimeId = OperationsMetricsGuard.RequiredCanonical(
            downtimeId,
            nameof(downtimeId));
        StationId = OperationsMetricsGuard.RequiredCanonical(stationId, nameof(stationId));
        SourceId = OperationsMetricsGuard.RequiredCanonical(sourceId, nameof(sourceId));
        OccurredAtUtc = OperationsMetricsGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        ActorId = OperationsMetricsGuard.RequiredCanonical(actorId, nameof(actorId));
        Kind = OperationsMetricsGuard.Defined(kind, nameof(kind));
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "A downtime fact revision must be positive.");
        }

        if (kind == DowntimeFactKind.ReasonAttributed)
        {
            ReasonCode = OperationsMetricsGuard.RequiredCanonical(
                reasonCode ?? string.Empty,
                nameof(reasonCode));
            ReasonComment = reasonComment is null
                ? null
                : OperationsMetricsGuard.RequiredCanonical(
                    reasonComment,
                    nameof(reasonComment));
        }
        else if (reasonCode is not null || reasonComment is not null)
        {
            throw new ArgumentException(
                "Only a reason-attribution fact may contain a reason.",
                nameof(kind));
        }

        Revision = revision;
    }

    public string FactId { get; }

    public string DowntimeId { get; }

    public int Revision { get; }

    public DowntimeFactKind Kind { get; }

    public string StationId { get; }

    public string SourceId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string ActorId { get; }

    public string? ReasonCode { get; }

    public string? ReasonComment { get; }
}

public sealed class DowntimeInterval
{
    private DowntimeInterval(DowntimeFact opened)
    {
        DowntimeId = opened.DowntimeId;
        StationId = opened.StationId;
        SourceId = opened.SourceId;
        StartedAtUtc = opened.OccurredAtUtc;
        Revision = opened.Revision;
        Facts = [opened];
    }

    public string DowntimeId { get; }

    public string StationId { get; }

    public string SourceId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? SourceClearedAtUtc { get; private set; }

    public string? ReasonCode { get; private set; }

    public string? ReasonComment { get; private set; }

    public string? ReasonAttributedBy { get; private set; }

    public DateTimeOffset? ReasonAttributedAtUtc { get; private set; }

    public int Revision { get; private set; }

    public IReadOnlyList<DowntimeFact> Facts { get; private set; }

    public static DowntimeInterval Open(
        string factId,
        string downtimeId,
        string stationId,
        string sourceId,
        DateTimeOffset occurredAtUtc,
        string actorId)
    {
        return new DowntimeInterval(new DowntimeFact(
            factId,
            downtimeId,
            revision: 1,
            DowntimeFactKind.Opened,
            stationId,
            sourceId,
            occurredAtUtc,
            actorId));
    }

    public static DowntimeInterval Rehydrate(IEnumerable<DowntimeFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var ordered = facts.OrderBy(static fact => fact.Revision).ToArray();
        if (ordered.Length == 0
            || ordered[0].Kind != DowntimeFactKind.Opened
            || ordered[0].Revision != 1)
        {
            throw new InvalidDataException(
                "A downtime interval must begin with revision one Opened.");
        }

        var interval = new DowntimeInterval(ordered[0]);
        foreach (var fact in ordered.Skip(1))
        {
            interval.Apply(fact);
        }

        return interval;
    }

    public DowntimeFact ClearFromSource(
        string factId,
        string stationId,
        string sourceId,
        DateTimeOffset occurredAtUtc,
        string actorId)
    {
        EnsureIdentity(stationId, sourceId);
        if (SourceClearedAtUtc is not null)
        {
            throw new InvalidOperationException(
                "The downtime source has already cleared this interval.");
        }

        if (occurredAtUtc < StartedAtUtc)
        {
            throw new ArgumentException(
                "Source clearance cannot occur before downtime opened.",
                nameof(occurredAtUtc));
        }

        return new DowntimeFact(
            factId,
            DowntimeId,
            checked(Revision + 1),
            DowntimeFactKind.SourceCleared,
            StationId,
            SourceId,
            occurredAtUtc,
            actorId);
    }

    public DowntimeFact AttributeReason(
        string factId,
        string reasonCode,
        string? reasonComment,
        DateTimeOffset occurredAtUtc,
        string actorId)
    {
        if (occurredAtUtc < StartedAtUtc)
        {
            throw new ArgumentException(
                "Reason attribution cannot occur before downtime opened.",
                nameof(occurredAtUtc));
        }

        return new DowntimeFact(
            factId,
            DowntimeId,
            checked(Revision + 1),
            DowntimeFactKind.ReasonAttributed,
            StationId,
            SourceId,
            occurredAtUtc,
            actorId,
            reasonCode,
            reasonComment);
    }

    private void Apply(DowntimeFact fact)
    {
        if (fact.DowntimeId != DowntimeId
            || fact.StationId != StationId
            || fact.SourceId != SourceId
            || fact.Revision != Revision + 1)
        {
            throw new InvalidDataException(
                "Downtime fact identity or revision continuity is invalid.");
        }

        switch (fact.Kind)
        {
            case DowntimeFactKind.Opened:
                throw new InvalidDataException(
                    "A downtime interval cannot contain a second Opened fact.");
            case DowntimeFactKind.SourceCleared:
                if (SourceClearedAtUtc is not null
                    || fact.OccurredAtUtc < StartedAtUtc)
                {
                    throw new InvalidDataException(
                        "Persisted downtime source clearance is invalid.");
                }

                SourceClearedAtUtc = fact.OccurredAtUtc;
                break;
            case DowntimeFactKind.ReasonAttributed:
                if (fact.OccurredAtUtc < StartedAtUtc)
                {
                    throw new InvalidDataException(
                        "Persisted downtime reason attribution is invalid.");
                }

                ReasonCode = fact.ReasonCode;
                ReasonComment = fact.ReasonComment;
                ReasonAttributedBy = fact.ActorId;
                ReasonAttributedAtUtc = fact.OccurredAtUtc;
                break;
            default:
                throw new InvalidDataException(
                    $"Downtime fact kind '{fact.Kind}' is unsupported.");
        }

        Revision = fact.Revision;
        Facts = [.. Facts, fact];
    }

    private void EnsureIdentity(string stationId, string sourceId)
    {
        if (!string.Equals(
                OperationsMetricsGuard.RequiredCanonical(
                    stationId,
                    nameof(stationId)),
                StationId,
                StringComparison.Ordinal)
            || !string.Equals(
                OperationsMetricsGuard.RequiredCanonical(
                    sourceId,
                    nameof(sourceId)),
                SourceId,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Only the owning station source may clear a downtime interval.");
        }
    }
}
