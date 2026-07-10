using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Artifacts;
using OpenLineOps.Traceability.Application.Judgements;
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
        var judgementOptions = LoadJudgementOptions(configuration);
        services.AddSingleton(judgementOptions);

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
                    new FileSystemTraceArtifactStorage(artifactStorageOptions.ResolveRootPath()));
                break;
        }

        services.AddSingleton<ITraceRecordService, TraceRecordService>();
        services.AddSingleton<ITraceJudgementGenerator, ConfiguredTraceJudgementGenerator>();
        services.AddSingleton<ITraceReadModelService, TraceReadModelService>();
        services.AddSingleton<ProductionRunTraceDomainEventSubscriber>();
        services.AddSingleton<IRuntimeDomainEventSubscriber>(serviceProvider =>
            serviceProvider.GetRequiredService<ProductionRunTraceDomainEventSubscriber>());
        services.AddSingleton<IProductionRunTerminalOutboxHandler>(serviceProvider =>
            serviceProvider.GetRequiredService<ProductionRunTraceDomainEventSubscriber>());
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

    private static TraceJudgementOptions LoadJudgementOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(TraceJudgementOptions.SectionName);

        return new TraceJudgementOptions
        {
            DefaultJudgement = section?["DefaultJudgement"] ?? "Passed",
            FailWhenAnyMeasurementFailed = ParseBoolean(section?["FailWhenAnyMeasurementFailed"], defaultValue: true),
            UnknownWhenAnyMeasurementIndeterminate = ParseBoolean(section?["UnknownWhenAnyMeasurementIndeterminate"], defaultValue: false),
            UnknownWhenNoMeasurements = ParseBoolean(section?["UnknownWhenNoMeasurements"], defaultValue: false)
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

    private static bool ParseBoolean(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }
}
