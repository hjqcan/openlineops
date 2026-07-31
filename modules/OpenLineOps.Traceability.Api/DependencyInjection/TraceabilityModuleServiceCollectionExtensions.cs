using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Artifacts;
using OpenLineOps.Traceability.Application.MaterialLifecycle;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.ReadModels;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Infrastructure.Artifacts;
using OpenLineOps.Traceability.Infrastructure.Persistence;
using OpenLineOps.Traceability.Infrastructure.Time;

namespace OpenLineOps.Traceability.Api.DependencyInjection;

public static class TraceabilityModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsTraceabilityModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        var persistenceOptions = LoadPersistenceOptions(configuration);
        services.AddSingleton(persistenceOptions);
        var artifactStorageOptions = LoadArtifactStorageOptions(configuration);
        services.AddSingleton(artifactStorageOptions);
        var artifactUploadOptions = LoadArtifactUploadOptions(configuration);
        services.AddSingleton(artifactUploadOptions);
        var projectionRebuildOptions = LoadProjectionRebuildOptions(configuration);
        services.AddSingleton(projectionRebuildOptions);

        switch (TraceRecordPersistenceProviders.Parse(persistenceOptions.Provider))
        {
            case TraceRecordPersistenceProvider.Sqlite:
                services.AddSingleton<ITraceRecordRepository>(_ =>
                    new SqliteTraceRecordRepository(persistenceOptions.ResolveSqliteConnectionString()));
                break;
            case TraceRecordPersistenceProvider.InMemory:
                services.AddSingleton<InMemoryTraceRecordRepository>();
                services.AddSingleton<ITraceRecordRepository>(serviceProvider =>
                    serviceProvider.GetRequiredService<InMemoryTraceRecordRepository>());
                break;
        }

        switch (TraceArtifactStorageProviders.Parse(artifactStorageOptions.Provider))
        {
            case TraceArtifactStorageProvider.FileSystem:
                services.AddSingleton<ITraceArtifactStorage>(_ =>
                    new FileSystemTraceArtifactStorage(
                        artifactStorageOptions.ResolveRootPath(),
                        artifactUploadOptions.MaximumArtifactSizeBytes));
                break;
        }

        services.AddSingleton<ITraceRecordService, TraceRecordService>();
        services.AddSingleton<ITraceReadModelService, TraceReadModelService>();
        services.AddSingleton<IProductionUnitMaterialLifecycleReader,
            ProductionUnitMaterialLifecycleReader>();
        services.AddSingleton<IStationArtifactReceiptService>(serviceProvider =>
            new StationArtifactReceiptService(
                serviceProvider.GetRequiredService<ITraceArtifactStorage>(),
                artifactUploadOptions.MaximumArtifactSizeBytes));
        services.AddSingleton<IStationArtifactUploadAuthorizer,
            StationArtifactUploadAuthorizer>();
        services.AddSingleton<IStationArtifactReceiptVerifier,
            TraceStationArtifactReceiptVerifier>();
        services.AddSingleton<ProductionRunTraceDomainEventSubscriber>();
        services.AddSingleton<IProductionRunTerminalOutboxHandler>(serviceProvider =>
            serviceProvider.GetRequiredService<ProductionRunTraceDomainEventSubscriber>());
        services.AddSingleton<ITraceProjectionRebuilder, TraceProjectionRebuilder>();
        services.AddHostedService<TraceProjectionRebuildHostedService>();
        services.AddHostedService<ProductionRunTerminalOutboxHostedService>();

        return services;
    }

    private static TraceRecordPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(TraceRecordPersistenceOptions.SectionName);

        return new TraceRecordPersistenceOptions
        {
            Provider = section?["Provider"] ?? TraceRecordPersistenceProviders.Sqlite,
            ConnectionString = section?["ConnectionString"],
            DatabasePath = section?["DatabasePath"] ?? "data/openlineops-traceability.sqlite"
        };
    }

    private static TraceArtifactStorageOptions LoadArtifactStorageOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(TraceArtifactStorageOptions.SectionName);

        return new TraceArtifactStorageOptions
        {
            Provider = section?["Provider"] ?? TraceArtifactStorageProviders.FileSystem,
            RootPath = section?["RootPath"] ?? "data/openlineops-traceability-artifacts"
        };
    }

    private static TraceProjectionRebuildOptions LoadProjectionRebuildOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(TraceProjectionRebuildOptions.SectionName);
        var enabledText = section?["Enabled"];
        if (enabledText is not null && !bool.TryParse(enabledText, out _))
        {
            throw new InvalidOperationException(
                "Trace projection rebuild Enabled must be a Boolean value.");
        }

        var pageSizeText = section?["PageSize"];
        if (pageSizeText is not null
            && !int.TryParse(
                pageSizeText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            throw new InvalidOperationException(
                "Trace projection rebuild PageSize must be a base-10 integer.");
        }

        return new TraceProjectionRebuildOptions(
            enabledText is not null && bool.Parse(enabledText),
            pageSizeText is null
                ? TraceProjectionRebuildOptions.DefaultPageSize
                : int.Parse(pageSizeText, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static StationArtifactUploadOptions LoadArtifactUploadOptions(
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(StationArtifactUploadOptions.SectionName);
        var maximumText = section?["MaximumArtifactSizeBytes"];
        var maximum = maximumText is null
            ? StationArtifactUploadOptions.DefaultMaximumArtifactSizeBytes
            : long.TryParse(
                maximumText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    "Station artifact upload maximum size must be a base-10 integer.");
        return new StationArtifactUploadOptions(maximum);
    }

}

public sealed class StationArtifactUploadOptions
{
    public const string SectionName = "OpenLineOps:Traceability:ArtifactUpload";
    public const long DefaultMaximumArtifactSizeBytes = 256L * 1024L * 1024L;

    public StationArtifactUploadOptions(long maximumArtifactSizeBytes)
    {
        if (maximumArtifactSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumArtifactSizeBytes),
                "Station artifact maximum size must be greater than zero.");
        }

        MaximumArtifactSizeBytes = maximumArtifactSizeBytes;
    }

    public long MaximumArtifactSizeBytes { get; }
}
