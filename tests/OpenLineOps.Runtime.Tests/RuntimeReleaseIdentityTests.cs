using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeReleaseIdentityTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("application")]
    [InlineData("snapshot")]
    public void CommandExecutionContextRejectsMissingReleaseIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeCommandExecutionContext(
            RuntimeSessionId.New(),
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
    public void TraceMetadataRejectsMissingReleaseOrTopologyIdentity(string missingField)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeSessionTraceMetadata(
            null,
            null,
            null,
            null,
            null,
            missingField == "project" ? " " : "project.main",
            missingField == "application" ? " " : "application.main",
            missingField == "snapshot" ? " " : "snapshot.main",
            missingField == "topology" ? " " : "topology.main"));
    }
}
