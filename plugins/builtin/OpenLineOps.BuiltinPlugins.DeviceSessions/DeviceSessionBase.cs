using System.Collections.Concurrent;
using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal interface IDeviceSession : IAsyncDisposable
{
    PluginDeviceSession Contract { get; }

    ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken);

    ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        CancellationToken cancellationToken);

    ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken);

    PluginDeviceHealthSnapshot GetHealth();

    PluginDeviceDiagnosticsSnapshot GetDiagnostics();
}

internal abstract class DeviceSessionBase : IDeviceSession
{
    private const int MaximumDiagnosticEntries = 256;
    private readonly ConcurrentDictionary<string, RecordedCommand> _commands =
        new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly List<PluginDeviceDiagnosticEntry> _diagnostics = [];
    private long _highestFencingToken;
    private PluginDeviceHealthStatus _healthStatus = PluginDeviceHealthStatus.Unknown;
    private DateTimeOffset? _lastHeartbeatAtUtc;
    private string? _healthDetails;
    private bool _disposed;

    protected DeviceSessionBase(
        PluginDeviceSession contract,
        TimeProvider timeProvider,
        DeviceSessionJournalWriter? journal)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Journal = journal;
        RestoredDeviceSequence = Journal?.ExistingEntries
            .Where(entry => entry.Kind == DeviceSessionJournal.SignalSampleKind
                            && string.Equals(
                                entry.DeviceInstanceId,
                                Contract.DeviceInstanceId,
                                StringComparison.Ordinal))
            .Select(static entry => entry.DeviceSequence ?? 0)
            .DefaultIfEmpty()
            .Max() ?? 0;
        RestoreCommandLedger();
    }

    public PluginDeviceSession Contract { get; }

    protected TimeProvider TimeProvider { get; }

    protected DeviceSessionJournalWriter? Journal { get; }

    protected long RestoredDeviceSequence { get; }

    protected CancellationTokenSource Lifetime { get; } = new();

    protected virtual bool AllowsNonDurableNonIdempotentCommands => false;

    public abstract ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken);

    public abstract ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken);

    public abstract IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        CancellationToken cancellationToken);

    public abstract ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken);

    public PluginDeviceHealthSnapshot GetHealth()
    {
        lock (_stateGate)
        {
            var now = TimeProvider.GetUtcNow();
            DateTimeOffset? heartbeat = _lastHeartbeatAtUtc is { } value && value <= now
                ? value
                : null;
            return new PluginDeviceHealthSnapshot(
                Contract.SessionId,
                _healthStatus,
                now,
                heartbeat,
                _healthDetails);
        }
    }

    public PluginDeviceDiagnosticsSnapshot GetDiagnostics()
    {
        lock (_stateGate)
        {
            return new PluginDeviceDiagnosticsSnapshot(
                Contract.SessionId,
                TimeProvider.GetUtcNow(),
                _diagnostics.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await Lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Journal is not null)
            {
                await Journal.DisposeAsync().ConfigureAwait(false);
            }

            Lifetime.Dispose();
        }
    }

    protected abstract ValueTask DisposeSessionAsync();

    protected async ValueTask<PluginDeviceOperationResult> ExecuteCommandAsync(
        PluginDeviceCommandEnvelope command,
        string requestKind,
        string? requestPayloadJson,
        Func<CancellationToken, ValueTask<PluginDeviceOperationResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKind);
        ArgumentNullException.ThrowIfNull(execute);
        var requestFingerprint = DeviceCommandEvidence.RequestFingerprint(
            requestKind,
            requestPayloadJson);
        var evidenceFingerprint = DeviceCommandEvidence.EnvelopeFingerprint(
            requestFingerprint,
            command.FencingToken,
            command.DeadlineUtc,
            command.IdempotencyClass,
            command.SafetyClass);

        if (_commands.TryGetValue(command.CommandId, out var recorded))
        {
            return !string.Equals(
                recorded.Fingerprint,
                evidenceFingerprint,
                StringComparison.Ordinal)
                ? PluginDeviceOperationResult.Rejected(
                    TimeProvider.GetUtcNow(),
                    $"Command id '{command.CommandId}' was reused with different evidence.")
                : await recorded.Result.ConfigureAwait(false);
        }

        var now = TimeProvider.GetUtcNow();
        if (command.SafetyClass == PluginDeviceCommandSafetyClass.SafetyCritical)
        {
            return PluginDeviceOperationResult.Rejected(
                now,
                "Safety-critical actions must be executed by a certified safety controller.");
        }

        if (command.DeadlineUtc <= now)
        {
            return PluginDeviceOperationResult.TimedOut(
                now,
                "Command deadline elapsed before execution.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return PluginDeviceOperationResult.Failed(
                now,
                "Command was cancelled before it was accepted.");
        }

        if (command.IdempotencyClass == PluginDeviceCommandIdempotencyClass.NonIdempotent
            && Journal is null
            && !AllowsNonDurableNonIdempotentCommands)
        {
            return PluginDeviceOperationResult.Rejected(
                now,
                "Non-idempotent commands require a durable command journal.");
        }

        Task<PluginDeviceOperationResult> resultTask;
        lock (_stateGate)
        {
            if (_commands.TryGetValue(command.CommandId, out recorded))
            {
                resultTask = string.Equals(
                    recorded.Fingerprint,
                    evidenceFingerprint,
                    StringComparison.Ordinal)
                    ? recorded.Result
                    : Task.FromResult(PluginDeviceOperationResult.Rejected(
                        TimeProvider.GetUtcNow(),
                        $"Command id '{command.CommandId}' was reused with different evidence."));
            }
            else if (command.FencingToken < _highestFencingToken)
            {
                resultTask = Task.FromResult(PluginDeviceOperationResult.Rejected(
                    TimeProvider.GetUtcNow(),
                    $"Fencing token {command.FencingToken} is stale."));
            }
            else
            {
                _highestFencingToken = command.FencingToken;
                resultTask = ExecuteCommandCoreAsync(
                    command,
                    requestKind,
                    requestFingerprint,
                    requestPayloadJson,
                    execute,
                    cancellationToken);
                if (!_commands.TryAdd(
                        command.CommandId,
                        new RecordedCommand(evidenceFingerprint, resultTask)))
                {
                    throw new InvalidOperationException(
                        $"Command id '{command.CommandId}' could not be recorded.");
                }
            }
        }

        return await resultTask.ConfigureAwait(false);
    }

    protected void MarkConnected(string diagnosticCode, string message)
    {
        var now = TimeProvider.GetUtcNow();
        lock (_stateGate)
        {
            _healthStatus = PluginDeviceHealthStatus.Healthy;
            _lastHeartbeatAtUtc = now;
            _healthDetails = null;
            AddDiagnosticUnsafe(
                new PluginDeviceDiagnosticEntry(
                    diagnosticCode,
                    PluginDeviceDiagnosticSeverity.Information,
                    message,
                    now));
        }
    }

    protected void MarkHeartbeat()
    {
        lock (_stateGate)
        {
            _lastHeartbeatAtUtc = TimeProvider.GetUtcNow();
            if (_healthStatus == PluginDeviceHealthStatus.Degraded)
            {
                _healthStatus = PluginDeviceHealthStatus.Healthy;
                _healthDetails = null;
            }
        }
    }

    protected void MarkDisconnected(
        string diagnosticCode,
        string message,
        bool reconnecting)
    {
        var now = TimeProvider.GetUtcNow();
        lock (_stateGate)
        {
            _healthStatus = reconnecting
                ? PluginDeviceHealthStatus.Degraded
                : PluginDeviceHealthStatus.Unhealthy;
            _healthDetails = message;
            AddDiagnosticUnsafe(
                new PluginDeviceDiagnosticEntry(
                    diagnosticCode,
                    reconnecting
                        ? PluginDeviceDiagnosticSeverity.Warning
                        : PluginDeviceDiagnosticSeverity.Error,
                    message,
                    now));
        }
    }

    protected void RecordDiagnostic(
        string code,
        PluginDeviceDiagnosticSeverity severity,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        lock (_stateGate)
        {
            AddDiagnosticUnsafe(new PluginDeviceDiagnosticEntry(
                code,
                severity,
                message,
                TimeProvider.GetUtcNow(),
                attributes));
        }
    }

    protected ValueTask RecordSignalAsync(
        PluginDeviceSignalSample sample,
        CancellationToken cancellationToken) =>
        Journal is null
            ? ValueTask.CompletedTask
            : Journal.AppendSignalAsync(
                Contract.DeviceInstanceId,
                sample,
                TimeProvider.GetUtcNow(),
                cancellationToken);

    protected static bool IsNetworkFailure(Exception exception) =>
        exception is IOException
            or System.Net.Sockets.SocketException
            or ObjectDisposedException;

    private async Task<PluginDeviceOperationResult> ExecuteCommandCoreAsync(
        PluginDeviceCommandEnvelope command,
        string requestKind,
        string requestFingerprint,
        string? requestPayloadJson,
        Func<CancellationToken, ValueTask<PluginDeviceOperationResult>> execute,
        CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        if (command.DeadlineUtc <= now)
        {
            return PluginDeviceOperationResult.TimedOut(
                now,
                "Command deadline elapsed before execution.");
        }

        using var deadline = new CancellationTokenSource(
            command.DeadlineUtc - now,
            TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            Lifetime.Token,
            deadline.Token);
        var requestPersisted = false;
        PluginDeviceOperationResult result;
        try
        {
            if (Journal is not null)
            {
                await Journal.AppendCommandRequestAsync(
                        Contract.DeviceInstanceId,
                        command,
                        requestKind,
                        requestFingerprint,
                        requestPayloadJson,
                        TimeProvider.GetUtcNow(),
                        linked.Token)
                    .ConfigureAwait(false);
                requestPersisted = true;
            }

            result = await execute(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            result = PluginDeviceOperationResult.TimedOut(
                TimeProvider.GetUtcNow(),
                "Command exceeded its deadline.");
        }
        catch (DeviceCommandCompletionUnknownException exception)
        {
            result = PluginDeviceOperationResult.UnknownCompletion(
                TimeProvider.GetUtcNow(),
                exception.Message);
            RecordDiagnostic(
                "DeviceSession.RecoveryRequired",
                PluginDeviceDiagnosticSeverity.Error,
                $"Command '{command.CommandId}' has unknown completion and requires equipment-state recovery before another physical action is attempted.");
        }
        catch (OperationCanceledException)
        {
            result = PluginDeviceOperationResult.Failed(
                TimeProvider.GetUtcNow(),
                "Command completion is unknown after cancellation.");
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or JsonException
                                           or FormatException)
        {
            result = PluginDeviceOperationResult.Rejected(
                TimeProvider.GetUtcNow(),
                exception.Message);
        }
        catch (Exception exception) when (IsNetworkFailure(exception))
        {
            result = PluginDeviceOperationResult.Failed(
                TimeProvider.GetUtcNow(),
                $"Device communication failed: {exception.Message}");
        }

        if (Journal is null || !requestPersisted)
        {
            return result;
        }

        try
        {
            await Journal.AppendCommandResponseAsync(
                    Contract.DeviceInstanceId,
                    command.CommandId,
                    result,
                    TimeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException
                                           or ObjectDisposedException)
        {
            RecordDiagnostic(
                "DeviceSession.CommandEvidencePersistenceFailed",
                PluginDeviceDiagnosticSeverity.Error,
                $"Command '{command.CommandId}' completed but its response evidence could not be persisted: {exception.Message}");
            return PluginDeviceOperationResult.UnknownCompletion(
                TimeProvider.GetUtcNow(),
                "Command response evidence could not be persisted; RecoveryRequired because safe replay cannot be proven.");
        }
    }

    private void RestoreCommandLedger()
    {
        if (Journal is null)
        {
            return;
        }

        var pending = new Dictionary<string, RestoredCommandRequest>(StringComparer.Ordinal);
        foreach (var entry in Journal.ExistingEntries.Where(entry =>
                     string.Equals(
                         entry.DeviceInstanceId,
                         Contract.DeviceInstanceId,
                         StringComparison.Ordinal)))
        {
            if (entry.Kind == DeviceSessionJournal.CommandRequestKind)
            {
                if (entry.CorrelationId is null
                    || pending.ContainsKey(entry.CorrelationId)
                    || _commands.ContainsKey(entry.CorrelationId))
                {
                    throw new InvalidDataException(
                        $"Journal command request {entry.JournalSequence} has a duplicate or missing command id.");
                }

                var request = DeviceSessionJournal
                    .DeserializePayload<JournalCommandRequestPayload>(
                        entry.PayloadJson,
                        $"command request journal line {entry.JournalSequence}");
                var expectedRequestFingerprint = DeviceCommandEvidence.RequestFingerprint(
                    request.RequestKind,
                    request.RequestPayloadJson);
                if (!string.Equals(
                        request.Fingerprint,
                        expectedRequestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Journal command request {entry.JournalSequence} has inconsistent request evidence.");
                }

                var evidenceFingerprint = DeviceCommandEvidence.EnvelopeFingerprint(
                    request.Fingerprint,
                    request.FencingToken,
                    request.DeadlineUtc,
                    request.IdempotencyClass,
                    request.SafetyClass);
                pending.Add(
                    entry.CorrelationId,
                    new RestoredCommandRequest(evidenceFingerprint));
                _highestFencingToken = Math.Max(
                    _highestFencingToken,
                    request.FencingToken);
            }
            else if (entry.Kind == DeviceSessionJournal.CommandResponseKind)
            {
                if (entry.CorrelationId is null
                    || !pending.Remove(entry.CorrelationId, out var request))
                {
                    throw new InvalidDataException(
                        $"Journal command response {entry.JournalSequence} has no matching request.");
                }

                var response = DeviceSessionJournal
                    .DeserializePayload<JournalCommandResponsePayload>(
                        entry.PayloadJson,
                        $"command response journal line {entry.JournalSequence}");
                var result = RestoreOperationResult(response);
                if (result.RecoveryRequired)
                {
                    AddDiagnosticUnsafe(new PluginDeviceDiagnosticEntry(
                        "DeviceSession.RecoveryRequired",
                        PluginDeviceDiagnosticSeverity.Error,
                        $"Command '{entry.CorrelationId}' has a durable unknown-completion result and will not be resent.",
                        TimeProvider.GetUtcNow()));
                }

                if (!_commands.TryAdd(
                        entry.CorrelationId,
                        new RecordedCommand(
                            request.EvidenceFingerprint,
                            Task.FromResult(result))))
                {
                    throw new InvalidDataException(
                        $"Journal command '{entry.CorrelationId}' is duplicated.");
                }
            }
        }

        foreach (var item in pending)
        {
            var completionUnknown = PluginDeviceOperationResult.UnknownCompletion(
                TimeProvider.GetUtcNow(),
                "Command completion is unknown after host interruption; RecoveryRequired. The command will not be resent.");
            if (!_commands.TryAdd(
                    item.Key,
                    new RecordedCommand(
                        item.Value.EvidenceFingerprint,
                        Task.FromResult(completionUnknown))))
            {
                throw new InvalidDataException(
                    $"Journal command '{item.Key}' is duplicated.");
            }

            AddDiagnosticUnsafe(new PluginDeviceDiagnosticEntry(
                "DeviceSession.RecoveryRequired",
                PluginDeviceDiagnosticSeverity.Error,
                $"Command '{item.Key}' has a durable request without a durable response and will not be resent.",
                TimeProvider.GetUtcNow()));
        }
    }

    private void AddDiagnosticUnsafe(PluginDeviceDiagnosticEntry entry)
    {
        _diagnostics.Add(entry);
        if (_diagnostics.Count > MaximumDiagnosticEntries)
        {
            _diagnostics.RemoveRange(
                0,
                _diagnostics.Count - MaximumDiagnosticEntries);
        }
    }

    private static PluginDeviceOperationResult RestoreOperationResult(
        JournalCommandResponsePayload response) =>
        response.CompletionState == PluginDeviceOperationCompletionState.Unknown
            ? PluginDeviceOperationResult.UnknownCompletion(
                response.CompletedAtUtc,
                response.FailureReason
                ?? "Command completion is unknown; RecoveryRequired.")
            : new PluginDeviceOperationResult(
                response.Outcome,
                response.CompletedAtUtc,
                response.OutputPayload,
                response.FailureReason);

    private sealed record RecordedCommand(
        string Fingerprint,
        Task<PluginDeviceOperationResult> Result);

    private sealed record RestoredCommandRequest(string EvidenceFingerprint);
}

internal sealed class DeviceCommandCompletionUnknownException : Exception
{
    public DeviceCommandCompletionUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
