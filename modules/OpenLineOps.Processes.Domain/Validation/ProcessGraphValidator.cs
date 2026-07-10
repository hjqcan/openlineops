using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;

namespace OpenLineOps.Processes.Domain.Validation;

public static class ProcessGraphValidator
{
    public static ProcessGraphValidationReport Validate(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = new List<ProcessGraphValidationIssue>();
        var nodes = definition.Nodes.ToDictionary(node => node.Id);
        var transitions = definition.Transitions.ToList();

        ValidateNodeSet(nodes, issues);
        ValidateCommandExecutionMetadata(nodes.Values, issues);
        ValidateBlocklyMetadata(nodes.Values, issues);
        ValidatePythonScriptMetadata(nodes.Values, issues);

        var validTransitions = ValidateTransitionEndpoints(nodes, transitions, issues);
        ValidateTransitionLoopPolicies(nodes, validTransitions, issues);

        var startNodes = nodes.Values.Where(node => node.Kind == ProcessNodeKind.Start).ToList();
        if (startNodes.Count == 1)
        {
            ValidateReachability(nodes, validTransitions, startNodes[0], issues);
        }

        ValidateAcyclic(nodes.Keys, validTransitions, issues);

        return new ProcessGraphValidationReport(issues);
    }

    private static void ValidateNodeSet(
        Dictionary<ProcessNodeId, ProcessNode> nodes,
        List<ProcessGraphValidationIssue> issues)
    {
        if (nodes.Count == 0)
        {
            issues.Add(Error(
                "Processes.GraphHasNoNodes",
                "Process graph must contain at least one node."));
        }

        var startNodeCount = nodes.Values.Count(node => node.Kind == ProcessNodeKind.Start);
        if (startNodeCount != 1)
        {
            issues.Add(Error(
                "Processes.GraphStartNodeCountInvalid",
                "Process graph must contain exactly one start node."));
        }
    }

