using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Application.Persistence;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.TestPlans;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Application.Services;

public sealed class QualityService(
    IQualityRepository repository,
    IClock clock)
    : IQualityService
{
    private const string TestAttemptResource = "test-attempt";
    private const string NonconformanceResource = "nonconformance";

    public async ValueTask<Result<TestPlanRevisionDetails>> CreateTestPlanRevisionAsync(
        CreateTestPlanRevisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var characteristics = command.Characteristics
                ?? throw new ArgumentException(
                    "Characteristics collection is required.",
                    nameof(command));
            var aggregate = TestPlanRevision.Create(
                new TestPlanRevisionId(command.TestPlanRevisionId),
                new TestPlanId(command.TestPlanId),
                command.RevisionNumber,
                command.DisplayName,
                characteristics.Select(ToDomain).ToArray(),
                clock.UtcNow);
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.test-plan-revision.create",
                command.IdempotencyKey,
                command);
            var write = await repository
                .AddTestPlanRevisionAsync(aggregate, actorId, idempotency, cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is not (QualityWriteStatus.Applied or QualityWriteStatus.Replayed))
            {
                return Conflict<TestPlanRevisionDetails>(
                    "Quality.TestPlanRevision.Exists",
                    "The test plan revision identity, revision number, or idempotency key conflicts with an existing record.");
            }

            return await GetTestPlanRevisionAsync(command.TestPlanRevisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Validation<TestPlanRevisionDetails>(
                "Quality.TestPlanRevision.Invalid",
                exception.Message);
        }
    }

    public async ValueTask<Result<TestPlanRevisionDetails>> GetTestPlanRevisionAsync(
        Guid testPlanRevisionId,
        CancellationToken cancellationToken = default)
    {
        if (testPlanRevisionId == Guid.Empty)
        {
            return Validation<TestPlanRevisionDetails>(
                "Quality.TestPlanRevision.Id",
                "Test plan revision ID cannot be empty.");
        }

        var stored = await repository
            .GetTestPlanRevisionAsync(testPlanRevisionId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null
            ? NotFound<TestPlanRevisionDetails>(
                "Quality.TestPlanRevision.NotFound",
                $"Test plan revision {testPlanRevisionId:D} was not found.")
            : Result.Success(QualityDetailsMapper.ToDetails(stored));
    }

    public async ValueTask<Result<IReadOnlyCollection<TestPlanRevisionDetails>>> ListTestPlanRevisionsAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await repository.ListTestPlanRevisionsAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyCollection<TestPlanRevisionDetails>>(
            stored.Select(QualityDetailsMapper.ToDetails).ToArray());
    }

    public async ValueTask<Result<CalibrationAssetDetails>> CreateCalibrationAssetAsync(
        CreateCalibrationAssetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var aggregate = CalibrationAsset.Create(
                new CalibrationAssetId(command.CalibrationAssetId),
                command.AssetCode,
                command.InstrumentId,
                command.CertificateId,
                command.CalibratedAtUtc,
                command.ValidUntilUtc);
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.calibration-asset.create",
                command.IdempotencyKey,
                command);
            var write = await repository
                .AddCalibrationAssetAsync(aggregate, actorId, idempotency, cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is not (QualityWriteStatus.Applied or QualityWriteStatus.Replayed))
            {
                return Conflict<CalibrationAssetDetails>(
                    "Quality.CalibrationAsset.Exists",
                    "The calibration asset identity or idempotency key conflicts with an existing record.");
            }

            return await GetCalibrationAssetAsync(command.CalibrationAssetId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Validation<CalibrationAssetDetails>(
                "Quality.CalibrationAsset.Invalid",
                exception.Message);
        }
    }

    public async ValueTask<Result<CalibrationAssetDetails>> GetCalibrationAssetAsync(
        Guid calibrationAssetId,
        CancellationToken cancellationToken = default)
    {
        if (calibrationAssetId == Guid.Empty)
        {
            return Validation<CalibrationAssetDetails>(
                "Quality.CalibrationAsset.Id",
                "Calibration asset ID cannot be empty.");
        }

        var stored = await repository
            .GetCalibrationAssetAsync(calibrationAssetId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null
            ? NotFound<CalibrationAssetDetails>(
                "Quality.CalibrationAsset.NotFound",
                $"Calibration asset {calibrationAssetId:D} was not found.")
            : Result.Success(QualityDetailsMapper.ToDetails(stored, clock.UtcNow));
    }

    public async ValueTask<Result<IReadOnlyCollection<CalibrationAssetDetails>>> ListCalibrationAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await repository.ListCalibrationAssetsAsync(cancellationToken).ConfigureAwait(false);
        var atUtc = clock.UtcNow;
        return Result.Success<IReadOnlyCollection<CalibrationAssetDetails>>(
            stored.Select(asset => QualityDetailsMapper.ToDetails(asset, atUtc)).ToArray());
    }

    public async ValueTask<Result<TestAttemptDetails>> CreateTestAttemptAsync(
        CreateTestAttemptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var stationId = QualityApplicationGuard.CanonicalText(command.StationId, nameof(command.StationId));
            var plan = await repository
                .GetTestPlanRevisionAsync(command.TestPlanRevisionId, cancellationToken)
                .ConfigureAwait(false);
            if (plan is null)
            {
                return NotFound<TestAttemptDetails>(
                    "Quality.TestPlanRevision.NotFound",
                    $"Test plan revision {command.TestPlanRevisionId:D} was not found.");
            }

            var aggregate = TestAttempt.Start(
                new TestAttemptId(command.TestAttemptId),
                plan.Value,
                command.ProductionUnitId,
                stationId,
                command.AttemptNumber,
                command.StartedAtUtc);
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.test-attempt.create",
                command.IdempotencyKey,
                command);
            var write = await repository
                .AddTestAttemptAsync(aggregate, actorId, idempotency, cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is not (QualityWriteStatus.Applied or QualityWriteStatus.Replayed))
            {
                return Conflict<TestAttemptDetails>(
                    "Quality.TestAttempt.Exists",
                    "The test attempt identity or idempotency key conflicts with an existing record.");
            }

            return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Validation<TestAttemptDetails>("Quality.TestAttempt.Invalid", exception.Message);
        }
    }

    public async ValueTask<Result<TestAttemptDetails>> AppendMeasurementAsync(
        AppendMeasurementCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var stationId = QualityApplicationGuard.CanonicalText(command.StationId, nameof(command.StationId));
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.measurement.append",
                command.IdempotencyKey,
                command);
            var replay = await repository
                .CheckIdempotencyAsync(
                    idempotency,
                    TestAttemptResource,
                    command.TestAttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status == QualityIdempotencyStatus.Replay)
            {
                return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (replay.Status == QualityIdempotencyStatus.Conflict)
            {
                return IdempotencyConflict<TestAttemptDetails>();
            }

            var stored = await repository
                .GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null || !string.Equals(stored.Value.StationId, stationId, StringComparison.Ordinal))
            {
                return NotFound<TestAttemptDetails>(
                    "Quality.TestAttempt.NotFound",
                    $"Test attempt {command.TestAttemptId:D} was not found for the authenticated station.");
            }

            if (stored.Revision != command.ExpectedRevision)
            {
                return RevisionConflict<TestAttemptDetails>(stored.Revision);
            }

            var calibration = await repository
                .GetCalibrationAssetAsync(command.CalibrationAssetId, cancellationToken)
                .ConfigureAwait(false);
            if (calibration is null)
            {
                return NotFound<TestAttemptDetails>(
                    "Quality.CalibrationAsset.NotFound",
                    $"Calibration asset {command.CalibrationAssetId:D} was not found.");
            }

            var measurement = stored.Value.RecordMeasurement(
                new MeasurementResultId(command.MeasurementResultId),
                new TestCharacteristicId(command.TestCharacteristicId),
                command.RawValue,
                command.NormalizedValue,
                calibration.Value,
                command.StepVersion,
                command.EvidenceSha256,
                command.MeasuredAtUtc);
            var nonconformance = CreateNonconformanceIfRequired(
                stored.Value,
                measurement,
                clock.UtcNow);
            var write = await repository
                .AppendMeasurementAsync(
                    stored.Value,
                    measurement,
                    nonconformance,
                    actorId,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.Status == QualityWriteStatus.Replayed)
            {
                return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (write.Status == QualityWriteStatus.RevisionConflict)
            {
                return RevisionConflict<TestAttemptDetails>(write.ResultRevision);
            }

            if (write.Status != QualityWriteStatus.Applied)
            {
                return Conflict<TestAttemptDetails>(
                    "Quality.Measurement.NotPersisted",
                    "Measurement could not be appended because its identity conflicts with an existing fact.");
            }

            return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Validation<TestAttemptDetails>("Quality.Measurement.Invalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<TestAttemptDetails>("Quality.Measurement.Conflict", exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Validation<TestAttemptDetails>("Quality.Measurement.Characteristic", exception.Message);
        }
    }

    public async ValueTask<Result<TestAttemptDetails>> CompleteTestAttemptAsync(
        CompleteTestAttemptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var stationId = QualityApplicationGuard.CanonicalText(command.StationId, nameof(command.StationId));
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.test-attempt.complete",
                command.IdempotencyKey,
                command);
            var replay = await repository
                .CheckIdempotencyAsync(
                    idempotency,
                    TestAttemptResource,
                    command.TestAttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status == QualityIdempotencyStatus.Replay)
            {
                return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (replay.Status == QualityIdempotencyStatus.Conflict)
            {
                return IdempotencyConflict<TestAttemptDetails>();
            }

            var stored = await repository
                .GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null || !string.Equals(stored.Value.StationId, stationId, StringComparison.Ordinal))
            {
                return NotFound<TestAttemptDetails>(
                    "Quality.TestAttempt.NotFound",
                    $"Test attempt {command.TestAttemptId:D} was not found for the authenticated station.");
            }

            if (stored.Revision != command.ExpectedRevision)
            {
                return RevisionConflict<TestAttemptDetails>(stored.Revision);
            }

            stored.Value.Complete(command.CompletedAtUtc);
            var write = await repository
                .CompleteTestAttemptAsync(
                    stored.Value,
                    actorId,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is QualityWriteStatus.Applied or QualityWriteStatus.Replayed)
            {
                return await GetTestAttemptAsync(command.TestAttemptId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return write.Status == QualityWriteStatus.RevisionConflict
                ? RevisionConflict<TestAttemptDetails>(write.ResultRevision)
                : Conflict<TestAttemptDetails>(
                    "Quality.TestAttempt.CompletionConflict",
                    "Test attempt completion could not be persisted.");
        }
        catch (ArgumentException exception)
        {
            return Validation<TestAttemptDetails>("Quality.TestAttempt.CompletionInvalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<TestAttemptDetails>("Quality.TestAttempt.CompletionConflict", exception.Message);
        }
    }

    public async ValueTask<Result<TestAttemptDetails>> GetTestAttemptAsync(
        Guid testAttemptId,
        CancellationToken cancellationToken = default)
    {
        if (testAttemptId == Guid.Empty)
        {
            return Validation<TestAttemptDetails>(
                "Quality.TestAttempt.Id",
                "Test attempt ID cannot be empty.");
        }

        var stored = await repository
            .GetTestAttemptAsync(testAttemptId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null
            ? NotFound<TestAttemptDetails>(
                "Quality.TestAttempt.NotFound",
                $"Test attempt {testAttemptId:D} was not found.")
            : Result.Success(QualityDetailsMapper.ToDetails(stored));
    }

    public async ValueTask<Result<IReadOnlyCollection<TestAttemptDetails>>> ListTestAttemptsAsync(
        string? stationId,
        string? productionUnitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            stationId = stationId is null
                ? null
                : QualityApplicationGuard.CanonicalText(stationId, nameof(stationId));
            productionUnitId = productionUnitId is null
                ? null
                : QualityApplicationGuard.CanonicalText(productionUnitId, nameof(productionUnitId));
            var stored = await repository
                .ListTestAttemptsAsync(stationId, productionUnitId, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyCollection<TestAttemptDetails>>(
                stored.Select(QualityDetailsMapper.ToDetails).ToArray());
        }
        catch (ArgumentException exception)
        {
            return Validation<IReadOnlyCollection<TestAttemptDetails>>(
                "Quality.TestAttempt.QueryInvalid",
                exception.Message);
        }
    }

    public async ValueTask<Result<NonconformanceDetails>> GetNonconformanceAsync(
        Guid nonconformanceId,
        CancellationToken cancellationToken = default)
    {
        if (nonconformanceId == Guid.Empty)
        {
            return Validation<NonconformanceDetails>(
                "Quality.Nonconformance.Id",
                "Nonconformance ID cannot be empty.");
        }

        var stored = await repository
            .GetNonconformanceAsync(nonconformanceId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null
            ? NotFound<NonconformanceDetails>(
                "Quality.Nonconformance.NotFound",
                $"Nonconformance {nonconformanceId:D} was not found.")
            : Result.Success(QualityDetailsMapper.ToDetails(stored));
    }

    public async ValueTask<Result<IReadOnlyCollection<NonconformanceDetails>>> ListNonconformancesAsync(
        string? stationId,
        string? productionUnitId,
        NonconformanceStatus? status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            stationId = stationId is null
                ? null
                : QualityApplicationGuard.CanonicalText(stationId, nameof(stationId));
            productionUnitId = productionUnitId is null
                ? null
                : QualityApplicationGuard.CanonicalText(productionUnitId, nameof(productionUnitId));
            var stored = await repository
                .ListNonconformancesAsync(stationId, productionUnitId, status, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyCollection<NonconformanceDetails>>(
                stored.Select(QualityDetailsMapper.ToDetails).ToArray());
        }
        catch (ArgumentException exception)
        {
            return Validation<IReadOnlyCollection<NonconformanceDetails>>(
                "Quality.Nonconformance.QueryInvalid",
                exception.Message);
        }
    }

    public async ValueTask<Result<NonconformanceDetails>> DispositionNonconformanceAsync(
        DispositionNonconformanceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var actorId = QualityApplicationGuard.CanonicalText(command.ActorId, nameof(command.ActorId));
            var idempotency = QualityApplicationGuard.Idempotency(
                "quality.nonconformance.disposition",
                command.IdempotencyKey,
                command);
            var replay = await repository
                .CheckIdempotencyAsync(
                    idempotency,
                    NonconformanceResource,
                    command.NonconformanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status == QualityIdempotencyStatus.Replay)
            {
                return await GetNonconformanceAsync(command.NonconformanceId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (replay.Status == QualityIdempotencyStatus.Conflict)
            {
                return IdempotencyConflict<NonconformanceDetails>();
            }

            var stored = await repository
                .GetNonconformanceAsync(command.NonconformanceId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null)
            {
                return NotFound<NonconformanceDetails>(
                    "Quality.Nonconformance.NotFound",
                    $"Nonconformance {command.NonconformanceId:D} was not found.");
            }

            if (stored.Revision != command.ExpectedRevision)
            {
                return RevisionConflict<NonconformanceDetails>(stored.Revision);
            }

            stored.Value.ApplyDisposition(command.Disposition, actorId, command.Reason, clock.UtcNow);
            var write = await repository
                .DispositionNonconformanceAsync(
                    stored.Value,
                    actorId,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is QualityWriteStatus.Applied or QualityWriteStatus.Replayed)
            {
                return await GetNonconformanceAsync(command.NonconformanceId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return write.Status == QualityWriteStatus.RevisionConflict
                ? RevisionConflict<NonconformanceDetails>(write.ResultRevision)
                : Conflict<NonconformanceDetails>(
                    "Quality.Nonconformance.DispositionConflict",
                    "Nonconformance disposition could not be persisted.");
        }
        catch (ArgumentException exception)
        {
            return Validation<NonconformanceDetails>(
                "Quality.Nonconformance.DispositionInvalid",
                exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<NonconformanceDetails>(
                "Quality.Nonconformance.DispositionConflict",
                exception.Message);
        }
    }

    private static TestCharacteristic ToDomain(CreateTestCharacteristicCommand characteristic)
    {
        ArgumentNullException.ThrowIfNull(characteristic);
        var limits = characteristic.Limits is null
            ? null
            : LimitSet.Create(
                new LimitSetId(characteristic.Limits.LimitSetId),
                characteristic.Limits.Unit,
                characteristic.Limits.LowerLimit,
                characteristic.Limits.LowerInclusive,
                characteristic.Limits.UpperLimit,
                characteristic.Limits.UpperInclusive);
        return TestCharacteristic.Create(
            new TestCharacteristicId(characteristic.TestCharacteristicId),
            characteristic.Code,
            characteristic.DisplayName,
            characteristic.Unit,
            characteristic.StepVersion,
            characteristic.IsRequired,
            limits);
    }

    private static Nonconformance? CreateNonconformanceIfRequired(
        TestAttempt attempt,
        MeasurementResult measurement,
        DateTimeOffset nowUtc)
    {
        if (measurement.Judgement is not (
            MeasurementJudgement.Failed or MeasurementJudgement.Invalid))
        {
            return null;
        }

        var isInvalid = measurement.Judgement == MeasurementJudgement.Invalid;
        var raisedAtUtc = nowUtc < measurement.MeasuredAtUtc ? measurement.MeasuredAtUtc : nowUtc;
        return Nonconformance.Open(
            NonconformanceId.New(),
            attempt,
            measurement,
            isInvalid ? "measurement.invalid" : "measurement.out-of-limit",
            isInvalid
                ? $"Measurement {measurement.CharacteristicCode} is invalid because its calibration status is {measurement.CalibrationStatus}."
                : $"Measurement {measurement.CharacteristicCode} is outside its frozen acceptance limits.",
            isInvalid ? NonconformanceSeverity.Critical : NonconformanceSeverity.Major,
            raisedAtUtc);
    }

    private static Result<T> Validation<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.Validation(code, message));

    private static Result<T> NotFound<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.NotFound(code, message));

    private static Result<T> Conflict<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.Conflict(code, message));

    private static Result<T> IdempotencyConflict<T>() =>
        Conflict<T>(
            "Quality.IdempotencyKey.Reused",
            "Idempotency key was reused with different immutable command evidence.");

    private static Result<T> RevisionConflict<T>(int? actualRevision) =>
        Conflict<T>(
            "Quality.Revision.Mismatch",
            actualRevision is null
                ? "Expected revision no longer matches the persisted resource."
                : $"Expected revision no longer matches persisted revision {actualRevision.Value}.");
}
