using System.Collections.Concurrent;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class InProcessStationOperationRegistry
{
    private readonly ConcurrentDictionary<string, ActiveExecution> _active =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingCancellation =
        new(StringComparer.Ordinal);

    public ExecutionLease Register(string idempotencyKey, CancellationToken hostToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var execution = new ActiveExecution(hostToken);
        if (!_active.TryAdd(idempotencyKey, execution))
        {
            execution.Dispose();
            throw new InvalidOperationException(
                $"Station operation '{idempotencyKey}' is already registered in-process.");
        }

        if (_pendingCancellation.TryRemove(idempotencyKey, out _))
        {
            _ = execution.RequestCancel();
        }

        return new ExecutionLease(this, idempotencyKey, execution);
    }

    public void RequestCancel(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (_active.TryGetValue(idempotencyKey, out var execution)
            && execution.RequestCancel())
        {
            return;
        }

        _pendingCancellation.TryAdd(idempotencyKey, 0);
        if (_active.TryGetValue(idempotencyKey, out execution))
        {
            _ = execution.RequestCancel();
        }
    }

    private void Complete(string idempotencyKey, ActiveExecution execution)
    {
        if (_active.TryGetValue(idempotencyKey, out var registered)
            && ReferenceEquals(registered, execution))
        {
            _active.TryRemove(idempotencyKey, out _);
        }

        _pendingCancellation.TryRemove(idempotencyKey, out _);
        execution.Dispose();
    }

    public sealed class ExecutionLease : IDisposable
    {
        private readonly InProcessStationOperationRegistry _owner;
        private readonly string _idempotencyKey;
        private ActiveExecution? _execution;

        internal ExecutionLease(
            InProcessStationOperationRegistry owner,
            string idempotencyKey,
            ActiveExecution execution)
        {
            _owner = owner;
            _idempotencyKey = idempotencyKey;
            _execution = execution;
        }

        public CancellationToken CancellationToken =>
            (_execution ?? throw new ObjectDisposedException(nameof(ExecutionLease)))
            .CancellationToken;

        public bool CancelRequested =>
            (_execution ?? throw new ObjectDisposedException(nameof(ExecutionLease)))
            .CancelRequested;

        public void Dispose()
        {
            var execution = Interlocked.Exchange(ref _execution, null);
            if (execution is not null)
            {
                _owner.Complete(_idempotencyKey, execution);
            }
        }
    }

    internal sealed class ActiveExecution(CancellationToken hostToken) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        private int _disposed;
        private int _cancelRequested;

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

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

public sealed class InProcessStationOperationCanceler(
    InProcessStationOperationRegistry executions) : IStationOperationCanceler
{
    public ValueTask<StationOperationCancellationResult> CancelAsync(
        StationOperationCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        executions.RequestCancel(request.JobIdempotencyKey);
        return ValueTask.FromResult(StationOperationCancellationResult.Success());
    }
}
