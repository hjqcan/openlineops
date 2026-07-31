using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Domain.Assets;

[Flags]
public enum MaintenanceDueReason
{
    None = 0,
    CycleThreshold = 1,
    OperatingHoursThreshold = 2,
    CalendarThreshold = 4
}

public enum MaintenanceTaskStatus
{
    Due = 0,
    Completed = 1
}

public enum EquipmentHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Critical = 3,
    Unavailable = 4
}

public enum CalibrationStatus
{
    Unknown = 0,
    Valid = 1,
    Expired = 2,
    Invalid = 3,
    NotRequired = 4
}

public sealed class MaintenancePlan
{
    internal MaintenancePlan(
        MaintenancePlanId id,
        string displayName,
        long? cycleInterval,
        decimal? operatingHoursInterval,
        TimeSpan? calendarInterval,
        bool blocksProduction,
        long baselineCycles,
        decimal baselineOperatingHours,
        DateTimeOffset? nextDueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (cycleInterval is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycleInterval),
                "Cycle interval must be positive when configured.");
        }

        if (operatingHoursInterval is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operatingHoursInterval),
                "Operating-hours interval must be positive when configured.");
        }

        if (calendarInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(calendarInterval),
                "Calendar interval must be positive when configured.");
        }

        if (cycleInterval is null
            && operatingHoursInterval is null
            && calendarInterval is null)
        {
            throw new ArgumentException(
                "A maintenance plan must configure at least one preventive threshold.",
                nameof(cycleInterval));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(baselineCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(baselineOperatingHours);
        Id = id;
        DisplayName = MaintenanceGuard.RequireCanonicalText(
            displayName,
            nameof(displayName));
        CycleInterval = cycleInterval;
        OperatingHoursInterval = operatingHoursInterval;
        CalendarInterval = calendarInterval;
        BlocksProduction = blocksProduction;
        BaselineCycles = baselineCycles;
        BaselineOperatingHours = baselineOperatingHours;
        NextDueAtUtc = nextDueAtUtc;
    }

    public MaintenancePlanId Id { get; }

    public string DisplayName { get; }

    public long? CycleInterval { get; }

    public decimal? OperatingHoursInterval { get; }

    public TimeSpan? CalendarInterval { get; }

    public bool BlocksProduction { get; }

    public long BaselineCycles { get; private set; }

    public decimal BaselineOperatingHours { get; private set; }

    public DateTimeOffset? NextDueAtUtc { get; private set; }

    public MaintenanceDueReason GetDueReason(
        long totalCycles,
        decimal totalOperatingHours,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(totalOperatingHours);
        MaintenanceGuard.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        var reason = MaintenanceDueReason.None;
        if (CycleInterval is { } cycleInterval
            && totalCycles - BaselineCycles >= cycleInterval)
        {
            reason |= MaintenanceDueReason.CycleThreshold;
        }

        if (OperatingHoursInterval is { } hoursInterval
            && totalOperatingHours - BaselineOperatingHours >= hoursInterval)
        {
            reason |= MaintenanceDueReason.OperatingHoursThreshold;
        }

        if (NextDueAtUtc is { } nextDue && evaluatedAtUtc >= nextDue)
        {
            reason |= MaintenanceDueReason.CalendarThreshold;
        }

        return reason;
    }

    internal void ResetBaseline(
        long totalCycles,
        decimal totalOperatingHours,
        DateTimeOffset completedAtUtc)
    {
        BaselineCycles = totalCycles;
        BaselineOperatingHours = totalOperatingHours;
        NextDueAtUtc = CalendarInterval is { } calendarInterval
            ? completedAtUtc.Add(calendarInterval)
            : null;
    }
}

public sealed class MaintenanceTask
{
    internal MaintenanceTask(
        MaintenanceTaskId id,
        MaintenancePlanId planId,
        MaintenanceDueReason dueReason,
        DateTimeOffset dueAtUtc,
        long dueAtCycles,
        decimal dueAtOperatingHours)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(planId);
        if (dueReason == MaintenanceDueReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueReason),
                "A maintenance task must identify at least one due threshold.");
        }

        Id = id;
        PlanId = planId;
        DueReason = dueReason;
        DueAtUtc = MaintenanceGuard.RequireUtc(dueAtUtc, nameof(dueAtUtc));
        ArgumentOutOfRangeException.ThrowIfNegative(dueAtCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(dueAtOperatingHours);
        DueAtCycles = dueAtCycles;
        DueAtOperatingHours = dueAtOperatingHours;
    }

    public MaintenanceTaskId Id { get; }

    public MaintenancePlanId PlanId { get; }

    public MaintenanceDueReason DueReason { get; }

    public DateTimeOffset DueAtUtc { get; }

    public long DueAtCycles { get; }

    public decimal DueAtOperatingHours { get; }

    public MaintenanceTaskStatus Status { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? CompletedBy { get; private set; }

    public string? CompletionNote { get; private set; }

    internal void Complete(
        string completedBy,
        string completionNote,
        DateTimeOffset completedAtUtc)
    {
        if (Status == MaintenanceTaskStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Maintenance task {Id} has already been completed.");
        }

        CompletedBy = MaintenanceGuard.RequireCanonicalText(
            completedBy,
            nameof(completedBy));
        CompletionNote = MaintenanceGuard.RequireCanonicalText(
            completionNote,
            nameof(completionNote),
            maximumLength: 500);
        CompletedAtUtc = MaintenanceGuard.RequireUtc(
            completedAtUtc,
            nameof(completedAtUtc));
        Status = MaintenanceTaskStatus.Completed;
    }
}

public sealed record CalibrationRecord(
    CalibrationStatus Status,
    string Reference,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? ValidUntilUtc);
