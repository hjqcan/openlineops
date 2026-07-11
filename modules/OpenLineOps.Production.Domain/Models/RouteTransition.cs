using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Domain.Models;

public enum RouteTransitionKind
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
        OutputKey = ProductionIdGuard.PortablePathSegment(outputKey, nameof(outputKey));
        ExpectedValue = expectedValue ?? throw new ArgumentNullException(nameof(expectedValue));
    }

    public string OutputKey { get; }

    public ProductionContextValue ExpectedValue { get; }
}

public enum RouteJudgement
{
    Passed = 1,
    Failed = 2,
    Aborted = 3,
    Unknown = 4,
    NotApplicable = 5
}

public sealed class RouteTransition : Entity<RouteTransitionId>
{
    private RouteTransition(
        RouteTransitionId id,
        OperationDefinitionId sourceOperationId,
        OperationDefinitionId targetOperationId,
        RouteTransitionKind kind,
        RouteJudgement? requiredJudgement,
        int? maxTraversals,
        string? parallelGroupId,
        RouteOutputCondition? outputCondition)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        SourceOperationId = sourceOperationId
            ?? throw new ArgumentNullException(nameof(sourceOperationId));
        TargetOperationId = targetOperationId
            ?? throw new ArgumentNullException(nameof(targetOperationId));
        if (SourceOperationId == TargetOperationId)
        {
            throw new ArgumentException("Route transitions cannot target their source operation.");
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
                "Unsupported route judgement.");
        }

        var isConditional = kind is RouteTransitionKind.Judgement or RouteTransitionKind.Rework;
        if (isConditional != (requiredJudgement is not null))
        {
            throw new ArgumentException(
                "Judgement and rework transitions require exactly one result judgement; other transition kinds cannot define one.");
        }

        if ((kind == RouteTransitionKind.Condition) != (outputCondition is not null))
        {
            throw new ArgumentException(
                "Condition transitions require exactly one typed output equality condition; other transitions cannot define one.");
        }

        if ((kind == RouteTransitionKind.Rework) != (maxTraversals is not null))
        {
            throw new ArgumentException(
                "Rework transitions require a traversal limit; other transition kinds cannot define one.");
        }

        if (maxTraversals is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTraversals),
                "Rework transition traversal limits must be positive.");
        }

        var isParallel = kind is RouteTransitionKind.ParallelFork or RouteTransitionKind.ParallelJoin;
        if (isParallel != (parallelGroupId is not null))
        {
            throw new ArgumentException(
                "Parallel fork and join transitions require a group id; other transition kinds cannot define one.");
        }

        Kind = kind;
        RequiredJudgement = requiredJudgement;
        MaxTraversals = maxTraversals;
        ParallelGroupId = parallelGroupId is null
            ? null
            : ProductionIdGuard.PortablePathSegment(parallelGroupId, nameof(parallelGroupId));
        OutputCondition = outputCondition;
    }

    public OperationDefinitionId SourceOperationId { get; }

    public OperationDefinitionId TargetOperationId { get; }

    public RouteTransitionKind Kind { get; }

    public RouteJudgement? RequiredJudgement { get; }

    public int? MaxTraversals { get; }

    public string? ParallelGroupId { get; }

    public RouteOutputCondition? OutputCondition { get; }

    public static RouteTransition Create(
        RouteTransitionId id,
        OperationDefinitionId sourceOperationId,
        OperationDefinitionId targetOperationId,
        RouteTransitionKind kind,
        RouteJudgement? requiredJudgement = null,
        int? maxTraversals = null,
        string? parallelGroupId = null,
        RouteOutputCondition? outputCondition = null)
    {
        return new RouteTransition(
            id,
            sourceOperationId,
            targetOperationId,
            kind,
            requiredJudgement,
            maxTraversals,
            parallelGroupId,
            outputCondition);
    }
}
