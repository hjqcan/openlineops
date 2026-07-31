using System.Runtime.ExceptionServices;

namespace OpenLineOps.Agent;

internal static class StationAgentWorkerLifecycle
{
    private static readonly TimeSpan UnexpectedSiblingDrainTimeout =
        TimeSpan.FromSeconds(5);

    public static async Task AwaitLoopsAsync(
        Task[] loops,
        CancellationTokenSource lifetime,
        StationAgentShutdownState shutdownState,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(shutdownState);
        try
        {
            await AwaitLoopsAsync(
                    loops,
                    lifetime,
                    "Station Agent worker",
                    stoppingToken)
                .ConfigureAwait(false);
        }
        finally
        {
            shutdownState.MarkWorkerQuiesced();
        }
    }

    public static async Task AwaitLoopsAsync(
        Task[] loops,
        CancellationTokenSource lifetime,
        string ownerName,
        CancellationToken stoppingToken,
        TimeSpan? unexpectedSiblingDrainTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        var siblingDrainTimeout =
            unexpectedSiblingDrainTimeout ?? UnexpectedSiblingDrainTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            siblingDrainTimeout,
            TimeSpan.Zero);
        if (loops.Length == 0 || loops.Any(static loop => loop is null))
        {
            throw new ArgumentException(
                $"{ownerName} loops must contain only concrete tasks.",
                nameof(loops));
        }

        var ended = await Task.WhenAny(loops).ConfigureAwait(false);
        var hostWasStopping = stoppingToken.IsCancellationRequested;
        Task? cancellationTask = null;
        Exception? cancellationStartFailure = null;
        if (!hostWasStopping)
        {
            try
            {
                cancellationTask = lifetime.CancelAsync();
            }
            catch (Exception exception)
            {
                cancellationStartFailure = exception;
            }
        }

        Exception? completionFailure = null;
        var allLoops = Task.WhenAll(loops);
        var failFastDrain = cancellationTask is null
            ? allLoops
            : Task.WhenAll(allLoops, cancellationTask);
        try
        {
            if (hostWasStopping)
            {
                await allLoops.ConfigureAwait(false);
            }
            else
            {
                await failFastDrain
                    .WaitAsync(siblingDrainTimeout)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }

        if (completionFailure is TimeoutException)
        {
            ObserveLateFault(failFastDrain);
            ObserveLateFault(allLoops);
            if (cancellationTask is not null)
            {
                ObserveLateFault(cancellationTask);
            }
        }

        var faults = loops
            .Where(static loop => loop.IsFaulted)
            .SelectMany(static loop => loop.Exception!.Flatten().InnerExceptions)
            .Concat(Flatten(cancellationStartFailure))
            .Concat(
                cancellationTask is { IsFaulted: true }
                    ? cancellationTask.Exception!.Flatten().InnerExceptions
                    : [])
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
            $"{ownerName} loop {Array.IndexOf(loops, ended)} ended unexpectedly while the Host was running.");
        if (completionFailure is TimeoutException drainTimeout)
        {
            var timeoutFailures = new List<Exception>();
            if (cancellationTask is { IsCompleted: false })
            {
                timeoutFailures.Add(
                    new TimeoutException(
                        $"{ownerName} fail-fast cancellation callbacks did not complete within {siblingDrainTimeout}.",
                        drainTimeout));
            }

            if (!allLoops.IsCompleted)
            {
                timeoutFailures.Add(
                    new TimeoutException(
                        $"{ownerName} sibling loops did not drain within {siblingDrainTimeout} after fail-fast cancellation.",
                        drainTimeout));
            }

            if (timeoutFailures.Count == 0)
            {
                timeoutFailures.Add(
                    new TimeoutException(
                        $"{ownerName} fail-fast cancellation and sibling drain did not complete within {siblingDrainTimeout}.",
                        drainTimeout));
            }

            throw new AggregateException(
            [
                unexpectedCompletion,
                .. timeoutFailures,
                .. faults
            ]);
        }

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

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static Exception[] Flatten(Exception? exception) =>
        exception switch
        {
            null => [],
            AggregateException aggregate =>
                aggregate.Flatten().InnerExceptions.ToArray(),
            _ => [exception]
        };

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

        if (shutdownState.WorkerState != StationAgentWorkerState.Quiesced)
        {
            throw new TimeoutException(
                "Station Agent worker did not quiesce before the host shutdown deadline.");
        }
    }
}
