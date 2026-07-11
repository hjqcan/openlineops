using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandRouteRequest
{
    public DeviceCommandRouteRequest(
        string runtimeSessionId,
        string productionRunId,
        string productionLineDefinitionId,
        string operationId,
        int operationAttempt,
        string productModelId,
        string productionUnitIdentityInputKey,
        string productionUnitIdentityValue,
        string? lotId,
        string? carrierId,
        string? fixtureId,
        string? deviceId,
        string runtimeStepId,
        string runtimeCommandId,
        string runtimeNodeId,
        string stationSystemId,
        string configurationSnapshotId,
        DeviceCapabilityId capabilityId,
        string commandName,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string targetKind,
        string targetId,
        string? inputPayload,
        TimeSpan timeout)
    {
        RuntimeSessionId = NotBlank(runtimeSessionId, nameof(runtimeSessionId));
        ProductionRunId = CanonicalGuid(productionRunId, nameof(productionRunId));
        ProductionLineDefinitionId = NotBlank(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        OperationId = NotBlank(operationId, nameof(operationId));
        if (operationAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationAttempt),
                "Operation attempt must be positive.");
        }

        OperationAttempt = operationAttempt;
        ProductModelId = NotBlank(productModelId, nameof(productModelId));
        ProductionUnitIdentityInputKey = NotBlank(
            productionUnitIdentityInputKey,
            nameof(productionUnitIdentityInputKey));
        ProductionUnitIdentityValue = NotBlank(
            productionUnitIdentityValue,
            nameof(productionUnitIdentityValue));
        LotId = Optional(lotId, nameof(lotId));
        CarrierId = Optional(carrierId, nameof(carrierId));
        FixtureId = Optional(fixtureId, nameof(fixtureId));
        DeviceId = Optional(deviceId, nameof(deviceId));
        RuntimeStepId = NotBlank(runtimeStepId, nameof(runtimeStepId));
        RuntimeCommandId = NotBlank(runtimeCommandId, nameof(runtimeCommandId));
        RuntimeNodeId = NotBlank(runtimeNodeId, nameof(runtimeNodeId));
        StationSystemId = NotBlank(stationSystemId, nameof(stationSystemId));
        ConfigurationSnapshotId = NotBlank(configurationSnapshotId, nameof(configurationSnapshotId));
        CapabilityId = capabilityId ?? throw new ArgumentNullException(nameof(capabilityId));
        CommandName = NotBlank(commandName, nameof(commandName));
        ProjectId = NotBlank(projectId, nameof(projectId));
        ApplicationId = NotBlank(applicationId, nameof(applicationId));
        ProjectSnapshotId = NotBlank(projectSnapshotId, nameof(projectSnapshotId));
        TargetKind = NotBlank(targetKind, nameof(targetKind));
        TargetId = NotBlank(targetId, nameof(targetId));
        InputPayload = inputPayload;
        if (timeout <= TimeSpan.Zero
            || timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Command timeout must be positive and use whole-millisecond precision.");
        }

        Timeout = timeout;
    }

    public string RuntimeSessionId { get; }

    public string ProductionRunId { get; }

    public string ProductionLineDefinitionId { get; }

    public string OperationId { get; }

    public int OperationAttempt { get; }

    public string ProductModelId { get; }

    public string ProductionUnitIdentityInputKey { get; }

    public string ProductionUnitIdentityValue { get; }

    public string? LotId { get; }

    public string? CarrierId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string RuntimeStepId { get; }

    public string RuntimeCommandId { get; }

    public string RuntimeNodeId { get; }

    public string StationSystemId { get; }

    public string ConfigurationSnapshotId { get; }

    public DeviceCapabilityId CapabilityId { get; }

    public string CommandName { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TargetKind { get; }

    public string TargetId { get; }

    public string? InputPayload { get; }

    public TimeSpan Timeout { get; }

    private static string NotBlank(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

    private static string? Optional(string? value, string parameterName)
    {
        return value is null ? null : NotBlank(value, parameterName);
    }

    private static string CanonicalGuid(string value, string parameterName)
    {
        return Guid.TryParseExact(value, "D", out var parsed)
               && parsed != Guid.Empty
               && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
            ? value
            : throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical GUID.",
                parameterName);
    }
}
