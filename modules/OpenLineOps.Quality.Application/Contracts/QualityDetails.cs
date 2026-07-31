using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Application.Contracts;

public sealed record LimitSetDetails(
    Guid LimitSetId,
    string Unit,
    decimal? LowerLimit,
    bool LowerInclusive,
    decimal? UpperLimit,
    bool UpperInclusive);

public sealed record TestCharacteristicDetails(
    Guid TestCharacteristicId,
    string Code,
    string DisplayName,
    string Unit,
    string StepVersion,
    bool IsRequired,
    LimitSetDetails? Limits);

public sealed record TestPlanRevisionDetails(
    Guid TestPlanRevisionId,
    Guid TestPlanId,
    int RevisionNumber,
    int ResourceRevision,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    IReadOnlyCollection<TestCharacteristicDetails> Characteristics);

public sealed record CalibrationAssetDetails(
    Guid CalibrationAssetId,
    int ResourceRevision,
    string AssetCode,
    string InstrumentId,
    string CertificateId,
    DateTimeOffset CalibratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    CalibrationStatus Status,
    string CreatedBy);

public sealed record MeasurementResultDetails(
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
    MeasurementJudgement Judgement,
    string InstrumentId,
    Guid CalibrationAssetId,
    string CalibrationCertificateId,
    CalibrationStatus CalibrationStatus,
    string StepVersion,
    string EvidenceSha256,
    DateTimeOffset MeasuredAtUtc);

public sealed record TestAttemptDetails(
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    int ResourceRevision,
    string ProductionUnitId,
    string StationId,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TestAttemptStatus Status,
    TestAttemptJudgement Judgement,
    string? AbortReason,
    string CreatedBy,
    IReadOnlyCollection<MeasurementResultDetails> Measurements);

public sealed record NonconformanceDetails(
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
    NonconformanceSeverity Severity,
    NonconformanceStatus Status,
    DateTimeOffset RaisedAtUtc,
    NonconformanceDisposition? Disposition,
    string? DispositionedBy,
    string? DispositionReason,
    DateTimeOffset? DispositionedAtUtc,
    string CreatedBy);
