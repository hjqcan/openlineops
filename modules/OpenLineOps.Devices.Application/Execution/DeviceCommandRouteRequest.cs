using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandRouteRequest
{
    public DeviceCommandRouteRequest(
        string runtimeSessionId,
        string runtimeStepId,
        string runtimeCommandId,
        string runtimeNodeId,
        string stationId,
        string configurationSnapshotId,
        DeviceCapabilityId capabilityId,
        string commandName,
        string? projectId = null,
        string? applicationId = null,
        string? projectSnapshotId = null)
    {
        RuntimeSessionId = NotBlank(runtimeSessionId, nameof(runtimeSessionId));
        RuntimeStepId = NotBlank(runtimeStepId, nameof(runtimeStepId));
        RuntimeCommandId = NotBlank(runtimeCommandId, nameof(runtimeCommandId));
        RuntimeNodeId = NotBlank(runtimeNodeId, nameof(runtimeNodeId));
        StationId = NotBlank(stationId, nameof(stationId));
        ConfigurationSnapshotId = NotBlank(configurationSnapshotId, nameof(configurationSnapshotId));
        CapabilityId = capabilityId ?? throw new ArgumentNullException(nameof(capabilityId));
        CommandName = NotBlank(commandName, nameof(commandName));
        ProjectId = NormalizeOptional(projectId);
        ApplicationId = NormalizeOptional(applicationId);
        ProjectSnapshotId = NormalizeOptional(projectSnapshotId);
    }

    public string RuntimeSessionId { get; }

    public string RuntimeStepId { get; }

    public string RuntimeCommandId { get; }

    public string RuntimeNodeId { get; }

    public string StationId { get; }

    public string ConfigurationSnapshotId { get; }

    public DeviceCapabilityId CapabilityId { get; }

    public string CommandName { get; }

    public string? ProjectId { get; }

    public string? ApplicationId { get; }

    public string? ProjectSnapshotId { get; }

    public bool HasProjectReleaseIdentity =>
        ProjectId is not null && ApplicationId is not null && ProjectSnapshotId is not null;

    private static string NotBlank(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName)
            : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
