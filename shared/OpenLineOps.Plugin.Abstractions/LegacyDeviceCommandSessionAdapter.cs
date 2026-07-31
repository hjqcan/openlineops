using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpenLineOps.Plugin.Abstractions;

/// <summary>
/// Adapts the version-one command contract to the session contract without
/// permitting a replay to execute a legacy non-idempotent command twice.
/// </summary>
public sealed class LegacyDeviceCommandSessionAdapter : IOpenLineOpsDeviceSessionPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOpenLineOpsDeviceCommandPlugin _legacyPlugin;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ConcurrentDictionary<string, LegacySessionState> _sessions =
        new(StringComparer.Ordinal);

    public LegacyDeviceCommandSessionAdapter(
        IOpenLineOpsDeviceCommandPlugin legacyPlugin,
        TimeProvider? timeProvider = null,
        TimeSpan? heartbeatInterval = null)
    {
        _legacyPlugin = legacyPlugin ?? throw new ArgumentNullException(nameof(legacyPlugin));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _heartbeatInterval = PluginDeviceContractGuard.PositiveWholeMilliseconds(
            heartbeatInterval ?? TimeSpan.FromSeconds(5),
            nameof(heartbeatInterval));
    }

    public PluginManifest Manifest => _legacyPlugin.Manifest;

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default) =>
        _legacyPlugin.InitializeAsync(services, cancellationToken);

    public ValueTask<PluginDeviceSession> OpenAsync(
        PluginDeviceSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var openedAtUtc = _timeProvider.GetUtcNow();
        var session = new PluginDeviceSession(
            $"{request.DeviceInstanceId}:legacy:{Guid.NewGuid():N}",
            request.DeviceInstanceId,
            openedAtUtc,
            _heartbeatInterval);
        if (!_sessions.TryAdd(
                session.SessionId,
                new LegacySessionState(session, request.ConfigurationPayload)))
        {
            throw new InvalidOperationException(
                $"Legacy device session '{session.SessionId}' already exists.");
        }

        return ValueTask.FromResult(session);
    }

    public ValueTask CloseAsync(
        PluginDeviceSessionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _sessions.TryRemove(request.SessionId, out _);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = CompatibilityEnvelope(
            request.SessionId,
            "read",
            _timeProvider.GetUtcNow());
        var result = await InvokeAsync(
                new PluginDeviceInvocationRequest(
                    request.SessionId,
                    command,
                    "Read",
                    JsonSerializer.Serialize(request.SignalIds, JsonOptions)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Legacy read failed: {result.FailureReason}");
        }

        try
        {
            return JsonSerializer.Deserialize<PluginDeviceSignalSample[]>(
                       result.OutputPayload ?? "[]",
                       JsonOptions)
                   ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Legacy Read result is not a valid signal sample collection.",
                exception);
        }
    }

    public ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(
            new PluginDeviceInvocationRequest(
                request.SessionId,
                request.Command,
                "Write",
                JsonSerializer.Serialize(request.Writes, JsonOptions)),
            cancellationToken);
    }

    public async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sequence = request.ResumeAfterSequence ?? 0;
        var interval = request.MinimumSamplingInterval ?? _heartbeatInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            var samples = await ReadAsync(
                    new PluginDeviceSignalReadRequest(request.SessionId, request.SignalIds),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var sample in samples)
            {
                sequence = checked(sequence + 1);
                yield return new PluginDeviceSignalSubscriptionEvent(
                    request.SubscriptionId,
                    sequence,
                    sample);
            }

            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_sessions.TryGetValue(request.SessionId, out var session))
        {
            return ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                $"Device session '{request.SessionId}' is not open."));
        }

        if (request.Command.SafetyClass == PluginDeviceCommandSafetyClass.SafetyCritical)
        {
            return ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                "The legacy command contract cannot execute safety-critical actions."));
        }

        var definition = ResolveCommand(request.Operation);
        if (definition is null)
        {
            return ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                _timeProvider.GetUtcNow(),
                $"Legacy plugin '{Manifest.Id}' does not declare command '{request.Operation}'."));
        }

        var fingerprint = new InvocationFingerprint(
            request.Command.FencingToken,
            request.Command.DeadlineUtc,
            request.Command.IdempotencyClass,
            request.Command.SafetyClass,
            request.Operation,
            request.InputPayload);

        Task<PluginDeviceOperationResult> execution;
        lock (session.SyncRoot)
        {
            if (session.Commands.TryGetValue(request.Command.CommandId, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new ValueTask<PluginDeviceOperationResult>(existing.Execution)
                    : ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                        _timeProvider.GetUtcNow(),
                        $"Command id '{request.Command.CommandId}' was reused with different evidence."));
            }

            if (request.Command.FencingToken < session.HighestFencingToken)
            {
                return ValueTask.FromResult(PluginDeviceOperationResult.Rejected(
                    _timeProvider.GetUtcNow(),
                    $"Fencing token {request.Command.FencingToken} is stale."));
            }

            session.HighestFencingToken = request.Command.FencingToken;
            execution = ExecuteLegacyCommandAsync(
                session,
                definition,
                request,
                cancellationToken);
            session.Commands.Add(
                request.Command.CommandId,
                new LegacyCommandExecution(fingerprint, execution));
        }

        return new ValueTask<PluginDeviceOperationResult>(execution);
    }

    public ValueTask<PluginDeviceHealthSnapshot> GetHealthAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        return ValueTask.FromResult(_sessions.ContainsKey(request.SessionId)
            ? new PluginDeviceHealthSnapshot(
                request.SessionId,
                PluginDeviceHealthStatus.Degraded,
                now,
                now,
                "Legacy command adapter is active; native subscriptions and device diagnostics are unavailable.")
            : new PluginDeviceHealthSnapshot(
                request.SessionId,
                PluginDeviceHealthStatus.Unhealthy,
                now,
                details: "Device session is not open."));
    }

    public ValueTask<PluginDeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var entry = new PluginDeviceDiagnosticEntry(
            "Plugin.LegacyCommandAdapter",
            PluginDeviceDiagnosticSeverity.Warning,
            "The plugin uses the compatibility command contract and should be upgraded to a native device session.",
            now);
        return ValueTask.FromResult(new PluginDeviceDiagnosticsSnapshot(
            request.SessionId,
            now,
            [entry]));
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.Clear();
        await _legacyPlugin.DisposeAsync().ConfigureAwait(false);
    }

    private PluginDeviceCommandDefinition? ResolveCommand(string operation)
    {
        return (Manifest.DeviceCommands ?? [])
            .SingleOrDefault(command =>
                string.Equals(command.CommandName, operation, StringComparison.Ordinal));
    }

    private async Task<PluginDeviceOperationResult> ExecuteLegacyCommandAsync(
        LegacySessionState session,
        PluginDeviceCommandDefinition definition,
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (request.Command.DeadlineUtc <= now)
        {
            return PluginDeviceOperationResult.TimedOut(
                now,
                "Command deadline elapsed before the legacy plugin was invoked.");
        }

        var remaining = request.Command.DeadlineUtc - now;
        var definitionTimeout = TimeSpan.FromMilliseconds(definition.TimeoutMilliseconds);
        var timeout = remaining < definitionTimeout ? remaining : definitionTimeout;
        using var deadlineCancellation = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);

        try
        {
            var result = await _legacyPlugin.ExecuteDeviceCommandAsync(
                    new PluginDeviceCommandExecutionRequest(
                        session.Session.DeviceInstanceId,
                        definition.Id,
                        definition.Capability,
                        definition.CommandName,
                        request.InputPayload,
                        timeout),
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            var completedAtUtc = _timeProvider.GetUtcNow();
            return result.Outcome switch
            {
                PluginDeviceCommandExecutionOutcome.Completed =>
                    PluginDeviceOperationResult.Completed(completedAtUtc, result.ResultPayload),
                PluginDeviceCommandExecutionOutcome.Rejected =>
                    PluginDeviceOperationResult.Rejected(
                        completedAtUtc,
                        result.FailureReason ?? "Legacy command was rejected."),
                PluginDeviceCommandExecutionOutcome.TimedOut =>
                    PluginDeviceOperationResult.TimedOut(
                        completedAtUtc,
                        result.FailureReason ?? "Legacy command timed out."),
                _ => PluginDeviceOperationResult.Failed(
                    completedAtUtc,
                    result.FailureReason ?? "Legacy command failed.")
            };
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            return PluginDeviceOperationResult.TimedOut(
                _timeProvider.GetUtcNow(),
                "Legacy command exceeded its deadline.");
        }
        catch (OperationCanceledException)
        {
            return PluginDeviceOperationResult.Failed(
                _timeProvider.GetUtcNow(),
                "Legacy command completion is unknown after cancellation; automatic replay is blocked.");
        }
        catch (Exception exception)
        {
            return PluginDeviceOperationResult.Failed(
                _timeProvider.GetUtcNow(),
                $"Legacy command failed: {exception.Message}");
        }
    }

    private PluginDeviceCommandEnvelope CompatibilityEnvelope(
        string sessionId,
        string purpose,
        DateTimeOffset now)
    {
        var fencingToken = 1L;
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.SyncRoot)
            {
                fencingToken = Math.Max(session.HighestFencingToken, 1);
            }
        }

        return new PluginDeviceCommandEnvelope(
            $"legacy:{purpose}:{Guid.NewGuid():N}",
            fencingToken,
            now.Add(_heartbeatInterval),
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Informational);
    }

    private sealed class LegacySessionState(
        PluginDeviceSession session,
        string? configurationPayload)
    {
        public object SyncRoot { get; } = new();

        public PluginDeviceSession Session { get; } = session;

        public string? ConfigurationPayload { get; } = configurationPayload;

        public long HighestFencingToken { get; set; }

        public Dictionary<string, LegacyCommandExecution> Commands { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed record LegacyCommandExecution(
        InvocationFingerprint Fingerprint,
        Task<PluginDeviceOperationResult> Execution);

    private sealed record InvocationFingerprint(
        long FencingToken,
        DateTimeOffset DeadlineUtc,
        PluginDeviceCommandIdempotencyClass IdempotencyClass,
        PluginDeviceCommandSafetyClass SafetyClass,
        string Operation,
        string? InputPayload);
}
