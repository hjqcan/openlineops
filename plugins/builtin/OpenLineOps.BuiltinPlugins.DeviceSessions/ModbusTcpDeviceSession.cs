using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class ModbusTcpDeviceSession : DeviceSessionBase
{
    private const byte ReadCoilsFunction = 0x01;
    private const byte ReadDiscreteInputsFunction = 0x02;
    private const byte ReadHoldingRegistersFunction = 0x03;
    private const byte ReadInputRegistersFunction = 0x04;
    private const byte WriteSingleCoilFunction = 0x05;
    private const byte WriteSingleRegisterFunction = 0x06;
    private const byte WriteMultipleCoilsFunction = 0x0F;
    private const byte WriteMultipleRegistersFunction = 0x10;
    private const int MaximumMultipleCoils = 1_968;
    private const int MaximumMultipleRegisters = 123;
    private readonly ModbusTcpConfiguration _configuration;
    private readonly Dictionary<string, ModbusSignalConfiguration> _signals;
    private readonly ModbusTcpTransport _transport;
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly object _historyGate = new();
    private readonly List<PluginDeviceSignalSample> _history = [];
    private long _sequence;
    private ushort _transactionId;

    private ModbusTcpDeviceSession(
        PluginDeviceSession contract,
        ModbusTcpConfiguration configuration,
        DeviceSessionJournalWriter? journal,
        TimeProvider timeProvider)
        : base(contract, timeProvider, journal)
    {
        _configuration = configuration;
        _signals = configuration.Signals.ToDictionary(
            static signal => signal.SignalId,
            StringComparer.Ordinal);
        _transport = new ModbusTcpTransport(
            configuration.Host,
            configuration.Port,
            configuration.ConnectTimeoutMilliseconds);
        _sequence = RestoredDeviceSequence;
    }

    public static async ValueTask<IDeviceSession> OpenAsync(
        string deviceInstanceId,
        ModbusTcpConfiguration configuration,
        DeviceSessionJournalWriter? journal,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var contract = new PluginDeviceSession(
            $"modbus-tcp:{deviceInstanceId}:{Guid.NewGuid():N}",
            deviceInstanceId,
            timeProvider.GetUtcNow(),
            TimeSpan.FromMilliseconds(configuration.HeartbeatMilliseconds));
        var session = new ModbusTcpDeviceSession(
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
        var definitions = request.SignalIds
            .Select(GetRequiredSignal)
            .ToArray();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_configuration.OperationTimeoutMilliseconds),
            TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            Lifetime.Token,
            timeout.Token);
        var samples = new List<PluginDeviceSignalSample>(definitions.Length);
        try
        {
            foreach (var definition in definitions)
            {
                var sample = await ReadSignalAsync(definition, linked.Token)
                    .ConfigureAwait(false);
                AddHistory(sample);
                await RecordSignalAsync(sample, linked.Token).ConfigureAwait(false);
                samples.Add(sample);
            }

            return samples.AsReadOnly();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !cancellationToken
                                                     .IsCancellationRequested)
        {
            throw new TimeoutException(
                "Modbus TCP signal read exceeded the configured operation timeout.");
        }
        catch (ModbusDeviceException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    public override ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        ModbusWritePlan plan;
        try
        {
            plan = CreateWritePlan(request.Writes);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or ArgumentException
                                           or FormatException
                                           or OverflowException
                                           or KeyNotFoundException)
        {
            return ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                TimeProvider.GetUtcNow(),
                exception.Message));
        }

        var requestPayload = DeviceSessionJournal.SerializePayload(
            new ModbusWriteRequestEvidence(
                _configuration.UnitId,
                plan.Values.Select(static value => new ModbusWriteEvidence(
                    value.SignalId,
                    value.Kind,
                    value.Address,
                    value.Value.Type,
                    value.Value.CanonicalValue,
                    value.Unit)).ToArray()));
        return ExecuteCommandAsync(
            request.Command,
            "Write",
            requestPayload,
            async token =>
            {
                try
                {
                    var function = await ExecuteWritePlanAsync(
                            plan,
                            request.Command.IdempotencyClass,
                            token)
                        .ConfigureAwait(false);
                    return PluginDeviceOperationResult.Completed(
                        TimeProvider.GetUtcNow(),
                        DeviceSessionJournal.SerializePayload(
                            new ModbusWriteResponse(
                                plan.Values.Count,
                                $"0x{function:X2}")));
                }
                catch (ModbusDeviceException exception)
                {
                    return PluginDeviceOperationResult.Rejected(
                        TimeProvider.GetUtcNow(),
                        exception.Message);
                }
            },
            cancellationToken);
    }

    public override async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        foreach (var signalId in request.SignalIds)
        {
            _ = GetRequiredSignal(signalId);
        }

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
            async token =>
            {
                if (!string.Equals(
                        request.Operation,
                        "Reconnect",
                        StringComparison.Ordinal))
                {
                    return PluginDeviceOperationResult.Rejected(
                        TimeProvider.GetUtcNow(),
                        $"Modbus TCP operation '{request.Operation}' is not supported.");
                }

                await _transportGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    await ConnectWithRetryUnsafeAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    _transportGate.Release();
                }

                return PluginDeviceOperationResult.Completed(
                    TimeProvider.GetUtcNow(),
                    """{"connected":true}""");
            },
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

    private async ValueTask<PluginDeviceSignalSample> ReadSignalAsync(
        ModbusSignalConfiguration definition,
        CancellationToken cancellationToken)
    {
        var function = ReadFunction(definition.Kind);
        var requestPdu = new byte[5];
        requestPdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(
            requestPdu.AsSpan(1, 2),
            checked((ushort)definition.Address));
        BinaryPrimitives.WriteUInt16BigEndian(requestPdu.AsSpan(3, 2), 1);

        var value = await ExecuteTransportTransactionAsync(
                requestPdu,
                PluginDeviceCommandIdempotencyClass.Idempotent,
                response => ParseReadResponse(function, definition.Kind, response),
                cancellationToken)
            .ConfigureAwait(false);
        var sourceTimestamp = TimeProvider.GetUtcNow();
        return new PluginDeviceSignalSample(
            definition.SignalId,
            value,
            definition.Unit,
            PluginDeviceSignalQuality.Good,
            sourceTimestamp,
            TimeProvider.GetUtcNow(),
            Interlocked.Increment(ref _sequence));
    }

    private async ValueTask<byte> ExecuteWritePlanAsync(
        ModbusWritePlan plan,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        CancellationToken cancellationToken)
    {
        var requestPdu = BuildWriteRequestPdu(plan);
        var function = requestPdu[0];
        await ExecuteTransportTransactionAsync(
                requestPdu,
                idempotencyClass,
                response =>
                {
                    ValidateWriteResponse(requestPdu, plan.Values.Count, response);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return function;
    }

    private async ValueTask<T> ExecuteTransportTransactionAsync<T>(
        byte[] requestPdu,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        Func<byte[], T> parseResponse,
        CancellationToken cancellationToken)
    {
        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retryCount = 0;
            while (true)
            {
                var transmissionStarted = false;
                try
                {
                    if (!_transport.IsConnected)
                    {
                        await _transport.ConnectAsync(cancellationToken)
                            .ConfigureAwait(false);
                        MarkConnected(
                            "ModbusTcp.Connected",
                            $"Connected to Modbus TCP endpoint "
                            + $"{_configuration.Host}:{_configuration.Port} "
                            + $"for unit {_configuration.UnitId}.");
                    }

                    var transactionId = NextTransactionId();
                    // Treat entry into ExchangeAsync as possibly transmitted. A socket
                    // failure can occur after a partial frame reached the device.
                    transmissionStarted = true;
                    var response = await _transport.ExchangeAsync(
                            transactionId,
                            checked((byte)_configuration.UnitId),
                            requestPdu,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var parsed = ParseResponseEnvelope(
                        requestPdu[0],
                        response,
                        parseResponse);
                    MarkHeartbeat();
                    return parsed;
                }
                catch (ModbusDeviceException exception)
                {
                    MarkHeartbeat();
                    RecordDiagnostic(
                        "ModbusTcp.DeviceException",
                        PluginDeviceDiagnosticSeverity.Warning,
                        exception.Message,
                        new Dictionary<string, string?>
                        {
                            ["functionCode"] = $"0x{exception.FunctionCode:X2}",
                            ["exceptionCode"] = $"0x{exception.ExceptionCode:X2}",
                            ["unitId"] = _configuration.UnitId.ToString(
                                CultureInfo.InvariantCulture)
                        });
                    throw;
                }
                catch (ModbusProtocolException exception)
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    MarkDisconnected(
                        "ModbusTcp.ProtocolViolation",
                        $"The Modbus TCP peer returned an invalid frame: {exception.Message}",
                        reconnecting: false);
                    if (transmissionStarted && RequiresRecovery(idempotencyClass))
                    {
                        throw CompletionUnknown(exception);
                    }

                    throw;
                }
                catch (Exception exception) when (IsTransientNetworkFailure(exception))
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                    var canRetry = !cancellationToken.IsCancellationRequested
                                   && CanRetry(retryCount)
                                   && (!transmissionStarted
                                       || idempotencyClass
                                       == PluginDeviceCommandIdempotencyClass.Idempotent);
                    MarkDisconnected(
                        "ModbusTcp.Disconnected",
                        canRetry
                            ? $"Modbus TCP connection failed; reconnect attempt {retryCount + 1} is scheduled."
                            : transmissionStarted
                              && idempotencyClass
                              != PluginDeviceCommandIdempotencyClass.Idempotent
                                ? "Modbus TCP connection failed after transmission; "
                                  + "command completion is unknown and the command "
                                  + "will not be resent."
                                : "Modbus TCP connection failed and reconnect is disabled or exhausted.",
                        canRetry);
                    if (!canRetry)
                    {
                        if (transmissionStarted && RequiresRecovery(idempotencyClass))
                        {
                            throw CompletionUnknown(exception);
                        }

                        if (exception is TimeoutException)
                        {
                            throw new IOException(
                                "The Modbus TCP connection attempt timed out.",
                                exception);
                        }

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
                        "ModbusTcp.Cancelled",
                        "The Modbus TCP connection was closed because the active "
                        + "transaction was cancelled or timed out.",
                        reconnecting: _configuration.Reconnect.Enabled);
                    if (transmissionStarted && RequiresRecovery(idempotencyClass))
                    {
                        throw CompletionUnknown(
                            new TimeoutException(
                                "The active Modbus TCP transaction was cancelled or timed out."));
                    }

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

    private async ValueTask ConnectWithRetryUnsafeAsync(
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                MarkConnected(
                    "ModbusTcp.Connected",
                    $"Connected to Modbus TCP endpoint "
                    + $"{_configuration.Host}:{_configuration.Port} "
                    + $"for unit {_configuration.UnitId}.");
                return;
            }
            catch (Exception exception) when (IsTransientNetworkFailure(exception)
                                              || exception
                                              is OperationCanceledException)
            {
                if (exception is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (!CanRetry(retryCount))
                {
                    MarkDisconnected(
                        "ModbusTcp.ConnectFailed",
                        "Modbus TCP connection could not be established.",
                        reconnecting: false);
                    if (exception is TimeoutException)
                    {
                        throw new IOException(
                            "The Modbus TCP connection attempt timed out.",
                            exception);
                    }

                    throw;
                }

                retryCount++;
                MarkDisconnected(
                    "ModbusTcp.Reconnecting",
                    $"Modbus TCP connection attempt failed; retry {retryCount} is scheduled.",
                    reconnecting: true);
                await DelayBeforeRetryAsync(retryCount, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private ModbusWritePlan CreateWritePlan(
        IReadOnlyList<PluginDeviceSignalWrite> writes)
    {
        var values = writes
            .Select(write =>
            {
                var signal = GetRequiredSignal(write.SignalId);
                if (signal.Kind is not (ModbusSignalKinds.Coil
                    or ModbusSignalKinds.HoldingRegister))
                {
                    throw new InvalidDataException(
                        $"Signal '{signal.SignalId}' is read-only.");
                }

                if (!string.Equals(write.Unit, signal.Unit, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Signal '{signal.SignalId}' requires unit '{signal.Unit ?? "<none>"}'.");
                }

                ValidateWriteValue(signal, write.Value);
                return new ModbusWriteValue(
                    signal.SignalId,
                    signal.Kind,
                    signal.Address,
                    write.Value,
                    signal.Unit);
            })
            .OrderBy(static value => value.Address)
            .ToArray();
        var kind = values[0].Kind;
        if (values.Any(value => !string.Equals(value.Kind, kind, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "One Modbus write request must target only coils or only holding registers.");
        }

        for (var index = 1; index < values.Length; index++)
        {
            if (values[index].Address != values[0].Address + index)
            {
                throw new InvalidDataException(
                    "Multiple Modbus writes must target one contiguous address range.");
            }
        }

        var maximum = kind == ModbusSignalKinds.Coil
            ? MaximumMultipleCoils
            : MaximumMultipleRegisters;
        if (values.Length > maximum)
        {
            throw new InvalidDataException(
                $"A Modbus {kind} write cannot contain more than {maximum} values.");
        }

        return new ModbusWritePlan(kind, values);
    }

    private static byte[] BuildWriteRequestPdu(ModbusWritePlan plan)
    {
        var startAddress = checked((ushort)plan.Values[0].Address);
        if (plan.Kind == ModbusSignalKinds.Coil)
        {
            if (plan.Values.Count == 1)
            {
                var singleCoilRequest = new byte[5];
                singleCoilRequest[0] = WriteSingleCoilFunction;
                BinaryPrimitives.WriteUInt16BigEndian(
                    singleCoilRequest.AsSpan(1, 2),
                    startAddress);
                BinaryPrimitives.WriteUInt16BigEndian(
                    singleCoilRequest.AsSpan(3, 2),
                    plan.Values[0].Value.CanonicalValue == "true"
                        ? (ushort)0xFF00
                        : (ushort)0);
                return singleCoilRequest;
            }

            var byteCount = (plan.Values.Count + 7) / 8;
            var multipleCoilsRequest = new byte[6 + byteCount];
            multipleCoilsRequest[0] = WriteMultipleCoilsFunction;
            BinaryPrimitives.WriteUInt16BigEndian(
                multipleCoilsRequest.AsSpan(1, 2),
                startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(
                multipleCoilsRequest.AsSpan(3, 2),
                checked((ushort)plan.Values.Count));
            multipleCoilsRequest[5] = checked((byte)byteCount);
            for (var index = 0; index < plan.Values.Count; index++)
            {
                if (plan.Values[index].Value.CanonicalValue == "true")
                {
                    multipleCoilsRequest[6 + index / 8] |= checked(
                        (byte)(1 << (index % 8)));
                }
            }

            return multipleCoilsRequest;
        }

        if (plan.Values.Count == 1)
        {
            var singleRegisterRequest = new byte[5];
            singleRegisterRequest[0] = WriteSingleRegisterFunction;
            BinaryPrimitives.WriteUInt16BigEndian(
                singleRegisterRequest.AsSpan(1, 2),
                startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(
                singleRegisterRequest.AsSpan(3, 2),
                RegisterValue(plan.Values[0].Value));
            return singleRegisterRequest;
        }

        var registerRequest = new byte[6 + plan.Values.Count * 2];
        registerRequest[0] = WriteMultipleRegistersFunction;
        BinaryPrimitives.WriteUInt16BigEndian(
            registerRequest.AsSpan(1, 2),
            startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(
            registerRequest.AsSpan(3, 2),
            checked((ushort)plan.Values.Count));
        registerRequest[5] = checked((byte)(plan.Values.Count * 2));
        for (var index = 0; index < plan.Values.Count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                registerRequest.AsSpan(6 + index * 2, 2),
                RegisterValue(plan.Values[index].Value));
        }

        return registerRequest;
    }

    private static void ValidateWriteResponse(
        byte[] requestPdu,
        int writtenValueCount,
        byte[] responsePdu)
    {
        if (writtenValueCount == 1)
        {
            if (responsePdu.Length != 5
                || !responsePdu.AsSpan().SequenceEqual(requestPdu))
            {
                throw new ModbusProtocolException(
                    "A single-write response must exactly echo the function, address, and value.");
            }

            return;
        }

        if (responsePdu.Length != 5
            || responsePdu[0] != requestPdu[0]
            || !responsePdu.AsSpan(1, 4).SequenceEqual(requestPdu.AsSpan(1, 4)))
        {
            throw new ModbusProtocolException(
                "A multiple-write response must exactly echo the function, start address, and quantity.");
        }
    }

    private static PluginDeviceValue ParseReadResponse(
        byte function,
        string kind,
        byte[] responsePdu)
    {
        if (kind is ModbusSignalKinds.Coil or ModbusSignalKinds.DiscreteInput)
        {
            if (responsePdu.Length != 3 || responsePdu[1] != 1)
            {
                throw new ModbusProtocolException(
                    $"Function 0x{function:X2} response must contain exactly one data byte.");
            }

            if ((responsePdu[2] & 0xFE) != 0)
            {
                throw new ModbusProtocolException(
                    $"Function 0x{function:X2} response has non-zero padding bits.");
            }

            return PluginDeviceValue.FromBoolean((responsePdu[2] & 0x01) != 0);
        }

        if (responsePdu.Length != 4 || responsePdu[1] != 2)
        {
            throw new ModbusProtocolException(
                $"Function 0x{function:X2} response must contain exactly two data bytes.");
        }

        return PluginDeviceValue.FromInt64(
            BinaryPrimitives.ReadUInt16BigEndian(responsePdu.AsSpan(2, 2)));
    }

    private static T ParseResponseEnvelope<T>(
        byte expectedFunction,
        byte[] responsePdu,
        Func<byte[], T> parseResponse)
    {
        if (responsePdu.Length == 0)
        {
            throw new ModbusProtocolException("A Modbus response PDU cannot be empty.");
        }

        if (responsePdu[0] == (expectedFunction | 0x80))
        {
            if (responsePdu.Length != 2 || responsePdu[1] == 0)
            {
                throw new ModbusProtocolException(
                    "A Modbus exception response must contain one non-zero exception code.");
            }

            throw new ModbusDeviceException(expectedFunction, responsePdu[1]);
        }

        if (responsePdu[0] != expectedFunction)
        {
            throw new ModbusProtocolException(
                $"Response function 0x{responsePdu[0]:X2} does not match request 0x{expectedFunction:X2}.");
        }

        return parseResponse(responsePdu);
    }

    private static byte ReadFunction(string kind) =>
        kind switch
        {
            ModbusSignalKinds.Coil => ReadCoilsFunction,
            ModbusSignalKinds.DiscreteInput => ReadDiscreteInputsFunction,
            ModbusSignalKinds.HoldingRegister => ReadHoldingRegistersFunction,
            ModbusSignalKinds.InputRegister => ReadInputRegistersFunction,
            _ => throw new InvalidDataException(
                $"Modbus signal kind '{kind}' is not supported.")
        };

    private static void ValidateWriteValue(
        ModbusSignalConfiguration signal,
        PluginDeviceValue value)
    {
        if (signal.Kind == ModbusSignalKinds.Coil)
        {
            if (value.Type != PluginDeviceValueType.Boolean)
            {
                throw new InvalidDataException(
                    $"Coil signal '{signal.SignalId}' requires a Boolean value.");
            }

            return;
        }

        if (value.Type != PluginDeviceValueType.SignedInteger
            || !long.TryParse(
                value.CanonicalValue,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var register)
            || register is < ushort.MinValue or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"Holding-register signal '{signal.SignalId}' requires an integer from 0 through 65535.");
        }
    }

    private static ushort RegisterValue(PluginDeviceValue value) =>
        checked((ushort)long.Parse(
            value.CanonicalValue,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture));

    private ModbusSignalConfiguration GetRequiredSignal(string signalId) =>
        _signals.TryGetValue(signalId, out var signal)
            ? signal
            : throw new KeyNotFoundException(
                $"Modbus signal '{signalId}' is not configured.");

    private void AddHistory(PluginDeviceSignalSample sample)
    {
        lock (_historyGate)
        {
            _history.Add(sample);
            if (_history.Count > _configuration.HistoryCapacity)
            {
                _history.RemoveRange(
                    0,
                    _history.Count - _configuration.HistoryCapacity);
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

    private ushort NextTransactionId()
    {
        _transactionId = _transactionId == ushort.MaxValue
            ? (ushort)0
            : checked((ushort)(_transactionId + 1));
        return _transactionId;
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

    private static bool IsTransientNetworkFailure(Exception exception) =>
        IsNetworkFailure(exception) || exception is TimeoutException;

    private static bool RequiresRecovery(
        PluginDeviceCommandIdempotencyClass idempotencyClass) =>
        idempotencyClass != PluginDeviceCommandIdempotencyClass.Idempotent;

    private static DeviceCommandCompletionUnknownException CompletionUnknown(
        Exception exception) =>
        new(
            "Modbus TCP command completion is unknown after transmission; "
            + "RecoveryRequired and automatic resend is prohibited.",
            exception);

    private sealed record ModbusWritePlan(
        string Kind,
        IReadOnlyList<ModbusWriteValue> Values);

    private sealed record ModbusWriteValue(
        string SignalId,
        string Kind,
        int Address,
        PluginDeviceValue Value,
        string? Unit);

    private sealed record ModbusWriteEvidence(
        string SignalId,
        string Kind,
        int Address,
        PluginDeviceValueType ValueType,
        string CanonicalValue,
        string? Unit);

    private sealed record ModbusWriteRequestEvidence(
        int UnitId,
        IReadOnlyList<ModbusWriteEvidence> Values);

    private sealed record ModbusWriteResponse(
        int WrittenSignalCount,
        string FunctionCode);
}
