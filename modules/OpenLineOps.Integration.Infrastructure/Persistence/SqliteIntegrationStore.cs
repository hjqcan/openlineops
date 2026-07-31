using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.WorkOrders;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed partial class SqliteIntegrationStore :
    IIntegrationInboxStore,
    IIntegrationOutboxStore,
    IWorkOrderFactStore,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteIntegrationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Integration persistence requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new ArgumentException(
                "Integration SQLite path has no parent directory.",
                nameof(connectionString)));
        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        _connectionString = builder.ToString();
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
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
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS integration_inbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    content_sha256 TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    response_message_id TEXT NULL,
                    response_json TEXT NULL,
                    completed_at_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS integration_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    correlation_id TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    last_error TEXT NULL,
                    delivered_at_utc TEXT NULL,
                    dead_lettered_at_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_integration_outbox_delivery
                    ON integration_outbox(delivered_at_utc, dead_lettered_at_utc, sequence);

                CREATE TABLE IF NOT EXISTS integration_outbox_replay_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    replayed_at_utc TEXT NOT NULL,
                    previous_attempt_count INTEGER NOT NULL,
                    FOREIGN KEY(message_id) REFERENCES integration_outbox(message_id)
                );

                CREATE TABLE IF NOT EXISTS integration_work_order_facts (
                    fact_id TEXT NOT NULL PRIMARY KEY,
                    work_order_id TEXT NOT NULL,
                    fact_sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    resulting_status TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    product_model_id TEXT NULL,
                    target_quantity INTEGER NULL,
                    reason TEXT NULL,
                    content_sha256 TEXT NOT NULL,
                    UNIQUE(work_order_id, fact_sequence)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        ValidateUtc(value, nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.TryParseExact(
                   value,
                   "O",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new InvalidDataException(
                $"Persisted Integration timestamp '{value}' is not canonical UTC.");
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Integration timestamp must be non-default UTC.",
                parameterName);
        }
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
               || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }
}
