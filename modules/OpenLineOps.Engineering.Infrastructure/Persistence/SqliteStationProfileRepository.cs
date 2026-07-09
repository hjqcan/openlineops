using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Stations;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class SqliteStationProfileRepository : IStationProfileRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationProfileRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async Task SaveAsync(
        StationProfile stationProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationProfile);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = EngineeringSnapshotMapper.ToSnapshot(stationProfile);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO station_profiles (
                station_profile_id,
                document_json,
                device_binding_count,
                updated_at_utc)
            VALUES (
                $station_profile_id,
                $document_json,
                $device_binding_count,
                $updated_at_utc)
            ON CONFLICT(station_profile_id) DO UPDATE SET
                document_json = excluded.document_json,
                device_binding_count = excluded.device_binding_count,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$station_profile_id", stationProfile.Id.Value);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$device_binding_count", stationProfile.DeviceBindings.Count);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StationProfile?> GetByIdAsync(
        StationProfileId stationProfileId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM station_profiles
            WHERE station_profile_id = $station_profile_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_profile_id", stationProfileId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeStationProfile((string)documentJson);
    }

    public async Task<IReadOnlyCollection<StationProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM station_profiles
            ORDER BY station_profile_id;
            """;

        var stationProfiles = new List<StationProfile>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stationProfiles.Add(DeserializeStationProfile(reader.GetString(0)));
        }

        return stationProfiles;
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

            SqliteStorage.EnsureDatabaseDirectory(_connectionString);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS station_profiles (
                    station_profile_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    device_binding_count INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
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

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static StationProfile DeserializeStationProfile(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedStationProfile>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted station profile document is empty.");

        return EngineeringSnapshotMapper.ToAggregate(snapshot);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
