namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class NullExternalPluginProcessEventSink : IExternalPluginProcessEventSink
{
    public static NullExternalPluginProcessEventSink Instance { get; } = new();

    private NullExternalPluginProcessEventSink()
    {
    }

    public void Record(ExternalPluginProcessEvent processEvent)
    {
    }
}
