using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class SimulatedRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = context.InputPayload ?? string.Empty;

        if (ContainsOutcome(payload, "failed") || ContainsOutcome(payload, "fail"))
        {
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Failed("Simulated command failure."));
        }

        if (ContainsOutcome(payload, "rejected") || ContainsOutcome(payload, "reject"))
        {
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Rejected("Simulated command rejection."));
        }

        if (ContainsOutcome(payload, "timedout") || ContainsOutcome(payload, "timeout"))
        {
            return ValueTask.FromResult(RuntimeCommandExecutionResult.TimedOut("Simulated command timeout."));
        }

        if (ContainsOutcome(payload, "canceled") || ContainsOutcome(payload, "cancel"))
        {
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Canceled("Simulated command cancellation."));
        }

        var resultPayload = string.IsNullOrWhiteSpace(context.InputPayload)
            ? $"{{\"nodeId\":\"{context.NodeId.Value}\",\"command\":\"{context.CommandName}\",\"simulated\":true}}"
            : context.InputPayload;

        return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(resultPayload));
    }

    private static bool ContainsOutcome(string payload, string outcome)
    {
        return payload.Contains(outcome, StringComparison.OrdinalIgnoreCase);
    }
}
