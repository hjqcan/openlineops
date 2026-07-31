using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Maintenance.Api.RuntimeIntegration;

public sealed class MaintenanceAwareProductionOperationReadiness(
    IProductionOperationReadiness inner,
    IEquipmentMaintenanceService maintenanceService,
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

        var maintenance = await maintenanceService.EvaluateProductionStartAsync(
                operation.Definition.StationSystemId,
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (maintenance.IsFailure)
        {
            return new ProductionOperationReadiness(
                ProductionOperationReadinessKind.Waiting,
                $"Maintenance readiness unavailable: {maintenance.Error.Code}.",
                baseline.MaterialResources,
                baseline.EvidenceKey);
        }

        var evidence = AppendEvidence(
            baseline.EvidenceKey,
            maintenance.Value);
        if (maintenance.Value.Allowed)
        {
            return baseline with { EvidenceKey = evidence };
        }

        return new ProductionOperationReadiness(
            ProductionOperationReadinessKind.Waiting,
            FormatBlocks(maintenance.Value),
            baseline.MaterialResources,
            evidence);
    }

    private static string FormatBlocks(
        StationProductionStartDecision decision)
    {
        var blocks = decision.Blocks
            .OrderBy(static block => block.AssetId, StringComparer.Ordinal)
            .ThenBy(static block => block.Reason)
            .ThenBy(static block => block.SubjectId, StringComparer.Ordinal)
            .Select(
                static block =>
                    $"asset={Uri.EscapeDataString(block.AssetId)},"
                    + $"reason={block.Reason},"
                    + $"subject={Uri.EscapeDataString(block.SubjectId)}");
        return "Maintenance start blocked: " + string.Join(" | ", blocks);
    }

    private static string AppendEvidence(
        string? baselineEvidence,
        StationProductionStartDecision decision)
    {
        var maintenanceEvidence = decision.AssetRevisions
            .OrderBy(static revision => revision.AssetId, StringComparer.Ordinal)
            .Select(
                revision =>
                    "maintenance:"
                    + Uri.EscapeDataString(decision.StationId)
                    + ":"
                    + Uri.EscapeDataString(revision.AssetId)
                    + ":"
                    + revision.Revision.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        if (maintenanceEvidence.Length == 0)
        {
            maintenanceEvidence =
            [
                "maintenance:"
                + Uri.EscapeDataString(decision.StationId)
                + ":none"
            ];
        }

        return string.Join(
            "|",
            string.IsNullOrEmpty(baselineEvidence)
                ? maintenanceEvidence
                : [baselineEvidence, .. maintenanceEvidence]);
    }
}

public static class MaintenanceRuntimeGateServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsMaintenanceRuntimeGate(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<ProductionOperationReadinessEvaluator>();
        services.RemoveAll<MaintenanceAwareProductionOperationReadiness>();
        services.RemoveAll<IProductionOperationReadiness>();
        services.AddScoped<ProductionOperationReadinessEvaluator>();
        services.AddScoped(
            static serviceProvider =>
                new MaintenanceAwareProductionOperationReadiness(
                    serviceProvider.GetRequiredService<
                        ProductionOperationReadinessEvaluator>(),
                    serviceProvider.GetRequiredService<
                        IEquipmentMaintenanceService>(),
                    serviceProvider.GetRequiredService<IClock>()));
        services.AddScoped<IProductionOperationReadiness>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<
                    MaintenanceAwareProductionOperationReadiness>());
        return services;
    }
}
