using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeSessionTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateCapturesImmutableExecutionInputs()
    {
        var session = CreateSession();

        Assert.Equal(RuntimeSessionStatus.Created, session.Status);
        Assert.Equal("station-a", session.StationId.Value);
        Assert.Equal("process-packaging", session.ProcessDefinitionId.Value);
        Assert.Equal("process-packaging@1.0.0", session.ProcessVersionId.Value);
        Assert.Equal("snapshot-20260629-001", session.ConfigurationSnapshotId.Value);
        Assert.Equal("recipe-20260629-001", session.RecipeSnapshotId.Value);
        Assert.IsType<RuntimeSessionCreatedDomainEvent>(Assert.Single(session.DomainEvents));
    }

    [Fact]
    public void StartFromQueuedMovesSessionToRunning()
    {
        var session = CreateSession();

        var queueResult = session.Queue(StartedAtUtc);
        var startResult = session.Start(StartedAtUtc.AddSeconds(1));

        Assert.True(queueResult.Succeeded);
        Assert.True(startResult.Succeeded);
        Assert.Equal(RuntimeSessionStatus.Running, session.Status);
        Assert.Equal(StartedAtUtc.AddSeconds(1), session.StartedAtUtc);
    }

    [Fact]
    public void PauseAndResumeAreExplicitAndIdempotent()
    {
        var session = CreateRunningSession();

        var pauseRequested = session.RequestPause(StartedAtUtc.AddSeconds(10), "operator pause");
        var pauseConfirmed = session.ConfirmPaused(StartedAtUtc.AddSeconds(11), "devices paused");
        var repeatedPause = session.RequestPause(StartedAtUtc.AddSeconds(12), "operator pause again");
        var resumed = session.Resume(StartedAtUtc.AddSeconds(13), "operator resume");
        var repeatedResume = session.Resume(StartedAtUtc.AddSeconds(14), "operator resume again");

        Assert.True(pauseRequested.Succeeded);
        Assert.True(pauseConfirmed.Succeeded);
        Assert.True(repeatedPause.Succeeded);
        Assert.True(resumed.Succeeded);
        Assert.True(repeatedResume.Succeeded);
        Assert.Equal(RuntimeSessionStatus.Running, session.Status);
    }

    [Fact]
    public void CompleteFromCreatedIsRejected()
    {
        var session = CreateSession();

        var result = session.Complete(StartedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeSessionStatus.Created, session.Status);
        Assert.Equal("Runtime.SessionTransitionRejected", result.Code);
    }

    [Fact]
    public void StopIsIdempotentAfterStopped()
    {
        var session = CreateRunningSession();

        var stopRequested = session.RequestStop(StartedAtUtc.AddSeconds(10), "operator stop");
        var stopped = session.MarkStopped(StartedAtUtc.AddSeconds(11), "all devices stopped");
        var repeatedStopRequest = session.RequestStop(StartedAtUtc.AddSeconds(12), "operator stop again");
        var repeatedStopped = session.MarkStopped(StartedAtUtc.AddSeconds(13), "all devices stopped again");

        Assert.True(stopRequested.Succeeded);
        Assert.True(stopped.Succeeded);
        Assert.True(repeatedStopRequest.Succeeded);
        Assert.True(repeatedStopped.Succeeded);
        Assert.Equal(RuntimeSessionStatus.Stopped, session.Status);
        Assert.True(session.IsTerminal);
    }

    [Fact]
    public void CancelIsIdempotentAndTerminal()
    {
        var session = CreateRunningSession();

        var canceled = session.Cancel(StartedAtUtc.AddSeconds(10), "operator cancel");
        var repeatedCancel = session.Cancel(StartedAtUtc.AddSeconds(11), "operator cancel again");
        var restart = session.Start(StartedAtUtc.AddSeconds(12));

        Assert.True(canceled.Succeeded);
        Assert.True(repeatedCancel.Succeeded);
        Assert.False(restart.Succeeded);
        Assert.Equal(RuntimeSessionStatus.Canceled, session.Status);
        Assert.True(session.IsTerminal);
    }

    private static RuntimeSession CreateRunningSession()
    {
        var session = CreateSession();
        var startResult = session.Start(StartedAtUtc);

        Assert.True(startResult.Succeeded);

        return session;
    }

    private static RuntimeSession CreateSession()
    {
        return RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId("station-a"),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@1.0.0"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RecipeSnapshotId("recipe-20260629-001"),
            StartedAtUtc.AddMinutes(-1));
    }
}
