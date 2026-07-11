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
        var transition = Assert.Single(definition.Transitions);
        Assert.Equal(RouteTransitionKind.Sequence, transition.Kind);
        Assert.Equal("operation.test", transition.TargetOperationId.Value);
        Assert.Equal("station.eol", definition.Operations.Last().StationSystemId);
        Assert.Equal("configuration.flow.test", definition.Operations.Last().ConfigurationSnapshotId);
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
            configurationSnapshotId!));
    }

    [Fact]
    public void CreateRejectsUnreachableOperation()
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(
            "operation.load",
            [Operation("operation.load"), Operation("operation.test")],
            []));

        Assert.Contains("not reachable", exception.Message, StringComparison.OrdinalIgnoreCase);
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
                Rework("test-retry", "operation.test", "operation.load", RouteJudgement.Failed, 2)
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
                Rework("test-forward", "operation.test", "operation.finish", RouteJudgement.Failed, 1)
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
                Sequence("join-finish", "operation.join", "operation.finish")
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
                Sequence("right-join", "operation.right", "operation.join")
            ]));

        Assert.Contains("same number", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("judgements must be unique", exception.Message, StringComparison.OrdinalIgnoreCase);
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
                    "false")
            ]);

        Assert.All(
            definition.Transitions,
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

    [Theory]
    [InlineData(" provider.test")]
    [InlineData("provider.test ")]
    [InlineData("")]
    public void ExternalAdapterRejectsNonCanonicalProviderKey(string providerKey)
    {
        Assert.Throws<ArgumentException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.test"),
            "Vendor Test",
            "test.external",
            "run",
            null,
            providerKey,
            [],
            ValidInputs(),
            ValidResults(),
            ValidOutcome(),
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ExternalAdapterRejectsHostAbsoluteExecutableAndMissingProductMappings()
    {
        Assert.Throws<ArgumentException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.absolute"),
            "Absolute",
            "test.execute",
            "Execute",
            "C:/tools/test.exe",
            null,
            [],
            ValidInputs(),
            ValidResults(),
            ValidOutcome(),
            TimeSpan.FromSeconds(30)));

        var exception = Assert.Throws<ArgumentException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.no-product"),
            "No Product",
            "test.execute",
            "Execute",
            null,
            "provider.test",
            [],
            [new ExternalTestProgramInputMapping("$product.identity", "serial")],
            ValidResults(),
            ValidOutcome(),
            TimeSpan.FromSeconds(30)));
        Assert.Contains("product identity and product model", exception.Message, StringComparison.Ordinal);
    }

    internal static ProductionLineDefinition Definition(string lineDefinitionId = "line.main")
    {
        return Create(
            "operation.load",
            [
                Operation("operation.load", "flow.load"),
                Operation("operation.test", "flow.test")
            ],
            [Sequence("load-test", "operation.load", "operation.test")],
            lineDefinitionId,
            [Adapter()]);
    }

    internal static OperationDefinition Operation(string id, string? flowId = null)
    {
        return OperationDefinition.Create(
            new OperationDefinitionId(id),
            id,
            "station.eol",
            flowId ?? $"flow.{id.Split('.').Last()}",
            $"configuration.{flowId ?? $"flow.{id.Split('.').Last()}"}");
    }

    internal static RouteTransition Sequence(string id, string source, string target)
    {
        return RouteTransition.Create(
            new RouteTransitionId(id),
            new OperationDefinitionId(source),
            new OperationDefinitionId(target),
            RouteTransitionKind.Sequence);
    }

    internal static ExternalTestProgramAdapter Adapter()
    {
        return ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.test"),
            "Vendor Test",
            "test.external",
            "ExecuteTestProgram",
            null,
            "provider.test",
            ["--serial", "{{product.identity}}"],
            ValidInputs(),
            ValidResults(),
            ValidOutcome(),
            TimeSpan.FromSeconds(30));
    }

    private static ProductionLineDefinition Create(
        string entryOperationId,
        IReadOnlyCollection<OperationDefinition> operations,
        IReadOnlyCollection<RouteTransition> transitions,
        string lineDefinitionId = "line.main",
        IReadOnlyCollection<ExternalTestProgramAdapter>? adapters = null)
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
            adapters ?? [],
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
            isFork ? RouteTransitionKind.ParallelFork : RouteTransitionKind.ParallelJoin,
            parallelGroupId: "parallel.inspect");
    }

    private static ExternalTestProgramInputMapping[] ValidInputs() =>
    [
        new ExternalTestProgramInputMapping("$product.identity", "serial"),
        new ExternalTestProgramInputMapping("$product.model", "model")
    ];

    private static ExternalTestProgramResultMapping[] ValidResults() =>
        [new ExternalTestProgramResultMapping("$.outcome", "test.outcome")];

    private static ExternalTestProgramOutcomeMapping ValidOutcome() =>
        new("$.outcome", "Passed", "Failed", "Aborted");
}
