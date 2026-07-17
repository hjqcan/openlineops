namespace OpenLineOps.Agent;

internal sealed class StationAgentShutdownState
{
    private readonly TaskCompletionSource _workerQuiescedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workerQuiesced;

    public bool WorkerQuiesced => Volatile.Read(ref _workerQuiesced) == 1;

    public void MarkWorkerQuiesced()
    {
        Volatile.Write(ref _workerQuiesced, 1);
        _workerQuiescedCompletion.TrySetResult();
    }

    public Task WaitForWorkerQuiescenceAsync(CancellationToken cancellationToken) =>
        _workerQuiescedCompletion.Task.WaitAsync(cancellationToken);
}
