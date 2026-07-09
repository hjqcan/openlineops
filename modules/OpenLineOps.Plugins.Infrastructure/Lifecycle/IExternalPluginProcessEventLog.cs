namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessEventLog : IExternalPluginProcessEventSink
{
    ValueTask<IReadOnlyList<ExternalPluginProcessEvent>> ListAsync(
        ExternalPluginProcessEventQuery? query = null,
        CancellationToken cancellationToken = default);
}
