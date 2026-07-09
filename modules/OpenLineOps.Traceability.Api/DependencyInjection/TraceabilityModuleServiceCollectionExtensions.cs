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

        if (IsSqlite(persistenceOptions.Provider))
        {
            services.AddSingleton<ITraceRecordRepository>(_ =>
                new SqliteTraceRecordRepository(persistenceOptions.ResolveSqliteConnectionString()));
        }
        else if (IsPostgreSql(persistenceOptions.Provider))
        {
            services.AddSingleton<ITraceRecordRepository>(_ =>
                new PostgresTraceRecordRepository(persistenceOptions.ResolvePostgreSqlConnectionString()));
        }
        else if (IsInMemory(persistenceOptions.Provider))
        {
            services.AddSingleton<InMemoryTraceRecordRepository>();
            services.AddSingleton<ITraceRecordRepository>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryTraceRecordRepository>());
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported traceability persistence provider '{persistenceOptions.Provider}'.");
        }

        if (IsLocalFileArtifactStorage(artifactStorageOptions.Provider))
        {
            services.AddSingleton<ITraceArtifactStorage>(_ =>
                new LocalFileTraceArtifactStorage(artifactStorageOptions.ResolveRootPath()));
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported traceability artifact storage provider '{artifactStorageOptions.Provider}'.");
        }

        services.AddSingleton<ITraceRecordService, TraceRecordService>();
        services.AddSingleton<ITraceJudgementGenerator, ConfiguredTraceJudgementGenerator>();
        services.AddSingleton<ITraceReadModelService, TraceReadModelService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRuntimeDomainEventSubscriber, TraceRecordRuntimeDomainEventSubscriber>());

        return services;
    }

    private static TraceRecordPersistenceOptions LoadPersistenceOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(TraceRecordPersistenceOptions.SectionName);

        return new TraceRecordPersistenceOptions
        {
            Provider = section?["Provider"] ?? TraceRecordPersistenceProviders.InMemory,
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
            Provider = section?["Provider"] ?? TraceArtifactStorageProviders.LocalFile,
            RootPath = section?["RootPath"] ?? "data/openlineops-traceability-artifacts"
        };
    }

    private static bool IsSqlite(string provider)
    {
        return string.Equals(provider, TraceRecordPersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "SQLite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInMemory(string provider)
    {
        return string.Equals(provider, TraceRecordPersistenceProviders.InMemory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostgreSql(string provider)
    {
        return string.Equals(provider, TraceRecordPersistenceProviders.PostgreSql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalFileArtifactStorage(string provider)
    {
        return string.Equals(provider, TraceArtifactStorageProviders.LocalFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "FileSystem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "File", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParseBoolean(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }
}
