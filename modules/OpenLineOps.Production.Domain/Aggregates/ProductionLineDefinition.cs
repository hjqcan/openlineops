using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;

namespace OpenLineOps.Production.Domain.Aggregates;

public sealed class ProductionLineDefinition : AggregateRoot<ProductionLineDefinitionId>
{
    private readonly List<OperationDefinition> _operations;
    private readonly List<RouteTransition> _transitions;
    private readonly List<ExternalTestProgramAdapter> _externalTestProgramAdapters;

    private ProductionLineDefinition(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        ProductModelDefinition productModel,
        OperationDefinitionId entryOperationId,
        IEnumerable<OperationDefinition> operations,
        IEnumerable<RouteTransition> transitions,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        TopologyId = ProductionIdGuard.NotBlank(topologyId, nameof(topologyId));
        ProductModel = productModel ?? throw new ArgumentNullException(nameof(productModel));
        EntryOperationId = entryOperationId ?? throw new ArgumentNullException(nameof(entryOperationId));
        _operations = MaterializeRequired(operations, nameof(operations))
            .OrderBy(operation => operation.Id.Value, StringComparer.Ordinal)
            .ToList();
        _transitions = MaterializeRequired(transitions, nameof(transitions))
            .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
            .ToList();
        _externalTestProgramAdapters = MaterializeRequired(
                externalTestProgramAdapters,
                nameof(externalTestProgramAdapters))
            .OrderBy(adapter => adapter.Id.Value, StringComparer.Ordinal)
            .ToList();
        EnsureValidComposition();
        if (createdAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Production line timestamps must use UTC offset zero.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Production line updated timestamp cannot precede creation.");
        }

        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string DisplayName { get; }

    public string TopologyId { get; }

    public ProductModelDefinition ProductModel { get; }

    public OperationDefinitionId EntryOperationId { get; }

    public IReadOnlyCollection<OperationDefinition> Operations => _operations.AsReadOnly();

    public IReadOnlyCollection<RouteTransition> Transitions => _transitions.AsReadOnly();

    public IReadOnlyCollection<ExternalTestProgramAdapter> ExternalTestProgramAdapters =>
        _externalTestProgramAdapters.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static ProductionLineDefinition Create(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        ProductModelDefinition productModel,
        OperationDefinitionId entryOperationId,
        IEnumerable<OperationDefinition> operations,
        IEnumerable<RouteTransition> transitions,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc)
    {
        return Restore(
            id,
            displayName,
            topologyId,
            productModel,
            entryOperationId,
            operations,
            transitions,
            externalTestProgramAdapters,
            createdAtUtc,
            createdAtUtc);
    }

    public static ProductionLineDefinition Restore(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        ProductModelDefinition productModel,
        OperationDefinitionId entryOperationId,
        IEnumerable<OperationDefinition> operations,
        IEnumerable<RouteTransition> transitions,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(externalTestProgramAdapters);

        return new ProductionLineDefinition(
            id,
            displayName,
            topologyId,
            productModel,
            entryOperationId,
            operations,
            transitions,
            externalTestProgramAdapters,
            createdAtUtc,
            updatedAtUtc);
    }

    private void EnsureValidComposition()
    {
        if (_operations.Count == 0)
        {
            throw new ArgumentException("Production line requires at least one operation.");
        }

        EnsureUnique(_operations.Select(operation => operation.Id.Value), "operation ids");
        EnsureUnique(_transitions.Select(transition => transition.Id.Value), "transition ids");
        EnsureUnique(
            _externalTestProgramAdapters.Select(adapter => adapter.Id.Value),
            "external test program adapter ids");
        if (_operations.All(operation => operation.Id != EntryOperationId))
        {
            throw new ArgumentException(
                $"Production line entry operation {EntryOperationId} does not exist.");
        }

        var operationIds = _operations.Select(operation => operation.Id).ToHashSet();
        foreach (var transition in _transitions)
        {
            if (!operationIds.Contains(transition.SourceOperationId)
                || !operationIds.Contains(transition.TargetOperationId))
            {
                throw new ArgumentException(
                    $"Route transition {transition.Id} must reference existing source and target operations.");
            }
        }

        var duplicateEdge = _transitions
            .GroupBy(transition => (
                transition.SourceOperationId,
                transition.TargetOperationId,
                transition.Kind))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEdge is not null)
        {
            throw new ArgumentException("Production line route cannot contain duplicate semantic transitions.");
        }

        var forwardTransitions = _transitions
            .Where(transition => transition.Kind != RouteTransitionKind.Rework)
            .ToArray();
        if (forwardTransitions.Any(transition => transition.TargetOperationId == EntryOperationId))
        {
            throw new ArgumentException(
                "Production line entry operation cannot have an incoming forward transition.");
        }

        EnsureOutgoingShapes();
        EnsureAllOperationsReachable();
        EnsureForwardRouteIsAcyclic(forwardTransitions);
        EnsureEveryOperationCanComplete(forwardTransitions);
        EnsureReworkTransitionsAreBackwardAndBounded(forwardTransitions);
        EnsureParallelForkJoinGroups(forwardTransitions);
    }

