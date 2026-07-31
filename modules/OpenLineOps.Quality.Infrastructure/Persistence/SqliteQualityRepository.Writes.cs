using Microsoft.Data.Sqlite;
using OpenLineOps.Quality.Application.Persistence;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.Testing;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

public sealed partial class SqliteQualityRepository
{
    public async ValueTask<QualityWriteResult> AddTestPlanRevisionAsync(
        TestPlanRevision testPlanRevision,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testPlanRevision);
        ValidateWriteArguments(createdBy, idempotency, "test-plan-revision", testPlanRevision.Id.Value);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-plan-revision",
                    testPlanRevision.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO quality_test_plan_revisions (
                        test_plan_revision_id, test_plan_id, revision_number,
                        created_at_utc, created_by, document_json)
                    VALUES (
                        $test_plan_revision_id, $test_plan_id, $revision_number,
                        $created_at_utc, $created_by, $document_json);
                    """;
                command.Parameters.AddWithValue(
                    "$test_plan_revision_id",
                    testPlanRevision.Id.Value.ToString("D"));
                command.Parameters.AddWithValue("$test_plan_id", testPlanRevision.TestPlanId.Value.ToString("D"));
                command.Parameters.AddWithValue("$revision_number", testPlanRevision.RevisionNumber);
                command.Parameters.AddWithValue(
                    "$created_at_utc",
                    FormatTimestamp(testPlanRevision.CreatedAtUtc));
                command.Parameters.AddWithValue("$created_by", createdBy);
                command.Parameters.AddWithValue(
                    "$document_json",
                    Serialize(QualityDocumentMapper.ToDocument(testPlanRevision)));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return new QualityWriteResult(QualityWriteStatus.ResourceConflict);
            }

            await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-plan-revision",
                    testPlanRevision.Id.Value,
                    resultRevision: 1,
                    testPlanRevision.CreatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<QualityWriteResult> AddCalibrationAssetAsync(
        CalibrationAsset calibrationAsset,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibrationAsset);
        ValidateWriteArguments(createdBy, idempotency, "calibration-asset", calibrationAsset.Id.Value);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "calibration-asset",
                    calibrationAsset.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO quality_calibration_assets (
                        calibration_asset_id, asset_code, instrument_id,
                        valid_until_utc, created_by, document_json)
                    VALUES (
                        $calibration_asset_id, $asset_code, $instrument_id,
                        $valid_until_utc, $created_by, $document_json);
                    """;
                command.Parameters.AddWithValue(
                    "$calibration_asset_id",
                    calibrationAsset.Id.Value.ToString("D"));
                command.Parameters.AddWithValue("$asset_code", calibrationAsset.AssetCode);
                command.Parameters.AddWithValue("$instrument_id", calibrationAsset.InstrumentId);
                command.Parameters.AddWithValue(
                    "$valid_until_utc",
                    FormatTimestamp(calibrationAsset.ValidUntilUtc));
                command.Parameters.AddWithValue("$created_by", createdBy);
                command.Parameters.AddWithValue(
                    "$document_json",
                    Serialize(QualityDocumentMapper.ToDocument(calibrationAsset)));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return new QualityWriteResult(QualityWriteStatus.ResourceConflict);
            }

            await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "calibration-asset",
                    calibrationAsset.Id.Value,
                    resultRevision: 1,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<QualityWriteResult> AddTestAttemptAsync(
        TestAttempt testAttempt,
        string createdBy,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testAttempt);
        ValidateWriteArguments(createdBy, idempotency, "test-attempt", testAttempt.Id.Value);
        if (testAttempt.Status != TestAttemptStatus.Running
            || testAttempt.Measurements.Count != 0)
        {
            throw new ArgumentException("Only a newly started test attempt can be added.", nameof(testAttempt));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-attempt",
                    testAttempt.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO quality_test_attempts (
                            test_attempt_id, test_plan_revision_id, production_unit_id,
                            station_id, attempt_number, started_at_utc, current_revision,
                            status, judgement, created_by)
                        VALUES (
                            $test_attempt_id, $test_plan_revision_id, $production_unit_id,
                            $station_id, $attempt_number, $started_at_utc, 1,
                            $status, $judgement, $created_by);
                        """;
                    command.Parameters.AddWithValue(
                        "$test_attempt_id",
                        testAttempt.Id.Value.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$test_plan_revision_id",
                        testAttempt.TestPlanRevisionId.Value.ToString("D"));
                    command.Parameters.AddWithValue("$production_unit_id", testAttempt.ProductionUnitId);
                    command.Parameters.AddWithValue("$station_id", testAttempt.StationId);
                    command.Parameters.AddWithValue("$attempt_number", testAttempt.AttemptNumber);
                    command.Parameters.AddWithValue(
                        "$started_at_utc",
                        FormatTimestamp(testAttempt.StartedAtUtc));
                    command.Parameters.AddWithValue("$status", testAttempt.Status.ToString());
                    command.Parameters.AddWithValue("$judgement", testAttempt.Judgement.ToString());
                    command.Parameters.AddWithValue("$created_by", createdBy);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await InsertAttemptEventAsync(
                        connection,
                        transaction,
                        testAttempt.Id.Value,
                        revision: 1,
                        "Created",
                        createdBy,
                        testAttempt.StartedAtUtc,
                        new PersistedAttemptCreated(
                            testAttempt.Id.Value,
                            testAttempt.TestPlanRevisionId.Value,
                            testAttempt.ProductionUnitId,
                            testAttempt.StationId,
                            testAttempt.AttemptNumber,
                            testAttempt.StartedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return new QualityWriteResult(QualityWriteStatus.ResourceConflict);
            }

            await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-attempt",
                    testAttempt.Id.Value,
                    resultRevision: 1,
                    testAttempt.StartedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<QualityWriteResult> AppendMeasurementAsync(
        TestAttempt testAttempt,
        MeasurementResult measurementResult,
        Nonconformance? nonconformance,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testAttempt);
        ArgumentNullException.ThrowIfNull(measurementResult);
        ValidateWriteArguments(actorId, idempotency, "test-attempt", testAttempt.Id.Value);
        if (measurementResult.AttemptId != testAttempt.Id
            || !testAttempt.Measurements.Any(candidate => candidate.Id == measurementResult.Id)
            || testAttempt.Status != TestAttemptStatus.Running)
        {
            throw new ArgumentException(
                "Measurement must be a recorded fact on the supplied running attempt.",
                nameof(measurementResult));
        }

        if (nonconformance is not null
            && (nonconformance.TestAttemptId != testAttempt.Id
                || nonconformance.MeasurementResultId != measurementResult.Id
                || nonconformance.Status != NonconformanceStatus.Open))
        {
            throw new ArgumentException(
                "Nonconformance must be an open fact for the appended measurement.",
                nameof(nonconformance));
        }

        var nextRevision = checked(expectedRevision + 1);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-attempt",
                    testAttempt.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE quality_test_attempts
                    SET current_revision = $next_revision,
                        status = $status,
                        judgement = $judgement
                    WHERE test_attempt_id = $test_attempt_id
                      AND current_revision = $expected_revision
                      AND status = 'Running';
                    """;
                update.Parameters.AddWithValue("$next_revision", nextRevision);
                update.Parameters.AddWithValue("$status", testAttempt.Status.ToString());
                update.Parameters.AddWithValue("$judgement", testAttempt.Judgement.ToString());
                update.Parameters.AddWithValue("$test_attempt_id", testAttempt.Id.Value.ToString("D"));
                update.Parameters.AddWithValue("$expected_revision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var actual = await GetCurrentRevisionAsync(
                            connection,
                            transaction,
                            "quality_test_attempts",
                            "test_attempt_id",
                            testAttempt.Id.Value,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return actual is null
                        ? new QualityWriteResult(QualityWriteStatus.NotFound)
                        : new QualityWriteResult(QualityWriteStatus.RevisionConflict, actual);
                }
            }

            try
            {
                var document = QualityDocumentMapper.ToDocument(measurementResult);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO quality_measurement_results (
                            measurement_result_id, test_attempt_id, attempt_revision,
                            test_characteristic_id, measured_at_utc, judgement,
                            evidence_sha256, document_json)
                        VALUES (
                            $measurement_result_id, $test_attempt_id, $attempt_revision,
                            $test_characteristic_id, $measured_at_utc, $judgement,
                            $evidence_sha256, $document_json);
                        """;
                    command.Parameters.AddWithValue(
                        "$measurement_result_id",
                        measurementResult.Id.Value.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$test_attempt_id",
                        testAttempt.Id.Value.ToString("D"));
                    command.Parameters.AddWithValue("$attempt_revision", nextRevision);
                    command.Parameters.AddWithValue(
                        "$test_characteristic_id",
                        measurementResult.CharacteristicId.Value.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$measured_at_utc",
                        FormatTimestamp(measurementResult.MeasuredAtUtc));
                    command.Parameters.AddWithValue("$judgement", measurementResult.Judgement.ToString());
                    command.Parameters.AddWithValue("$evidence_sha256", measurementResult.EvidenceSha256);
                    command.Parameters.AddWithValue("$document_json", Serialize(document));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await InsertAttemptEventAsync(
                        connection,
                        transaction,
                        testAttempt.Id.Value,
                        nextRevision,
                        "MeasurementRecorded",
                        actorId,
                        measurementResult.MeasuredAtUtc,
                        document,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (nonconformance is not null)
                {
                    await InsertNonconformanceAsync(
                            connection,
                            transaction,
                            nonconformance,
                            testAttempt.StationId,
                            actorId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await InsertIdempotencyAsync(
                        connection,
                        transaction,
                        idempotency,
                        "test-attempt",
                        testAttempt.Id.Value,
                        nextRevision,
                        measurementResult.MeasuredAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return new QualityWriteResult(QualityWriteStatus.ResourceConflict);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, nextRevision);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<QualityWriteResult> CompleteTestAttemptAsync(
        TestAttempt testAttempt,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testAttempt);
        ValidateWriteArguments(actorId, idempotency, "test-attempt", testAttempt.Id.Value);
        if (testAttempt.Status != TestAttemptStatus.Completed
            || testAttempt.CompletedAtUtc is null)
        {
            throw new ArgumentException("Only a completed attempt can append completion.", nameof(testAttempt));
        }

        var nextRevision = checked(expectedRevision + 1);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-attempt",
                    testAttempt.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE quality_test_attempts
                    SET current_revision = $next_revision,
                        status = $status,
                        judgement = $judgement
                    WHERE test_attempt_id = $test_attempt_id
                      AND current_revision = $expected_revision
                      AND status = 'Running';
                    """;
                update.Parameters.AddWithValue("$next_revision", nextRevision);
                update.Parameters.AddWithValue("$status", testAttempt.Status.ToString());
                update.Parameters.AddWithValue("$judgement", testAttempt.Judgement.ToString());
                update.Parameters.AddWithValue("$test_attempt_id", testAttempt.Id.Value.ToString("D"));
                update.Parameters.AddWithValue("$expected_revision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var actual = await GetCurrentRevisionAsync(
                            connection,
                            transaction,
                            "quality_test_attempts",
                            "test_attempt_id",
                            testAttempt.Id.Value,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return actual is null
                        ? new QualityWriteResult(QualityWriteStatus.NotFound)
                        : new QualityWriteResult(QualityWriteStatus.RevisionConflict, actual);
                }
            }

            var completed = new PersistedAttemptCompleted(
                testAttempt.CompletedAtUtc.Value,
                testAttempt.Judgement);
            await InsertAttemptEventAsync(
                    connection,
                    transaction,
                    testAttempt.Id.Value,
                    nextRevision,
                    "Completed",
                    actorId,
                    testAttempt.CompletedAtUtc.Value,
                    completed,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "test-attempt",
                    testAttempt.Id.Value,
                    nextRevision,
                    testAttempt.CompletedAtUtc.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, nextRevision);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<QualityWriteResult> DispositionNonconformanceAsync(
        Nonconformance nonconformance,
        string actorId,
        int expectedRevision,
        QualityIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nonconformance);
        ValidateWriteArguments(actorId, idempotency, "nonconformance", nonconformance.Id.Value);
        if (nonconformance.Status != NonconformanceStatus.Dispositioned
            || nonconformance.Disposition is null
            || nonconformance.DispositionedBy is null
            || nonconformance.DispositionReason is null
            || nonconformance.DispositionedAtUtc is null)
        {
            throw new ArgumentException(
                "Only a dispositioned nonconformance can append disposition evidence.",
                nameof(nonconformance));
        }

        var nextRevision = checked(expectedRevision + 1);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "nonconformance",
                    nonconformance.Id.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status != QualityIdempotencyStatus.Missing)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return FromIdempotency(replay);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE quality_nonconformances
                    SET current_revision = $next_revision,
                        status = $status
                    WHERE nonconformance_id = $nonconformance_id
                      AND current_revision = $expected_revision
                      AND status = 'Open';
                    """;
                update.Parameters.AddWithValue("$next_revision", nextRevision);
                update.Parameters.AddWithValue("$status", nonconformance.Status.ToString());
                update.Parameters.AddWithValue(
                    "$nonconformance_id",
                    nonconformance.Id.Value.ToString("D"));
                update.Parameters.AddWithValue("$expected_revision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var actual = await GetCurrentRevisionAsync(
                            connection,
                            transaction,
                            "quality_nonconformances",
                            "nonconformance_id",
                            nonconformance.Id.Value,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return actual is null
                        ? new QualityWriteResult(QualityWriteStatus.NotFound)
                        : new QualityWriteResult(QualityWriteStatus.RevisionConflict, actual);
                }
            }

            var disposition = new PersistedNonconformanceDispositioned(
                nonconformance.Disposition.Value,
                nonconformance.DispositionedBy,
                nonconformance.DispositionReason,
                nonconformance.DispositionedAtUtc.Value);
            await InsertNonconformanceEventAsync(
                    connection,
                    transaction,
                    nonconformance.Id.Value,
                    nextRevision,
                    "Dispositioned",
                    actorId,
                    nonconformance.DispositionedAtUtc.Value,
                    disposition,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    idempotency,
                    "nonconformance",
                    nonconformance.Id.Value,
                    nextRevision,
                    nonconformance.DispositionedAtUtc.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityWriteResult(QualityWriteStatus.Applied, nextRevision);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async ValueTask InsertAttemptEventAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid testAttemptId,
        int revision,
        string eventKind,
        string actorId,
        DateTimeOffset occurredAtUtc,
        TDocument document,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quality_test_attempt_events (
                test_attempt_id, revision, event_kind, actor_id,
                occurred_at_utc, document_json)
            VALUES (
                $test_attempt_id, $revision, $event_kind, $actor_id,
                $occurred_at_utc, $document_json);
            """;
        command.Parameters.AddWithValue("$test_attempt_id", testAttemptId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        command.Parameters.AddWithValue("$actor_id", actorId);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(occurredAtUtc));
        command.Parameters.AddWithValue("$document_json", Serialize(document));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertNonconformanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Nonconformance nonconformance,
        string stationId,
        string actorId,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_nonconformances (
                    nonconformance_id, test_attempt_id, measurement_result_id,
                    production_unit_id, station_id, current_revision, status,
                    raised_at_utc, created_by)
                VALUES (
                    $nonconformance_id, $test_attempt_id, $measurement_result_id,
                    $production_unit_id, $station_id, 1, $status,
                    $raised_at_utc, $created_by);
                """;
            command.Parameters.AddWithValue(
                "$nonconformance_id",
                nonconformance.Id.Value.ToString("D"));
            command.Parameters.AddWithValue(
                "$test_attempt_id",
                nonconformance.TestAttemptId.Value.ToString("D"));
            command.Parameters.AddWithValue(
                "$measurement_result_id",
                nonconformance.MeasurementResultId.Value.ToString("D"));
            command.Parameters.AddWithValue("$production_unit_id", nonconformance.ProductionUnitId);
            command.Parameters.AddWithValue("$station_id", stationId);
            command.Parameters.AddWithValue("$status", nonconformance.Status.ToString());
            command.Parameters.AddWithValue(
                "$raised_at_utc",
                FormatTimestamp(nonconformance.RaisedAtUtc));
            command.Parameters.AddWithValue("$created_by", actorId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertNonconformanceEventAsync(
                connection,
                transaction,
                nonconformance.Id.Value,
                revision: 1,
                "Opened",
                actorId,
                nonconformance.RaisedAtUtc,
                new PersistedNonconformanceOpened(
                    nonconformance.Id.Value,
                    nonconformance.TestAttemptId.Value,
                    nonconformance.MeasurementResultId.Value,
                    nonconformance.Code,
                    nonconformance.Description,
                    nonconformance.Severity,
                    nonconformance.RaisedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertNonconformanceEventAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nonconformanceId,
        int revision,
        string eventKind,
        string actorId,
        DateTimeOffset occurredAtUtc,
        TDocument document,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quality_nonconformance_events (
                nonconformance_id, revision, event_kind, actor_id,
                occurred_at_utc, document_json)
            VALUES (
                $nonconformance_id, $revision, $event_kind, $actor_id,
                $occurred_at_utc, $document_json);
            """;
        command.Parameters.AddWithValue("$nonconformance_id", nonconformanceId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        command.Parameters.AddWithValue("$actor_id", actorId);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(occurredAtUtc));
        command.Parameters.AddWithValue("$document_json", Serialize(document));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static QualityWriteResult FromIdempotency(QualityIdempotencyCheck replay)
    {
        return replay.Status switch
        {
            QualityIdempotencyStatus.Replay =>
                new QualityWriteResult(QualityWriteStatus.Replayed, replay.ResultRevision),
            QualityIdempotencyStatus.Conflict =>
                new QualityWriteResult(QualityWriteStatus.ResourceConflict),
            _ => throw new InvalidOperationException("Missing idempotency cannot be converted to a write result.")
        };
    }

    private static void ValidateWriteArguments(
        string actorId,
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId)
    {
        if (string.IsNullOrWhiteSpace(actorId)
            || char.IsWhiteSpace(actorId[0])
            || char.IsWhiteSpace(actorId[^1]))
        {
            throw new ArgumentException("Actor ID must be canonical.", nameof(actorId));
        }

        ValidateIdempotency(idempotency, resourceKind, resourceId);
    }
}
