using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record AgentPresenceSnapshot(
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid SessionId,
    long Sequence,
    AgentPresenceState State,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc);

public interface IAgentPresenceRepository
{
    ValueTask<bool> RecordAsync(
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<AgentPresenceSnapshot?> GetAsync(
        string agentId,
        string stationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<AgentPresenceSnapshot>> ListAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AgentPresenceMonitoringOptions
{
    public const string SectionName = "OpenLineOps:Runtime:AgentPresence";

    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(15);

    public bool PresenceRequired { get; init; }

    public void Validate()
    {
        if (TimeToLive <= TimeSpan.Zero || TimeToLive > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeToLive must be greater than zero and no more than five minutes.");
        }
    }
}
