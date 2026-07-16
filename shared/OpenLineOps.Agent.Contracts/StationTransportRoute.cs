using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Agent.Contracts;

public static class StationTransportRoute
{
    private const string TargetDomain = "openlineops.station-target";

    public static string Job(string agentId, string stationId) =>
        Target(agentId, stationId, "job");

    public static string ResourceLeaseChanged(string agentId, string stationId) =>
        Target(agentId, stationId, "resource-lease-changed");

    public static string Safety(string agentId, string stationId, string command) =>
        Target(agentId, stationId, KindSegment(command));

    public static string JobQueue(string agentId, string stationId) =>
        $"openlineops.station.{TargetSegment(agentId, stationId)}.jobs";

    public static string SafetyQueue(string agentId, string stationId, string command) =>
        $"openlineops.station.{TargetSegment(agentId, stationId)}.{KindSegment(command)}";

    public static string Event(string agentId, string stationId, string kind) =>
        $"station.{TargetSegment(agentId, stationId)}.{KindSegment(kind)}";

    public static string EventPattern(string kind) =>
        $"station.*.{KindSegment(kind)}";

    public static string TargetSegment(string agentId, string stationId)
    {
        var canonicalAgent = StationIdentityContract.Require(agentId, nameof(agentId));
        var canonicalStation = StationIdentityContract.Require(stationId, nameof(stationId));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{TargetDomain}\n{canonicalAgent}\n{canonicalStation}")));
    }

    private static string Target(string agentId, string stationId, string suffix) =>
        $"station.{TargetSegment(agentId, stationId)}.{suffix}";

    private static string KindSegment(string kind)
    {
        var canonical = Required(kind, nameof(kind));
        if (canonical.Contains('.')
            || canonical.Contains('*')
            || canonical.Contains('#'))
        {
            throw new ArgumentException(
                "Station event kind must be one literal topic segment.",
                nameof(kind));
        }

        return canonical;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}
