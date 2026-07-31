using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OpenLineOps.Maintenance.Infrastructure.Persistence;

public sealed class MaintenancePersistenceOptions
{
    public const string SectionName = "OpenLineOps:Maintenance:Persistence";
    public const string DefaultDatabasePath =
        "data/openlineops-maintenance.sqlite";

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = DefaultDatabasePath;

    public string ResolveConnectionString()
    {
        var failure = MaintenancePersistenceOptionsValidator.ValidateValue(this);
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
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
    }
}

public sealed class MaintenancePersistenceOptionsValidator :
    IValidateOptions<MaintenancePersistenceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        MaintenancePersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failure = ValidateValue(options);
        return failure is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failure);
    }

    internal static string? ValidateValue(
        MaintenancePersistenceOptions options)
    {
        if (options.ConnectionString is not null)
        {
            if (!IsCanonical(options.ConnectionString))
            {
                return "Maintenance SQLite ConnectionString must be non-empty canonical text.";
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder(
                    options.ConnectionString);
                return ValidateFileBackedDataSource(
                    builder.DataSource,
                    builder.Mode);
            }
            catch (Exception exception)
                when (exception is ArgumentException or FormatException)
            {
                return $"Maintenance SQLite ConnectionString is invalid: {exception.Message}";
            }
        }

        if (!IsCanonical(options.DatabasePath))
        {
            return "Maintenance SQLite DatabasePath must be a non-empty canonical path.";
        }

        try
        {
            _ = Path.GetFullPath(options.DatabasePath);
            return ValidateFileBackedDataSource(
                options.DatabasePath,
                SqliteOpenMode.ReadWriteCreate);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return $"Maintenance SQLite DatabasePath is invalid: {exception.Message}";
        }
    }

    private static string? ValidateFileBackedDataSource(
        string? dataSource,
        SqliteOpenMode mode)
    {
        if (string.IsNullOrWhiteSpace(dataSource)
            || mode == SqliteOpenMode.Memory
            || string.Equals(
                dataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || dataSource.Contains(
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(
                "file:",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Maintenance persistence requires a file-backed SQLite database path.";
        }

        return null;
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);
}
