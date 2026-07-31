using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OpenLineOps.Integration.Api.DependencyInjection;

public sealed class IntegrationPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Integration:Persistence";
    public const string SqliteProvider = "Sqlite";
    public const string DefaultDatabasePath = "data/openlineops-integration.sqlite";

    public string Provider { get; set; } = SqliteProvider;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = DefaultDatabasePath;

    public string ResolveConnectionString()
    {
        var failure = IntegrationPersistenceOptionsValidator.ValidateValue(this);
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        if (ConnectionString is not null)
        {
            return ConnectionString;
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }
}

public sealed class IntegrationPersistenceOptionsValidator :
    IValidateOptions<IntegrationPersistenceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        IntegrationPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failure = ValidateValue(options);
        return failure is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failure);
    }

    internal static string? ValidateValue(IntegrationPersistenceOptions options)
    {
        if (!string.Equals(
                options.Provider,
                IntegrationPersistenceOptions.SqliteProvider,
                StringComparison.Ordinal))
        {
            return "Integration persistence Provider must be exactly 'Sqlite'.";
        }

        if (options.ConnectionString is not null)
        {
            if (!IsCanonical(options.ConnectionString))
            {
                return "Integration SQLite ConnectionString must be canonical text.";
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                return ValidateDataSource(builder.DataSource, builder.Mode);
            }
            catch (ArgumentException exception)
            {
                return $"Integration SQLite ConnectionString is invalid: {exception.Message}";
            }
        }

        if (!IsCanonical(options.DatabasePath))
        {
            return "Integration SQLite DatabasePath must be a canonical path.";
        }

        try
        {
            _ = Path.GetFullPath(options.DatabasePath);
            return ValidateDataSource(options.DatabasePath, SqliteOpenMode.ReadWriteCreate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Integration SQLite DatabasePath is invalid: {exception.Message}";
        }
    }

    private static string? ValidateDataSource(
        string? dataSource,
        SqliteOpenMode mode)
    {
        return string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || mode == SqliteOpenMode.Memory
                ? "Integration persistence requires a file-backed SQLite database."
                : null;
    }

    private static bool IsCanonical(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }
}
