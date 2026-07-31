using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresAgentPresenceRepositoryIntegrationTests(
    PostgresContainerFixture postgres)
{
    [PostgresIntegrationFact]
    public async Task LatestSessionSequenceAndCoordinatorReceiveTimeSurviveRestart()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var agentId = $"agent-{suffix}";
        var stationId = $"station-{suffix}";
        var stationSystemId = $"station-system-{suffix}";
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var receivedAtUtc = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        using (var repository = new PostgreSqlAgentPresenceRepository(postgres.ConnectionString))
        {
            Assert.True(await repository.RecordAsync(Presence(
                firstSession,
                1,
                AgentPresenceState.Started,
                receivedAtUtc.AddDays(1)), receivedAtUtc));
            Assert.True(await repository.RecordAsync(Presence(
                firstSession,
                2,
                AgentPresenceState.Heartbeat,
                receivedAtUtc.AddDays(-1)), receivedAtUtc.AddSeconds(1)));
            Assert.True(await repository.RecordAsync(Presence(
                secondSession,
                1,
                AgentPresenceState.Started,
                receivedAtUtc.AddYears(-1)), receivedAtUtc.AddSeconds(2)));
        }

        using var restarted = new PostgreSqlAgentPresenceRepository(postgres.ConnectionString);
        Assert.False(await restarted.RecordAsync(Presence(
            firstSession,
            3,
            AgentPresenceState.Heartbeat,
            receivedAtUtc.AddYears(1)), receivedAtUtc.AddSeconds(3)));
        Assert.True(await restarted.RecordAsync(Presence(
            secondSession,
            2,
            AgentPresenceState.Heartbeat,
            receivedAtUtc.AddYears(-1)), receivedAtUtc.AddSeconds(4)));
        var latest = Assert.IsType<AgentPresenceSnapshot>(
            await restarted.GetAsync(agentId, stationId));
        Assert.Equal(secondSession, latest.SessionId);
        Assert.Equal(2, latest.Sequence);
        Assert.Equal(receivedAtUtc.AddYears(-1), latest.ObservedAtUtc);
        Assert.Equal(receivedAtUtc.AddSeconds(4), latest.ReceivedAtUtc);
        Assert.Contains(await restarted.ListAsync(), presence =>
            presence.AgentId == agentId
            && presence.StationId == stationId
            && presence.SessionId == secondSession);

        AgentPresenceReported Presence(
            Guid sessionId,
            long sequence,
            AgentPresenceState state,
            DateTimeOffset observedAtUtc) => new(
            agentId,
            stationId,
            stationSystemId,
            sessionId,
            sequence,
            state,
            observedAtUtc);
    }
}
