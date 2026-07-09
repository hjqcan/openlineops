namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessEventSink
{
    void Record(ExternalPluginProcessEvent processEvent);
}
