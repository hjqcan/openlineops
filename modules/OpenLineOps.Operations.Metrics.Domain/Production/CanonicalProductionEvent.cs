namespace OpenLineOps.Operations.Metrics.Domain.Production;

public enum ProductionEventKind
{
    UnitStarted = 0,
    UnitCompleted = 1,
    UnitHeld = 2,
    UnitScrapped = 3
}

public sealed record CanonicalProductionEvent
{
    public const int CurrentSchemaVersion = 1;

    public CanonicalProductionEvent(
        string eventId,
        string stationId,
        string unitId,
        ProductionEventKind kind,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc,
        bool firstAttempt,
        bool good,
        TimeSpan? cycleDuration,
        int schemaVersion)
    {
        EventId = OperationsMetricsGuard.RequiredCanonical(eventId, nameof(eventId));
        StationId = OperationsMetricsGuard.RequiredCanonical(stationId, nameof(stationId));
        UnitId = OperationsMetricsGuard.RequiredCanonical(unitId, nameof(unitId));
        Kind = OperationsMetricsGuard.Defined(kind, nameof(kind));
        SourceTimestampUtc = OperationsMetricsGuard.Utc(
            sourceTimestampUtc,
            nameof(sourceTimestampUtc));
        OccurredAtUtc = OperationsMetricsGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        ReceivedAtUtc = OperationsMetricsGuard.Utc(receivedAtUtc, nameof(receivedAtUtc));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Production event schema version must be {CurrentSchemaVersion}.");
        }

        if (kind == ProductionEventKind.UnitCompleted)
        {
            if (cycleDuration is null
                || cycleDuration <= TimeSpan.Zero
                || cycleDuration > TimeSpan.FromDays(7))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cycleDuration),
                    cycleDuration,
                    "A completed unit requires a positive cycle duration no longer than seven days.");
            }
        }
        else if (firstAttempt || good || cycleDuration is not null)
        {
            throw new ArgumentException(
                "First-attempt, good and cycle-duration facts are valid only for UnitCompleted events.",
                nameof(kind));
        }

        FirstAttempt = firstAttempt;
        Good = good;
        CycleDuration = cycleDuration;
        SchemaVersion = schemaVersion;
    }

    public string EventId { get; }

    public string StationId { get; }

    public string UnitId { get; }

    public ProductionEventKind Kind { get; }

    public DateTimeOffset SourceTimestampUtc { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public bool FirstAttempt { get; }

    public bool Good { get; }

    public TimeSpan? CycleDuration { get; }

    public int SchemaVersion { get; }
}
