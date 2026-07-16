using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

/// <summary>
/// Computes a fail-closed upper bound for one frozen Runtime process execution.
/// </summary>
/// <remarks>
/// Removing every counted transition must leave a directed acyclic graph. Any
/// execution is therefore one initial acyclic segment followed by at most one
/// acyclic segment for each permitted counted-transition traversal. Summing the
/// longest possible segment for the initial node and for every counted edge is
/// a mathematical upper bound for every legal execution path; it is not a
/// sampling heuristic or a graph-size multiplier.
/// </remarks>
public static class ExecutableRuntimeProcessExecutionBounds
{
    public static ExecutableRuntimeProcessBounds Calculate(ExecutableRuntimeProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var invalidNode = process.Nodes.FirstOrDefault(static node => !node.IsValid);
        if (invalidNode is not null)
        {
            throw new InvalidDataException(
                $"Runtime node {invalidNode.NodeId} has invalid execution metadata.");
        }

        return process.UsesGraph
            ? CalculateGraph(process)
            : CalculateLinear(process);
    }

    private static ExecutableRuntimeProcessBounds CalculateLinear(
        ExecutableRuntimeProcess process)
    {
        try
        {
            var ticks = process.Nodes.Aggregate(
                0L,
                static (total, node) => checked(total + node.Timeout.Ticks));
            return new ExecutableRuntimeProcessBounds(
                TimeSpan.FromTicks(ticks),
                process.Nodes.Count);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Runtime process maximum node execution time exceeds TimeSpan capacity.",
                exception);
        }
    }

    private static ExecutableRuntimeProcessBounds CalculateGraph(
        ExecutableRuntimeProcess process)
    {
        var startNodeId = process.StartNodeId
            ?? throw new InvalidDataException(
                "Runtime process graph must declare a start node id.");
        var executableNodes = new Dictionary<RuntimeNodeId, ExecutableRuntimeNode>();
        var routingNodes = new Dictionary<RuntimeNodeId, ExecutableRuntimeRoutingNode>();
        if (process.Nodes.Any(node => !executableNodes.TryAdd(node.NodeId, node))
            || process.RoutingNodes.Any(node => !routingNodes.TryAdd(node.NodeId, node))
            || executableNodes.Keys.Any(routingNodes.ContainsKey))
        {
            throw new InvalidDataException(
                "Runtime process graph node identities must be unique.");
        }

        var allNodeIds = executableNodes.Keys.Concat(routingNodes.Keys).ToHashSet();
        if (!allNodeIds.Contains(startNodeId))
        {
            throw new InvalidDataException(
                $"Runtime process graph start node {startNodeId} is missing.");
        }

        var invalidRoutingNode = process.RoutingNodes.FirstOrDefault(static node => !node.IsValid);
        if (invalidRoutingNode is not null)
        {
            throw new InvalidDataException(
                $"Runtime routing node {invalidRoutingNode.NodeId} has invalid metadata.");
        }

        foreach (var transition in process.Transitions)
        {
            if (!allNodeIds.Contains(transition.FromNodeId)
                || !allNodeIds.Contains(transition.ToNodeId))
            {
                throw new InvalidDataException(
                    $"Runtime transition {transition.FromNodeId} -> {transition.ToNodeId} references a missing node.");
            }

            if (transition.MaxTraversals is not null and <= 0)
            {
                throw new InvalidDataException(
                    $"Runtime transition {transition.FromNodeId} -> {transition.ToNodeId} must declare a positive max traversal count.");
            }
        }

        var transitionsBySource = process.Transitions
            .GroupBy(static transition => transition.FromNodeId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        foreach (var (sourceNodeId, outgoingTransitions) in transitionsBySource)
        {
            if (outgoingTransitions.Length > 1
                && (!routingNodes.TryGetValue(sourceNodeId, out var sourceNode)
                    || sourceNode.Kind != ExecutableRuntimeRoutingNodeKind.Decision))
            {
                throw new InvalidDataException(
                    $"Runtime node {sourceNodeId} branches but is not a Decision node.");
            }
        }

        var reachable = FindReachable(startNodeId, transitionsBySource);
        if (reachable.Count != allNodeIds.Count)
        {
            var missing = allNodeIds
                .Where(nodeId => !reachable.Contains(nodeId))
                .OrderBy(static nodeId => nodeId.Value, StringComparer.Ordinal);
            throw new InvalidDataException(
                $"Runtime process graph contains unreachable nodes: {string.Join(", ", missing)}.");
        }

        var uncountedBySource = process.Transitions
            .Where(static transition => transition.MaxTraversals is null)
            .GroupBy(static transition => transition.FromNodeId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var topologicalOrder = TopologicalOrder(reachable, uncountedBySource);
        var segmentBounds = CalculateAcyclicSegmentBounds(
            topologicalOrder,
            uncountedBySource,
            executableNodes);

        try
        {
            var maximumTicks = segmentBounds[startNodeId].ExecutionTicks;
            long maximumNodeVisits = segmentBounds[startNodeId].NodeVisits;
            foreach (var transition in process.Transitions.Where(static item =>
                         item.MaxTraversals is not null))
            {
                var segment = segmentBounds[transition.ToNodeId];
                maximumTicks = checked(maximumTicks
                    + checked(segment.ExecutionTicks * transition.MaxTraversals!.Value));
                maximumNodeVisits = checked(maximumNodeVisits
                    + checked((long)segment.NodeVisits * transition.MaxTraversals.Value));
            }

            if (maximumNodeVisits > int.MaxValue)
            {
                throw new OverflowException(
                    "Runtime process maximum node visits exceed the executable traversal counter capacity.");
            }

            return new ExecutableRuntimeProcessBounds(
                TimeSpan.FromTicks(maximumTicks),
                checked((int)maximumNodeVisits));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Runtime process bounded execution envelope exceeds supported duration or traversal capacity.",
                exception);
        }
    }

    private static HashSet<RuntimeNodeId> FindReachable(
        RuntimeNodeId startNodeId,
        Dictionary<RuntimeNodeId, ExecutableRuntimeTransition[]> transitionsBySource)
    {
        var reachable = new HashSet<RuntimeNodeId>();
        var pending = new Stack<RuntimeNodeId>();
        pending.Push(startNodeId);
        while (pending.TryPop(out var nodeId))
        {
            if (!reachable.Add(nodeId)
                || !transitionsBySource.TryGetValue(nodeId, out var outgoing))
            {
                continue;
            }

            foreach (var transition in outgoing)
            {
                pending.Push(transition.ToNodeId);
            }
        }

        return reachable;
    }

    private static List<RuntimeNodeId> TopologicalOrder(
        HashSet<RuntimeNodeId> nodeIds,
        Dictionary<RuntimeNodeId, ExecutableRuntimeTransition[]> uncountedBySource)
    {
        var incoming = nodeIds.ToDictionary(static nodeId => nodeId, static _ => 0);
        foreach (var transition in uncountedBySource.Values.SelectMany(static value => value))
        {
            incoming[transition.ToNodeId] = checked(incoming[transition.ToNodeId] + 1);
        }

        var ready = new PriorityQueue<RuntimeNodeId, string>(StringComparer.Ordinal);
        foreach (var (nodeId, count) in incoming)
        {
            if (count == 0)
            {
                ready.Enqueue(nodeId, nodeId.Value);
            }
        }

        var ordered = new List<RuntimeNodeId>(nodeIds.Count);
        while (ready.TryDequeue(out var nodeId, out _))
        {
            ordered.Add(nodeId);
            if (!uncountedBySource.TryGetValue(nodeId, out var outgoing))
            {
                continue;
            }

            foreach (var transition in outgoing)
            {
                var remaining = checked(incoming[transition.ToNodeId] - 1);
                incoming[transition.ToNodeId] = remaining;
                if (remaining == 0)
                {
                    ready.Enqueue(transition.ToNodeId, transition.ToNodeId.Value);
                }
            }
        }

        return ordered.Count == nodeIds.Count
            ? ordered
            : throw new InvalidDataException(
                "Runtime process graph contains a cycle without an explicit counted transition.");
    }

    private static Dictionary<RuntimeNodeId, AcyclicSegmentBounds>
        CalculateAcyclicSegmentBounds(
            List<RuntimeNodeId> topologicalOrder,
            Dictionary<RuntimeNodeId, ExecutableRuntimeTransition[]> uncountedBySource,
            Dictionary<RuntimeNodeId, ExecutableRuntimeNode> executableNodes)
    {
        var result = new Dictionary<RuntimeNodeId, AcyclicSegmentBounds>();
        try
        {
            for (var index = topologicalOrder.Count - 1; index >= 0; index--)
            {
                var nodeId = topologicalOrder[index];
                long childTicks = 0;
                var childVisits = 0;
                if (uncountedBySource.TryGetValue(nodeId, out var outgoing))
                {
                    foreach (var transition in outgoing)
                    {
                        var child = result[transition.ToNodeId];
                        childTicks = Math.Max(childTicks, child.ExecutionTicks);
                        childVisits = Math.Max(childVisits, child.NodeVisits);
                    }
                }

                var ownTicks = executableNodes.TryGetValue(nodeId, out var executableNode)
                    ? executableNode.Timeout.Ticks
                    : 0L;
                result.Add(nodeId, new AcyclicSegmentBounds(
                    checked(ownTicks + childTicks),
                    checked(1 + childVisits)));
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Runtime process acyclic execution segment exceeds supported duration or traversal capacity.",
                exception);
        }

        return result;
    }

    private sealed record AcyclicSegmentBounds(long ExecutionTicks, int NodeVisits);
}

public sealed record ExecutableRuntimeProcessBounds(
    TimeSpan MaximumNodeExecutionTime,
    int MaximumNodeVisits);
