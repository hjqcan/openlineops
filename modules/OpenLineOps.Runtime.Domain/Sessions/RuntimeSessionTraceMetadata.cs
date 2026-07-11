using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public RuntimeSessionTraceMetadata(
        ProductionRunId productionRunId,
        ProductionUnitId productionUnitId,
        string productionLineDefinitionId,
        string operationId,
        string operationRunId,
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
        string topologyId,
        IEnumerable<ResourceLeaseFenceEvidence> resourceLeaseFences)
    {
        if (productionRunId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production run id cannot be empty.", nameof(productionRunId));
        }

        ProductionRunId = productionRunId;
        ProductionUnitId = productionUnitId.Value == Guid.Empty
            ? throw new ArgumentException("Production Unit id cannot be empty.", nameof(productionUnitId))
            : productionUnitId;
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        OperationId = Required(operationId, nameof(operationId));
        OperationRunId = Required(operationRunId, nameof(operationRunId));
        if (operationAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationAttempt),
                "Operation attempt must be positive.");
        }

        OperationAttempt = operationAttempt;
        if (!string.Equals(
                OperationRunId,
                $"{OperationId}@{OperationAttempt:D4}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Operation Run id must match the Operation identity and attempt.",
                nameof(operationRunId));
        }

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
        ArgumentNullException.ThrowIfNull(resourceLeaseFences);
        var fences = resourceLeaseFences.ToArray();
        if (fences.Length == 0
            || fences.Any(static fence => fence is null)
            || fences.Select(static fence => fence.Resource).Distinct().Count() != fences.Length
            || !fences.Any(fence => fence.Resource.Kind == ResourceKind.Station
                && string.Equals(
                    fence.Resource.ResourceId,
                    StationSystemId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Runtime trace metadata requires unique resource fences including its Station.",
                nameof(resourceLeaseFences));
        }

        ResourceLeaseFences = fences
            .OrderBy(static fence => fence.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    public ProductionRunId ProductionRunId { get; }

    public ProductionUnitId ProductionUnitId { get; }

    public string ProductionLineDefinitionId { get; }

    public string OperationId { get; }

    public string OperationRunId { get; }

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

    public IReadOnlyList<ResourceLeaseFenceEvidence> ResourceLeaseFences { get; }

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
