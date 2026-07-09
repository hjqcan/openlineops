namespace OpenLineOps.Processes.Application.Definitions;

public sealed record CreateProcessTransitionRequest(
    string TransitionId,
    string FromNodeId,
    string ToNodeId,
    string? Label,
    string? LoopPolicy,
    int? MaxTraversals);
