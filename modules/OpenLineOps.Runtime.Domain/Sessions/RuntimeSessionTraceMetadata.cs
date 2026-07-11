using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public RuntimeSessionTraceMetadata(
        ProductionRunId productionRunId,
        string productionLineDefinitionId,
        string operationId,
        int operationAttempt,
        string stationSystemId,
        ProductionUnitIdentity productionUnitIdentity,
        string? lotId,
        string? carrierId,
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
        OperationId = Required(operationId, nameof(operationId));
        if (operationAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationAttempt),
                "Operation attempt must be positive.");
        }

        OperationAttempt = operationAttempt;
        StationSystemId = Required(stationSystemId, nameof(stationSystemId));
        ProductionUnitIdentity = productionUnitIdentity
            ?? throw new ArgumentNullException(nameof(productionUnitIdentity));
        LotId = NormalizeOptional(lotId);
        CarrierId = NormalizeOptional(carrierId);
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

    public string OperationId { get; }

    public int OperationAttempt { get; }

    public string StationSystemId { get; }

    public ProductionUnitIdentity ProductionUnitIdentity { get; }

    public string? LotId { get; }

    public string? CarrierId { get; }

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
