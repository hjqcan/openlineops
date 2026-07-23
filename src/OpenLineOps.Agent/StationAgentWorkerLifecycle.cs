using System.Runtime.ExceptionServices;

namespace OpenLineOps.Agent;

internal static class StationAgentWorkerLifecycle
{
    public static async Task AwaitLoopsAsync(
        Task[] loops,
        CancellationTokenSource lifetime,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (loops.Length == 0 || loops.Any(static loop => loop is null))
        {
            throw new ArgumentException(
                "Station Agent worker loops must contain only concrete tasks.",
                nameof(loops));
        }

        var ended = await Task.WhenAny(loops).ConfigureAwait(false);
        var hostWasStopping = stoppingToken.IsCancellationRequested;
        if (!hostWasStopping)
        {
            lifetime.Cancel();
        }

        Exception? completionFailure = null;
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }

        var faults = loops
            .Where(static loop => loop.IsFaulted)
            .SelectMany(static loop => loop.Exception!.Flatten().InnerExceptions)
            .ToArray();
        if (hostWasStopping)
        {
            if (faults.Length == 1)
            {
                ExceptionDispatchInfo.Capture(faults[0]).Throw();
            }

            if (faults.Length > 1)
            {
                throw new AggregateException(faults);
            }

            return;
        }

        var unexpectedCompletion = new IOException(
            $"Station Agent worker loop '{ended.Id}' ended unexpectedly while the host was running.");
        if (faults.Length == 0)
        {
            throw completionFailure is not null
                  && completionFailure is not OperationCanceledException
                ? new IOException(unexpectedCompletion.Message, completionFailure)
                : unexpectedCompletion;
        }

        throw new AggregateException(
            [unexpectedCompletion, .. faults]);
    }

    public static async Task RequireQuiescenceAsync(
        Task stopTask,
        StationAgentShutdownState shutdownState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopTask);
        ArgumentNullException.ThrowIfNull(shutdownState);
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Station Agent worker did not quiesce before the host shutdown deadline.",
                exception);
        }

        if (!shutdownState.WorkerQuiesced)
        {
            throw new TimeoutException(
                "Station Agent worker did not quiesce before the host shutdown deadline.");
        }
    }
}
