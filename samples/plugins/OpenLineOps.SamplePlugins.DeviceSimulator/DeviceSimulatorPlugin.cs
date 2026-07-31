using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.SamplePlugins.DeviceSimulator;

public sealed class DeviceSimulatorPlugin : IOpenLineOpsDeviceSessionPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ConcurrentDictionary<string, SimulatorSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public DeviceSimulatorPlugin()
        : this(TimeProvider.System)
    {
    }

    internal DeviceSimulatorPlugin(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public PluginManifest Manifest { get; } = new(
        Id: "openlineops.samples.device-simulator",
        Name: "Device Session Simulator",
        Version: "0.1.0",
        Kind: PluginKind.DeviceDriver,
        EntryAssembly: "OpenLineOps.SamplePlugins.DeviceSimulator.dll",
        EntryType: typeof(DeviceSimulatorPlugin).FullName!,
        Capabilities: ["device.simulator"],
        DeviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.simulator:increment",
                "device.simulator",
                "Increment",
                "application/json",
                "application/json",
                5_000),
            new PluginDeviceCommandDefinition(
                "device.simulator:replay",
                "device.simulator",
                "Replay",
                "application/json",
                "application/json",
                30_000),
            new PluginDeviceCommandDefinition(
                "device.simulator:inject-fault",
                "device.simulator",
                "InjectFault",
                "application/json",
                "application/json",
                5_000),
            new PluginDeviceCommandDefinition(
                "device.simulator:clear-fault",
                "device.simulator",
                "ClearFault",
                "application/json",
                "application/json",
                5_000)
        ]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginDeviceSession> OpenAsync(
        PluginDeviceSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ParseConfiguration(request.ConfigurationPayload);
        var openedAtUtc = _timeProvider.GetUtcNow();
        var sessionContract = new PluginDeviceSession(
            $"simulator:{request.DeviceInstanceId}:{Guid.NewGuid():N}",
            request.DeviceInstanceId,
            openedAtUtc,
            TimeSpan.FromMilliseconds(configuration.HeartbeatMilliseconds));
        var session = new SimulatorSession(sessionContract, configuration, openedAtUtc);
        if (!_sessions.TryAdd(sessionContract.SessionId, session))
        {
            throw new InvalidOperationException(
                $"Simulator session '{sessionContract.SessionId}' already exists.");
        }

        return ValueTask.FromResult(sessionContract);
    }

    public ValueTask CloseAsync(
        PluginDeviceSessionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryRemove(request.SessionId, out var session))
        {
            session.CompleteSubscriptions();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (session.SyncRoot)
        {
            var samples = new List<PluginDeviceSignalSample>(request.SignalIds.Count);
            foreach (var signalId in request.SignalIds)
            {
                if (!session.Signals.TryGetValue(signalId, out var signal))
                {
                    throw new KeyNotFoundException(
                        $"Simulator signal '{signalId}' is not configured.");
                }

                samples.Add(ToSample(session, signal));
            }

            RecordDiagnostic(
                session,
                "Simulator.Read",
                PluginDeviceDiagnosticSeverity.Information,
                $"Read {samples.Count} signal(s).");
            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceSignalSample>>(samples);
        }
    }

    public async ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            return PluginDeviceOperationResult.Rejected(_timeProvider.GetUtcNow(), error);
        }

        var fingerprint = JsonSerializer.Serialize(request.Writes, JsonOptions);
        return await ExecuteCommandAsync(
                session,
                request.Command,
                $"Write:{fingerprint}",
                () => WriteCore(session, request.Writes),
                allowWhenFaulted: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var subscription = new SimulatorSubscription(
            request.SubscriptionId,
            request.SignalIds.ToHashSet(StringComparer.Ordinal));
        PluginDeviceSignalSample[] replay;
        lock (session.SyncRoot)
        {
            if (!session.Subscriptions.TryAdd(request.SubscriptionId, subscription))
            {
                throw new InvalidOperationException(
                    $"Simulator subscription '{request.SubscriptionId}' is already active.");
            }

            var cutoff = session.Sequence;
            replay = session.History
                .Where(sample => sample.Sequence > (request.ResumeAfterSequence ?? 0)
                    && sample.Sequence <= cutoff
                    && subscription.SignalIds.Contains(sample.SignalId))
                .ToArray();
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

            await foreach (var sample in subscription.Channel.Reader.ReadAllAsync(cancellationToken)
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
            session.Subscriptions.TryRemove(request.SubscriptionId, out _);
            subscription.Channel.Writer.TryComplete();
        }
    }

    public async ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            return PluginDeviceOperationResult.Rejected(_timeProvider.GetUtcNow(), error);
        }

        return await ExecuteCommandAsync(
                session,
                request.Command,
                $"Invoke:{request.Operation}:{request.InputPayload}",
                () => InvokeCore(session, request.Operation, request.InputPayload),
                allowWhenFaulted: string.Equals(
                    request.Operation,
                    "ClearFault",
                    StringComparison.Ordinal),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<PluginDeviceHealthSnapshot> GetHealthAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            return ValueTask.FromResult(new PluginDeviceHealthSnapshot(
                request.SessionId,
                PluginDeviceHealthStatus.Unhealthy,
                now,
                details: error));
        }

        lock (session.SyncRoot)
        {
            return ValueTask.FromResult(new PluginDeviceHealthSnapshot(
                request.SessionId,
                session.InjectedFault is null
                    ? PluginDeviceHealthStatus.Healthy
                    : PluginDeviceHealthStatus.Unhealthy,
                now,
                now,
                session.InjectedFault));
        }
    }

    public ValueTask<PluginDeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSession(request.SessionId, out var session, out var error))
        {
            return ValueTask.FromResult(new PluginDeviceDiagnosticsSnapshot(
                request.SessionId,
                _timeProvider.GetUtcNow(),
                [
                    new PluginDeviceDiagnosticEntry(
                        "Simulator.SessionNotOpen",
                        PluginDeviceDiagnosticSeverity.Error,
                        error,
                        _timeProvider.GetUtcNow())
                ]));
        }

        lock (session.SyncRoot)
        {
            return ValueTask.FromResult(new PluginDeviceDiagnosticsSnapshot(
                request.SessionId,
                _timeProvider.GetUtcNow(),
                session.Diagnostics.ToArray()));
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.CompleteSubscriptions();
        }

        _sessions.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task<PluginDeviceOperationResult> ExecuteCommandAsync(
        SimulatorSession session,
        PluginDeviceCommandEnvelope command,
        string fingerprint,
        Func<PluginDeviceOperationResult> execute,
        bool allowWhenFaulted,
        CancellationToken cancellationToken)
    {
        if (command.SafetyClass == PluginDeviceCommandSafetyClass.SafetyCritical)
        {
            return PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                "The simulator cannot execute safety-critical actions.");
        }

        Task<PluginDeviceOperationResult> task;
        lock (session.SyncRoot)
        {
            if (session.Commands.TryGetValue(command.CommandId, out var existing))
            {
                if (existing.Fingerprint != fingerprint)
                {
                    return PluginDeviceOperationResult.Rejected(
                        _timeProvider.GetUtcNow(),
                        $"Command id '{command.CommandId}' was reused with different evidence.");
                }

                task = existing.Result;
            }
            else
            {
                if (command.FencingToken < session.HighestFencingToken)
                {
                    return PluginDeviceOperationResult.Rejected(
                        _timeProvider.GetUtcNow(),
                        $"Fencing token {command.FencingToken} is stale.");
                }

                session.HighestFencingToken = command.FencingToken;
                task = ExecuteCommandCoreAsync(
                    session,
                    command,
                    execute,
                    allowWhenFaulted,
                    cancellationToken);
                session.Commands.Add(
                    command.CommandId,
                    new RecordedCommand(fingerprint, task));
            }
        }

        return await task.ConfigureAwait(false);
    }

    private async Task<PluginDeviceOperationResult> ExecuteCommandCoreAsync(
        SimulatorSession session,
        PluginDeviceCommandEnvelope command,
        Func<PluginDeviceOperationResult> execute,
        bool allowWhenFaulted,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (command.DeadlineUtc <= now)
        {
            return PluginDeviceOperationResult.TimedOut(
                now,
                "Simulator command deadline elapsed before execution.");
        }

        using var deadline = new CancellationTokenSource(
            command.DeadlineUtc - now,
            _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            cancellationToken);
        try
        {
            if (session.Configuration.OperationLatencyMilliseconds > 0)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            session.Configuration.OperationLatencyMilliseconds),
                        _timeProvider,
                        linked.Token)
                    .ConfigureAwait(false);
            }

            lock (session.SyncRoot)
            {
                if (!allowWhenFaulted)
                {
                    session.OperationCount = checked(session.OperationCount + 1);
                    if (session.Configuration.FailAfterOperations is { } limit
                        && session.OperationCount > limit)
                    {
                        session.InjectedFault ??=
                            $"Configured fault after {limit} successful operation(s).";
                    }
                }

                if (session.InjectedFault is not null && !allowWhenFaulted)
                {
                    RecordDiagnostic(
                        session,
                        "Simulator.InjectedFault",
                        PluginDeviceDiagnosticSeverity.Error,
                        session.InjectedFault);
                    return PluginDeviceOperationResult.Failed(
                        _timeProvider.GetUtcNow(),
                        session.InjectedFault);
                }

                return execute();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return PluginDeviceOperationResult.TimedOut(
                _timeProvider.GetUtcNow(),
                "Simulator command exceeded its deadline.");
        }
        catch (OperationCanceledException)
        {
            return PluginDeviceOperationResult.Failed(
                _timeProvider.GetUtcNow(),
                "Simulator command completion is unknown after cancellation.");
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or JsonException
                                           or OverflowException)
        {
            return PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                exception.Message);
        }
    }

    private PluginDeviceOperationResult WriteCore(
        SimulatorSession session,
        IReadOnlyList<PluginDeviceSignalWrite> writes)
    {
        foreach (var write in writes)
        {
            if (!session.Signals.TryGetValue(write.SignalId, out var signal))
            {
                return PluginDeviceOperationResult.Rejected(
                    _timeProvider.GetUtcNow(),
                    $"Simulator signal '{write.SignalId}' is not configured.");
            }

            if (signal.Value.Type != write.Value.Type)
            {
                return PluginDeviceOperationResult.Rejected(
                    _timeProvider.GetUtcNow(),
                    $"Simulator signal '{write.SignalId}' requires {signal.Value.Type}, not {write.Value.Type}.");
            }
        }

        foreach (var write in writes)
        {
            var signal = session.Signals[write.SignalId];
            signal.Value = write.Value;
            signal.Unit = write.Unit ?? signal.Unit;
            signal.SourceTimestampUtc = _timeProvider.GetUtcNow();
            Publish(session, signal);
        }

        RecordDiagnostic(
            session,
            "Simulator.Write",
            PluginDeviceDiagnosticSeverity.Information,
            $"Wrote {writes.Count} signal(s).");
        return PluginDeviceOperationResult.Completed(
            _timeProvider.GetUtcNow(),
            JsonSerializer.Serialize(
                new { writtenSignalCount = writes.Count },
                JsonOptions));
    }

    private PluginDeviceOperationResult InvokeCore(
        SimulatorSession session,
        string operation,
        string? inputPayload)
    {
        return operation switch
        {
            "Increment" => Increment(session, inputPayload),
            "Replay" => Replay(session, inputPayload),
            "InjectFault" => InjectFault(session, inputPayload),
            "ClearFault" => ClearFault(session),
            _ => PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                $"Simulator operation '{operation}' is not supported.")
        };
    }

    private PluginDeviceOperationResult Increment(
        SimulatorSession session,
        string? inputPayload)
    {
        var request = DeserializeRequired<IncrementRequest>(inputPayload, "Increment");
        if (!session.Signals.TryGetValue(request.SignalId, out var signal)
            || signal.Value.Type != PluginDeviceValueType.SignedInteger
            || !long.TryParse(
                signal.Value.CanonicalValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var current))
        {
            return PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                $"Simulator signal '{request.SignalId}' is not a signed integer.");
        }

        var next = checked(current + request.Delta);
        signal.Value = PluginDeviceValue.FromInt64(next);
        signal.SourceTimestampUtc = _timeProvider.GetUtcNow();
        Publish(session, signal);
        return PluginDeviceOperationResult.Completed(
            _timeProvider.GetUtcNow(),
            JsonSerializer.Serialize(new { value = next }, JsonOptions));
    }

    private PluginDeviceOperationResult Replay(
        SimulatorSession session,
        string? inputPayload)
    {
        var request = DeserializeRequired<ReplayRequest>(inputPayload, "Replay");
        if (request.Samples is null || request.Samples.Length == 0)
        {
            throw new InvalidDataException("Replay requires at least one sample.");
        }

        foreach (var replay in request.Samples)
        {
            if (!session.Signals.TryGetValue(replay.SignalId, out var signal))
            {
                throw new InvalidDataException(
                    $"Replay signal '{replay.SignalId}' is not configured.");
            }

            var value = new PluginDeviceValue(replay.Type, replay.CanonicalValue);
            if (signal.Value.Type != value.Type)
            {
                throw new InvalidDataException(
                    $"Replay signal '{replay.SignalId}' type does not match its configuration.");
            }

            signal.Value = value;
            signal.Unit = replay.Unit ?? signal.Unit;
            signal.SourceTimestampUtc = _timeProvider.GetUtcNow();
            Publish(session, signal);
        }

        RecordDiagnostic(
            session,
            "Simulator.Replay",
            PluginDeviceDiagnosticSeverity.Information,
            $"Replayed {request.Samples.Length} signal sample(s).");
        return PluginDeviceOperationResult.Completed(
            _timeProvider.GetUtcNow(),
            JsonSerializer.Serialize(
                new { replayedSampleCount = request.Samples.Length },
                JsonOptions));
    }

    private PluginDeviceOperationResult InjectFault(
        SimulatorSession session,
        string? inputPayload)
    {
        var request = DeserializeRequired<InjectFaultRequest>(inputPayload, "InjectFault");
        session.InjectedFault = string.IsNullOrWhiteSpace(request.Reason)
            ? throw new InvalidDataException("Injected fault reason is required.")
            : request.Reason.Trim();
        RecordDiagnostic(
            session,
            "Simulator.InjectedFault",
            PluginDeviceDiagnosticSeverity.Error,
            session.InjectedFault);
        return PluginDeviceOperationResult.Completed(_timeProvider.GetUtcNow());
    }

    private PluginDeviceOperationResult ClearFault(SimulatorSession session)
    {
        session.InjectedFault = null;
        RecordDiagnostic(
            session,
            "Simulator.FaultCleared",
            PluginDeviceDiagnosticSeverity.Information,
            "Injected simulator fault cleared.");
        return PluginDeviceOperationResult.Completed(_timeProvider.GetUtcNow());
    }

    private void Publish(SimulatorSession session, SimulatorSignal signal)
    {
        session.Sequence = checked(session.Sequence + 1);
        signal.Sequence = session.Sequence;
        var sample = ToSample(session, signal);
        session.History.Add(sample);
        if (session.History.Count > 10_000)
        {
            session.History.RemoveAt(0);
        }

        foreach (var subscription in session.Subscriptions.Values)
        {
            if (subscription.SignalIds.Contains(signal.SignalId))
            {
                subscription.Channel.Writer.TryWrite(sample);
            }
        }
    }

    private PluginDeviceSignalSample ToSample(
        SimulatorSession session,
        SimulatorSignal signal) =>
        new(
            signal.SignalId,
            signal.Value,
            signal.Unit,
            session.InjectedFault is null
                ? PluginDeviceSignalQuality.Good
                : PluginDeviceSignalQuality.Bad,
            signal.SourceTimestampUtc,
            _timeProvider.GetUtcNow(),
            signal.Sequence,
            session.InjectedFault is null ? null : "Simulator.InjectedFault");

    private void RecordDiagnostic(
        SimulatorSession session,
        string code,
        PluginDeviceDiagnosticSeverity severity,
        string message)
    {
        session.Diagnostics.Add(new PluginDeviceDiagnosticEntry(
            code,
            severity,
            message,
            _timeProvider.GetUtcNow()));
        if (session.Diagnostics.Count > 256)
        {
            session.Diagnostics.RemoveAt(0);
        }
    }

    private bool TryGetSession(
        string sessionId,
        out SimulatorSession session,
        out string error)
    {
        if (_sessions.TryGetValue(sessionId, out session!))
        {
            error = string.Empty;
            return true;
        }

        error = $"Simulator session '{sessionId}' is not open.";
        return false;
    }

    private static SimulatorConfiguration ParseConfiguration(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SimulatorConfiguration.Default();
        }

        var configuration = JsonSerializer.Deserialize<SimulatorConfiguration>(
            payload,
            JsonOptions)
            ?? throw new InvalidDataException("Simulator configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    private static T DeserializeRequired<T>(string? payload, string operation)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidDataException($"{operation} payload is required.");
        }

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
               ?? throw new InvalidDataException($"{operation} payload is empty.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private sealed class SimulatorSession
    {
        public SimulatorSession(
            PluginDeviceSession session,
            SimulatorConfiguration configuration,
            DateTimeOffset openedAtUtc)
        {
            Session = session;
            Configuration = configuration;
            Signals = configuration.Signals.ToDictionary(
                signal => signal.SignalId,
                signal => new SimulatorSignal(
                    signal.SignalId,
                    new PluginDeviceValue(signal.Type, signal.CanonicalValue),
                    signal.Unit,
                    openedAtUtc),
                StringComparer.Ordinal);
        }

        public object SyncRoot { get; } = new();

        public PluginDeviceSession Session { get; }

        public SimulatorConfiguration Configuration { get; }

        public Dictionary<string, SimulatorSignal> Signals { get; }

        public Dictionary<string, RecordedCommand> Commands { get; } =
            new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, SimulatorSubscription> Subscriptions { get; } =
            new(StringComparer.Ordinal);

        public List<PluginDeviceSignalSample> History { get; } = [];

        public List<PluginDeviceDiagnosticEntry> Diagnostics { get; } = [];

        public long HighestFencingToken { get; set; }

        public long Sequence { get; set; }

        public int OperationCount { get; set; }

        public string? InjectedFault { get; set; }

        public void CompleteSubscriptions()
        {
            foreach (var subscription in Subscriptions.Values)
            {
                subscription.Channel.Writer.TryComplete();
            }

            Subscriptions.Clear();
        }
    }

    private sealed class SimulatorSignal(
        string signalId,
        PluginDeviceValue value,
        string? unit,
        DateTimeOffset sourceTimestampUtc)
    {
        public string SignalId { get; } = signalId;

        public PluginDeviceValue Value { get; set; } = value;

        public string? Unit { get; set; } = unit;

        public DateTimeOffset SourceTimestampUtc { get; set; } = sourceTimestampUtc;

        public long Sequence { get; set; }
    }

    private sealed class SimulatorSubscription(
        string subscriptionId,
        HashSet<string> signalIds)
    {
        public string SubscriptionId { get; } = subscriptionId;

        public HashSet<string> SignalIds { get; } = signalIds;

        public Channel<PluginDeviceSignalSample> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<PluginDeviceSignalSample>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
    }

    private sealed record RecordedCommand(
        string Fingerprint,
        Task<PluginDeviceOperationResult> Result);

    private sealed record IncrementRequest(string SignalId, long Delta);

    private sealed record InjectFaultRequest(string Reason);

    private sealed record ReplayRequest(ReplaySample[] Samples);

    private sealed record ReplaySample(
        string SignalId,
        PluginDeviceValueType Type,
        string CanonicalValue,
        string? Unit);

    private sealed record SimulatorConfiguration(
        int HeartbeatMilliseconds,
        int OperationLatencyMilliseconds,
        int? FailAfterOperations,
        SimulatorSignalConfiguration[] Signals)
    {
        public static SimulatorConfiguration Default() =>
            new(
                HeartbeatMilliseconds: 1_000,
                OperationLatencyMilliseconds: 0,
                FailAfterOperations: null,
                Signals:
                [
                    new SimulatorSignalConfiguration(
                        "sim.counter",
                        PluginDeviceValueType.SignedInteger,
                        "0",
                        "count"),
                    new SimulatorSignalConfiguration(
                        "sim.ready",
                        PluginDeviceValueType.Boolean,
                        "true",
                        null),
                    new SimulatorSignalConfiguration(
                        "sim.measurement",
                        PluginDeviceValueType.FloatingPoint,
                        "0",
                        "V")
                ]);

        public void Validate()
        {
            if (HeartbeatMilliseconds <= 0
                || OperationLatencyMilliseconds < 0
                || FailAfterOperations is < 0
                || Signals is null
                || Signals.Length == 0
                || Signals.Select(signal => signal.SignalId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != Signals.Length)
            {
                throw new InvalidDataException("Simulator configuration is invalid.");
            }

            foreach (var signal in Signals)
            {
                _ = new PluginDeviceSignalWrite(
                    signal.SignalId,
                    new PluginDeviceValue(signal.Type, signal.CanonicalValue),
                    signal.Unit);
            }
        }
    }

    private sealed record SimulatorSignalConfiguration(
        string SignalId,
        PluginDeviceValueType Type,
        string CanonicalValue,
        string? Unit);
}
