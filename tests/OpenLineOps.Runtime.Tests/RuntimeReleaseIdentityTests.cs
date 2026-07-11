using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeReleaseIdentityTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("application")]
    [InlineData("snapshot")]
    [InlineData("line")]
    [InlineData("operation")]
    [InlineData("stationSystem")]
    public void CommandExecutionContextRejectsMissingReleaseIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeCommandExecutionContext(
            RuntimeSessionId.New(),
            ProductionRunId.New(),
            ProductionUnitId.New(),
            missingField == "line" ? " line.main " : "line.main",
            missingField == "operation" ? " operation.main " : "operation.main",
            "operation.main@0001",
            1,
            missingField == "stationSystem" ? " station.main " : "station.main",
            new ProductionUnitIdentity("product.main", "serialNumber", "UNIT-001"),
            null,
            null,
            null,
            null,
            new ConfigurationSnapshotId("configuration.main"),
            RuntimeStepId.New(),
            RuntimeCommandId.New(),
            new RuntimeNodeId("node.main"),
            new RuntimeCapabilityId("capability.main"),
            "Execute",
            null,
            TimeSpan.FromSeconds(1),
            new RuntimeActionId("action.main"),
            "System",
            "system.main",
            missingField == "project" ? " " : "project.main",
            missingField == "application" ? " " : "application.main",
            missingField == "snapshot" ? " " : "snapshot.main",
            RuntimeTestReleaseIdentity.ResourceFences()));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("application")]
    [InlineData("snapshot")]
    [InlineData("topology")]
    [InlineData("line")]
    [InlineData("operation")]
    [InlineData("stationSystem")]
    [InlineData("actor")]
    public void TraceMetadataRejectsMissingReleaseOrTopologyIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeSessionTraceMetadata(
            ProductionRunId.New(),
            ProductionUnitId.New(),
            missingField == "line" ? " " : "line.main",
            missingField == "operation" ? " " : "operation.main",
            "operation.main@0001",
            1,
            missingField == "stationSystem" ? " " : "station.main",
            new ProductionUnitIdentity("product.main", "serialNumber", "UNIT-001"),
            null,
            null,
            null,
            null,
            missingField == "actor" ? " " : "operator.main",
            missingField == "project" ? " " : "project.main",
            missingField == "application" ? " " : "application.main",
            missingField == "snapshot" ? " " : "snapshot.main",
            missingField == "topology" ? " " : "topology.main",
            RuntimeTestReleaseIdentity.ResourceFences()));
    }
}
