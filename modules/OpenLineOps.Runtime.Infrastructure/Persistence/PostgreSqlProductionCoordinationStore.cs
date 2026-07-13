using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
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
        ProductionRunAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(admission);
        if (run.ExecutionStatus != ExecutionStatus.Pending || executionPlan.RunId != run.Id)
        {
            throw new ArgumentException(
                "A new Production Run must be Pending and own its frozen execution plan.");
        }

        var createdOutboxItem = ProductionRunCreatedOutboxItem.FromAdmission(run);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await TryReserveProductionUnitAsync(
                connection,
                transaction,
                run,
                admission,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_production_runs (
                run_id, production_unit_id, document_json, execution_plan_json, revision, execution_status,
                production_line_definition_id, last_transition_at_utc)
            VALUES (
                @run_id, @production_unit_id, @document_json::jsonb, @execution_plan_json::jsonb, 0,
                @execution_status, @line_id, @last_transition_at_utc)
            ON CONFLICT (run_id) DO NOTHING;
            """;
        AddRunParameters(command, run);
        command.Parameters.AddWithValue(
            "execution_plan_json",
            JsonSerializer.Serialize(
                ProductionRunExecutionPlanSnapshotMapper.ToSnapshot(executionPlan),
                JsonOptions));
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (added)
        {
            await using var outbox = connection.CreateCommand();
            outbox.Transaction = transaction;
            outbox.CommandText = """
                INSERT INTO olo_production_created_outbox (
                    run_id, event_id, occurred_at_utc, attempt_count, last_error)
                VALUES (@run_id, @event_id, @occurred_at_utc, 0, NULL);
                """;
            outbox.Parameters.AddWithValue("run_id", createdOutboxItem.RunId.Value);
            outbox.Parameters.AddWithValue("event_id", createdOutboxItem.EventId);
            outbox.Parameters.AddWithValue("occurred_at_utc", createdOutboxItem.OccurredAtUtc);
            if (await outbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Production Run Created-event outbox item {run.Id} was not stored atomically.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            run.ClearDomainEvents();
        }
        else
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return added;
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

        var productionUnitSynchronization = await SynchronizeProductionUnitAsync(
                connection,
                transaction,
                run,
                nextRevision,
                cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeCompletedSlotsAsync(
                connection,
                transaction,
                run,
                productionUnitSynchronization.Unit,
                cancellationToken)
            .ConfigureAwait(false);
        if (productionUnitSynchronization.PreviousDisposition
            != productionUnitSynchronization.Unit.Disposition)
        {
            await InsertTimelineAsync(
                    connection,
                    transaction,
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(),
                        productionUnitSynchronization.Unit.Id,
                        run.Id,
                        productionUnitSynchronization.PreviousDisposition,
                        productionUnitSynchronization.Unit.Disposition,
                        productionUnitSynchronization.Unit.DispositionReason,
                        run.ActorId,
                        productionUnitSynchronization.Unit.LastDispositionTransitionAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
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

    public async ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
        ListPendingCreatedOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, event_id, occurred_at_utc, attempt_count, last_error
            FROM olo_production_created_outbox
            ORDER BY occurred_at_utc, run_id
            LIMIT @maximum_count;
            """;
        command.Parameters.AddWithValue("maximum_count", maximumCount);
        var items = new List<ProductionRunCreatedOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ProductionRunCreatedOutboxItem(
                new ProductionRunId(reader.GetGuid(0)),
                reader.GetGuid(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return items;
    }

    public ValueTask MarkCreatedOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default) =>
        ExecuteRequiredAsync(
            "DELETE FROM olo_production_created_outbox WHERE run_id = @id;",
            runId.Value,
            "Production Run Created-event outbox item",
            cancellationToken);

    public async ValueTask RecordCreatedOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        if (char.IsWhiteSpace(failureDescription[0])
            || char.IsWhiteSpace(failureDescription[^1]))
        {
            throw new ArgumentException(
                "Created-event failure description must be canonical.",
                nameof(failureDescription));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE olo_production_created_outbox
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
                $"Production Run Created-event outbox item {runId} does not exist.");
        }
    }

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

    public async ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT resource_kind, resource_id, run_id, operation_run_id,
                   fencing_token, acquired_at_utc, expires_at_utc
            FROM olo_resource_leases
            ORDER BY resource_kind, resource_id;
            """;
        var leases = new List<ResourceLease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            leases.Add(new ResourceLease(
                new ResourceRequirement(
                    ParseResourceKind(reader.GetString(0)),
                    reader.GetString(1)),
                new ProductionRunId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return leases;
    }

    public async ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(resources);
        var requested = resources.Distinct().OrderBy(static resource => resource.CanonicalKey).ToArray();
        if (requested.Length == 0 || requested.Length != resources.Count || duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Resource leases require unique resources and positive duration.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var ownerLock = connection.CreateCommand())
        {
            ownerLock.Transaction = transaction;
            ownerLock.CommandText =
                "SELECT pg_advisory_xact_lock(hashtextextended(@owner_identity, 0));";
            ownerLock.Parameters.AddWithValue(
                "owner_identity",
                $"{runId.Value:D}/{operationRunId}");
            _ = await ownerLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var currentExpiries = new List<DateTimeOffset>(requested.Length);
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
                SELECT expires_at_utc
                FROM olo_resource_leases
                WHERE resource_kind = @kind AND resource_id = @resource_id
                FOR UPDATE;
                """;
            AddResourceParameters(lockCommand, resource);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currentExpiries.Add(reader.GetFieldValue<DateTimeOffset>(0));
            }
        }

        var databaseNowUtc = await ReadDatabaseClockAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        if (currentExpiries.Any(expiresAtUtc => expiresAtUtc > databaseNowUtc))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
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

            await using var leaseCommand = connection.CreateCommand();
            leaseCommand.Transaction = transaction;
            leaseCommand.CommandText = """
                INSERT INTO olo_resource_leases (
                    resource_kind, resource_id, run_id, operation_run_id,
                    fencing_token, acquired_at_utc, expires_at_utc)
                VALUES (
                    @kind, @resource_id, @run_id, @operation_run_id,
                    @fencing_token, @database_now_utc, @database_now_utc + @duration)
                ON CONFLICT (resource_kind, resource_id)
                DO UPDATE SET
                    run_id = EXCLUDED.run_id,
                    operation_run_id = EXCLUDED.operation_run_id,
                    fencing_token = EXCLUDED.fencing_token,
                    acquired_at_utc = EXCLUDED.acquired_at_utc,
                    expires_at_utc = EXCLUDED.expires_at_utc
                RETURNING acquired_at_utc, expires_at_utc;
                """;
            AddResourceParameters(leaseCommand, resource);
            leaseCommand.Parameters.AddWithValue("run_id", runId.Value);
            leaseCommand.Parameters.AddWithValue("operation_run_id", operationRunId);
            leaseCommand.Parameters.AddWithValue("fencing_token", token);
            leaseCommand.Parameters.AddWithValue("database_now_utc", databaseNowUtc);
            leaseCommand.Parameters.AddWithValue("duration", duration);
            await using var reader = await leaseCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("PostgreSQL did not return the acquired resource lease.");
            }

            leases.Add(new ResourceLease(
                resource,
                runId,
                operationRunId,
                token,
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1)));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return leases;
    }

    public async ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(evidence);
        var supplied = evidence.ToArray();
        if (supplied.Length == 0
            || supplied.Any(static item => item is null)
            || supplied.Select(static item => item.Resource).Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Resource lease validation requires non-empty unique evidence.",
                nameof(evidence));
        }

        Array.Sort(
            supplied,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.Resource.CanonicalKey,
                right.Resource.CanonicalKey));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in supplied)
        {
            await using (var fenceCommand = connection.CreateCommand())
            {
                fenceCommand.Transaction = transaction;
                fenceCommand.CommandText = """
                    SELECT fencing_token
                    FROM olo_resource_fencing_tokens
                    WHERE resource_kind = @kind AND resource_id = @resource_id
                    FOR SHARE;
                    """;
                AddResourceParameters(fenceCommand, item.Resource);
                var currentToken = await fenceCommand.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (currentToken is null
                    || Convert.ToInt64(currentToken, CultureInfo.InvariantCulture) != item.FencingToken)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ResourceLeaseFenceValidationResult.Reject(
                        $"Resource lease fence {item.Resource.CanonicalKey}/{item.FencingToken} is stale.");
                }
            }

            await using var leaseCommand = connection.CreateCommand();
            leaseCommand.Transaction = transaction;
            leaseCommand.CommandText = """
                SELECT run_id, operation_run_id, fencing_token, expires_at_utc
                FROM olo_resource_leases
                WHERE resource_kind = @kind AND resource_id = @resource_id
                FOR SHARE;
                """;
            AddResourceParameters(leaseCommand, item.Resource);
            await using var reader = await leaseCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var valid = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.GetGuid(0) == runId.Value
                && string.Equals(reader.GetString(1), operationRunId, StringComparison.Ordinal)
                && reader.GetInt64(2) == item.FencingToken
                && reader.GetFieldValue<DateTimeOffset>(3) == item.ExpiresAtUtc;
            if (!valid)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ResourceLeaseFenceValidationResult.Reject(
                    $"Resource lease fence {item.Resource.CanonicalKey}/{item.FencingToken} is missing or owned by another Operation Run.");
            }
        }

        var databaseNowUtc = await ReadDatabaseClockAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        var expired = supplied.FirstOrDefault(item => item.ExpiresAtUtc <= databaseNowUtc);
        if (expired is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return ResourceLeaseFenceValidationResult.Reject(
                $"Resource lease fence {expired.Resource.CanonicalKey}/{expired.FencingToken} is expired.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ResourceLeaseFenceValidationResult.Accept();
    }

    public async ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(claims);
        var supplied = claims.ToArray();
        if (supplied.Any(static claim => claim is null)
            || supplied.Select(static claim => claim.Resource).Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Resource lease release claims must be unique.",
                nameof(claims));
        }

        if (supplied.Length == 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var claim in supplied.OrderBy(
                     static claim => claim.Resource.CanonicalKey,
                     StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM olo_resource_leases
                WHERE resource_kind = @kind
                  AND resource_id = @resource_id
                  AND run_id = @run_id
                  AND operation_run_id = @operation_run_id
                  AND fencing_token = @fencing_token;
                """;
            AddResourceParameters(command, claim.Resource);
            command.Parameters.AddWithValue("run_id", runId.Value);
            command.Parameters.AddWithValue("operation_run_id", operationRunId);
            command.Parameters.AddWithValue("fencing_token", claim.FencingToken);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

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
        IReadOnlyCollection<ResourceLeaseChanged> resourceLeaseChanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resourceLeaseChanges);
        var orderedChanges = ValidateDispatch(request, resourceLeaseChanges);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        for (var sequence = 0; sequence < orderedChanges.Length; sequence++)
        {
            var change = orderedChanges[sequence];
            _ = await InsertStationDispatchOutboxAsync(
                connection,
                transaction,
                change.MessageId,
                request.JobId,
                change.IdempotencyKey,
                nameof(ResourceLeaseChanged),
                sequence,
                JsonSerializer.Serialize(change, JsonOptions),
                request.RequestedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        var jobAdded = await InsertStationDispatchOutboxAsync(
            connection,
            transaction,
            request.MessageId,
            request.JobId,
            request.IdempotencyKey,
            nameof(StationJobRequested),
            orderedChanges.Length,
            JsonSerializer.Serialize(request, JsonOptions),
            request.RequestedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return jobAdded;
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

    public async ValueTask<StationJobRecoveryRequired?> GetRecoveryRequiredAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json::text
            FROM olo_station_job_event_inbox
            WHERE job_id = @job_id
              AND kind = 'StationJobRecoveryRequired';
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            as string;
        return payload is null
            ? null
            : JsonSerializer.Deserialize<StationJobRecoveryRequired>(payload, JsonOptions)
              ?? throw new InvalidDataException(
                  "Station recovery-required Inbox payload is empty.");
    }

    public async ValueTask RecordCompletionAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        StationMessageContract.Validate(completion);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(completion, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockStationJobAsync(connection, transaction, completion.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (await TryEnsureExistingCompletionAsync(
                connection,
                transaction,
                completion,
                payload,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = await RequireDispatchRequestAsync(
                connection,
                transaction,
                completion.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateStationResultIdentity(
            request,
            completion.JobId,
            completion.IdempotencyKey,
            completion.AgentId,
            completion.StationId);
        if (completion.RuntimeSessionId != request.RuntimeSessionId)
        {
            throw new InvalidDataException(
                "Station completion Runtime Session does not match its dispatch request.");
        }

        var timeline = await ReadStationEventTimelineAsync(
                connection,
                transaction,
                completion.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!timeline.HasAccepted
            || timeline.HasRecoveryRequired
            || completion.CompletedAtUtc < timeline.LatestAtUtc)
        {
            throw new InvalidDataException(
                "Station completion arrived before durable acceptance or precedes its event timeline.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_station_job_result_inbox (
                message_id, idempotency_key, payload_json, received_at_utc)
            VALUES (@message_id, @idempotency_key, @payload_json::jsonb, @received_at_utc)
            ;
            """;
        command.Parameters.AddWithValue("message_id", completion.MessageId);
        command.Parameters.AddWithValue("idempotency_key", completion.IdempotencyKey);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("received_at_utc", completion.CompletedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RecordAcceptedAsync(
        StationJobAccepted accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        StationMessageContract.Validate(accepted);
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
        StationMessageContract.Validate(progress);
        return RecordStationEventAsync(
            progress.MessageId,
            progress.JobId,
            progress.IdempotencyKey,
            nameof(StationJobProgressed),
            JsonSerializer.Serialize(progress, JsonOptions),
            progress.ProgressedAtUtc,
            cancellationToken);
    }

    public ValueTask RecordRecoveryRequiredAsync(
        StationJobRecoveryRequired recoveryRequired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryRequired);
        StationMessageContract.Validate(recoveryRequired);
        return RecordStationEventAsync(
            recoveryRequired.MessageId,
            recoveryRequired.JobId,
            recoveryRequired.IdempotencyKey,
            nameof(StationJobRecoveryRequired),
            JsonSerializer.Serialize(recoveryRequired, JsonOptions),
            recoveryRequired.DetectedAtUtc,
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
            SELECT message_id, job_id, idempotency_key, kind, sequence, payload_json::text,
                   attempt_count, created_at_utc
            FROM olo_station_job_outbox AS candidate
            WHERE candidate.published_at_utc IS NULL
              AND candidate.quarantined_at_utc IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM olo_station_job_outbox AS predecessor
                  WHERE predecessor.job_id = candidate.job_id
                    AND predecessor.published_at_utc IS NULL
                    AND predecessor.sequence < candidate.sequence)
            ORDER BY candidate.created_at_utc,
                     candidate.job_id,
                     candidate.sequence,
                     candidate.message_id
            LIMIT @maximum_count;
            """;
        command.Parameters.AddWithValue("maximum_count", maximumCount);
        var items = new List<StationJobOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new StationJobOutboxItem(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return items;
    }

    public async ValueTask<StationJobRequested?> GetDispatchRequestAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Station Job id cannot be empty.", nameof(jobId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json::text
            FROM olo_station_job_outbox
            WHERE job_id = @job_id
              AND kind = 'StationJobRequested';
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return payload is null
            ? null
            : JsonSerializer.Deserialize<StationJobRequested>(payload, JsonOptions)
              ?? throw new InvalidDataException("Station dispatch request payload is empty.");
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        ExecuteRequiredAsync(
            "UPDATE olo_station_job_outbox SET published_at_utc = now() WHERE message_id = @id AND quarantined_at_utc IS NULL;",
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

    public async ValueTask QuarantineJobAsync(
        Guid jobId,
        string reason,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Station Job id cannot be empty.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (quarantinedAtUtc == default || quarantinedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station dispatch quarantine timestamp must be non-default UTC.",
                nameof(quarantinedAtUtc));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<(DateTimeOffset? PublishedAtUtc, string? Reason, DateTimeOffset? QuarantinedAtUtc)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT published_at_utc, quarantine_reason, quarantined_at_utc
                FROM olo_station_job_outbox
                WHERE job_id = @job_id
                ORDER BY sequence, message_id
                FOR UPDATE;
                """;
            read.Parameters.AddWithValue("job_id", jobId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Station dispatch Job {jobId:D} does not exist.");
        }

        var unpublished = rows.Where(static row => row.PublishedAtUtc is null).ToArray();
        if (unpublished.Length == 0)
        {
            throw new InvalidOperationException(
                $"Station dispatch Job {jobId:D} has no unpublished messages to quarantine.");
        }

        var existingEvidence = unpublished
            .Where(static row => row.QuarantinedAtUtc is not null)
            .ToArray();
        if (existingEvidence.Any(row => !string.Equals(
                row.Reason,
                reason,
                StringComparison.Ordinal))
            || existingEvidence.Select(static row => row.QuarantinedAtUtc)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                $"Station dispatch Job {jobId:D} already has different quarantine evidence.");
        }

        var effectiveTime = existingEvidence.FirstOrDefault().QuarantinedAtUtc
            ?? quarantinedAtUtc;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE olo_station_job_outbox
            SET quarantine_reason = @reason,
                quarantined_at_utc = @quarantined_at_utc
            WHERE job_id = @job_id
              AND published_at_utc IS NULL
              AND quarantined_at_utc IS NULL;
            """;
        update.Parameters.AddWithValue("job_id", jobId);
        update.Parameters.AddWithValue("reason", reason);
        update.Parameters.AddWithValue("quarantined_at_utc", effectiveTime);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != unpublished.Count(static row => row.QuarantinedAtUtc is null))
        {
            throw new InvalidOperationException(
                $"Station dispatch Job {jobId:D} quarantine update was not atomic.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<StationJobQuarantineItem>> ListQuarantinedAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, job_id, kind, sequence, quarantine_reason, quarantined_at_utc
            FROM olo_station_job_outbox
            WHERE job_id = @job_id
              AND quarantined_at_utc IS NOT NULL
            ORDER BY sequence, message_id;
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        var result = new List<StationJobQuarantineItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StationJobQuarantineItem(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
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
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await PostgreSqlSchemaInitialization.AcquireLockAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PostgreSqlProductionMaterialRepository.SchemaSql + """
                CREATE TABLE IF NOT EXISTS olo_production_runs (
                    run_id uuid PRIMARY KEY,
                    production_unit_id uuid NOT NULL,
                    document_json jsonb NOT NULL,
                    execution_plan_json jsonb NOT NULL,
                    revision bigint NOT NULL,
                    execution_status text NOT NULL,
                    production_line_definition_id text NOT NULL,
                    last_transition_at_utc timestamptz NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_olo_production_runs_active
                    ON olo_production_runs(execution_status, production_line_definition_id, last_transition_at_utc);
                CREATE UNIQUE INDEX IF NOT EXISTS uq_olo_production_runs_active_unit
                    ON olo_production_runs(production_unit_id)
                    WHERE execution_status IN ('Pending', 'Running');

                CREATE TABLE IF NOT EXISTS olo_production_terminal_outbox (
                    run_id uuid PRIMARY KEY REFERENCES olo_production_runs(run_id) ON DELETE CASCADE,
                    document_json jsonb NOT NULL,
                    occurred_at_utc timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    last_error text NULL
                );

                CREATE TABLE IF NOT EXISTS olo_production_created_outbox (
                    run_id uuid PRIMARY KEY REFERENCES olo_production_runs(run_id) ON DELETE CASCADE,
                    event_id uuid NOT NULL UNIQUE,
                    occurred_at_utc timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    last_error text NULL,
                    CHECK (
                        (attempt_count = 0 AND last_error IS NULL)
                        OR (attempt_count > 0
                            AND last_error IS NOT NULL
                            AND length(last_error) > 0
                            AND last_error = btrim(last_error)))
                );
                CREATE INDEX IF NOT EXISTS ix_olo_production_created_outbox_order
                    ON olo_production_created_outbox(occurred_at_utc, run_id);

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
                    job_id uuid NOT NULL,
                    idempotency_key text NOT NULL UNIQUE,
                    kind text NOT NULL,
                    sequence integer NOT NULL,
                    payload_json jsonb NOT NULL,
                    created_at_utc timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    last_error text NULL,
                    quarantine_reason text NULL,
                    quarantined_at_utc timestamptz NULL,
                    published_at_utc timestamptz NULL,
                    UNIQUE(job_id, sequence)
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
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask ValidateSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["olo_production_runs"] = ["run_id", "production_unit_id", "document_json", "execution_plan_json", "revision", "execution_status", "production_line_definition_id", "last_transition_at_utc"],
            ["olo_production_terminal_outbox"] = ["run_id", "document_json", "occurred_at_utc", "attempt_count", "last_error"],
            ["olo_production_created_outbox"] = ["run_id", "event_id", "occurred_at_utc", "attempt_count", "last_error"],
            ["olo_resource_fencing_tokens"] = ["resource_kind", "resource_id", "fencing_token"],
            ["olo_resource_leases"] = ["resource_kind", "resource_id", "run_id", "operation_run_id", "fencing_token", "acquired_at_utc", "expires_at_utc"],
            ["olo_station_job_outbox"] = ["message_id", "job_id", "idempotency_key", "kind", "sequence", "payload_json", "created_at_utc", "attempt_count", "last_error", "quarantine_reason", "quarantined_at_utc", "published_at_utc"],
            ["olo_station_job_result_inbox"] = ["message_id", "idempotency_key", "payload_json", "received_at_utc"],
            ["olo_station_job_event_inbox"] = ["message_id", "job_id", "idempotency_key", "kind", "payload_json", "occurred_at_utc"]
        };
        foreach (var table in expected)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
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

    private static async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT clock_timestamp();";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("PostgreSQL did not return its lease clock.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async ValueTask<bool> TryReserveProductionUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionRun run,
        ProductionRunAdmission admission,
        CancellationToken cancellationToken)
    {
        await using (var existingRun = connection.CreateCommand())
        {
            existingRun.Transaction = transaction;
            existingRun.CommandText = "SELECT 1 FROM olo_production_runs WHERE run_id = @run_id;";
            existingRun.Parameters.AddWithValue("run_id", run.Id.Value);
            if (await existingRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_units
            WHERE production_unit_id = @production_unit_id
            FOR UPDATE;
            """;
        read.Parameters.AddWithValue("production_unit_id", run.ProductionUnitId.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var unit = DeserializeProductionUnit(reader.GetString(0));
        var unitRevision = reader.GetInt64(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (unitRevision != admission.ExpectedRevision
            || unit.ToSnapshot() != admission.ProductionUnit
            || unit.Id != run.ProductionUnitId)
        {
            return false;
        }

        var result = unit.ReserveProductionRun(run.Id, run.CreatedAtUtc);
        return result.Succeeded
            && await UpdateProductionUnitAsync(
                    connection,
                    transaction,
                    unit,
                    unitRevision,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static async ValueTask<ProductionUnitSynchronization> SynchronizeProductionUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionRun run,
        long runRevision,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_units
            WHERE production_unit_id = @production_unit_id
            FOR UPDATE;
            """;
        read.Parameters.AddWithValue("production_unit_id", run.ProductionUnitId.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                $"Production Unit {run.ProductionUnitId} disappeared during Production Run {run.Id}.");
        }

        var unit = DeserializeProductionUnit(reader.GetString(0));
        var unitRevision = reader.GetInt64(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        var previousDisposition = unit.Disposition;
        var result = unit.SynchronizeProductionRun(
            run.Id,
            runRevision,
            run.Disposition,
            run.IsTerminal,
            run.FailureReason,
            run.LastTransitionAtUtc);
        if (!result.Succeeded
            || !await UpdateProductionUnitAsync(
                    connection,
                    transaction,
                    unit,
                    unitRevision,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                result.Succeeded
                    ? $"Production Unit {unit.Id} changed during atomic run synchronization."
                    : result.Message);
        }

        return new ProductionUnitSynchronization(unit, previousDisposition);
    }

    private static async ValueTask SynchronizeCompletedSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionRun run,
        ProductionUnit unit,
        CancellationToken cancellationToken)
    {
        var expectedMaterial = unit.Location is
        {
            Kind: MaterialLocationKind.CarrierPosition,
            CarrierId: { } carrierId
        }
            ? MaterialReference.ForCarrier(carrierId)
            : MaterialReference.ForProductionUnit(unit.Id);
        foreach (var completedSlot in ProductionRunSlotLifecycle.ResolveCompletedSlots(run))
        {
            if (await HasSlotCompletionEvidenceAsync(
                    connection,
                    transaction,
                    run.Id,
                    completedSlot,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT document_json::text, revision
                FROM olo_slot_occupancies
                WHERE line_id = @line_id
                  AND station_system_id = @station_system_id
                  AND slot_id = @slot_id
                FOR UPDATE;
                """;
            AddSlotIdentity(read, completedSlot.Address);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    $"Resolved Slot {completedSlot.Address} disappeared during run synchronization.");
            }

            var slot = DeserializeSlot(reader.GetString(0));
            var revision = reader.GetInt64(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (slot.Material != expectedMaterial)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} is bound to {slot.Material}, not {expectedMaterial}.");
            }

            if (slot.Status != SlotOccupancyStatus.Running)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} has no exact completion evidence for Operation Run "
                    + $"{completedSlot.OperationRunId} at fencing token {completedSlot.FencingToken}; "
                    + $"its status must be Running, not {slot.Status}.");
            }

            var completed = slot.Complete(expectedMaterial, completedSlot.CompletedAtUtc);
            if (!completed.Succeeded)
            {
                throw new InvalidDataException(completed.Message);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE olo_slot_occupancies
                SET document_json = @document_json::jsonb,
                    revision = @next_revision,
                    status = @status,
                    updated_at_utc = @updated_at_utc
                WHERE line_id = @line_id
                  AND station_system_id = @station_system_id
                  AND slot_id = @slot_id
                  AND revision = @expected_revision;
                """;
            update.Parameters.AddWithValue(
                "document_json",
                JsonSerializer.Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(slot), JsonOptions));
            update.Parameters.AddWithValue("next_revision", checked(revision + 1));
            update.Parameters.AddWithValue("status", slot.Status.ToString());
            update.Parameters.AddWithValue("updated_at_utc", slot.LastTransitionAtUtc);
            AddSlotIdentity(update, slot.Address);
            update.Parameters.AddWithValue("expected_revision", revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} changed during atomic run synchronization.");
            }

            await InsertTimelineAsync(
                    connection,
                    transaction,
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        slot.Address,
                        expectedMaterial,
                        run.Id,
                        completedSlot.OperationRunId,
                        completedSlot.FencingToken,
                        SlotOccupancyStatus.Running,
                        SlotOccupancyStatus.Occupied,
                        run.ActorId,
                        completedSlot.CompletedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> HasSlotCompletionEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionRunId runId,
        CompletedSlotOperation completedSlot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json::text
            FROM olo_production_material_timeline
            WHERE kind = @kind
              AND production_run_id = @production_run_id
              AND operation_run_id = @operation_run_id
              AND slot_fencing_token = @slot_fencing_token
              AND occurred_at_utc = @occurred_at_utc;
            """;
        command.Parameters.AddWithValue(
            "kind",
            ProductionMaterialEvidenceKind.SlotOccupancyTransition.ToString());
        command.Parameters.AddWithValue("production_run_id", runId.Value);
        command.Parameters.AddWithValue("operation_run_id", completedSlot.OperationRunId);
        command.Parameters.AddWithValue("slot_fencing_token", completedSlot.FencingToken);
        command.Parameters.AddWithValue("occurred_at_utc", completedSlot.CompletedAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<PersistedProductionMaterialTimelineEntry>(
                reader.GetString(0),
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Persisted Slot completion evidence is empty.");
            if (ProductionRunSlotLifecycle.IsCompletionEvidence(
                    ProductionMaterialSnapshotMapper.ToAggregate(snapshot),
                    runId,
                    completedSlot))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask InsertTimelineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionMaterialTimelineEntry evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_production_material_timeline (
                evidence_id, kind, production_run_id, operation_run_id, slot_fencing_token,
                production_unit_id, carrier_id,
                genealogy_parent_unit_id, genealogy_child_unit_id, document_json, occurred_at_utc)
            VALUES (
                @evidence_id, @kind, @production_run_id, @operation_run_id, @slot_fencing_token,
                @production_unit_id, @carrier_id,
                @genealogy_parent_unit_id, @genealogy_child_unit_id, @document_json::jsonb,
                @occurred_at_utc);
            """;
        command.Parameters.AddWithValue("evidence_id", evidence.EvidenceId);
        command.Parameters.AddWithValue("kind", evidence.Kind.ToString());
        command.Parameters.Add("production_run_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.ProductionRunId?.Value ?? DBNull.Value;
        command.Parameters.Add("operation_run_id", NpgsqlDbType.Text).Value =
            (object?)evidence.OperationRunId ?? DBNull.Value;
        command.Parameters.Add("slot_fencing_token", NpgsqlDbType.Bigint).Value =
            (object?)evidence.SlotFencingToken ?? DBNull.Value;
        command.Parameters.Add("production_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.ProductionUnitId?.Value ?? DBNull.Value;
        command.Parameters.Add("carrier_id", NpgsqlDbType.Text).Value =
            (object?)evidence.CarrierId?.Value ?? DBNull.Value;
        command.Parameters.Add("genealogy_parent_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.Genealogy?.ParentUnitId.Value ?? DBNull.Value;
        command.Parameters.Add("genealogy_child_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.Genealogy?.ChildUnitId.Value ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "document_json",
            JsonSerializer.Serialize(
                ProductionMaterialSnapshotMapper.ToSnapshot(evidence),
                JsonOptions));
        command.Parameters.AddWithValue("occurred_at_utc", evidence.OccurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> UpdateProductionUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionUnit unit,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE olo_production_units
            SET document_json = @document_json::jsonb,
                revision = @next_revision,
                disposition = @disposition,
                updated_at_utc = @updated_at_utc
            WHERE production_unit_id = @production_unit_id
              AND revision = @expected_revision;
            """;
        update.Parameters.AddWithValue(
            "document_json",
            JsonSerializer.Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(unit), JsonOptions));
        update.Parameters.AddWithValue("next_revision", checked(expectedRevision + 1));
        update.Parameters.AddWithValue("disposition", unit.Disposition.ToString());
        update.Parameters.AddWithValue("updated_at_utc", unit.LastTransitionAtUtc);
        update.Parameters.AddWithValue("production_unit_id", unit.Id.Value);
        update.Parameters.AddWithValue("expected_revision", expectedRevision);
        return await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static ProductionUnit DeserializeProductionUnit(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProductionUnit>(documentJson, JsonOptions)
            ?? throw new InvalidDataException("PostgreSQL Production Unit document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static SlotOccupancy DeserializeSlot(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedSlotOccupancy>(documentJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted Slot occupancy document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static void AddSlotIdentity(NpgsqlCommand command, SlotAddress address)
    {
        command.Parameters.AddWithValue("line_id", address.LineId);
        command.Parameters.AddWithValue("station_system_id", address.StationSystemId);
        command.Parameters.AddWithValue("slot_id", address.SlotId);
    }

    private sealed record ProductionUnitSynchronization(
        ProductionUnit Unit,
        ProductDisposition PreviousDisposition);

    private static void AddRunParameters(NpgsqlCommand command, ProductionRun run)
    {
        command.Parameters.AddWithValue("run_id", run.Id.Value);
        command.Parameters.AddWithValue("production_unit_id", run.ProductionUnitId.Value);
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

    private static ResourceKind ParseResourceKind(string value) =>
        Enum.TryParse<ResourceKind>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted resource kind '{value}' is not a canonical token.");

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockStationJobAsync(connection, transaction, jobId, cancellationToken)
            .ConfigureAwait(false);
        if (await TryEnsureExistingStationEventAsync(
                connection,
                transaction,
                messageId,
                jobId,
                idempotencyKey,
                kind,
                payload,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = await RequireDispatchRequestAsync(
                connection,
                transaction,
                jobId,
                cancellationToken)
            .ConfigureAwait(false);
        var timeline = await ReadStationEventTimelineAsync(
                connection,
                transaction,
                jobId,
                cancellationToken)
            .ConfigureAwait(false);
        switch (kind)
        {
            case nameof(StationJobAccepted):
                {
                    var accepted = JsonSerializer.Deserialize<StationJobAccepted>(payload, JsonOptions)
                        ?? throw new InvalidDataException("Station acceptance payload is empty.");
                    ValidateStationResultIdentity(
                        request,
                        accepted.JobId,
                        accepted.IdempotencyKey,
                        accepted.AgentId,
                        accepted.StationId);
                    if (accepted.AcceptedAtUtc < request.RequestedAtUtc
                        || timeline.HasAccepted
                        || timeline.EventCount != 0)
                    {
                        throw new InvalidDataException(
                            "Station acceptance is out of order for its dispatch request.");
                    }

                    break;
                }
            case nameof(StationJobProgressed):
                {
                    var progress = JsonSerializer.Deserialize<StationJobProgressed>(payload, JsonOptions)
                        ?? throw new InvalidDataException("Station progress payload is empty.");
                    ValidateStationResultIdentity(
                        request,
                        progress.JobId,
                        progress.IdempotencyKey,
                        progress.AgentId,
                        progress.StationId);
                    if (!timeline.HasAccepted
                        || timeline.HasRecoveryRequired
                        || progress.ProgressedAtUtc < timeline.LatestAtUtc
                        || progress.Percent < timeline.MaximumProgressPercent
                        || await CompletionExistsAsync(
                                connection,
                                transaction,
                                progress.IdempotencyKey,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidDataException("Station progress is not monotonic.");
                    }

                    break;
                }
            case nameof(StationJobRecoveryRequired):
                {
                    var recoveryRequired = JsonSerializer.Deserialize<StationJobRecoveryRequired>(
                        payload,
                        JsonOptions)
                        ?? throw new InvalidDataException(
                            "Station recovery-required payload is empty.");
                    ValidateStationResultIdentity(
                        request,
                        recoveryRequired.JobId,
                        recoveryRequired.JobIdempotencyKey,
                        recoveryRequired.AgentId,
                        recoveryRequired.StationId);
                    if (!timeline.HasAccepted
                        || timeline.HasRecoveryRequired
                        || recoveryRequired.ProductionRunId != request.ProductionRunId
                        || recoveryRequired.RuntimeSessionId != request.RuntimeSessionId
                        || !string.Equals(
                            recoveryRequired.OperationRunId,
                            request.OperationRunId,
                            StringComparison.Ordinal)
                        || recoveryRequired.DetectedAtUtc < timeline.LatestAtUtc
                        || await CompletionExistsAsync(
                                connection,
                                transaction,
                                request.IdempotencyKey,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            "Station recovery-required evidence does not exactly follow its dispatch timeline.");
                    }

                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported Station event kind '{kind}'.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_station_job_event_inbox (
                message_id, job_id, idempotency_key, kind, payload_json, occurred_at_utc)
            VALUES (@message_id, @job_id, @idempotency_key, @kind, @payload_json::jsonb, @occurred_at_utc)
            ;
            """;
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask LockStationJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@job_id::text, 0));";
        command.Parameters.AddWithValue("job_id", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<StationJobRequested> RequireDispatchRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_json::text
            FROM olo_station_job_outbox
            WHERE job_id = @job_id
              AND kind = 'StationJobRequested';
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            as string;
        return payload is null
            ? throw new InvalidDataException(
                $"Station result references unknown job {jobId:D}.")
            : JsonSerializer.Deserialize<StationJobRequested>(payload, JsonOptions)
              ?? throw new InvalidDataException("Station dispatch request payload is empty.");
    }

    private static async ValueTask<StationEventTimeline> ReadStationEventTimelineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                count(*)::integer,
                count(*) FILTER (WHERE kind = 'StationJobAccepted')::integer,
                count(*) FILTER (WHERE kind = 'StationJobRecoveryRequired')::integer,
                max(occurred_at_utc),
                coalesce(max((payload_json ->> 'percent')::integer)
                    FILTER (WHERE kind = 'StationJobProgressed'), 0)::integer
            FROM olo_station_job_event_inbox
            WHERE job_id = @job_id;
            """;
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Station event timeline query returned no aggregate row.");
        }

        return new StationEventTimeline(
            reader.GetInt32(0),
            reader.GetInt32(1) == 1,
            reader.GetInt32(2) == 1,
            reader.IsDBNull(3)
                ? default
                : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt32(4));
    }

    private static async ValueTask<bool> TryEnsureExistingStationEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        Guid jobId,
        string idempotencyKey,
        string kind,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id, idempotency_key, kind, payload_json::text
            FROM olo_station_job_event_inbox
            WHERE message_id = @message_id;
            """;
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (reader.GetGuid(0) != jobId
            || !string.Equals(reader.GetString(1), idempotencyKey, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Station event message {messageId:D} was reused with different identity.");
        }

        EnsureSameJsonEvidence(
            reader.GetString(3),
            payload,
            $"Station event message {messageId:D}");
        return true;
    }

    private static async ValueTask<bool> TryEnsureExistingCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StationJobCompleted completion,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT message_id, idempotency_key, payload_json::text
            FROM olo_station_job_result_inbox
            WHERE message_id = @message_id OR idempotency_key = @idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("message_id", completion.MessageId);
        command.Parameters.AddWithValue("idempotency_key", completion.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (reader.GetGuid(0) != completion.MessageId
            || !string.Equals(
                reader.GetString(1),
                completion.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Station completion identity was reused with different evidence.");
        }

        EnsureSameJsonEvidence(reader.GetString(2), payload, "Station completion");
        return true;
    }

    private static async ValueTask<bool> CompletionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM olo_station_job_result_inbox
                WHERE idempotency_key = @idempotency_key);
            """;
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Station completion existence query returned null."));
    }

    private static void ValidateStationResultIdentity(
        StationJobRequested request,
        Guid jobId,
        string idempotencyKey,
        string agentId,
        string stationId)
    {
        if (request.JobId != jobId
            || !string.Equals(request.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
            || !string.Equals(request.AgentId, agentId, StringComparison.Ordinal)
            || !string.Equals(request.StationId, stationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station result identity does not exactly match its dispatch request.");
        }
    }

    private static void EnsureSameJsonEvidence(
        string existing,
        string candidate,
        string description)
    {
        using var existingJson = JsonDocument.Parse(existing);
        using var candidateJson = JsonDocument.Parse(candidate);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                $"{description} was reused with different evidence.");
        }
    }

    private sealed record StationEventTimeline(
        int EventCount,
        bool HasAccepted,
        bool HasRecoveryRequired,
        DateTimeOffset LatestAtUtc,
        int MaximumProgressPercent);

    private static async ValueTask<bool> InsertStationDispatchOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        Guid jobId,
        string idempotencyKey,
        string kind,
        int sequence,
        string payload,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_station_job_outbox (
                message_id, job_id, idempotency_key, kind, sequence, payload_json, created_at_utc,
                attempt_count, last_error, quarantine_reason, quarantined_at_utc, published_at_utc)
            VALUES (
                @message_id, @job_id, @idempotency_key, @kind, @sequence, @payload_json::jsonb,
                @created_at_utc, 0, NULL, NULL, NULL, NULL)
            ON CONFLICT (idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("payload_json", payload);
        command.Parameters.AddWithValue("created_at_utc", createdAtUtc);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (added)
        {
            return true;
        }

        await using var existingCommand = connection.CreateCommand();
        existingCommand.Transaction = transaction;
        existingCommand.CommandText = """
            SELECT message_id, job_id, kind, sequence, payload_json::text
            FROM olo_station_job_outbox
            WHERE idempotency_key = @idempotency_key;
            """;
        existingCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "Station dispatch idempotency conflict has no stored payload.");
        }

        if (reader.GetGuid(0) != messageId
            || reader.GetGuid(1) != jobId
            || !string.Equals(reader.GetString(2), kind, StringComparison.Ordinal)
            || reader.GetInt32(3) != sequence)
        {
            throw new InvalidOperationException(
                $"Station dispatch idempotency key '{idempotencyKey}' was reused with different identity.");
        }

        using var existingJson = JsonDocument.Parse(reader.GetString(4));
        using var candidateJson = JsonDocument.Parse(payload);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                $"Station dispatch idempotency key '{idempotencyKey}' was reused with different evidence.");
        }

        return false;
    }

    private static ResourceLeaseChanged[] ValidateDispatch(
        StationJobRequested request,
        IReadOnlyCollection<ResourceLeaseChanged> changes)
    {
        var expected = request.ResourceFences
            .OrderBy(static fence => fence.ResourceKind, StringComparer.Ordinal)
            .ThenBy(static fence => fence.ResourceId, StringComparer.Ordinal)
            .Select(fence => StationDispatchMessageIdentity.CreateLeaseGranted(request, fence))
            .ToArray();
        var supplied = changes
            .OrderBy(static change => change.ResourceKind, StringComparer.Ordinal)
            .ThenBy(static change => change.ResourceId, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != supplied.Length)
        {
            throw new InvalidDataException(
                "Station dispatch resource lease changes do not match its fences.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != supplied[index])
            {
                throw new InvalidDataException(
                    "Station dispatch resource lease change evidence is not canonical.");
            }
        }

        return supplied;
    }

    private static ProductionRun DeserializeRun(string json) =>
        ProductionRunSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionRun>(json, JsonOptions)
            ?? throw new InvalidDataException("PostgreSQL Production Run document is empty."));
}
