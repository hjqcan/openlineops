using OpenLineOps.Maintenance.Domain.Assets;

namespace OpenLineOps.Maintenance.Application.Contracts;

public sealed record RegisterEquipmentAssetCommand(
    string AssetId,
    string StationId,
    string DisplayName,
    bool ProductionCritical,
    bool RequiresCalibration,
    string CommandId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record AddMaintenancePlanCommand(
    string AssetId,
    string PlanId,
    string DisplayName,
    long? CycleInterval,
    decimal? OperatingHoursInterval,
    TimeSpan? CalendarInterval,
    bool BlocksProduction,
    string CommandId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record RecordEquipmentUsageCommand(
    string AssetId,
    long CycleDelta,
    decimal OperatingHoursDelta,
    string CommandId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record EvaluateMaintenanceCommand(
    string AssetId,
    string CommandId,
    string ActorId,
    DateTimeOffset EvaluatedAtUtc);

public sealed record CompleteMaintenanceTaskCommand(
    string AssetId,
    string TaskId,
    string CompletionNote,
    string CommandId,
    string ActorId,
    DateTimeOffset CompletedAtUtc);

public sealed record RecordCalibrationCommand(
    string AssetId,
    CalibrationStatus Status,
    string Reference,
    DateTimeOffset? ValidUntilUtc,
    string CommandId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record RecordEquipmentHealthCommand(
    string AssetId,
    EquipmentHealthStatus Status,
    string Diagnostic,
    string CommandId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);