    private static void ValidateCommandExecutionMetadata(
        IEnumerable<ProcessNode> nodes,
        List<ProcessGraphValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => node.RequiresCapability))
        {
            if (node.RequiredCapability is null)
            {
                issues.Add(Error(
                    "Processes.CommandCapabilityMissing",
                    $"Command node {node.Id} must declare a required capability."));
            }

            if (string.IsNullOrWhiteSpace(node.CommandName))
            {
                issues.Add(Error(
                    "Processes.CommandNameMissing",
                    $"Command node {node.Id} must declare a command name."));
            }

            if (node.TargetKind is null)
            {
                issues.Add(Error(
                    "Processes.CommandTargetKindMissing",
                    $"Command node {node.Id} must declare a target kind."));
            }

            if (string.IsNullOrWhiteSpace(node.TargetId))
            {
                issues.Add(Error(
                    "Processes.CommandTargetIdMissing",
                    $"Command node {node.Id} must declare a target id."));
            }

            if (node.CommandTimeout is null || node.CommandTimeout <= TimeSpan.Zero)
            {
                issues.Add(Error(
                    "Processes.CommandTimeoutInvalid",
                    $"Command node {node.Id} must declare a positive command timeout."));
            }
        }
    }

    private static void ValidatePythonScriptMetadata(
        IEnumerable<ProcessNode> nodes,
        List<ProcessGraphValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => node.IsPythonScript))
        {
            if (!string.Equals(node.ScriptLanguage, "Python", StringComparison.Ordinal))
            {
                issues.Add(Error(
                    "Processes.PythonScriptLanguageInvalid",
                    $"Python script node {node.Id} must declare Python as the script language."));
            }

            if (string.IsNullOrWhiteSpace(node.ScriptSourceCode))
            {
                issues.Add(Error(
                    "Processes.PythonScriptSourceMissing",
                    $"Python script node {node.Id} must persist Python source code."));
            }

            if (string.IsNullOrWhiteSpace(node.ScriptSourceHash))
            {
                issues.Add(Error(
                    "Processes.PythonScriptSourceHashMissing",
                    $"Python script node {node.Id} must persist a source hash."));
            }

            if (string.IsNullOrWhiteSpace(node.ScriptVersion))
            {
                issues.Add(Error(
                    "Processes.PythonScriptVersionMissing",
                    $"Python script node {node.Id} must declare a script version."));
            }

            if (node.ScriptTimeout is null || node.ScriptTimeout <= TimeSpan.Zero)
            {
                issues.Add(Error(
                    "Processes.PythonScriptTimeoutInvalid",
                    $"Python script node {node.Id} must declare a positive script timeout."));
            }
        }
    }

    private static void ValidateBlocklyMetadata(
        IEnumerable<ProcessNode> nodes,
        List<ProcessGraphValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => node.IsBlockly))
        {
            if (string.IsNullOrWhiteSpace(node.BlocklyWorkspaceJson))
            {
                issues.Add(Error(
                    "Processes.BlocklyWorkspaceMissing",
                    $"Blockly node {node.Id} must persist workspace JSON."));
            }

            if (node.CommandTimeout is null || node.CommandTimeout <= TimeSpan.Zero)
            {
                issues.Add(Error(
                    "Processes.BlocklyTimeoutInvalid",
                    $"Blockly node {node.Id} must declare a positive execution timeout."));
            }

            if (!string.IsNullOrWhiteSpace(node.ScriptLanguage)
                || !string.IsNullOrWhiteSpace(node.ScriptSourceCode)
                || !string.IsNullOrWhiteSpace(node.ScriptSourceHash)
                || !string.IsNullOrWhiteSpace(node.ScriptVersion)
                || node.ScriptTimeout is not null)
            {
                issues.Add(Error(
                    "Processes.BlocklyScriptMetadataForbidden",
                    $"Blockly node {node.Id} cannot contain Python script metadata."));
            }
        }
    }

    private static List<ProcessTransition> ValidateTransitionEndpoints(
        Dictionary<ProcessNodeId, ProcessNode> nodes,
        IEnumerable<ProcessTransition> transitions,
        List<ProcessGraphValidationIssue> issues)
    {
        var validTransitions = new List<ProcessTransition>();

        foreach (var transition in transitions)
        {
            var fromExists = nodes.ContainsKey(transition.FromNodeId);
            var toExists = nodes.ContainsKey(transition.ToNodeId);

            if (!fromExists)
            {
                issues.Add(Error(
                    "Processes.TransitionSourceMissing",
                    $"Transition {transition.Id} references missing source node {transition.FromNodeId}."));
            }

            if (!toExists)
            {
                issues.Add(Error(
                    "Processes.TransitionTargetMissing",
                    $"Transition {transition.Id} references missing target node {transition.ToNodeId}."));
            }

            if (fromExists && toExists)
            {
                validTransitions.Add(transition);
            }
        }

        return validTransitions;
    }

    private static void ValidateReachability(
        Dictionary<ProcessNodeId, ProcessNode> nodes,
        IEnumerable<ProcessTransition> transitions,
        ProcessNode startNode,
        List<ProcessGraphValidationIssue> issues)
    {
        var reachableNodeIds = FindReachableNodeIds(startNode.Id, transitions);

        foreach (var nodeId in nodes.Keys.Where(nodeId => !reachableNodeIds.Contains(nodeId)))
        {
            issues.Add(Error(
                "Processes.NodeUnreachable",
                $"Process node {nodeId} cannot be reached from the start node."));
        }
    }

    private static void ValidateTransitionLoopPolicies(
        Dictionary<ProcessNodeId, ProcessNode> nodes,
        IReadOnlyCollection<ProcessTransition> transitions,
        List<ProcessGraphValidationIssue> issues)
    {
        foreach (var transition in transitions)
        {
            if (transition.LoopPolicy == ProcessTransitionLoopPolicy.None)
            {
                if (transition.MaxTraversals is not null)
                {
                    issues.Add(Error(
                        "Processes.LoopPolicyMaxTraversalsWithoutPolicy",
                        $"Transition {transition.Id} declares max traversals without an explicit loop policy."));
                }

                continue;
            }

            if (transition.LoopPolicy != ProcessTransitionLoopPolicy.Counted)
            {
                issues.Add(Error(
                    "Processes.LoopPolicyUnsupported",
                    $"Transition {transition.Id} declares unsupported loop policy {transition.LoopPolicy}."));
                continue;
            }

            if (transition.MaxTraversals is null or <= 0)
            {
                issues.Add(Error(
                    "Processes.LoopPolicyMaxTraversalsInvalid",
                    $"Counted loop transition {transition.Id} must declare a positive max traversal count."));
            }

            if (nodes.TryGetValue(transition.FromNodeId, out var sourceNode)
                && sourceNode.Kind != ProcessNodeKind.Decision)
            {
                issues.Add(Error(
                    "Processes.LoopPolicySourceMustBeDecision",
                    $"Counted loop transition {transition.Id} must originate from a decision node."));
            }

            if (!TransitionCanCreateCycle(transition, transitions))
            {
                issues.Add(Error(
                    "Processes.LoopPolicyTransitionNotCyclic",
                    $"Counted loop transition {transition.Id} does not route back to an earlier reachable node."));
            }
        }
    }

    private static void ValidateAcyclic(
        IEnumerable<ProcessNodeId> nodeIds,
        IEnumerable<ProcessTransition> transitions,
        List<ProcessGraphValidationIssue> issues)
    {
        var adjacency = BuildAdjacency(transitions
            .Where(transition => transition.LoopPolicy == ProcessTransitionLoopPolicy.None));

        if (ContainsCycle(nodeIds, adjacency))
        {
            issues.Add(Error(
                "Processes.GraphCycleDetected",
                "Process graph contains a cycle, but loop policy is not explicit."));
        }
    }

    private static HashSet<ProcessNodeId> FindReachableNodeIds(
        ProcessNodeId startNodeId,
        IEnumerable<ProcessTransition> transitions)
    {
        var adjacency = BuildAdjacency(transitions);
        var reachable = new HashSet<ProcessNodeId>();
        var stack = new Stack<ProcessNodeId>();

        stack.Push(startNodeId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!reachable.Add(current))
            {
                continue;
            }

            if (!adjacency.TryGetValue(current, out var nextNodeIds))
            {
                continue;
            }

            foreach (var nextNodeId in nextNodeIds)
            {
                stack.Push(nextNodeId);
            }
        }

        return reachable;
    }

    private static Dictionary<ProcessNodeId, List<ProcessNodeId>> BuildAdjacency(
        IEnumerable<ProcessTransition> transitions)
    {
        var adjacency = new Dictionary<ProcessNodeId, List<ProcessNodeId>>();

        foreach (var transition in transitions)
        {
            if (!adjacency.TryGetValue(transition.FromNodeId, out var nextNodeIds))
            {
                nextNodeIds = [];
                adjacency.Add(transition.FromNodeId, nextNodeIds);
            }

            nextNodeIds.Add(transition.ToNodeId);
        }

        return adjacency;
    }

    private static bool TransitionCanCreateCycle(
        ProcessTransition transition,
        IReadOnlyCollection<ProcessTransition> transitions)
    {
        if (transition.FromNodeId == transition.ToNodeId)
        {
            return true;
        }

        var remainingTransitions = transitions.Where(candidate => candidate.Id != transition.Id);

        return CanReach(transition.ToNodeId, transition.FromNodeId, remainingTransitions);
    }

    private static bool CanReach(
        ProcessNodeId fromNodeId,
        ProcessNodeId targetNodeId,
        IEnumerable<ProcessTransition> transitions)
    {
        var adjacency = BuildAdjacency(transitions);
        var visited = new HashSet<ProcessNodeId>();
        var stack = new Stack<ProcessNodeId>();

        stack.Push(fromNodeId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current == targetNodeId)
            {
                return true;
            }

            if (!adjacency.TryGetValue(current, out var nextNodeIds))
            {
                continue;
            }

            foreach (var nextNodeId in nextNodeIds)
            {
                stack.Push(nextNodeId);
            }
        }

        return false;
    }

    private static bool ContainsCycle(
        IEnumerable<ProcessNodeId> nodeIds,
        IReadOnlyDictionary<ProcessNodeId, List<ProcessNodeId>> adjacency)
    {
        var states = new Dictionary<ProcessNodeId, VisitState>();

        foreach (var nodeId in nodeIds)
        {
            if (Visit(nodeId, adjacency, states))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Visit(
        ProcessNodeId nodeId,
        IReadOnlyDictionary<ProcessNodeId, List<ProcessNodeId>> adjacency,
        IDictionary<ProcessNodeId, VisitState> states)
    {
        if (states.TryGetValue(nodeId, out var state))
        {
            return state == VisitState.Visiting;
        }

        states[nodeId] = VisitState.Visiting;

        if (adjacency.TryGetValue(nodeId, out var nextNodeIds))
        {
            foreach (var nextNodeId in nextNodeIds)
            {
                if (Visit(nextNodeId, adjacency, states))
                {
                    return true;
                }
            }
        }

        states[nodeId] = VisitState.Visited;

        return false;
    }

    private static ProcessGraphValidationIssue Error(string code, string message)
    {
        return new ProcessGraphValidationIssue(ProcessGraphValidationSeverity.Error, code, message);
    }

    private enum VisitState
    {
        Visiting = 0,
        Visited = 1
    }
}
