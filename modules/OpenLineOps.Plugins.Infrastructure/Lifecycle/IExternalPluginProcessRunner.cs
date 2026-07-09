namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessRunner
{
    ValueTask<IExternalPluginProcess> StartAsync(
        ExternalPluginProcessStartRequest request,
        CancellationToken cancellationToken = default);
}
