using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Scripting;

public sealed class RuntimeAutomationPlanExpander
{
    public const int MaximumActionCount = 1_000;

    private const double MaximumWaitDurationMilliseconds = uint.MaxValue - 1d;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly int _maximumActionCount;

    public RuntimeAutomationPlanExpander()
    {
        _maximumActionCount = MaximumActionCount;
    }

    public Result<RuntimeAutomationPlanExpansion> Expand(
        ExecutableRuntimeNode parentNode,
        string? scriptPayload)
    {
        ArgumentNullException.ThrowIfNull(parentNode);

        if (parentNode.DynamicChildren is null || string.IsNullOrWhiteSpace(scriptPayload))
        {
            return Result.Success(RuntimeAutomationPlanExpansion.Empty(scriptPayload));
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(scriptPayload) as JsonObject;
        }
        catch (JsonException)
        {
            return Result.Success(RuntimeAutomationPlanExpansion.Empty(scriptPayload));
        }

        if (root is null || !root.TryGetPropertyValue("automation_plan", out var planNode))
        {
            return Result.Success(RuntimeAutomationPlanExpansion.Empty(scriptPayload));
        }

        if (planNode is not JsonArray plan)
        {
            return Failure("PythonScript automation_plan must be a JSON array.");
        }

        if (plan.Count > _maximumActionCount)
        {
            return Failure(
                $"PythonScript automation_plan contains {plan.Count} actions; the maximum is {_maximumActionCount}.");
        }

        var actions = new List<RuntimeAutomationPlanAction>(plan.Count);
        for (var index = 0; index < plan.Count; index++)
        {
            if (plan[index] is not JsonObject action)
            {
                return Failure($"Automation action {index + 1} must be a JSON object.");
            }

            var expanded = ExpandAction(parentNode, action, index);
            if (expanded.IsFailure)
            {
                return Result.Failure<RuntimeAutomationPlanExpansion>(expanded.Error);
            }

            actions.Add(expanded.Value);
        }

        return Result.Success(new RuntimeAutomationPlanExpansion(
            scriptPayload,
            HasAutomationPlan: true,
            actions));
    }

    private static Result<RuntimeAutomationPlanAction> ExpandAction(
        ExecutableRuntimeNode parentNode,
        JsonObject action,
        int index)
    {
        var slot = parentNode.DynamicChildren!;
        int sequence;
        try
        {
            sequence = checked(slot.SequenceBase + index);
        }
        catch (OverflowException)
        {
            return ActionFailure(index, "sequence is outside the supported range.");
        }

        var actionType = ReadString(action, "type")?.Trim();
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return ActionFailure(index, "must declare a type.");
        }

        var timeoutResult = ReadTimeout(action, parentNode.Timeout, index);
        if (timeoutResult.IsFailure)
        {
            return Result.Failure<RuntimeAutomationPlanAction>(timeoutResult.Error);
        }

        var commandResult = ResolveCommand(action, actionType, index);
        if (commandResult.IsFailure)
        {
            return Result.Failure<RuntimeAutomationPlanAction>(commandResult.Error);
        }

        var command = commandResult.Value;
        if (string.Equals(command.Capability, RuntimeScriptCommand.PythonCapability, StringComparison.OrdinalIgnoreCase)
            && string.Equals(command.CommandName, RuntimeScriptCommand.PythonCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailure(index, "cannot recursively execute a Python automation plan in Flow IR v1.");
        }

        var actionId = $"{slot.ChildActionIdPrefix}{sequence}";
        var nodeId = $"{slot.SlotId}:node:{sequence}";
        var displayName = ReadString(action, "displayName", "display_name")?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = $"{parentNode.DisplayName} / {actionType} #{sequence}";
        }

        var node = new ExecutableRuntimeNode(
            new RuntimeNodeId(nodeId),
            displayName,
            new RuntimeCapabilityId(command.Capability),
            command.CommandName,
            timeoutResult.Value,
            command.InputPayload,
            new RuntimeActionId(actionId));

