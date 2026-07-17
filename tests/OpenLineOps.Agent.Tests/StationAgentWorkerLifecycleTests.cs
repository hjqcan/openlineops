namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentWorkerLifecycleTests
{
    [Fact]
    public async Task RunningLoopFaultCancelsSiblingsAndPreservesTheFault()
    {
        using var lifetime = new CancellationTokenSource();
        var fault = new InvalidDataException("Synthetic receiver failure.");
        Task[] loops =
        [
            Task.FromException(fault),
            WaitForCancellationAsync(lifetime.Token),
            WaitForCancellationAsync(lifetime.Token)
        ];

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            StationAgentWorkerLifecycle.AwaitLoopsAsync(
                loops,
                lifetime,
                CancellationToken.None));

        Assert.Contains(exception.InnerExceptions, item => ReferenceEquals(item, fault));
        Assert.Contains(
            exception.InnerExceptions,
            static item => item is IOException
                           && item.Message.Contains(
                               "ended unexpectedly",
                               StringComparison.Ordinal));
        Assert.All(loops[1..], static loop => Assert.True(loop.IsCanceled));
    }

    [Fact]
    public async Task HostStopPropagatesCleanupFault()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
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
                stopping.Token));

        Assert.Same(cleanupFailure, exception);
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
        shutdownState.MarkWorkerQuiesced();

        await StationAgentWorkerLifecycle.RequireQuiescenceAsync(
            Task.CompletedTask,
            shutdownState,
            CancellationToken.None);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}
