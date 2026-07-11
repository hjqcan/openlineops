using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;
using static OpenLineOps.Traceability.Domain.Records.TraceMaterialEvidenceGuard;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record TraceMaterialLocation
{
    public TraceMaterialLocation(
        string kind,
        string? lineId,
        string? stationSystemId,
        string? slotId,
        string? carrierId,
        string? carrierPositionId)
    {
        Kind = TraceabilityIdGuard.NotBlank(kind, nameof(kind));
        LineId = TraceabilityIdGuard.OptionalText(lineId);
        StationSystemId = TraceabilityIdGuard.OptionalText(stationSystemId);
        SlotId = TraceabilityIdGuard.OptionalText(slotId);
        CarrierId = TraceabilityIdGuard.OptionalText(carrierId);
        CarrierPositionId = TraceabilityIdGuard.OptionalText(carrierPositionId);
        var valid = kind switch
        {
            "StationQueue" => LineId is not null
                && StationSystemId is not null
                && SlotId is null
                && CarrierId is null
                && CarrierPositionId is null,
            "Slot" => LineId is not null
                && StationSystemId is not null
                && SlotId is not null
                && CarrierId is null
                && CarrierPositionId is null,
            "CarrierPosition" => LineId is null
                && StationSystemId is null
                && SlotId is null
                && CarrierId is not null
                && CarrierPositionId is not null,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException($"Trace Material location {kind} has an invalid shape.");
        }
    }

    public string Kind { get; }
    public string? LineId { get; }
    public string? StationSystemId { get; }
    public string? SlotId { get; }
    public string? CarrierId { get; }
    public string? CarrierPositionId { get; }
}

public sealed record TraceMaterialLocationTransition
{
    public TraceMaterialLocationTransition(
        Guid evidenceId,
        Guid? productionRunId,
        string materialKind,
        string materialId,
        TraceMaterialLocation? source,
        TraceMaterialLocation destination,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        EvidenceId = RequiredGuid(evidenceId, nameof(evidenceId));
        ProductionRunId = OptionalGuid(productionRunId, nameof(productionRunId));
        MaterialKind = RequiredToken(materialKind, nameof(materialKind), "ProductionUnit", "Carrier");
        MaterialId = TraceabilityIdGuard.NotBlank(materialId, nameof(materialId));
        Source = source;
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        ActorId = TraceabilityIdGuard.NotBlank(actorId, nameof(actorId));
        OccurredAtUtc = RequiredTimestamp(occurredAtUtc, nameof(occurredAtUtc));
    }

