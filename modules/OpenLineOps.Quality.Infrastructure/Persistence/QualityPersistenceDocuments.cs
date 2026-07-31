using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

internal sealed record PersistedLimitSet(
    Guid LimitSetId,
    string Unit,
    decimal? LowerLimit,
    bool LowerInclusive,
    decimal? UpperLimit,
    bool UpperInclusive);

internal sealed record PersistedTestCharacteristic(
    Guid TestCharacteristicId,
    string Code,
    string DisplayName,
    string Unit,
    string StepVersion,
    bool IsRequired,
    PersistedLimitSet? Limits);

internal sealed record PersistedTestPlanRevision(
    Guid TestPlanRevisionId,
    Guid TestPlanId,
    int RevisionNumber,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<PersistedTestCharacteristic> Characteristics);

internal sealed record PersistedCalibrationAsset(
    Guid CalibrationAssetId,
    string AssetCode,
    string InstrumentId,
    string CertificateId,
    DateTimeOffset CalibratedAtUtc,
    DateTimeOffset ValidUntilUtc);

internal sealed record PersistedAttemptCreated(
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    string ProductionUnitId,
    string StationId,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc);

internal sealed record PersistedMeasurementResult(
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

internal sealed record PersistedAttemptCompleted(
    DateTimeOffset CompletedAtUtc,
    TestAttemptJudgement Judgement);

internal sealed record PersistedNonconformanceOpened(
    Guid NonconformanceId,
    Guid TestAttemptId,
    Guid MeasurementResultId,
    string Code,
    string Description,
    NonconformanceSeverity Severity,
    DateTimeOffset RaisedAtUtc);

internal sealed record PersistedNonconformanceDispositioned(
    NonconformanceDisposition Disposition,
    string DispositionedBy,
    string Reason,
    DateTimeOffset DispositionedAtUtc);
