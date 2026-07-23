using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly OpenLineOpsApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductionRunsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task RawProductionRunSubmissionIsRejectedWithoutLaunching()
    {
        var launcher = new RejectIfCalledProductionRunLauncher();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProjectReleaseProductionRunLauncher>();
            services.AddSingleton<IProjectReleaseProductionRunLauncher>(launcher);
        }));
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/production-runs", new
        {
            projectId = "project.raw-plan",
            projectSnapshotId = "snapshot.raw-plan",
            productionRunId = Guid.NewGuid().ToString("D"),
            productionUnitId = Guid.NewGuid().ToString("D"),
            operations = new[] { new { resourceId = "client.injected" } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, launcher.InvocationCount);
    }

    [Fact]
    public async Task NestedProjectSnapshotProductionRunRouteDoesNotExist()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/automation-projects/project.removed/snapshots/snapshot.removed/production-runs",
            new
            {
                projectId = "project.removed",
                projectSnapshotId = "snapshot.removed",
                productionRunId = Guid.NewGuid().ToString("D"),
                productionUnitId = Guid.NewGuid().ToString("D")
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByIdReturnsOperationDualAxesAndRejectsNonCanonicalIdentity()
    {
        var runId = ProductionRunId.New();
        var createdAtUtc = new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);
        var operationPlan = OperationPlan();
        var run = ProductionRun.Create(
            runId,
            "project.production-run-api",
            "application.production-run-api",
            "snapshot.production-run-api",
            "topology.production-run-api",
            "line.production-run-api",
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-API-001"),
            "lot.production-run-api",
            "carrier.production-run-api",
            "operator.production-run-api",
            operationPlan.Definition.OperationId,
            createdAtUtc,
            [operationPlan.Definition],
            TerminalRoutes(operationPlan.Definition.OperationId));
        var repository = _factory.Services.GetRequiredService<IProductionRunRepository>();
        var materials = _factory.Services.GetRequiredService<
            OpenLineOps.Runtime.Application.Materials.IProductionMaterialRepository>();
        var unit = OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            new OpenLineOps.Runtime.Domain.ProductionUnits.ProductionLotId(run.LotId!),
            run.ActorId,
            createdAtUtc.AddTicks(-1));
        Assert.True(await materials.TryAddAsync(unit));
        var unitEntry = Assert.IsType<
            OpenLineOps.Runtime.Application.Materials.ProductionMaterialPersistenceEntry<
                OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnit>>(
            await materials.GetProductionUnitAsync(unit.Id));
        Assert.True(await repository.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(runId, [operationPlan]),
            new ProductionRunAdmission(unitEntry.Aggregate.ToSnapshot(), unitEntry.Revision)));
        Assert.True(run.Start(createdAtUtc.AddSeconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements.Select(resource => new ResourceLease(
            resource,
            run.Id,
            operation.OperationRunId,
            7,
            createdAtUtc,
            createdAtUtc.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            createdAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            null,
            0,
            0,
            0,
            createdAtUtc.AddSeconds(3),
            CreateExecutionEvidence(run, operation, createdAtUtc.AddSeconds(3))).Succeeded);
        await repository.SaveAsync(run, 0);

        using var response = await _client.GetAsync($"/api/production-runs/{runId.Value:D}");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        Assert.Equal("Completed", root.GetProperty("executionStatus").GetString());
        Assert.Equal("Failed", root.GetProperty("judgement").GetString());
        Assert.Equal("Nonconforming", root.GetProperty("disposition").GetString());
        Assert.Equal("SN-API-001", root.GetProperty("productionUnitIdentity")
            .GetProperty("value").GetString());
        var operationResponse = Assert.Single(root.GetProperty("operations").EnumerateArray());
        Assert.Equal("operation.main", operationResponse.GetProperty("operationId").GetString());
        Assert.Equal("Completed", operationResponse.GetProperty("executionStatus").GetString());
        Assert.Equal("Failed", operationResponse.GetProperty("judgement").GetString());
        Assert.Equal(7, Assert.Single(operationResponse.GetProperty("resources").EnumerateArray())
            .GetProperty("fencingToken").GetInt64());

        using var nonCanonical = await _client.GetAsync(
            $"/api/production-runs/{runId.Value.ToString("D").ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.BadRequest, nonCanonical.StatusCode);
    }

    [Fact]
    public async Task ReconcileCommandRequiresStrictEvidenceAndIsDurablyIdempotent()
    {
        var runId = ProductionRunId.New();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var operationPlan = OperationPlan();
        var run = ProductionRun.Create(
            runId,
            "project.recovery-api",
            "application.recovery-api",
            "snapshot.recovery-api",
            "topology.recovery-api",
            "line.recovery-api",
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-RECOVERY-001"),
            null,
            null,
            "operator.recovery",
            operationPlan.Definition.OperationId,
            now,
            [operationPlan.Definition],
            TerminalRoutes(operationPlan.Definition.OperationId));
        var repository = _factory.Services.GetRequiredService<IProductionRunRepository>();
        var materials = _factory.Services.GetRequiredService<
            OpenLineOps.Runtime.Application.Materials.IProductionMaterialRepository>();
        var unit = OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            now.AddTicks(-1));
        Assert.True(await materials.TryAddAsync(unit));
        var unitEntry = Assert.IsType<
            OpenLineOps.Runtime.Application.Materials.ProductionMaterialPersistenceEntry<
                OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnit>>(
            await materials.GetProductionUnitAsync(unit.Id));
        Assert.True(await repository.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(runId, [operationPlan]),
            new ProductionRunAdmission(unitEntry.Aggregate.ToSnapshot(), unitEntry.Revision)));
        Assert.True(run.Start(now.AddSeconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            operation.ResourceRequirements.Select(resource => new ResourceLease(
                resource,
                run.Id,
                operation.OperationRunId,
                17,
                now,
                now.AddHours(1))).ToArray(),
            now.AddSeconds(2)).Succeeded);
        Assert.True(run.MarkRecoveryRequired(
            "Agent disconnected after actuation.",
            now.AddSeconds(3)).Succeeded);
        await repository.SaveAsync(run, 0);

        var decisionId = "a5555555-5555-5555-5555-555555555555";
        var request = new
        {
            reason = "Inspection confirms completion.",
            operationId = (string?)null,
            recoveryDecision = new
            {
                decisionId,
                evidenceReference = "inspection:recovery-api-001",
                decidedAtUtc = now.AddSeconds(4).ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                operationRunId = operation.OperationRunId,
                operationId = (string?)null,
                observedJudgement = "Passed",
                observedOutputs = new Dictionary<string, object>
                {
                    ["inspection"] = new { kind = "Text", canonicalValue = "confirmed" }
                }
            }
        };
        using var response = await _client.PostAsJsonAsync(
            $"/api/production-runs/{runId.Value:D}/commands/Reconcile",
            request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Completed", document.RootElement.GetProperty("executionStatus").GetString());
        var recovery = Assert.Single(document.RootElement.GetProperty("recoveryDecisions").EnumerateArray());
        Assert.Equal(decisionId, recovery.GetProperty("decisionId").GetGuid().ToString("D"));
        Assert.Equal("Reconcile", recovery.GetProperty("kind").GetString());
        Assert.Equal(
            ApiTestAuthentication.StandardActorId,
            recovery.GetProperty("actorId").GetString());

        using var duplicate = await _client.PostAsJsonAsync(
            $"/api/production-runs/{runId.Value:D}/commands/Reconcile",
            request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        using var mismatch = await _client.PostAsJsonAsync(
            $"/api/production-runs/{runId.Value:D}/commands/Reconcile",
            new
            {
                reason = "Different immutable evidence.",
                request.operationId,
                recoveryDecision = new
                {
                    request.recoveryDecision.decisionId,
                    request.recoveryDecision.evidenceReference,
                    request.recoveryDecision.decidedAtUtc,
                    request.recoveryDecision.operationRunId,
                    request.recoveryDecision.operationId,
                    request.recoveryDecision.observedJudgement,
                    request.recoveryDecision.observedOutputs
                }
            });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

        using var uppercase = await _client.PostAsJsonAsync(
            $"/api/production-runs/{runId.Value:D}/commands/Reconcile",
            new
            {
                request.reason,
                request.operationId,
                recoveryDecision = new
                {
                    decisionId = request.recoveryDecision.decisionId.ToUpperInvariant(),
                    request.recoveryDecision.evidenceReference,
                    request.recoveryDecision.decidedAtUtc,
                    request.recoveryDecision.operationRunId,
                    request.recoveryDecision.operationId,
                    request.recoveryDecision.observedJudgement,
                    request.recoveryDecision.observedOutputs
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, uppercase.StatusCode);
    }

    private static OperationExecutionPlan OperationPlan() => new(
        "operation.main",
        "station.main",
        new StationId("station.main"),
        new ConfigurationSnapshotId("configuration.main"),
        new RecipeSnapshotId("recipe.main"),
        new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process-version.main"),
            []),
        []);

    private static RouteTransitionDefinition[] TerminalRoutes(string operationId) =>
    [
        new RouteTransitionDefinition(
            $"{operationId}.failed",
            operationId,
            null,
            RuntimeRouteTransitionKind.Judgement,
            ResultJudgement.Failed,
            terminalDisposition: ProductDisposition.Nonconforming),
        new RouteTransitionDefinition(
            $"{operationId}.default",
            operationId,
            null,
            RuntimeRouteTransitionKind.Sequence,
            terminalDisposition: ProductDisposition.Completed)
    ];

    private static OperationExecutionEvidence CreateExecutionEvidence(
        ProductionRun run,
        OperationRun operation,
        DateTimeOffset completedAtUtc) => new(
            OperationExecutionEvidenceOrigin.RuntimeSession,
            operation.RuntimeSessionId!.Value.Value,
            run.Id.Value,
            run.ProductionUnitId.Value,
            run.ProductionLineDefinitionId,
            operation.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.StationSystemId,
            operation.StationId.Value,
            operation.ProcessDefinitionId.Value,
            operation.ProcessVersionId.Value,
            operation.ConfigurationSnapshotId.Value,
            operation.RecipeSnapshotId.Value,
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
            "Completed",
            completedAtUtc,
            operation.FencingTokens.Select(pair => new OperationResourceFenceEvidence(
                pair.Key.Kind.ToString(),
                pair.Key.ResourceId,
                pair.Value,
                completedAtUtc.AddMinutes(5))).ToArray(),
            [],
            [],
            [],
            []);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    private sealed class RejectIfCalledProductionRunLauncher : IProjectReleaseProductionRunLauncher
    {
        public int InvocationCount { get; private set; }

        public ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
            PublishedProjectSnapshotDetails snapshot,
            SubmitProjectReleaseProductionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            throw new InvalidOperationException("Invalid API input reached the production launcher.");
        }
    }
}