        return Result.Success(new RuntimeAutomationPlanAction(
            sequence,
            actionType,
            node));
    }

    private static Result<ResolvedActionCommand> ResolveCommand(
        JsonObject action,
        string actionType,
        int index)
    {
        if (string.Equals(actionType, "flow.wait", StringComparison.OrdinalIgnoreCase))
        {
            var duration = ReadNumber(action, "durationMilliseconds", "duration_ms", "DURATION_MS");
            if (duration is null && HasAnyProperty(
                    action,
                    "durationMilliseconds",
                    "duration_ms",
                    "DURATION_MS"))
            {
                return ActionCommandFailure(index, "flow.wait duration must be numeric.");
            }

            var resolvedDuration = duration ?? 0;
            if (!double.IsFinite(resolvedDuration)
                || resolvedDuration < 0
                || resolvedDuration > MaximumWaitDurationMilliseconds)
            {
                return ActionCommandFailure(
                    index,
                    "flow.wait duration must be a finite, non-negative value inside the supported timer range.");
            }

            var payload = new JsonObject
            {
                ["durationMilliseconds"] = resolvedDuration
            }.ToJsonString(JsonOptions);

            return Result.Success(new ResolvedActionCommand(
                RuntimeFlowCommand.Capability,
                RuntimeFlowCommand.WaitCommandName,
                payload));
        }

        if (string.Equals(actionType, "command.execute", StringComparison.OrdinalIgnoreCase))
        {
            var capability = ReadString(action, "capability", "targetCapability")?.Trim();
            if (string.IsNullOrWhiteSpace(capability))
            {
                return ActionCommandFailure(index, "command.execute must declare a capability.");
            }

            var commandName = ReadString(action, "command", "commandName")?.Trim();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return ActionCommandFailure(index, "command.execute must declare a command.");
            }

            return Result.Success(new ResolvedActionCommand(
                capability,
                commandName,
                ReadActionInputPayload(action)));
        }

        var mapped = actionType.ToLowerInvariant() switch
        {
            "axis.move" => new ResolvedActionCommand("motion.axis", "MoveAxis", action.ToJsonString(JsonOptions)),
            "io.light" => new ResolvedActionCommand("io.light", "SetLight", action.ToJsonString(JsonOptions)),
            "motor.rotate" => new ResolvedActionCommand("motion.motor", "RotateMotor", action.ToJsonString(JsonOptions)),
            _ => null
        };

        return mapped is null
            ? ActionCommandFailure(index, $"type '{actionType}' is not supported.")
            : Result.Success(mapped);
    }

    private static Result<TimeSpan> ReadTimeout(
        JsonObject action,
        TimeSpan defaultTimeout,
        int index)
    {
        var timeoutMilliseconds = ReadNumber(
            action,
            "timeoutMilliseconds",
            "timeout_ms",
            "TIMEOUT_MS");
        if (timeoutMilliseconds is null && HasAnyProperty(
                action,
                "timeoutMilliseconds",
                "timeout_ms",
                "TIMEOUT_MS"))
        {
            return Result.Failure<TimeSpan>(Invalid(
                $"Automation action {index + 1} timeout must be numeric."));
        }

        if (timeoutMilliseconds is null)
        {
            return Result.Success(defaultTimeout);
        }

        if (!double.IsFinite(timeoutMilliseconds.Value) || timeoutMilliseconds.Value <= 0)
        {
            return Result.Failure<TimeSpan>(Invalid(
                $"Automation action {index + 1} timeout must be a finite number greater than zero."));
        }

        try
        {
            return Result.Success(TimeSpan.FromMilliseconds(timeoutMilliseconds.Value));
        }
        catch (OverflowException)
        {
            return Result.Failure<TimeSpan>(Invalid(
                $"Automation action {index + 1} timeout is outside the supported range."));
        }
    }

    private static string? ReadActionInputPayload(JsonObject action)
    {
        if (!action.TryGetPropertyValue("payload", out var payloadNode) || payloadNode is null)
        {
            return null;
        }

        return payloadNode is JsonValue payloadValue
            && payloadValue.TryGetValue<string>(out var text)
                ? text
                : payloadNode.ToJsonString(JsonOptions);
    }

    private static string? ReadString(JsonObject action, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (action.TryGetPropertyValue(propertyName, out var node)
                && node is JsonValue value
                && value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return null;
    }

    private static double? ReadNumber(JsonObject action, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!action.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool HasAnyProperty(JsonObject action, params string[] propertyNames)
    {
        return propertyNames.Any(action.ContainsKey);
    }

    private static Result<RuntimeAutomationPlanExpansion> Failure(string message)
    {
        return Result.Failure<RuntimeAutomationPlanExpansion>(Invalid(message));
    }

    private static Result<RuntimeAutomationPlanAction> ActionFailure(int index, string message)
    {
        return Result.Failure<RuntimeAutomationPlanAction>(Invalid(
            $"Automation action {index + 1} {message}"));
    }

    private static Result<ResolvedActionCommand> ActionCommandFailure(int index, string message)
    {
        return Result.Failure<ResolvedActionCommand>(Invalid(
            $"Automation action {index + 1} {message}"));
    }

    private static ApplicationError Invalid(string message)
    {
        return ApplicationError.Validation("Runtime.AutomationPlanInvalid", message);
    }

    private sealed record ResolvedActionCommand(
        string Capability,
        string CommandName,
        string? InputPayload);
}

public sealed record RuntimeAutomationPlanExpansion(
    string? SourcePayload,
    bool HasAutomationPlan,
    IReadOnlyList<RuntimeAutomationPlanAction> Actions)
{
    public static RuntimeAutomationPlanExpansion Empty(string? sourcePayload) =>
        new(sourcePayload, HasAutomationPlan: false, []);

    public string? CreateCompletionPayload(
        IReadOnlyCollection<RuntimeAutomationPlanActionResult> results)
    {
        if (!HasAutomationPlan || string.IsNullOrWhiteSpace(SourcePayload))
        {
            return SourcePayload;
        }

        var root = JsonNode.Parse(SourcePayload) as JsonObject;
        if (root is null)
        {
            return SourcePayload;
        }

        root["automation_dispatch"] = new JsonArray(results
            .OrderBy(result => result.Sequence)
            .Select(result => (JsonNode)new JsonObject
            {
                ["sequence"] = result.Sequence,
                ["actionId"] = result.ActionId,
                ["nodeId"] = result.NodeId,
                ["type"] = result.ActionType,
                ["outcome"] = result.Outcome.ToString(),
                ["payload"] = result.Payload,
                ["reason"] = result.Reason
            })
            .ToArray());

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

public sealed record RuntimeAutomationPlanAction(
    int Sequence,
    string ActionType,
    ExecutableRuntimeNode Node);

public sealed record RuntimeAutomationPlanActionResult(
    int Sequence,
    string ActionId,
    string NodeId,
    string ActionType,
    RuntimeCommandExecutionOutcome Outcome,
    string? Payload,
    string? Reason);
