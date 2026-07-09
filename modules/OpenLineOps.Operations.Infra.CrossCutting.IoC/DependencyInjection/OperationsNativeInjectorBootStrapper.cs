using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Application.Services;
using OpenLineOps.Operations.Domain.Events.Converters;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;

public static class OperationsNativeInjectorBootStrapper
{
    public static IServiceCollection AddOpenLineOpsOperationsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);
        AddSharedServices(services);

        if (IsEfSqlite(persistenceOptions.Provider))
        {
            services.AddEfSqliteOperationsPersistence(persistenceOptions.ResolveSqliteConnectionString());
        }
        else if (IsPostgreSql(persistenceOptions.Provider))
        {
            services.AddEfPostgreSqlOperationsPersistence(persistenceOptions.ResolvePostgreSqlConnectionString());
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            var databaseName = configuration?
                .GetSection(OperationsPersistenceOptions.SectionName)["DatabaseName"];
            services.AddDbContext<OperationsDbContext>(options =>
                options.UseInMemoryDatabase(string.IsNullOrWhiteSpace(databaseName)
                    ? "OpenLineOps.Operations"
                    : databaseName));
            services.AddScoped<IAlarmRepository, EfAlarmRepository>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported operations persistence provider '{persistenceOptions.Provider}'.");
        }

        return services;
    }

    public static IServiceCollection AddOpenLineOpsOperationsModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        services.AddDbContext<OperationsDbContext>(configureDbContext);
        AddSharedServices(services);
        services.AddScoped<IAlarmRepository, EfAlarmRepository>();

        return services;
    }

    private static void AddSharedServices(IServiceCollection services)
    {
        services.TryAddSingleton<IntegrationDtoConverterRegistry>();
        services.AddSingleton<IIntegrationDtoConverter, AlarmIntegrationDtoConverter>();
        services.AddScoped<IAlarmAppService, AlarmAppService>();
    }

    private static OperationsPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(OperationsPersistenceOptions.SectionName);

        return new OperationsPersistenceOptions
        {
            Provider = section?["Provider"] ?? OperationsPersistenceProviders.EfSqlite,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-operations.sqlite"
        };
    }

    private static bool IsEfSqlite(string provider)
    {
        return string.Equals(provider, OperationsPersistenceProviders.EfSqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "EntityFrameworkSqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, OperationsPersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostgreSql(string provider)
    {
        return string.Equals(provider, OperationsPersistenceProviders.PostgreSql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
