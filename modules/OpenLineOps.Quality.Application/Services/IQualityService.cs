using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Domain.Nonconformances;

namespace OpenLineOps.Quality.Application.Services;

public interface IQualityService
{
    ValueTask<Result<TestPlanRevisionDetails>> CreateTestPlanRevisionAsync(
        CreateTestPlanRevisionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<TestPlanRevisionDetails>> GetTestPlanRevisionAsync(
        Guid testPlanRevisionId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<TestPlanRevisionDetails>>> ListTestPlanRevisionsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result<CalibrationAssetDetails>> CreateCalibrationAssetAsync(
        CreateCalibrationAssetCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CalibrationAssetDetails>> GetCalibrationAssetAsync(
        Guid calibrationAssetId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<CalibrationAssetDetails>>> ListCalibrationAssetsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result<TestAttemptDetails>> CreateTestAttemptAsync(
        CreateTestAttemptCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<TestAttemptDetails>> AppendMeasurementAsync(
        AppendMeasurementCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<TestAttemptDetails>> CompleteTestAttemptAsync(
        CompleteTestAttemptCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<TestAttemptDetails>> GetTestAttemptAsync(
        Guid testAttemptId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<TestAttemptDetails>>> ListTestAttemptsAsync(
        string? stationId,
        string? productionUnitId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<NonconformanceDetails>> GetNonconformanceAsync(
        Guid nonconformanceId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<NonconformanceDetails>>> ListNonconformancesAsync(
        string? stationId,
        string? productionUnitId,
        NonconformanceStatus? status,
        CancellationToken cancellationToken = default);

    ValueTask<Result<NonconformanceDetails>> DispositionNonconformanceAsync(
        DispositionNonconformanceCommand command,
        CancellationToken cancellationToken = default);
}
