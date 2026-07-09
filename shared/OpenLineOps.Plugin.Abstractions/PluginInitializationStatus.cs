namespace OpenLineOps.Plugin.Abstractions;

public enum PluginInitializationStatus
{
    NotStarted = 0,
    Initialized = 1,
    Degraded = 2,
    Failed = 3
}
