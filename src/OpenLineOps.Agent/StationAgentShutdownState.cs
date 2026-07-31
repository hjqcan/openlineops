namespace OpenLineOps.Agent;

internal enum StationAgentWorkerState
{
    NotStarted,
    Running,
    Quiesced
}

internal sealed class StationAgentShutdownState
{
    private readonly TaskCompletionSource _workerQuiescedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workerState;

    public StationAgentWorkerState WorkerState =>
        (StationAgentWorkerState)Volatile.Read(ref _workerState);

    public bool TryMarkWorkerRunning() =>
        Interlocked.CompareExchange(
            ref _workerState,
            (int)StationAgentWorkerState.Running,
            (int)StationAgentWorkerState.NotStarted)
        == (int)StationAgentWorkerState.NotStarted;

    public void BeginShutdown()
    {
        if (Interlocked.CompareExchange(
                ref _workerState,
                (int)StationAgentWorkerState.Quiesced,
                (int)StationAgentWorkerState.NotStarted)
            == (int)StationAgentWorkerState.NotStarted)
        {
            _workerQuiescedCompletion.TrySetResult();
        }
    }

    public void MarkWorkerQuiesced()
    {
        Interlocked.Exchange(
            ref _workerState,
            (int)StationAgentWorkerState.Quiesced);
        _workerQuiescedCompletion.TrySetResult();
    }

    public async Task WaitForWorkerQuiescenceAsync(
        CancellationToken cancellationToken)
    {
        await _workerQuiescedCompletion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
