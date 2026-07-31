using System.Text.Json.Serialization;

namespace OpenLineOps.Maintenance.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterEquipmentAssetRequest(
    string AssetId,
    string StationId,
    string DisplayName,
    bool ProductionCritical,
    bool RequiresCalibration,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddMaintenancePlanRequest(
    string PlanId,
    string DisplayName,
    long? CycleInterval,
    decimal? OperatingHoursInterval,
    int? CalendarIntervalSeconds,
    bool BlocksProduction,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordEquipmentUsageRequest(
    long CycleDelta,
    decimal OperatingHoursDelta,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EvaluateMaintenanceDueRequest(
    DateTimeOffset EvaluatedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteMaintenanceTaskRequest(
    string CompletionNote,
    DateTimeOffset CompletedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordCalibrationRequest(
    string Status,
    string Reference,
    DateTimeOffset? ValidUntilUtc,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordEquipmentHealthRequest(
    string Status,
    string Diagnostic,
    DateTimeOffset OccurredAtUtc);

public sealed record EquipmentAssetResponse(
    string AssetId,
    long Revision,
    string StationId,
    string DisplayName,
    bool ProductionCritical,
    bool RequiresCalibration,
    long TotalCycles,
    decimal TotalOperatingHours,
    string HealthStatus,
    string? HealthDiagnostic,
    CalibrationResponse? Calibration,
    IReadOnlyCollection<MaintenancePlanResponse> Plans,
    IReadOnlyCollection<MaintenanceTaskResponse> Tasks);

public sealed record MaintenancePlanResponse(
    string PlanId,
    string DisplayName,
    long? CycleInterval,
    decimal? OperatingHoursInterval,
    int? CalendarIntervalSeconds,
    bool BlocksProduction,
    long BaselineCycles,
    decimal BaselineOperatingHours,
    DateTimeOffset? NextDueAtUtc);

public sealed record MaintenanceTaskResponse(
    string TaskId,
    string PlanId,
    string DueReason,
    DateTimeOffset DueAtUtc,
    long DueAtCycles,
    decimal DueAtOperatingHours,
    string Status,
    DateTimeOffset? CompletedAtUtc,
    string? CompletedBy,
    string? CompletionNote);

public sealed record CalibrationResponse(
    string Status,
    string Reference,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? ValidUntilUtc);

public sealed record StationProductionReadinessResponse(
    string StationId,
    DateTimeOffset EvaluatedAtUtc,
    bool Allowed,
    IReadOnlyCollection<ProductionStartBlockResponse> Blocks,
    IReadOnlyCollection<EquipmentAssetRevisionResponse> AssetRevisions);

public sealed record ProductionStartBlockResponse(
    string AssetId,
    string Reason,
    string SubjectId,
    string Detail);

public sealed record EquipmentAssetRevisionResponse(
    string AssetId,
    long Revision);
