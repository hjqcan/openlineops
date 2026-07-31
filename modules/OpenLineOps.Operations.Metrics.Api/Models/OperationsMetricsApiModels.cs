namespace OpenLineOps.Operations.Metrics.Api.Models;

public sealed record RecordProductionEventRequest(
    string EventId,
    string StationId,
    string UnitId,
    string Kind,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset OccurredAtUtc,
    bool FirstAttempt,
    bool Good,
    double? CycleDurationMilliseconds,
    int SchemaVersion);

public sealed record DefineShiftRequest(
    string ShiftId,
    string StationId,
    string Name,
    string TimeZoneId,
    TimeOnly LocalStartTime,
    TimeOnly LocalEndTime,
    int SchemaVersion);

public sealed record ScheduleProductionWindowRequest(
    string WindowId,
    string StationId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int TargetQuantity,
    double IdealCycleTimeMilliseconds,
    int SchemaVersion);

public sealed record OpenDowntimeRequest(
    string FactId,
    string DowntimeId,
    string StationId,
    string SourceId,
    DateTimeOffset OccurredAtUtc);

public sealed record ClearDowntimeRequest(
    string FactId,
    string StationId,
    string SourceId,
    DateTimeOffset OccurredAtUtc,
    int ExpectedRevision);

public sealed record AttributeDowntimeReasonRequest(
    string FactId,
    string ReasonCode,
    string? ReasonComment,
    DateTimeOffset OccurredAtUtc,
    int ExpectedRevision);

public sealed record ProductionEventResponse(
    string EventId,
    string StationId,
    string UnitId,
    string Kind,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    bool FirstAttempt,
    bool Good,
    double? CycleDurationMilliseconds,
    int SchemaVersion);

public sealed record ShiftDefinitionResponse(
    string ShiftId,
    string StationId,
    string Name,
    string TimeZoneId,
    TimeOnly LocalStartTime,
    TimeOnly LocalEndTime,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    int SchemaVersion);

public sealed record PlannedProductionWindowResponse(
    string WindowId,
    string ShiftId,
    string StationId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int TargetQuantity,
    double IdealCycleTimeMilliseconds,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    int SchemaVersion);

public sealed record ShiftScheduleResponse(
    ShiftDefinitionResponse Definition,
    IReadOnlyList<PlannedProductionWindowResponse> Windows);

public sealed record DowntimeResponse(
    string DowntimeId,
    string StationId,
    string SourceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? SourceClearedAtUtc,
    string? ReasonCode,
    string? ReasonComment,
    string? ReasonAttributedBy,
    DateTimeOffset? ReasonAttributedAtUtc,
    int Revision);

public sealed record OeeMetricDefinitionsResponse(
    string CycleTime,
    string Takt,
    string FirstPassYield,
    string Yield,
    string Availability,
    string Performance,
    string Quality,
    string Oee);

public sealed record OeeResponse(
    string StationId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlannedWindowCount,
    double PlannedProductionSeconds,
    double DowntimeSeconds,
    double OperatingSeconds,
    double PlannedTargetQuantity,
    int CompletedCount,
    int GoodCount,
    int FirstAttemptCount,
    int FirstPassGoodCount,
    double CycleTimeSeconds,
    double TaktSeconds,
    double FirstPassYield,
    double Yield,
    double Availability,
    double Performance,
    double Quality,
    double Oee,
    OeeMetricDefinitionsResponse Definitions);
