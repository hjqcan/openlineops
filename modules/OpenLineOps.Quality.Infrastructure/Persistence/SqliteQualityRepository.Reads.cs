using Microsoft.Data.Sqlite;
using OpenLineOps.Quality.Application.Persistence;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

public sealed partial class SqliteQualityRepository
{
    public async ValueTask<VersionedTestPlanRevision?> GetTestPlanRevisionAsync(
        Guid testPlanRevisionId,
        CancellationToken cancellationToken = default)
    {
        if (testPlanRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Test plan revision ID cannot be empty.", nameof(testPlanRevisionId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var stored = await LoadTestPlanRevisionAsync(
                connection,
                transaction,
                testPlanRevisionId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<IReadOnlyCollection<VersionedTestPlanRevision>> ListTestPlanRevisionsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json, created_by
            FROM quality_test_plan_revisions
            ORDER BY test_plan_id, revision_number, test_plan_revision_id;
            """;
        var results = new List<VersionedTestPlanRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new VersionedTestPlanRevision(
                QualityDocumentMapper.ToDomain(
                    Deserialize<PersistedTestPlanRevision>(reader.GetString(0))),
                Revision: 1,
                reader.GetString(1)));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async ValueTask<VersionedCalibrationAsset?> GetCalibrationAssetAsync(
        Guid calibrationAssetId,
        CancellationToken cancellationToken = default)
    {
        if (calibrationAssetId == Guid.Empty)
        {
            throw new ArgumentException("Calibration asset ID cannot be empty.", nameof(calibrationAssetId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var stored = await LoadCalibrationAssetAsync(
                connection,
                transaction,
                calibrationAssetId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<IReadOnlyCollection<VersionedCalibrationAsset>> ListCalibrationAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json, created_by
            FROM quality_calibration_assets
            ORDER BY asset_code, calibration_asset_id;
            """;
        var results = new List<VersionedCalibrationAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new VersionedCalibrationAsset(
                QualityDocumentMapper.ToDomain(
                    Deserialize<PersistedCalibrationAsset>(reader.GetString(0))),
                Revision: 1,
                reader.GetString(1)));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async ValueTask<VersionedTestAttempt?> GetTestAttemptAsync(
        Guid testAttemptId,
        CancellationToken cancellationToken = default)
    {
        if (testAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Test attempt ID cannot be empty.", nameof(testAttemptId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var stored = await LoadTestAttemptAsync(
                connection,
                transaction,
                testAttemptId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<IReadOnlyCollection<VersionedTestAttempt>> ListTestAttemptsAsync(
        string? stationId,
        string? productionUnitId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT test_attempt_id
                FROM quality_test_attempts
                WHERE ($station_id IS NULL OR station_id = $station_id)
                  AND ($production_unit_id IS NULL OR production_unit_id = $production_unit_id)
                ORDER BY started_at_utc DESC, test_attempt_id
                LIMIT 500;
                """;
            command.Parameters.AddWithValue("$station_id", (object?)stationId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$production_unit_id",
                (object?)productionUnitId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
            }
        }

        var results = new List<VersionedTestAttempt>(ids.Count);
        foreach (var id in ids)
        {
            results.Add(await LoadTestAttemptAsync(connection, transaction, id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Quality test attempt {id:D} disappeared during query."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async ValueTask<VersionedNonconformance?> GetNonconformanceAsync(
        Guid nonconformanceId,
        CancellationToken cancellationToken = default)
    {
        if (nonconformanceId == Guid.Empty)
        {
            throw new ArgumentException("Nonconformance ID cannot be empty.", nameof(nonconformanceId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var stored = await LoadNonconformanceAsync(
                connection,
                transaction,
                nonconformanceId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<IReadOnlyCollection<VersionedNonconformance>> ListNonconformancesAsync(
        string? stationId,
        string? productionUnitId,
        NonconformanceStatus? status,
        CancellationToken cancellationToken = default)
    {
        if (status is not null && !Enum.IsDefined(status.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not defined.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT nonconformance_id
                FROM quality_nonconformances
                WHERE ($station_id IS NULL OR station_id = $station_id)
                  AND ($production_unit_id IS NULL OR production_unit_id = $production_unit_id)
                  AND ($status IS NULL OR status = $status)
                ORDER BY raised_at_utc DESC, nonconformance_id
                LIMIT 500;
                """;
            command.Parameters.AddWithValue("$station_id", (object?)stationId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$production_unit_id",
                (object?)productionUnitId ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", status?.ToString() ?? (object)DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
            }
        }

        var results = new List<VersionedNonconformance>(ids.Count);
        foreach (var id in ids)
        {
            results.Add(await LoadNonconformanceAsync(connection, transaction, id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Quality nonconformance {id:D} disappeared during query."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static async ValueTask<VersionedTestPlanRevision?> LoadTestPlanRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid testPlanRevisionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json, created_by
            FROM quality_test_plan_revisions
            WHERE test_plan_revision_id = $test_plan_revision_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$test_plan_revision_id",
            testPlanRevisionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new VersionedTestPlanRevision(
                QualityDocumentMapper.ToDomain(
                    Deserialize<PersistedTestPlanRevision>(reader.GetString(0))),
                Revision: 1,
                reader.GetString(1))
            : null;
    }

    private static async ValueTask<VersionedCalibrationAsset?> LoadCalibrationAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid calibrationAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json, created_by
            FROM quality_calibration_assets
            WHERE calibration_asset_id = $calibration_asset_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$calibration_asset_id", calibrationAssetId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new VersionedCalibrationAsset(
                QualityDocumentMapper.ToDomain(
                    Deserialize<PersistedCalibrationAsset>(reader.GetString(0))),
                Revision: 1,
                reader.GetString(1))
            : null;
    }

    private static async ValueTask<VersionedTestAttempt?> LoadTestAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid testAttemptId,
        CancellationToken cancellationToken)
    {
        int currentRevision;
        string persistedStatus;
        string persistedJudgement;
        string createdBy;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT current_revision, status, judgement, created_by
                FROM quality_test_attempts
                WHERE test_attempt_id = $test_attempt_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$test_attempt_id", testAttemptId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            currentRevision = reader.GetInt32(0);
            persistedStatus = reader.GetString(1);
            persistedJudgement = reader.GetString(2);
            createdBy = reader.GetString(3);
        }

        var created = await LoadAttemptEventAsync<PersistedAttemptCreated>(
                connection,
                transaction,
                testAttemptId,
                revision: 1,
                "Created",
                cancellationToken)
            .ConfigureAwait(false);
        var plan = await LoadTestPlanRevisionAsync(
                connection,
                transaction,
                created.TestPlanRevisionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Quality test attempt {testAttemptId:D} references a missing test plan revision.");
        var attempt = TestAttempt.Start(
            new TestAttemptId(created.TestAttemptId),
            plan.Value,
            created.ProductionUnitId,
            created.StationId,
            created.AttemptNumber,
            created.StartedAtUtc);

        var measurementDocuments = new List<PersistedMeasurementResult>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT document_json
                FROM quality_measurement_results
                WHERE test_attempt_id = $test_attempt_id
                  AND attempt_revision <= $current_revision
                ORDER BY attempt_revision;
                """;
            command.Parameters.AddWithValue("$test_attempt_id", testAttemptId.ToString("D"));
            command.Parameters.AddWithValue("$current_revision", currentRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                measurementDocuments.Add(
                    Deserialize<PersistedMeasurementResult>(reader.GetString(0)));
            }
        }

        foreach (var document in measurementDocuments)
        {
            var calibration = await LoadCalibrationAssetAsync(
                    connection,
                    transaction,
                    document.CalibrationAssetId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Quality measurement {document.MeasurementResultId:D} references a missing calibration asset.");
            var measurement = attempt.RecordMeasurement(
                new MeasurementResultId(document.MeasurementResultId),
                new TestCharacteristicId(document.TestCharacteristicId),
                document.RawValue,
                document.NormalizedValue,
                calibration.Value,
                document.StepVersion,
                document.EvidenceSha256,
                document.MeasuredAtUtc);
            if (QualityDocumentMapper.ToDocument(measurement) != document)
            {
                throw new InvalidDataException(
                    $"Quality measurement {document.MeasurementResultId:D} no longer matches its frozen evidence.");
            }
        }

        var status = ParseCanonicalEnum<TestAttemptStatus>(persistedStatus, "test attempt status");
        var judgement = ParseCanonicalEnum<TestAttemptJudgement>(
            persistedJudgement,
            "test attempt judgement");
        if (status == TestAttemptStatus.Completed)
        {
            var completed = await LoadAttemptEventByKindAsync<PersistedAttemptCompleted>(
                    connection,
                    transaction,
                    testAttemptId,
                    currentRevision,
                    "Completed",
                    cancellationToken)
                .ConfigureAwait(false);
            attempt.Complete(completed.CompletedAtUtc);
            if (attempt.Judgement != completed.Judgement)
            {
                throw new InvalidDataException(
                    $"Quality test attempt {testAttemptId:D} completion judgement is inconsistent.");
            }
        }
        else if (status != TestAttemptStatus.Running)
        {
            throw new InvalidDataException(
                $"Quality test attempt {testAttemptId:D} has unsupported persisted status '{status}'.");
        }

        if (attempt.Status != status || attempt.Judgement != judgement)
        {
            throw new InvalidDataException(
                $"Quality test attempt {testAttemptId:D} head does not match its append-only facts.");
        }

        await RequireContiguousAttemptEventsAsync(
                connection,
                transaction,
                testAttemptId,
                currentRevision,
                cancellationToken)
            .ConfigureAwait(false);
        return new VersionedTestAttempt(attempt, currentRevision, createdBy);
    }

    private static async ValueTask<VersionedNonconformance?> LoadNonconformanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nonconformanceId,
        CancellationToken cancellationToken)
    {
        int currentRevision;
        string persistedStatus;
        string stationId;
        string createdBy;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT current_revision, status, station_id, created_by
                FROM quality_nonconformances
                WHERE nonconformance_id = $nonconformance_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$nonconformance_id", nonconformanceId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            currentRevision = reader.GetInt32(0);
            persistedStatus = reader.GetString(1);
            stationId = reader.GetString(2);
            createdBy = reader.GetString(3);
        }

        var opened = await LoadNonconformanceEventAsync<PersistedNonconformanceOpened>(
                connection,
                transaction,
                nonconformanceId,
                revision: 1,
                "Opened",
                cancellationToken)
            .ConfigureAwait(false);
        var attempt = await LoadTestAttemptAsync(
                connection,
                transaction,
                opened.TestAttemptId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} references a missing test attempt.");
        var measurement = attempt.Value.Measurements.SingleOrDefault(
            candidate => candidate.Id.Value == opened.MeasurementResultId)
            ?? throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} references a missing measurement.");
        var nonconformance = Nonconformance.Open(
            new NonconformanceId(opened.NonconformanceId),
            attempt.Value,
            measurement,
            opened.Code,
            opened.Description,
            opened.Severity,
            opened.RaisedAtUtc);
        var status = ParseCanonicalEnum<NonconformanceStatus>(
            persistedStatus,
            "nonconformance status");
        if (status == NonconformanceStatus.Dispositioned)
        {
            var dispositioned =
                await LoadNonconformanceEventByKindAsync<PersistedNonconformanceDispositioned>(
                        connection,
                        transaction,
                        nonconformanceId,
                        currentRevision,
                        "Dispositioned",
                        cancellationToken)
                    .ConfigureAwait(false);
            nonconformance.ApplyDisposition(
                dispositioned.Disposition,
                dispositioned.DispositionedBy,
                dispositioned.Reason,
                dispositioned.DispositionedAtUtc);
        }
        else if (currentRevision != 1)
        {
            throw new InvalidDataException(
                $"Open Quality nonconformance {nonconformanceId:D} must remain at revision 1.");
        }

        if (nonconformance.Status != status)
        {
            throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} head does not match its append-only facts.");
        }

        await RequireContiguousNonconformanceEventsAsync(
                connection,
                transaction,
                nonconformanceId,
                currentRevision,
                cancellationToken)
            .ConfigureAwait(false);
        return new VersionedNonconformance(
            nonconformance,
            currentRevision,
            stationId,
            createdBy);
    }

    private static async ValueTask<TDocument> LoadAttemptEventAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        int revision,
        string eventKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM quality_test_attempt_events
            WHERE test_attempt_id = $test_attempt_id
              AND revision = $revision
              AND event_kind = $event_kind
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$test_attempt_id", attemptId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize<TDocument>(json)
            : throw new InvalidDataException(
                $"Quality test attempt {attemptId:D} is missing {eventKind} revision {revision}.");
    }

    private static async ValueTask<TDocument> LoadAttemptEventByKindAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        int currentRevision,
        string eventKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM quality_test_attempt_events
            WHERE test_attempt_id = $test_attempt_id
              AND revision <= $current_revision
              AND event_kind = $event_kind
            ORDER BY revision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$test_attempt_id", attemptId.ToString("D"));
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize<TDocument>(json)
            : throw new InvalidDataException(
                $"Quality test attempt {attemptId:D} is missing {eventKind} evidence.");
    }

    private static async ValueTask<TDocument> LoadNonconformanceEventAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nonconformanceId,
        int revision,
        string eventKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM quality_nonconformance_events
            WHERE nonconformance_id = $nonconformance_id
              AND revision = $revision
              AND event_kind = $event_kind
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$nonconformance_id", nonconformanceId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize<TDocument>(json)
            : throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} is missing {eventKind} revision {revision}.");
    }

    private static async ValueTask<TDocument> LoadNonconformanceEventByKindAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nonconformanceId,
        int currentRevision,
        string eventKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM quality_nonconformance_events
            WHERE nonconformance_id = $nonconformance_id
              AND revision <= $current_revision
              AND event_kind = $event_kind
            ORDER BY revision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$nonconformance_id", nonconformanceId.ToString("D"));
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        command.Parameters.AddWithValue("$event_kind", eventKind);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize<TDocument>(json)
            : throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} is missing {eventKind} evidence.");
    }

    private static async ValueTask RequireContiguousAttemptEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        int currentRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), MIN(revision), MAX(revision)
            FROM quality_test_attempt_events
            WHERE test_attempt_id = $test_attempt_id
              AND revision <= $current_revision;
            """;
        command.Parameters.AddWithValue("$test_attempt_id", attemptId.ToString("D"));
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt32(0) != currentRevision
            || reader.GetInt32(1) != 1
            || reader.GetInt32(2) != currentRevision)
        {
            throw new InvalidDataException(
                $"Quality test attempt {attemptId:D} event revisions are not contiguous.");
        }
    }

    private static async ValueTask RequireContiguousNonconformanceEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nonconformanceId,
        int currentRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), MIN(revision), MAX(revision)
            FROM quality_nonconformance_events
            WHERE nonconformance_id = $nonconformance_id
              AND revision <= $current_revision;
            """;
        command.Parameters.AddWithValue("$nonconformance_id", nonconformanceId.ToString("D"));
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt32(0) != currentRevision
            || reader.GetInt32(1) != 1
            || reader.GetInt32(2) != currentRevision)
        {
            throw new InvalidDataException(
                $"Quality nonconformance {nonconformanceId:D} event revisions are not contiguous.");
        }
    }
}
