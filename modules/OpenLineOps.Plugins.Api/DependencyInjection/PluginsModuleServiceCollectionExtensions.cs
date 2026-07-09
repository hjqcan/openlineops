using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Plugins.Api.Management;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Compatibility;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Api.DependencyInjection;

public static class PluginsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsPluginsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var options = LoadOptions(configuration);
        services.AddSingleton(options);

        var hostOptions = LoadHostOptions(configuration, options);
        services.AddSingleton(hostOptions);

        services.AddSingleton<IPluginPackageCatalog>(_ =>
            new FileSystemPluginPackageCatalog(
                options.ResolvePackageRoot(),
                options.ManifestFileNames));
        services.AddSingleton<IPluginManifestValidator>(_ =>
            new PluginManifestValidator(new PluginCompatibilityOptions(
                options.PlatformVersion,
                options.ContractVersion)));
        services.AddSingleton<PluginCapabilityInventory>();
        services.AddSingleton<PluginDeviceCommandInventory>();
        services.AddSingleton<PluginProcessCommandInventory>();
        if (options.RegisterRoutingInventories)
        {
            services.AddSingleton<IPluginCapabilityInventory>(serviceProvider =>
                serviceProvider.GetRequiredService<PluginCapabilityInventory>());
            services.AddSingleton<IPluginDeviceCommandInventory>(serviceProvider =>
                serviceProvider.GetRequiredService<PluginDeviceCommandInventory>());
            services.AddSingleton<IPluginProcessCommandInventory>(serviceProvider =>
                serviceProvider.GetRequiredService<PluginProcessCommandInventory>());
        }

        services.AddSingleton<IExternalPluginProcessRegistry, ExternalPluginProcessRegistry>();
        AddEventLog(services, options);
        services.AddSingleton<IExternalPluginProcessRunner>(serviceProvider =>
            new SystemDiagnosticsExternalPluginProcessRunner(
                serviceProvider.GetRequiredService<ExternalProcessPluginHostOptions>(),
                serviceProvider.GetRequiredService<IExternalPluginProcessEventSink>()));
        services.AddSingleton<IPluginInstanceActivator>(serviceProvider =>
            CreateActivator(options, serviceProvider));
        services.AddSingleton<IPluginLifecycleManager, PluginLifecycleManager>();
        services.TryAddSingleton<IPluginDeviceCommandInvoker, ExternalProcessPluginDeviceCommandInvoker>();
        services.TryAddSingleton<IPluginProcessCommandInvoker, ExternalProcessPluginProcessCommandInvoker>();
        services.AddSingleton<IPluginManagementService, PluginManagementService>();

        return services;
    }

    private static PluginsModuleOptions LoadOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(PluginsModuleOptions.SectionName);
        var options = new PluginsModuleOptions
        {
            PackageRoot = section?["PackageRoot"] ?? string.Empty,
            Activator = section?["Activator"] ?? PluginActivators.ManifestOnly,
            EventLogProvider = section?["EventLog:Provider"] ?? PluginEventLogProviders.Sqlite,
            EventLogConnectionString = section?["EventLog:ConnectionString"],
            EventLogDatabasePath = section?["EventLog:DatabasePath"] ?? "data/openlineops-plugin-events.sqlite",
            PlatformVersion = section?["Compatibility:PlatformVersion"] ?? "1.0.0",
            ContractVersion = section?["Compatibility:ContractVersion"] ?? "1.0.0",
            RegisterRoutingInventories = TryReadBoolean(section?["RegisterRoutingInventories"], defaultValue: false),
            ExternalHostExecutablePath = section?["ExternalHost:ExecutablePath"],
            ExternalHostArgumentsTemplate = section?["ExternalHost:ArgumentsTemplate"]
        };

        var manifestFileNames = section?.GetSection("ManifestFileNames").Get<string[]>();
        if (manifestFileNames is { Length: > 0 })
        {
            options.ManifestFileNames = manifestFileNames;
        }

        return options;
    }

    private static bool TryReadBoolean(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static ExternalProcessPluginHostOptions LoadHostOptions(
        IConfiguration? configuration,
        PluginsModuleOptions options)
    {
        var section = configuration?.GetSection($"{PluginsModuleOptions.SectionName}:ExternalHost");
        var hostOptions = new ExternalProcessPluginHostOptions
        {
            ExecutablePath = options.ExternalHostExecutablePath ?? "dotnet",
            ArgumentsTemplate = options.ExternalHostArgumentsTemplate
                ?? "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\""
        };

        if (TimeSpan.TryParse(section?["StartupProbeDelay"], out var startupProbeDelay))
        {
            hostOptions.StartupProbeDelay = startupProbeDelay;
        }

        if (TimeSpan.TryParse(section?["ShutdownTimeout"], out var shutdownTimeout))
        {
            hostOptions.ShutdownTimeout = shutdownTimeout;
        }

        return hostOptions;
    }

    private static void AddEventLog(
        IServiceCollection services,
        PluginsModuleOptions options)
    {
        if (string.Equals(options.EventLogProvider, PluginEventLogProviders.None, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<NoOpExternalPluginProcessEventLog>();
            services.AddSingleton<IExternalPluginProcessEventLog>(serviceProvider =>
                serviceProvider.GetRequiredService<NoOpExternalPluginProcessEventLog>());
            services.AddSingleton<IExternalPluginProcessEventSink>(serviceProvider =>
                serviceProvider.GetRequiredService<NoOpExternalPluginProcessEventLog>());

            return;
        }

        services.AddSingleton(_ =>
            new SqliteExternalPluginProcessEventLog(options.ResolveEventLogConnectionString()));
        services.AddSingleton<IExternalPluginProcessEventLog>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteExternalPluginProcessEventLog>());
        services.AddSingleton<IExternalPluginProcessEventSink>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteExternalPluginProcessEventLog>());
    }

    private static IPluginInstanceActivator CreateActivator(
        PluginsModuleOptions options,
        IServiceProvider serviceProvider)
    {
        if (string.Equals(options.Activator, PluginActivators.AssemblyLoadContext, StringComparison.OrdinalIgnoreCase))
        {
            return new AssemblyLoadContextPluginInstanceActivator();
        }

        if (string.Equals(options.Activator, PluginActivators.ExternalProcess, StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalProcessPluginInstanceActivator(
                serviceProvider.GetRequiredService<IExternalPluginProcessRunner>(),
                serviceProvider.GetRequiredService<IExternalPluginProcessRegistry>(),
                serviceProvider.GetRequiredService<ExternalProcessPluginHostOptions>(),
                serviceProvider.GetRequiredService<IExternalPluginProcessEventSink>());
        }

        return new ManifestOnlyPluginInstanceActivator();
    }

    private sealed class NoOpExternalPluginProcessEventLog : IExternalPluginProcessEventLog
    {
        public void Record(ExternalPluginProcessEvent processEvent)
        {
        }

        public ValueTask<IReadOnlyList<ExternalPluginProcessEvent>> ListAsync(
            ExternalPluginProcessEventQuery? query = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ExternalPluginProcessEvent>>([]);
        }
    }
}
