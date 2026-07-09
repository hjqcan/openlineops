using OpenLineOps.Plugins.Api.Models;

namespace OpenLineOps.Plugins.Api.Management;

public interface IPluginManagementService
{
    Task<PluginManagementOverviewResponse> GetOverviewAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PluginLifecycleRecordResponse>> StartAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PluginLifecycleRecordResponse>> StopAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ExternalPluginProcessEventResponse>> ListEventsAsync(
        string? pluginId,
        string? kind,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
