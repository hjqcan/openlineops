namespace OpenLineOps.Plugins.Application.Commands;

public enum PluginProcessCommandInvocationOutcome
{
    Completed = 0,
    Failed = 1,
    Rejected = 2,
    TimedOut = 3,
    Canceled = 4
}
