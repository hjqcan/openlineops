namespace OpenLineOps.Plugin.Abstractions;

public interface IOpenLineOpsDeviceSessionPlugin : IOpenLineOpsPlugin
{
    ValueTask<PluginDeviceSession> OpenAsync(
        PluginDeviceSessionOpenRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(
        PluginDeviceSessionCloseRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PluginDeviceHealthSnapshot> GetHealthAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PluginDeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default);
}
