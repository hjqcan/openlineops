using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

public sealed class QualityPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Quality:Persistence";
    public const string SqliteProvider = "Sqlite";
    public const string DefaultDatabasePath = "data/openlineops-quality.sqlite";

    public string Provider { get; set; } = SqliteProvider;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = DefaultDatabasePath;

    public string ResolveSqliteConnectionString()
    {
        var validation = QualityPersistenceOptionsValidator.ValidateValue(this);
        if (validation is not null)
        {
            throw new InvalidOperationException(validation);
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

public sealed class QualityPersistenceOptionsValidator
    : IValidateOptions<QualityPersistenceOptions>
{
    public ValidateOptionsResult Validate(string? name, QualityPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failure = ValidateValue(options);
        return failure is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failure);
    }

    internal static string? ValidateValue(QualityPersistenceOptions options)
    {
        if (!string.Equals(
                options.Provider,
                QualityPersistenceOptions.SqliteProvider,
                StringComparison.Ordinal))
        {
            return $"Quality persistence Provider must be exactly '{QualityPersistenceOptions.SqliteProvider}'.";
        }

        if (options.ConnectionString is not null)
        {
            if (!IsCanonical(options.ConnectionString))
            {
                return "Quality SQLite ConnectionString must be a non-empty canonical string.";
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                return ValidateFileBackedDataSource(builder.DataSource);
            }
            catch (ArgumentException exception)
            {
                return $"Quality SQLite ConnectionString is invalid: {exception.Message}";
            }
        }

        if (!IsCanonical(options.DatabasePath))
        {
            return "Quality SQLite DatabasePath must be a non-empty canonical path.";
        }

        try
        {
            _ = Path.GetFullPath(options.DatabasePath);
            return ValidateFileBackedDataSource(options.DatabasePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Quality SQLite DatabasePath is invalid: {exception.Message}";
        }
    }

    private static string? ValidateFileBackedDataSource(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)))
        {
            return "Quality persistence requires a file-backed SQLite database.";
        }

        return null;
    }

    private static bool IsCanonical(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }
}
