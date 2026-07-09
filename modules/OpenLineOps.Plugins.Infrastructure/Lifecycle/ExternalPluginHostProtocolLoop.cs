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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await HandleMessageAsync(plugin, line, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<object> HandleMessageAsync(
        IOpenLineOpsPlugin plugin,
        string line,
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

        return envelope.MessageType switch
        {
            "device-command" => await HandleDeviceCommandAsync(plugin, line, cancellationToken).ConfigureAwait(false),
            "process-command" => await HandleProcessCommandAsync(plugin, line, cancellationToken).ConfigureAwait(false),
            _ => DeviceFailureResponse(
                envelope.RequestId,
                $"Plugin host protocol message type '{envelope.MessageType}' is not supported.")
        };
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
        string RequestId);

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
}
