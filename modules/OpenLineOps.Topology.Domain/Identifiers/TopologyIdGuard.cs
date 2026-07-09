namespace OpenLineOps.Topology.Domain.Identifiers;

internal static class TopologyIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName)
            : value.Trim();
    }
}
