using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;

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
        Assert.Equal(ExecutionStatus.Completed, command.Status);
        Assert.True(command.IsTerminal);
        Assert.Equal("{\"ok\":true}", command.ResultPayload);
    }

    [Fact]
    public void ProductJudgementCompletesCommandWithoutBecomingExecutionFailure()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);
        session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2));
        session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3));

        var completed = session.CompleteCommand(
            command.Id,
            "{\"judgement\":\"Failed\"}",
            StartedAtUtc.AddSeconds(4),
            ResultJudgement.Failed);

        Assert.True(completed.Succeeded);
        Assert.Equal(ExecutionStatus.Completed, command.Status);
        Assert.Equal(ResultJudgement.Failed, command.ResultJudgement);
        Assert.Equal("{\"judgement\":\"Failed\"}", command.ResultPayload);
    }

    [Fact]
    public void CommandCannotCompleteBeforeStart()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        var completed = session.CompleteCommand(command.Id, null, StartedAtUtc.AddSeconds(2));

        Assert.False(completed.Succeeded);
        Assert.Equal(ExecutionStatus.Pending, command.Status);
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
        Assert.Equal(ExecutionStatus.TimedOut, command.Status);
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
        Assert.Equal(ExecutionStatus.Rejected, command.Status);
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
        Assert.Equal(ExecutionStatus.Rejected, command.Status);
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
                Assert.Equal(ExecutionStatus.Pending, first.FromStatus);
                Assert.Equal(ExecutionStatus.Running, first.ToStatus);
            },
            second =>
            {
                Assert.Equal(ExecutionStatus.Running, second.FromStatus);
                Assert.Equal(ExecutionStatus.Completed, second.ToStatus);
            });
    }

    [Fact]
    public void CompletedCommandAllowsOnlyExactTerminalEvidenceReplay()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);
        Assert.True(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);
        Assert.True(session.CompleteCommand(
            command.Id,
            "original-payload",
            StartedAtUtc.AddSeconds(4),
            ResultJudgement.Passed).Succeeded);
        var eventCount = session.DomainEvents.Count;

        var exactReplay = session.CompleteCommand(
            command.Id,
            "original-payload",
            StartedAtUtc.AddSeconds(4),
            ResultJudgement.Passed);
        var changedPayload = session.CompleteCommand(
            command.Id,
            "changed-payload",
            StartedAtUtc.AddSeconds(4),
            ResultJudgement.Passed);
        var changedTimestamp = session.CompleteCommand(
            command.Id,
            "original-payload",
            StartedAtUtc.AddSeconds(5),
            ResultJudgement.Passed);
        var changedJudgement = session.CompleteCommand(
            command.Id,
            "original-payload",
            StartedAtUtc.AddSeconds(4),
            ResultJudgement.Failed);
        var lateTimeout = session.TimeoutCommand(
            command.Id,
            StartedAtUtc.AddSeconds(5),
            "timeout-payload");
        var lateReject = session.RejectCommand(
            command.Id,
            "late reject",
            StartedAtUtc.AddSeconds(5));

        Assert.True(exactReplay.Succeeded);
        Assert.All(
            new[] { changedPayload, changedTimestamp, changedJudgement, lateTimeout, lateReject },
            result => Assert.False(result.Succeeded));
        Assert.Equal("original-payload", command.ResultPayload);
        Assert.Equal(ResultJudgement.Passed, command.ResultJudgement);
        Assert.Null(command.FailureReason);
        Assert.Equal(StartedAtUtc.AddSeconds(4), command.CompletedAtUtc);
        Assert.Equal(eventCount, session.DomainEvents.Count);
    }

    [Fact]
    public void FailedCanceledTimedOutAndRejectedCommandsRejectDifferentReplayEvidence()
    {
        var failed = CreateStartedCommand();
        Assert.True(failed.Session.FailCommand(
            failed.Command.Id,
            "original failure",
            StartedAtUtc.AddSeconds(4),
            "failure-payload").Succeeded);
        Assert.True(failed.Session.FailCommand(
            failed.Command.Id,
            "original failure",
            StartedAtUtc.AddSeconds(4),
            "failure-payload").Succeeded);
        Assert.False(failed.Session.FailCommand(
            failed.Command.Id,
            "changed failure",
            StartedAtUtc.AddSeconds(4),
            "failure-payload").Succeeded);
        Assert.Equal("original failure", failed.Command.FailureReason);
        Assert.Equal("failure-payload", failed.Command.ResultPayload);
        Assert.Equal(StartedAtUtc.AddSeconds(4), failed.Command.CompletedAtUtc);

        var canceled = CreateStartedCommand();
        Assert.True(canceled.Session.CancelCommand(
            canceled.Command.Id,
            StartedAtUtc.AddSeconds(4),
            "original cancel",
            "cancel-payload").Succeeded);
        Assert.True(canceled.Session.CancelCommand(
            canceled.Command.Id,
            StartedAtUtc.AddSeconds(4),
            "original cancel",
            "cancel-payload").Succeeded);
        Assert.False(canceled.Session.CancelCommand(
            canceled.Command.Id,
            StartedAtUtc.AddSeconds(5),
            "changed cancel",
            "changed-payload").Succeeded);
        Assert.Equal("original cancel", canceled.Command.FailureReason);
        Assert.Equal("cancel-payload", canceled.Command.ResultPayload);
        Assert.Equal(ResultJudgement.Aborted, canceled.Command.ResultJudgement);
        Assert.Equal(StartedAtUtc.AddSeconds(4), canceled.Command.CompletedAtUtc);

        var timedOut = CreateStartedCommand();
        Assert.True(timedOut.Session.TimeoutCommand(
            timedOut.Command.Id,
            StartedAtUtc.AddSeconds(4),
            "timeout-payload").Succeeded);
        Assert.True(timedOut.Session.TimeoutCommand(
            timedOut.Command.Id,
            StartedAtUtc.AddSeconds(4),
            "timeout-payload").Succeeded);
        Assert.False(timedOut.Session.TimeoutCommand(
            timedOut.Command.Id,
            StartedAtUtc.AddSeconds(5),
            "changed-payload").Succeeded);
        Assert.Equal("Command timed out.", timedOut.Command.FailureReason);
        Assert.Equal("timeout-payload", timedOut.Command.ResultPayload);
        Assert.Equal(StartedAtUtc.AddSeconds(4), timedOut.Command.CompletedAtUtc);

        var rejectedSession = CreateRunningSession();
        var rejectedCommand = CreateCommand(rejectedSession);
        Assert.True(rejectedSession.RejectCommand(
            rejectedCommand.Id,
            "original reject",
            StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(rejectedSession.RejectCommand(
            rejectedCommand.Id,
            "original reject",
            StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.False(rejectedSession.RejectCommand(
            rejectedCommand.Id,
            "changed reject",
            StartedAtUtc.AddSeconds(3)).Succeeded);
        Assert.Equal("original reject", rejectedCommand.FailureReason);
        Assert.Equal(StartedAtUtc.AddSeconds(2), rejectedCommand.CompletedAtUtc);
    }

    [Fact]
    public void AcceptedAndStartedCommandReplayRequiresExactTransitionTimestamp()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);

        Assert.True(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.False(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);
        Assert.Equal(StartedAtUtc.AddSeconds(2), command.AcceptedAtUtc);

        Assert.True(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);
        Assert.True(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);
        Assert.False(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(4)).Succeeded);
        Assert.Equal(StartedAtUtc.AddSeconds(3), command.StartedAtUtc);
    }

    [Fact]
    public void RestoreRejectsInconsistentOrUndefinedExecutionAxes()
    {
        Assert.Throws<ArgumentException>(() => RestoreCommand(
            ExecutionStatus.Completed,
            acceptedAtUtc: null,
            startedAtUtc: null,
            completedAtUtc: StartedAtUtc.AddSeconds(4),
            failureReason: null,
            resultJudgement: ResultJudgement.Passed));
        Assert.Throws<ArgumentException>(() => RestoreCommand(
            ExecutionStatus.Canceled,
            acceptedAtUtc: StartedAtUtc.AddSeconds(2),
            startedAtUtc: StartedAtUtc.AddSeconds(3),
            completedAtUtc: StartedAtUtc.AddSeconds(4),
            failureReason: "operator canceled",
            resultJudgement: ResultJudgement.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestoreCommand(
            (ExecutionStatus)int.MaxValue,
            acceptedAtUtc: null,
            startedAtUtc: null,
            completedAtUtc: null,
            failureReason: null,
            resultJudgement: null));
    }

    private static RuntimeCommand RestoreCommand(
        ExecutionStatus status,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureReason,
        ResultJudgement? resultJudgement)
    {
        return RuntimeCommand.Restore(
            RuntimeCommandId.New(),
            RuntimeStepId.New(),
            new RuntimeCapabilityId("device.restore"),
            "Restore",
            status,
            StartedAtUtc.AddSeconds(1),
            TimeSpan.FromSeconds(30),
            acceptedAtUtc,
            startedAtUtc,
            completedAtUtc,
            resultPayload: null,
            failureReason,
            resultJudgement,
            new RuntimeActionId("restore:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.restore"));
    }

    private static (RuntimeSession Session, RuntimeCommand Command) CreateStartedCommand()
    {
        var session = CreateRunningSession();
        var command = CreateCommand(session);
        Assert.True(session.AcceptCommand(command.Id, StartedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.StartCommand(command.Id, StartedAtUtc.AddSeconds(3)).Succeeded);
        return (session, command);
    }

    private static RuntimeCommand CreateCommand(RuntimeSession session)
    {
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node-scan-barcode"),
            "Scan barcode",
            StartedAtUtc.AddSeconds(1),
            new RuntimeActionId("node-scan-barcode:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.scanner"));

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
            StartedAtUtc.AddMinutes(-1),
            RuntimeTestReleaseIdentity.TraceMetadata());

        session.Start(StartedAtUtc);

        return session;
    }
}
