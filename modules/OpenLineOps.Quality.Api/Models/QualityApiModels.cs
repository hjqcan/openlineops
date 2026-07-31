using System.Text.Json.Serialization;

namespace OpenLineOps.Quality.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LimitSetRequest(
    Guid LimitSetId,
    string Unit,
    decimal? LowerLimit,
    bool LowerInclusive,
    decimal? UpperLimit,
    bool UpperInclusive);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestCharacteristicRequest(
    Guid TestCharacteristicId,
    string Code,
    string DisplayName,
    string Unit,
    string StepVersion,
    bool IsRequired,
    LimitSetRequest? Limits);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTestPlanRevisionRequest(
    Guid TestPlanRevisionId,
    Guid TestPlanId,
    int RevisionNumber,
    string DisplayName,
    IReadOnlyCollection<TestCharacteristicRequest> Characteristics);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCalibrationAssetRequest(
    Guid CalibrationAssetId,
    string AssetCode,
    string InstrumentId,
    string CertificateId,
    DateTimeOffset CalibratedAtUtc,
    DateTimeOffset ValidUntilUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTestAttemptRequest(
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    string ProductionUnitId,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppendMeasurementRequest(
    int ExpectedRevision,
    Guid MeasurementResultId,
    Guid TestCharacteristicId,
    string RawValue,
    decimal NormalizedValue,
    Guid CalibrationAssetId,
    string StepVersion,
    string EvidenceSha256,
    DateTimeOffset MeasuredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteTestAttemptRequest(
    int ExpectedRevision,
    DateTimeOffset CompletedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispositionNonconformanceRequest(
    int ExpectedRevision,
    string Disposition,
    string Reason);

public sealed record LimitSetResponse(
    Guid LimitSetId,
    string Unit,
    decimal? LowerLimit,
    bool LowerInclusive,
    decimal? UpperLimit,
    bool UpperInclusive);

public sealed record TestCharacteristicResponse(
    Guid TestCharacteristicId,
    string Code,
    string DisplayName,
    string Unit,
    string StepVersion,
    bool IsRequired,
    LimitSetResponse? Limits);

public sealed record TestPlanRevisionResponse(
    Guid TestPlanRevisionId,
    Guid TestPlanId,
    int RevisionNumber,
    int ResourceRevision,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    IReadOnlyCollection<TestCharacteristicResponse> Characteristics);

public sealed record CalibrationAssetResponse(
    Guid CalibrationAssetId,
    int ResourceRevision,
    string AssetCode,
    string InstrumentId,
    string CertificateId,
    DateTimeOffset CalibratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    string Status,
    string CreatedBy);

public sealed record MeasurementResultResponse(
    Guid MeasurementResultId,
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    Guid TestCharacteristicId,
    string CharacteristicCode,
    string RawValue,
    decimal NormalizedValue,
    string Unit,
    Guid? LimitSetId,
    decimal? LowerLimit,
    bool? LowerInclusive,
    decimal? UpperLimit,
    bool? UpperInclusive,
    string Judgement,
    string InstrumentId,
    Guid CalibrationAssetId,
    string CalibrationCertificateId,
    string CalibrationStatus,
    string StepVersion,
    string EvidenceSha256,
    DateTimeOffset MeasuredAtUtc);

public sealed record TestAttemptResponse(
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    int ResourceRevision,
    string ProductionUnitId,
    string StationId,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Status,
    string Judgement,
    string? AbortReason,
    string CreatedBy,
    IReadOnlyCollection<MeasurementResultResponse> Measurements);

public sealed record NonconformanceResponse(
    Guid NonconformanceId,
    int ResourceRevision,
    Guid TestAttemptId,
    Guid MeasurementResultId,
    Guid TestPlanRevisionId,
    Guid TestCharacteristicId,
    string ProductionUnitId,
    string StationId,
    string Code,
    string Description,
    string Severity,
    string Status,
    DateTimeOffset RaisedAtUtc,
    string? Disposition,
    string? DispositionedBy,
    string? DispositionReason,
    DateTimeOffset? DispositionedAtUtc,
    string CreatedBy);
