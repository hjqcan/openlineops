namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginDeviceCommandExecutionResult(
    PluginDeviceCommandExecutionOutcome Outcome,
    string? ResultPayload,
    string? FailureReason)
{
    public bool Succeeded => Outcome == PluginDeviceCommandExecutionOutcome.Completed;

    public static PluginDeviceCommandExecutionResult Completed(string? resultPayload = null)
    {
        return new PluginDeviceCommandExecutionResult(
            PluginDeviceCommandExecutionOutcome.Completed,
            resultPayload,
            null);
    }

    public static PluginDeviceCommandExecutionResult Failed(string failureReason)
    {
        return new PluginDeviceCommandExecutionResult(
            PluginDeviceCommandExecutionOutcome.Failed,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginDeviceCommandExecutionResult Rejected(string failureReason)
    {
        return new PluginDeviceCommandExecutionResult(
            PluginDeviceCommandExecutionOutcome.Rejected,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginDeviceCommandExecutionResult TimedOut(string failureReason)
    {
        return new PluginDeviceCommandExecutionResult(
            PluginDeviceCommandExecutionOutcome.TimedOut,
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
