using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Api.DependencyInjection;

public sealed class CommissioningPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Commissioning:Persistence";
    public const string SqliteProvider = "Sqlite";
    public const string InMemoryProvider = "InMemory";
    public const string DefaultDatabasePath =
        "data/openlineops-commissioning.sqlite";

    public string Provider { get; set; } = SqliteProvider;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = DefaultDatabasePath;

    public string ResolveSqliteConnectionString()
    {
        var validation = CommissioningPersistenceOptionsValidator.ValidateValue(this);
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

public sealed class CommissioningAuthorizationOptions
{
    public const string SectionName = "OpenLineOps:Commissioning:Authorization";

    public Dictionary<string, string[]> RoleCapabilities { get; set; } =
        new(StringComparer.Ordinal)
        {
            [OpenLineOpsApiSecurity.EngineeringRole] =
                Enum.GetNames<CommissioningCapability>()
        };
}

public sealed class CommissioningPersistenceOptionsValidator :
    IValidateOptions<CommissioningPersistenceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        CommissioningPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failure = ValidateValue(options);
        return failure is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failure);
    }

    internal static string? ValidateValue(CommissioningPersistenceOptions options)
    {
        if (string.Equals(
                options.Provider,
                CommissioningPersistenceOptions.InMemoryProvider,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(
                options.Provider,
                CommissioningPersistenceOptions.SqliteProvider,
                StringComparison.Ordinal))
        {
            return "Commissioning persistence Provider must be exactly "
                + $"'{CommissioningPersistenceOptions.SqliteProvider}' or "
                + $"'{CommissioningPersistenceOptions.InMemoryProvider}'.";
        }

        var dataSource = options.ConnectionString is null
            ? options.DatabasePath
            : TryReadDataSource(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(dataSource)
            || !string.Equals(dataSource, dataSource.Trim(), StringComparison.Ordinal)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return "Commissioning persistence requires a canonical file-backed SQLite database.";
        }

        try
        {
            _ = Path.GetFullPath(dataSource);
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Commissioning SQLite path is invalid: {exception.Message}";
        }
    }

    private static string? TryReadDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || !string.Equals(
                connectionString,
                connectionString.Trim(),
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public sealed class CommissioningAuthorizationOptionsValidator :
    IValidateOptions<CommissioningAuthorizationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        CommissioningAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RoleCapabilities.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "Commissioning authorization must grant at least one role.");
        }

        foreach (var (role, capabilities) in options.RoleCapabilities)
        {
            if (string.IsNullOrWhiteSpace(role)
                || !string.Equals(role, role.Trim(), StringComparison.Ordinal)
                || capabilities is null
                || capabilities.Length == 0)
            {
                return ValidateOptionsResult.Fail(
                    "Commissioning role and capability grants must be canonical and non-empty.");
            }

            var parsed = new HashSet<CommissioningCapability>();
            foreach (var capability in capabilities)
            {
                if (!TryParseCapability(capability, out var value)
                    || !parsed.Add(value))
                {
                    return ValidateOptionsResult.Fail(
                        $"Commissioning capability grant '{capability}' for role "
                        + $"'{role}' is invalid or duplicated.");
                }
            }
        }

        return ValidateOptionsResult.Success;
    }

    internal static bool TryParseCapability(
        string? value,
        out CommissioningCapability capability)
    {
        return Enum.TryParse(value, ignoreCase: false, out capability)
            && Enum.IsDefined(capability)
            && string.Equals(capability.ToString(), value, StringComparison.Ordinal);
    }
}
