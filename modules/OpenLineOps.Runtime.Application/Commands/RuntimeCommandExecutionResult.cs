namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionResult(
    RuntimeCommandExecutionOutcome Outcome,
    string? Payload,
    string? Reason)
{
    public static RuntimeCommandExecutionResult Completed(string? payload = null)
    {
        return new RuntimeCommandExecutionResult(RuntimeCommandExecutionOutcome.Completed, payload, null);
    }

    public static RuntimeCommandExecutionResult Failed(string reason)
    {
        return new RuntimeCommandExecutionResult(RuntimeCommandExecutionOutcome.Failed, null, NormalizeReason(reason, "Command failed."));
    }

    public static RuntimeCommandExecutionResult Rejected(string reason)
    {
        return new RuntimeCommandExecutionResult(RuntimeCommandExecutionOutcome.Rejected, null, NormalizeReason(reason, "Command rejected."));
    }

    public static RuntimeCommandExecutionResult TimedOut(string reason = "Command timed out.")
    {
        return new RuntimeCommandExecutionResult(RuntimeCommandExecutionOutcome.TimedOut, null, NormalizeReason(reason, "Command timed out."));
    }

    public static RuntimeCommandExecutionResult Canceled(string reason = "Command canceled.")
    {
        return new RuntimeCommandExecutionResult(RuntimeCommandExecutionOutcome.Canceled, null, NormalizeReason(reason, "Command canceled."));
    }

    private static string NormalizeReason(string reason, string fallback)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? fallback
            : reason.Trim();
    }
}
