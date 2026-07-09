using OpenLineOps.Devices.Application.Execution;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ConfiguredSimulatorDeviceCommandExecutor : IDeviceCommandExecutor
{
    private readonly ConfiguredSimulatorDeviceCommandOptions _options;

    public ConfiguredSimulatorDeviceCommandExecutor(ConfiguredSimulatorDeviceCommandOptions options)
    {
        _options = options;
    }

    public async Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var rule = FindRule(request);
        var outcomeText = rule?.Outcome ?? _options.DefaultOutcome;
        if (!TryParseOutcome(outcomeText, out var outcome))
        {
            return DeviceCommandExecutionResult.Rejected(
                $"Configured simulator outcome '{outcomeText}' is not supported.");
        }

        var delayMilliseconds = Math.Max(0, rule?.DelayMilliseconds ?? _options.DefaultDelayMilliseconds);
        if (delayMilliseconds > request.Timeout.TotalMilliseconds)
        {
            return DeviceCommandExecutionResult.TimedOut(
                $"Configured simulator delay {delayMilliseconds}ms exceeded timeout {request.Timeout.TotalMilliseconds:0}ms.");
        }

        if (delayMilliseconds > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken).ConfigureAwait(false);
        }

        return outcome switch
        {
            DeviceCommandExecutionOutcome.Completed => DeviceCommandExecutionResult.Completed(
                BuildResultPayload(request, rule)),
            DeviceCommandExecutionOutcome.Failed => DeviceCommandExecutionResult.Failed(
                BuildFailureReason(rule, "Configured simulator command failed.")),
            DeviceCommandExecutionOutcome.Rejected => DeviceCommandExecutionResult.Rejected(
                BuildFailureReason(rule, "Configured simulator command rejected.")),
            DeviceCommandExecutionOutcome.TimedOut => DeviceCommandExecutionResult.TimedOut(
                BuildFailureReason(rule, "Configured simulator command timed out.")),
            _ => DeviceCommandExecutionResult.Rejected(
                $"Configured simulator outcome '{outcome}' is not supported.")
        };
    }

    private ConfiguredSimulatorDeviceCommandRule? FindRule(DeviceCommandExecutionRequest request)
    {
        return _options.Commands.FirstOrDefault(rule => Matches(rule, request));
    }

    private static bool Matches(
        ConfiguredSimulatorDeviceCommandRule rule,
        DeviceCommandExecutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(rule.CommandDefinitionId))
        {
            return string.Equals(
                rule.CommandDefinitionId.Trim(),
                request.CommandDefinitionId.Value,
                StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(rule.CapabilityId)
            && !string.IsNullOrWhiteSpace(rule.CommandName))
        {
            return string.Equals(
                    rule.CapabilityId.Trim(),
                    request.CapabilityId.Value,
                    StringComparison.Ordinal)
                && string.Equals(
                    rule.CommandName.Trim(),
                    request.CommandName,
                    StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(rule.CommandName)
            && string.Equals(
                rule.CommandName.Trim(),
                request.CommandName,
                StringComparison.OrdinalIgnoreCase);
    }

    private string? BuildResultPayload(
        DeviceCommandExecutionRequest request,
        ConfiguredSimulatorDeviceCommandRule? rule)
    {
        if (!string.IsNullOrWhiteSpace(rule?.ResultPayload))
        {
            return rule.ResultPayload.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultResultPayload))
        {
            return _options.DefaultResultPayload.Trim();
        }

        return $$"""
            {"simulator":"configured","deviceInstanceId":"{{request.DeviceInstanceId.Value}}","commandDefinitionId":"{{request.CommandDefinitionId.Value}}","capabilityId":"{{request.CapabilityId.Value}}","commandName":"{{request.CommandName}}","inputPayload":{{FormatNullableJsonString(request.InputPayload)}}}
            """;
    }

    private string BuildFailureReason(
        ConfiguredSimulatorDeviceCommandRule? rule,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(rule?.FailureReason))
        {
            return rule.FailureReason.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultFailureReason))
        {
            return _options.DefaultFailureReason.Trim();
        }

        return fallback;
    }

    private static bool TryParseOutcome(
        string? value,
        out DeviceCommandExecutionOutcome outcome)
    {
        return Enum.TryParse(value, ignoreCase: true, out outcome)
            && Enum.IsDefined(outcome);
    }

    private static string FormatNullableJsonString(string? value)
    {
        return value is null
            ? "null"
            : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
