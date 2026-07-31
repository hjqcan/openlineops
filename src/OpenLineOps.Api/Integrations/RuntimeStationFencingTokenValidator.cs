using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Api.RuntimeIntegration;
using OpenLineOps.Recipes.Application.Fencing;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Api.Integrations;

public sealed class RuntimeStationFencingTokenValidator(
    IResourceLeaseRepository resourceLeases)
    : IStationFencingTokenValidator
{
    public async ValueTask<bool> IsCurrentAsync(
        string stationId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);

        var matches = (await resourceLeases
                .ListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(lease =>
                lease.Resource.Kind == ResourceKind.Station
                && string.Equals(
                    lease.Resource.ResourceId,
                    stationId,
                    StringComparison.Ordinal)
                && lease.FencingToken == fencingToken)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        var lease = matches[0];
        var validation = await resourceLeases.ValidateCurrentAsync(
                lease.ProductionRunId,
                lease.OperationRunId,
                [ResourceLeaseFenceEvidence.FromLease(lease)],
                cancellationToken)
            .ConfigureAwait(false);
        return validation.Accepted;
    }
}

public sealed class RecipeAwareProductionOperationReadiness(
    IProductionOperationReadiness inner,
    IRecipeProductionReadinessGate recipeReadiness,
    IClock clock) : IProductionOperationReadiness
{
    public async ValueTask<ProductionOperationReadiness> EvaluateAsync(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(operation);
        var baseline = await inner.EvaluateAsync(
                run,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
        if (baseline.Kind != ProductionOperationReadinessKind.Ready)
        {
            return baseline;
        }

        var recipeId = operation.Definition.RecipeId;
        if (recipeId is null)
        {
            // Direct in-process callers created before recipe identities were split
            // remain readable. Immutable project releases cannot enter this path
            // without a frozen RecipeId.
            return baseline;
        }

        var decision = await recipeReadiness.EvaluateAsync(
                new RecipeProductionReadinessRequest(
                    operation.Definition.StationSystemId,
                    run.ProductionUnitIdentity.ModelId,
                    recipeId,
                    operation.Definition.RecipeSnapshotId.Value,
                    clock.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        var evidence = AppendEvidence(baseline.EvidenceKey, decision, recipeId);
        if (decision.Allowed)
        {
            return baseline with { EvidenceKey = evidence };
        }

        var blocks = decision.Blocks
            .OrderBy(static block => block.Code, StringComparer.Ordinal)
            .ThenBy(static block => block.Detail, StringComparer.Ordinal)
            .Select(static block =>
                $"code={Uri.EscapeDataString(block.Code)},"
                + $"detail={Uri.EscapeDataString(block.Detail)}");
        return new ProductionOperationReadiness(
            ProductionOperationReadinessKind.Waiting,
            "Recipe start blocked: " + string.Join(" | ", blocks),
            baseline.MaterialResources,
            evidence);
    }

    private static string AppendEvidence(
        string? baselineEvidence,
        RecipeProductionReadinessResult decision,
        string recipeId)
    {
        var recipeEvidence =
            "recipe:"
            + Uri.EscapeDataString(recipeId)
            + ":assignment="
            + decision.AssignmentId?.ToString("D")
            + ":deployment="
            + decision.DeploymentId?.ToString("D")
            + ":sha256="
            + (decision.ConfigurationSha256 ?? "none");
        return string.IsNullOrEmpty(baselineEvidence)
            ? recipeEvidence
            : $"{baselineEvidence}|{recipeEvidence}";
    }
}

public sealed class RecipeStationStartAuthorityAdapter(
    IStationRecipeAuthorityResolver recipeAuthority)
    : IStationRecipeStartAuthority
{
    public async ValueTask<StationRecipeStartAuthorityDecision> ResolveAsync(
        StationId stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        var resolved = await recipeAuthority.ResolveAsync(
                stationId.Value,
                evaluatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.Allowed || resolved.Authority is null)
        {
            var blocks = resolved.Blocks
                .OrderBy(static block => block.Code, StringComparer.Ordinal)
                .ThenBy(static block => block.Detail, StringComparer.Ordinal)
                .ToArray();
            var code = blocks.Length == 1
                ? blocks[0].Code
                : "Runtime.StationRecipeAuthorityRejected";
            var reason = blocks.Length == 0
                ? "Recipe authority resolution returned no immutable authority."
                : string.Join(
                    " | ",
                    blocks.Select(static block =>
                        $"{block.Code}: {block.Detail}"));
            return StationRecipeStartAuthorityDecision.Reject(code, reason);
        }

        var authority = resolved.Authority;
        return StationRecipeStartAuthorityDecision.Allow(
            new OpenLineOps.Runtime.Application.Stations.StationRecipeStartAuthority(
                authority.RecipeId,
                authority.VersionId,
                authority.AssignmentId,
                authority.DeploymentId,
                authority.ConfigurationSha256));
    }
}

public static class RecipeRuntimeIntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsRecipeRuntimeIntegration(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Scoped<
            IStationFencingTokenValidator,
            RuntimeStationFencingTokenValidator>());
        services.Replace(ServiceDescriptor.Scoped<
            IStationRecipeStartAuthority,
            RecipeStationStartAuthorityAdapter>());
        services.RemoveAll<RecipeAwareProductionOperationReadiness>();
        services.RemoveAll<IProductionOperationReadiness>();
        services.AddScoped(static serviceProvider =>
            new RecipeAwareProductionOperationReadiness(
                serviceProvider.GetRequiredService<
                    MaintenanceAwareProductionOperationReadiness>(),
                serviceProvider.GetRequiredService<
                    IRecipeProductionReadinessGate>(),
                serviceProvider.GetRequiredService<IClock>()));
        services.AddScoped<IProductionOperationReadiness>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<
                    RecipeAwareProductionOperationReadiness>());
        return services;
    }
}
