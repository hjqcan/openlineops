namespace OpenLineOps.Runtime.Application.Commands;

public interface IRuntimeCommandExecutor
{
    ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default);
}
