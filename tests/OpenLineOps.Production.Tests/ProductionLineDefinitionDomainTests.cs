using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Tests;

public sealed class ProductionLineDefinitionDomainTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatePreservesProductAndRouteGraphContract()
    {
        var definition = Definition();

        Assert.Equal("MODEL-A", definition.ProductModel.ModelCode);
        Assert.Equal("operation.load", definition.EntryOperationId.Value);
        Assert.Equal(
            ["operation.load", "operation.test"],
            definition.Operations.Select(operation => operation.Id.Value));
        var transition = Assert.Single(definition.Transitions, candidate =>
            candidate.TargetOperationId is not null);
        Assert.Equal(RouteTransitionKind.Sequence, transition.Kind);
        Assert.Equal("operation.test", transition.TargetOperationId!.Value);
        Assert.Equal("station.eol", definition.Operations.Last().StationSystemId);
        Assert.Equal("configuration.flow.test", definition.Operations.Last().ConfigurationSnapshotId);
        Assert.Equal(
            ["operation.load", "operation.test"],
            definition.RouteLayout.OperationPositions.Select(position => position.OperationId.Value));
    }

    [Fact]
    public void CreateRejectsRouteLayoutMissingOrAddingAnOperation()
    {
        var operations = new[] { Operation("operation.load"), Operation("operation.test") };
        var transitions = new[]
        {
            Sequence("load-test", "operation.load", "operation.test"),
            Terminal("test-completed", "operation.test", TerminalDisposition.Completed)
        };
        var missing = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            operations,
            transitions,
            routeLayout: new ProductionRouteLayout(
            [
                new OperationCanvasPosition(new OperationDefinitionId("operation.load"), 120, 80)
            ])));
        Assert.Contains("exactly one position", missing.Message, StringComparison.OrdinalIgnoreCase);

        var extra = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            operations,
            transitions,
            routeLayout: new ProductionRouteLayout(
            [
                new OperationCanvasPosition(new OperationDefinitionId("operation.load"), 120, 80),
                new OperationCanvasPosition(new OperationDefinitionId("operation.test"), 400, 80),
                new OperationCanvasPosition(new OperationDefinitionId("operation.extra"), 680, 80)
            ])));
        Assert.Contains("exactly one position", extra.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100001, 0)]
    [InlineData(0, 100001)]
    public void RouteLayoutRejectsCoordinatesOutsideExplicitBounds(int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationCanvasPosition(
            new OperationDefinitionId("operation.load"),
            x,
            y));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" configuration.main")]
    [InlineData("configuration.main ")]
    public void OperationRejectsMissingOrNonCanonicalConfigurationSnapshotId(
        string? configurationSnapshotId)
    {
        Assert.Throws<ArgumentException>(() => OperationDefinition.Create(
            new OperationDefinitionId("operation.invalid-configuration"),
            "Invalid Configuration",
            "station.eol",
            "flow.load",
            configurationSnapshotId!,
            StationResources("invalid-configuration"),
            []));
    }

    [Fact]
    public void CreateRejectsUnreachableOperation()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            [Operation("operation.load"), Operation("operation.test")],
            [
                Terminal("load-terminal", "operation.load", TerminalDisposition.Completed),
                Terminal("test-terminal", "operation.test", TerminalDisposition.Completed)
            ]));

        Assert.Contains("not reachable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateRejectsImplicitTerminalOperation()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            [Operation("operation.load")],
            []));

        Assert.Contains("explicit route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationRequiresOneFixedStationResourceEqualToStationSystemId()
    {
        var exception = Assert.Throws<ArgumentException>(() => OperationDefinition.Create(
            new OperationDefinitionId("operation.invalid-station-resource"),
            "Invalid Station Resource",
            "station.eol",
            "flow.load",
            "configuration.load",
            [new OperationResourceBinding(
                new OperationResourceBindingId("resource.station"),
                OperationResourceKind.Station,
                "station.other",
                OperationResourceResolution.Fixed)],
            []));

        Assert.Contains("exactly one Fixed Station", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsUnboundedForwardCycle()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.a",
            [Operation("operation.a"), Operation("operation.b"), Operation("operation.c")],
            [
                Sequence("a-b", "operation.a", "operation.b"),
                Sequence("b-c", "operation.b", "operation.c"),
                Sequence("c-b", "operation.c", "operation.b")
            ]));

        Assert.Contains("acyclic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundedReworkCanReturnToEarlierOperation()
    {
        var definition = Create(
            "operation.load",
            [Operation("operation.load"), Operation("operation.test"), Operation("operation.finish")],
            [
                Sequence("load-test", "operation.load", "operation.test"),
                Judgement("test-pass", "operation.test", "operation.finish", RouteJudgement.Passed),
                Rework("test-retry", "operation.test", "operation.load", RouteJudgement.Failed, 2),
                TerminalJudgement("test-failed", "operation.test", TerminalDisposition.Nonconforming, RouteJudgement.Failed),
                Terminal("test-default", "operation.test", TerminalDisposition.Held),
                Terminal("finish-completed", "operation.finish", TerminalDisposition.Completed)
            ]);

        var rework = Assert.Single(
            definition.Transitions,
            transition => transition.Kind == RouteTransitionKind.Rework);
        Assert.Equal(2, rework.MaxTraversals);
        Assert.Equal(RouteJudgement.Failed, rework.RequiredJudgement);
    }

    [Fact]
    public void ReworkCannotJumpForward()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            [Operation("operation.load"), Operation("operation.test"), Operation("operation.finish")],
            [
                Sequence("load-test", "operation.load", "operation.test"),
                Rework("test-forward", "operation.test", "operation.finish", RouteJudgement.Failed, 1),
                TerminalJudgement("test-failed", "operation.test", TerminalDisposition.Nonconforming, RouteJudgement.Failed),
                Terminal("test-default", "operation.test", TerminalDisposition.Held),
                Terminal("finish-completed", "operation.finish", TerminalDisposition.Completed)
            ]));

        Assert.Contains("earlier operation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParallelForkJoinRequiresDisjointMatchedBranches()
    {
        var definition = Create(
            "operation.start",
            [
                Operation("operation.start"),
                Operation("operation.left"),
                Operation("operation.right"),
                Operation("operation.join"),
                Operation("operation.finish")
            ],
            [
                Parallel("fork-left", "operation.start", "operation.left", true),
                Parallel("fork-right", "operation.start", "operation.right", true),
                Parallel("join-left", "operation.left", "operation.join", false),
                Parallel("join-right", "operation.right", "operation.join", false),
                Sequence("join-finish", "operation.join", "operation.finish"),
                Terminal("finish-completed", "operation.finish", TerminalDisposition.Completed)
            ]);

        Assert.Equal(4, definition.Transitions.Count(transition =>
            transition.ParallelGroupId == "parallel.inspect"));
    }

    [Fact]
    public void ParallelForkJoinRejectsUnmatchedBranchCount()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.start",
            [
                Operation("operation.start"),
                Operation("operation.left"),
                Operation("operation.right"),
                Operation("operation.join")
            ],
            [
                Parallel("fork-left", "operation.start", "operation.left", true),
                Parallel("fork-right", "operation.start", "operation.right", true),
                Parallel("join-left", "operation.left", "operation.join", false),
                Sequence("right-join", "operation.right", "operation.join"),
                Terminal("join-completed", "operation.join", TerminalDisposition.Completed)
            ]));

        Assert.Contains("same number", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParallelBranchOutputCanFeedOperationAfterAndJoin()
    {
        var definition = Create(
            "operation.start",
            [
                Operation("operation.start"),
                Operation("operation.left"),
                Operation("operation.right"),
                Operation("operation.join"),
                OperationWithInput("operation.finish", "operation.left")
            ],
            [
                Parallel("fork-left", "operation.start", "operation.left", true),
                Parallel("fork-right", "operation.start", "operation.right", true),
                Parallel("join-left", "operation.left", "operation.join", false),
                Parallel("join-right", "operation.right", "operation.join", false),
                Sequence("join-finish", "operation.join", "operation.finish"),
                Terminal("finish-completed", "operation.finish", TerminalDisposition.Completed)
            ]);

        var mapping = Assert.Single(definition.Operations.Single(operation =>
            operation.Id.Value == "operation.finish").InputMappings);
        Assert.Equal("operation.left", mapping.SourceOperationId.Value);
    }

    [Fact]
    public void ParallelSiblingOutputCannotFeedSiblingBeforeJoin()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.start",
            [
                Operation("operation.start"),
                Operation("operation.left"),
                OperationWithInput("operation.right", "operation.left"),
                Operation("operation.join")
            ],
            [
                Parallel("fork-left", "operation.start", "operation.left", true),
                Parallel("fork-right", "operation.start", "operation.right", true),
                Parallel("join-left", "operation.left", "operation.join", false),
                Parallel("join-right", "operation.right", "operation.join", false),
                Terminal("join-completed", "operation.join", TerminalDisposition.Completed)
            ]));

        Assert.Contains("sibling-branch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConditionalBypassCannotFeedMergedOperation()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.inspect",
            [
                Operation("operation.inspect"),
                Operation("operation.measured"),
                Operation("operation.bypass"),
                OperationWithInput("operation.merge", "operation.measured")
            ],
            [
                Condition(
                    "inspect-measured",
                    "operation.inspect",
                    "operation.measured",
                    "inspection.route",
                    ProductionContextValueKind.Text,
                    "measure"),
                Sequence("inspect-bypass", "operation.inspect", "operation.bypass"),
                Sequence("measured-merge", "operation.measured", "operation.merge"),
                Sequence("bypass-merge", "operation.bypass", "operation.merge"),
                Terminal("merge-completed", "operation.merge", TerminalDisposition.Completed)
            ]));

        Assert.Contains("conditional bypass", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JudgementBranchesMustBeUnique()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.inspect",
            [Operation("operation.inspect"), Operation("operation.pass"), Operation("operation.rework")],
            [
                Judgement("inspect-pass", "operation.inspect", "operation.pass", RouteJudgement.Passed),
                Judgement("inspect-also-pass", "operation.inspect", "operation.rework", RouteJudgement.Passed)
            ]));

        Assert.Contains("deterministic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypedOutputConditionsUseOneKeyAndUniqueCanonicalValues()
    {
        var definition = Create(
            "operation.inspect",
            [Operation("operation.inspect"), Operation("operation.pass"), Operation("operation.reject")],
            [
                Condition(
                    "inspect-accepted",
                    "operation.inspect",
                    "operation.pass",
                    "inspection.accepted",
                    ProductionContextValueKind.Boolean,
                    "true"),
                Condition(
                    "inspect-rejected",
                    "operation.inspect",
                    "operation.reject",
                    "inspection.accepted",
                    ProductionContextValueKind.Boolean,
                    "false"),
                Sequence("inspect-default", "operation.inspect", "operation.reject"),
                Terminal("pass-completed", "operation.pass", TerminalDisposition.Completed),
                Terminal("reject-held", "operation.reject", TerminalDisposition.Held)
            ]);

        Assert.All(
            definition.Transitions.Where(transition => transition.Kind == RouteTransitionKind.Condition),
            transition => Assert.Equal(RouteTransitionKind.Condition, transition.Kind));
    }

    [Fact]
    public void TypedOutputConditionsRejectDuplicateExpectedValues()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.inspect",
            [Operation("operation.inspect"), Operation("operation.pass"), Operation("operation.reject")],
            [
                Condition(
                    "inspect-pass",
                    "operation.inspect",
                    "operation.pass",
                    "inspection.code",
                    ProductionContextValueKind.Text,
                    "PASS"),
                Condition(
                    "inspect-duplicate",
                    "operation.inspect",
                    "operation.reject",
                    "inspection.code",
                    ProductionContextValueKind.Text,
                    "PASS")
            ]));

        Assert.Contains("unique typed expected values", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static ProductionLineDefinition Definition(string lineDefinitionId = "line.main")
    {
        return Create(
            "operation.load",
            [
                Operation("operation.load", "flow.load"),
                Operation("operation.test", "flow.test")
            ],
            [
                Sequence("load-test", "operation.load", "operation.test"),
                Terminal("test-completed", "operation.test", TerminalDisposition.Completed)
            ],
            lineDefinitionId);
    }

    internal static OperationDefinition Operation(string id, string? flowId = null)
    {
        var resolvedFlowId = flowId ?? $"flow.{id.Split('.').Last()}";
        return OperationDefinition.Create(
            new OperationDefinitionId(id),
            id,
            "station.eol",
            resolvedFlowId,
            $"configuration.{resolvedFlowId}",
            StationResources(id),
            []);
    }

    private static OperationDefinition OperationWithInput(string id, string sourceOperationId)
    {
        var resolvedFlowId = $"flow.{id.Split('.').Last()}";
        return OperationDefinition.Create(
            new OperationDefinitionId(id),
            id,
            "station.eol",
            resolvedFlowId,
            $"configuration.{resolvedFlowId}",
            StationResources(id),
            [new OperationInputMapping(
                "input.result",
                new OperationDefinitionId(sourceOperationId),
                "output.result",
                ProductionContextValueKind.Text)]);
    }

    private static OperationResourceBinding[] StationResources(string suffix) =>
        [new OperationResourceBinding(
            new OperationResourceBindingId($"resource.station.{suffix}"),
            OperationResourceKind.Station,
            "station.eol",
            OperationResourceResolution.Fixed)];

    internal static RouteTransition Sequence(string id, string source, string target)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            null,
            RouteTransitionKind.Sequence);
    }

    private static ProductionLineDefinition Create(
        string entryOperationId,
        IReadOnlyCollection<OperationDefinition> operations,
        IReadOnlyCollection<RouteTransition> transitions,
        string lineDefinitionId = "line.main",
        ProductionRouteLayout? routeLayout = null)
    {
        return ProductionLineDefinition.Create(
            new ProductionLineDefinitionId(lineDefinitionId),
            "Main Line",
            "topology.main",
            ProductModelDefinition.Create(
                new ProductModelId("product.model-a"),
                "MODEL-A",
                "serialNumber"),
            new OperationDefinitionId(entryOperationId),
            operations,
            transitions,
            [],
            routeLayout ?? new ProductionRouteLayout(operations.Select((operation, index) =>
                new OperationCanvasPosition(operation.Id, 120 + (index * 280), 80))),
            CreatedAtUtc);
    }

    private static RouteTransition Judgement(
        string id,
        string source,
        string target,
        RouteJudgement judgement)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            null,
            RouteTransitionKind.Judgement,
            judgement);
    }

    private static RouteTransition Rework(
        string id,
        string source,
        string target,
        RouteJudgement judgement,
        int maxTraversals)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            null,
            RouteTransitionKind.Rework,
            judgement,
            maxTraversals);
    }

    private static RouteTransition Condition(
        string id,
        string source,
        string target,
        string outputKey,
        ProductionContextValueKind kind,
        string expectedValue)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            null,
            RouteTransitionKind.Condition,
            outputCondition: new RouteOutputCondition(
                outputKey,
                new ProductionContextValue(kind, expectedValue)));
    }

    private static RouteTransition Parallel(
        string id,
        string source,
        string target,
        bool isFork)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            null,
            isFork ? RouteTransitionKind.ParallelFork : RouteTransitionKind.ParallelJoin,
            parallelGroupId: "parallel.inspect");
    }

    private static RouteTransition Terminal(
        string id,
        string source,
        TerminalDisposition disposition) => RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            null,
            disposition,
            RouteTransitionKind.Sequence);

    private static RouteTransition TerminalJudgement(
        string id,
        string source,
        TerminalDisposition disposition,
        RouteJudgement judgement) => RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            null,
            disposition,
            RouteTransitionKind.Judgement,
            judgement);

}
