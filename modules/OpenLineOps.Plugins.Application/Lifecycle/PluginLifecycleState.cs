namespace OpenLineOps.Plugins.Application.Lifecycle;

public enum PluginLifecycleState
{
    Discovered = 1,
    Invalid = 2,
    Initialized = 3,
    Degraded = 4,
    Failed = 5,
    Stopped = 6
}
