using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
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
        Assert.Collection(
            run.RouteDecisions,
            decision => Assert.Equal("test", decision.TargetOperationId),
            decision => Assert.Equal(ProductDisposition.Completed, decision.TerminalDisposition));
    }

    [Fact]
    public void ExplicitTerminalDispositionIsIndependentFromResultJudgement()
    {
        var run = CreateRun(
            [Operation("inspect")],
            [TerminalTransition("inspect-held", "inspect", ProductDisposition.Held)]);

        StartAndComplete(run, "inspect@0001", ResultJudgement.Passed, Now.AddMinutes(1));

        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, run.Judgement);
        Assert.Equal(ProductDisposition.Held, run.Disposition);
        var decision = Assert.Single(run.RouteDecisions);
        Assert.Null(decision.TargetOperationId);
        Assert.Equal(ProductDisposition.Held, decision.TerminalDisposition);
    }

    [Fact]
    public void MissingExplicitRouteFailsClosedWithoutInferringDisposition()
    {
        var operation = Operation("inspect");
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-ROUTE-FAIL"),
            null,
            null,
            "operator-001",
            operation.OperationId,
            Now,
            [operation],
            []);
        Assert.True(run.Start(Now).Succeeded);

        StartAndComplete(run, "inspect@0001", ResultJudgement.Passed, Now.AddMinutes(1));

        Assert.Equal(ExecutionStatus.Failed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, run.Judgement);
        Assert.Equal(ProductDisposition.Held, run.Disposition);
        Assert.Equal("Runtime.RouteResolutionFailed", run.FailureCode);
        Assert.Empty(run.RouteDecisions);
    }

    [Fact]
    public void TransitionTargetRequiresExactlyOneOperationOrTerminalDisposition()
    {
        Assert.Throws<ArgumentException>(() => new RouteTransitionDefinition(
            "invalid.none",
            "inspect",
            null,
            RuntimeRouteTransitionKind.Sequence));
        Assert.Throws<ArgumentException>(() => new RouteTransitionDefinition(
            "invalid.both",
            "inspect",
            "next",
            RuntimeRouteTransitionKind.Sequence,
            terminalDisposition: ProductDisposition.Completed));
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
                    maxTraversals: 1),
                TerminalTransition(
                    "test-failed-terminal",
                    "test",
                    ProductDisposition.Nonconforming,
                    RuntimeRouteTransitionKind.Judgement,
                    ResultJudgement.Failed),
                TerminalTransition(
                    "test-default-terminal",
                    "test",
                    ProductDisposition.Held)
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
    public void AutomaticReworkPassesOnReplacementWaveWithoutErasingFailedEvidence()
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
                    maxTraversals: 1),
                TerminalTransition(
                    "test-failed-terminal",
                    "test",
                    ProductDisposition.Nonconforming,
                    RuntimeRouteTransitionKind.Judgement,
                    ResultJudgement.Failed),
                TerminalTransition(
                    "test-completed-terminal",
                    "test",
                    ProductDisposition.Completed)
            ]);

        StartAndComplete(run, "assemble@0001", ResultJudgement.Passed, Now.AddMinutes(1));
        StartAndComplete(run, "test@0001", ResultJudgement.Failed, Now.AddMinutes(2));
        StartAndComplete(run, "assemble@0002", ResultJudgement.Passed, Now.AddMinutes(3));
        StartAndComplete(run, "test@0002", ResultJudgement.Passed, Now.AddMinutes(4));

        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, run.Judgement);
        Assert.Equal(ProductDisposition.Completed, run.Disposition);
        var failedEvidence = run.Operations.Single(operation =>
            operation.OperationRunId == "test@0001");
        Assert.Equal(ExecutionStatus.Completed, failedEvidence.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, failedEvidence.Judgement);
        Assert.NotNull(failedEvidence.ExecutionEvidence);
        var replacement = run.Operations.Single(operation =>
            operation.OperationRunId == "test@0002");
        Assert.Equal("assemble@0002", replacement.SourceOperationRunBindings["assemble"]);
        Assert.Equal(1, run.ToSnapshot().TransitionTraversals["test-rework"]);
    }

    [Fact]
    public void ManualUpstreamReworkCancelsPendingDescendantsAndFreezesReplacementBinding()
    {
        var run = CreateRun(
            [Operation("assemble"), Operation("test")],
            [Transition("assemble-test", "assemble", "test", RuntimeRouteTransitionKind.Sequence)]);

        StartAndComplete(run, "assemble@0001", ResultJudgement.Passed, Now.AddMinutes(1));
        var firstTestAttempt = run.Operations.Single(operation =>
            operation.OperationRunId == "test@0001");
        Assert.Equal("assemble@0001", firstTestAttempt.SourceOperationRunBindings["assemble"]);
        Assert.True(run.Hold("Quality requested upstream rework.", Now.AddMinutes(2)).Succeeded);

        Assert.True(run.Rework("assemble", Now.AddMinutes(3)).Succeeded);

        Assert.Equal(ExecutionStatus.Canceled, firstTestAttempt.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, firstTestAttempt.Judgement);
        Assert.Equal("Runtime.OperationSupersededByRework", firstTestAttempt.FailureCode);
        Assert.Equal(
            "Pending Operation attempt was superseded by an upstream Rework wave.",
            firstTestAttempt.FailureReason);
        var cancellationEvent = Assert.Single(run.DomainEvents.OfType<OperationRunStatusChangedDomainEvent>(),
            domainEvent => domainEvent.OperationRunId == "test@0001"
                && domainEvent.ToStatus == ExecutionStatus.Canceled);
        Assert.Equal(ExecutionStatus.Pending, cancellationEvent.FromStatus);

        var replacementAssemble = run.Operations.Single(operation =>
            operation.OperationRunId == "assemble@0002");
        Assert.Empty(replacementAssemble.SourceOperationRunBindings);
        StartAndComplete(run, replacementAssemble.OperationRunId, ResultJudgement.Passed, Now.AddMinutes(4));

        var pendingTests = run.Operations.Where(operation =>
            operation.OperationId == "test"
            && operation.ExecutionStatus == ExecutionStatus.Pending).ToArray();
        var secondTestAttempt = Assert.Single(pendingTests);
        Assert.Equal("test@0002", secondTestAttempt.OperationRunId);
        Assert.Equal("assemble@0002", secondTestAttempt.SourceOperationRunBindings["assemble"]);
        Assert.Equal(2, run.Operations.Count(operation => operation.OperationId == "test"));
    }

    [Fact]
    public void ManualReworkRejectsTimestampBeforeLatestRunTransitionWithoutMutation()
    {
        var run = CreateRun(
            [Operation("assemble"), Operation("test")],
            [Transition("assemble-test", "assemble", "test", RuntimeRouteTransitionKind.Sequence)]);
        StartAndComplete(run, "assemble@0001", ResultJudgement.Passed, Now.AddMinutes(1));
        var heldAtUtc = Now.AddMinutes(2);
        Assert.True(run.Hold("Quality requested upstream rework.", heldAtUtc).Succeeded);

        var result = run.Rework("assemble", heldAtUtc.AddTicks(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.ProductionRunReworkTimestampInvalid", result.Code);
        Assert.Equal(heldAtUtc, run.LastTransitionAtUtc);
        Assert.Equal(2, run.Operations.Count);
        Assert.Equal(
            ExecutionStatus.Pending,
            run.Operations.Single(operation => operation.OperationRunId == "test@0001")
                .ExecutionStatus);
        Assert.DoesNotContain(
            run.Operations,
            operation => operation.OperationRunId == "assemble@0002");
        Assert.Equal(ProductionRunControlState.Held, run.ControlState);
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
    public void ParallelJoinReusesUnaffectedBranchOnlyWithinTheExactForkWave()
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
        StartAndComplete(run, "left@0001", ResultJudgement.Passed, Now.AddMinutes(2));
        StartAndComplete(run, "right@0001", ResultJudgement.Passed, Now.AddMinutes(3));
        var firstJoin = run.Operations.Single(operation => operation.OperationRunId == "join@0001");
        Assert.Equal("left@0001", firstJoin.SourceOperationRunBindings["left"]);
        Assert.Equal("right@0001", firstJoin.SourceOperationRunBindings["right"]);

        Assert.True(run.Hold("Repeat only the left branch.", Now.AddMinutes(4)).Succeeded);
        Assert.True(run.Rework("left", Now.AddMinutes(5)).Succeeded);
        Assert.Equal(ExecutionStatus.Canceled, firstJoin.ExecutionStatus);
        var replacementLeft = run.Operations.Single(operation =>
            operation.OperationRunId == "left@0002");
        Assert.Equal("entry@0001", replacementLeft.SourceOperationRunBindings["entry"]);

        StartAndComplete(run, replacementLeft.OperationRunId, ResultJudgement.Passed, Now.AddMinutes(6));

        var secondJoin = run.Operations.Single(operation => operation.OperationRunId == "join@0002");
        Assert.Equal("entry@0001", secondJoin.SourceOperationRunBindings["entry"]);
        Assert.Equal("left@0002", secondJoin.SourceOperationRunBindings["left"]);
        Assert.Equal("right@0001", secondJoin.SourceOperationRunBindings["right"]);
        Assert.Single(run.Operations, operation => operation.OperationId == "right");
        Assert.Equal(2, run.Operations.Count(operation => operation.OperationId == "join"));
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
                        new ProductionContextValue(ProductionContextValueKind.Text, "B"))),
                TerminalTransition(
                    "inspect-default-terminal",
                    "inspect",
                    ProductDisposition.Held)
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
            Now.AddSeconds(2),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "inspect@0001",
                ExecutionStatus.Completed,
                ResultJudgement.NotApplicable,
                Now.AddSeconds(2),
                1,
                1)).Succeeded);

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
            Now.AddSeconds(2),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "test@0001",
                ExecutionStatus.Failed,
                ResultJudgement.Unknown,
                Now.AddSeconds(2),
                commandCount: 1,
                incidentCount: 1,
                failureCode: "Vendor.ProtocolInvalid",
                failureReason: "Vendor returned malformed JSON."));

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
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ProductionRecoveryDecisionKind.Retry,
            "operator-001",
            "Station state was inspected before retry.",
            "inspection:retry-001",
            Now.AddSeconds(3),
            operationId: "test");
        Assert.True(run.RetryRecovery(decision).Succeeded);

        Assert.Equal(ProductionRunControlState.Active, run.ControlState);
        Assert.Equal(ExecutionStatus.Canceled, run.Operations[0].ExecutionStatus);
        Assert.Equal("test@0002", run.Operations[1].OperationRunId);
        Assert.Equal(ExecutionStatus.Pending, run.Operations[1].ExecutionStatus);
        Assert.Equal(decision, Assert.Single(run.RecoveryDecisions));
    }

    [Fact]
    public void ReconcileCompletesInterruptedOperationFromObservedEvidenceWithoutReplay()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));
        Assert.True(run.MarkRecoveryRequired("Agent disconnected after hardware actuation.", Now.AddSeconds(2)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ProductionRecoveryDecisionKind.Reconcile,
            "operator-001",
            "Physical inspection confirms the operation completed.",
            "inspection:camera-report-42",
            Now.AddSeconds(3),
            operationRunId: "test@0001",
            observedJudgement: ResultJudgement.Passed,
            observedOutputs: new Dictionary<string, ProductionContextValue>
            {
                ["inspectionResult"] = new(ProductionContextValueKind.Text, "confirmed")
            });

        Assert.True(run.ReconcileRecovery(decision).Succeeded);

        Assert.Equal(ExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, run.Judgement);
        Assert.Equal("confirmed", run.Operations[0].Outputs["inspectionResult"].CanonicalValue);
        Assert.Equal(decision, Assert.Single(run.RecoveryDecisions));
    }

    [Fact]
    public void RestoreRejectsDanglingOrForgedOperationRecoveryDecisionReference()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));
        Assert.True(run.MarkRecoveryRequired("Agent disconnected.", Now.AddSeconds(2)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("abababab-abab-abab-abab-abababababab"),
            ProductionRecoveryDecisionKind.Reconcile,
            "operator-001",
            "Physical inspection confirms completion.",
            "inspection:recovery-reference",
            Now.AddSeconds(3),
            operationRunId: "test@0001",
            observedJudgement: ResultJudgement.Passed);
        Assert.True(run.ReconcileRecovery(decision).Succeeded);
        var snapshot = run.ToSnapshot();
        var operation = Assert.Single(snapshot.Operations);

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(
            ReplaceSingleOperation(snapshot, operation with
            {
                RecoveryDecisionId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd")
            })));
        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(
            ReplaceSingleOperation(snapshot, operation with { RecoveryDecisionId = null })));
    }

    [Fact]
    public void RecoveryDecisionIdIsIdempotentAndRejectsDifferentEvidence()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));
        Assert.True(run.MarkRecoveryRequired("Host terminated.", Now.AddSeconds(2)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ProductionRecoveryDecisionKind.Abort,
            "operator-001",
            "Product state cannot be established.",
            "incident:recovery-88",
            Now.AddSeconds(3));

        Assert.True(run.AbortRecovery(decision).Succeeded);
        Assert.True(run.AbortRecovery(decision).Succeeded);
        var mismatch = new ProductionRecoveryDecision(
            decision.DecisionId,
            decision.Kind,
            decision.ActorId,
            "Different immutable reason.",
            decision.EvidenceReference,
            decision.DecidedAtUtc);

        var result = run.AbortRecovery(mismatch);
        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.RecoveryDecisionIdentityMismatch", result.Code);
        Assert.Single(run.RecoveryDecisions);
    }

    [Fact]
    public void CancelOperationRequiresRunningOperationChronologicalUtcAndNonnegativeMetrics()
    {
        var run = CreateRun([Operation("test")], []);
        var pending = run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Operator canceled.",
            0,
            0,
            0,
            Now.AddSeconds(1),
            null!);
        Assert.False(pending.Succeeded);
        Assert.Equal("Runtime.OperationCancelRejected", pending.Code);
        StartOperation(run, "test@0001", Now.AddSeconds(2));

        var invalidMetrics = run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Operator canceled.",
            0,
            -1,
            0,
            Now.AddSeconds(3),
            null!);
        Assert.False(invalidMetrics.Succeeded);
        Assert.Equal("Runtime.OperationCancelMetricsInvalid", invalidMetrics.Code);
        var invalidTimestamp = run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Operator canceled.",
            0,
            1,
            0,
            Now.AddSeconds(1),
            null!);
        Assert.False(invalidTimestamp.Succeeded);
        Assert.Equal("Runtime.OperationCancelTimestampInvalid", invalidTimestamp.Code);
        Assert.Throws<ArgumentException>(() => run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Operator canceled.",
            0,
            1,
            0,
            Now.AddSeconds(3).ToOffset(TimeSpan.FromHours(8)),
            null!));

        var canceled = run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Operator canceled.",
            0,
            1,
            0,
            Now.AddSeconds(3),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "test@0001",
                ExecutionStatus.Canceled,
                ResultJudgement.Aborted,
                Now.AddSeconds(3),
                commandCount: 1,
                failureCode: "Runtime.OperationCanceled",
                failureReason: "Operator canceled."));

        Assert.True(canceled.Succeeded);
        Assert.Equal(ExecutionStatus.Canceled, run.ExecutionStatus);
        Assert.Equal("Runtime.ProductionRunCanceled", run.FailureCode);
        var operation = Assert.Single(run.Operations);
        Assert.Equal(ExecutionStatus.Canceled, operation.ExecutionStatus);
        Assert.Equal("Runtime.OperationCanceled", operation.FailureCode);
        Assert.Equal(1, operation.CommandCount);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("cancel")]
    public void ImmediateResultApisRejectParallelDispatchBeforeMutatingEitherOperation(
        string resultKind)
    {
        var run = CreateRun(
            [Operation("entry"), Operation("left"), Operation("right"), Operation("join")],
            [
                Transition("fork-left", "entry", "left", RuntimeRouteTransitionKind.ParallelFork, group: "work"),
                Transition("fork-right", "entry", "right", RuntimeRouteTransitionKind.ParallelFork, group: "work"),
                Transition("join-left", "left", "join", RuntimeRouteTransitionKind.ParallelJoin, group: "work"),
                Transition("join-right", "right", "join", RuntimeRouteTransitionKind.ParallelJoin, group: "work"),
                TerminalTransition("join-terminal", "join", ProductDisposition.Completed)
            ]);
        StartAndComplete(run, "entry@0001", ResultJudgement.NotApplicable, Now.AddSeconds(1));
        StartOperation(run, "left@0001", Now.AddSeconds(3));
        StartOperation(run, "right@0001", Now.AddSeconds(3));
        var lastTransitionBeforeResult = run.LastTransitionAtUtc;
        var routeDecisionCountBeforeResult = run.RouteDecisions.Count;
        var leftTerminalStatus = resultKind switch
        {
            "complete" => ExecutionStatus.Completed,
            "fail" => ExecutionStatus.Failed,
            "cancel" => ExecutionStatus.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
        };
        var leftJudgement = resultKind == "complete"
            ? ResultJudgement.Passed
            : resultKind == "cancel"
                ? ResultJudgement.Aborted
                : ResultJudgement.Unknown;
        var leftEvidence = ProductionRunExecutionEvidenceTestFactory.Create(
            run,
            "left@0001",
            leftTerminalStatus,
            leftJudgement,
            Now.AddSeconds(4),
            completedStepCount: 1,
            commandCount: 2,
            incidentCount: 1,
            failureCode: resultKind == "complete" ? null : "Runtime.LeftTerminal",
            failureReason: resultKind == "complete" ? null : "Left branch terminated.");

        var immediate = resultKind switch
        {
            "complete" => run.CompleteOperation(
                "left@0001",
                ResultJudgement.Passed,
                outputs: null,
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                completedAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            "fail" => run.FailOperation(
                "left@0001",
                ExecutionStatus.Failed,
                "Runtime.LeftTerminal",
                "Left branch terminated.",
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                failedAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            "cancel" => run.CancelOperation(
                "left@0001",
                "Runtime.LeftTerminal",
                "Left branch terminated.",
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                canceledAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
        };

        Assert.False(immediate.Succeeded);
        Assert.Equal("Runtime.DispatchWaveRecordingRequired", immediate.Code);
        Assert.Equal(lastTransitionBeforeResult, run.LastTransitionAtUtc);
        Assert.Equal(routeDecisionCountBeforeResult, run.RouteDecisions.Count);
        Assert.Equal(ExecutionStatus.Running, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.Active, run.ControlState);
        Assert.All(
            run.Operations.Where(operation => operation.OperationId is "left" or "right"),
            operation =>
            {
                Assert.Equal(ExecutionStatus.Running, operation.ExecutionStatus);
                Assert.Null(operation.CompletedAtUtc);
                Assert.Null(operation.ExecutionEvidence);
                Assert.Equal((0, 0, 0),
                    (operation.CompletedStepCount, operation.CommandCount, operation.IncidentCount));
            });

        var recordedLeft = resultKind switch
        {
            "complete" => run.RecordOperationCompletion(
                "left@0001",
                ResultJudgement.Passed,
                outputs: null,
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                completedAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            "fail" => run.RecordOperationFailure(
                "left@0001",
                ExecutionStatus.Failed,
                "Runtime.LeftTerminal",
                "Left branch terminated.",
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                failedAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            "cancel" => run.RecordOperationCancellation(
                "left@0001",
                "Runtime.LeftTerminal",
                "Left branch terminated.",
                completedStepCount: 1,
                commandCount: 2,
                incidentCount: 1,
                canceledAtUtc: Now.AddSeconds(4),
                executionEvidence: leftEvidence),
            _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
        };
        var rightEvidence = ProductionRunExecutionEvidenceTestFactory.Create(
            run,
            "right@0001",
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            Now.AddSeconds(5),
            completedStepCount: 2,
            commandCount: 3);
        var lastTransitionAfterLeft = run.LastTransitionAtUtc;
        var immediateRight = run.CompleteOperation(
            "right@0001",
            ResultJudgement.Passed,
            outputs: null,
            completedStepCount: 2,
            commandCount: 3,
            incidentCount: 0,
            completedAtUtc: Now.AddSeconds(5),
            executionEvidence: rightEvidence);

        Assert.False(immediateRight.Succeeded);
        Assert.Equal("Runtime.DispatchWaveRecordingRequired", immediateRight.Code);
        Assert.Equal(lastTransitionAfterLeft, run.LastTransitionAtUtc);
        Assert.Same(
            leftEvidence,
            run.Operations.Single(operation =>
                operation.OperationRunId == "left@0001").ExecutionEvidence);
        var stillRunningRight = run.Operations.Single(operation =>
            operation.OperationRunId == "right@0001");
        Assert.Equal(ExecutionStatus.Running, stillRunningRight.ExecutionStatus);
        Assert.Null(stillRunningRight.CompletedAtUtc);
        Assert.Null(stillRunningRight.ExecutionEvidence);

        var recordedRight = run.RecordOperationCompletion(
            "right@0001",
            ResultJudgement.Passed,
            outputs: null,
            completedStepCount: 2,
            commandCount: 3,
            incidentCount: 0,
            completedAtUtc: Now.AddSeconds(5),
            executionEvidence: rightEvidence);

        Assert.True(recordedLeft.Succeeded);
        Assert.True(recordedRight.Succeeded);
        Assert.True(run.ResolveDispatchWave(["left@0001", "right@0001"]).Succeeded);
        var left = run.Operations.Single(operation => operation.OperationRunId == "left@0001");
        var right = run.Operations.Single(operation => operation.OperationRunId == "right@0001");
        Assert.Same(leftEvidence, left.ExecutionEvidence);
        Assert.Same(rightEvidence, right.ExecutionEvidence);
        Assert.Equal((1, 2, 1), (left.CompletedStepCount, left.CommandCount, left.IncidentCount));
        Assert.Equal((2, 3, 0), (right.CompletedStepCount, right.CommandCount, right.IncidentCount));
    }

    [Fact]
    public void ParallelRunningOperationsPreserveEachCanceledExecutionMetricsBeforeRunTerminates()
    {
        var run = CreateRun(
            [Operation("entry"), Operation("left"), Operation("right")],
            [
                Transition("fork-left", "entry", "left", RuntimeRouteTransitionKind.ParallelFork, group: "work"),
                Transition("fork-right", "entry", "right", RuntimeRouteTransitionKind.ParallelFork, group: "work")
            ]);
        StartAndComplete(run, "entry@0001", ResultJudgement.NotApplicable, Now.AddSeconds(1));
        StartOperation(run, "left@0001", Now.AddSeconds(3));
        StartOperation(run, "right@0001", Now.AddSeconds(3));

        Assert.True(run.RecordOperationCancellation(
            "left@0001",
            "Runtime.LeftCanceled",
            "Operator canceled both branches.",
            0,
            2,
            0,
            Now.AddSeconds(4),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "left@0001",
                ExecutionStatus.Canceled,
                ResultJudgement.Aborted,
                Now.AddSeconds(4),
                commandCount: 2,
                failureCode: "Runtime.LeftCanceled",
                failureReason: "Operator canceled both branches.")).Succeeded);
        Assert.Equal(ExecutionStatus.Running, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.Active, run.ControlState);
        Assert.True(run.RecordOperationCancellation(
            "right@0001",
            "Runtime.RightCanceled",
            "Operator canceled both branches.",
            1,
            3,
            1,
            Now.AddSeconds(5),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "right@0001",
                ExecutionStatus.Canceled,
                ResultJudgement.Aborted,
                Now.AddSeconds(5),
                completedStepCount: 1,
                commandCount: 3,
                incidentCount: 1,
                failureCode: "Runtime.RightCanceled",
                failureReason: "Operator canceled both branches.")).Succeeded);
        Assert.True(run.ResolveDispatchWave(["left@0001", "right@0001"]).Succeeded);

        Assert.Equal(ExecutionStatus.Canceled, run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, run.Judgement);
        Assert.Equal("Runtime.ProductionRunCanceled", run.FailureCode);
        var left = run.Operations.Single(operation => operation.OperationRunId == "left@0001");
        var right = run.Operations.Single(operation => operation.OperationRunId == "right@0001");
        Assert.Equal((0, 2, 0), (left.CompletedStepCount, left.CommandCount, left.IncidentCount));
        Assert.Equal((1, 3, 1), (right.CompletedStepCount, right.CommandCount, right.IncidentCount));
        Assert.Equal("Runtime.LeftCanceled", left.FailureCode);
        Assert.Equal("Runtime.RightCanceled", right.FailureCode);
    }

    [Fact]
    public void SafeStopBarrierCannotClaimSuccessBeforeIndependentAcknowledgement()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));

        Assert.True(run.RequestSafeStop(
            "operator.safety",
            "Guard opened.",
            Now.AddSeconds(2)).Succeeded);
        Assert.Equal(ProductionRunControlState.StopRequested, run.ControlState);
        Assert.Null(run.SafeStopAcknowledgedAtUtc);
        Assert.True(run.CompleteOperation(
            "test@0001",
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            Now.AddSeconds(3),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "test@0001",
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                Now.AddSeconds(3),
                1,
                1)).Succeeded);

        Assert.Equal(ExecutionStatus.Running, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.StopRequested, run.ControlState);
        Assert.True(run.AcknowledgeSafeStop(Now.AddSeconds(4)).Succeeded);
        Assert.Equal(ExecutionStatus.Canceled, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.SafeStopped, run.ControlState);
        Assert.Equal("Runtime.ProductionRunSafeStopped", run.FailureCode);
    }

    [Fact]
    public void SafeStopAcknowledgementThenExecutionCancellationPreservesSafetyEvidence()
    {
        var run = CreateRun([Operation("test")], []);
        StartOperation(run, "test@0001", Now.AddSeconds(1));
        Assert.True(run.RequestSafeStop(
            "operator.safety",
            "Guard opened.",
            Now.AddSeconds(2)).Succeeded);
        Assert.True(run.AcknowledgeSafeStop(Now.AddSeconds(3)).Succeeded);

        Assert.True(run.CancelOperation(
            "test@0001",
            "Runtime.OperationCanceled",
            "Station runtime process tree terminated.",
            0,
            1,
            0,
            Now.AddSeconds(4),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                "test@0001",
                ExecutionStatus.Canceled,
                ResultJudgement.Aborted,
                Now.AddSeconds(4),
                commandCount: 1,
                failureCode: "Runtime.OperationCanceled",
                failureReason: "Station runtime process tree terminated.")).Succeeded);

        Assert.Equal(ExecutionStatus.Canceled, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.SafeStopped, run.ControlState);
        Assert.Equal("operator.safety", run.SafeStopRequestedBy);
        Assert.Equal("Guard opened.", run.SafeStopReason);
        Assert.Equal(Now.AddSeconds(2), run.SafeStopRequestedAtUtc);
        Assert.Equal(Now.AddSeconds(3), run.SafeStopAcknowledgedAtUtc);
        Assert.Equal("Runtime.ProductionRunSafeStopped", run.FailureCode);
    }

    [Fact]
    public void SafeStopBeforeHardwareDispatchIsAConfirmedNoOp()
    {
        var run = CreateRun([Operation("test")], []);

        Assert.True(run.RequestSafeStop(
            "operator.safety",
            "Stop before dispatch.",
            Now.AddSeconds(1)).Succeeded);

        Assert.Equal(ExecutionStatus.Canceled, run.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.SafeStopped, run.ControlState);
        Assert.Equal(run.SafeStopRequestedAtUtc, run.SafeStopAcknowledgedAtUtc);
        Assert.Equal(ExecutionStatus.Canceled, Assert.Single(run.Operations).ExecutionStatus);
    }

    [Fact]
    public void StopBeforeExecutionProducesStrictlyRestorableCancellationEvidence()
    {
        var run = CreateRun([Operation("test")], []);

        Assert.True(run.RequestStop("Operator requested stop.", Now.AddSeconds(1)).Succeeded);

        AssertPreExecutionCancellationRoundTrips(run);
    }

    [Fact]
    public void CancelBeforeExecutionProducesStrictlyRestorableCancellationEvidence()
    {
        var run = CreateRun([Operation("test")], []);

        Assert.True(run.Cancel("Operator canceled the run.", Now.AddSeconds(1)).Succeeded);

        AssertPreExecutionCancellationRoundTrips(run);
    }

    [Fact]
    public void ScrapBeforeExecutionProducesStrictlyRestorableCancellationEvidence()
    {
        var run = CreateRun([Operation("test")], []);

        Assert.True(run.RequestScrap(
            "operator.scrap",
            "Product was physically damaged.",
            Now.AddSeconds(1)).Succeeded);

        AssertPreExecutionCancellationRoundTrips(run);
        Assert.Equal("operator.scrap", run.ScrapRequestedBy);
        Assert.Equal("Product was physically damaged.", run.ScrapReason);
        Assert.Equal(Now.AddSeconds(1), run.ScrapRequestedAtUtc);
    }

    [Fact]
    public void RestoreRejectsUnknownPreExecutionCancellationCode()
    {
        var snapshot = PreExecutionCancellationSnapshot();

        var invalid = ReplaceSingleOperation(
            snapshot,
            Assert.Single(snapshot.Operations) with
            {
                FailureCode = "Runtime.UnrecognizedCancellation"
            });

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(invalid));
    }

    [Fact]
    public void RestoreRejectsDefaultPreExecutionCancellationTimestamp()
    {
        var snapshot = PreExecutionCancellationSnapshot();

        var invalid = ReplaceSingleOperation(
            snapshot,
            Assert.Single(snapshot.Operations) with
            {
                CompletedAtUtc = default(DateTimeOffset)
            });

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(invalid));
    }

    [Fact]
    public void RestoreRejectsNonUtcPreExecutionCancellationTimestamp()
    {
        var snapshot = PreExecutionCancellationSnapshot();

        var invalid = ReplaceSingleOperation(
            snapshot,
            Assert.Single(snapshot.Operations) with
            {
                CompletedAtUtc = Now.AddSeconds(1).ToOffset(TimeSpan.FromHours(8))
            });

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(invalid));
    }

    [Fact]
    public void RestoreRejectsPreExecutionCancellationBeforeRunCreation()
    {
        var snapshot = PreExecutionCancellationSnapshot();

        var invalid = ReplaceSingleOperation(
            snapshot,
            Assert.Single(snapshot.Operations) with
            {
                CompletedAtUtc = snapshot.CreatedAtUtc.AddTicks(-1)
            });

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(invalid));
    }

    [Fact]
    public void RestoreRejectsExecutionEvidenceForOperationCanceledBeforeExecution()
    {
        var snapshot = PreExecutionCancellationSnapshot();
        var operation = Assert.Single(snapshot.Operations);

        var invalid = ReplaceSingleOperation(
            snapshot,
            operation with
            {
                ExecutionEvidence = ExecutionEvidence(snapshot, operation)
            });

        Assert.Throws<InvalidOperationException>(() => ProductionRun.Restore(invalid));
    }

    private static ProductionRun CreateRun(
        IReadOnlyCollection<OperationRunDefinition> operations,
        IReadOnlyCollection<RouteTransitionDefinition> transitions)
    {
        var first = operations.First();
        var explicitTransitions = transitions.ToList();
        foreach (var operation in operations.Where(operation => !explicitTransitions.Any(transition =>
                     transition.Kind != RuntimeRouteTransitionKind.Rework
                     && string.Equals(
                         transition.SourceOperationId,
                         operation.OperationId,
                         StringComparison.Ordinal))))
        {
            explicitTransitions.Add(TerminalTransition(
                $"{operation.OperationId}.completed",
                operation.OperationId,
                ProductDisposition.Completed));
        }

        var run = ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            "lot-001",
            "carrier-001",
            "operator-001",
            first.OperationId,
            Now,
            operations,
            explicitTransitions);
        Assert.True(run.Start(Now).Succeeded);
        return run;
    }

    private static ProductionRunSnapshot PreExecutionCancellationSnapshot()
    {
        var run = CreateRun([Operation("test")], []);
        Assert.True(run.Cancel("Operator canceled before dispatch.", Now.AddSeconds(1)).Succeeded);
        return run.ToSnapshot();
    }

    private static ProductionRunSnapshot ReplaceSingleOperation(
        ProductionRunSnapshot snapshot,
        OperationRunSnapshot operation) => snapshot with
        {
            Operations = [operation]
        };

    private static OperationExecutionEvidence ExecutionEvidence(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation) => new(
        OperationExecutionEvidenceOrigin.Coordinator,
        Guid.NewGuid(),
        run.RunId.Value,
        run.ProductionUnitId.Value,
        run.ProductionLineDefinitionId,
        operation.Definition.OperationId,
        operation.OperationRunId,
        operation.Attempt,
        operation.Definition.StationSystemId,
        operation.Definition.StationId.Value,
        operation.Definition.ProcessDefinitionId.Value,
        operation.Definition.ProcessVersionId.Value,
        operation.Definition.ConfigurationSnapshotId.Value,
        operation.Definition.RecipeSnapshotId.Value,
        run.ProductionUnitIdentity.ModelId,
        run.ProductionUnitIdentity.InputKey,
        run.ProductionUnitIdentity.Value,
        run.LotId,
        run.CarrierId,
        null,
        null,
        run.ActorId,
        run.ProjectId,
        run.ApplicationId,
        run.ProjectSnapshotId,
        run.TopologyId,
        "Canceled",
        operation.CompletedAtUtc!.Value,
        [],
        [],
        [],
        [],
        []);

    private static void AssertPreExecutionCancellationRoundTrips(ProductionRun run)
    {
        var restored = ProductionRun.Restore(run.ToSnapshot());
        var operation = Assert.Single(restored.Operations);
        Assert.Equal(ExecutionStatus.Canceled, operation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, operation.Judgement);
        Assert.Equal("Runtime.ProductionRunStopped", operation.FailureCode);
        Assert.Null(operation.RuntimeSessionId);
        Assert.Null(operation.StartedAtUtc);
        Assert.NotNull(operation.CompletedAtUtc);
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
            atUtc.AddSeconds(1),
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                operationRunId,
                ExecutionStatus.Completed,
                judgement,
                atUtc.AddSeconds(1),
                1,
                1)).Succeeded);
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

    private static RouteTransitionDefinition TerminalTransition(
        string id,
        string source,
        ProductDisposition disposition,
        RuntimeRouteTransitionKind kind = RuntimeRouteTransitionKind.Sequence,
        ResultJudgement? judgement = null) => new(
        id,
        source,
        null,
        kind,
        requiredJudgement: judgement,
        terminalDisposition: disposition);
}
