using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed record RuntimeSessionRecoveryCandidate(
    RuntimeSessionId SessionId,
    StationId StationId,
    ProcessVersionId ProcessVersionId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    RecipeSnapshotId RecipeSnapshotId,
    RuntimeSessionStatus Status,
    DateTimeOffset LastTransitionAtUtc,
    string RecoveryReason);
