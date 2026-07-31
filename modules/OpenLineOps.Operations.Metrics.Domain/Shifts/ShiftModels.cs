namespace OpenLineOps.Operations.Metrics.Domain.Shifts;

public sealed record ShiftDefinition
{
    public const int CurrentSchemaVersion = 1;

    public ShiftDefinition(
        string shiftId,
        string stationId,
        string name,
        string timeZoneId,
        TimeOnly localStartTime,
        TimeOnly localEndTime,
        string createdBy,
        DateTimeOffset createdAtUtc,
        int schemaVersion)
    {
        ShiftId = OperationsMetricsGuard.RequiredCanonical(shiftId, nameof(shiftId));
        StationId = OperationsMetricsGuard.RequiredCanonical(stationId, nameof(stationId));
        Name = OperationsMetricsGuard.RequiredCanonical(name, nameof(name));
        TimeZoneId = OperationsMetricsGuard.RequiredCanonical(
            timeZoneId,
            nameof(timeZoneId));
        CreatedBy = OperationsMetricsGuard.RequiredCanonical(createdBy, nameof(createdBy));
        CreatedAtUtc = OperationsMetricsGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        if (localStartTime == localEndTime)
        {
            throw new ArgumentException(
                "A shift local start and end time must differ.",
                nameof(localEndTime));
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Shift schema version must be {CurrentSchemaVersion}.");
        }

        LocalStartTime = localStartTime;
        LocalEndTime = localEndTime;
        SchemaVersion = schemaVersion;
    }

    public string ShiftId { get; }

    public string StationId { get; }

    public string Name { get; }

    public string TimeZoneId { get; }

    public TimeOnly LocalStartTime { get; }

    public TimeOnly LocalEndTime { get; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public int SchemaVersion { get; }
}

public sealed record PlannedProductionWindow
{
    public const int CurrentSchemaVersion = 1;

    public PlannedProductionWindow(
        string windowId,
        string shiftId,
        string stationId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        int targetQuantity,
        TimeSpan idealCycleTime,
        string createdBy,
        DateTimeOffset createdAtUtc,
        int schemaVersion)
    {
        WindowId = OperationsMetricsGuard.RequiredCanonical(windowId, nameof(windowId));
        ShiftId = OperationsMetricsGuard.RequiredCanonical(shiftId, nameof(shiftId));
        StationId = OperationsMetricsGuard.RequiredCanonical(stationId, nameof(stationId));
        StartsAtUtc = OperationsMetricsGuard.Utc(startsAtUtc, nameof(startsAtUtc));
        EndsAtUtc = OperationsMetricsGuard.Utc(endsAtUtc, nameof(endsAtUtc));
        CreatedBy = OperationsMetricsGuard.RequiredCanonical(createdBy, nameof(createdBy));
        CreatedAtUtc = OperationsMetricsGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException(
                "A planned production window must end after it starts.",
                nameof(endsAtUtc));
        }

        if (targetQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetQuantity),
                targetQuantity,
                "A planned production target must be positive.");
        }

        if (idealCycleTime <= TimeSpan.Zero
            || idealCycleTime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(idealCycleTime),
                idealCycleTime,
                "Ideal cycle time must be positive and no longer than seven days.");
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Planned production window schema version must be {CurrentSchemaVersion}.");
        }

        TargetQuantity = targetQuantity;
        IdealCycleTime = idealCycleTime;
        SchemaVersion = schemaVersion;
    }

    public string WindowId { get; }

    public string ShiftId { get; }

    public string StationId { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    public int TargetQuantity { get; }

    public TimeSpan IdealCycleTime { get; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public int SchemaVersion { get; }
}
