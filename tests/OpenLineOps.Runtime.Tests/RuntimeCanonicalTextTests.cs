using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeCanonicalTextTests
{
    [Theory]
    [InlineData(" node.main")]
    [InlineData("node.main ")]
    public void RuntimeIdentifiersRejectBoundaryWhitespace(string value)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeNodeId(value));
    }

    [Fact]
    public void RuntimeIncidentRejectsPaddedCodeAndMessage()
    {
        Assert.Throws<ArgumentException>(() => RuntimeIncident.Record(
            RuntimeIncidentId.New(),
            RuntimeIncidentSeverity.Error,
            " Runtime.Fault",
            "Fault",
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => RuntimeIncident.Record(
            RuntimeIncidentId.New(),
            RuntimeIncidentSeverity.Error,
            "Runtime.Fault",
            "Fault ",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RuntimeStepAndCommandRejectPaddedSemanticNames()
    {
        var target = new RuntimeTargetReference(RuntimeTargetKinds.System, "system.main");
        Assert.Throws<ArgumentException>(() => RuntimeStep.Start(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.main"),
            " Step",
            DateTimeOffset.UtcNow,
            new RuntimeActionId("action.main"),
            target));
        Assert.Throws<ArgumentException>(() => RuntimeCommand.Create(
            RuntimeCommandId.New(),
            RuntimeStepId.New(),
            new RuntimeCapabilityId("capability.main"),
            "Execute ",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            new RuntimeActionId("action.main"),
            target));
    }

    [Fact]
    public void CommandExecutionResultRejectsPaddedFailureReason()
    {
        Assert.Throws<ArgumentException>(() =>
            RuntimeCommandExecutionResult.Failed(" failure"));
        Assert.Throws<ArgumentException>(() =>
            RuntimeCommandExecutionResult.Rejected("failure "));
    }
}
