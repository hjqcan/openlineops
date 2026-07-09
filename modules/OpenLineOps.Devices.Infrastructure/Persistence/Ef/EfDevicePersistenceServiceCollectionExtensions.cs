using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Application.Persistence;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public static class EfDevicePersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddEfSqliteDevicePersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        }

        var normalizedConnectionString = connectionString.Trim();

        services.AddDbContext<DevicesDbContext>(options => options.UseSqlite(normalizedConnectionString));
        services.AddScoped<IDeviceDefinitionRepository, EfDeviceDefinitionRepository>();
        services.AddScoped<IDeviceInstanceRepository, EfDeviceInstanceRepository>();

        return services;
    }
}
