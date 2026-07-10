using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Api.Scripting;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Infrastructure.Persistence;
using OpenLineOps.Processes.Infrastructure.Scripting;
using OpenLineOps.Processes.Infrastructure.Time;

namespace OpenLineOps.Processes.Api.DependencyInjection;

public static class ProcessesModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsProcessesModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);

        if (IsSqlite(persistenceOptions.Provider))
        {
            services.AddSingleton<IProcessDefinitionRepository>(_ =>
                new SqliteProcessDefinitionRepository(persistenceOptions.ResolveSqliteConnectionString()));
            services.AddSingleton<IProcessBlocklyBlockDefinitionRepository>(_ =>
                new SqliteProcessBlocklyBlockDefinitionRepository(persistenceOptions.ResolveSqliteConnectionString()));
        }
        else if (IsPostgreSql(persistenceOptions.Provider))
        {
            services.AddSingleton<IProcessDefinitionRepository>(_ =>
                new PostgresProcessDefinitionRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
            services.AddSingleton<IProcessBlocklyBlockDefinitionRepository>(_ =>
                new PostgresProcessBlocklyBlockDefinitionRepository(
                    persistenceOptions.ResolvePostgreSqlConnectionString()));
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            services.AddSingleton<InMemoryProcessDefinitionRepository>();
            services.AddSingleton<IProcessDefinitionRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryProcessDefinitionRepository>());
            services.AddSingleton<IProcessBlocklyBlockDefinitionRepository, InMemoryProcessBlocklyBlockDefinitionRepository>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported process definition persistence provider '{persistenceOptions.Provider}'.");
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProcessBlocklyBlockCatalogSource, PluginCommandBlocklyBlockCatalogSource>());
        services.TryAddSingleton<IProcessBlocklyBlockCatalog, ProcessBlocklyBlockCatalog>();
        services.TryAddSingleton<IProcessScriptDefinitionValidator, PythonScriptDefinitionValidator>();
        services.TryAddSingleton<IProjectProcessDefinitionRepository, FileSystemProjectProcessDefinitionRepository>();
        services.TryAddSingleton<
            IProjectProcessBlocklyBlockDefinitionRepository,
            FileSystemProjectProcessBlocklyBlockDefinitionRepository>();
        services.AddScoped<IProcessDefinitionService, ProcessDefinitionService>();
        services.AddScoped<IProjectProcessDefinitionService, ProjectProcessDefinitionService>();
        services.AddScoped<IProjectProcessBlocklyBlockCatalog, ProjectProcessBlocklyBlockCatalog>();
        services.AddScoped<IProcessRuntimeSessionLauncher, ProcessRuntimeSessionLauncher>();
        services.AddScoped<IProjectProcessRuntimeSessionLauncher, ProjectProcessRuntimeSessionLauncher>();

        return services;
    }

    private static ProcessDefinitionPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(ProcessDefinitionPersistenceOptions.SectionName);

        return new ProcessDefinitionPersistenceOptions
        {
            Provider = section?["Provider"] ?? ProcessDefinitionPersistenceProviders.InMemory,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-processes.sqlite"
        };
    }

    private static bool IsSqlite(string provider)
    {
        return string.Equals(provider, ProcessDefinitionPersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "SQLite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, ProcessDefinitionPersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostgreSql(string provider)
    {
        return string.Equals(provider, ProcessDefinitionPersistenceProviders.PostgreSql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
