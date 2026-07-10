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
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Infrastructure.Commands;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.Runtime.Infrastructure.Time;
using OpenLineOps.Runtime.Api.HostedServices;

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
        var pythonScriptRuntimeOptions = LoadPythonScriptRuntimeOptions(configuration);
        services.AddSingleton(pythonScriptRuntimeOptions);

        switch (RuntimeSessionPersistenceProviders.Parse(persistenceOptions.Provider))
        {
            case RuntimeSessionPersistenceProvider.Sqlite:
                var sqliteConnectionString = persistenceOptions.ResolveSqliteConnectionString();
                services.AddSingleton<IRuntimeSessionRepository>(_ =>
                    new SqliteRuntimeSessionRepository(sqliteConnectionString));
                services.AddSingleton<IProductionRunRepository>(_ =>
                    new SqliteProductionRunRepository(sqliteConnectionString));
                services.AddSingleton(new SqliteRuntimeStoreExclusiveLease(sqliteConnectionString));
                services.AddHostedService<SqliteRuntimeStoreLeaseHostedService>();
                break;
            case RuntimeSessionPersistenceProvider.InMemory:
                services.AddSingleton<InMemoryRuntimeSessionRepository>();
                services.AddSingleton<IRuntimeSessionRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryRuntimeSessionRepository>());
                services.AddSingleton<InMemoryProductionRunRepository>();
                services.AddSingleton<IProductionRunRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryProductionRunRepository>());
                break;
        }

        services.AddSingleton<IRuntimeDomainEventPublisher, RuntimeDomainEventPublisher>();
        services.AddSingleton<IProductionRunTerminalOutboxDispatcher,
            ProductionRunTerminalOutboxDispatcher>();

        services.TryAddSingleton<RuntimeFlowCommandExecutor>();
        services.TryAddSingleton<PluginRuntimeCommandExecutor>();
        services.TryAddSingleton<ProcessIsolatedPythonScriptRuntimeScriptExecutor>();
        services.TryAddSingleton<IRuntimeScriptExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<ProcessIsolatedPythonScriptRuntimeScriptExecutor>());
        services.AddSingleton<RuntimeMonitoringProjection>();
        services.AddSingleton<IRuntimeMonitoringService>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeMonitoringProjection>());
        services.AddSingleton<IRuntimeDomainEventSubscriber>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeMonitoringProjection>());
        services.AddScoped<IRuntimeSessionRunner, RuntimeSessionRunner>();
        services.AddScoped<IProductionRunRunner, ProductionRunRunner>();
        services.AddScoped<IRuntimeSessionRecoveryService, RuntimeSessionRecoveryService>();
        services.AddScoped<IProductionRunRecoveryService, ProductionRunRecoveryService>();
        services.AddHostedService<ProductionRunStartupRecoveryHostedService>();

        return services;
    }

    private static RuntimeSessionPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(RuntimeSessionPersistenceOptions.SectionName);

        return new RuntimeSessionPersistenceOptions
        {
            Provider = section?["Provider"] ?? RuntimeSessionPersistenceProviders.Sqlite,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-runtime.sqlite"
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
            "true" => true,
            "false" => false,
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
}
