using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

internal static class RuntimeTestReleaseIdentity
{
    public static RuntimeSessionTraceMetadata TraceMetadata(
        string? serialNumber = null,
        string? batchId = null,
        string? fixtureId = null,
        string? deviceId = null,
        string? actorId = null)
    {
        return new RuntimeSessionTraceMetadata(
            serialNumber,
            batchId,
            fixtureId,
            deviceId,
            actorId,
            "project.main",
            "application.main",
            "snapshot.release",
            "topology.main");
    }
}
