namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeRecoveryPlanResponse(
    int Count,
    IReadOnlyCollection<RuntimeRecoveryCandidateResponse> Candidates);
