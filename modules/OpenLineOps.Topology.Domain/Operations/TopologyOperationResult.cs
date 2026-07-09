namespace OpenLineOps.Topology.Domain.Operations;

public sealed record TopologyOperationResult(bool Succeeded, string Code, string Message)
{
    public static TopologyOperationResult Accepted(string message = "Accepted")
    {
        return new TopologyOperationResult(true, "Topology.Accepted", message);
    }

    public static TopologyOperationResult Rejected(string code, string message)
    {
        return new TopologyOperationResult(false, code, message);
    }
}
