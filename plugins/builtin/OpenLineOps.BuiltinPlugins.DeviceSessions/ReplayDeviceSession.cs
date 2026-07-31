using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class ReplayDeviceSession : DeviceSessionBase
{
    private readonly ReplayConfiguration _configuration;
    private readonly IReadOnlyList<ReplaySignal> _signals;
    private readonly IReadOnlyList<ReplayCommand> _commands;
    private readonly HashSet<string> _knownSignalIds;
    private readonly object _signalGate = new();
    private readonly object _commandGate = new();
    private readonly List<PluginDeviceSignalSample> _history = [];
    private readonly Dictionary<string, ReplaySubscription> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginDeviceSignalSample> _latest =
        new(StringComparer.Ordinal);
    private Task? _playbackTask;
    private int _commandIndex;
    private bool _playbackCompleted;

    protected override bool AllowsNonDurableNonIdempotentCommands => true;

    private ReplayDeviceSession(
        PluginDeviceSession contract,
        ReplayConfiguration configuration,
        IReadOnlyList<ReplaySignal> signals,
        IReadOnlyList<ReplayCommand> commands,
        TimeProvider timeProvider)
        : base(contract, timeProvider, journal: null)
    {
        _configuration = configuration;
        _signals = signals;
        _commands = commands;
        _knownSignalIds = signals
            .Select(static signal => signal.Sample.SignalId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static async ValueTask<IDeviceSession> OpenAsync(
        string deviceInstanceId,
        ReplayConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entries = await DeviceSessionJournal
            .ReadAndVerifyAsync(configuration.JournalPath, cancellationToken)
            .ConfigureAwait(false);
        var signals = ParseSignals(entries);
        var knownSignalIds = signals
            .Select(static signal => signal.Sample.SignalId)
            .ToHashSet(StringComparer.Ordinal);
        var unknownBadQualitySignal = configuration.BadQualitySignalIds
            .FirstOrDefault(signalId => !knownSignalIds.Contains(signalId));
        if (unknownBadQualitySignal is not null)
        {
            throw new InvalidDataException(
                $"Bad-quality fault injection references unknown signal '{unknownBadQualitySignal}'.");
        }

        if (configuration.DisconnectAtJournalSequence is { } disconnectSequence
            && !signals.Any(signal => signal.JournalSequence >= disconnectSequence))
        {
            throw new InvalidDataException(
                $"Disconnect fault injection sequence {disconnectSequence} does not precede a replayable signal.");
        }

        var commands = ParseCommands(entries);
        var contract = new PluginDeviceSession(
            $"device-replay:{deviceInstanceId}:{Guid.NewGuid():N}",
            deviceInstanceId,
            timeProvider.GetUtcNow(),
            TimeSpan.FromMilliseconds(configuration.HeartbeatMilliseconds));
        var session = new ReplayDeviceSession(
            contract,
            configuration,
            signals,
            commands,
            timeProvider);
        session.MarkConnected(
            "DeviceReplay.Opened",
            $"Verified {entries.Count} journal event(s) before replay.");
        session._playbackTask = session.PlaybackAsync();
        return session;
    }

    public override ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_signalGate)
        {
            var samples = new List<PluginDeviceSignalSample>(request.SignalIds.Count);
            foreach (var signalId in request.SignalIds)
            {
                if (!_knownSignalIds.Contains(signalId))
                {
                    throw new KeyNotFoundException(
                        $"Replay journal does not contain signal '{signalId}'.");
                }

                if (_latest.TryGetValue(signalId, out var sample))
                {
                    samples.Add(sample);
                }
            }

            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceSignalSample>>(
                samples.AsReadOnly());
        }
    }

    public override ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        var requestPayload = DeviceSessionJournal.SerializePayload(
            request.Writes.Select(static write => new ReplayWritePayload(
                write.SignalId,
                write.Value.Type,
                write.Value.CanonicalValue,
                write.Unit)).ToArray());
        var requestFingerprint = DeviceCommandEvidence.RequestFingerprint(
            "Write",
            requestPayload);
        return ExecuteCommandAsync(
            request.Command,
            "Write",
            requestPayload,
            _ => ValueTask.FromResult(ReplayRecordedCommand(
                "Write",
                requestFingerprint)),
            cancellationToken);
    }

    public override async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateSession(request.SessionId);
        foreach (var signalId in request.SignalIds)
        {
            if (!_knownSignalIds.Contains(signalId))
            {
                throw new KeyNotFoundException(
                    $"Replay journal does not contain signal '{signalId}'.");
            }
        }

        var subscription = new ReplaySubscription(
            request.SubscriptionId,
            request.SignalIds.ToHashSet(StringComparer.Ordinal));
        PluginDeviceSignalSample[] replay;
        lock (_signalGate)
        {
            if (_subscriptions.ContainsKey(request.SubscriptionId))
            {
                throw new InvalidOperationException(
                    $"Replay subscription '{request.SubscriptionId}' is already active.");
            }

            var cutoff = _history.Count == 0 ? 0 : _history[^1].Sequence;
            replay = _history
                .Where(sample => sample.Sequence > (request.ResumeAfterSequence ?? 0)
                                 && sample.Sequence <= cutoff
                                 && subscription.SignalIds.Contains(sample.SignalId))
                .ToArray();
            _subscriptions.Add(request.SubscriptionId, subscription);
            if (_playbackCompleted)
            {
                subscription.Channel.Writer.TryComplete();
            }
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
        var requestFingerprint = DeviceCommandEvidence.RequestFingerprint(
            "Invoke",
            requestPayload);
        return ExecuteCommandAsync(
            request.Command,
            "Invoke",
            requestPayload,
            _ => ValueTask.FromResult(ReplayRecordedCommand(
                "Invoke",
                requestFingerprint)),
            cancellationToken);
    }

    protected override async ValueTask DisposeSessionAsync()
    {
        if (_playbackTask is not null)
        {
            try
            {
                await _playbackTask.ConfigureAwait(false);
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
    }

    private async Task PlaybackAsync()
    {
        DateTimeOffset? previousRecordedAt = null;
        var injectedDisconnect = false;
        try
        {
            foreach (var signal in _signals)
            {
                Lifetime.Token.ThrowIfCancellationRequested();
                if (!injectedDisconnect
                    && _configuration.DisconnectAtJournalSequence is { } sequence
                    && signal.JournalSequence >= sequence)
                {
                    injectedDisconnect = true;
                    MarkDisconnected(
                        "DeviceReplay.InjectedDisconnect",
                        $"Injected disconnect at journal sequence {signal.JournalSequence}.",
                        reconnecting: true);
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                _configuration.DisconnectDurationMilliseconds),
                            TimeProvider,
                            Lifetime.Token)
                        .ConfigureAwait(false);
                    MarkConnected(
                        "DeviceReplay.Reconnected",
                        "Replay resumed after the injected disconnect.");
                }

                var timingDelay = previousRecordedAt is null
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks(Math.Max(
                        0,
                        (long)((signal.RecordedAtUtc - previousRecordedAt.Value).Ticks
                               / _configuration.SpeedFactor)));
                var delay = timingDelay
                            + TimeSpan.FromMilliseconds(
                                _configuration.AdditionalDelayMilliseconds);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, TimeProvider, Lifetime.Token)
                        .ConfigureAwait(false);
                }

                previousRecordedAt = signal.RecordedAtUtc;
                var sample = _configuration.BadQualitySignalIds.Contains(
                    signal.Sample.SignalId,
                    StringComparer.Ordinal)
                    ? new PluginDeviceSignalSample(
                        signal.Sample.SignalId,
                        signal.Sample.Value,
                        signal.Sample.Unit,
                        PluginDeviceSignalQuality.Bad,
                        signal.Sample.SourceTimestampUtc,
                        signal.Sample.ObservedTimestampUtc,
                        signal.Sample.Sequence,
                        "InjectedBadQuality")
                    : signal.Sample;
                ReplaySubscription[] subscribers;
                lock (_signalGate)
                {
                    _latest[sample.SignalId] = sample;
                    _history.Add(sample);
                    if (_history.Count > _configuration.HistoryCapacity)
                    {
                        _history.RemoveRange(
                            0,
                            _history.Count - _configuration.HistoryCapacity);
                    }

                    subscribers = _subscriptions.Values
                        .Where(subscription =>
                            subscription.SignalIds.Contains(sample.SignalId))
                        .ToArray();
                }

                foreach (var subscription in subscribers)
                {
                    subscription.Channel.Writer.TryWrite(sample);
                }

                MarkHeartbeat();
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            lock (_signalGate)
            {
                _playbackCompleted = true;
                foreach (var subscription in _subscriptions.Values)
                {
                    subscription.Channel.Writer.TryComplete();
                }
            }
        }
    }

    private PluginDeviceOperationResult ReplayRecordedCommand(
        string requestKind,
        string fingerprint)
    {
        lock (_commandGate)
        {
            if (_commandIndex >= _commands.Count)
            {
                return PluginDeviceOperationResult.Rejected(
                    TimeProvider.GetUtcNow(),
                    "Replay journal has no remaining command response.");
            }

            var expected = _commands[_commandIndex];
            if (!string.Equals(expected.Request.RequestKind, requestKind, StringComparison.Ordinal)
                || !string.Equals(
                    expected.Request.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return PluginDeviceOperationResult.Rejected(
                    TimeProvider.GetUtcNow(),
                    $"Replay command does not match journal sequence {expected.JournalSequence}.");
            }

            _commandIndex++;
            return expected.Response.CompletionState
                   == PluginDeviceOperationCompletionState.Unknown
                ? PluginDeviceOperationResult.UnknownCompletion(
                    expected.Response.CompletedAtUtc,
                    expected.Response.FailureReason
                    ?? "Command completion is unknown; RecoveryRequired.")
                : new PluginDeviceOperationResult(
                    expected.Response.Outcome,
                    expected.Response.CompletedAtUtc,
                    expected.Response.OutputPayload,
                    expected.Response.FailureReason);
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ReplaySignal> ParseSignals(
        IReadOnlyList<DeviceSessionJournalEntry> entries)
    {
        var result = new List<ReplaySignal>();
        long previousSequence = -1;
        foreach (var entry in entries.Where(static entry =>
                     entry.Kind == DeviceSessionJournal.SignalSampleKind))
        {
            if (entry.DeviceSequence is not { } sequence
                || sequence <= previousSequence)
            {
                throw new InvalidDataException(
                    $"Signal device sequence at journal line {entry.JournalSequence} is not strictly increasing.");
            }

            if (entry.SignalId is null
                || entry.SourceTimestampUtc is null
                || entry.ObservedTimestampUtc is null)
            {
                throw new InvalidDataException(
                    $"Signal journal line {entry.JournalSequence} is incomplete.");
            }

            var payload = DeviceSessionJournal.DeserializePayload<JournalSignalPayload>(
                entry.PayloadJson,
                $"signal journal line {entry.JournalSequence}");
            var sample = new PluginDeviceSignalSample(
                entry.SignalId,
                new PluginDeviceValue(payload.ValueType, payload.CanonicalValue),
                payload.Unit,
                payload.Quality,
                entry.SourceTimestampUtc.Value,
                entry.ObservedTimestampUtc.Value,
                sequence,
                payload.QualityCode);
            result.Add(new ReplaySignal(entry.JournalSequence, entry.RecordedAtUtc, sample));
            previousSequence = sequence;
        }

        return result.AsReadOnly();
    }

    private static ReplayCommand[] ParseCommands(
        IReadOnlyList<DeviceSessionJournalEntry> entries)
    {
        var pending = new Dictionary<string, PendingReplayCommand>(StringComparer.Ordinal);
        var result = new List<ReplayCommand>();
        foreach (var entry in entries)
        {
            if (entry.Kind == DeviceSessionJournal.CommandRequestKind)
            {
                if (entry.CorrelationId is null
                    || !pending.TryAdd(
                        entry.CorrelationId,
                        new PendingReplayCommand(
                            entry.JournalSequence,
                            DeviceSessionJournal
                                .DeserializePayload<JournalCommandRequestPayload>(
                                    entry.PayloadJson,
                                    $"command request journal line {entry.JournalSequence}"))))
                {
                    throw new InvalidDataException(
                        $"Command request journal line {entry.JournalSequence} has an invalid or duplicate correlation id.");
                }
            }
            else if (entry.Kind == DeviceSessionJournal.CommandResponseKind)
            {
                if (entry.CorrelationId is null
                    || !pending.Remove(entry.CorrelationId, out var request))
                {
                    throw new InvalidDataException(
                        $"Command response journal line {entry.JournalSequence} has no matching request.");
                }

                var response = DeviceSessionJournal
                    .DeserializePayload<JournalCommandResponsePayload>(
                        entry.PayloadJson,
                        $"command response journal line {entry.JournalSequence}");
                result.Add(new ReplayCommand(
                    request.JournalSequence,
                    request.Request,
                    response));
            }
        }

        if (pending.Count != 0)
        {
            throw new InvalidDataException(
                "Replay journal contains a command request without a response.");
        }

        return result
            .OrderBy(static command => command.JournalSequence)
            .ToArray();
    }

    private void ValidateSession(string sessionId)
    {
        if (!string.Equals(sessionId, Contract.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Request session '{sessionId}' does not match '{Contract.SessionId}'.");
        }
    }

    private sealed record ReplaySignal(
        long JournalSequence,
        DateTimeOffset RecordedAtUtc,
        PluginDeviceSignalSample Sample);

    private sealed record PendingReplayCommand(
        long JournalSequence,
        JournalCommandRequestPayload Request);

    private sealed record ReplayCommand(
        long JournalSequence,
        JournalCommandRequestPayload Request,
        JournalCommandResponsePayload Response);

    private sealed record ReplayWritePayload(
        string SignalId,
        PluginDeviceValueType ValueType,
        string CanonicalValue,
        string? Unit);

    private sealed class ReplaySubscription
    {
        public ReplaySubscription(
            string subscriptionId,
            HashSet<string> signalIds)
        {
            SubscriptionId = subscriptionId;
            SignalIds = signalIds;
        }

        public string SubscriptionId { get; }

        public HashSet<string> SignalIds { get; }

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
}
