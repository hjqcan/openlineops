using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcquireStationAgentControlLeaseApiRequest(
    string OwnerInstanceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenewStationAgentControlLeaseApiRequest(
    string OwnerInstanceId,
    long FencingToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseStationAgentControlLeaseApiRequest(
    string OwnerInstanceId,
    long FencingToken);

public sealed record StationAgentControlLeaseApiResponse(
    string StationId,
    string OwnerAgentId,
    string OwnerInstanceId,
    long FencingToken,
    bool Active,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset RenewedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record StationAgentControlLeaseStatusApiResponse(
    string StationId,
    bool Active,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ObservedAtUtc);
