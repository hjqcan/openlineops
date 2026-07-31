using OpenLineOps.Api.Integrations;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class RecipeAwareProductionOperationReadinessTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BlocksDispatchWhenExactRecipeRevisionIsNotReady()
    {
        var recipeGate = new StubRecipeGate(new RecipeProductionReadinessResult(
            false,
            null,
            null,
            null,
            [new RecipeProductionReadinessBlock(
                "Recipe.Verification.Missing",
                "No exact device readback exists.")]));
        var gate = new RecipeAwareProductionOperationReadiness(
            new StubReadiness(ProductionOperationReadiness.Ready),
            recipeGate,
            new FixedClock(Now));
        var (run, operation) = RuntimeSnapshots(recipeId: "recipe-a");

        var result = await gate.EvaluateAsync(run, operation);

        Assert.Equal(ProductionOperationReadinessKind.Waiting, result.Kind);
        Assert.Contains(
            "Recipe.Verification.Missing",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Equal(
            new RecipeProductionReadinessRequest(
                "station-a",
                "model-a",
                "recipe-a",
                "recipe-v1",
                Now),
            recipeGate.LastRequest);
    }

    [Fact]
    public async Task AddsImmutableRecipeEvidenceWhenReady()
    {
        var assignmentId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var configurationSha256 = new string('a', 64);
        var recipeGate = new StubRecipeGate(new RecipeProductionReadinessResult(
            true,
            assignmentId,
            deploymentId,
            configurationSha256,
            []));
        var baseline = ProductionOperationReadiness.Ready with
        {
            EvidenceKey = "baseline:1"
        };
        var gate = new RecipeAwareProductionOperationReadiness(
            new StubReadiness(baseline),
            recipeGate,
            new FixedClock(Now));
        var (run, operation) = RuntimeSnapshots(recipeId: "recipe-a");

        var result = await gate.EvaluateAsync(run, operation);

        Assert.Equal(ProductionOperationReadinessKind.Ready, result.Kind);
        var evidence = Assert.IsType<string>(result.EvidenceKey);
        Assert.Contains("baseline:1|recipe:recipe-a", evidence);
        Assert.Contains(assignmentId.ToString("D"), evidence);
        Assert.Contains(deploymentId.ToString("D"), evidence);
        Assert.Contains(configurationSha256, evidence);
    }

    [Fact]
    public async Task PreservesEarlierGateAndLegacySnapshotBehavior()
    {
        var recipeGate = new StubRecipeGate(new RecipeProductionReadinessResult(
            false,
            null,
            null,
            null,
            []));
        var waiting = new ProductionOperationReadiness(
            ProductionOperationReadinessKind.Waiting,
            "maintenance blocked",
            [],
            "maintenance:1");
        var earlierGate = new RecipeAwareProductionOperationReadiness(
            new StubReadiness(waiting),
            recipeGate,
            new FixedClock(Now));
        var (run, operation) = RuntimeSnapshots(recipeId: "recipe-a");

        Assert.Equal(waiting, await earlierGate.EvaluateAsync(run, operation));
        Assert.Equal(0, recipeGate.Calls);

        var legacyGate = new RecipeAwareProductionOperationReadiness(
            new StubReadiness(ProductionOperationReadiness.Ready),
            recipeGate,
            new FixedClock(Now));
        var legacy = RuntimeSnapshots(recipeId: null);
        Assert.Equal(
            ProductionOperationReadiness.Ready,
            await legacyGate.EvaluateAsync(legacy.Run, legacy.Operation));
        Assert.Equal(0, recipeGate.Calls);
    }

    private static (ProductionRunSnapshot Run, OperationRunSnapshot Operation)
        RuntimeSnapshots(string? recipeId)
    {
        var definition = new OperationRunDefinition(
            "operation-a",
            "station-a",
            new StationId("station-a"),
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId("process-v1"),
            new ConfigurationSnapshotId("configuration-v1"),
            new RecipeSnapshotId("recipe-v1"),
            recipeId: recipeId);
        var operation = new OperationRunSnapshot(
            definition,
            "operation-run-a",
            Attempt: 1,
            ExecutionStatus.Pending,
            ResultJudgement.Unknown,
            RuntimeSessionId: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureReason: null,
            CompletedStepCount: 0,
            CommandCount: 0,
            IncidentCount: 0,
            RecoveryDecisionId: null,
            ExecutionEvidence: null,
            Outputs: new Dictionary<string, ProductionContextValue>(),
            FencingTokens: new Dictionary<ResourceRequirement, long>(),
            SourceOperationRunBindings: new Dictionary<string, string>());
        var run = new ProductionRunSnapshot(
            ProductionRunId.New(),
            "project-a",
            "application-a",
            "snapshot-a",
            "topology-a",
            "line-a",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("model-a", "serialNumber", "SN-001"),
            LotId: null,
            CarrierId: null,
            "actor-a",
            ExecutionStatus.Pending,
            ResultJudgement.Unknown,
            ProductDisposition.InProcess,
            ProductionRunControlState.Active,
            SafeStopRequestedBy: null,
            SafeStopReason: null,
            SafeStopRequestedAtUtc: null,
            SafeStopAcknowledgedAtUtc: null,
            ScrapRequestedBy: null,
            ScrapReason: null,
            ScrapRequestedAtUtc: null,
            Now,
            Now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureReason: null,
            "operation-a",
            [definition],
            [],
            [operation],
            [],
            new Dictionary<string, int>(),
            []);
        return (run, operation);
    }

    private sealed class StubReadiness(ProductionOperationReadiness result)
        : IProductionOperationReadiness
    {
        public ValueTask<ProductionOperationReadiness> EvaluateAsync(
            ProductionRunSnapshot run,
            OperationRunSnapshot operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubRecipeGate(RecipeProductionReadinessResult result)
        : IRecipeProductionReadinessGate
    {
        public int Calls { get; private set; }

        public RecipeProductionReadinessRequest? LastRequest { get; private set; }

        public ValueTask<RecipeProductionReadinessResult> EvaluateAsync(
            RecipeProductionReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
