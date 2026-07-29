namespace OpenLineOps.Agent.Tests;

public sealed class StationMaterialArrivalWorkerLifecycleTests
{
    [Fact]
    public async Task IpcFaultCancelsAndAwaitsOutboxSibling()
    {
        var fault = new InvalidDataException("Synthetic IPC failure.");

        var exception = await RunFaultScenarioAsync(
            faultFirst: true,
            fault);

        Assert.Contains(
            exception.Flatten().InnerExceptions,
            item => ReferenceEquals(item, fault));
    }

    [Fact]
    public async Task OutboxFaultCancelsAndAwaitsIpcSibling()
    {
        var fault = new InvalidDataException("Synthetic outbox failure.");

        var exception = await RunFaultScenarioAsync(
            faultFirst: false,
            fault);

        Assert.Contains(
            exception.Flatten().InnerExceptions,
            item => ReferenceEquals(item, fault));
    }

    [Fact]
    public async Task IpcEarlyCompletionCancelsAndAwaitsOutboxSibling()
    {
        await RunEarlyCompletionScenarioAsync(completedFirst: true);
    }

    [Fact]
    public async Task OutboxEarlyCompletionCancelsAndAwaitsIpcSibling()
    {
        await RunEarlyCompletionScenarioAsync(completedFirst: false);
    }

    [Fact]
    public async Task EarlyCompletionBoundsNonCooperativeSiblingDrain()
    {
        using var lifetime = new CancellationTokenSource();
        var sibling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] loops =
        [
            Task.CompletedTask,
            sibling.Task
        ];

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            StationMaterialArrivalWorker.AwaitLoopsAsync(
                loops,
                lifetime,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(50)));

        Assert.True(lifetime.IsCancellationRequested);
        Assert.Contains(
            exception.Flatten().InnerExceptions,
            static item => item is TimeoutException
                           && item.Message.Contains(
                               "did not drain",
                               StringComparison.Ordinal));
        sibling.TrySetCanceled();
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotSkipSiblingDrain()
    {
        using var lifetime = new CancellationTokenSource();
        var siblingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = WaitForCancellationAsync(
            siblingCanceled,
            lifetime.Token);
        var callbackFailure = new InvalidOperationException(
            "Synthetic cancellation callback failure.");
        using var callback = lifetime.Token.Register(() =>
            throw callbackFailure);
        Task[] loops =
        [
            Task.CompletedTask,
            sibling
        ];

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            StationMaterialArrivalWorker.AwaitLoopsAsync(
                loops,
                lifetime,
                CancellationToken.None));

        await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sibling.IsCanceled);
        Assert.Contains(
            exception.Flatten().InnerExceptions,
            item => ReferenceEquals(item, callbackFailure));
    }

    private static async Task<AggregateException> RunFaultScenarioAsync(
        bool faultFirst,
        Exception fault)
    {
        using var lifetime = new CancellationTokenSource();
        var siblingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = WaitForCancellationAsync(
            siblingCanceled,
            lifetime.Token);
        Task[] loops = faultFirst
            ? [Task.FromException(fault), sibling]
            : [sibling, Task.FromException(fault)];

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            StationMaterialArrivalWorker.AwaitLoopsAsync(
                loops,
                lifetime,
                CancellationToken.None));

        await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(sibling.IsCanceled);
        return exception;
    }

    private static async Task RunEarlyCompletionScenarioAsync(
        bool completedFirst)
    {
        using var lifetime = new CancellationTokenSource();
        var siblingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = WaitForCancellationAsync(
            siblingCanceled,
            lifetime.Token);
        Task[] loops = completedFirst
            ? [Task.CompletedTask, sibling]
            : [sibling, Task.CompletedTask];

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            StationMaterialArrivalWorker.AwaitLoopsAsync(
                loops,
                lifetime,
                CancellationToken.None));

        Assert.Contains(
            "ended unexpectedly",
            exception.Message,
            StringComparison.Ordinal);
        await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(sibling.IsCanceled);
    }

    private static async Task WaitForCancellationAsync(
        TaskCompletionSource cancellationObserved,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved.TrySetResult();
            throw;
        }
    }
}
