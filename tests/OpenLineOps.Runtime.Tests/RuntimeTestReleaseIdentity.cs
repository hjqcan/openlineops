using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

internal static class RuntimeTestReleaseIdentity
{
    public static RuntimeSessionTraceMetadata TraceMetadata(
        string? dutIdentityValue = null,
        string? batchId = null,
        string? fixtureId = null,
        string? deviceId = null,
        string actorId = "runtime-test-operator",
        string projectId = "project.main",
        string applicationId = "application.main",
        string projectSnapshotId = "snapshot.release",
        string topologyId = "topology.main")
    {
        return new RuntimeSessionTraceMetadata(
            new ProductionRunId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            "line.main",
            "stage.main",
            1,
            "workstation.main",
            new DutIdentity("dut.default", "serialNumber", dutIdentityValue ?? "DUT-DEFAULT"),
            batchId,
            fixtureId,
            deviceId,
            actorId,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId);
    }
}
