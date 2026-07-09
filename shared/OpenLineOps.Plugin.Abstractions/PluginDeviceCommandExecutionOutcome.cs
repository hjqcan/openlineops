namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceCommandExecutionOutcome
{
    Completed = 0,
    Failed = 1,
    Rejected = 2,
    TimedOut = 3
}
