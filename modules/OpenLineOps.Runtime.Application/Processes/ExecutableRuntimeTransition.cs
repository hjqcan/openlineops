using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeTransition(
    RuntimeNodeId FromNodeId,
    RuntimeNodeId ToNodeId,
    string? Label,
    int? MaxTraversals = null);
