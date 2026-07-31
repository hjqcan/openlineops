using OpenLineOps.Quality.Domain.Nonconformances;

namespace OpenLineOps.Quality.Application.Contracts;

public sealed record CreateLimitSetCommand(
    Guid LimitSetId,
    string Unit,
    decimal? LowerLimit,
    bool LowerInclusive,
    decimal? UpperLimit,
    bool UpperInclusive);

public sealed record CreateTestCharacteristicCommand(
    Guid TestCharacteristicId,
    string Code,
    string DisplayName,
    string Unit,
    string StepVersion,
    bool IsRequired,
    CreateLimitSetCommand? Limits);

public sealed record CreateTestPlanRevisionCommand(
    Guid TestPlanRevisionId,
    Guid TestPlanId,
    int RevisionNumber,
    string DisplayName,
    IReadOnlyCollection<CreateTestCharacteristicCommand> Characteristics,
    string ActorId,
    string IdempotencyKey);

public sealed record CreateCalibrationAssetCommand(
    Guid CalibrationAssetId,
    string AssetCode,
    string InstrumentId,
    string CertificateId,
    DateTimeOffset CalibratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    string ActorId,
    string IdempotencyKey);

public sealed record CreateTestAttemptCommand(
    Guid TestAttemptId,
    Guid TestPlanRevisionId,
    string ProductionUnitId,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc,
    string StationId,
    string ActorId,
    string IdempotencyKey);

public sealed record AppendMeasurementCommand(
    Guid TestAttemptId,
    int ExpectedRevision,
    Guid MeasurementResultId,
    Guid TestCharacteristicId,
    string RawValue,
    decimal NormalizedValue,
    Guid CalibrationAssetId,
    string StepVersion,
    string EvidenceSha256,
    DateTimeOffset MeasuredAtUtc,
    string StationId,
    string ActorId,
    string IdempotencyKey);

public sealed record CompleteTestAttemptCommand(
    Guid TestAttemptId,
    int ExpectedRevision,
    DateTimeOffset CompletedAtUtc,
    string StationId,
    string ActorId,
    string IdempotencyKey);

public sealed record DispositionNonconformanceCommand(
    Guid NonconformanceId,
    int ExpectedRevision,
    NonconformanceDisposition Disposition,
    string Reason,
    string ActorId,
    string IdempotencyKey);