    private void EnsureOutgoingShapes()
    {
        foreach (var operation in _operations)
        {
            var outgoing = _transitions
                .Where(transition => transition.SourceOperationId == operation.Id)
                .ToArray();
            if (outgoing.Length == 0)
            {
                continue;
            }

            if (outgoing.Length == 1
                && outgoing[0].Kind is RouteTransitionKind.Sequence
                    or RouteTransitionKind.ParallelJoin)
            {
                continue;
            }

            if (outgoing.All(transition => transition.Kind is
                    RouteTransitionKind.Judgement or RouteTransitionKind.Rework))
            {
                var judgements = outgoing.Select(transition => transition.RequiredJudgement!.Value).ToArray();
                if (judgements.Distinct().Count() != judgements.Length)
                {
                    throw new ArgumentException(
                        $"Operation {operation.Id} route judgements must be unique.");
                }

                continue;
            }

            if (outgoing.All(transition => transition.Kind == RouteTransitionKind.Condition))
            {
                var outputKeys = outgoing
                    .Select(transition => transition.OutputCondition!.OutputKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var expectedValues = outgoing
                    .Select(transition => transition.OutputCondition!.ExpectedValue)
                    .ToArray();
                if (outputKeys.Length != 1
                    || expectedValues.Distinct().Count() != expectedValues.Length)
                {
                    throw new ArgumentException(
                        $"Operation {operation.Id} condition routes must use one output key and unique typed expected values.");
                }

                continue;
            }

            if (outgoing.Length >= 2
                && outgoing.All(transition => transition.Kind == RouteTransitionKind.ParallelFork)
                && outgoing.Select(transition => transition.ParallelGroupId)
                    .Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }

            throw new ArgumentException(
                $"Operation {operation.Id} must use exactly one sequence/join edge, one unique judgement or typed condition branch set, or one parallel fork group.");
        }
    }

    private void EnsureAllOperationsReachable()
    {
        var reachable = TraverseFrom(EntryOperationId, _transitions);
        var unreachable = _operations.FirstOrDefault(operation => !reachable.Contains(operation.Id));
        if (unreachable is not null)
        {
            throw new ArgumentException(
                $"Operation {unreachable.Id} is not reachable from entry operation {EntryOperationId}.");
        }
    }

    private void EnsureForwardRouteIsAcyclic(IReadOnlyCollection<RouteTransition> forwardTransitions)
    {
        var indegrees = _operations.ToDictionary(operation => operation.Id, _ => 0);
        foreach (var transition in forwardTransitions)
        {
            indegrees[transition.TargetOperationId]++;
        }

        var queue = new Queue<OperationDefinitionId>(
            indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (queue.TryDequeue(out var operationId))
        {
            visited++;
            foreach (var transition in forwardTransitions.Where(candidate =>
                         candidate.SourceOperationId == operationId))
            {
                indegrees[transition.TargetOperationId]--;
                if (indegrees[transition.TargetOperationId] == 0)
                {
                    queue.Enqueue(transition.TargetOperationId);
                }
            }
        }

        if (visited != _operations.Count)
        {
            throw new ArgumentException(
                "Production line forward route must be acyclic; every loop must use an explicitly bounded rework transition.");
        }
    }

    private void EnsureEveryOperationCanComplete(
        IReadOnlyCollection<RouteTransition> forwardTransitions)
    {
        var terminals = _operations
            .Where(operation => forwardTransitions.All(transition =>
                transition.SourceOperationId != operation.Id))
            .Select(operation => operation.Id)
            .ToArray();
        if (terminals.Length == 0)
        {
            throw new ArgumentException("Production line route requires at least one terminal operation.");
        }

        var completable = new HashSet<OperationDefinitionId>(terminals);
        var queue = new Queue<OperationDefinitionId>(terminals);
        while (queue.TryDequeue(out var targetId))
        {
            foreach (var transition in forwardTransitions.Where(candidate =>
                         candidate.TargetOperationId == targetId))
            {
                if (completable.Add(transition.SourceOperationId))
                {
                    queue.Enqueue(transition.SourceOperationId);
                }
            }
        }

        var trapped = _operations.FirstOrDefault(operation => !completable.Contains(operation.Id));
        if (trapped is not null)
        {
            throw new ArgumentException(
                $"Operation {trapped.Id} has no forward path to a terminal operation.");
        }
    }

    private void EnsureReworkTransitionsAreBackwardAndBounded(
        IReadOnlyCollection<RouteTransition> forwardTransitions)
    {
        foreach (var rework in _transitions.Where(transition =>
                     transition.Kind == RouteTransitionKind.Rework))
        {
            var reachableFromTarget = TraverseFrom(rework.TargetOperationId, forwardTransitions);
            if (!reachableFromTarget.Contains(rework.SourceOperationId))
            {
                throw new ArgumentException(
                    $"Rework transition {rework.Id} must return to an earlier operation on its forward route.");
            }
        }
    }

    private void EnsureParallelForkJoinGroups(
        IReadOnlyCollection<RouteTransition> forwardTransitions)
    {
        var groups = _transitions
            .Where(transition => transition.ParallelGroupId is not null)
            .GroupBy(transition => transition.ParallelGroupId!, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var forks = group.Where(transition =>
                transition.Kind == RouteTransitionKind.ParallelFork).ToArray();
            var joins = group.Where(transition =>
                transition.Kind == RouteTransitionKind.ParallelJoin).ToArray();
            if (forks.Length < 2
                || joins.Length < 2
                || forks.Length != joins.Length
                || forks.Select(transition => transition.SourceOperationId).Distinct().Count() != 1
                || joins.Select(transition => transition.TargetOperationId).Distinct().Count() != 1
                || forks.Select(transition => transition.TargetOperationId).Distinct().Count() != forks.Length
                || joins.Select(transition => transition.SourceOperationId).Distinct().Count() != joins.Length)
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} must define one fork and one join with the same number of distinct branches.");
            }

            var forkSource = forks[0].SourceOperationId;
            var joinTarget = joins[0].TargetOperationId;
            if (forkSource == joinTarget)
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} fork and join operations must be distinct.");
            }

