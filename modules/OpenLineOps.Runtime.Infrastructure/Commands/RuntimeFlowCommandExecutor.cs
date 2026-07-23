using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class RuntimeFlowCommandExecutor : IRuntimeCommandExecutor
{
    private const double MaximumTimerDelayMilliseconds = uint.MaxValue - 1d;

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RuntimeFlowCommand.IsResultPatch(context))
        {
            return ExecuteResultPatch(context);
        }

        if (!RuntimeFlowCommand.IsWait(context))
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Runtime flow command '{context.TargetCapability.Value}/{context.CommandName}' is not supported.");
        }

        if (!TryReadDuration(context.InputPayload, out var duration, out var error))
        {
            return RuntimeCommandExecutionResult.Rejected(
                error ?? "Runtime flow wait payload is invalid.");
        }

        if (context.Timeout <= TimeSpan.Zero
            || context.Timeout.TotalMilliseconds > MaximumTimerDelayMilliseconds)
        {
            return RuntimeCommandExecutionResult.Rejected(
                "Runtime flow wait timeout must be positive and inside the supported timer range.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.Canceled("Runtime flow wait was canceled.");
        }

        if (duration == TimeSpan.Zero)
        {
            return RuntimeCommandExecutionResult.Completed(context.InputPayload);
        }

        using var timeoutCancellation = new CancellationTokenSource(context.Timeout);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            await Task.Delay(duration, linkedCancellation.Token).ConfigureAwait(false);
            return RuntimeCommandExecutionResult.Completed(context.InputPayload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.Canceled("Runtime flow wait was canceled.");
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.TimedOut("Runtime flow wait timed out.");
        }
    }

    private static RuntimeCommandExecutionResult ExecuteResultPatch(
        RuntimeCommandExecutionContext context)
    {
        try
        {
            var payload = JsonNode.Parse(context.InputPayload ?? string.Empty) as JsonObject;
            if (payload?["assignments"] is not JsonArray assignments)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    "Runtime result patch payload must contain an assignments array.");
            }

            var result = new JsonObject();
            foreach (var assignmentNode in assignments)
            {
                if (assignmentNode is not JsonObject assignment
                    || assignment["key"] is not JsonValue keyValue
                    || !keyValue.TryGetValue<string>(out var key)
                    || string.IsNullOrWhiteSpace(key))
                {
                    return RuntimeCommandExecutionResult.Rejected(
                        "Runtime result patch assignments must contain canonical string keys.");
                }

                result[key] = ResolveResultValue(assignment["value"], context);
            }

            return RuntimeCommandExecutionResult.Completed(result.ToJsonString());
        }
        catch (JsonException exception)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Runtime result patch payload is invalid JSON: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return RuntimeCommandExecutionResult.Rejected(exception.Message);
        }
    }

    private static JsonNode? ResolveResultValue(
        JsonNode? value,
        RuntimeCommandExecutionContext context)
    {
        if (value is JsonObject productionInputReference
            && productionInputReference.Count == 1
            && productionInputReference["$productionInput"] is JsonValue inputKeyValue
            && inputKeyValue.TryGetValue<string>(out var inputKey))
        {
            if (!context.ProductionInputs.TryGetValue(inputKey, out var productionInput))
            {
                throw new InvalidDataException(
                    $"Runtime result patch references undeclared Production Context input '{inputKey}'.");
            }

            return ToJsonValue(productionInput);
        }

        if (value is not JsonObject contextValue
            || contextValue.Count != 1
            || contextValue["$context"] is not JsonValue contextNameValue
            || !contextNameValue.TryGetValue<string>(out var contextName))
        {
            return value?.DeepClone();
        }

        return contextName switch
        {
            "nodeId" => JsonValue.Create(context.NodeId.Value),
            "timestampUtc" => JsonValue.Create(DateTimeOffset.UtcNow),
            "inputPayload" => JsonValue.Create(context.InputPayload),
            _ => value.DeepClone()
        };
    }

    private static JsonValue ToJsonValue(ProductionContextValue value) => value.Kind switch
    {
        ProductionContextValueKind.Text or ProductionContextValueKind.DateTimeUtc =>
            JsonValue.Create(value.CanonicalValue)!,
        ProductionContextValueKind.Boolean =>
            JsonValue.Create(string.Equals(value.CanonicalValue, "true", StringComparison.Ordinal))!,
        ProductionContextValueKind.WholeNumber =>
            JsonValue.Create(long.Parse(value.CanonicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture))!,
        ProductionContextValueKind.FixedPoint =>
            JsonValue.Create(decimal.Parse(value.CanonicalValue, NumberStyles.Number, CultureInfo.InvariantCulture))!,
        _ => throw new InvalidDataException(
            $"Unsupported Production Context value kind {value.Kind}.")
    };

    private static bool TryReadDuration(
        string? inputPayload,
        out TimeSpan duration,
        out string? error)
    {
        duration = TimeSpan.Zero;
        error = null;

        if (string.IsNullOrWhiteSpace(inputPayload))
        {
            error = "Runtime flow wait payload is required.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(inputPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Runtime flow wait payload must be a JSON object.";
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1
                || !string.Equals(properties[0].Name, "durationMilliseconds", StringComparison.Ordinal))
            {
                error = "Runtime flow wait payload must contain only the canonical durationMilliseconds field.";
                return false;
            }

            var durationValue = properties[0].Value;
            if (durationValue.ValueKind != JsonValueKind.Number
                || !durationValue.TryGetDouble(out var durationMilliseconds))
            {
                error = "Runtime flow wait durationMilliseconds must be a JSON number.";
                return false;
            }

            if (!double.IsFinite(durationMilliseconds) || durationMilliseconds < 0)
            {
                error = "Runtime flow wait durationMilliseconds must be a finite number greater than or equal to zero.";
                return false;
            }

            if (durationMilliseconds > MaximumTimerDelayMilliseconds)
            {
                error = "Runtime flow wait durationMilliseconds is outside the supported timer range.";
                return false;
            }

            try
            {
                duration = TimeSpan.FromMilliseconds(durationMilliseconds);
                return true;
            }
            catch (OverflowException)
            {
                error = "Runtime flow wait durationMilliseconds is too large.";
                return false;
            }
        }
        catch (JsonException exception)
        {
            error = $"Runtime flow wait payload is invalid JSON: {exception.Message}";
            return false;
        }
    }

}
