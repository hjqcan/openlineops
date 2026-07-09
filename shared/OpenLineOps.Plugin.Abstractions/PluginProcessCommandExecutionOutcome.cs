namespace OpenLineOps.Plugin.Abstractions;

public enum PluginProcessCommandExecutionOutcome
{
    Completed = 0,
    Failed = 1,
    Rejected = 2,
    TimedOut = 3,
    Canceled = 4
}
