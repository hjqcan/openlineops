using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcknowledgeStationControllerRecoveryApiRequest(
    long ExpectedRecoveryEpoch,
    string ControllerSessionId,
    string OperationalStateSha256,
    string Reason);
