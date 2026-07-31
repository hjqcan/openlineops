using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class ScpiTcpDeviceSession : DeviceSessionBase
{
    private readonly ScpiTcpConfiguration _configuration;
    private readonly TcpLineTransport _transport;
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly object _historyGate = new();
    private readonly List<PluginDeviceSignalSample> _history = [];
    private long _sequence;

    private ScpiTcpDeviceSession(
        PluginDeviceSession contract,
        ScpiTcpConfiguration configuration,
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
            configuration.LineTerminator);
        _sequence = RestoredDeviceSequence;
    }

    public static async ValueTask<IDeviceSession> OpenAsync(
        string deviceInstanceId,
        ScpiTcpConfiguration configuration,
        DeviceSessionJournalWriter? journal,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var contract = new PluginDeviceSession(
            $"scpi-tcp:{deviceInstanceId}:{Guid.NewGuid():N}",
            deviceInstanceId,
            timeProvider.GetUtcNow(),
            TimeSpan.FromMilliseconds(configuration.HeartbeatMilliseconds));
        var session = new ScpiTcpDeviceSession(
            contract,
            configuration,
            journal,
            timeProvider);
        await session.ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public override async ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_configuration.OperationTimeoutMilliseconds),
            TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            Lifetime.Token,
            timeout.Token);
        var samples = new List<PluginDeviceSignalSample>(request.SignalIds.Count);
        try
        {
            foreach (var signalId in request.SignalIds)
            {
                var response = await ExecuteTransportTransactionAsync(
                        signalId,
                        expectsResponse: true,
                        PluginDeviceCommandIdempotencyClass.Idempotent,
                        linked.Token)
                    .ConfigureAwait(false);
                var sourceTimestamp = TimeProvider.GetUtcNow();
                var sample = new PluginDeviceSignalSample(
                    signalId,
                    PluginDeviceValue.FromString(response!),
                    unit: null,
                    PluginDeviceSignalQuality.Good,
                    sourceTimestamp,
                    TimeProvider.GetUtcNow(),
                    Interlocked.Increment(ref _sequence));
                AddHistory(sample);
                await RecordSignalAsync(sample, linked.Token).ConfigureAwait(false);
                samples.Add(sample);
            }

            return samples.AsReadOnly();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "SCPI signal read exceeded the configured operation timeout.");
        }
    }

    public override ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        var requestPayload = DeviceSessionJournal.SerializePayload(
            request.Writes.Select(static write => new ScpiSignalWritePayload(
                write.SignalId,
                write.Value.Type,
                write.Value.CanonicalValue,
                write.Unit)).ToArray());
        return ExecuteCommandAsync(
            request.Command,
            "Write",
            requestPayload,
            async token =>
            {
                foreach (var write in request.Writes)
                {
                    var command = $"{write.SignalId} {write.Value.CanonicalValue}";
                    await ExecuteTransportTransactionAsync(
                            command,
                            expectsResponse: false,
                            request.Command.IdempotencyClass,
                            token)
                        .ConfigureAwait(false);
                }

                return PluginDeviceOperationResult.Completed(
                    TimeProvider.GetUtcNow(),
                    DeviceSessionJournal.SerializePayload(
                        new ScpiWriteResponse(request.Writes.Count)));
            },
            cancellationToken);
    }

    public override async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        var resumeAfter = request.ResumeAfterSequence ?? 0;
        PluginDeviceSignalSample[] replay;
        lock (_historyGate)
        {
            replay = _history
                .Where(sample => sample.Sequence > resumeAfter
                                 && request.SignalIds.Contains(
                                     sample.SignalId,
                                     StringComparer.Ordinal))
                .ToArray();
        }

        foreach (var sample in replay)
        {
            yield return new PluginDeviceSignalSubscriptionEvent(
                request.SubscriptionId,
                sample.Sequence,
                sample);
            resumeAfter = Math.Max(resumeAfter, sample.Sequence);
        }

        var interval = request.MinimumSamplingInterval
                       ?? TimeSpan.FromMilliseconds(
                           _configuration.HeartbeatMilliseconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            var samples = await ReadAsync(
                    new PluginDeviceSignalReadRequest(
                        request.SessionId,
                        request.SignalIds),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var sample in samples.Where(sample =>
                         sample.Sequence > resumeAfter))
            {
                yield return new PluginDeviceSignalSubscriptionEvent(
                    request.SubscriptionId,
                    sample.Sequence,
                    sample);
                resumeAfter = sample.Sequence;
            }

            await Task.Delay(interval, TimeProvider, cancellationToken)
                .ConfigureAwait(false);
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
            token => InvokeCoreAsync(request, token),
            cancellationToken);
    }

    protected override async ValueTask DisposeSessionAsync()
    {
        await _transportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _transportGate.Release();
            _transportGate.Dispose();
        }
    }

    private async ValueTask<PluginDeviceOperationResult> InvokeCoreAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Operation, "Reconnect", StringComparison.Ordinal))
        {
            await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _transport.DisconnectAsync().ConfigureAwait(false);
                await ConnectWithRetryUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transportGate.Release();
            }

            return PluginDeviceOperationResult.Completed(
                TimeProvider.GetUtcNow(),
                """{"connected":true}""");
        }

        var payload = DeserializeRequired<ScpiCommandPayload>(
            request.InputPayload,
            request.Operation);
        return request.Operation switch
        {
            "Query" or "Read" => PluginDeviceOperationResult.Completed(
                TimeProvider.GetUtcNow(),
                DeviceSessionJournal.SerializePayload(new ScpiQueryResponse(
                    await ExecuteTransportTransactionAsync(
                            payload.Command,
                            expectsResponse: true,
                            request.Command.IdempotencyClass,
                            cancellationToken)
                        .ConfigureAwait(false)))),
            "Write" => await WriteCommandAsync(
                    payload.Command,
                    request.Command.IdempotencyClass,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => PluginDeviceOperationResult.Rejected(
                TimeProvider.GetUtcNow(),
                $"SCPI operation '{request.Operation}' is not supported.")
        };
    }

    private async ValueTask<PluginDeviceOperationResult> WriteCommandAsync(
        string command,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        CancellationToken cancellationToken)
    {
        await ExecuteTransportTransactionAsync(
                command,
                expectsResponse: false,
                idempotencyClass,
                cancellationToken)
            .ConfigureAwait(false);
        return PluginDeviceOperationResult.Completed(
            TimeProvider.GetUtcNow(),
            """{"written":true}""");
    }

    private async ValueTask<string?> ExecuteTransportTransactionAsync(
        string command,
        bool expectsResponse,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command)
            || !string.Equals(command, command.Trim(), StringComparison.Ordinal)
            || command.Contains('\r', StringComparison.Ordinal)
            || command.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "SCPI command must be one canonical line without CR or LF.");
        }

        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retryCount = 0;
            while (true)
            {
                var transmitted = false;
                try
                {
                    if (!_transport.IsConnected)
                    {
                        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        MarkConnected(
                            "ScpiTcp.Connected",
                            $"Connected to SCPI endpoint {_configuration.Host}:{_configuration.Port}.");
                    }

                    await _transport.WriteLineAsync(command, cancellationToken)
                        .ConfigureAwait(false);
                    transmitted = true;
                    var response = expectsResponse
                        ? await _transport.ReadLineAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                    MarkHeartbeat();
                    return response;
                }
                catch (Exception exception) when (IsNetworkFailure(exception))
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    var canRetry = CanRetry(retryCount)
                                   && (!transmitted
                                       || idempotencyClass
                                       == PluginDeviceCommandIdempotencyClass.Idempotent);
                    MarkDisconnected(
                        "ScpiTcp.Disconnected",
                        canRetry
                            ? $"SCPI connection failed; reconnect attempt {retryCount + 1} is scheduled."
                            : transmitted
                                ? "SCPI connection failed after transmission; command completion is unknown and the command will not be resent."
                                : "SCPI connection failed and reconnect is disabled or exhausted.",
                        canRetry);
                    if (!canRetry)
                    {
                        throw;
                    }

                    retryCount++;
                    await DelayBeforeRetryAsync(retryCount, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    MarkDisconnected(
                        "ScpiTcp.Cancelled",
                        "SCPI connection was closed because the active transaction was cancelled.",
                        reconnecting: _configuration.Reconnect.Enabled);
                    throw;
                }
            }
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async ValueTask ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectWithRetryUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async ValueTask ConnectWithRetryUnsafeAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                MarkConnected(
                    "ScpiTcp.Connected",
                    $"Connected to SCPI endpoint {_configuration.Host}:{_configuration.Port}.");
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
                        "ScpiTcp.ConnectFailed",
                        "SCPI connection could not be established.",
                        reconnecting: false);
                    throw;
                }

                retryCount++;
                MarkDisconnected(
                    "ScpiTcp.Reconnecting",
                    $"SCPI connection attempt failed; retry {retryCount} is scheduled.",
                    reconnecting: true);
                await DelayBeforeRetryAsync(retryCount, cancellationToken)
                    .ConfigureAwait(false);
            }
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

    private void AddHistory(PluginDeviceSignalSample sample)
    {
        lock (_historyGate)
        {
            _history.Add(sample);
            if (_history.Count > 1_024)
            {
                _history.RemoveRange(0, _history.Count - 1_024);
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

    private static T DeserializeRequired<T>(string? payload, string operation)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidDataException(
                $"SCPI operation '{operation}' requires a JSON payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(
                    payload,
                    DeviceSessionJournal.JsonOptions)
                ?? throw new InvalidDataException(
                    $"SCPI operation '{operation}' payload cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"SCPI operation '{operation}' payload is invalid.",
                exception);
        }
    }

    private sealed record ScpiCommandPayload(string Command);

    private sealed record ScpiQueryResponse(string? Response);

    private sealed record ScpiWriteResponse(int WrittenSignalCount);

    private sealed record ScpiSignalWritePayload(
        string SignalId,
        PluginDeviceValueType ValueType,
        string CanonicalValue,
        string? Unit);
}

internal sealed record JournalInvokeRequest(
    string Operation,
    string? InputPayload);
