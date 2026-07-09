using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Infrastructure.Commands;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.Runtime.Infrastructure.Time;

namespace OpenLineOps.Runtime.Api.DependencyInjection;

public static class RuntimeModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsRuntimeModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IConfiguration>(_ => configuration ?? new ConfigurationBuilder().Build());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRuntimeIdProvider, GuidRuntimeIdProvider>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);
        var commandExecutorOptions = LoadCommandExecutorOptions(configuration);
        services.AddSingleton(commandExecutorOptions);
        var pythonScriptRuntimeOptions = LoadPythonScriptRuntimeOptions(configuration);
        services.AddSingleton(pythonScriptRuntimeOptions);

        if (IsSqlite(persistenceOptions.Provider))
        {
            services.AddSingleton<IRuntimeSessionRepository>(_ =>
                new SqliteRuntimeSessionRepository(persistenceOptions.ResolveSqliteConnectionString()));
        }
        else if (IsPostgreSql(persistenceOptions.Provider))
        {
            services.AddSingleton<IRuntimeSessionRepository>(_ =>
                new PostgresRuntimeSessionRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            services.AddSingleton<InMemoryRuntimeSessionRepository>();
            services.AddSingleton<IRuntimeSessionRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryRuntimeSessionRepository>());
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported runtime session persistence provider '{persistenceOptions.Provider}'.");
        }

        services.AddSingleton<InMemoryRuntimeDomainEventPublisher>();
        services.AddSingleton<IRuntimeDomainEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<InMemoryRuntimeDomainEventPublisher>());

        services.TryAddSingleton<SimulatedRuntimeCommandExecutor>();
        services.TryAddSingleton<PythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<ProcessIsolatedPythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<IRuntimeScriptExecutor, ConfigurableRuntimeScriptExecutor>();
        services.TryAddSingleton<RuntimeAutomationPlanDispatcher>();
        services.Replace(ServiceDescriptor.Singleton<IRuntimeCommandExecutor, ConfigurableRuntimeCommandExecutor>());
        services.AddSingleton<RuntimeMonitoringProjection>();
        services.AddSingleton<IRuntimeMonitoringService>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeMonitoringProjection>());
        services.AddSingleton<IRuntimeDomainEventSubscriber>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeMonitoringProjection>());
        services.AddScoped<IRuntimeSessionRunner, RuntimeSessionRunner>();
        services.AddScoped<IRuntimeSessionRecoveryService, RuntimeSessionRecoveryService>();

        return services;
    }

    private static RuntimeSessionPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(RuntimeSessionPersistenceOptions.SectionName);

        return new RuntimeSessionPersistenceOptions
        {
            Provider = section?["Provider"] ?? RuntimeSessionPersistenceProviders.InMemory,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-runtime.sqlite"
        };
    }

    private static RuntimeCommandExecutorOptions LoadCommandExecutorOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(RuntimeCommandExecutorOptions.SectionName);

        return new RuntimeCommandExecutorOptions
        {
            CommandExecutor = section?["CommandExecutor"] ?? RuntimeCommandExecutors.Simulator
        };
    }

    private static PythonScriptRuntimeOptions LoadPythonScriptRuntimeOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(PythonScriptRuntimeOptions.SectionName);

        return new PythonScriptRuntimeOptions
        {
            ExecutionMode = section?["ExecutionMode"] ?? PythonScriptRuntimeExecutionModes.InProcessTrusted,
            WorkerFileName = section?["WorkerFileName"],
            WorkerArguments = section?["WorkerArguments"],
            WorkerWorkingDirectory = section?["WorkerWorkingDirectory"],
            Sandbox = LoadPythonScriptWorkerSandboxOptions(section?.GetSection("Sandbox"))
        };
    }

    private static PythonScriptWorkerSandboxOptions LoadPythonScriptWorkerSandboxOptions(
        IConfigurationSection? section)
    {
        var options = new PythonScriptWorkerSandboxOptions
        {
            RequireLeastPrivilegeExecution = TryReadBoolean(section?["RequireLeastPrivilegeExecution"], defaultValue: false),
            IsolationMode = section?["IsolationMode"] ?? PythonScriptWorkerIsolationModes.ExternalProcess,
            LeastPrivilegeIdentity = section?["LeastPrivilegeIdentity"],
            LeastPrivilegeLauncherExecutable = section?["LeastPrivilegeLauncherExecutable"],
            LeastPrivilegeArgumentsTemplate = section?["LeastPrivilegeArgumentsTemplate"],
            LeastPrivilegeNoInteractivePrompt = TryReadBoolean(section?["LeastPrivilegeNoInteractivePrompt"], defaultValue: true),
            ContainerImage = section?["ContainerImage"],
            ContainerRuntimeExecutable = section?["ContainerRuntimeExecutable"],
            ContainerMountSource = section?["ContainerMountSource"],
            ContainerWorkspacePath = section?["ContainerWorkspacePath"] ?? "/openlineops/script-worker",
            ContainerWorkingDirectory = section?["ContainerWorkingDirectory"],
            ContainerExecutablePath = section?["ContainerExecutablePath"],
            ContainerArgumentsTemplate = section?["ContainerArgumentsTemplate"] ?? "{WorkerArguments}",
            ContainerNetwork = section?["ContainerNetwork"] ?? "none",
            ContainerNoNewPrivileges = TryReadBoolean(section?["ContainerNoNewPrivileges"], defaultValue: true),
            ContainerDropAllCapabilities = TryReadBoolean(section?["ContainerDropAllCapabilities"], defaultValue: true),
            ContainerReadOnlyRootFilesystem = TryReadBoolean(section?["ContainerReadOnlyRootFilesystem"], defaultValue: true),
            ContainerMountReadOnly = TryReadBoolean(section?["ContainerMountReadOnly"], defaultValue: true),
            ContainerPidsLimit = TryReadInt(section?["ContainerPidsLimit"], defaultValue: 128)
        };

        foreach (var argument in section?.GetSection("AdditionalContainerRunArguments").Get<string[]>() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(argument))
            {
                options.AdditionalContainerRunArguments.Add(argument);
            }
        }

        return options;
    }

    private static bool IsSqlite(string provider)
    {
        return string.Equals(provider, RuntimeSessionPersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "SQLite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, RuntimeSessionPersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostgreSql(string provider)
    {
        return string.Equals(provider, RuntimeSessionPersistenceProviders.PostgreSql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBoolean(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int TryReadInt(string? value, int defaultValue)
    {
        return int.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }
}
