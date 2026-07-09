namespace OpenLineOps.Runtime.Application.Recovery;

public sealed record RuntimeSessionRecoveryPlan(
    IReadOnlyCollection<RuntimeSessionRecoveryCandidate> Candidates)
{
    public int Count => Candidates.Count;
}
