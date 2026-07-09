using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Domain.Transitions;

public sealed class ProcessTransition : Entity<ProcessTransitionId>
{
    private ProcessTransition(
        ProcessTransitionId id,
        ProcessNodeId fromNodeId,
        ProcessNodeId toNodeId,
        string? label,
        ProcessTransitionLoopPolicy loopPolicy,
        int? maxTraversals)
        : base(id)
    {
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Label = NormalizeLabel(label);
        LoopPolicy = loopPolicy;
        MaxTraversals = maxTraversals;
    }

    public ProcessNodeId FromNodeId { get; }

    public ProcessNodeId ToNodeId { get; }

    public string? Label { get; }

    public ProcessTransitionLoopPolicy LoopPolicy { get; }

    public int? MaxTraversals { get; }

    public static ProcessTransition Create(
        ProcessTransitionId id,
        ProcessNodeId fromNodeId,
        ProcessNodeId toNodeId,
        string? label = null,
        ProcessTransitionLoopPolicy loopPolicy = ProcessTransitionLoopPolicy.None,
        int? maxTraversals = null)
    {
        return new ProcessTransition(id, fromNodeId, toNodeId, label, loopPolicy, maxTraversals);
    }

    private static string? NormalizeLabel(string? label)
    {
        return string.IsNullOrWhiteSpace(label)
            ? null
            : label.Trim();
    }
}
