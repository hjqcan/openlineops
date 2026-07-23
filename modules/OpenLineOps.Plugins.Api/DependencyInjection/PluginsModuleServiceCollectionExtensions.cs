using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Compatibility;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Application.Trials;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;
using OpenLineOps.Plugins.Infrastructure.Trials;

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

        services.AddSingleton<IPluginPackageCatalog, FileSystemPluginPackageCatalog>();
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
            new ExternalProcessPluginInstanceActivator(
                serviceProvider.GetRequiredService<IExternalPluginProcessRunner>(),
                serviceProvider.GetRequiredService<IExternalPluginProcessRegistry>(),
                serviceProvider.GetRequiredService<ExternalProcessPluginHostOptions>(),
                serviceProvider.GetRequiredService<IExternalPluginProcessEventSink>()));
        services.AddSingleton<IPluginLifecycleManager, PluginLifecycleManager>();
        services.AddSingleton<IPluginProviderTrialRunner, ExternalProcessPluginProviderTrialRunner>();
        services.TryAddSingleton<IPluginDeviceCommandInvoker, ExternalProcessPluginDeviceCommandInvoker>();
        services.TryAddSingleton<IPluginProcessCommandInvoker, ExternalProcessPluginProcessCommandInvoker>();
        return services;
    }

    private static PluginsModuleOptions LoadOptions(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(PluginsModuleOptions.SectionName);
        var options = new PluginsModuleOptions
        {
            EventLogProvider = section?["EventLog:Provider"] ?? PluginEventLogProviders.Sqlite,
            EventLogConnectionString = section?["EventLog:ConnectionString"],
            EventLogDatabasePath = section?["EventLog:DatabasePath"] ?? "data/openlineops-plugin-events.sqlite",
            PlatformVersion = section?["Compatibility:PlatformVersion"] ?? "1.0.0",
            ContractVersion = section?["Compatibility:ContractVersion"] ?? "1.0.0",
            RegisterRoutingInventories = ReadOptionalBoolean(
                section?["RegisterRoutingInventories"],
                defaultValue: false,
                $"{PluginsModuleOptions.SectionName}:RegisterRoutingInventories"),
            ExternalHostExecutablePath = section?["ExternalHost:ExecutablePath"],
            ExternalHostArgumentsTemplate = section?["ExternalHost:ArgumentsTemplate"]
        };

        _ = PluginEventLogProviders.Parse(options.EventLogProvider);
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

    private static ExternalProcessPluginHostOptions LoadHostOptions(
        IConfiguration? configuration,
        PluginsModuleOptions options)
    {
        var section = configuration?.GetSection($"{PluginsModuleOptions.SectionName}:ExternalHost");
        var hostOptions = new ExternalProcessPluginHostOptions
        {
            ExecutablePath = options.ExternalHostExecutablePath ?? "OpenLineOps.PluginHost.exe",
            ArgumentsTemplate = options.ExternalHostArgumentsTemplate
                ?? "--openlineops-plugin-host --manifest \"{ManifestPath}\" --entry \"{EntryAssemblyPath}\" --type \"{EntryType}\""
        };
        var sandboxSection = section?.GetSection("Sandbox");
        var isolationMode = sandboxSection?["IsolationMode"]
            ?? ExternalPluginIsolationModes.ExternalProcess;
        _ = ExternalPluginIsolationModes.Parse(isolationMode);
        hostOptions.Sandbox.IsolationMode = isolationMode;

        var startupProbeDelayValue = section?["StartupProbeDelay"];
        if (startupProbeDelayValue is not null)
        {
            hostOptions.StartupProbeDelay = ReadTimeSpan(
                startupProbeDelayValue,
                $"{PluginsModuleOptions.SectionName}:ExternalHost:StartupProbeDelay");
        }

        var shutdownTimeoutValue = section?["ShutdownTimeout"];
        if (shutdownTimeoutValue is not null)
        {
            hostOptions.ShutdownTimeout = ReadTimeSpan(
                shutdownTimeoutValue,
                $"{PluginsModuleOptions.SectionName}:ExternalHost:ShutdownTimeout");
        }

        return hostOptions;
    }

    private static void AddEventLog(
        IServiceCollection services,
        PluginsModuleOptions options)
    {
        switch (PluginEventLogProviders.Parse(options.EventLogProvider))
        {
            case PluginEventLogProvider.Sqlite:
                services.AddSingleton(_ =>
                    new SqliteExternalPluginProcessEventLog(options.ResolveEventLogConnectionString()));
                services.AddSingleton<IExternalPluginProcessEventLog>(serviceProvider =>
                    serviceProvider.GetRequiredService<SqliteExternalPluginProcessEventLog>());
                services.AddSingleton<IExternalPluginProcessEventSink>(serviceProvider =>
                    serviceProvider.GetRequiredService<SqliteExternalPluginProcessEventLog>());
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported plugin event-log provider '{options.EventLogProvider}'.");
        }
    }

    private static TimeSpan ReadTimeSpan(string value, string configurationPath)
    {
        return TimeSpan.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration '{configurationPath}' must be a valid TimeSpan.");
    }
}
