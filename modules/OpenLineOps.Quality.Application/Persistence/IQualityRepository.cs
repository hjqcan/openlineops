using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.TestPlans;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Application.Persistence;

public interface IQualityRepository
{
    ValueTask<QualityIdempotencyCheck> CheckIdempotencyAsync(
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId,
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> AddTestPlanRevisionAsync(
        TestPlanRevision testPlanRevision,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<VersionedTestPlanRevision?> GetTestPlanRevisionAsync(
        Guid testPlanRevisionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<VersionedTestPlanRevision>> ListTestPlanRevisionsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> AddCalibrationAssetAsync(
        CalibrationAsset calibrationAsset,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<VersionedCalibrationAsset?> GetCalibrationAssetAsync(
        Guid calibrationAssetId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<VersionedCalibrationAsset>> ListCalibrationAssetsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> AddTestAttemptAsync(
        TestAttempt testAttempt,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<VersionedTestAttempt?> GetTestAttemptAsync(
        Guid testAttemptId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<VersionedTestAttempt>> ListTestAttemptsAsync(
        string? stationId,
        string? productionUnitId,
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> AppendMeasurementAsync(
        TestAttempt testAttempt,
        MeasurementResult measurementResult,
        Nonconformance? nonconformance,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> CompleteTestAttemptAsync(
        TestAttempt testAttempt,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<VersionedNonconformance?> GetNonconformanceAsync(
        Guid nonconformanceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<VersionedNonconformance>> ListNonconformancesAsync(
        string? stationId,
        string? productionUnitId,
        NonconformanceStatus? status,
        CancellationToken cancellationToken = default);

    ValueTask<QualityWriteResult> DispositionNonconformanceAsync(
        Nonconformance nonconformance,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);
}

public sealed record QualityIdempotencyContext(
    string Scope,
    string Key,
    string RequestSha256);

public sealed record QualityIdempotencyCheck(
    QualityIdempotencyStatus Status,
    int? ResultRevision = null);

public enum QualityIdempotencyStatus
{
    Missing = 0,
    Replay = 1,
    Conflict = 2
}

public sealed record QualityWriteResult(
    QualityWriteStatus Status,
    int? ResultRevision = null);

public enum QualityWriteStatus
{
    Applied = 0,
    Replayed = 1,
    ResourceConflict = 2,
    RevisionConflict = 3,
    NotFound = 4
}

public sealed record VersionedTestPlanRevision(
    TestPlanRevision Value,
    int Revision,
    string CreatedBy);

public sealed record VersionedCalibrationAsset(
    CalibrationAsset Value,
    int Revision,
    string CreatedBy);

public sealed record VersionedTestAttempt(
    TestAttempt Value,
    int Revision,
    string CreatedBy);

public sealed record VersionedNonconformance(
    Nonconformance Value,
    int Revision,
    string StationId,
    string CreatedBy);
