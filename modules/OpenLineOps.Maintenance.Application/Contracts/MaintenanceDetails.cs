using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Readiness;

namespace OpenLineOps.Maintenance.Application.Contracts;

public sealed record EquipmentAssetDetails(
    string AssetId,
    long Revision,
    string StationId,
    string DisplayName,
    bool ProductionCritical,
    bool RequiresCalibration,
    long TotalCycles,
    decimal TotalOperatingHours,
    EquipmentHealthStatus HealthStatus,
    string? HealthDiagnostic,
    CalibrationDetails? Calibration,
    IReadOnlyCollection<MaintenancePlanDetails> Plans,
    IReadOnlyCollection<MaintenanceTaskDetails> Tasks);

public sealed record MaintenancePlanDetails(
    string PlanId,
    string DisplayName,
    long? CycleInterval,
    decimal? OperatingHoursInterval,
    TimeSpan? CalendarInterval,
    bool BlocksProduction,
    long BaselineCycles,
    decimal BaselineOperatingHours,
    DateTimeOffset? NextDueAtUtc);

public sealed record MaintenanceTaskDetails(
    string TaskId,
    string PlanId,
    MaintenanceDueReason DueReason,
    DateTimeOffset DueAtUtc,
    long DueAtCycles,
    decimal DueAtOperatingHours,
    MaintenanceTaskStatus Status,
    DateTimeOffset? CompletedAtUtc,
    string? CompletedBy,
    string? CompletionNote);

public sealed record CalibrationDetails(
    CalibrationStatus Status,
    string Reference,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? ValidUntilUtc);

public sealed record StationProductionStartDecision(
    string StationId,
    DateTimeOffset EvaluatedAtUtc,
    bool Allowed,
    IReadOnlyCollection<ProductionStartBlock> Blocks,
    IReadOnlyCollection<EquipmentAssetRevision> AssetRevisions);

public sealed record EquipmentAssetRevision(
    string AssetId,
    long Revision);
