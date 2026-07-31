using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationController;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationControllerCommandJournal :
    IStationControllerCommandJournal,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationControllerCommandJournal(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<StationControllerCommandAcceptanceResult> TryAcceptAsync(
        StationControllerCommandEnvelope command,
        string commandSha256,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        ValidateSha256(commandSha256);
        RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var fence = await ReadFenceAsync(
                    connection,
                    transaction,
                    command.StationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fence is not null && command.FencingToken < fence.Token)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Acceptance(
                    StationControllerCommandAcceptanceStatus.StaleFencingToken,
                    null,
                    fence.Token);
            }

            if (fence is not null
                && command.FencingToken == fence.Token
                && !fence.IsOwnedBy(command))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Acceptance(
                    StationControllerCommandAcceptanceStatus.FencingOwnerMismatch,
                    null,
                    fence.Token);
            }

            var existing = await ReadEntryAsync(
                    connection,
                    transaction,
                    command.StationId,
                    command.CommandId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (fence is null)
                {
                    throw new InvalidDataException(
                        $"Station {command.StationId} has controller command "
                        + "evidence without its durable fencing high-water mark.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Acceptance(
                    string.Equals(
                        existing.CommandSha256,
                        commandSha256,
                        StringComparison.Ordinal)
                        ? StationControllerCommandAcceptanceStatus.Existing
                        : StationControllerCommandAcceptanceStatus.CommandIdentityConflict,
                    existing,
                    fence.Token);
            }

            if (fence is null || command.FencingToken > fence.Token)
            {
                await UpsertFenceAsync(
                        connection,
                        transaction,
                        command,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var accepted = new StationControllerCommandJournalEntry(
                command,
                commandSha256,
                StationControllerCommandJournalStatus.Accepted,
                InvocationAttemptCount: 0,
                acceptedAtUtc,
                InvocationStartedAtUtc: null,
                TerminalAtUtc: null,
                Result: null);
            await InsertEntryAsync(
                    connection,
                    transaction,
                    accepted,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Acceptance(
                StationControllerCommandAcceptanceStatus.Accepted,
                accepted,
                command.FencingToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<StationControllerCommandJournalEntry?> GetAsync(
        string stationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        Required(stationId, nameof(stationId));
        Required(commandId, nameof(commandId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadEntryAsync(
                connection,
                transaction: null,
                stationId,
                commandId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<long?> GetFencingTokenHighWaterAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        Required(stationId, nameof(stationId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var fence = await ReadFenceAsync(
                connection,
                transaction: null,
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        return fence?.Token;
    }

    public async ValueTask<StationControllerCommandInvocationClaim> MarkInvokingAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus expectedStatus,
        int expectedInvocationAttemptCount,
        DateTimeOffset invocationStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        Required(stationId, nameof(stationId));
        Required(commandId, nameof(commandId));
        ValidateSha256(commandSha256);
        RequireUtc(invocationStartedAtUtc, nameof(invocationStartedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadExactEntryAsync(
                    connection,
                    transaction,
                    stationId,
                    commandId,
                    commandSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Status != expectedStatus
                || current.InvocationAttemptCount
                    != expectedInvocationAttemptCount)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StationControllerCommandInvocationClaim(false, current);
            }

            if (current.Status == StationControllerCommandJournalStatus.Invoking
                && current.Command.Idempotency
                    != StationControllerCommandIdempotency.Idempotent)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StationControllerCommandInvocationClaim(false, current);
            }

            if (current.Status is not StationControllerCommandJournalStatus.Accepted
                and not StationControllerCommandJournalStatus.Invoking)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StationControllerCommandInvocationClaim(false, current);
            }

            if (invocationStartedAtUtc < current.AcceptedAtUtc
                || current.InvocationStartedAtUtc is { } priorStartedAtUtc
                && invocationStartedAtUtc < priorStartedAtUtc)
            {
                throw new ArgumentException(
                    "Invocation time cannot precede durable command history.",
                    nameof(invocationStartedAtUtc));
            }

            var invoking = current with
            {
                Status = StationControllerCommandJournalStatus.Invoking,
                InvocationAttemptCount = checked(current.InvocationAttemptCount + 1),
                InvocationStartedAtUtc = invocationStartedAtUtc
            };
            await UpdateEntryAsync(
                    connection,
                    transaction,
                    invoking,
                    current.Status,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationControllerCommandInvocationClaim(true, invoking);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<StationControllerCommandJournalEntry> CompleteAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus terminalStatus,
        StationPhysicalControllerExecutionResult result,
        DateTimeOffset terminalAtUtc,
        CancellationToken cancellationToken = default)
    {
        Required(stationId, nameof(stationId));
        Required(commandId, nameof(commandId));
        ValidateSha256(commandSha256);
        ValidateTerminalStatus(terminalStatus);
        ArgumentNullException.ThrowIfNull(result);
        ValidateTerminalResult(terminalStatus, result);
        RequireUtc(terminalAtUtc, nameof(terminalAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadExactEntryAsync(
                    connection,
                    transaction,
                    stationId,
                    commandId,
                    commandSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsTerminal(current.Status))
            {
                if (current.Status != terminalStatus || current.Result != result)
                {
                    throw new InvalidDataException(
                        $"Command {commandId} terminal evidence does not match "
                        + "the durable journal.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            if (current.Status != StationControllerCommandJournalStatus.Invoking
                && !(current.Status == StationControllerCommandJournalStatus.Accepted
                    && terminalStatus
                        == StationControllerCommandJournalStatus.Rejected))
            {
                throw new InvalidOperationException(
                    $"Command {commandId} cannot complete from {current.Status}.");
            }

            if (terminalAtUtc < current.AcceptedAtUtc)
            {
                throw new ArgumentException(
                    "Terminal time cannot precede command acceptance.",
                    nameof(terminalAtUtc));
            }

            if (current.InvocationStartedAtUtc is { } invocationStartedAtUtc
                && terminalAtUtc < invocationStartedAtUtc)
            {
                throw new ArgumentException(
                    "Terminal time cannot precede physical invocation.",
                    nameof(terminalAtUtc));
            }

            var terminal = current with
            {
                Status = terminalStatus,
                TerminalAtUtc = terminalAtUtc,
                Result = result
            };
            await UpdateEntryAsync(
                    connection,
                    transaction,
                    terminal,
                    current.Status,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return terminal;
        }
        finally
        {
            _writeLock.Release();
        }
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

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;

                CREATE TABLE IF NOT EXISTS station_controller_fencing_high_water (
                    station_id TEXT NOT NULL PRIMARY KEY,
                    fencing_token INTEGER NOT NULL CHECK(fencing_token >= 1),
                    owner_agent_id TEXT NOT NULL,
                    owner_instance_id TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS station_controller_command_journal (
                    station_id TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    contract_version INTEGER NOT NULL
                        CHECK(contract_version = 1),
                    command_sha256 TEXT NOT NULL,
                    command_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    invocation_attempt_count INTEGER NOT NULL
                        CHECK(invocation_attempt_count >= 0),
                    accepted_at_utc TEXT NOT NULL,
                    invocation_started_at_utc TEXT NULL,
                    terminal_at_utc TEXT NULL,
                    result_json TEXT NULL,
                    PRIMARY KEY (station_id, command_id)
                );

                CREATE INDEX IF NOT EXISTS ix_station_controller_journal_status
                    ON station_controller_command_journal(
                        station_id,
                        status,
                        accepted_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = """
                SELECT COUNT(*)
                FROM pragma_table_info('station_controller_command_journal')
                WHERE name = 'contract_version';
                """;
            var hasContractVersion = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
            if (!hasContractVersion)
            {
                command.CommandText = """
                    ALTER TABLE station_controller_command_journal
                    ADD COLUMN contract_version INTEGER NOT NULL DEFAULT 1
                        CHECK(contract_version = 1);
                    """;
                _ = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask<FenceOwner?> ReadFenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string stationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fencing_token, owner_agent_id, owner_instance_id
            FROM station_controller_fencing_high_water
            WHERE station_id = $station_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FenceOwner(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2))
            : null;
    }

    private static async ValueTask<StationControllerCommandJournalEntry?>
        ReadEntryAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string stationId,
            string commandId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT contract_version,
                   command_sha256,
                   command_json,
                   status,
                   invocation_attempt_count,
                   accepted_at_utc,
                   invocation_started_at_utc,
                   terminal_at_utc,
                   result_json
            FROM station_controller_command_journal
            WHERE station_id = $station_id
              AND command_id = $command_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$command_id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var contractVersion = reader.GetInt32(0);
        if (contractVersion != StationControllerCommandFingerprint.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Persisted controller command contract version "
                + $"{contractVersion} is unsupported.");
        }

        var commandSha256 = reader.GetString(1);
        ValidateSha256(commandSha256);
        var commandEnvelope = Deserialize<StationControllerCommandEnvelope>(
            reader.GetString(2),
            "controller command");
        if (commandEnvelope.ContractVersion != contractVersion)
        {
            throw new InvalidDataException(
                "Persisted controller command contract version does not match "
                + "its durable row metadata.");
        }
        if (!string.Equals(
                commandEnvelope.StationId,
                stationId,
                StringComparison.Ordinal)
            || !string.Equals(
                commandEnvelope.CommandId,
                commandId,
                StringComparison.Ordinal)
            || !string.Equals(
                StationControllerCommandFingerprint.Compute(commandEnvelope),
                commandSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted controller command {stationId}/{commandId} identity "
                + "or fingerprint is invalid.");
        }

        var entry = new StationControllerCommandJournalEntry(
            commandEnvelope,
            commandSha256,
            ParseStatus(reader.GetString(3)),
            reader.GetInt32(4),
            ParseUtc(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            reader.IsDBNull(8)
                ? null
                : Deserialize<StationPhysicalControllerExecutionResult>(
                    reader.GetString(8),
                    "physical controller result"));
        ValidatePersistedEntry(entry);
        return entry;
    }

    private static async ValueTask<StationControllerCommandJournalEntry>
        ReadExactEntryAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string stationId,
            string commandId,
            string commandSha256,
            CancellationToken cancellationToken)
    {
        var entry = await ReadEntryAsync(
                connection,
                transaction,
                stationId,
                commandId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Controller command {stationId}/{commandId} was not journaled.");
        return string.Equals(
            entry.CommandSha256,
            commandSha256,
            StringComparison.Ordinal)
            ? entry
            : throw new InvalidDataException(
                $"Controller command {commandId} immutable evidence changed.");
    }

    private static async ValueTask UpsertFenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationControllerCommandEnvelope commandEnvelope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_controller_fencing_high_water (
                station_id,
                fencing_token,
                owner_agent_id,
                owner_instance_id)
            VALUES (
                $station_id,
                $fencing_token,
                $owner_agent_id,
                $owner_instance_id)
            ON CONFLICT(station_id)
            DO UPDATE SET
                fencing_token = excluded.fencing_token,
                owner_agent_id = excluded.owner_agent_id,
                owner_instance_id = excluded.owner_instance_id
            WHERE excluded.fencing_token > fencing_token;
            """;
        AddFenceParameters(command, commandEnvelope);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationControllerCommandJournalEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_controller_command_journal (
                station_id,
                command_id,
                contract_version,
                command_sha256,
                command_json,
                status,
                invocation_attempt_count,
                accepted_at_utc,
                invocation_started_at_utc,
                terminal_at_utc,
                result_json)
            VALUES (
                $station_id,
                $command_id,
                $contract_version,
                $command_sha256,
                $command_json,
                $status,
                $invocation_attempt_count,
                $accepted_at_utc,
                NULL,
                NULL,
                NULL);
            """;
        AddEntryParameters(command, entry);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask UpdateEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationControllerCommandJournalEntry entry,
        StationControllerCommandJournalStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE station_controller_command_journal
            SET status = $status,
                invocation_attempt_count = $invocation_attempt_count,
                invocation_started_at_utc = $invocation_started_at_utc,
                terminal_at_utc = $terminal_at_utc,
                result_json = $result_json
            WHERE station_id = $station_id
              AND command_id = $command_id
              AND command_sha256 = $command_sha256
              AND status = $expected_status;
            """;
        AddEntryParameters(command, entry);
        command.Parameters.AddWithValue("$expected_status", expectedStatus.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Controller command {entry.Command.CommandId} journal state "
                + "changed concurrently.");
        }
    }

    private static void AddFenceParameters(
        SqliteCommand command,
        StationControllerCommandEnvelope commandEnvelope)
    {
        command.Parameters.AddWithValue("$station_id", commandEnvelope.StationId);
        command.Parameters.AddWithValue("$fencing_token", commandEnvelope.FencingToken);
        command.Parameters.AddWithValue("$owner_agent_id", commandEnvelope.OwnerAgentId);
        command.Parameters.AddWithValue(
            "$owner_instance_id",
            commandEnvelope.OwnerInstanceId);
    }

    private static void AddEntryParameters(
        SqliteCommand command,
        StationControllerCommandJournalEntry entry)
    {
        command.Parameters.AddWithValue("$station_id", entry.Command.StationId);
        command.Parameters.AddWithValue("$command_id", entry.Command.CommandId);
        command.Parameters.AddWithValue(
            "$contract_version",
            entry.Command.ContractVersion);
        command.Parameters.AddWithValue("$command_sha256", entry.CommandSha256);
        command.Parameters.AddWithValue(
            "$command_json",
            JsonSerializer.Serialize(entry.Command, JsonOptions));
        command.Parameters.AddWithValue("$status", entry.Status.ToString());
        command.Parameters.AddWithValue(
            "$invocation_attempt_count",
            entry.InvocationAttemptCount);
        command.Parameters.AddWithValue(
            "$accepted_at_utc",
            FormatUtc(entry.AcceptedAtUtc));
        command.Parameters.AddWithValue(
            "$invocation_started_at_utc",
            entry.InvocationStartedAtUtc is { } started
                ? FormatUtc(started)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$terminal_at_utc",
            entry.TerminalAtUtc is { } terminal
                ? FormatUtc(terminal)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$result_json",
            entry.Result is null
                ? DBNull.Value
                : JsonSerializer.Serialize(entry.Result, JsonOptions));
    }

    private static StationControllerCommandAcceptanceResult Acceptance(
        StationControllerCommandAcceptanceStatus status,
        StationControllerCommandJournalEntry? entry,
        long highWater) => new(status, entry, highWater);

    private static T Deserialize<T>(string json, string description) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException(
            $"Persisted {description} JSON is null.");

    private static StationControllerCommandJournalStatus ParseStatus(string value) =>
        Enum.TryParse<StationControllerCommandJournalStatus>(
            value,
            ignoreCase: false,
            out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted controller command status '{value}' is invalid.");

    private static void ValidateCommand(StationControllerCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.StationId, nameof(command.StationId));
        Required(command.OwnerAgentId, nameof(command.OwnerAgentId));
        Required(command.OwnerInstanceId, nameof(command.OwnerInstanceId));
        Required(command.CommandId, nameof(command.CommandId));
        if (command.ContractVersion
            != StationControllerCommandFingerprint.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported controller command contract version "
                + $"{command.ContractVersion}.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(command.FencingToken, 1);
    }

    private static void ValidateTerminalStatus(
        StationControllerCommandJournalStatus status)
    {
        if (!IsTerminal(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static void ValidateTerminalResult(
        StationControllerCommandJournalStatus status,
        StationPhysicalControllerExecutionResult result)
    {
        var expectedOutcome = status switch
        {
            StationControllerCommandJournalStatus.Completed =>
                StationPhysicalControllerExecutionOutcome.Completed,
            StationControllerCommandJournalStatus.Failed
                or StationControllerCommandJournalStatus.Rejected =>
                StationPhysicalControllerExecutionOutcome.Failed,
            StationControllerCommandJournalStatus.CompletionUnknown =>
                StationPhysicalControllerExecutionOutcome.CompletionUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        if (result.Outcome != expectedOutcome)
        {
            throw new InvalidDataException(
                $"Journal status {status} requires physical outcome "
                + $"{expectedOutcome}.");
        }
    }

    private static void ValidatePersistedEntry(
        StationControllerCommandJournalEntry entry)
    {
        RequireUtc(entry.AcceptedAtUtc, nameof(entry.AcceptedAtUtc));
        if (entry.InvocationStartedAtUtc is { } invocationStartedAtUtc)
        {
            RequireUtc(invocationStartedAtUtc, nameof(entry.InvocationStartedAtUtc));
        }

        if (entry.TerminalAtUtc is { } terminalAtUtc)
        {
            RequireUtc(terminalAtUtc, nameof(entry.TerminalAtUtc));
        }

        var isTerminal = IsTerminal(entry.Status);
        if (entry.InvocationAttemptCount < 0
            || (entry.Status == StationControllerCommandJournalStatus.Accepted
                && (entry.InvocationAttemptCount != 0
                    || entry.InvocationStartedAtUtc is not null))
            || (entry.Status == StationControllerCommandJournalStatus.Invoking
                && (entry.InvocationAttemptCount < 1
                    || entry.InvocationStartedAtUtc is null))
            || (isTerminal != (entry.TerminalAtUtc is not null))
            || (isTerminal != (entry.Result is not null))
            || (!isTerminal && entry.Result is not null)
            || (entry.Status == StationControllerCommandJournalStatus.Rejected
                && (entry.InvocationAttemptCount != 0
                    || entry.InvocationStartedAtUtc is not null))
            || (isTerminal
                && entry.Status != StationControllerCommandJournalStatus.Rejected
                && (entry.InvocationAttemptCount < 1
                    || entry.InvocationStartedAtUtc is null)))
        {
            throw new InvalidDataException(
                $"Persisted controller command {entry.Command.CommandId} has "
                + "an inconsistent journal state.");
        }

        if (entry.InvocationStartedAtUtc is { } started
            && started < entry.AcceptedAtUtc
            || entry.TerminalAtUtc is { } terminal
            && terminal < entry.AcceptedAtUtc)
        {
            throw new InvalidDataException(
                $"Persisted controller command {entry.Command.CommandId} has "
                + "non-monotonic journal timestamps.");
        }

        if (entry.Result is not null)
        {
            ValidateTerminalResult(entry.Status, entry.Result);
        }
    }

    private static bool IsTerminal(StationControllerCommandJournalStatus status) =>
        status is StationControllerCommandJournalStatus.Completed
            or StationControllerCommandJournalStatus.Failed
            or StationControllerCommandJournalStatus.CompletionUnknown
            or StationControllerCommandJournalStatus.Rejected;

    private static void ValidateSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Command SHA-256 must be lowercase hexadecimal.",
                nameof(value));
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }

    private static void Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical text.",
                parameterName);
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(
                builder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Station controller command journal requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var fullPath = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        builder.DataSource = fullPath;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        return builder.ToString();
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    public void Dispose()
    {
        _schemaLock.Dispose();
        _writeLock.Dispose();
    }

    private sealed record FenceOwner(
        long Token,
        string AgentId,
        string OwnerInstanceId)
    {
        public bool IsOwnedBy(StationControllerCommandEnvelope command) =>
            string.Equals(AgentId, command.OwnerAgentId, StringComparison.Ordinal)
            && string.Equals(
                OwnerInstanceId,
                command.OwnerInstanceId,
                StringComparison.Ordinal);
    }
}
