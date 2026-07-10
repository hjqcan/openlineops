using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Application.ProjectWorkspaces;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Engineering.Infrastructure.Processes;
using OpenLineOps.Engineering.Infrastructure.Time;
using OpenLineOps.Processes.Application.Runtime;

namespace OpenLineOps.Engineering.Api.DependencyInjection;

public static class EngineeringModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsEngineeringModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);

        if (IsSqlite(persistenceOptions.Provider))
        {
            services.AddSingleton<IWorkspaceRepository>(_ =>
                new SqliteWorkspaceRepository(persistenceOptions.ResolveSqliteConnectionString()));
            services.AddSingleton<IEngineeringProjectRepository>(_ =>
                new SqliteEngineeringProjectRepository(persistenceOptions.ResolveSqliteConnectionString()));
            services.AddSingleton<IRecipeRepository>(_ =>
                new SqliteRecipeRepository(persistenceOptions.ResolveSqliteConnectionString()));
            services.AddSingleton<IStationProfileRepository>(_ =>
                new SqliteStationProfileRepository(persistenceOptions.ResolveSqliteConnectionString()));
        }
        else if (IsPostgreSql(persistenceOptions.Provider))
        {
            services.AddSingleton<IWorkspaceRepository>(_ =>
                new PostgresWorkspaceRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
            services.AddSingleton<IEngineeringProjectRepository>(_ =>
                new PostgresEngineeringProjectRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
            services.AddSingleton<IRecipeRepository>(_ =>
                new PostgresRecipeRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
            services.AddSingleton<IStationProfileRepository>(_ =>
                new PostgresStationProfileRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            services.AddSingleton<InMemoryWorkspaceRepository>();
            services.AddSingleton<IWorkspaceRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryWorkspaceRepository>());

            services.AddSingleton<InMemoryEngineeringProjectRepository>();
            services.AddSingleton<IEngineeringProjectRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryEngineeringProjectRepository>());

            services.AddSingleton<InMemoryRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryRecipeRepository>());

            services.AddSingleton<InMemoryStationProfileRepository>();
            services.AddSingleton<IStationProfileRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryStationProfileRepository>());
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported engineering persistence provider '{persistenceOptions.Provider}'.");
        }

        services.TryAddSingleton<
            IProjectEngineeringConfigurationRepository,
            FileSystemProjectEngineeringConfigurationRepository>();
        services.AddScoped<IEngineeringConfigurationService, EngineeringConfigurationService>();
        services.AddScoped<IProjectEngineeringConfigurationService, ProjectEngineeringConfigurationService>();
        services.AddScoped<IRuntimeConfigurationSnapshotResolver, EngineeringRuntimeConfigurationSnapshotResolver>();
        services.AddScoped<
            IProjectRuntimeConfigurationSnapshotResolver,
            ProjectEngineeringRuntimeConfigurationSnapshotResolver>();

        return services;
    }

    private static EngineeringPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(EngineeringPersistenceOptions.SectionName);

        return new EngineeringPersistenceOptions
        {
            Provider = section?["Provider"] ?? EngineeringPersistenceProviders.InMemory,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-engineering.sqlite"
        };
    }

    private static bool IsSqlite(string provider)
    {
        return string.Equals(provider, EngineeringPersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "SQLite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, EngineeringPersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostgreSql(string provider)
    {
        return string.Equals(provider, EngineeringPersistenceProviders.PostgreSql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
