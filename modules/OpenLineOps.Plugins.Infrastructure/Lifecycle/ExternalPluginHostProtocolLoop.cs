using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public static class ExternalPluginHostProtocolLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask RunAsync(
        IOpenLineOpsPlugin plugin,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new ProtocolFrameWriter(output);
        var subscriptions = new List<ActiveSubscription>();
        var sessionPlugin = plugin as IOpenLineOpsDeviceSessionPlugin
                            ?? (plugin is IOpenLineOpsDeviceCommandPlugin legacyPlugin
                                ? new LegacyDeviceCommandSessionAdapter(legacyPlugin)
                                : null);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PruneCompletedSubscriptions(subscriptions);

                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = await HandleMessageAsync(
                        plugin,
                        sessionPlugin,
                        line,
                        writer,
                        subscriptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response is not null)
                {
                    await writer.WriteLegacyAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Cancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(subscriptions.Select(static subscription => subscription.Task))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Preserve the host cancellation raised by the main protocol loop.
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Cancellation.Dispose();
            }
        }
    }

    private static void PruneCompletedSubscriptions(
        ICollection<ActiveSubscription> subscriptions)
    {
        foreach (var subscription in subscriptions
                     .Where(static subscription => subscription.Task.IsCompletedSuccessfully)
                     .ToArray())
        {
            subscriptions.Remove(subscription);
            subscription.Cancellation.Dispose();
        }
    }

    private static async ValueTask<object?> HandleMessageAsync(
        IOpenLineOpsPlugin plugin,
        IOpenLineOpsDeviceSessionPlugin? sessionPlugin,
        string line,
        ProtocolFrameWriter writer,
        ICollection<ActiveSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        ExternalProtocolEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExternalProtocolEnvelope>(line, JsonOptions);
        }
        catch (JsonException exception)
        {
            return DeviceFailureResponse(
                requestId: "",
                $"Plugin host protocol request JSON is invalid: {exception.Message}");
        }

        if (envelope is null)
        {
            return DeviceFailureResponse("", "Plugin host protocol request is empty.");
        }

        if (IsDeviceSessionMessage(envelope.MessageType))
        {
            await HandleDeviceSessionMessageAsync(
                    plugin,
                    sessionPlugin,
                    envelope,
                    line,
                    writer,
                    subscriptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        return envelope.MessageType switch
        {
            "device-command" => await HandleDeviceCommandAsync(plugin, line, cancellationToken).ConfigureAwait(false),
            "process-command" => await HandleProcessCommandAsync(plugin, line, cancellationToken).ConfigureAwait(false),
            _ => DeviceFailureResponse(
                envelope.RequestId,
                $"Plugin host protocol message type '{envelope.MessageType}' is not supported.")
        };
    }

    private static bool IsDeviceSessionMessage(string? messageType)
    {
        return messageType is
            "device-session-open"
            or "device-session-close"
            or "device-signal-read"
            or "device-signal-write"
            or "device-session-invoke"
            or "device-session-health"
            or "device-session-diagnostics"
            or "device-signal-subscribe"
            or "device-session-heartbeat";
    }

    private static async ValueTask HandleDeviceSessionMessageAsync(
        IOpenLineOpsPlugin plugin,
        IOpenLineOpsDeviceSessionPlugin? sessionPlugin,
        ExternalProtocolEnvelope envelope,
        string line,
        ProtocolFrameWriter writer,
        ICollection<ActiveSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var responseMessageType = $"{envelope.MessageType}-result";

        try
        {
            RequireProtocolIdentity(envelope.RequestId, "request id");

            if (envelope.MessageType == "device-session-heartbeat")
            {
                if (envelope.SessionId is not null)
                {
                    RequireProtocolIdentity(envelope.SessionId, "session id");
                }

                await writer.WriteSessionAsync(
                        responseMessageType,
                        envelope.SessionId,
                        envelope.RequestId ?? "",
                        new ExternalHeartbeatPayload(DateTimeOffset.UtcNow),
                        error: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (sessionPlugin is null)
            {
                throw new NotSupportedException(
                    $"Plugin '{plugin.Manifest.Id}' does not implement "
                    + $"{nameof(IOpenLineOpsDeviceSessionPlugin)}.");
            }

            switch (envelope.MessageType)
            {
                case "device-session-open":
                    await HandleSessionOpenAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-session-close":
                    await HandleSessionCloseAsync(
                            sessionPlugin,
                            line,
                            writer,
                            subscriptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-signal-read":
                    await HandleSignalReadAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-signal-write":
                    await HandleSignalWriteAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-session-invoke":
                    await HandleSessionInvokeAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-session-health":
                    await HandleSessionHealthAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-session-diagnostics":
                    await HandleSessionDiagnosticsAsync(
                            sessionPlugin,
                            line,
                            writer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "device-signal-subscribe":
                    await HandleSignalSubscribeAsync(
                            sessionPlugin,
                            line,
                            writer,
                            subscriptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Device session message type '{envelope.MessageType}' has no handler.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await writer.WriteSessionAsync(
                    responseMessageType,
                    envelope.SessionId,
                    envelope.RequestId ?? "",
                    payload: null,
                    $"Plugin host {envelope.MessageType} failed: {exception.Message}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask HandleSessionOpenAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<PluginDeviceSessionOpenRequest>(line);
        if (request.SessionId is not null)
        {
            throw new InvalidDataException(
                "Device session open request cannot declare a session id before the session exists.");
        }

        var session = await plugin.OpenAsync(request.Payload, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty session.");
        if (!string.Equals(
                request.Payload.DeviceInstanceId,
                session.DeviceInstanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Opened session device id '{session.DeviceInstanceId}' does not match "
                + $"requested device '{request.Payload.DeviceInstanceId}'.");
        }

        await writer.WriteSessionAsync(
                "device-session-open-result",
                session.SessionId,
                request.RequestId,
                session,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSessionCloseAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        ICollection<ActiveSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<PluginDeviceSessionCloseRequest>(line);
        RequireMatchingSession(request.SessionId, request.Payload.SessionId);

        await plugin.CloseAsync(request.Payload, cancellationToken).ConfigureAwait(false);
        await writer.WriteSessionAsync(
                "device-session-close-result",
                request.Payload.SessionId,
                request.RequestId,
                ExternalProtocolAcknowledgement.Success,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var subscription in subscriptions.Where(subscription =>
                     string.Equals(
                         subscription.SessionId,
                         request.Payload.SessionId,
                         StringComparison.Ordinal)))
        {
            subscription.Cancellation.Cancel();
        }
    }

    private static async ValueTask HandleSignalReadAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<ExternalSignalReadPayload>(line);
        var payload = request.Payload.ToContract();
        RequireMatchingSession(request.SessionId, payload.SessionId);

        var samples = await plugin.ReadAsync(payload, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty signal collection.");
        if (samples.Any(static sample => sample is null))
        {
            throw new InvalidDataException("Device session plugin returned a null signal sample.");
        }

        await writer.WriteSessionAsync(
                "device-signal-read-result",
                payload.SessionId,
                request.RequestId,
                samples.ToArray(),
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSignalWriteAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<ExternalSignalWritePayload>(line);
        var payload = request.Payload.ToContract();
        RequireMatchingSession(request.SessionId, payload.SessionId);

        var result = await plugin.WriteAsync(payload, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty write result.");

        await writer.WriteSessionAsync(
                "device-signal-write-result",
                payload.SessionId,
                request.RequestId,
                result,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSessionInvokeAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<PluginDeviceInvocationRequest>(line);
        RequireMatchingSession(request.SessionId, request.Payload.SessionId);

        var result = await plugin.InvokeAsync(request.Payload, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty invocation result.");

        await writer.WriteSessionAsync(
                "device-session-invoke-result",
                request.Payload.SessionId,
                request.RequestId,
                result,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSessionHealthAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<PluginDeviceSessionRequest>(line);
        RequireMatchingSession(request.SessionId, request.Payload.SessionId);

        var health = await plugin.GetHealthAsync(request.Payload, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty health snapshot.");
        RequireMatchingSession(request.Payload.SessionId, health.SessionId);

        await writer.WriteSessionAsync(
                "device-session-health-result",
                request.Payload.SessionId,
                request.RequestId,
                health,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSessionDiagnosticsAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<PluginDeviceSessionRequest>(line);
        RequireMatchingSession(request.SessionId, request.Payload.SessionId);

        var diagnostics = await plugin.GetDiagnosticsAsync(request.Payload, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Device session plugin returned an empty diagnostics snapshot.");
        RequireMatchingSession(request.Payload.SessionId, diagnostics.SessionId);

        await writer.WriteSessionAsync(
                "device-session-diagnostics-result",
                request.Payload.SessionId,
                request.RequestId,
                diagnostics,
                error: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleSignalSubscribeAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        string line,
        ProtocolFrameWriter writer,
        ICollection<ActiveSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var request = DeserializeSessionRequest<ExternalSignalSubscriptionPayload>(line);
        var payload = request.Payload.ToContract();
        RequireMatchingSession(request.SessionId, payload.SessionId);
        if (subscriptions.Any(subscription =>
                string.Equals(subscription.SessionId, payload.SessionId, StringComparison.Ordinal)
                && string.Equals(
                    subscription.SubscriptionId,
                    payload.SubscriptionId,
                    StringComparison.Ordinal)
                && !subscription.Task.IsCompleted))
        {
            throw new InvalidDataException(
                $"Subscription '{payload.SubscriptionId}' is already active "
                + $"for session '{payload.SessionId}'.");
        }

        await writer.WriteSessionAsync(
                "device-signal-subscribe-result",
                payload.SessionId,
                request.RequestId,
                new ExternalSubscriptionAccepted(payload.SubscriptionId),
                error: null,
                cancellationToken)
            .ConfigureAwait(false);

        var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var subscription = new ActiveSubscription(
            payload.SessionId,
            payload.SubscriptionId,
            subscriptionCancellation);
        subscriptions.Add(subscription);
        subscription.Task = PumpSubscriptionAsync(
            plugin,
            payload,
            request.RequestId,
            writer,
            subscriptionCancellation.Token,
            cancellationToken);
    }

    private static async Task PumpSubscriptionAsync(
        IOpenLineOpsDeviceSessionPlugin plugin,
        PluginDeviceSignalSubscriptionRequest request,
        string requestId,
        ProtocolFrameWriter writer,
        CancellationToken subscriptionCancellationToken,
        CancellationToken hostCancellationToken)
    {
        try
        {
            await foreach (var update in plugin
                               .SubscribeAsync(request, subscriptionCancellationToken)
                               .WithCancellation(subscriptionCancellationToken)
                               .ConfigureAwait(false))
            {
                if (update is null)
                {
                    throw new InvalidDataException(
                        "Device session plugin returned a null subscription event.");
                }

                if (!string.Equals(
                        request.SubscriptionId,
                        update.SubscriptionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Subscription event id '{update.SubscriptionId}' does not match "
                        + $"requested subscription '{request.SubscriptionId}'.");
                }

                await writer.WriteSessionAsync(
                        "device-signal-subscription-event",
                        request.SessionId,
                        requestId,
                        update,
                        error: null,
                        hostCancellationToken)
                    .ConfigureAwait(false);
            }

            await writer.WriteSessionAsync(
                    "device-signal-subscription-completed",
                    request.SessionId,
                    requestId,
                    new ExternalSubscriptionCompleted(request.SubscriptionId, "completed"),
                    error: null,
                    hostCancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (subscriptionCancellationToken.IsCancellationRequested)
        {
            if (!hostCancellationToken.IsCancellationRequested)
            {
                await writer.WriteSessionAsync(
                        "device-signal-subscription-completed",
                        request.SessionId,
                        requestId,
                        new ExternalSubscriptionCompleted(request.SubscriptionId, "canceled"),
                        error: null,
                        hostCancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await writer.WriteSessionAsync(
                    "device-signal-subscription-error",
                    request.SessionId,
                    requestId,
                    new ExternalSubscriptionFailed(request.SubscriptionId),
                    $"Plugin host device subscription failed: {exception.Message}",
                    hostCancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ParsedDeviceSessionProtocolRequest<TPayload> DeserializeSessionRequest<TPayload>(
        string line)
        where TPayload : class
    {
        ExternalDeviceSessionProtocolRequest<TPayload>? request;
        try
        {
            request = JsonSerializer.Deserialize<ExternalDeviceSessionProtocolRequest<TPayload>>(
                line,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Device session request JSON is invalid: {exception.Message}",
                exception);
        }

        if (request is null)
        {
            throw new InvalidDataException("Device session request is empty.");
        }

        if (request.Payload is null)
        {
            throw new InvalidDataException("Device session request payload is required.");
        }

        return new ParsedDeviceSessionProtocolRequest<TPayload>(
            request.MessageType,
            request.SessionId,
            request.RequestId,
            request.Payload);
    }

    private static void RequireMatchingSession(string? envelopeSessionId, string payloadSessionId)
    {
        if (!string.Equals(envelopeSessionId, payloadSessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Protocol session id '{envelopeSessionId}' does not match "
                + $"payload session id '{payloadSessionId}'.");
        }
    }

    private static void RequireProtocolIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Protocol {name} must be non-empty canonical text.");
        }
    }

    private static async ValueTask<ExternalDeviceCommandProtocolResponse> HandleDeviceCommandAsync(
        IOpenLineOpsPlugin plugin,
        string line,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ExternalDeviceCommandProtocolRequest>(line, JsonOptions);
        if (request is null)
        {
            return DeviceFailureResponse("", "Plugin host device command request is empty.");
        }

        if (request.Payload is null)
        {
            return DeviceFailureResponse(request.RequestId, "Plugin host device command payload is required.");
        }

        if (plugin is not IOpenLineOpsDeviceCommandPlugin deviceCommandPlugin)
        {
            return DeviceSuccessResponse(
                request.RequestId,
                PluginDeviceCommandInvocationResult.Rejected(
                    $"Plugin '{plugin.Manifest.Id}' does not implement {nameof(IOpenLineOpsDeviceCommandPlugin)}."));
        }

        try
        {
            var result = await deviceCommandPlugin
                .ExecuteDeviceCommandAsync(ToPluginContractRequest(request.Payload), cancellationToken)
                .ConfigureAwait(false);

            return DeviceSuccessResponse(request.RequestId, ToInvocationResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DeviceSuccessResponse(
                request.RequestId,
                PluginDeviceCommandInvocationResult.Failed(
                    $"Plugin device command execution failed: {exception.Message}"));
        }
    }

    private static async ValueTask<ExternalProcessCommandProtocolResponse> HandleProcessCommandAsync(
        IOpenLineOpsPlugin plugin,
        string line,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ExternalProcessCommandProtocolRequest>(line, JsonOptions);
        if (request is null)
        {
            return ProcessFailureResponse("", "Plugin host process command request is empty.");
        }

        if (request.Payload is null)
        {
            return ProcessFailureResponse(request.RequestId, "Plugin host process command payload is required.");
        }

        if (plugin is not IOpenLineOpsProcessNodePlugin processNodePlugin)
        {
            return ProcessSuccessResponse(
                request.RequestId,
                PluginProcessCommandInvocationResult.Rejected(
                    $"Plugin '{plugin.Manifest.Id}' does not implement {nameof(IOpenLineOpsProcessNodePlugin)}."));
        }

        try
        {
            var result = await processNodePlugin
                .ExecuteProcessCommandAsync(ToPluginContractRequest(request.Payload), cancellationToken)
                .ConfigureAwait(false);

            return ProcessSuccessResponse(request.RequestId, ToInvocationResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ProcessSuccessResponse(
                request.RequestId,
                PluginProcessCommandInvocationResult.Failed(
                    $"Plugin process command execution failed: {exception.Message}"));
        }
    }

    private static PluginDeviceCommandExecutionRequest ToPluginContractRequest(
        PluginDeviceCommandInvocationRequest request)
    {
        return new PluginDeviceCommandExecutionRequest(
            request.DeviceInstanceId,
            request.CommandDefinitionId,
            request.Capability,
            request.CommandName,
            request.InputPayload,
            request.TimeoutMilliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
    }

    private static PluginProcessCommandExecutionRequest ToPluginContractRequest(
        PluginProcessCommandInvocationRequest request)
    {
        return new PluginProcessCommandExecutionRequest(
            request.SessionId,
            request.StationId,
            request.ConfigurationSnapshotId,
            request.StepId,
            request.CommandId,
            request.NodeId,
            request.CommandDefinitionId,
            request.Capability,
            request.CommandName,
            request.InputPayload,
            request.TimeoutMilliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
    }

    private static PluginDeviceCommandInvocationResult ToInvocationResult(
        PluginDeviceCommandExecutionResult result)
    {
        return result.Outcome switch
        {
            PluginDeviceCommandExecutionOutcome.Completed => PluginDeviceCommandInvocationResult.Completed(
                result.ResultPayload),
            PluginDeviceCommandExecutionOutcome.Failed => PluginDeviceCommandInvocationResult.Failed(
                result.FailureReason ?? "Plugin device command failed."),
            PluginDeviceCommandExecutionOutcome.Rejected => PluginDeviceCommandInvocationResult.Rejected(
                result.FailureReason ?? "Plugin device command rejected."),
            PluginDeviceCommandExecutionOutcome.TimedOut => PluginDeviceCommandInvocationResult.TimedOut(
                result.FailureReason ?? "Plugin device command timed out."),
            _ => PluginDeviceCommandInvocationResult.Failed(
                $"Plugin device command returned unsupported outcome '{result.Outcome}'.")
        };
    }

    private static PluginProcessCommandInvocationResult ToInvocationResult(
        PluginProcessCommandExecutionResult result)
    {
        return result.Outcome switch
        {
            PluginProcessCommandExecutionOutcome.Completed => PluginProcessCommandInvocationResult.Completed(
                result.ResultPayload),
            PluginProcessCommandExecutionOutcome.Failed => PluginProcessCommandInvocationResult.Failed(
                result.FailureReason ?? "Plugin process command failed."),
            PluginProcessCommandExecutionOutcome.Rejected => PluginProcessCommandInvocationResult.Rejected(
                result.FailureReason ?? "Plugin process command rejected."),
            PluginProcessCommandExecutionOutcome.TimedOut => PluginProcessCommandInvocationResult.TimedOut(
                result.FailureReason ?? "Plugin process command timed out."),
            PluginProcessCommandExecutionOutcome.Canceled => PluginProcessCommandInvocationResult.Canceled(
                result.FailureReason ?? "Plugin process command canceled."),
            _ => PluginProcessCommandInvocationResult.Failed(
                $"Plugin process command returned unsupported outcome '{result.Outcome}'.")
        };
    }

    private static ExternalDeviceCommandProtocolResponse DeviceSuccessResponse(
        string requestId,
        PluginDeviceCommandInvocationResult result)
    {
        return new ExternalDeviceCommandProtocolResponse(
            "device-command-result",
            requestId,
            result,
            null);
    }

    private static ExternalDeviceCommandProtocolResponse DeviceFailureResponse(
        string requestId,
        string error)
    {
        return new ExternalDeviceCommandProtocolResponse(
            "device-command-result",
            requestId,
            null,
            error);
    }

    private static ExternalProcessCommandProtocolResponse ProcessSuccessResponse(
        string requestId,
        PluginProcessCommandInvocationResult result)
    {
        return new ExternalProcessCommandProtocolResponse(
            "process-command-result",
            requestId,
            result,
            null);
    }

    private static ExternalProcessCommandProtocolResponse ProcessFailureResponse(
        string requestId,
        string error)
    {
        return new ExternalProcessCommandProtocolResponse(
            "process-command-result",
            requestId,
            null,
            error);
    }

    private sealed record ExternalProtocolEnvelope(
        string MessageType,
        string RequestId,
        string? SessionId = null);

    private sealed record ExternalDeviceCommandProtocolRequest(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationRequest? Payload);

    private sealed record ExternalDeviceCommandProtocolResponse(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationResult? Payload,
        string? Error);

    private sealed record ExternalProcessCommandProtocolRequest(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationRequest? Payload);

    private sealed record ExternalProcessCommandProtocolResponse(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationResult? Payload,
        string? Error);

    private sealed record ExternalSignalReadPayload(
        string SessionId,
        IReadOnlyList<string> SignalIds)
    {
        public PluginDeviceSignalReadRequest ToContract() =>
            new(SessionId, SignalIds);
    }

    private sealed record ExternalSignalWritePayload(
        string SessionId,
        PluginDeviceCommandEnvelope Command,
        IReadOnlyList<PluginDeviceSignalWrite> Writes)
    {
        public PluginDeviceSignalWriteRequest ToContract() =>
            new(SessionId, Command, Writes);
    }

    private sealed record ExternalSignalSubscriptionPayload(
        string SessionId,
        string SubscriptionId,
        IReadOnlyList<string> SignalIds,
        long? ResumeAfterSequence,
        TimeSpan? MinimumSamplingInterval)
    {
        public PluginDeviceSignalSubscriptionRequest ToContract() =>
            new(
                SessionId,
                SubscriptionId,
                SignalIds,
                ResumeAfterSequence,
                MinimumSamplingInterval);
    }

    private sealed record ExternalDeviceSessionProtocolRequest<TPayload>(
        string MessageType,
        string? SessionId,
        string RequestId,
        TPayload? Payload)
        where TPayload : class;

    private sealed record ParsedDeviceSessionProtocolRequest<TPayload>(
        string MessageType,
        string? SessionId,
        string RequestId,
        TPayload Payload)
        where TPayload : class;

    private sealed record ExternalDeviceSessionProtocolResponse(
        string MessageType,
        string? SessionId,
        string RequestId,
        long Sequence,
        object? Payload,
        string? Error);

    private sealed record ExternalProtocolAcknowledgement(bool Accepted)
    {
        public static ExternalProtocolAcknowledgement Success { get; } = new(true);
    }

    private sealed record ExternalHeartbeatPayload(DateTimeOffset ObservedAtUtc);

    private sealed record ExternalSubscriptionAccepted(string SubscriptionId);

    private sealed record ExternalSubscriptionCompleted(
        string SubscriptionId,
        string Reason);

    private sealed record ExternalSubscriptionFailed(string SubscriptionId);

    private sealed class ActiveSubscription(
        string sessionId,
        string subscriptionId,
        CancellationTokenSource cancellation)
    {
        public string SessionId { get; } = sessionId;

        public string SubscriptionId { get; } = subscriptionId;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class ProtocolFrameWriter(TextWriter output) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _sequence;

        public async ValueTask WriteLegacyAsync(
            object response,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteFrameAsync(response, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask WriteSessionAsync(
            string messageType,
            string? sessionId,
            string requestId,
            object? payload,
            string? error,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sequence = checked(++_sequence);
                await WriteFrameAsync(
                        new ExternalDeviceSessionProtocolResponse(
                            messageType,
                            sessionId,
                            requestId,
                            sequence,
                            payload,
                            error),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async ValueTask WriteFrameAsync(
            object response,
            CancellationToken cancellationToken)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions))
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}
