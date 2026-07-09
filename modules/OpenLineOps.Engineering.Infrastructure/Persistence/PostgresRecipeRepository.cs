using System.Text.Json;
using Npgsql;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Recipes;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class PostgresRecipeRepository :
    IRecipeRepository,
    IDisposable,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgresRecipeRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString.Trim());
    }

    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = EngineeringSnapshotMapper.ToSnapshot(recipe);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recipes (
                recipe_id,
                document_json,
                version_id,
                status,
                updated_at_utc)
            VALUES (
                @recipe_id,
                @document_json::jsonb,
                @version_id,
                @status,
                @updated_at_utc)
            ON CONFLICT (recipe_id) DO UPDATE SET
                document_json = EXCLUDED.document_json,
                version_id = EXCLUDED.version_id,
                status = EXCLUDED.status,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """;
        command.Parameters.AddWithValue("recipe_id", recipe.Id.Value);
        command.Parameters.AddWithValue("document_json", documentJson);
        command.Parameters.AddWithValue("version_id", recipe.VersionId.Value);
        command.Parameters.AddWithValue("status", recipe.Status.ToString());
        command.Parameters.AddWithValue("updated_at_utc", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Recipe?> GetByIdAsync(
        RecipeId recipeId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM recipes
            WHERE recipe_id = @recipe_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("recipe_id", recipeId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeRecipe((string)documentJson);
    }

    public async Task<IReadOnlyCollection<Recipe>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
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

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS recipes (
                    recipe_id text NOT NULL PRIMARY KEY,
                    document_json jsonb NOT NULL,
                    version_id text NOT NULL,
                    status text NOT NULL,
                    updated_at_utc timestamptz NOT NULL
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

    private static Recipe DeserializeRecipe(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedRecipe>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted recipe document is empty.");

        return EngineeringSnapshotMapper.ToAggregate(snapshot);
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        _schemaLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _schemaLock.Dispose();
    }
}
