using System.Text.Json;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunTerminalEvidenceSnapshotMapper
{
    private const int CurrentSchemaVersion = 1;
    private const string CurrentResourceKind = "OpenLineOps.ProductionRunTerminalEvidence";

    public static PersistedProductionRunTerminalEvidence ToSnapshot(
        ProductionRunTerminalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new PersistedProductionRunTerminalEvidence(
            CurrentSchemaVersion,
            CurrentResourceKind,
            ProductionRunSnapshotMapper.ToSnapshot(
                OpenLineOps.Runtime.Domain.Runs.ProductionRun.Restore(evidence.Run)),
            evidence.MaterialTimeline
                .Select(ProductionMaterialSnapshotMapper.ToSnapshot)
                .ToArray());
    }

    public static ProductionRunTerminalEvidence ToAggregate(
        PersistedProductionRunTerminalEvidence snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(
                snapshot.ResourceKind,
                CurrentResourceKind,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted terminal evidence must be exactly {CurrentResourceKind} "
                + $"schema {CurrentSchemaVersion}.");
        }

        var persistedRun = snapshot.Run
            ?? throw new InvalidDataException("Persisted terminal evidence has no Production Run.");
        var persistedTimeline = snapshot.MaterialTimeline
            ?? throw new InvalidDataException("Persisted terminal evidence has no material manifest.");
        var evidence = new ProductionRunTerminalEvidence(
            ProductionRunSnapshotMapper.ToAggregate(persistedRun).ToSnapshot(),
            persistedTimeline.Select(ProductionMaterialSnapshotMapper.ToAggregate).ToArray());
        if (!JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(ToSnapshot(evidence)),
                JsonSerializer.SerializeToElement(snapshot)))
        {
            throw new InvalidDataException(
                "Persisted terminal evidence is not in canonical immutable form.");
        }

        return evidence;
    }
}

internal sealed record PersistedProductionRunTerminalEvidence(
    int SchemaVersion,
    string? ResourceKind,
    PersistedProductionRun? Run,
    PersistedProductionMaterialTimelineEntry[]? MaterialTimeline);
