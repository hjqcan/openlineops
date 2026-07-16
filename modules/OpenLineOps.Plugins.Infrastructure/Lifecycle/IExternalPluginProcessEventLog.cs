namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessEventLog : IExternalPluginProcessEventSink
{
    ValueTask<IReadOnlyList<ExternalPluginProcessEvent>> ListAsync(
        ExternalPluginProcessEventQuery query,
        CancellationToken cancellationToken = default);
}
