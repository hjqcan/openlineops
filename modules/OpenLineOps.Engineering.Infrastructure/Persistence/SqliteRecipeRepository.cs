using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Recipes;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class SqliteRecipeRepository : IRecipeRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteRecipeRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = EngineeringSnapshotMapper.ToSnapshot(recipe);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recipes (
                recipe_id,
                document_json,
                version_id,
                status,
                updated_at_utc)
            VALUES (
                $recipe_id,
                $document_json,
                $version_id,
                $status,
                $updated_at_utc)
            ON CONFLICT(recipe_id) DO UPDATE SET
                document_json = excluded.document_json,
                version_id = excluded.version_id,
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$recipe_id", recipe.Id.Value);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$version_id", recipe.VersionId.Value);
        command.Parameters.AddWithValue("$status", recipe.Status.ToString());
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Recipe?> GetByIdAsync(
        RecipeId recipeId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM recipes
            WHERE recipe_id = $recipe_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$recipe_id", recipeId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeRecipe((string)documentJson);
    }

    public async Task<IReadOnlyCollection<Recipe>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM recipes
            ORDER BY recipe_id;
            """;

        var recipes = new List<Recipe>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            recipes.Add(DeserializeRecipe(reader.GetString(0)));
        }

        return recipes;
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
                CREATE TABLE IF NOT EXISTS recipes (
                    recipe_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    version_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_recipes_status
                    ON recipes(status);
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

    private static Recipe DeserializeRecipe(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedRecipe>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted recipe document is empty.");

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
