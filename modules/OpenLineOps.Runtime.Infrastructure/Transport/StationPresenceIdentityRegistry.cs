using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class StationPresenceIdentityRegistry
{
    private readonly Dictionary<AgentStationKey, string> _stationSystems;

    public StationPresenceIdentityRegistry(StationCoordinatorTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stationSystems = new Dictionary<AgentStationKey, string>();
        foreach (var deployment in options.Deployments)
        {
            var key = new AgentStationKey(
                Required(deployment.AgentId, nameof(deployment.AgentId)),
                Required(deployment.StationId, nameof(deployment.StationId)));
            var stationSystemId = Required(
                deployment.StationSystemId,
                nameof(deployment.StationSystemId));
            if (stationSystems.TryGetValue(key, out var existing)
                && !string.Equals(existing, stationSystemId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Agent/Station presence identity '{key.AgentId}/{key.StationId}' maps to more than one Station System.");
            }

            stationSystems[key] = stationSystemId;
        }

        _stationSystems = stationSystems;
    }

    public void Authorize(AgentPresenceReported presence)
    {
        AgentPresenceContract.Validate(presence);
        var key = new AgentStationKey(presence.AgentId, presence.StationId);
        if (!_stationSystems.TryGetValue(key, out var stationSystemId)
            || !string.Equals(
                stationSystemId,
                presence.StationSystemId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Agent presence does not match one configured Agent/Station/Station System deployment identity.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidOperationException(
                $"Station presence deployment {parameterName} must be canonical text.")
            : value;

    private sealed record AgentStationKey(string AgentId, string StationId);
}
