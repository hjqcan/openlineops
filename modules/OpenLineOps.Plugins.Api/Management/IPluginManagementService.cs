using OpenLineOps.Plugins.Api.Models;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

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
        ExternalPluginProcessEventKind? kind,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
