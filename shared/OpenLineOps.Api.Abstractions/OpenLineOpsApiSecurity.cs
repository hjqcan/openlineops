using System.Security.Claims;

namespace OpenLineOps.Api.Abstractions;

public static class OpenLineOpsApiSecurity
{
    public const string AuthenticationScheme = "OpenLineOpsBearer";

    public const string EngineeringRole = "Engineering";

    public const string OperatorRole = "Operator";

    public const string SafetyRole = "Safety";

    public const string StationAgentRole = "StationAgent";

    public const string StationIdClaim = "openlineops:station_id";

    public const string EngineeringPolicy = "OpenLineOps.Engineering";

    public const string OperatorPolicy = "OpenLineOps.Operator";

    public const string SafetyPolicy = "OpenLineOps.Safety";

    public const string SafetyConfirmationPolicy = "OpenLineOps.SafetyConfirmation";

    public const string StationAgentPolicy = "OpenLineOps.StationAgent";

    public static string GetRequiredStationId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var stationId = principal.FindFirstValue(StationIdClaim);
        return string.IsNullOrWhiteSpace(stationId)
            ? throw new InvalidOperationException(
                "The authenticated Station Agent does not contain a Station identity claim.")
            : stationId;
    }

    public static string GetRequiredActorId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var actorId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new InvalidOperationException(
                "The authenticated caller does not contain a canonical Actor identity claim.");
        }

        return actorId;
    }
}
