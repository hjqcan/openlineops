using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class TcpLineScannerDeviceSession : DeviceSessionBase
{
    private readonly TcpLineScannerConfiguration _configuration;
    private readonly TcpLineTransport _transport;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _signalGate = new();
    private readonly List<PluginDeviceSignalSample> _history = [];
    private readonly Dictionary<string, ScannerSubscription> _subscriptions =
        new(StringComparer.Ordinal);
    private PluginDeviceSignalSample? _latest;
    private Task? _receiveTask;
    private long _sequence;

    private TcpLineScannerDeviceSession(
        PluginDeviceSession contract,
        TcpLineScannerConfiguration configuration,
        DeviceSessionJournalWriter? journal,
        TimeProvider timeProvider)
        : base(contract, timeProvider, journal)
    {
        _configuration = configuration;
        _transport = new TcpLineTransport(
            configuration.Host,
            configuration.Port,
            configuration.ConnectTimeoutMilliseconds,
            configuration.Encoding,
            "lf");
        _sequence = RestoredDeviceSequence;
    }

    public static async ValueTask<IDeviceSession> OpenAsync(
        string deviceInstanceId,
        TcpLineScannerConfiguration configuration,
        DeviceSessionJournalWriter? journal,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var contract = new PluginDeviceSession(
            $"tcp-line-scanner:{deviceInstanceId}:{Guid.NewGuid():N}",
            deviceInstanceId,
            timeProvider.GetUtcNow(),
            TimeSpan.FromMilliseconds(configuration.HeartbeatMilliseconds));
        var session = new TcpLineScannerDeviceSession(
            contract,
            configuration,
            journal,
            timeProvider);
        await session.ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
        session._receiveTask = session.ReceiveLoopAsync();
        return session;
    }

    public override ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SignalIds.Count != 1
            || !string.Equals(
                request.SignalIds[0],
                _configuration.SignalId,
                StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                $"Scanner only exposes signal '{_configuration.SignalId}'.");
        }

        lock (_signalGate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceSignalSample>>(
                _latest is null
                    ? []
                    : [_latest]);
        }
    }

    public override ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        var payload = DeviceSessionJournal.SerializePayload(
            request.Writes.Select(static write => new ScannerWritePayload(
                write.SignalId,
                write.Value.Type,
                write.Value.CanonicalValue,
                write.Unit)).ToArray());
        return ExecuteCommandAsync(
            request.Command,
            "Write",
            payload,
            _ => ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                TimeProvider.GetUtcNow(),
                "A passive TCP line scanner does not accept signal writes.")),
            cancellationToken);
    }

    public override async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        if (request.SignalIds.Count != 1
            || !string.Equals(
                request.SignalIds[0],
                _configuration.SignalId,
                StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                $"Scanner only exposes signal '{_configuration.SignalId}'.");
        }

        var subscription = new ScannerSubscription(request.SubscriptionId);
        PluginDeviceSignalSample[] replay;
        lock (_signalGate)
        {
            if (_subscriptions.ContainsKey(request.SubscriptionId))
            {
                throw new InvalidOperationException(
                    $"Scanner subscription '{request.SubscriptionId}' is already active.");
            }

            var cutoff = _sequence;
            replay = _history
                .Where(sample => sample.Sequence > (request.ResumeAfterSequence ?? 0)
                                 && sample.Sequence <= cutoff)
                .ToArray();
            _subscriptions.Add(request.SubscriptionId, subscription);
        }

        try
        {
            foreach (var sample in replay)
            {
                yield return new PluginDeviceSignalSubscriptionEvent(
                    request.SubscriptionId,
                    sample.Sequence,
                    sample);
            }

            await foreach (var sample in subscription.Channel.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return new PluginDeviceSignalSubscriptionEvent(
                    request.SubscriptionId,
                    sample.Sequence,
                    sample);
            }
        }
        finally
        {
            lock (_signalGate)
            {
                _subscriptions.Remove(request.SubscriptionId);
            }

            subscription.Channel.Writer.TryComplete();
        }
    }

    public override ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        var requestPayload = DeviceSessionJournal.SerializePayload(new JournalInvokeRequest(
            request.Operation,
            request.InputPayload));
        return ExecuteCommandAsync(
            request.Command,
            "Invoke",
            requestPayload,
            async token =>
            {
                if (!string.Equals(
                        request.Operation,
                        "Reconnect",
                        StringComparison.Ordinal))
                {
                    return PluginDeviceOperationResult.Rejected(
                        TimeProvider.GetUtcNow(),
                        $"Scanner operation '{request.Operation}' is not supported.");
                }

                await _connectionGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    MarkDisconnected(
                        "TcpLineScanner.ManualReconnect",
                        "A controlled scanner reconnect was requested.",
                        reconnecting: _configuration.Reconnect.Enabled);
                }
                finally
                {
                    _connectionGate.Release();
                }

                return PluginDeviceOperationResult.Completed(
                    TimeProvider.GetUtcNow(),
                    """{"reconnectRequested":true}""");
            },
            cancellationToken);
    }

    protected override async ValueTask DisposeSessionAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
            {
                // Expected while closing the session.
            }
        }

        lock (_signalGate)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Channel.Writer.TryComplete();
            }

            _subscriptions.Clear();
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _connectionGate.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var retryCount = 0;
        while (!Lifetime.IsCancellationRequested)
        {
            try
            {
                if (!_transport.IsConnected)
                {
                    await ConnectOnceAsync(Lifetime.Token).ConfigureAwait(false);
                    retryCount = 0;
                }

                var line = await _transport.ReadLineAsync(Lifetime.Token)
                    .ConfigureAwait(false);
                var sourceTimestamp = TimeProvider.GetUtcNow();
                var sample = new PluginDeviceSignalSample(
                    _configuration.SignalId,
                    PluginDeviceValue.FromString(line),
                    unit: null,
                    PluginDeviceSignalQuality.Good,
                    sourceTimestamp,
                    TimeProvider.GetUtcNow(),
                    Interlocked.Increment(ref _sequence));
                ScannerSubscription[] subscriptions;
                lock (_signalGate)
                {
                    _latest = sample;
                    _history.Add(sample);
                    if (_history.Count > _configuration.HistoryCapacity)
                    {
                        _history.RemoveRange(
                            0,
                            _history.Count - _configuration.HistoryCapacity);
                    }

                    subscriptions = _subscriptions.Values.ToArray();
                }

                await RecordSignalAsync(sample, Lifetime.Token).ConfigureAwait(false);
                foreach (var subscription in subscriptions)
                {
                    subscription.Channel.Writer.TryWrite(sample);
                }

                MarkHeartbeat();
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsNetworkFailure(exception)
                                               || exception
                                               is OperationCanceledException)
            {
                await DisconnectTransportAsync().ConfigureAwait(false);
                var canRetry = CanRetry(retryCount);
                MarkDisconnected(
                    "TcpLineScanner.Disconnected",
                    canRetry
                        ? $"Scanner connection was lost; reconnect attempt {retryCount + 1} is scheduled."
                        : $"Scanner connection was lost: {exception.Message}",
                    canRetry);
                if (!canRetry)
                {
                    CompleteSubscriptions(exception);
                    return;
                }

                retryCount++;
                try
                {
                    await DelayBeforeRetryAsync(retryCount, Lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                MarkDisconnected(
                    "TcpLineScanner.ReceiveFailed",
                    $"Scanner receive loop stopped: {exception.Message}",
                    reconnecting: false);
                CompleteSubscriptions(exception);
                return;
            }
        }
    }

    private async ValueTask ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsNetworkFailure(exception)
                                              || exception is OperationCanceledException)
            {
                if (exception is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (!CanRetry(retryCount))
                {
                    MarkDisconnected(
                        "TcpLineScanner.ConnectFailed",
                        "Scanner connection could not be established.",
                        reconnecting: false);
                    throw;
                }

                retryCount++;
                MarkDisconnected(
                    "TcpLineScanner.Reconnecting",
                    $"Scanner connection attempt failed; retry {retryCount} is scheduled.",
                    reconnecting: true);
                await DelayBeforeRetryAsync(retryCount, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ConnectOnceAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_transport.IsConnected)
            {
                return;
            }

            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            MarkConnected(
                "TcpLineScanner.Connected",
                $"Connected to scanner endpoint {_configuration.Host}:{_configuration.Port}.");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async ValueTask DisconnectTransportAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private bool CanRetry(int retryCount) =>
        _configuration.Reconnect.Enabled
        && (_configuration.Reconnect.MaxAttempts == 0
            || retryCount < _configuration.Reconnect.MaxAttempts);

    private ValueTask DelayBeforeRetryAsync(
        int retryCount,
        CancellationToken cancellationToken)
    {
        var initial = _configuration.Reconnect.InitialDelayMilliseconds;
        var factor = Math.Pow(2, Math.Min(retryCount - 1, 20));
        var milliseconds = Math.Min(
            _configuration.Reconnect.MaximumDelayMilliseconds,
            initial * factor);
        return new ValueTask(Task.Delay(
            TimeSpan.FromMilliseconds(milliseconds),
            TimeProvider,
            cancellationToken));
    }

    private void CompleteSubscriptions(Exception exception)
    {
        lock (_signalGate)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Channel.Writer.TryComplete(exception);
            }
        }
    }

    private void ValidateSession(string sessionId)
    {
        if (!string.Equals(sessionId, Contract.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Request session '{sessionId}' does not match '{Contract.SessionId}'.");
        }
    }

    private sealed class ScannerSubscription
    {
        public ScannerSubscription(string subscriptionId)
        {
            SubscriptionId = subscriptionId;
        }

        public string SubscriptionId { get; }

        public Channel<PluginDeviceSignalSample> Channel { get; } =
            System.Threading.Channels.Channel
                .CreateUnbounded<PluginDeviceSignalSample>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    });
    }

    private sealed record ScannerWritePayload(
        string SignalId,
        PluginDeviceValueType ValueType,
        string CanonicalValue,
        string? Unit);
}
