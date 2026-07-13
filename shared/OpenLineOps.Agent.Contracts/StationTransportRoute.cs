using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Agent.Contracts;

public static class StationTransportRoute
{
    public static string Event(string stationId, string kind) =>
        $"station.{StationSegment(stationId)}.{KindSegment(kind)}";

    public static string EventPattern(string kind) =>
        $"station.*.{KindSegment(kind)}";

    public static string StationSegment(string stationId)
    {
        var canonical = Required(stationId, nameof(stationId));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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
