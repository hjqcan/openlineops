using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Sessions;

public sealed record StartRuntimeSessionRequest
{
    public StartRuntimeSessionRequest(
        RuntimeSessionId sessionId,
        StationId stationId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        ExecutableRuntimeProcess process,
        RuntimeSessionTraceMetadata traceMetadata)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Runtime session id cannot be empty.", nameof(sessionId));
        }

        SessionId = sessionId;
        StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        ConfigurationSnapshotId = configurationSnapshotId
            ?? throw new ArgumentNullException(nameof(configurationSnapshotId));
        RecipeSnapshotId = recipeSnapshotId ?? throw new ArgumentNullException(nameof(recipeSnapshotId));
        Process = process ?? throw new ArgumentNullException(nameof(process));
        TraceMetadata = traceMetadata ?? throw new ArgumentNullException(nameof(traceMetadata));
    }

    public RuntimeSessionId SessionId { get; }

    public StationId StationId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }

    public ExecutableRuntimeProcess Process { get; }

    public RuntimeSessionTraceMetadata TraceMetadata { get; }
}
