namespace OpenLineOps.Plugins.Application.Commands;

public sealed record PluginDeviceCommandInvocationResult(
    PluginDeviceCommandInvocationOutcome Outcome,
    string? ResultPayload,
    string? FailureReason)
{
    public bool Succeeded => Outcome == PluginDeviceCommandInvocationOutcome.Completed;

    public static PluginDeviceCommandInvocationResult Completed(string? resultPayload = null)
    {
        return new PluginDeviceCommandInvocationResult(
            PluginDeviceCommandInvocationOutcome.Completed,
            resultPayload,
            null);
    }

    public static PluginDeviceCommandInvocationResult Failed(string failureReason)
    {
        return new PluginDeviceCommandInvocationResult(
            PluginDeviceCommandInvocationOutcome.Failed,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginDeviceCommandInvocationResult Rejected(string failureReason)
    {
        return new PluginDeviceCommandInvocationResult(
            PluginDeviceCommandInvocationOutcome.Rejected,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginDeviceCommandInvocationResult TimedOut(string failureReason)
    {
        return new PluginDeviceCommandInvocationResult(
            PluginDeviceCommandInvocationOutcome.TimedOut,
            null,
            NormalizeReason(failureReason));
    }

    private static string NormalizeReason(string failureReason)
    {
        return string.IsNullOrWhiteSpace(failureReason)
            ? throw new ArgumentException("Failure reason is required.", nameof(failureReason))
            : failureReason.Trim();
    }
}
