using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

internal static class RuntimeTestReleaseIdentity
{
    public static RuntimeSessionTraceMetadata TraceMetadata(
        string? productionUnitIdentityValue = null,
        string? lotId = null,
        string? carrierId = null,
        string? fixtureId = null,
        string? deviceId = null,
        string stationSystemId = "station.main",
        string actorId = "runtime-test-operator",
        string projectId = "project.main",
        string applicationId = "application.main",
        string projectSnapshotId = "snapshot.release",
        string topologyId = "topology.main")
    {
        return new RuntimeSessionTraceMetadata(
            new ProductionRunId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            "line.main",
            "operation.main",
            1,
            stationSystemId,
            new ProductionUnitIdentity(
                "product.default",
                "serialNumber",
                productionUnitIdentityValue ?? "UNIT-DEFAULT"),
            lotId,
            carrierId,
            fixtureId,
            deviceId,
            actorId,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId);
    }
}