    public Guid EvidenceId { get; }
    public Guid? ProductionRunId { get; }
    public string MaterialKind { get; }
    public string MaterialId { get; }
    public TraceMaterialLocation? Source { get; }
    public TraceMaterialLocation Destination { get; }
    public string ActorId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record TraceSlotOccupancyTransition
{
    public TraceSlotOccupancyTransition(
        Guid evidenceId,
        Guid? productionRunId,
        string lineId,
        string stationSystemId,
        string slotId,
        string? materialKind,
        string? materialId,
        string previousStatus,
        string currentStatus,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        EvidenceId = RequiredGuid(evidenceId, nameof(evidenceId));
        ProductionRunId = OptionalGuid(productionRunId, nameof(productionRunId));
        LineId = TraceabilityIdGuard.NotBlank(lineId, nameof(lineId));
        StationSystemId = TraceabilityIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
        SlotId = TraceabilityIdGuard.NotBlank(slotId, nameof(slotId));
        MaterialKind = materialKind is null
            ? null
            : RequiredToken(materialKind, nameof(materialKind), "ProductionUnit", "Carrier");
        MaterialId = TraceabilityIdGuard.OptionalText(materialId);
        if ((MaterialKind is null) != (MaterialId is null))
        {
            throw new ArgumentException(
                "Trace Slot material kind and identity must both be present or both be absent.");
        }
        PreviousStatus = RequiredToken(
            previousStatus,
            nameof(previousStatus),
            "Available", "Reserved", "Occupied", "Running", "Blocked", "Offline");
        CurrentStatus = RequiredToken(
            currentStatus,
            nameof(currentStatus),
            "Available", "Reserved", "Occupied", "Running", "Blocked", "Offline");
        if (string.Equals(PreviousStatus, CurrentStatus, StringComparison.Ordinal))
        {
            throw new ArgumentException("Trace Slot transition must change status.", nameof(currentStatus));
        }


        var requiresMaterial = PreviousStatus is "Reserved" or "Occupied" or "Running"
            || CurrentStatus is "Reserved" or "Occupied" or "Running";
        if (requiresMaterial != (MaterialKind is not null))
        {
            throw new ArgumentException(
                "Trace Slot binding evidence differs from its occupancy statuses.");
        }

        ActorId = TraceabilityIdGuard.NotBlank(actorId, nameof(actorId));
        OccurredAtUtc = RequiredTimestamp(occurredAtUtc, nameof(occurredAtUtc));
    }

    public Guid EvidenceId { get; }
    public Guid? ProductionRunId { get; }
    public string LineId { get; }
    public string StationSystemId { get; }
    public string SlotId { get; }
    public string? MaterialKind { get; }
    public string? MaterialId { get; }
    public string PreviousStatus { get; }
    public string CurrentStatus { get; }
    public string ActorId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record TraceDispositionTransition
{
    public TraceDispositionTransition(
        Guid evidenceId,
        Guid productionUnitId,
        Guid? productionRunId,
        ProductDisposition previousDisposition,
        ProductDisposition currentDisposition,
        string? reason,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        EvidenceId = RequiredGuid(evidenceId, nameof(evidenceId));
        ProductionUnitId = RequiredGuid(productionUnitId, nameof(productionUnitId));
        ProductionRunId = OptionalGuid(productionRunId, nameof(productionRunId));
        PreviousDisposition = Enum.IsDefined(previousDisposition)
            ? previousDisposition
            : throw new ArgumentOutOfRangeException(nameof(previousDisposition));
        CurrentDisposition = Enum.IsDefined(currentDisposition)
            ? currentDisposition
            : throw new ArgumentOutOfRangeException(nameof(currentDisposition));
        if (PreviousDisposition == CurrentDisposition)
        {
            throw new ArgumentException("Trace disposition transition must change disposition.");
        }

        Reason = TraceabilityIdGuard.OptionalText(reason);
        ActorId = TraceabilityIdGuard.NotBlank(actorId, nameof(actorId));
        OccurredAtUtc = RequiredTimestamp(occurredAtUtc, nameof(occurredAtUtc));
    }

    public Guid EvidenceId { get; }
    public Guid ProductionUnitId { get; }
    public Guid? ProductionRunId { get; }
    public ProductDisposition PreviousDisposition { get; }
    public ProductDisposition CurrentDisposition { get; }
    public string? Reason { get; }
    public string ActorId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record TraceMaterialGenealogy
{
    public TraceMaterialGenealogy(
        Guid linkId,
        Guid parentProductionUnitId,
        Guid childProductionUnitId,
        string relationship,
        string operationId,
        string linkedBy,
        DateTimeOffset linkedAtUtc)
    {
        LinkId = RequiredGuid(linkId, nameof(linkId));
        ParentProductionUnitId = RequiredGuid(parentProductionUnitId, nameof(parentProductionUnitId));
        ChildProductionUnitId = RequiredGuid(childProductionUnitId, nameof(childProductionUnitId));
        if (ParentProductionUnitId == ChildProductionUnitId)
        {
            throw new ArgumentException("Trace genealogy parent and child must differ.");
        }

        Relationship = TraceabilityIdGuard.NotBlank(relationship, nameof(relationship));
        OperationId = TraceabilityIdGuard.NotBlank(operationId, nameof(operationId));
        LinkedBy = TraceabilityIdGuard.NotBlank(linkedBy, nameof(linkedBy));
        LinkedAtUtc = RequiredTimestamp(linkedAtUtc, nameof(linkedAtUtc));
    }

    public Guid LinkId { get; }
    public Guid ParentProductionUnitId { get; }
    public Guid ChildProductionUnitId { get; }
    public string Relationship { get; }
    public string OperationId { get; }
    public string LinkedBy { get; }
    public DateTimeOffset LinkedAtUtc { get; }
}

internal static class TraceMaterialEvidenceGuard
{
    public static Guid RequiredGuid(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("Trace Material evidence GUID cannot be empty.", parameterName)
        : value;

    public static Guid? OptionalGuid(Guid? value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("Optional Trace Material evidence GUID cannot be empty.", parameterName)
        : value;

    public static DateTimeOffset RequiredTimestamp(DateTimeOffset value, string parameterName) =>
        value == default || value.Offset != TimeSpan.Zero
            ? throw new ArgumentException(
                "Trace Material evidence timestamp must use UTC offset zero.",
                parameterName)
            : value;

    public static string RequiredToken(string value, string parameterName, params string[] allowed)
    {
        var canonical = TraceabilityIdGuard.NotBlank(value, parameterName);
        return allowed.Contains(canonical, StringComparer.Ordinal)
            ? canonical
            : throw new ArgumentException(
                $"Trace Material evidence token '{value}' is invalid.",
                parameterName);
    }
}
