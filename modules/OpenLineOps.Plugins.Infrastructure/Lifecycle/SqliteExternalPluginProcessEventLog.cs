using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class SqliteExternalPluginProcessEventLog : IExternalPluginProcessEventLog, IDisposable
{
    private readonly string _connectionString;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteExternalPluginProcessEventLog(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite external plugin process event log connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public void Record(ExternalPluginProcessEvent processEvent)
    {
        ArgumentNullException.ThrowIfNull(processEvent);
        RequireCanonical(processEvent.ProjectId, nameof(processEvent.ProjectId));
        RequireCanonical(processEvent.ApplicationId, nameof(processEvent.ApplicationId));
        RequireCanonicalPluginId(processEvent.PluginId, nameof(processEvent));
        RequireSha256(processEvent.PackageContentSha256, nameof(processEvent.PackageContentSha256));

        lock (_syncRoot)
        {
            EnsureSchema();

            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO external_plugin_process_events (
                    kind,
                    kind_name,
                    project_id,
                    application_id,
                    plugin_id,
                    package_content_sha256,
                    message,
                    detail,
                    occurred_at_utc,
                    recorded_at_utc)
                VALUES (
                    $kind,
                    $kind_name,
                    $project_id,
                    $application_id,
                    $plugin_id,
                    $package_content_sha256,
                    $message,
                    $detail,
                    $occurred_at_utc,
                    $recorded_at_utc);
                """;
            command.Parameters.AddWithValue("$kind", (int)processEvent.Kind);
            command.Parameters.AddWithValue("$kind_name", processEvent.Kind.ToString());
            command.Parameters.AddWithValue("$project_id", processEvent.ProjectId);
            command.Parameters.AddWithValue("$application_id", processEvent.ApplicationId);
            command.Parameters.AddWithValue("$plugin_id", processEvent.PluginId);
            command.Parameters.AddWithValue(
                "$package_content_sha256",
                processEvent.PackageContentSha256);
            command.Parameters.AddWithValue("$message", processEvent.Message);
            command.Parameters.AddWithValue("$detail", (object?)processEvent.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(processEvent.OccurredAtUtc));
            command.Parameters.AddWithValue("$recorded_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

            command.ExecuteNonQuery();
        }
    }

    public async ValueTask<IReadOnlyList<ExternalPluginProcessEvent>> ListAsync(
        ExternalPluginProcessEventQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildListSql(query);
        AddQueryParameters(command, query);

        var events = new List<ExternalPluginProcessEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new ExternalPluginProcessEvent(
                (ExternalPluginProcessEventKind)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseTimestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return events;
    }

    private void EnsureSchema()
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        _schemaLock.Wait();
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();

            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();

            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
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

            EnsureDatabaseDirectory();

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static string BuildListSql(ExternalPluginProcessEventQuery query)
    {
        var filters = new List<string>();
        filters.Add("project_id = $project_id");
        filters.Add("application_id = $application_id");
        filters.Add("package_content_sha256 = $package_content_sha256");

        if (query.Kind is not null)
        {
            filters.Add("kind = $kind");
        }

        if (query.OccurredFromUtc is not null)
        {
            filters.Add("occurred_at_utc >= $occurred_from_utc");
        }

        if (query.OccurredToUtc is not null)
        {
            filters.Add("occurred_at_utc <= $occurred_to_utc");
        }

        var whereClause = filters.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}WHERE {string.Join(" AND ", filters)}";

        return $"""
            SELECT kind, project_id, application_id, plugin_id, package_content_sha256,
                   message, occurred_at_utc, detail
            FROM external_plugin_process_events{whereClause}
            ORDER BY occurred_at_utc, event_id
            LIMIT $take OFFSET $skip;
            """;
    }

    private static void AddQueryParameters(
        SqliteCommand command,
        ExternalPluginProcessEventQuery query)
    {
        command.Parameters.AddWithValue("$project_id", query.ProjectId);
        command.Parameters.AddWithValue("$application_id", query.ApplicationId);
        command.Parameters.AddWithValue("$package_content_sha256", query.PackageContentSha256);

        if (query.Kind is not null)
        {
            command.Parameters.AddWithValue("$kind", (int)query.Kind.Value);
        }

        if (query.OccurredFromUtc is not null)
        {
            command.Parameters.AddWithValue("$occurred_from_utc", FormatTimestamp(query.OccurredFromUtc.Value));
        }

        if (query.OccurredToUtc is not null)
        {
            command.Parameters.AddWithValue("$occurred_to_utc", FormatTimestamp(query.OccurredToUtc.Value));
        }

        command.Parameters.AddWithValue("$take", query.Take);
        command.Parameters.AddWithValue("$skip", query.Skip);
    }

    private static void ValidateQuery(ExternalPluginProcessEventQuery query)
    {
        RequireCanonical(query.ProjectId, nameof(query.ProjectId));
        RequireCanonical(query.ApplicationId, nameof(query.ApplicationId));
        RequireSha256(query.PackageContentSha256, nameof(query.PackageContentSha256));

        if (query.Skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "External plugin process event query skip must be greater than or equal to zero.");
        }

        if (query.Take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "External plugin process event query take must be greater than zero.");
        }
    }

    private static void RequireCanonicalPluginId(string pluginId, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(pluginId)
            || !string.Equals(pluginId, pluginId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "External plugin process event plugin id must be non-empty and must not contain surrounding whitespace.",
                parameterName);
        }
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "External plugin process event scope must be canonical non-empty text.",
                parameterName);
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "External plugin process event package hash must be a lowercase SHA-256 value.",
                parameterName);
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS external_plugin_process_events (
            event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            kind INTEGER NOT NULL,
            kind_name TEXT NOT NULL,
            project_id TEXT NOT NULL,
            application_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            package_content_sha256 TEXT NOT NULL,
            message TEXT NOT NULL,
            detail TEXT NULL,
            occurred_at_utc TEXT NOT NULL,
            recorded_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_external_plugin_process_events_scope_time
            ON external_plugin_process_events(
                project_id,
                application_id,
                package_content_sha256,
                occurred_at_utc,
                event_id);

        CREATE INDEX IF NOT EXISTS ix_external_plugin_process_events_kind_time
            ON external_plugin_process_events(kind, occurred_at_utc, event_id);
        """;
}
