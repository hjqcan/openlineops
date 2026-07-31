using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class AgentPresenceRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ContractRequiresCanonicalIdentityUtcAndStateSequencePair()
    {
        var valid = Presence(Guid.NewGuid(), 1, AgentPresenceState.Started, Now);
        AgentPresenceContract.Validate(valid);
        Assert.Equal(
            AgentPresenceContract.MessageId(valid),
            AgentPresenceContract.MessageId(valid));

        foreach (var invalid in new[]
                 {
                     valid with { AgentId = " agent.main" },
                     valid with { StationId = string.Empty },
                     valid with { StationSystemId = "station-system.main " },
                     valid with { SessionId = Guid.Empty },
                     valid with { Sequence = 0 },
                     valid with { Sequence = 2 },
                     valid with { State = AgentPresenceState.Heartbeat },
                     valid with { ObservedAtUtc = default },
                     valid with { ObservedAtUtc = Now.ToOffset(TimeSpan.FromHours(8)) }
                 })
        {
            Assert.ThrowsAny<Exception>(() => AgentPresenceContract.Validate(invalid));
        }
    }

    [Fact]
    public async Task LatestSessionAndSequenceRejectReplayAndUseCoordinatorReceiveTime()
    {
        var repository = new InMemoryAgentPresenceRepository();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        Assert.False(await repository.RecordAsync(
            Presence(firstSession, 2, AgentPresenceState.Heartbeat, Now),
            Now));
        Assert.True(await repository.RecordAsync(
            Presence(firstSession, 1, AgentPresenceState.Started, Now),
            Now.AddSeconds(1)));
        Assert.True(await repository.RecordAsync(
            Presence(firstSession, 2, AgentPresenceState.Heartbeat, Now.AddHours(-1)),
            Now.AddSeconds(2)));
        Assert.False(await repository.RecordAsync(
            Presence(firstSession, 2, AgentPresenceState.Heartbeat, Now.AddHours(1)),
            Now.AddSeconds(3)));

        Assert.True(await repository.RecordAsync(
            Presence(secondSession, 1, AgentPresenceState.Started, Now.AddDays(-1)),
            Now.AddSeconds(4)));
        Assert.False(await repository.RecordAsync(
            Presence(firstSession, 3, AgentPresenceState.Heartbeat, Now.AddDays(1)),
            Now.AddSeconds(5)));
        Assert.True(await repository.RecordAsync(
            Presence(secondSession, 2, AgentPresenceState.Stopping, Now.AddDays(-1)),
            Now.AddSeconds(6)));
        Assert.False(await repository.RecordAsync(
            Presence(secondSession, 3, AgentPresenceState.Heartbeat, Now.AddDays(1)),
            Now.AddSeconds(7)));

        var latest = Assert.IsType<AgentPresenceSnapshot>(
            await repository.GetAsync("agent.main", "station.main"));
        Assert.Equal(secondSession, latest.SessionId);
        Assert.Equal(2, latest.Sequence);
        Assert.Equal(AgentPresenceState.Stopping, latest.State);
        Assert.Equal(Now.AddDays(-1), latest.ObservedAtUtc);
        Assert.Equal(Now.AddSeconds(6), latest.ReceivedAtUtc);
        Assert.Single(await repository.ListAsync());
    }

    private static AgentPresenceReported Presence(
        Guid sessionId,
        long sequence,
        AgentPresenceState state,
        DateTimeOffset observedAtUtc) => new(
        "agent.main",
        "station.main",
        "station-system.main",
        sessionId,
        sequence,
        state,
        observedAtUtc);
}
