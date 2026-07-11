using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionMaterialTimelineEntryTests
{
    private static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    private static readonly ProductionRunId RunId = ProductionRunId.New();

    private static readonly MaterialReference Material =
        MaterialReference.ForProductionUnit(ProductionUnitId.New());

    private static readonly SlotAddress Slot =
        new("line.main", "station.main", "slot.main");

    [Fact]
    public void RunBoundCompletionRequiresOperationRunIdAndPositiveSlotFence()
    {
        Assert.Throws<ArgumentException>(() => Completion(null, null));
        Assert.Throws<ArgumentException>(() => Completion("operation.main@0001", null));
        Assert.Throws<ArgumentException>(() => Completion(null, 41));
        Assert.Throws<ArgumentOutOfRangeException>(() => Completion("operation.main@0001", 0));
    }

    [Fact]
    public void ExactCompletionIdentityIsRejectedForWrongShape()
    {
        Assert.Throws<ArgumentException>(() => ProductionMaterialTimelineEntry.SlotOccupancy(
            Guid.NewGuid(),
            Slot,
            Material,
            RunId,
            "operation.main@0001",
            41,
            SlotOccupancyStatus.Occupied,
            SlotOccupancyStatus.Running,
            "coordinator.main",
            OccurredAtUtc));
        Assert.Throws<ArgumentException>(() => ProductionMaterialTimelineEntry.SlotOccupancy(
            Guid.NewGuid(),
            Slot,
            Material,
            null,
            "operation.main@0001",
            41,
            SlotOccupancyStatus.Running,
            SlotOccupancyStatus.Occupied,
            "coordinator.main",
            OccurredAtUtc));
    }

    [Fact]
    public void ExactCompletionIdentityIsRetained()
    {
        var evidence = Completion("operation.main@0001", 41);

        Assert.Equal(RunId, evidence.ProductionRunId);
        Assert.Equal("operation.main@0001", evidence.OperationRunId);
        Assert.Equal(41, evidence.SlotFencingToken);
        Assert.Equal(SlotOccupancyStatus.Running, evidence.PreviousSlotStatus);
        Assert.Equal(SlotOccupancyStatus.Occupied, evidence.CurrentSlotStatus);
    }

    [Fact]
    public void CompletionMatchingRejectsWrongOperationRunOrFence()
    {
        var evidence = Completion("operation.main@0001", 41);

        Assert.True(ProductionRunSlotLifecycle.IsCompletionEvidence(
            evidence,
            RunId,
            new CompletedSlotOperation(
                Slot,
                "operation.main@0001",
                41,
                OccurredAtUtc)));
        Assert.False(ProductionRunSlotLifecycle.IsCompletionEvidence(
            evidence,
            RunId,
            new CompletedSlotOperation(
                Slot,
                "operation.main@0002",
                41,
                OccurredAtUtc)));
        Assert.False(ProductionRunSlotLifecycle.IsCompletionEvidence(
            evidence,
            RunId,
            new CompletedSlotOperation(
                Slot,
                "operation.main@0001",
                42,
                OccurredAtUtc)));
    }

    private static ProductionMaterialTimelineEntry Completion(
        string? operationRunId,
        long? slotFencingToken) => ProductionMaterialTimelineEntry.SlotOccupancy(
        Guid.NewGuid(),
        Slot,
        Material,
        RunId,
        operationRunId,
        slotFencingToken,
        SlotOccupancyStatus.Running,
        SlotOccupancyStatus.Occupied,
        "coordinator.main",
        OccurredAtUtc);
}
