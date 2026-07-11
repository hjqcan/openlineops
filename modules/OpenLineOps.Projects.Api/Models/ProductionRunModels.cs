using System.Text.Json.Serialization;

namespace OpenLineOps.Projects.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitPublishedProductionRunRequest(
    string? ProjectId,
    string? ProjectSnapshotId,
    string? ProductionRunId,
    string? ProductionUnitId,
    string? ActorId);
