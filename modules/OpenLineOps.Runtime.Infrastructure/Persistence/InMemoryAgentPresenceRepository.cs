using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Monitoring;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryAgentPresenceRepository : IAgentPresenceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<PresenceKey, PresenceEntry> _entries = [];

    public ValueTask<bool> RecordAsync(
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        AgentPresenceContract.Validate(presence);
        RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = new PresenceKey(presence.AgentId, presence.StationId);
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (presence.State != AgentPresenceState.Started)
                {
                    return ValueTask.FromResult(false);
                }

                _entries.Add(
                    key,
                    new PresenceEntry(ToSnapshot(presence, receivedAtUtc), [presence.SessionId]));
                return ValueTask.FromResult(true);
            }

            if (presence.SessionId == entry.Latest.SessionId)
            {
                if (!string.Equals(
                        entry.Latest.StationSystemId,
                        presence.StationSystemId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "One Agent presence session cannot change its Station System identity.");
                }

                if (presence.Sequence <= entry.Latest.Sequence
                    || entry.Latest.State == AgentPresenceState.Stopping)
                {
                    return ValueTask.FromResult(false);
                }

                entry.Latest = ToSnapshot(presence, receivedAtUtc);
                return ValueTask.FromResult(true);
            }

            if (entry.Sessions.Contains(presence.SessionId)
                || presence.State != AgentPresenceState.Started)
            {
                return ValueTask.FromResult(false);
            }

            entry.Sessions.Add(presence.SessionId);
            entry.Latest = ToSnapshot(presence, receivedAtUtc);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<AgentPresenceSnapshot?> GetAsync(
        string agentId,
        string stationId,
        CancellationToken cancellationToken = default)
    {
        RequireCanonical(agentId, nameof(agentId));
        RequireCanonical(stationId, nameof(stationId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_entries.TryGetValue(
                new PresenceKey(agentId, stationId),
                out var entry)
                ? entry.Latest
                : null);
        }
    }

    public ValueTask<IReadOnlyCollection<AgentPresenceSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<AgentPresenceSnapshot>>(
                _entries.Values
                    .Select(static entry => entry.Latest)
                    .OrderBy(static presence => presence.AgentId, StringComparer.Ordinal)
                    .ThenBy(static presence => presence.StationId, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    private static AgentPresenceSnapshot ToSnapshot(
        AgentPresenceReported presence,
        DateTimeOffset receivedAtUtc) => new(
        presence.AgentId,
        presence.StationId,
        presence.StationSystemId,
        presence.SessionId,
        presence.Sequence,
        presence.State,
        presence.ObservedAtUtc,
        receivedAtUtc);

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName);
        }
    }

    private sealed class PresenceEntry(
        AgentPresenceSnapshot latest,
        HashSet<Guid> sessions)
    {
        public AgentPresenceSnapshot Latest { get; set; } = latest;

        public HashSet<Guid> Sessions { get; } = sessions;
    }

    private sealed record PresenceKey(string AgentId, string StationId);
}
