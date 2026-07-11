using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class PostgreSqlProductionCoordinationStore :
    IProductionRunRepository,
    IProductionRunExecutionPlanRepository,
    IResourceLeaseRepository,
    IStationJobCoordinationStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgreSqlProductionCoordinationStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            || char.IsWhiteSpace(connectionString[0])
            || char.IsWhiteSpace(connectionString[^1])
            ? throw new ArgumentException(
                "PostgreSQL Production coordination connection string must be canonical.",
                nameof(connectionString))
            : connectionString;
    }

    public void Dispose() => _schemaLock.Dispose();

    public async ValueTask<bool> TryAddAsync(
        ProductionRun run,
        ProductionRunExecutionPlan executionPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executionPlan);
        if (run.ExecutionStatus != ExecutionStatus.Pending || executionPlan.RunId != run.Id)
        {
            throw new ArgumentException(
                "A new Production Run must be Pending and own its frozen execution plan.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_production_runs (
                run_id, document_json, execution_plan_json, revision, execution_status,
                production_line_definition_id, last_transition_at_utc)
            VALUES (
                @run_id, @document_json::jsonb, @execution_plan_json::jsonb, 0,
                @execution_status, @line_id, @last_transition_at_utc)
            ON CONFLICT (run_id) DO NOTHING;
            """;
        AddRunParameters(command, run);
        command.Parameters.AddWithValue(
            "execution_plan_json",
            JsonSerializer.Serialize(
                ProductionRunExecutionPlanSnapshotMapper.ToSnapshot(executionPlan),
                JsonOptions));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<long> SaveAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextRevision = checked(expectedRevision + 1);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE olo_production_runs
                SET document_json = @document_json::jsonb,
                    revision = @next_revision,
                    execution_status = @execution_status,
                    production_line_definition_id = @line_id,
                    last_transition_at_utc = @last_transition_at_utc
                WHERE run_id = @run_id
                  AND revision = @expected_revision;
                """;
            AddRunParameters(command, run);
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            command.Parameters.AddWithValue("next_revision", nextRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProductionRunConcurrencyException(run.Id, expectedRevision);
            }
        }

        if (run.IsTerminal)
        {
            await using var outbox = connection.CreateCommand();
            outbox.Transaction = transaction;
            outbox.CommandText = """
                INSERT INTO olo_production_terminal_outbox (
                    run_id, document_json, occurred_at_utc, attempt_count, last_error)
                VALUES (@run_id, @document_json::jsonb, @occurred_at_utc, 0, NULL)
                ON CONFLICT (run_id) DO NOTHING;
                """;
            outbox.Parameters.AddWithValue("run_id", run.Id.Value);
            outbox.Parameters.AddWithValue(
                "document_json",
                JsonSerializer.Serialize(ProductionRunSnapshotMapper.ToSnapshot(run), JsonOptions));
            outbox.Parameters.AddWithValue(
                "occurred_at_utc",
                run.CompletedAtUtc ?? throw new InvalidOperationException(
                    "Terminal Production Run has no completion timestamp."));
            await outbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return nextRevision;
    }

    public async ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_runs
            WHERE run_id = @run_id;
            """;
        command.Parameters.AddWithValue("run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionRunPersistenceEntry(DeserializeRun(reader.GetString(0)), reader.GetInt64(1))
            : null;
    }

    public async ValueTask<ProductionRunExecutionPlan?> GetByRunIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_plan_json::text
            FROM olo_production_runs
            WHERE run_id = @run_id;
            """;
        command.Parameters.AddWithValue("run_id", runId.Value);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (json is null)
        {
            return null;
        }

        return ProductionRunExecutionPlanSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionRunExecutionPlan>(json, JsonOptions)
            ?? throw new InvalidDataException("PostgreSQL execution plan document is empty."));
    }

    public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default) =>
        ListRunsAsync(null, null, null, cancellationToken);

    public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
        string? productionLineDefinitionId = null,
        string? stationSystemId = null,
        string? slotId = null,
        CancellationToken cancellationToken = default) =>
        ListRunsAsync(productionLineDefinitionId, stationSystemId, slotId, cancellationToken);

    public async ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
        ListPendingTerminalOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, attempt_count, last_error
            FROM olo_production_terminal_outbox
            ORDER BY occurred_at_utc, run_id
            LIMIT @maximum_count;
            """;
        command.Parameters.AddWithValue("maximum_count", maximumCount);
        var items = new List<ProductionRunTerminalOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ProductionRunTerminalOutboxItem(
                DeserializeRun(reader.GetString(0)).ToSnapshot(),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return items;
    }

    public ValueTask MarkTerminalOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default) =>
        ExecuteRequiredAsync(
            "DELETE FROM olo_production_terminal_outbox WHERE run_id = @id;",
            runId.Value,
            "Production Run terminal outbox item",
            cancellationToken);

    public async ValueTask RecordTerminalOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE olo_production_terminal_outbox
            SET attempt_count = attempt_count + 1,
                last_error = @last_error
            WHERE run_id = @run_id;
            """;
        command.Parameters.AddWithValue("run_id", runId.Value);
        command.Parameters.AddWithValue(
            "last_error",
            failureDescription.Length <= 4096
                ? failureDescription
                : failureDescription[..4096]);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Production Run terminal outbox item {runId} does not exist.");
        }
    }

    public async ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var requested = resources.Distinct().OrderBy(static resource => resource.CanonicalKey).ToArray();
        if (requested.Length == 0 || requested.Length != resources.Count || duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Resource leases require unique resources and positive duration.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var resource in requested)
        {
            // The fencing row is the permanent serialization point for a physical resource.
            // Creating and locking it before inspecting the expiring lease prevents two
            // transactions from both observing an absent lease and later overwriting each other.
            await using (var ensureFenceCommand = connection.CreateCommand())
            {
                ensureFenceCommand.Transaction = transaction;
                ensureFenceCommand.CommandText = """
                    INSERT INTO olo_resource_fencing_tokens (
                        resource_kind, resource_id, fencing_token)
                    VALUES (@kind, @resource_id, 0)
                    ON CONFLICT (resource_kind, resource_id) DO NOTHING;
                    """;
                AddResourceParameters(ensureFenceCommand, resource);
                await ensureFenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var fenceLockCommand = connection.CreateCommand())
            {
                fenceLockCommand.Transaction = transaction;
                fenceLockCommand.CommandText = """
                    SELECT fencing_token
                    FROM olo_resource_fencing_tokens
                    WHERE resource_kind = @kind AND resource_id = @resource_id
                    FOR UPDATE;
                    """;
                AddResourceParameters(fenceLockCommand, resource);
                _ = await fenceLockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Resource fencing row was not created.");
            }

            await using var lockCommand = connection.CreateCommand();
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = """
                SELECT run_id, operation_run_id, expires_at_utc
                FROM olo_resource_leases
                WHERE resource_kind = @kind AND resource_id = @resource_id
                FOR UPDATE;
                """;
            AddResourceParameters(lockCommand, resource);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.GetFieldValue<DateTimeOffset>(2) > acquiredAtUtc
                && (reader.GetGuid(0) != runId.Value
                    || !string.Equals(reader.GetString(1), operationRunId, StringComparison.Ordinal)))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
        }

        var leases = new List<ResourceLease>(requested.Length);
        foreach (var resource in requested)
        {
            long token;
            await using (var tokenCommand = connection.CreateCommand())
            {
                tokenCommand.Transaction = transaction;
                tokenCommand.CommandText = """
                    UPDATE olo_resource_fencing_tokens
                    SET fencing_token = fencing_token + 1
                    WHERE resource_kind = @kind AND resource_id = @resource_id
                    RETURNING fencing_token;
                    """;
                AddResourceParameters(tokenCommand, resource);
                token = Convert.ToInt64(
                    await tokenCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            var lease = new ResourceLease(
                resource,
                runId,
                operationRunId,
                token,
                acquiredAtUtc,
                acquiredAtUtc.Add(duration));
            await using var leaseCommand = connection.CreateCommand();
            leaseCommand.Transaction = transaction;
            leaseCommand.CommandText = """
                INSERT INTO olo_resource_leases (
                    resource_kind, resource_id, run_id, operation_run_id,
                    fencing_token, acquired_at_utc, expires_at_utc)
                VALUES (
                    @kind, @resource_id, @run_id, @operation_run_id,
                    @fencing_token, @acquired_at_utc, @expires_at_utc)
                ON CONFLICT (resource_kind, resource_id)
                DO UPDATE SET
                    run_id = EXCLUDED.run_id,
                    operation_run_id = EXCLUDED.operation_run_id,
                    fencing_token = EXCLUDED.fencing_token,
                    acquired_at_utc = EXCLUDED.acquired_at_utc,
                    expires_at_utc = EXCLUDED.expires_at_utc;
                """;
            AddResourceParameters(leaseCommand, resource);
            leaseCommand.Parameters.AddWithValue("run_id", runId.Value);
            leaseCommand.Parameters.AddWithValue("operation_run_id", operationRunId);
            leaseCommand.Parameters.AddWithValue("fencing_token", token);
            leaseCommand.Parameters.AddWithValue("acquired_at_utc", acquiredAtUtc);
            leaseCommand.Parameters.AddWithValue("expires_at_utc", lease.ExpiresAtUtc);
            await leaseCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            leases.Add(lease);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return leases;
    }

    public ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default) =>
        ExecuteOwnerLeaseAsync(
            "DELETE FROM olo_resource_leases WHERE run_id = @run_id AND operation_run_id = @operation_run_id;",
            runId,
            operationRunId,
            cancellationToken);

    public ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default) =>
        ExecuteOwnerLeaseAsync(
            "UPDATE olo_resource_leases SET expires_at_utc = 'infinity' WHERE run_id = @run_id AND operation_run_id = @operation_run_id;",
            runId,
            operationRunId,
            cancellationToken);

    public async ValueTask<bool> TryEnqueueAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(request, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_station_job_outbox (
                message_id, idempotency_key, payload_json, created_at_utc,
                attempt_count, last_error, published_at_utc)
            VALUES (@message_id, @idempotency_key, @payload_json::jsonb, @created_at_utc, 0, NULL, NULL)
            ON CONFLICT (idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("message_id", request.MessageId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("created_at_utc", request.RequestedAtUtc);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!added)
        {
            await EnsureSameStationJobAsync(
                connection,
                request.IdempotencyKey,
                payload,
                cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    public async ValueTask<StationJobCompleted?> GetCompletionAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json::text
            FROM olo_station_job_result_inbox
            WHERE idempotency_key = @idempotency_key;
            """;
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? null
            : JsonSerializer.Deserialize<StationJobCompleted>(json, JsonOptions)
              ?? throw new InvalidDataException("Station result inbox payload is empty.");
    }

    public async ValueTask RecordCompletionAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(completion, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_station_job_result_inbox (
                message_id, idempotency_key, payload_json, received_at_utc)
            VALUES (@message_id, @idempotency_key, @payload_json::jsonb, @received_at_utc)
            ON CONFLICT (idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("message_id", completion.MessageId);
        command.Parameters.AddWithValue("idempotency_key", completion.IdempotencyKey);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("received_at_utc", completion.CompletedAtUtc);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!added)
        {
            await EnsureSameCompletionAsync(
                connection,
                completion.IdempotencyKey,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask RecordAcceptedAsync(
        StationJobAccepted accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return RecordStationEventAsync(
            accepted.MessageId,
            accepted.JobId,
            accepted.IdempotencyKey,
            nameof(StationJobAccepted),
            JsonSerializer.Serialize(accepted, JsonOptions),
            accepted.AcceptedAtUtc,
            cancellationToken);
    }

    public ValueTask RecordProgressAsync(
        StationJobProgressed progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return RecordStationEventAsync(
            progress.MessageId,
            progress.JobId,
            progress.IdempotencyKey,
            nameof(StationJobProgressed),
            JsonSerializer.Serialize(progress, JsonOptions),
            progress.ProgressedAtUtc,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<StationJobEventInboxItem>> ListEventsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, job_id, idempotency_key, kind, payload_json::text, occurred_at_utc
            FROM olo_station_job_event_inbox
            WHERE job_id = @job_id
            ORDER BY occurred_at_utc, message_id;
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        var result = new List<StationJobEventInboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StationJobEventInboxItem(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }

    public async ValueTask<IReadOnlyCollection<StationJobOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, idempotency_key, payload_json::text, attempt_count, created_at_utc
            FROM olo_station_job_outbox
            WHERE published_at_utc IS NULL
            ORDER BY created_at_utc, message_id
            LIMIT @maximum_count;
            """;
        command.Parameters.AddWithValue("maximum_count", maximumCount);
        var items = new List<StationJobOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new StationJobOutboxItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return items;
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        ExecuteRequiredAsync(
            "UPDATE olo_station_job_outbox SET published_at_utc = now() WHERE message_id = @id;",
            messageId,
            "Station job outbox message",
            cancellationToken);

    public async ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE olo_station_job_outbox
            SET attempt_count = attempt_count + 1,
                last_error = @last_error
            WHERE message_id = @message_id;
            """;
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue(
            "last_error",
            failure.Length <= 4096 ? failure : failure[..4096]);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Station job outbox message {messageId:D} does not exist.");
        }
    }

    private async ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRunsAsync(
        string? lineId,
        string? stationSystemId,
        string? slotId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_runs
            WHERE execution_status IN ('Pending', 'Running')
              AND (@line_id IS NULL OR production_line_definition_id = @line_id)
            ORDER BY last_transition_at_utc, run_id;
            """;
        command.Parameters.AddWithValue("line_id", (object?)lineId ?? DBNull.Value);
        var entries = new List<ProductionRunPersistenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var run = DeserializeRun(reader.GetString(0));
            if (stationSystemId is not null
                && run.OperationDefinitions.All(definition => !string.Equals(
                    definition.StationSystemId,
                    stationSystemId,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            if (slotId is not null && run.OperationDefinitions.All(definition =>
                    definition.ResourceRequirements.All(resource =>
                        resource.Kind != ResourceKind.Slot
                        || !string.Equals(resource.ResourceId, slotId, StringComparison.Ordinal))))
            {
                continue;
            }

            entries.Add(new ProductionRunPersistenceEntry(run, reader.GetInt64(1)));
        }

        return entries;
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS olo_production_runs (
                    run_id uuid PRIMARY KEY,
                    document_json jsonb NOT NULL,
                    execution_plan_json jsonb NOT NULL,
                    revision bigint NOT NULL,
                    execution_status text NOT NULL,
                    production_line_definition_id text NOT NULL,
                    last_transition_at_utc timestamptz NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_olo_production_runs_active
                    ON olo_production_runs(execution_status, production_line_definition_id, last_transition_at_utc);

                CREATE TABLE IF NOT EXISTS olo_production_terminal_outbox (
                    run_id uuid PRIMARY KEY REFERENCES olo_production_runs(run_id) ON DELETE CASCADE,
                    document_json jsonb NOT NULL,
                    occurred_at_utc timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    last_error text NULL
                );

                CREATE TABLE IF NOT EXISTS olo_resource_fencing_tokens (
                    resource_kind text NOT NULL,
                    resource_id text NOT NULL,
                    fencing_token bigint NOT NULL,
                    PRIMARY KEY(resource_kind, resource_id)
                );
                CREATE TABLE IF NOT EXISTS olo_resource_leases (
                    resource_kind text NOT NULL,
                    resource_id text NOT NULL,
                    run_id uuid NOT NULL,
                    operation_run_id text NOT NULL,
                    fencing_token bigint NOT NULL,
                    acquired_at_utc timestamptz NOT NULL,
                    expires_at_utc timestamptz NOT NULL,
                    PRIMARY KEY(resource_kind, resource_id)
                );
                CREATE INDEX IF NOT EXISTS ix_olo_resource_leases_owner
                    ON olo_resource_leases(run_id, operation_run_id);

                CREATE TABLE IF NOT EXISTS olo_station_job_outbox (
                    message_id uuid PRIMARY KEY,
                    idempotency_key text NOT NULL UNIQUE,
                    payload_json jsonb NOT NULL,
                    created_at_utc timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    last_error text NULL,
                    published_at_utc timestamptz NULL
                );
                CREATE TABLE IF NOT EXISTS olo_station_job_result_inbox (
                    message_id uuid PRIMARY KEY,
                    idempotency_key text NOT NULL UNIQUE,
                    payload_json jsonb NOT NULL,
                    received_at_utc timestamptz NOT NULL
                );
                CREATE TABLE IF NOT EXISTS olo_station_job_event_inbox (
                    message_id uuid PRIMARY KEY,
                    job_id uuid NOT NULL,
                    idempotency_key text NOT NULL,
                    kind text NOT NULL,
                    payload_json jsonb NOT NULL,
                    occurred_at_utc timestamptz NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_olo_station_job_event_inbox_job
                    ON olo_station_job_event_inbox(job_id, occurred_at_utc, message_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask ValidateSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["olo_production_runs"] = ["run_id", "document_json", "execution_plan_json", "revision", "execution_status", "production_line_definition_id", "last_transition_at_utc"],
            ["olo_production_terminal_outbox"] = ["run_id", "document_json", "occurred_at_utc", "attempt_count", "last_error"],
            ["olo_resource_fencing_tokens"] = ["resource_kind", "resource_id", "fencing_token"],
            ["olo_resource_leases"] = ["resource_kind", "resource_id", "run_id", "operation_run_id", "fencing_token", "acquired_at_utc", "expires_at_utc"],
            ["olo_station_job_outbox"] = ["message_id", "idempotency_key", "payload_json", "created_at_utc", "attempt_count", "last_error", "published_at_utc"],
            ["olo_station_job_result_inbox"] = ["message_id", "idempotency_key", "payload_json", "received_at_utc"],
            ["olo_station_job_event_inbox"] = ["message_id", "job_id", "idempotency_key", "kind", "payload_json", "occurred_at_utc"]
        };
        foreach (var table in expected)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = @table_name
                ORDER BY ordinal_position;
                """;
            command.Parameters.AddWithValue("table_name", table.Key);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }

            if (!columns.SequenceEqual(table.Value, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"PostgreSQL table {table.Key} does not match the only supported Production coordination schema.");
            }
        }
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddRunParameters(NpgsqlCommand command, ProductionRun run)
    {
        command.Parameters.AddWithValue("run_id", run.Id.Value);
        command.Parameters.AddWithValue(
            "document_json",
            JsonSerializer.Serialize(ProductionRunSnapshotMapper.ToSnapshot(run), JsonOptions));
        command.Parameters.AddWithValue("execution_status", run.ExecutionStatus.ToString());
        command.Parameters.AddWithValue("line_id", run.ProductionLineDefinitionId);
        command.Parameters.AddWithValue("last_transition_at_utc", run.LastTransitionAtUtc);
    }

    private static void AddResourceParameters(NpgsqlCommand command, ResourceRequirement resource)
    {
        command.Parameters.AddWithValue("kind", resource.Kind.ToString());
        command.Parameters.AddWithValue("resource_id", resource.ResourceId);
    }

    private async ValueTask ExecuteOwnerLeaseAsync(
        string sql,
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("run_id", runId.Value);
        command.Parameters.AddWithValue("operation_run_id", operationRunId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordStationEventAsync(
        Guid messageId,
        Guid jobId,
        string idempotencyKey,
        string kind,
        string payload,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_station_job_event_inbox (
                message_id, job_id, idempotency_key, kind, payload_json, occurred_at_utc)
            VALUES (@message_id, @job_id, @idempotency_key, @kind, @payload_json::jsonb, @occurred_at_utc)
            ON CONFLICT (message_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await using var existing = connection.CreateCommand();
            existing.CommandText = """
                SELECT job_id, idempotency_key, kind, payload_json::text
                FROM olo_station_job_event_inbox
                WHERE message_id = @message_id;
                """;
            existing.Parameters.AddWithValue("message_id", messageId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetGuid(0) != jobId
                || !string.Equals(reader.GetString(1), idempotencyKey, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Station event message {messageId:D} was reused with different identity.");
            }

            var existingPayload = reader.GetString(3);
            using var existingJson = JsonDocument.Parse(existingPayload);
            using var candidateJson = JsonDocument.Parse(payload);
            if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
            {
                throw new InvalidOperationException(
                    $"Station event message {messageId:D} was reused with different evidence.");
            }
        }
    }

    private async ValueTask ExecuteRequiredAsync(
        string sql,
        Guid id,
        string description,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"{description} {id:D} does not exist.");
        }
    }

    private static async ValueTask EnsureSameStationJobAsync(
        NpgsqlConnection connection,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken) =>
        await EnsureSamePayloadAsync(
            connection,
            "olo_station_job_outbox",
            idempotencyKey,
            payload,
            "Station job",
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask EnsureSameCompletionAsync(
        NpgsqlConnection connection,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken) =>
        await EnsureSamePayloadAsync(
            connection,
            "olo_station_job_result_inbox",
            idempotencyKey,
            payload,
            "Station completion",
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask EnsureSamePayloadAsync(
        NpgsqlConnection connection,
        string table,
        string idempotencyKey,
        string payload,
        string description,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json::text FROM {table} WHERE idempotency_key = @idempotency_key;";
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        var existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        using var existingJson = JsonDocument.Parse(existing ?? throw new InvalidDataException(
            $"{description} idempotency conflict has no stored payload."));
        using var candidateJson = JsonDocument.Parse(payload);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                $"{description} idempotency key '{idempotencyKey}' was reused with different evidence.");
        }
    }

    private static ProductionRun DeserializeRun(string json) =>
        ProductionRunSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionRun>(json, JsonOptions)
            ?? throw new InvalidDataException("PostgreSQL Production Run document is empty."));
}
