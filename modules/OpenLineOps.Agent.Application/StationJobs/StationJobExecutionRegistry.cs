using System.Collections.Concurrent;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed class StationJobExecutionRegistry
{
    private readonly ConcurrentDictionary<StationJobId, ActiveExecution> _active = [];
    private readonly ConcurrentDictionary<StationJobId, byte> _pendingCancellation = [];

    internal StationJobExecutionLease Register(
        StationJobId jobId,
        CancellationToken agentStoppingToken)
    {
        var execution = new ActiveExecution(agentStoppingToken);
        if (!_active.TryAdd(jobId, execution))
        {
            execution.Dispose();
            throw new InvalidOperationException(
                $"Station job {jobId} already has an active execution registration.");
        }

        if (_pendingCancellation.TryRemove(jobId, out _))
        {
            _ = execution.RequestCancel();
        }

        return new StationJobExecutionLease(this, jobId, execution);
    }

    private bool RequestCancel(StationJobId jobId)
    {
        return _active.TryGetValue(jobId, out var execution)
            && execution.RequestCancel();
    }

    internal void RequestCancelOrRemember(StationJobId jobId)
    {
        if (RequestCancel(jobId))
        {
            return;
        }

        _pendingCancellation.TryAdd(jobId, 0);
        _ = RequestCancel(jobId);
    }

    internal void Forget(StationJobId jobId) => _pendingCancellation.TryRemove(jobId, out _);

    private void Complete(StationJobId jobId, ActiveExecution execution)
    {
        if (_active.TryGetValue(jobId, out var registered)
            && ReferenceEquals(registered, execution))
        {
            _active.TryRemove(jobId, out _);
        }

        execution.Dispose();
    }

    public sealed class StationJobExecutionLease : IDisposable
    {
        private readonly StationJobExecutionRegistry _owner;
        private readonly StationJobId _jobId;
        private ActiveExecution? _execution;

        internal StationJobExecutionLease(
            StationJobExecutionRegistry owner,
            StationJobId jobId,
            ActiveExecution execution)
        {
            _owner = owner;
            _jobId = jobId;
            _execution = execution;
        }

        public CancellationToken CancellationToken =>
            (_execution ?? throw new ObjectDisposedException(nameof(StationJobExecutionLease)))
            .CancellationToken;

        public void Dispose()
        {
            var execution = Interlocked.Exchange(ref _execution, null);
            if (execution is not null)
            {
                _owner.Complete(_jobId, execution);
            }
        }
    }

    internal sealed class ActiveExecution(CancellationToken agentStoppingToken) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(agentStoppingToken);
        private int _disposed;
        private int _cancelRequested;

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool RequestCancel()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _cancelRequested, 1) == 0)
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            return Volatile.Read(ref _disposed) == 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }
    }
}
