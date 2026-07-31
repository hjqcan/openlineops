using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Domain.Runs;

public enum RuntimeRouteTransitionKind
{
    Sequence = 1,
    Judgement = 2,
    Condition = 3,
    Rework = 4,
    ParallelFork = 5,
    ParallelJoin = 6
}

public sealed record RouteOutputCondition
{
    public RouteOutputCondition(string outputKey, ProductionContextValue expectedValue)
    {
        OutputKey = ProductionRunText.Required(outputKey, nameof(outputKey));
        ExpectedValue = expectedValue ?? throw new ArgumentNullException(nameof(expectedValue));
    }

    public string OutputKey { get; }

    public ProductionContextValue ExpectedValue { get; }

    public bool Matches(IReadOnlyDictionary<string, ProductionContextValue> outputs) =>
        outputs.TryGetValue(OutputKey, out var actual) && actual == ExpectedValue;
}

public sealed record RouteTransitionDefinition
{
    public RouteTransitionDefinition(
        string transitionId,
        string sourceOperationId,
        string? targetOperationId,
        RuntimeRouteTransitionKind kind,
        ResultJudgement? requiredJudgement = null,
        int? maxTraversals = null,
        string? parallelGroupId = null,
        RouteOutputCondition? outputCondition = null,
        ProductDisposition? terminalDisposition = null)
    {
        TransitionId = ProductionRunText.Required(transitionId, nameof(transitionId));
        SourceOperationId = ProductionRunText.Required(sourceOperationId, nameof(sourceOperationId));
        if ((targetOperationId is null) == (terminalDisposition is null))
        {
            throw new ArgumentException(
                "A route transition requires exactly one target Operation or terminal disposition.");
        }

        TargetOperationId = targetOperationId is null
            ? null
            : ProductionRunText.Required(targetOperationId, nameof(targetOperationId));
        if (terminalDisposition is ProductDisposition.InProcess
            || terminalDisposition is not null && !Enum.IsDefined(terminalDisposition.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalDisposition),
                terminalDisposition,
                "A terminal route disposition must be Completed, Nonconforming, Held, or Scrapped.");
        }

        TerminalDisposition = terminalDisposition;
        if (string.Equals(SourceOperationId, TargetOperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A route transition cannot target its source operation.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported route transition kind.");
        }

        if (requiredJudgement is not null && !Enum.IsDefined(requiredJudgement.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredJudgement),
                requiredJudgement,
                "Unsupported result judgement.");
        }

        var conditional = kind is RuntimeRouteTransitionKind.Judgement
            or RuntimeRouteTransitionKind.Rework;
        if (conditional != (requiredJudgement is not null))
        {
            throw new ArgumentException(
                "Judgement and rework transitions require exactly one result judgement.");
        }

        if ((kind == RuntimeRouteTransitionKind.Condition) != (outputCondition is not null))
        {
            throw new ArgumentException(
                "Condition transitions require exactly one typed output equality condition; other transition kinds cannot define one.");
        }

        if ((kind == RuntimeRouteTransitionKind.Rework) != (maxTraversals is not null)
            || maxTraversals is <= 0)
        {
            throw new ArgumentException(
                "Rework transitions require a positive traversal limit and other transitions cannot define one.");
        }

        var parallel = kind is RuntimeRouteTransitionKind.ParallelFork
            or RuntimeRouteTransitionKind.ParallelJoin;
        if (parallel != (parallelGroupId is not null))
        {
            throw new ArgumentException(
                "Parallel transitions require a group id and other transitions cannot define one.");
        }

        if (terminalDisposition is not null
            && kind is RuntimeRouteTransitionKind.Rework
                or RuntimeRouteTransitionKind.ParallelFork
                or RuntimeRouteTransitionKind.ParallelJoin)
        {
            throw new ArgumentException(
                "Rework and parallel transitions must target an Operation.");
        }

        Kind = kind;
        RequiredJudgement = requiredJudgement;
        MaxTraversals = maxTraversals;
        ParallelGroupId = parallelGroupId is null
            ? null
            : ProductionRunText.Required(parallelGroupId, nameof(parallelGroupId));
        OutputCondition = outputCondition;
    }

    public string TransitionId { get; }

    public string SourceOperationId { get; }

    public string? TargetOperationId { get; }

    public ProductDisposition? TerminalDisposition { get; }

    public RuntimeRouteTransitionKind Kind { get; }

    public ResultJudgement? RequiredJudgement { get; }

    public int? MaxTraversals { get; }

    public string? ParallelGroupId { get; }

    public RouteOutputCondition? OutputCondition { get; }
}
