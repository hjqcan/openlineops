using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Sessions;

public sealed record StartRuntimeSessionRequest(
    StationId StationId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    RecipeSnapshotId RecipeSnapshotId,
    ExecutableRuntimeProcess Process,
    RuntimeSessionTraceMetadata? TraceMetadata = null);
