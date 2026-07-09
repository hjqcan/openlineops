using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Scripting;

public sealed class RuntimeAutomationPlanDispatcher
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<RuntimeCommandExecutionResult> DispatchAsync(
        RuntimeScriptExecutionRequest request,
        RuntimeCommandExecutionResult scriptResult,
        Func<RuntimeCommandExecutionContext, CancellationToken, ValueTask<RuntimeCommandExecutionResult>> executeCommandAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executeCommandAsync);
        cancellationToken.ThrowIfCancellationRequested();

        if (scriptResult.Outcome != RuntimeCommandExecutionOutcome.Completed
            || string.IsNullOrWhiteSpace(scriptResult.Payload))
        {
            return scriptResult;
        }

        var root = TryParseObject(scriptResult.Payload);
        if (root is null)
        {
            return scriptResult;
        }

        if (!root.TryGetPropertyValue("automation_plan", out var automationPlanNode))
        {
            return scriptResult;
        }

        if (automationPlanNode is not JsonArray automationPlan)
        {
            return RuntimeCommandExecutionResult.Rejected(
                "PythonScript automation_plan must be a JSON array.");
        }

        var dispatchResults = new JsonArray();
        var sequence = 0;
        foreach (var actionNode in automationPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sequence += 1;

            if (actionNode is not JsonObject action)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"Automation action {sequence} must be a JSON object.");
            }

            var dispatchResult = await DispatchActionAsync(
                request,
                action,
                sequence,
                executeCommandAsync,
                cancellationToken).ConfigureAwait(false);

            dispatchResults.Add(ToJsonObject(sequence, action, dispatchResult));

            if (dispatchResult.Outcome != RuntimeCommandExecutionOutcome.Completed)
            {
                root["automation_dispatch"] = dispatchResults;
                return new RuntimeCommandExecutionResult(
                    dispatchResult.Outcome,
                    root.ToJsonString(_jsonOptions),
                    $"Automation action {sequence} ({ReadActionType(action) ?? "unknown"}) {dispatchResult.Outcome}: {dispatchResult.Reason}");
            }
        }

        root["automation_dispatch"] = dispatchResults;
        return RuntimeCommandExecutionResult.Completed(root.ToJsonString(_jsonOptions));
    }

    private async ValueTask<RuntimeCommandExecutionResult> DispatchActionAsync(
        RuntimeScriptExecutionRequest request,
        JsonObject action,
        int sequence,
        Func<RuntimeCommandExecutionContext, CancellationToken, ValueTask<RuntimeCommandExecutionResult>> executeCommandAsync,
        CancellationToken cancellationToken)
    {
        var actionType = ReadActionType(action);
        if (string.Equals(actionType, "flow.wait", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWaitAsync(action, cancellationToken).ConfigureAwait(false);
        }

        if (!TryCreateActionCommandContext(request, action, sequence, out var context, out var error))
        {
            return RuntimeCommandExecutionResult.Rejected(error ?? "Automation action is invalid.");
        }

        return await executeCommandAsync(context!, cancellationToken).ConfigureAwait(false);
    }

    private bool TryCreateActionCommandContext(
        RuntimeScriptExecutionRequest request,
        JsonObject action,
        int sequence,
        out RuntimeCommandExecutionContext? context,
        out string? error)
    {
        context = null;
        error = null;

        var actionType = ReadActionType(action);
        if (string.IsNullOrWhiteSpace(actionType))
        {
            error = $"Automation action {sequence} must declare a type.";
            return false;
        }

        if (!TryResolveActionCommand(action, actionType, sequence, out var command, out error))
        {
            return false;
        }

        var parent = request.CommandContext;
        if (!TryReadActionTimeout(action, parent.Timeout, out var timeout, out error))
        {
            return false;
        }

        context = new RuntimeCommandExecutionContext(
            parent.SessionId,
            parent.StationId,
            parent.ConfigurationSnapshotId,
            parent.StepId,
            RuntimeCommandId.New(),
            new RuntimeNodeId($"{parent.NodeId.Value}.automation.{sequence}"),
            new RuntimeCapabilityId(command!.Capability),
            command.CommandName,
            command.InputPayload,
            timeout);
        return true;
    }

    private bool TryResolveActionCommand(
        JsonObject action,
        string actionType,
        int sequence,
        out ResolvedActionCommand? command,
        out string? error)
    {
        command = null;
        error = null;

        if (string.Equals(actionType, "command.execute", StringComparison.OrdinalIgnoreCase))
        {
            var capability = ReadString(action, "capability", "targetCapability");
            if (string.IsNullOrWhiteSpace(capability))
            {
                error = $"Automation action {sequence} command.execute must declare a capability.";
                return false;
            }

            var commandName = ReadString(action, "command", "commandName");
            if (string.IsNullOrWhiteSpace(commandName))
            {
                error = $"Automation action {sequence} command.execute must declare a command.";
                return false;
            }

            command = new ResolvedActionCommand(
                capability.Trim(),
                commandName.Trim(),
                ReadActionInputPayload(action));
            return true;
        }

        var mappedCommand = MapActionToCommand(actionType);
        if (mappedCommand is null)
        {
            error = $"Automation action type '{actionType}' is not supported.";
            return false;
        }

        command = new ResolvedActionCommand(
            mappedCommand.Value.Capability,
            mappedCommand.Value.CommandName,
            action.ToJsonString(_jsonOptions));
        return true;
    }

    private string? ReadActionInputPayload(JsonObject action)
    {
        if (!action.TryGetPropertyValue("payload", out var payloadNode))
        {
            return null;
        }

        if (payloadNode is null)
        {
            return null;
        }

        if (payloadNode is JsonValue payloadValue
            && payloadValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return payloadNode.ToJsonString(_jsonOptions);
    }

    private async ValueTask<RuntimeCommandExecutionResult> ExecuteWaitAsync(
        JsonObject action,
        CancellationToken cancellationToken)
    {
        var durationMilliseconds = ReadNumber(action, "duration_ms")
            ?? ReadNumber(action, "DURATION_MS")
            ?? 0;
        if (durationMilliseconds < 0)
        {
            return RuntimeCommandExecutionResult.Rejected(
                "Automation wait duration must be greater than or equal to zero.");
        }

        if (durationMilliseconds > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(durationMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }

        return RuntimeCommandExecutionResult.Completed(action.ToJsonString(_jsonOptions));
    }

    private static (string Capability, string CommandName)? MapActionToCommand(string actionType)
    {
        return actionType.ToLowerInvariant() switch
        {
            "axis.move" => ("motion.axis", "MoveAxis"),
            "io.light" => ("io.light", "SetLight"),
            "motor.rotate" => ("motion.motor", "RotateMotor"),
            _ => null
        };
    }

    private static JsonObject? TryParseObject(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject ToJsonObject(
        int sequence,
        JsonObject action,
        RuntimeCommandExecutionResult result)
    {
        return new JsonObject
        {
            ["sequence"] = sequence,
            ["type"] = ReadActionType(action),
            ["outcome"] = result.Outcome.ToString(),
            ["payload"] = result.Payload,
            ["reason"] = result.Reason
        };
    }

    private static string? ReadActionType(JsonObject action)
    {
        if (!action.TryGetPropertyValue("type", out var typeNode)
            || typeNode is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var type)
            ? type
            : null;
    }

    private static double? ReadNumber(JsonObject action, string propertyName)
    {
        if (!action.TryGetPropertyValue(propertyName, out var valueNode)
            || valueNode is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static bool TryReadActionTimeout(
        JsonObject action,
        TimeSpan defaultTimeout,
        out TimeSpan timeout,
        out string? error)
    {
        timeout = defaultTimeout;
        error = null;

        var timeoutMilliseconds = ReadNumber(action, "timeout_ms")
            ?? ReadNumber(action, "timeoutMilliseconds")
            ?? ReadNumber(action, "TIMEOUT_MS");
        if (timeoutMilliseconds is null)
        {
            return true;
        }

        if (!double.IsFinite(timeoutMilliseconds.Value)
            || timeoutMilliseconds.Value <= 0)
        {
            error = "Automation action timeout_ms must be greater than zero.";
            return false;
        }

        try
        {
            timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds.Value);
            return true;
        }
        catch (OverflowException)
        {
            error = "Automation action timeout_ms is too large.";
            return false;
        }
    }

    private static string? ReadString(JsonObject action, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!action.TryGetPropertyValue(propertyName, out var valueNode)
                || valueNode is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return null;
    }

    private sealed record ResolvedActionCommand(
        string Capability,
        string CommandName,
        string? InputPayload);
}
