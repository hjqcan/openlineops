using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Api.Models;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Api.Management;

public sealed class PluginManagementService : IPluginManagementService
{
    private readonly IPluginPackageCatalog _packageCatalog;
    private readonly IPluginManifestValidator _manifestValidator;
    private readonly PluginCapabilityInventory _capabilityInventory;
    private readonly PluginDeviceCommandInventory _deviceCommandInventory;
    private readonly PluginProcessCommandInventory _processCommandInventory;
    private readonly IPluginLifecycleManager _lifecycleManager;
    private readonly IExternalPluginProcessEventLog _eventLog;
    private readonly IServiceProvider _services;

    public PluginManagementService(
        IPluginPackageCatalog packageCatalog,
        IPluginManifestValidator manifestValidator,
        PluginCapabilityInventory capabilityInventory,
        PluginDeviceCommandInventory deviceCommandInventory,
        PluginProcessCommandInventory processCommandInventory,
        IPluginLifecycleManager lifecycleManager,
        IExternalPluginProcessEventLog eventLog,
        IServiceProvider services)
    {
        _packageCatalog = packageCatalog;
        _manifestValidator = manifestValidator;
        _capabilityInventory = capabilityInventory;
        _deviceCommandInventory = deviceCommandInventory;
        _processCommandInventory = processCommandInventory;
        _lifecycleManager = lifecycleManager;
        _eventLog = eventLog;
        _services = services;
    }

    public async Task<PluginManagementOverviewResponse> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var packagesTask = _packageCatalog.DiscoverAsync(cancellationToken).AsTask();
        var capabilitiesTask = _capabilityInventory.ListCapabilitiesAsync(cancellationToken).AsTask();
        var deviceCommandsTask = _deviceCommandInventory.ListDeviceCommandsAsync(cancellationToken).AsTask();
        var processCommandsTask = _processCommandInventory.ListProcessCommandsAsync(cancellationToken).AsTask();
        var eventsTask = _eventLog.ListAsync(
            new ExternalPluginProcessEventQuery(Take: 20),
            cancellationToken).AsTask();

        await Task
            .WhenAll(packagesTask, capabilitiesTask, deviceCommandsTask, processCommandsTask, eventsTask)
            .ConfigureAwait(false);

        return new PluginManagementOverviewResponse(
            packagesTask.Result.Select(ToResponse).ToArray(),
            capabilitiesTask.Result.Select(ToResponse).ToArray(),
            deviceCommandsTask.Result.Select(ToResponse).ToArray(),
            processCommandsTask.Result.Select(ToResponse).ToArray(),
            eventsTask.Result.Select(ToResponse).ToArray());
    }

    public async Task<IReadOnlyCollection<PluginLifecycleRecordResponse>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _lifecycleManager
            .StartAsync(_services, cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToResponse).ToArray();
    }

    public async Task<IReadOnlyCollection<PluginLifecycleRecordResponse>> StopAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _lifecycleManager
            .StopAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToResponse).ToArray();
    }

    public async Task<IReadOnlyCollection<ExternalPluginProcessEventResponse>> ListEventsAsync(
        string? pluginId,
        ExternalPluginProcessEventKind? kind,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = new ExternalPluginProcessEventQuery(
            RequireCanonicalOptionalPluginId(pluginId),
            kind,
            Skip: skip,
            Take: take);
        var events = await _eventLog
            .ListAsync(query, cancellationToken)
            .ConfigureAwait(false);

        return events.Select(ToResponse).ToArray();
    }

    private static string? RequireCanonicalOptionalPluginId(string? pluginId)
    {
        if (pluginId is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(pluginId)
            || !string.Equals(pluginId, pluginId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Plugin id must be non-empty and must not contain surrounding whitespace.",
                nameof(pluginId));
        }

        return pluginId;
    }

    private PluginPackageResponse ToResponse(PluginPackageDescriptor package)
    {
        var validationReport = _manifestValidator.Validate(package.Manifest);

        return new PluginPackageResponse(
            ToResponse(package.Manifest),
            package.PackagePath,
            package.ManifestPath,
            validationReport.IsValid,
            validationReport.Issues.Select(ToResponse).ToArray());
    }

    private static PluginManifestResponse ToResponse(PluginManifest manifest)
    {
        return new PluginManifestResponse(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Kind.ToString(),
            manifest.EntryAssembly,
            manifest.EntryType,
            manifest.ContractVersion,
            manifest.MinimumPlatformVersion,
            manifest.Capabilities?.ToArray() ?? [],
            manifest.DeviceCommands?.Select(ToResponse).ToArray() ?? [],
            manifest.ProcessCommands?.Select(ToResponse).ToArray() ?? []);
    }

    private static PluginCommandDefinitionResponse ToResponse(PluginDeviceCommandDefinition command)
    {
        return new PluginCommandDefinitionResponse(
            command.Id,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.TimeoutMilliseconds,
            command.MaxRetries);
    }

    private static PluginCommandDefinitionResponse ToResponse(PluginProcessCommandDefinition command)
    {
        return new PluginCommandDefinitionResponse(
            command.Id,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.TimeoutMilliseconds,
            command.MaxRetries);
    }

    private static PluginValidationIssueResponse ToResponse(PluginValidationIssue issue)
    {
        return new PluginValidationIssueResponse(issue.Code, issue.Message);
    }

    private static PluginCapabilityResponse ToResponse(PluginCapabilityDescriptor capability)
    {
        return new PluginCapabilityResponse(
            capability.PluginId,
            capability.PluginName,
            capability.PluginKind.ToString(),
            capability.Capability);
    }

    private static PluginCommandResponse ToResponse(PluginDeviceCommandDescriptor command)
    {
        return new PluginCommandResponse(
            command.PluginId,
            command.PluginName,
            command.PluginKind.ToString(),
            command.CommandDefinitionId,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.TimeoutMilliseconds,
            command.MaxRetries);
    }

    private static PluginCommandResponse ToResponse(PluginProcessCommandDescriptor command)
    {
        return new PluginCommandResponse(
            command.PluginId,
            command.PluginName,
            command.PluginKind.ToString(),
            command.CommandDefinitionId,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.TimeoutMilliseconds,
            command.MaxRetries);
    }

    private static PluginLifecycleRecordResponse ToResponse(PluginLifecycleRecord record)
    {
        return new PluginLifecycleRecordResponse(
            ToResponse(record.Manifest),
            record.State.ToString(),
            record.InitializationStatus.ToString(),
            record.ValidationIssues.Select(ToResponse).ToArray(),
            record.FailureReason);
    }

    private static ExternalPluginProcessEventResponse ToResponse(ExternalPluginProcessEvent processEvent)
    {
        return new ExternalPluginProcessEventResponse(
            processEvent.Kind.ToString(),
            processEvent.PluginId,
            processEvent.Message,
            processEvent.OccurredAtUtc,
            processEvent.Detail);
    }

}
