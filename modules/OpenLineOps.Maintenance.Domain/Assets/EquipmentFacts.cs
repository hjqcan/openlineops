using System.Text.Json.Serialization;

namespace OpenLineOps.Maintenance.Domain.Assets;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$fact")]
[JsonDerivedType(typeof(EquipmentAssetRegisteredFact), "asset-registered")]
[JsonDerivedType(typeof(MaintenancePlanAddedFact), "plan-added")]
[JsonDerivedType(typeof(EquipmentUsageRecordedFact), "usage-recorded")]
[JsonDerivedType(typeof(MaintenanceEvaluationRecordedFact), "maintenance-evaluated")]
[JsonDerivedType(typeof(MaintenanceTaskRaisedFact), "task-raised")]
[JsonDerivedType(typeof(MaintenanceTaskCompletedFact), "task-completed")]
[JsonDerivedType(typeof(CalibrationStatusRecordedFact), "calibration-recorded")]
[JsonDerivedType(typeof(EquipmentHealthRecordedFact), "health-recorded")]
public abstract record EquipmentFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId);

public sealed record EquipmentAssetRegisteredFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string AssetId,
    string StationId,
    string DisplayName,
    bool ProductionCritical,
    bool RequiresCalibration)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record MaintenancePlanAddedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string PlanId,
    string DisplayName,
    long? CycleInterval,
    decimal? OperatingHoursInterval,
    TimeSpan? CalendarInterval,
    bool BlocksProduction,
    long BaselineCycles,
    decimal BaselineOperatingHours,
    DateTimeOffset? NextDueAtUtc)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record EquipmentUsageRecordedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    long CycleDelta,
    decimal OperatingHoursDelta)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record MaintenanceEvaluationRecordedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record MaintenanceTaskRaisedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string TaskId,
    string PlanId,
    MaintenanceDueReason DueReason,
    long DueAtCycles,
    decimal DueAtOperatingHours)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record MaintenanceTaskCompletedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string TaskId,
    string CompletionNote)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record CalibrationStatusRecordedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    CalibrationStatus Status,
    string Reference,
    DateTimeOffset? ValidUntilUtc)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);

public sealed record EquipmentHealthRecordedFact(
    long Sequence,
    string FactId,
    string CommandId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    EquipmentHealthStatus Status,
    string Diagnostic)
    : EquipmentFact(Sequence, FactId, CommandId, OccurredAtUtc, ActorId);
