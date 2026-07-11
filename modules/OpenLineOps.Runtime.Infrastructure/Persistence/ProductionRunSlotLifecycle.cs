using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunSlotLifecycle
{
    public static IReadOnlyCollection<CompletedSlotOperation> ResolveCompletedSlots(
        ProductionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var latestBySlot = new Dictionary<SlotAddress, SlotOperation>();
        foreach (var operation in run.Operations.Where(static operation =>
                     operation.StartedAtUtc is not null))
        {
            foreach (var (resource, fencingToken) in operation.FencingTokens.Where(static pair =>
                         pair.Key.Kind == ResourceKind.Slot))
            {
                var address = ParseAddress(resource.ResourceId);
                var candidate = new SlotOperation(
                    operation.OperationRunId,
                    fencingToken,
                    operation.IsTerminal,
                    operation.CompletedAtUtc);
                if (!latestBySlot.TryGetValue(address, out var current)
                    || candidate.FencingToken > current.FencingToken)
                {
                    latestBySlot[address] = candidate;
                }
                else if (candidate.FencingToken == current.FencingToken
                         && !string.Equals(
                             candidate.OperationRunId,
                             current.OperationRunId,
                             StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Slot {address} fencing token {fencingToken} is claimed by multiple Operation Runs.");
                }
            }
        }

        return latestBySlot
            .Where(static pair => pair.Value.IsTerminal)
            .Select(pair => new CompletedSlotOperation(
                pair.Key,
                pair.Value.CompletedAtUtc
                ?? throw new InvalidDataException(
                    $"Terminal Operation Run {pair.Value.OperationRunId} has no completion timestamp.")))
            .ToArray();
    }

    public static bool IsCompletionEvidence(
        ProductionMaterialTimelineEntry evidence,
        ProductionRunId runId,
        CompletedSlotOperation completedSlot)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(completedSlot);
        return evidence.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition
            && evidence.ProductionRunId == runId
            && evidence.Slot == completedSlot.Address
            && evidence.PreviousSlotStatus == SlotOccupancyStatus.Running
            && evidence.CurrentSlotStatus == SlotOccupancyStatus.Occupied
            && evidence.OccurredAtUtc == completedSlot.CompletedAtUtc;
    }

    private static SlotAddress ParseAddress(string value)
    {
        var segments = value.Split('/', StringSplitOptions.None);
        return segments.Length == 3
               && segments.All(static segment => !string.IsNullOrWhiteSpace(segment))
            ? new SlotAddress(segments[0], segments[1], segments[2])
            : throw new InvalidDataException(
                $"Slot resource '{value}' is not a canonical Line/Station/Slot address.");
    }

    private sealed record SlotOperation(
        string OperationRunId,
        long FencingToken,
        bool IsTerminal,
        DateTimeOffset? CompletedAtUtc);
}

internal sealed record CompletedSlotOperation(
    SlotAddress Address,
    DateTimeOffset CompletedAtUtc);
