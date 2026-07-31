using Npgsql;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Monitoring;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class PostgreSqlAgentPresenceRepository :
    IAgentPresenceRepository,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgreSqlAgentPresenceRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            || char.IsWhiteSpace(connectionString[0])
            || char.IsWhiteSpace(connectionString[^1])
                ? throw new ArgumentException(
                    "PostgreSQL Agent presence connection string must be canonical.",
                    nameof(connectionString))
                : connectionString;
    }

    public async ValueTask<bool> RecordAsync(
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        AgentPresenceContract.Validate(presence);
        RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireIdentityLockAsync(connection, transaction, presence, cancellationToken)
            .ConfigureAwait(false);
        var latest = await ReadLatestAsync(
                connection,
                transaction,
                presence.AgentId,
                presence.StationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            if (presence.State != AgentPresenceState.Started)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await InsertSessionAsync(
                    connection,
                    transaction,
                    presence,
                    receivedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertLatestAsync(
                    connection,
                    transaction,
                    presence,
                    receivedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (latest.SessionId == presence.SessionId)
        {
            if (!string.Equals(
                    latest.StationSystemId,
                    presence.StationSystemId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One Agent presence session cannot change its Station System identity.");
            }

            if (presence.Sequence <= latest.Sequence
                || latest.State == AgentPresenceState.Stopping)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await UpdateLatestAsync(
                    connection,
                    transaction,
                    presence,
                    receivedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var knownSession = await SessionExistsAsync(
                connection,
                transaction,
                presence,
                cancellationToken)
            .ConfigureAwait(false);
        if (knownSession
            || presence.State != AgentPresenceState.Started)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await InsertSessionAsync(
                connection,
                transaction,
                presence,
                receivedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await UpdateLatestAsync(
                connection,
                transaction,
                presence,
                receivedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<AgentPresenceSnapshot?> GetAsync(
        string agentId,
        string stationId,
        CancellationToken cancellationToken = default)
    {
        RequireCanonical(agentId, nameof(agentId));
        RequireCanonical(stationId, nameof(stationId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLatestAsync(
                connection,
                transaction: null,
                agentId,
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<AgentPresenceSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, station_id, station_system_id, session_id, sequence,
                   state, observed_at_utc, received_at_utc
            FROM olo_agent_presence
            ORDER BY agent_id, station_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<AgentPresenceSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSnapshot(reader));
        }

        return result;
    }

    public void Dispose() => _schemaLock.Dispose();

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaCreated == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS olo_agent_presence (
                    agent_id text NOT NULL,
                    station_id text NOT NULL,
                    station_system_id text NOT NULL,
                    session_id uuid NOT NULL,
                    sequence bigint NOT NULL CHECK (sequence > 0),
                    state text NOT NULL CHECK (state IN ('Started', 'Heartbeat', 'Stopping')),
                    observed_at_utc timestamptz NOT NULL,
                    received_at_utc timestamptz NOT NULL,
                    PRIMARY KEY (agent_id, station_id)
                );
                CREATE TABLE IF NOT EXISTS olo_agent_presence_sessions (
                    agent_id text NOT NULL,
                    station_id text NOT NULL,
                    session_id uuid NOT NULL,
                    started_at_utc timestamptz NOT NULL,
                    PRIMARY KEY (agent_id, station_id, session_id)
                );
                CREATE INDEX IF NOT EXISTS ix_olo_agent_presence_station_system
                    ON olo_agent_presence (station_system_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async ValueTask AcquireIdentityLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentPresenceReported presence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtext(@agent_id), hashtext(@station_id));";
        command.Parameters.AddWithValue("agent_id", presence.AgentId);
        command.Parameters.AddWithValue("station_id", presence.StationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AgentPresenceSnapshot?> ReadLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string agentId,
        string stationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id, station_id, station_system_id, session_id, sequence,
                   state, observed_at_utc, received_at_utc
            FROM olo_agent_presence
            WHERE agent_id = @agent_id AND station_id = @station_id;
            """;
        command.Parameters.AddWithValue("agent_id", agentId);
        command.Parameters.AddWithValue("station_id", stationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSnapshot(reader)
            : null;
    }

    private static AgentPresenceSnapshot ReadSnapshot(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetGuid(3),
        reader.GetInt64(4),
        Enum.Parse<AgentPresenceState>(reader.GetString(5), ignoreCase: false),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static async ValueTask<bool> SessionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentPresenceReported presence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM olo_agent_presence_sessions
                WHERE agent_id = @agent_id
                  AND station_id = @station_id
                  AND session_id = @session_id);
            """;
        AddIdentity(command, presence);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("PostgreSQL Agent presence session query returned null."));
    }

    private static async ValueTask InsertSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_agent_presence_sessions (
                agent_id, station_id, session_id, started_at_utc)
            VALUES (@agent_id, @station_id, @session_id, @observed_at_utc);
            """;
        AddIdentity(command, presence);
        command.Parameters.AddWithValue("observed_at_utc", receivedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_agent_presence (
                agent_id, station_id, station_system_id, session_id, sequence,
                state, observed_at_utc, received_at_utc)
            VALUES (
                @agent_id, @station_id, @station_system_id, @session_id, @sequence,
                @state, @observed_at_utc, @received_at_utc);
            """;
        AddPresence(command, presence, receivedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE olo_agent_presence
            SET station_system_id = @station_system_id,
                session_id = @session_id,
                sequence = @sequence,
                state = @state,
                observed_at_utc = @observed_at_utc,
                received_at_utc = @received_at_utc
            WHERE agent_id = @agent_id AND station_id = @station_id;
            """;
        AddPresence(command, presence, receivedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("PostgreSQL Agent presence latest row was not updated.");
        }
    }

    private static void AddIdentity(NpgsqlCommand command, AgentPresenceReported presence)
    {
        command.Parameters.AddWithValue("agent_id", presence.AgentId);
        command.Parameters.AddWithValue("station_id", presence.StationId);
        command.Parameters.AddWithValue("session_id", presence.SessionId);
    }

    private static void AddPresence(
        NpgsqlCommand command,
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc)
    {
        AddIdentity(command, presence);
        command.Parameters.AddWithValue("station_system_id", presence.StationSystemId);
        command.Parameters.AddWithValue("sequence", presence.Sequence);
        command.Parameters.AddWithValue("state", presence.State.ToString());
        command.Parameters.AddWithValue("observed_at_utc", presence.ObservedAtUtc);
        command.Parameters.AddWithValue("received_at_utc", receivedAtUtc);
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

    private static void RequireCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName);
        }
    }
}
