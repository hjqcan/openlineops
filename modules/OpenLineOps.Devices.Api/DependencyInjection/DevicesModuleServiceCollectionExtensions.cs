using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Devices.Application.Configuration;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;
using OpenLineOps.Devices.Infrastructure.Time;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Devices.Api.DependencyInjection;

public static class DevicesModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsDevicesModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IConfiguration>(_ => configuration ?? new ConfigurationBuilder().Build());
        services.TryAddSingleton<IClock, SystemClock>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);
        AddDevicePersistence(services, persistenceOptions);

        var pythonScriptRuntimeOptions = LoadPythonScriptRuntimeOptions(configuration);
        services.AddSingleton(pythonScriptRuntimeOptions);

        services.TryAddSingleton<ProjectReleaseSimulatorDeviceCommandExecutor>();
        services.TryAddSingleton<PluginDeviceCommandExecutor>();
        services.TryAddSingleton<IDeviceCommandExecutor, ProjectReleaseDeviceCommandExecutor>();
        services.TryAddSingleton(serviceProvider => new ProjectReleaseDeviceCommandRouteResolver(
            serviceProvider.GetRequiredService<IAutomationProjectRepository>(),
            serviceProvider.GetRequiredService<IProjectReleaseArtifactStore>(),
            serviceProvider.GetRequiredService<IProjectEngineeringConfigurationRepository>()));
        services.Replace(ServiceDescriptor.Singleton<IProjectReleaseRuntimeCommandRouteResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<ProjectReleaseDeviceCommandRouteResolver>()));
        services.TryAddSingleton<DeviceRuntimeCommandExecutor>();
        services.TryAddSingleton<RuntimeFlowCommandExecutor>();
        services.TryAddSingleton<PluginRuntimeCommandExecutor>();
        services.TryAddSingleton<PythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<ProcessIsolatedPythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<IRuntimeScriptExecutor, ConfigurableRuntimeScriptExecutor>();
        services.Replace(ServiceDescriptor.Singleton<IRuntimeCommandExecutor, ProjectReleaseRuntimeCommandExecutor>());
        services.AddScoped<IDeviceConfigurationService, DeviceConfigurationService>();

        return services;
    }

    private static void AddDevicePersistence(
        IServiceCollection services,
        DevicePersistenceOptions persistenceOptions)
    {
        if (IsEfSqlite(persistenceOptions.Provider))
        {
            services.AddEfSqliteDevicePersistence(persistenceOptions.ResolveSqliteConnectionString());
            services.AddHostedService<EfSqliteDevicesDatabaseMigrator>();
        }
        else if (IsSqlite(persistenceOptions.Provider))
        {
            services.AddSingleton<IDeviceDefinitionRepository>(_ =>
                new SqliteDeviceDefinitionRepository(persistenceOptions.ResolveSqliteConnectionString()));
            services.AddSingleton<IDeviceInstanceRepository>(_ =>
                new SqliteDeviceInstanceRepository(persistenceOptions.ResolveSqliteConnectionString()));
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            services.AddSingleton<InMemoryDeviceDefinitionRepository>();
            services.AddSingleton<IDeviceDefinitionRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryDeviceDefinitionRepository>());

            services.AddSingleton<InMemoryDeviceInstanceRepository>();
            services.AddSingleton<IDeviceInstanceRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryDeviceInstanceRepository>());
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported device persistence provider '{persistenceOptions.Provider}'.");
        }
    }

    private static DevicePersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(DevicePersistenceOptions.SectionName);

        return new DevicePersistenceOptions
        {
            Provider = section?["Provider"] ?? DevicePersistenceProviders.EfSqlite,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-devices.sqlite"
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
        return string.Equals(provider, DevicePersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "SQLite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEfSqlite(string provider)
    {
        return string.Equals(provider, DevicePersistenceProviders.EfSqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "EntityFrameworkSqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, DevicePersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
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
