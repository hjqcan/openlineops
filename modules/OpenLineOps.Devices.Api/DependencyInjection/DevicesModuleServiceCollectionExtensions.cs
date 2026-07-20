using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Devices.Api.ExternalPrograms;
using OpenLineOps.Devices.Application.Configuration;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;
using OpenLineOps.Devices.Infrastructure.Time;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Projects.Application.ExternalPrograms;
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
        var externalProgramHostOptions = LoadExternalProgramHostOptions(configuration);
        services.AddSingleton(externalProgramHostOptions);
        services.TryAddSingleton<IExternalProgramHost, ExternalProgramHost>();
        services.TryAddSingleton<IExternalProgramTrialExecutor, ExternalProgramResourceTrialExecutor>();

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
        services.TryAddSingleton<ProcessIsolatedPythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<IRuntimeScriptExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<ProcessIsolatedPythonScriptRuntimeScriptExecutor>());
        services.Replace(ServiceDescriptor.Singleton<IRuntimeCommandExecutor, ProjectReleaseRuntimeCommandExecutor>());
        services.AddScoped<IDeviceConfigurationService, DeviceConfigurationService>();

        return services;
    }

    private static void AddDevicePersistence(
        IServiceCollection services,
        DevicePersistenceOptions persistenceOptions)
    {
        switch (DevicePersistenceProviders.Parse(persistenceOptions.Provider))
        {
            case DevicePersistenceProvider.Sqlite:
                services.AddEfSqliteDevicePersistence(persistenceOptions.ResolveSqliteConnectionString());
                services.AddHostedService<EfSqliteDevicesDatabaseMigrator>();
                break;
            case DevicePersistenceProvider.InMemory:
                services.AddSingleton<InMemoryDeviceDefinitionRepository>();
                services.AddSingleton<IDeviceDefinitionRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryDeviceDefinitionRepository>());

                services.AddSingleton<InMemoryDeviceInstanceRepository>();
                services.AddSingleton<IDeviceInstanceRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryDeviceInstanceRepository>());
                break;
        }
    }

    private static DevicePersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(DevicePersistenceOptions.SectionName);

        return new DevicePersistenceOptions
        {
            Provider = section?["Provider"] ?? DevicePersistenceProviders.Sqlite,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-devices.sqlite"
        };
    }

    private static PythonScriptRuntimeOptions LoadPythonScriptRuntimeOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(PythonScriptRuntimeOptions.SectionName);
        var executionMode = section?["ExecutionMode"]
            ?? PythonScriptRuntimeExecutionModes.ProcessIsolated;
        PythonScriptRuntimeExecutionModes.RequireCurrent(executionMode);

        return new PythonScriptRuntimeOptions
        {
            ExecutionMode = executionMode,
            WorkerFileName = section?["WorkerFileName"],
            WorkerArguments = section?["WorkerArguments"],
            WorkerWorkingDirectory = section?["WorkerWorkingDirectory"],
            Sandbox = LoadPythonScriptWorkerSandboxOptions(section?.GetSection("Sandbox"))
        };
    }

    private static ExternalProgramHostOptions LoadExternalProgramHostOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(ExternalProgramHostOptions.SectionName);
        var traceArtifactRoot = configuration?
            .GetSection("OpenLineOps:Traceability:ArtifactStorage")["RootPath"];
        var options = new ExternalProgramHostOptions
        {
            WorkspaceRootPath = section?["WorkspaceRootPath"]
                ?? "data/openlineops-external-program-workspaces",
            EvidenceRootPath = section?["EvidenceRootPath"]
                ?? traceArtifactRoot
                ?? "data/openlineops-traceability-artifacts",
            MaximumStandardOutputBytes = ReadOptionalInt(
                section?["MaximumStandardOutputBytes"],
                4 * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumStandardOutputBytes"),
            MaximumStandardErrorBytes = ReadOptionalInt(
                section?["MaximumStandardErrorBytes"],
                4 * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumStandardErrorBytes"),
            MaximumArtifactCount = ReadOptionalInt(
                section?["MaximumArtifactCount"],
                128,
                $"{ExternalProgramHostOptions.SectionName}:MaximumArtifactCount"),
            MaximumArtifactBytes = ReadOptionalLong(
                section?["MaximumArtifactBytes"],
                128L * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumArtifactBytes"),
            MaximumTotalArtifactBytes = ReadOptionalLong(
                section?["MaximumTotalArtifactBytes"],
                512L * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumTotalArtifactBytes"),
            MaximumWorkingSetBytes = ReadOptionalLong(
                section?["MaximumWorkingSetBytes"],
                1024L * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumWorkingSetBytes"),
            MaximumJobMemoryBytes = ReadOptionalLong(
                section?["MaximumJobMemoryBytes"],
                4L * 1024 * 1024 * 1024,
                $"{ExternalProgramHostOptions.SectionName}:MaximumJobMemoryBytes"),
            MaximumProcessCount = ReadOptionalInt(
                section?["MaximumProcessCount"],
                32,
                $"{ExternalProgramHostOptions.SectionName}:MaximumProcessCount"),
            MaximumCpuTimeMilliseconds = ReadOptionalLong(
                section?["MaximumCpuTimeMilliseconds"],
                3_600_000,
                $"{ExternalProgramHostOptions.SectionName}:MaximumCpuTimeMilliseconds"),
            MaximumExecutionTimeMilliseconds = ReadOptionalLong(
                section?["MaximumExecutionTimeMilliseconds"],
                3_600_000,
                $"{ExternalProgramHostOptions.SectionName}:MaximumExecutionTimeMilliseconds"),
            MaximumOutputDirectoryEntries = ReadOptionalInt(
                section?["MaximumOutputDirectoryEntries"],
                512,
                $"{ExternalProgramHostOptions.SectionName}:MaximumOutputDirectoryEntries"),
            MaximumOutputDirectoryDepth = ReadOptionalInt(
                section?["MaximumOutputDirectoryDepth"],
                8,
                $"{ExternalProgramHostOptions.SectionName}:MaximumOutputDirectoryDepth"),
            OutputDirectoryScanIntervalMilliseconds = ReadOptionalInt(
                section?["OutputDirectoryScanIntervalMilliseconds"],
                50,
                $"{ExternalProgramHostOptions.SectionName}:OutputDirectoryScanIntervalMilliseconds"),
            RequireRestrictedHostIdentity = ReadOptionalBoolean(
                section?["RequireRestrictedHostIdentity"],
                defaultValue: false,
                $"{ExternalProgramHostOptions.SectionName}:RequireRestrictedHostIdentity"),
            RequireImmutableContentProtection = ReadOptionalBoolean(
                section?["RequireImmutableContentProtection"],
                defaultValue: false,
                $"{ExternalProgramHostOptions.SectionName}:RequireImmutableContentProtection"),
            RequireAppContainerIsolation = ReadOptionalBoolean(
                section?["RequireAppContainerIsolation"],
                defaultValue: false,
                $"{ExternalProgramHostOptions.SectionName}:RequireAppContainerIsolation"),
            AppContainerProfileName = section?["AppContainerProfileName"],
            AppContainerProfileExternallyOwned = ReadOptionalBoolean(
                section?["AppContainerProfileExternallyOwned"],
                defaultValue: false,
                $"{ExternalProgramHostOptions.SectionName}:AppContainerProfileExternallyOwned"),
            RestrictedServiceSid = section?["RestrictedServiceSid"]
        };
        options.AllowedInheritedEnvironmentVariables.Clear();
        foreach (var variable in section?
                     .GetSection("AllowedInheritedEnvironmentVariables")
                     .Get<string[]>()
                 ?? ["SystemRoot", "WINDIR"])
        {
            options.AllowedInheritedEnvironmentVariables.Add(variable);
        }

        options.Validate();
        return options;
    }

    private static PythonScriptWorkerSandboxOptions LoadPythonScriptWorkerSandboxOptions(
        IConfigurationSection? section)
    {
        var isolationMode = section?["IsolationMode"]
            ?? PythonScriptWorkerIsolationModes.ExternalProcess;
        _ = PythonScriptWorkerIsolationModes.Parse(isolationMode);
        var options = new PythonScriptWorkerSandboxOptions
        {
            RequireLeastPrivilegeExecution = ReadOptionalBoolean(
                section?["RequireLeastPrivilegeExecution"],
                defaultValue: false,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:RequireLeastPrivilegeExecution"),
            IsolationMode = isolationMode,
            LeastPrivilegeIdentity = section?["LeastPrivilegeIdentity"],
            LeastPrivilegeLauncherExecutable = section?["LeastPrivilegeLauncherExecutable"],
            LeastPrivilegeArgumentsTemplate = section?["LeastPrivilegeArgumentsTemplate"],
            LeastPrivilegeNoInteractivePrompt = ReadOptionalBoolean(
                section?["LeastPrivilegeNoInteractivePrompt"],
                defaultValue: true,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:LeastPrivilegeNoInteractivePrompt"),
            ContainerImage = section?["ContainerImage"],
            ContainerRuntimeExecutable = section?["ContainerRuntimeExecutable"],
            ContainerMountSource = section?["ContainerMountSource"],
            ContainerWorkspacePath = section?["ContainerWorkspacePath"] ?? "/openlineops/script-worker",
            ContainerWorkingDirectory = section?["ContainerWorkingDirectory"],
            ContainerExecutablePath = section?["ContainerExecutablePath"],
            ContainerArgumentsTemplate = section?["ContainerArgumentsTemplate"] ?? "{WorkerArguments}",
            ContainerNetwork = section?["ContainerNetwork"] ?? "none",
            ContainerNoNewPrivileges = ReadOptionalBoolean(
                section?["ContainerNoNewPrivileges"],
                defaultValue: true,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:ContainerNoNewPrivileges"),
            ContainerDropAllCapabilities = ReadOptionalBoolean(
                section?["ContainerDropAllCapabilities"],
                defaultValue: true,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:ContainerDropAllCapabilities"),
            ContainerReadOnlyRootFilesystem = ReadOptionalBoolean(
                section?["ContainerReadOnlyRootFilesystem"],
                defaultValue: true,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:ContainerReadOnlyRootFilesystem"),
            ContainerMountReadOnly = ReadOptionalBoolean(
                section?["ContainerMountReadOnly"],
                defaultValue: true,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:ContainerMountReadOnly"),
            ContainerPidsLimit = ReadOptionalInt(
                section?["ContainerPidsLimit"],
                defaultValue: 128,
                $"{PythonScriptRuntimeOptions.SectionName}:Sandbox:ContainerPidsLimit")
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

    private static bool ReadOptionalBoolean(string? value, bool defaultValue, string configurationPath)
    {
        if (value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            "true" or "True" => true,
            "false" or "False" => false,
            _ => throw new InvalidOperationException(
                $"Configuration '{configurationPath}' must be exactly 'true' or 'false'.")
        };
    }

    private static int ReadOptionalInt(string? value, int defaultValue, string configurationPath)
    {
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration '{configurationPath}' must be an integer.");
    }

    private static long ReadOptionalLong(string? value, long defaultValue, string configurationPath)
    {
        if (value is null)
        {
            return defaultValue;
        }

        return long.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration '{configurationPath}' must be an integer.");
    }
}
