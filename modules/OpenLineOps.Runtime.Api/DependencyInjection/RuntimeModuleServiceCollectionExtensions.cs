using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Api.HostedServices;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Safety;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Infrastructure.Commands;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Execution;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.Runtime.Infrastructure.Time;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Api.DependencyInjection;

public static class RuntimeModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsRuntimeModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddLogging();
        services.TryAddSingleton<IConfiguration>(_ => configuration ?? new ConfigurationBuilder().Build());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRuntimeIdProvider, GuidRuntimeIdProvider>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        var coordinationOptions = LoadCoordinationPersistenceOptions(configuration);
        var transportOptions = LoadStationCoordinatorTransportOptions(configuration);
        var stationExecutionOptions = LoadStationExecutionOptions(configuration);
        services.AddSingleton(persistenceOptions);
        services.AddSingleton(coordinationOptions);
        services.AddSingleton(transportOptions);
        services.AddSingleton(stationExecutionOptions);
        var pythonScriptRuntimeOptions = LoadPythonScriptRuntimeOptions(configuration);
        services.AddSingleton(pythonScriptRuntimeOptions);

        switch (RuntimeSessionPersistenceProviders.Parse(persistenceOptions.Provider))
        {
            case RuntimeSessionPersistenceProvider.Sqlite:
                var sqliteConnectionString = persistenceOptions.ResolveSqliteConnectionString();
                services.AddSingleton<IRuntimeSessionRepository>(_ =>
                    new SqliteRuntimeSessionRepository(sqliteConnectionString));
                services.AddSingleton(new SqliteRuntimeStoreExclusiveLease(sqliteConnectionString));
                services.AddHostedService<SqliteRuntimeStoreLeaseHostedService>();
                break;
            case RuntimeSessionPersistenceProvider.InMemory:
                services.AddSingleton<InMemoryRuntimeSessionRepository>();
                services.AddSingleton<IRuntimeSessionRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryRuntimeSessionRepository>());
                break;
        }

        var coordinationProvider = ProductionCoordinationPersistenceProviders.Parse(
            coordinationOptions.Provider);
        switch (coordinationProvider)
        {
            case ProductionCoordinationPersistenceProvider.PostgreSql:
                var productionPostgreSqlConnectionString =
                    coordinationOptions.ResolvePostgreSqlConnectionString();
                services.AddSingleton(_ => new PostgreSqlProductionCoordinationStore(
                    productionPostgreSqlConnectionString));
                services.AddSingleton<IProductionRunRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<PostgreSqlProductionCoordinationStore>());
                services.AddSingleton<IProductionRunExecutionPlanRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<PostgreSqlProductionCoordinationStore>());
                services.AddSingleton<IResourceLeaseRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<PostgreSqlProductionCoordinationStore>());
                services.AddSingleton<IStationJobCoordinationStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<PostgreSqlProductionCoordinationStore>());
                services.AddSingleton<IProductionMaterialRepository>(_ =>
                    new PostgreSqlProductionMaterialRepository(
                        productionPostgreSqlConnectionString));
                services.AddSingleton<IStationEmergencyStopRepository>(_ =>
                    new PostgreSqlStationEmergencyStopRepository(
                        productionPostgreSqlConnectionString));
                services.AddSingleton<IProductionMaterialArrivalInbox>(_ =>
                    new PostgreSqlProductionMaterialArrivalInbox(
                        productionPostgreSqlConnectionString));
                break;
            case ProductionCoordinationPersistenceProvider.Sqlite:
                var productionSqliteConnectionString = coordinationOptions.ResolveSqliteConnectionString();
                services.AddSingleton(_ =>
                    new SqliteProductionRunRepository(productionSqliteConnectionString));
                services.AddSingleton<IProductionRunRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<SqliteProductionRunRepository>());
                services.AddSingleton<IProductionRunExecutionPlanRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<SqliteProductionRunRepository>());
                services.AddSingleton<IResourceLeaseRepository>(_ =>
                    new SqliteResourceLeaseRepository(productionSqliteConnectionString));
                services.AddSingleton<InMemoryStationJobCoordinationStore>();
                services.AddSingleton<IStationJobCoordinationStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryStationJobCoordinationStore>());
                services.AddSingleton<IProductionMaterialRepository>(_ =>
                    new SqliteProductionMaterialRepository(productionSqliteConnectionString));
                services.AddSingleton<IStationEmergencyStopRepository>(_ =>
                    new SqliteStationEmergencyStopRepository(productionSqliteConnectionString));
                services.AddSingleton<IProductionMaterialArrivalInbox>(_ =>
                    new SqliteProductionMaterialArrivalInbox(productionSqliteConnectionString));
                break;
            case ProductionCoordinationPersistenceProvider.InMemory:
                services.AddSingleton<InMemoryProductionMaterialRepository>();
                services.AddSingleton<IProductionMaterialRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryProductionMaterialRepository>());
                services.AddSingleton<InMemoryProductionRunRepository>();
                services.AddSingleton<IProductionRunRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryProductionRunRepository>());
                services.AddSingleton<IProductionRunExecutionPlanRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryProductionRunRepository>());
                services.AddSingleton<IResourceLeaseRepository, InMemoryResourceLeaseRepository>();
                services.AddSingleton<InMemoryStationJobCoordinationStore>();
                services.AddSingleton<IStationJobCoordinationStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryStationJobCoordinationStore>());
                services.AddSingleton<IStationEmergencyStopRepository,
                    InMemoryStationEmergencyStopRepository>();
                services.AddSingleton<IProductionMaterialArrivalInbox,
                    InMemoryProductionMaterialArrivalInbox>();
                break;
        }

        services.AddScoped<ProductionMaterialService>();
        services.TryAddScoped<IProductionMaterialArrivalAuthorizer,
            RejectingProductionMaterialArrivalAuthorizer>();
        services.AddScoped<ProductionMaterialArrivalIngress>();
        services.TryAddSingleton<IStationEmergencyStopOperatorAuthorizer,
            ExplicitStationEmergencyStopOperatorAuthorizer>();
        services.AddScoped<IStationEmergencyStopProductionRunLinker,
            StationEmergencyStopProductionRunLinker>();
        services.AddScoped<StationEmergencyStopService>();
        services.AddSingleton<IRuntimeCommandResourceFenceValidator, RuntimeCommandResourceFenceValidator>();
        services.AddScoped<IProductionOperationReadiness, ProductionOperationReadinessEvaluator>();
        services.AddScoped<IProductionLineRuntimeStateReader, ProductionLineRuntimeStateReader>();

        services.TryAddSingleton<IStationDeploymentResolver, FileSystemStationDeploymentResolver>();
        services.AddScoped<StationDispatchPublicationAuthorizer>();
        services.AddScoped<IProductionRunRecoveryService, ProductionRunRecoveryService>();
        services.AddScoped<StationJobRecoveryRequiredIngress>();
        services.AddHostedService<ProductionRunStartupRecoveryHostedService>();
        var transportProvider = StationCoordinatorTransportProviders.Parse(transportOptions.Provider);
        switch (transportProvider)
        {
            case StationCoordinatorTransportProvider.RabbitMq:
                _ = transportOptions.ResolveBrokerUri();
                services.AddSingleton<RabbitMqStationCoordinatorTransport>();
                services.AddSingleton<IStationJobOutboxPublisher>(serviceProvider =>
                    serviceProvider.GetRequiredService<RabbitMqStationCoordinatorTransport>());
                services.TryAddSingleton<IStationJobGateway, DurableStationJobGateway>();
                services.TryAddSingleton<RabbitMqStationSafetyGateway>();
                services.TryAddSingleton<IStationEmergencyStopGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<RabbitMqStationSafetyGateway>());
                services.TryAddSingleton<IStationSafetyGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<RabbitMqStationSafetyGateway>());
                services.TryAddSingleton<IStationJobCancellationGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<RabbitMqStationSafetyGateway>());
                services.AddHostedService<StationJobOutboxHostedService>();
                services.AddHostedService<StationResultInboxHostedService>();
                break;
            case StationCoordinatorTransportProvider.Disabled:
                if (coordinationProvider == ProductionCoordinationPersistenceProvider.PostgreSql)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL Production coordination requires RabbitMq Station transport.");
                }

                services.TryAddSingleton<IStationJobGateway, DisabledStationJobGateway>();
                services.TryAddSingleton<DisabledStationSafetyGateway>();
                services.TryAddSingleton<IStationEmergencyStopGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<DisabledStationSafetyGateway>());
                services.TryAddSingleton<IStationSafetyGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<DisabledStationSafetyGateway>());
                services.TryAddSingleton<IStationJobCancellationGateway>(serviceProvider =>
                    serviceProvider.GetRequiredService<DisabledStationSafetyGateway>());
                break;
        }

        var stationExecutionProvider = StationExecutionProviders.Parse(
            stationExecutionOptions.Provider);
        if (stationExecutionProvider == StationExecutionProvider.Agent
            && transportProvider != StationCoordinatorTransportProvider.RabbitMq)
        {
            throw new InvalidOperationException(
                "Agent Station execution requires RabbitMq Station transport.");
        }

        if (stationExecutionProvider == StationExecutionProvider.InProcess
            && (coordinationProvider == ProductionCoordinationPersistenceProvider.PostgreSql
                || transportProvider != StationCoordinatorTransportProvider.Disabled))
        {
            throw new InvalidOperationException(
                "InProcess Station execution is allowed only with explicit local/test coordination "
                + "and Disabled Agent transport.");
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
        // The in-process dispatcher is available only for explicit test composition.
        // Production composition must provide an Agent gateway and deployment resolver.
        services.AddScoped<InProcessStationOperationDispatcher>();
        services.AddScoped<AgentStationOperationDispatcher>();
        services.AddScoped<InProcessStationOperationCanceler>();
        services.AddScoped<AgentStationOperationCanceler>();
        services.AddSingleton<InProcessStationOperationRegistry>();
        services.AddScoped<AgentStationSafetyController>();
        services.AddScoped<InProcessStationSafetyController>();
        if (stationExecutionProvider == StationExecutionProvider.Agent)
        {
            services.TryAddScoped<IStationOperationDispatcher>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentStationOperationDispatcher>());
            services.TryAddScoped<IStationOperationCanceler>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentStationOperationCanceler>());
            services.TryAddScoped<IStationSafetyController>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentStationSafetyController>());
        }
        else
        {
            services.TryAddScoped<IStationOperationDispatcher>(serviceProvider =>
                serviceProvider.GetRequiredService<InProcessStationOperationDispatcher>());
            services.TryAddScoped<IStationOperationCanceler>(serviceProvider =>
                serviceProvider.GetRequiredService<InProcessStationOperationCanceler>());
            services.TryAddScoped<IStationSafetyController>(serviceProvider =>
                serviceProvider.GetRequiredService<InProcessStationSafetyController>());
        }
        services.AddScoped<IProductionRunRunner, ProductionRunRunner>();
        services.AddScoped<IProductionRunCoordinator, ProductionRunCoordinator>();
        services.AddScoped<IRuntimeSessionRecoveryService, RuntimeSessionRecoveryService>();
        services.AddHostedService<ProductionRunCoordinatorHostedService>();

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

    private static ProductionCoordinationPersistenceOptions LoadCoordinationPersistenceOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(ProductionCoordinationPersistenceOptions.SectionName);
        return new ProductionCoordinationPersistenceOptions
        {
            Provider = section?["Provider"]
                ?? ProductionCoordinationPersistenceProviders.PostgreSql,
            ConnectionString = section?["ConnectionString"],
            SqliteDatabasePath = section?["SqliteDatabasePath"]
        };
    }

    private static StationCoordinatorTransportOptions LoadStationCoordinatorTransportOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(StationCoordinatorTransportOptions.SectionName);
        return section?.Get<StationCoordinatorTransportOptions>()
               ?? new StationCoordinatorTransportOptions();
    }

    private static StationExecutionOptions LoadStationExecutionOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(StationExecutionOptions.SectionName);
        return new StationExecutionOptions
        {
            Provider = section?["Provider"] ?? StationExecutionProviders.Agent
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
