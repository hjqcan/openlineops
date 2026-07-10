using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeMonitoringScope
{
    public RuntimeMonitoringScope(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        ProductionRunId? productionRunId = null)
    {
        ProjectId = RequiredCanonical(projectId, nameof(projectId));
        ApplicationId = RequiredCanonical(applicationId, nameof(applicationId));
        ProjectSnapshotId = RequiredCanonical(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = RequiredCanonical(topologyId, nameof(topologyId));
        if (productionRunId is { Value: var value } && value == Guid.Empty)
        {
            throw new ArgumentException(
                "productionRunId must be null or a non-empty Production Run ID.",
                nameof(productionRunId));
        }

        ProductionRunId = productionRunId;
    }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public ProductionRunId? ProductionRunId { get; }

    private static string RequiredCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName);
        }

        return value;
    }
}
