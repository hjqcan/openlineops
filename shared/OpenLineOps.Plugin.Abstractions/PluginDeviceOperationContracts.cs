namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceOperationOutcome
{
    Completed = 0,
    Rejected = 1,
    Failed = 2,
    TimedOut = 3
}

public sealed record PluginDeviceInvocationRequest
{
    public PluginDeviceInvocationRequest(
        string sessionId,
        PluginDeviceCommandEnvelope command,
        string operation,
        string? inputPayload = null)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Operation = PluginDeviceContractGuard.RequiredCanonical(operation, nameof(operation));
        InputPayload = inputPayload;
    }

    public string SessionId { get; }

    public PluginDeviceCommandEnvelope Command { get; }

    public string Operation { get; }

    public string? InputPayload { get; }
}

public sealed record PluginDeviceOperationResult
{
    public PluginDeviceOperationResult(
        PluginDeviceOperationOutcome outcome,
        DateTimeOffset completedAtUtc,
        string? outputPayload,
        string? failureReason)
    {
        Outcome = PluginDeviceContractGuard.Defined(outcome, nameof(outcome));
        CompletedAtUtc = PluginDeviceContractGuard.Utc(completedAtUtc, nameof(completedAtUtc));
        OutputPayload = outputPayload;

        if (Outcome == PluginDeviceOperationOutcome.Completed)
        {
            if (failureReason is not null)
            {
                throw new ArgumentException(
                    "A completed operation cannot include a failure reason.",
                    nameof(failureReason));
            }

            FailureReason = null;
            return;
        }

        FailureReason = PluginDeviceContractGuard.RequiredCanonical(
            failureReason!,
            nameof(failureReason));
    }

    public PluginDeviceOperationOutcome Outcome { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public string? OutputPayload { get; }

    public string? FailureReason { get; }

    public bool Succeeded => Outcome == PluginDeviceOperationOutcome.Completed;

    public static PluginDeviceOperationResult Completed(
        DateTimeOffset completedAtUtc,
        string? outputPayload = null) =>
        new(PluginDeviceOperationOutcome.Completed, completedAtUtc, outputPayload, null);

    public static PluginDeviceOperationResult Rejected(
        DateTimeOffset completedAtUtc,
        string failureReason) =>
        new(PluginDeviceOperationOutcome.Rejected, completedAtUtc, null, failureReason);

    public static PluginDeviceOperationResult Failed(
        DateTimeOffset completedAtUtc,
        string failureReason) =>
        new(PluginDeviceOperationOutcome.Failed, completedAtUtc, null, failureReason);

    public static PluginDeviceOperationResult TimedOut(
        DateTimeOffset completedAtUtc,
        string failureReason) =>
        new(PluginDeviceOperationOutcome.TimedOut, completedAtUtc, null, failureReason);
}
