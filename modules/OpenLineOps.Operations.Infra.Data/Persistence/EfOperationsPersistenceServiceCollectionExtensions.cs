using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Operations.Domain.Repositories;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

public static class EfOperationsPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddEfSqliteOperationsPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        }

        var normalizedConnectionString = connectionString.Trim();
        SqliteOperationsStorage.EnsureDatabaseDirectory(normalizedConnectionString);

        services.AddDbContext<OperationsDbContext>(options => options.UseSqlite(normalizedConnectionString));
        services.AddScoped<IAlarmRepository, EfAlarmRepository>();

        return services;
    }

    public static IServiceCollection AddEfPostgreSqlOperationsPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        var normalizedConnectionString = connectionString.Trim();

        services.AddDbContext<OperationsDbContext>(options => options.UseNpgsql(normalizedConnectionString));
        services.AddScoped<IAlarmRepository, EfAlarmRepository>();

        return services;
    }
}
