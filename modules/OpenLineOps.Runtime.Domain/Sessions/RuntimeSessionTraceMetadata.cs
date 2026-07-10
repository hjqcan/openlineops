using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public RuntimeSessionTraceMetadata(
        ProductionRunId productionRunId,
        string productionLineDefinitionId,
        string productionStageId,
        int stageSequence,
        string workstationId,
        DutIdentity dutIdentity,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string actorId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId)
    {
        if (productionRunId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production run id cannot be empty.", nameof(productionRunId));
        }

        ProductionRunId = productionRunId;
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        ProductionStageId = Required(productionStageId, nameof(productionStageId));
        if (stageSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageSequence), "Stage sequence must be positive.");
        }

        StageSequence = stageSequence;
        WorkstationId = Required(workstationId, nameof(workstationId));
        DutIdentity = dutIdentity ?? throw new ArgumentNullException(nameof(dutIdentity));
        BatchId = NormalizeOptional(batchId);
        FixtureId = NormalizeOptional(fixtureId);
        DeviceId = NormalizeOptional(deviceId);
        ActorId = Required(actorId, nameof(actorId));
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = Required(topologyId, nameof(topologyId));
    }

    public ProductionRunId ProductionRunId { get; }

    public string ProductionLineDefinitionId { get; }

    public string ProductionStageId { get; }

    public int StageSequence { get; }

    public string WorkstationId { get; }

    public DutIdentity DutIdentity { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string ActorId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Optional trace metadata must be null or a non-empty canonical string.", nameof(value));
        }

        return value;
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be a non-empty canonical string.", parameterName)
            : value;
    }
}
