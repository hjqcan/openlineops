using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
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
    [InlineData("stage")]
    [InlineData("workstation")]
    public void CommandExecutionContextRejectsMissingReleaseIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeCommandExecutionContext(
            RuntimeSessionId.New(),
            ProductionRunId.New(),
            missingField == "line" ? " line.main " : "line.main",
            missingField == "stage" ? " stage.main " : "stage.main",
            1,
            missingField == "workstation" ? " workstation.main " : "workstation.main",
            new DutIdentity("dut.main", "serialNumber", "SN-001"),
            new StationId("station.main"),
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
            missingField == "snapshot" ? " " : "snapshot.main"));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("application")]
    [InlineData("snapshot")]
    [InlineData("topology")]
    [InlineData("line")]
    [InlineData("stage")]
    [InlineData("workstation")]
    [InlineData("actor")]
    public void TraceMetadataRejectsMissingReleaseOrTopologyIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeSessionTraceMetadata(
            ProductionRunId.New(),
            missingField == "line" ? " " : "line.main",
            missingField == "stage" ? " " : "stage.main",
            1,
            missingField == "workstation" ? " " : "workstation.main",
            new DutIdentity("dut.main", "serialNumber", "SN-001"),
            null,
            null,
            null,
            missingField == "actor" ? " " : "operator.main",
            missingField == "project" ? " " : "project.main",
            missingField == "application" ? " " : "application.main",
            missingField == "snapshot" ? " " : "snapshot.main",
            missingField == "topology" ? " " : "topology.main"));
    }
}
