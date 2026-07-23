using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

const int hostFailureExitCode = 70;
string? windowsServiceEventLogSource = null;

try
{
    var commandLine = StationAgentCommandLine.Parse(args);
    var builder = Host.CreateApplicationBuilder(commandLine.ConfigurationArguments);
    if (commandLine.ProvisionContentCache)
    {
        StationAgentContentCacheProvisioningCommand.Execute(builder.Configuration);
        return 0;
    }
    if (commandLine.RemoveContentCachePackageSha256 is not null)
    {
        await StationAgentContentCacheProvisioningCommand.RemovePackageAsync(
            builder.Configuration,
            commandLine.RemoveContentCachePackageSha256);
        return 0;
    }

    var windowsServiceName = WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
        builder.Configuration["OpenLineOps:WindowsServiceName"],
        "OpenLineOps:WindowsServiceName");
    windowsServiceEventLogSource = windowsServiceName;

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = windowsServiceName;
    });

    var options = StationAgentHostOptions.Load(builder.Configuration);
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "OpenLineOps Station Agent requires a Windows LocalService token with a restricted service SID.");
    }

    var stationServiceSid = WindowsStationServiceIdentityReader.ReadRequired(
        WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(windowsServiceName))
        .ServiceSid;
    Directory.CreateDirectory(options.DataDirectory);
    var sqliteBuilder = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataDirectory, "station-agent.sqlite"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    };

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new StationAgentPresenceOptions(
        options.AgentId,
        options.StationId,
        options.StationSystemId,
        options.HeartbeatInterval));
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddSingleton<IStationJobStore>(_ =>
        new SqliteStationJobStore(sqliteBuilder.ToString()));
    builder.Services.AddSingleton<IStationSafetyInboxStore>(_ =>
        new SqliteStationSafetyInboxStore(sqliteBuilder.ToString()));
    builder.Services.AddSingleton(_ =>
        new SqliteStationMaterialArrivalOutboxStore(sqliteBuilder.ToString()));
    builder.Services.AddSingleton<IStationMaterialArrivalOutboxStore>(serviceProvider =>
        serviceProvider.GetRequiredService<SqliteStationMaterialArrivalOutboxStore>());
    builder.Services.AddSingleton(serviceProvider =>
        new SqliteStationResourceFenceValidator(
            sqliteBuilder.ToString(),
            serviceProvider.GetRequiredService<IClock>()));
    builder.Services.AddSingleton<IStationResourceFenceValidator>(serviceProvider =>
        serviceProvider.GetRequiredService<SqliteStationResourceFenceValidator>());
    builder.Services.AddSingleton<IStationResourceLeaseChangeInbox>(serviceProvider =>
        serviceProvider.GetRequiredService<SqliteStationResourceFenceValidator>());
    builder.Services.AddSingleton(_ => new SignedStationPackageInstaller(
        new StationPackageTrustOptions(
            options.PackageCacheDirectory,
            options.TrustedPackagePublicKeys,
            ImmutableReaderSid: WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
            ImmutableStationServiceSid: stationServiceSid)));
    builder.Services.AddSingleton<IStationMaterialArrivalDeploymentProvider>(serviceProvider =>
        new SignedStationMaterialArrivalDeploymentProvider(
            new SignedStationMaterialArrivalDeploymentOptions(
                options.AgentId,
                options.StationId,
                Path.Combine(
                    options.PackageDistributionDirectory,
                    $"{options.MaterialArrivalPackageContentSha256}.olopkg"),
                options.MaterialArrivalPackageContentSha256),
            serviceProvider.GetRequiredService<SignedStationPackageInstaller>()));
    builder.Services.AddSingleton<StationMaterialArrivalReporter>();
    builder.Services.AddSingleton(serviceProvider => new ProcessStationRuntimeHost(
        new ProcessStationRuntimeHostOptions(
            options.RuntimeExecutablePath,
            options.PluginHostExecutablePath,
            options.RuntimeWorkingDirectory,
            options.ArtifactDirectory,
            options.RuntimeTimeout,
            options.MaximumRuntimeOutputBytes,
            RequireRestrictedExternalProgramHostIdentity: true,
            RestrictedServiceSid: stationServiceSid,
            RequireExternalProgramAppContainerIsolation: true,
            ExternalProgramAppContainerProfileNamespace:
                options.ExternalProgramAppContainerProfileNamespace,
            RequireImmutableExternalProgramContent: true,
            PythonScript: options.PythonScript),
        serviceProvider.GetRequiredService<IStationResourceFenceValidator>(),
        clock: serviceProvider.GetRequiredService<IClock>()));
    builder.Services.AddSingleton<IStationRuntimeHost>(serviceProvider =>
        serviceProvider.GetRequiredService<ProcessStationRuntimeHost>());
    builder.Services.AddSingleton<IStationRuntimeIsolationCleaner>(serviceProvider =>
        serviceProvider.GetRequiredService<ProcessStationRuntimeHost>());
    builder.Services.AddSingleton<IStationSafetyActuator>(_ =>
        new ProcessStationSafetyActuator(
            new ProcessStationSafetyOptions(
                options.SafetyExecutablePath,
                options.SafetyWorkingDirectory,
                options.SafetyTimeout)));
    builder.Services.AddSingleton<IStationOperationExecutor, PackageStationOperationExecutor>(provider =>
        new PackageStationOperationExecutor(
            new PackageStationOperationExecutorOptions(options.PackageDistributionDirectory),
            provider.GetRequiredService<SignedStationPackageInstaller>(),
            provider.GetRequiredService<IStationRuntimeHost>()));
    builder.Services.AddSingleton(_ => new RabbitMqStationTransport(
        new RabbitMqStationTransportOptions(
            options.BrokerUri,
            options.AgentId,
            options.StationId,
            options.StationSystemId,
            PrefetchCount: options.PrefetchCount,
            MaximumConcurrentJobs: options.MaximumConcurrentJobs,
            RequireTls: options.RequireBrokerTls)));
    builder.Services.AddSingleton<IStationJobReceiver>(provider =>
        provider.GetRequiredService<RabbitMqStationTransport>());
    builder.Services.AddSingleton<IStationAgentMessagePublisher>(provider =>
        provider.GetRequiredService<RabbitMqStationTransport>());
    builder.Services.AddSingleton<StationMaterialArrivalOutboxDispatcher>();
    builder.Services.AddSingleton(_ =>
        StationMaterialArrivalLocalIpcOptions.ForStationServiceSid(stationServiceSid));
    builder.Services.AddSingleton<StationMaterialArrivalLocalIpcServer>();
    const string artifactUploadHttpClient = "OpenLineOps.StationArtifactUpload";
    builder.Services
        .AddHttpClient(artifactUploadHttpClient, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        });
    builder.Services.AddSingleton<IStationArtifactTransfer>(serviceProvider =>
        new HttpStationArtifactTransfer(
            new HttpStationArtifactTransferOptions(
                options.ArtifactDirectory,
                options.CoordinatorBaseUri,
                options.ArtifactUploadBearerToken,
                options.AgentId,
                options.StationId,
                options.ArtifactUploadTimeout),
            serviceProvider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(artifactUploadHttpClient)));
    builder.Services.AddSingleton(provider => new RabbitMqStationSafetyReceiver(
        new RabbitMqStationSafetyOptions(
            options.BrokerUri,
            options.AgentId,
            options.StationId,
            RequireTls: options.RequireBrokerTls),
        provider.GetRequiredService<StationSafetyCommandCoordinator>()));
    builder.Services.AddSingleton<IStationSafetyReceiver>(provider =>
        provider.GetRequiredService<RabbitMqStationSafetyReceiver>());
    builder.Services.AddSingleton<StationJobExecutionRegistry>();
    builder.Services.AddSingleton<StationJobCoordinator>();
    builder.Services.AddSingleton(serviceProvider => new StationResourceLeaseChangeCoordinator(
        options.AgentId,
        options.StationId,
        serviceProvider.GetRequiredService<IStationResourceLeaseChangeInbox>()));
    builder.Services.AddSingleton<StationSafetyCommandCoordinator>();
    builder.Services.AddSingleton<StationJobOutboxDispatcher>();
    builder.Services.AddSingleton<StationAgentShutdownState>();
    builder.Services.AddHostedService<StationAgentPresenceWorker>();
    builder.Services.AddHostedService<StationAgentWorker>();
    builder.Services.AddHostedService<StationMaterialArrivalWorker>();

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception)
{
    var failureMessage = $"OpenLineOps Station Agent terminated: {exception.Message}";
    await Console.Error.WriteLineAsync(failureMessage);
    if (OperatingSystem.IsWindows()
        && windowsServiceEventLogSource is not null)
    {
        try
        {
            EventLog.WriteEntry(
                windowsServiceEventLogSource,
                StationAgentStartupDiagnostics.CreateEventLogFailureMessage(exception),
                EventLogEntryType.Error);
        }
        catch (Exception diagnosticException)
        {
            await Console.Error.WriteLineAsync(
                $"OpenLineOps Station Agent could not write its startup failure to EventLog: {diagnosticException.Message}");
        }
    }

    return hostFailureExitCode;
}
