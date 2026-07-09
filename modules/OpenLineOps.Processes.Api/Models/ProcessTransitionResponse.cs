namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessTransitionResponse(
    string TransitionId,
    string FromNodeId,
    string ToNodeId,
    string? Label,
    string LoopPolicy,
    int? MaxTraversals);
