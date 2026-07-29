namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentWorkerLifecycleTests
{
    [Fact]
    public async Task RunningLoopFaultMarksQuiescedOnlyAfterEverySiblingDrains()
    {
        using var lifetime = new CancellationTokenSource();
        var shutdownState = new StationAgentShutdownState();
        Assert.True(shutdownState.TryMarkWorkerRunning());
        var fault = new InvalidDataException("Synthetic receiver failure.");
        var drainStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] loops =
        [
            Task.FromException(fault),
            DrainCancellationAsync(drainStarted, releaseDrain, lifetime.Token),
            WaitForCancellationAsync(lifetime.Token)
        ];

        var awaitingLoops = StationAgentWorkerLifecycle.AwaitLoopsAsync(
            loops,
            lifetime,
            shutdownState,
            CancellationToken.None);
        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            StationAgentWorkerState.Running,
            shutdownState.WorkerState);

        releaseDrain.TrySetResult();
        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            awaitingLoops);

        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        Assert.Contains(exception.InnerExceptions, item => ReferenceEquals(item, fault));
        Assert.Contains(
            exception.InnerExceptions,
            static item => item is IOException
                           && item.Message.Contains(
                               "ended unexpectedly",
                               StringComparison.Ordinal));
        Assert.True(loops[1].IsCompletedSuccessfully);
        Assert.True(loops[2].IsCanceled);
    }

    [Fact]
    public async Task HostStopPropagatesCleanupFault()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var shutdownState = new StationAgentShutdownState();
        Assert.True(shutdownState.TryMarkWorkerRunning());
        var cleanupFailure = new InvalidDataException("Synthetic drain failure.");
        Task[] loops =
        [
            Task.FromCanceled(stopping.Token),
            Task.FromException(cleanupFailure),
            Task.FromCanceled(stopping.Token)
        ];

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            StationAgentWorkerLifecycle.AwaitLoopsAsync(
                loops,
                lifetime,
                shutdownState,
                stopping.Token));

        Assert.Same(cleanupFailure, exception);
        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
    }

    [Fact]
    public async Task HostStopMarksRunningWorkerQuiescedAfterNormalDrain()
    {
        using var stopping = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var shutdownState = new StationAgentShutdownState();
        Assert.True(shutdownState.TryMarkWorkerRunning());
        Task[] loops =
        [
            WaitForCancellationAsync(lifetime.Token),
            WaitForCancellationAsync(lifetime.Token),
            WaitForCancellationAsync(lifetime.Token)
        ];

        stopping.Cancel();
        await StationAgentWorkerLifecycle.AwaitLoopsAsync(
            loops,
            lifetime,
            shutdownState,
            stopping.Token);

        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        Assert.All(loops, static loop => Assert.True(loop.IsCanceled));
    }

    [Fact]
    public async Task BlockingFailFastCancellationCallbackCannotHideUnexpectedLoopCompletion()
    {
        using var lifetime = new CancellationTokenSource();
        var siblingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = ObserveCancellationAsync(
            siblingCanceled,
            lifetime.Token);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCallbackFailure = new InvalidOperationException(
            "Synthetic late cancellation callback failure.");
        using var callback = lifetime.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
            throw lateCallbackFailure;
        });
        Task[] loops =
        [
            Task.CompletedTask,
            sibling
        ];

        var awaitingLoops = StationAgentWorkerLifecycle.AwaitLoopsAsync(
            loops,
            lifetime,
            "Synthetic worker",
            CancellationToken.None,
            TimeSpan.FromMilliseconds(100));
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var exception = await Assert.ThrowsAsync<AggregateException>(
                async () => await awaitingLoops.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(lifetime.IsCancellationRequested);
            Assert.Contains(
                exception.Flatten().InnerExceptions,
                static item => item is TimeoutException
                               && item.Message.Contains(
                                   "cancellation callbacks did not complete",
                                   StringComparison.Ordinal));
            Assert.Contains(
                exception.Flatten().InnerExceptions,
                static item => item is TimeoutException
                               && item.Message.Contains(
                                   "sibling loops did not drain",
                                   StringComparison.Ordinal));
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling);
    }

    [Fact]
    public async Task ShutdownClosesNotStartedWorkerAndRejectsLateStart()
    {
        var shutdownState = new StationAgentShutdownState();

        shutdownState.BeginShutdown();
        await shutdownState.WaitForWorkerQuiescenceAsync(CancellationToken.None);

        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        Assert.False(shutdownState.TryMarkWorkerRunning());
    }

    [Fact]
    public void StartupRollbackTransitionsRunningWorkerToQuiesced()
    {
        var shutdownState = new StationAgentShutdownState();
        Assert.True(shutdownState.TryMarkWorkerRunning());

        shutdownState.MarkWorkerQuiesced();

        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        Assert.False(shutdownState.TryMarkWorkerRunning());
    }

    [Fact]
    public async Task StopDeadlineIsReportedAsQuiescenceTimeout()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var canceledStop = Task.FromCanceled(deadline.Token);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            StationAgentWorkerLifecycle.RequireQuiescenceAsync(
                canceledStop,
                new StationAgentShutdownState(),
                deadline.Token));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task CompletedStopRequiresWorkerQuiescenceEvidence()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            StationAgentWorkerLifecycle.RequireQuiescenceAsync(
                Task.CompletedTask,
                new StationAgentShutdownState(),
                CancellationToken.None));

        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task CompletedStopAcceptsWorkerQuiescenceEvidence()
    {
        var shutdownState = new StationAgentShutdownState();
        Assert.True(shutdownState.TryMarkWorkerRunning());
        shutdownState.MarkWorkerQuiesced();

        await StationAgentWorkerLifecycle.RequireQuiescenceAsync(
            Task.CompletedTask,
            shutdownState,
            CancellationToken.None);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task ObserveCancellationAsync(
        TaskCompletionSource cancellationObserved,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForCancellationAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved.TrySetResult();
            throw;
        }
    }

    private static async Task DrainCancellationAsync(
        TaskCompletionSource drainStarted,
        TaskCompletionSource releaseDrain,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            drainStarted.TrySetResult();
            await releaseDrain.Task;
        }
    }
}
