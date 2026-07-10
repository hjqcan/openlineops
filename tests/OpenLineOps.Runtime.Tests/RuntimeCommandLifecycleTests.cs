using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeCommandLifecycleTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CommandCanMoveThroughAcceptedStartedAndCompleted()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        var accepted = session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2));
        var started = session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3));
        var completed = session.CompleteCommand(command.Id, "{\"ok\":true}", StartedAtUtc.AddSeconds(4));

        Assert.True(accepted.Succeeded);
        Assert.True(started.Succeeded);
        Assert.True(completed.Succeeded);
        Assert.Equal(RuntimeCommandStatus.Completed, command.Status);
        Assert.True(command.IsTerminal);
        Assert.Equal("{\"ok\":true}", command.ResultPayload);
    }

    [Fact]
    public void CommandCannotCompleteBeforeStart()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        var completed = session.CompleteCommand(command.Id, null, StartedAtUtc.AddSeconds(2));

        Assert.False(completed.Succeeded);
        Assert.Equal(RuntimeCommandStatus.Pending, command.Status);
        Assert.Equal("Runtime.CommandTransitionRejected", completed.Code);
    }

    [Fact]
    public void TimeoutMarksCommandTerminal()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2));
        session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3));

        var timedOut = session.TimeoutCommand(command.Id, StartedAtUtc.AddSeconds(35));

        Assert.True(timedOut.Succeeded);
        Assert.Equal(RuntimeCommandStatus.TimedOut, command.Status);
        Assert.True(command.IsTerminal);
        Assert.Equal("Command timed out.", command.FailureReason);
    }

    [Fact]
    public void RejectedCommandIsTerminal()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        var rejected = session.RejectCommand(command.Id, "capability unavailable", StartedAtUtc.AddSeconds(2));
        var startAfterReject = session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3));

        Assert.True(rejected.Succeeded);
        Assert.False(startAfterReject.Succeeded);
        Assert.Equal(RuntimeCommandStatus.Rejected, command.Status);
        Assert.True(command.IsTerminal);
        Assert.Equal("capability unavailable", command.FailureReason);
    }

    [Fact]
    public void BackendCanRejectCommandAfterRuntimeDispatchStarted()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);
        Assert.True(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);

        var rejected = session.RejectCommand(
            command.Id,
            "backend route unavailable",
            StartedAtUtc.AddSeconds(4));

        Assert.True(rejected.Succeeded);
        Assert.Equal(RuntimeCommandStatus.Rejected, command.Status);
        Assert.Equal("backend route unavailable", command.FailureReason);
    }

    [Fact]
    public void CommandLifecycleEmitsStatusChangedEventsInOrder()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2));
        session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3));
        session.CompleteCommand(command.Id, "done", StartedAtUtc.AddSeconds(4));

        var commandEvents = session.DomainEvents
            .OfType<RuntimeCommandStatusChangedDomainEvent>()
            .ToArray();

        Assert.Collection(
            commandEvents,
            first =>
            {
                Assert.Equal(RuntimeCommandStatus.Pending, first.FromStatus);
                Assert.Equal(RuntimeCommandStatus.Accepted, first.ToStatus);
            },
            second =>
            {
                Assert.Equal(RuntimeCommandStatus.Accepted, second.FromStatus);
                Assert.Equal(RuntimeCommandStatus.InProgress, second.ToStatus);
            },
            third =>
            {
                Assert.Equal(RuntimeCommandStatus.InProgress, third.FromStatus);
                Assert.Equal(RuntimeCommandStatus.Completed, third.ToStatus);
            });
    }

    private static RuntimeCommand CreateCommand(RuntimeSession session)
    {
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node-scan-barcode"),
            "Scan barcode",
            StartedAtUtc.AddSeconds(1));

        return session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("device.scanner"),
            "Scan",
            StartedAtUtc.AddSeconds(1),
            TimeSpan.FromSeconds(30));
    }

    private static RuntimeSession CreateRunningSession()
    {
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId("station-a"),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@1.0.0"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RecipeSnapshotId("recipe-20260629-001"),
            StartedAtUtc.AddMinutes(-1));

        session.Start(StartedAtUtc);

        return session;
    }
}