            var joinSources = joins.Select(transition => transition.SourceOperationId).ToHashSet();
            var branchSets = new List<HashSet<OperationDefinitionId>>();
            var assignedJoinSources = new HashSet<OperationDefinitionId>();
            foreach (var fork in forks)
            {
                var branch = TraverseUntil(
                    fork.TargetOperationId,
                    joinTarget,
                    forwardTransitions);
                var branchJoinSources = branch.Where(joinSources.Contains).ToArray();
                if (branchJoinSources.Length != 1 || !assignedJoinSources.Add(branchJoinSources[0]))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} must map every fork branch to exactly one distinct join branch.");
                }

                if (branch.Any(operationId => forwardTransitions.Any(transition =>
                        transition.SourceOperationId == operationId
                        && transition.Kind is RouteTransitionKind.ParallelFork
                            or RouteTransitionKind.ParallelJoin
                        && !string.Equals(
                            transition.ParallelGroupId,
                            group.Key,
                            StringComparison.Ordinal))))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} cannot contain a nested parallel group.");
                }

                var branchTerminal = branch.FirstOrDefault(operationId =>
                    forwardTransitions.All(transition => transition.SourceOperationId != operationId));
                if (branchTerminal is not null)
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} branch {fork.TargetOperationId} can terminate before its join.");
                }

                if (branchSets.Any(existing => existing.Overlaps(branch)))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} branches must remain disjoint until the join.");
                }

                branchSets.Add(branch);
            }

            if (!assignedJoinSources.SetEquals(joinSources))
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} contains an unmatched join branch.");
            }

            foreach (var branch in branchSets)
            {
                foreach (var operationId in branch)
                {
                    var invalidIncoming = forwardTransitions.Any(transition =>
                        transition.TargetOperationId == operationId
                        && !branch.Contains(transition.SourceOperationId)
                        && !(transition.SourceOperationId == forkSource
                            && transition.Kind == RouteTransitionKind.ParallelFork
                            && string.Equals(
                                transition.ParallelGroupId,
                                group.Key,
                                StringComparison.Ordinal)));
                    if (invalidIncoming)
                    {
                        throw new ArgumentException(
                            $"Parallel group {group.Key} branch cannot be entered outside its fork.");
                    }

                    foreach (var transition in forwardTransitions.Where(candidate =>
                                 candidate.SourceOperationId == operationId
                                 && candidate.TargetOperationId == joinTarget))
                    {
                        if (transition.Kind != RouteTransitionKind.ParallelJoin
                            || !string.Equals(
                                transition.ParallelGroupId,
                                group.Key,
                                StringComparison.Ordinal))
                        {
                            throw new ArgumentException(
                                $"Parallel group {group.Key} branches must enter their join through parallel join transitions.");
                        }
                    }
                }
            }
        }

        var unpairedParallel = _transitions.FirstOrDefault(transition =>
            transition.Kind is RouteTransitionKind.ParallelFork or RouteTransitionKind.ParallelJoin
            && !groups.Any(group => string.Equals(
                group.Key,
                transition.ParallelGroupId,
                StringComparison.Ordinal)));
        if (unpairedParallel is not null)
        {
            throw new ArgumentException(
                $"Parallel transition {unpairedParallel.Id} is not part of a complete fork/join group.");
        }
    }

    private static HashSet<OperationDefinitionId> TraverseFrom(
        OperationDefinitionId start,
        IEnumerable<RouteTransition> transitions)
    {
        var edges = transitions.ToLookup(transition => transition.SourceOperationId);
        var visited = new HashSet<OperationDefinitionId>();
        var queue = new Queue<OperationDefinitionId>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var operationId))
        {
            if (!visited.Add(operationId))
            {
                continue;
            }

            foreach (var transition in edges[operationId])
            {
                queue.Enqueue(transition.TargetOperationId);
            }
        }

        return visited;
    }

    private static HashSet<OperationDefinitionId> TraverseUntil(
        OperationDefinitionId start,
        OperationDefinitionId stop,
        IEnumerable<RouteTransition> transitions)
    {
        var edges = transitions.ToLookup(transition => transition.SourceOperationId);
        var visited = new HashSet<OperationDefinitionId>();
        var queue = new Queue<OperationDefinitionId>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var operationId))
        {
            if (operationId == stop || !visited.Add(operationId))
            {
                continue;
            }

            foreach (var transition in edges[operationId])
            {
                queue.Enqueue(transition.TargetOperationId);
            }
        }

        return visited;
    }

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var valueArray = values.ToArray();
        if (valueArray.Distinct(StringComparer.Ordinal).Count() != valueArray.Length)
        {
            throw new ArgumentException($"Production line {description} must be unique.");
        }

        if (valueArray.Distinct(StringComparer.OrdinalIgnoreCase).Count() != valueArray.Length)
        {
            throw new ArgumentException(
                $"Production line {description} cannot contain identities that differ only by case.");
        }
    }

    private static List<T> MaterializeRequired<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        var items = values.ToList();
        if (items.Any(static item => item is null))
        {
            throw new ArgumentException(
                "Production line semantic collections cannot contain null items.",
                parameterName);
        }

        return items;
    }
}
