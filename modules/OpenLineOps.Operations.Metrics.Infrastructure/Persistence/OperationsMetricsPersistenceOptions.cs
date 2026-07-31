using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

public sealed class OperationsMetricsPersistenceOptions
{
    public const string SectionName = "OpenLineOps:OperationsMetrics";

    public string ConnectionString { get; init; } =
        "Data Source=data/openlineops-operations-metrics.sqlite";

    public string ResolveConnectionString()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        var builder = new SqliteConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(
                builder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new InvalidOperationException(
                "Operations metrics persistence requires a file-backed SQLite data source.");
        }

        builder.DataSource = Path.GetFullPath(builder.DataSource);
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        return builder.ToString();
    }
}

public sealed class OperationsMetricsPersistenceOptionsValidator :
    IValidateOptions<OperationsMetricsPersistenceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        OperationsMetricsPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            _ = options.ResolveConnectionString();
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
