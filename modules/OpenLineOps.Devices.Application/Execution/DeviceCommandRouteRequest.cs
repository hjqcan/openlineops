using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandRouteRequest
{
    public DeviceCommandRouteRequest(
        string runtimeSessionId,
        string productionRunId,
        string productionLineDefinitionId,
        string productionStageId,
        int stageSequence,
        string workstationId,
        string dutModelId,
        string dutIdentityInputKey,
        string dutIdentityValue,
        string runtimeStepId,
        string runtimeCommandId,
        string runtimeNodeId,
        string stationId,
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
        ProductionStageId = NotBlank(productionStageId, nameof(productionStageId));
        if (stageSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stageSequence),
                "Stage sequence must be positive.");
        }

        StageSequence = stageSequence;
        WorkstationId = NotBlank(workstationId, nameof(workstationId));
        DutModelId = NotBlank(dutModelId, nameof(dutModelId));
        DutIdentityInputKey = NotBlank(dutIdentityInputKey, nameof(dutIdentityInputKey));
        DutIdentityValue = NotBlank(dutIdentityValue, nameof(dutIdentityValue));
        RuntimeStepId = NotBlank(runtimeStepId, nameof(runtimeStepId));
        RuntimeCommandId = NotBlank(runtimeCommandId, nameof(runtimeCommandId));
        RuntimeNodeId = NotBlank(runtimeNodeId, nameof(runtimeNodeId));
        StationId = NotBlank(stationId, nameof(stationId));
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

    public string ProductionStageId { get; }

    public int StageSequence { get; }

    public string WorkstationId { get; }

    public string DutModelId { get; }

    public string DutIdentityInputKey { get; }

    public string DutIdentityValue { get; }

    public string RuntimeStepId { get; }

    public string RuntimeCommandId { get; }

    public string RuntimeNodeId { get; }

    public string StationId { get; }

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
