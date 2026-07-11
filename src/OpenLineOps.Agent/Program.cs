using Microsoft.Data.Sqlite;
using OpenLineOps.Agent;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ProcessIsolation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpenLineOps Station Agent";
});

var options = StationAgentHostOptions.Load(builder.Configuration);
Directory.CreateDirectory(options.DataDirectory);
var sqliteBuilder = new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(options.DataDirectory, "station-agent.sqlite"),
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared
};

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IStationJobStore>(_ =>
    new SqliteStationJobStore(sqliteBuilder.ToString()));
builder.Services.AddSingleton<IStationSafetyInboxStore>(_ =>
    new SqliteStationSafetyInboxStore(sqliteBuilder.ToString()));
builder.Services.AddSingleton<IStationResourceFenceValidator>(serviceProvider =>
    new SqliteStationResourceFenceValidator(
        sqliteBuilder.ToString(),
        serviceProvider.GetRequiredService<IClock>()));
builder.Services.AddSingleton(_ => new SignedStationPackageInstaller(
    new StationPackageTrustOptions(
        options.PackageCacheDirectory,
        options.TrustedPackagePublicKeys,
        ImmutableReaderSid: WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName))));
builder.Services.AddSingleton(serviceProvider => new ProcessStationRuntimeHost(
    new ProcessStationRuntimeHostOptions(
        options.RuntimeExecutablePath,
        options.RuntimeWorkingDirectory,
        options.ArtifactDirectory,
        options.RuntimeTimeout,
        options.MaximumRuntimeOutputBytes,
        RequireRestrictedExternalProgramHostIdentity: true,
        AllowedRestrictedExternalProgramHostAccounts:
            options.AllowedRestrictedExternalProgramHostAccounts,
        AllowedRestrictedExternalProgramHostSids:
            options.AllowedRestrictedExternalProgramHostSids,
        RequireExternalProgramAppContainerIsolation: true,
        ExternalProgramAppContainerProfileNamespace:
            options.ExternalProgramAppContainerProfileNamespace,
        RequireImmutableExternalProgramContent: true),
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
        PrefetchCount: options.PrefetchCount,
        MaximumConcurrentJobs: options.MaximumConcurrentJobs,
        RequireTls: options.RequireBrokerTls)));
builder.Services.AddSingleton<IStationJobReceiver>(provider =>
    provider.GetRequiredService<RabbitMqStationTransport>());
builder.Services.AddSingleton<IStationAgentMessagePublisher>(provider =>
    provider.GetRequiredService<RabbitMqStationTransport>());
builder.Services.AddSingleton<IStationArtifactTransfer>(_ =>
    new FileSystemStationArtifactTransfer(
        new FileSystemStationArtifactTransferOptions(
            options.ArtifactDirectory,
            options.ArtifactExchangeDirectory)));
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
builder.Services.AddSingleton<StationSafetyCommandCoordinator>();
builder.Services.AddSingleton<StationJobOutboxDispatcher>();
builder.Services.AddHostedService<StationAgentWorker>();

await builder.Build().RunAsync();
