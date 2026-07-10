namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeRecoveryCandidateResponse(
    Guid SessionId,
    string StationSystemId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string Status,
    DateTimeOffset LastTransitionAtUtc,
    string RecoveryReason);
