using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SequenceCompletesWithIndependentExecutionJudgementAndDisposition()
    {
        var run = CreateRun(
            [Operation("assemble"), Operation("test")],
            [Transition("assemble-test", "assemble", "test", RuntimeRouteTransitionKind.Sequence)]);

        StartAndComplete(run, "assemble@0001", ResultJudgement.NotApplicable, Now.AddMinutes(1));
        StartAndComplete(run, "test@0001", ResultJudgement.Passed, Now.AddMinutes(2));

        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, run.Judgement);
        Assert.Equal(ProductDisposition.Completed, run.Disposition);
        Assert.Equal(2, run.Operations.Count);
        Assert.Single(run.RouteDecisions);
    }

    [Fact]
    public void ProductFailureIsCompletedExecutionAndTraversesBoundedRework()
    {
        var run = CreateRun(
            [Operation("assemble"), Operation("test")],
            [
                Transition("assemble-test", "assemble", "test", RuntimeRouteTransitionKind.Sequence),
                Transition(
                    "test-rework",
                    "test",
                    "assemble",
                    RuntimeRouteTransitionKind.Rework,
                    ResultJudgement.Failed,
                    maxTraversals: 1)
            ]);

        StartAndComplete(run, "assemble@0001", ResultJudgement.Passed, Now.AddMinutes(1));
        StartAndComplete(run, "test@0001", ResultJudgement.Failed, Now.AddMinutes(2));
        StartAndComplete(run, "assemble@0002", ResultJudgement.Passed, Now.AddMinutes(3));
        StartAndComplete(run, "test@0002", ResultJudgement.Failed, Now.AddMinutes(4));

        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, run.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, run.Disposition);
        Assert.Equal(1, run.ToSnapshot().TransitionTraversals["test-rework"]);
    }

    [Fact]
    public void ParallelJoinActivatesOnlyAfterEveryBranchCompletes()
    {
        var run = CreateRun(
            [Operation("entry"), Operation("left"), Operation("right"), Operation("join")],
            [
                Transition("fork-left", "entry", "left", RuntimeRouteTransitionKind.ParallelFork, group: "work"),
                Transition("fork-right", "entry", "right", RuntimeRouteTransitionKind.ParallelFork, group: "work"),
                Transition("left-join", "left", "join", RuntimeRouteTransitionKind.ParallelJoin, group: "work"),
                Transition("right-join", "right", "join", RuntimeRouteTransitionKind.ParallelJoin, group: "work")
            ]);

        StartAndComplete(run, "entry@0001", ResultJudgement.NotApplicable, Now.AddMinutes(1));
        Assert.Equal(2, run.Operations.Count(operation =>
            operation.ExecutionStatus == ExecutionStatus.Pending));

        StartAndComplete(run, "left@0001", ResultJudgement.Passed, Now.AddMinutes(2));
        Assert.DoesNotContain(run.Operations, operation => operation.OperationId == "join");

        StartAndComplete(run, "right@0001", ResultJudgement.Passed, Now.AddMinutes(3));
        Assert.Contains(run.Operations, operation => operation.OperationRunId == "join@0001");
        StartAndComplete(run, "join@0001", ResultJudgement.Passed, Now.AddMinutes(4));
        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
    }

    [Fact]
    public void TypedOperationOutputSelectsExactConditionRoute()
    {
        var run = CreateRun(
            [Operation("inspect"), Operation("accept"), Operation("reject")],
            [
                new RouteTransitionDefinition(
                    "inspect-accept",
                    "inspect",
                    "accept",
                    RuntimeRouteTransitionKind.Condition,
                    outputCondition: new RouteOutputCondition(
                        "grade",
                        new ProductionContextValue(ProductionContextValueKind.Text, "A"))),
                new RouteTransitionDefinition(
                    "inspect-reject",
                    "inspect",
                    "reject",
                    RuntimeRouteTransitionKind.Condition,
                    outputCondition: new RouteOutputCondition(
                        "grade",
                        new ProductionContextValue(ProductionContextValueKind.Text, "B")))
            ]);
        StartOperation(run, "inspect@0001", Now.AddSeconds(1));

        Assert.True(run.CompleteOperation(
            "inspect@0001",
            ResultJudgement.NotApplicable,
            new Dictionary<string, ProductionContextValue>
            {
                ["grade"] = new(ProductionContextValueKind.Text, "B")
            },
            1,
            1,
            0,
            Now.AddSeconds(2)).Succeeded);

        Assert.Contains(run.Operations, operation => operation.OperationRunId == "reject@0001");
        Assert.DoesNotContain(run.Operations, operation => operation.OperationId == "accept");
    }

    [Fact]
    public void SystemFailureDoesNotClaimProductFailure()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));

        var result = run.FailOperation(
            "test@0001",
            ExecutionStatus.Failed,
            "Vendor.ProtocolInvalid",
            "Vendor returned malformed JSON.",
            0,
            1,
            1,
            Now.AddSeconds(2));

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionStatus.Failed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, run.Judgement);
        Assert.Equal(ProductDisposition.Held, run.Disposition);
    }

    [Fact]
    public void InterruptedOperationRequiresExplicitRetryAndCreatesNewAttempt()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));

        Assert.True(run.MarkRecoveryRequired("Host terminated.", Now.AddSeconds(2)).Succeeded);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, run.ControlState);
        Assert.True(run.RetryRecovery("test", "Operator reconciled station.", Now.AddSeconds(3)).Succeeded);

        Assert.Equal(ProductionRunControlState.Active, run.ControlState);
        Assert.Equal(ExecutionStatus.Canceled, run.Operations[0].ExecutionStatus);
        Assert.Equal("test@0002", run.Operations[1].OperationRunId);
        Assert.Equal(ExecutionStatus.Pending, run.Operations[1].ExecutionStatus);
    }

    private static ProductionRun CreateRun(
        IReadOnlyCollection<OperationRunDefinition> operations,
        IReadOnlyCollection<RouteTransitionDefinition> transitions)
    {
        var first = operations.First();
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            "lot-001",
            "carrier-001",
            "operator-001",
            first.OperationId,
            Now,
            operations,
            transitions);
        Assert.True(run.Start(Now).Succeeded);
        return run;
    }

    private static void StartAndComplete(
        ProductionRun run,
        string operationRunId,
        ResultJudgement judgement,
        DateTimeOffset atUtc)
    {
        StartOperation(run, operationRunId, atUtc);
        Assert.True(run.CompleteOperation(
            operationRunId,
            judgement,
            null,
            1,
            1,
            0,
            atUtc.AddSeconds(1)).Succeeded);
    }

    private static void StartOperation(
        ProductionRun run,
        string operationRunId,
        DateTimeOffset atUtc)
    {
        var operation = run.Operations.Single(candidate => candidate.OperationRunId == operationRunId);
        var leases = operation.ResourceRequirements.Select((resource, index) => new ResourceLease(
            resource,
            run.Id,
            operationRunId,
            index + 1,
            atUtc,
            atUtc.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operationRunId,
            RuntimeSessionId.New(),
            leases,
            atUtc).Succeeded);
    }

    private static OperationRunDefinition Operation(string id) => new(
        id,
        $"station.{id}",
        new StationId($"station.{id}"),
        new ProcessDefinitionId($"process.{id}"),
        new ProcessVersionId($"process-version.{id}"),
        new ConfigurationSnapshotId($"configuration.{id}"),
        new RecipeSnapshotId($"recipe.{id}"));

    private static RouteTransitionDefinition Transition(
        string id,
        string source,
        string target,
        RuntimeRouteTransitionKind kind,
        ResultJudgement? judgement = null,
        int? maxTraversals = null,
        string? group = null) => new(
        id,
        source,
        target,
        kind,
        judgement,
        maxTraversals,
        group);
}
