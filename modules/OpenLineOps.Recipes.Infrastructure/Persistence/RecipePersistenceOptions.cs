using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OpenLineOps.Recipes.Infrastructure.Persistence;

public sealed class RecipePersistenceOptions
{
    public const string SectionName = "OpenLineOps:Recipes";

    public string Provider { get; set; } = "sqlite";

    public string DatabasePath { get; set; } = "data/openlineops-recipes.sqlite";

    public string? ConnectionString { get; set; }

    public string EngineeringProjectId { get; set; } = string.Empty;

    public string EngineeringApplicationId { get; set; } = string.Empty;

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ConnectionString;
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }
}

public sealed class RecipePersistenceOptionsValidator
    : IValidateOptions<RecipePersistenceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        RecipePersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.Provider, "sqlite", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "OpenLineOps Recipes persistence provider must be exactly 'sqlite'.");
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath)
            || !string.Equals(
                options.DatabasePath,
                options.DatabasePath.Trim(),
                StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "OpenLineOps Recipes database path must be canonical non-empty text.");
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder(
                options.ResolveConnectionString());
            if (builder.Mode == SqliteOpenMode.Memory
                || string.IsNullOrWhiteSpace(builder.DataSource)
                || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
                || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    && builder.DataSource.Contains(
                        "mode=memory",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return ValidateOptionsResult.Fail(
                    "OpenLineOps Recipes persistence requires file-backed SQLite.");
            }
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }

        return ValidateOptionsResult.Success;
    }
}
