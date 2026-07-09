namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandExecutionResult(
    DeviceCommandExecutionOutcome Outcome,
    string? ResultPayload,
    string? FailureReason)
{
    public bool Succeeded => Outcome == DeviceCommandExecutionOutcome.Completed;

    public static DeviceCommandExecutionResult Completed(string? resultPayload = null)
    {
        return new DeviceCommandExecutionResult(
            DeviceCommandExecutionOutcome.Completed,
            resultPayload,
            null);
    }

    public static DeviceCommandExecutionResult Failed(string failureReason)
    {
        return new DeviceCommandExecutionResult(
            DeviceCommandExecutionOutcome.Failed,
            null,
            NormalizeReason(failureReason));
    }

    public static DeviceCommandExecutionResult Rejected(string failureReason)
    {
        return new DeviceCommandExecutionResult(
            DeviceCommandExecutionOutcome.Rejected,
            null,
            NormalizeReason(failureReason));
    }

    public static DeviceCommandExecutionResult TimedOut(string failureReason)
    {
        return new DeviceCommandExecutionResult(
            DeviceCommandExecutionOutcome.TimedOut,
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
