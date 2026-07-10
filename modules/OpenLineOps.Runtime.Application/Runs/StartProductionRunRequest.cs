using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record StartProductionRunRequest
{
    public StartProductionRunRequest(
        ProductionRunId runId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        DutIdentity dutIdentity,
        string actorId,
        IReadOnlyList<ProductionStageExecutionPlan> stages,
        string? batchId = null,
        string? fixtureId = null,
        string? deviceId = null)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production run id cannot be empty.", nameof(runId));
        }

        RunId = runId;
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = Required(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        DutIdentity = dutIdentity ?? throw new ArgumentNullException(nameof(dutIdentity));
        ActorId = Required(actorId, nameof(actorId));
        ArgumentNullException.ThrowIfNull(stages);
        Stages = stages.ToArray();
        BatchId = Optional(batchId, nameof(batchId));
        FixtureId = Optional(fixtureId, nameof(fixtureId));
        DeviceId = Optional(deviceId, nameof(deviceId));
    }

    public ProductionRunId RunId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public string ProductionLineDefinitionId { get; }

    public DutIdentity DutIdentity { get; }

    public string ActorId { get; }

    public IReadOnlyList<ProductionStageExecutionPlan> Stages { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
    }

    private static string? Optional(string? value, string parameterName)
    {
        return value is null ? null : Required(value, parameterName);
    }
}
