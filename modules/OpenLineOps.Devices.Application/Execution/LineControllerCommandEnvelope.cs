using System.Text.Json.Serialization;

namespace OpenLineOps.Devices.Application.Execution;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LineControllerCommandEnvelope
{
    public const string RequiredSchema = "openlineops.line-controller-command";

    public LineControllerCommandEnvelope(
        string authorizationId,
        string targetStationSystemId,
        string targetSystemId,
        string targetBindingId,
        string targetCapabilityId,
        string targetAction,
        string? inputPayload)
    {
        AuthorizationId = Required(authorizationId, nameof(authorizationId));
        TargetStationSystemId = Required(
            targetStationSystemId,
            nameof(targetStationSystemId));
        TargetSystemId = Required(targetSystemId, nameof(targetSystemId));
        TargetBindingId = Required(targetBindingId, nameof(targetBindingId));
        TargetCapabilityId = Required(targetCapabilityId, nameof(targetCapabilityId));
        TargetAction = Required(targetAction, nameof(targetAction));
        InputPayload = inputPayload;
    }

    public string Schema { get; } = RequiredSchema;

    public string AuthorizationId { get; }

    public string TargetStationSystemId { get; }

    public string TargetSystemId { get; }

    public string TargetBindingId { get; }

    public string TargetCapabilityId { get; }

    public string TargetAction { get; }

    public string? InputPayload { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
}
