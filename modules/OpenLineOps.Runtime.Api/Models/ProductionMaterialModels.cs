using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionUnitApiRequest(
    Guid ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    string? LotId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionLotApiRequest(
    string LotId,
    string ProductModelId,
    int? DeclaredQuantity,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionCarrierApiRequest(
    string CarrierId,
    string CarrierTypeId,
    int Capacity,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterSlotOccupancyApiRequest(
    string LineId,
    string StationSystemId,
    string SlotId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterialArrivalApiRequest(
    string LineId,
    string StationSystemId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionUnitDispositionCommandApiRequest(
    string ActorId,
    string? Reason,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterialTransferApiRequest(
    MaterialLocationApiRequest ExpectedLocation,
    MaterialLocationApiRequest Destination,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SlotOccupancyCommandApiRequest(
    string? MaterialKind,
    string? MaterialId,
    MaterialLocationApiRequest? Destination,
    string? Reason,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterialLocationApiRequest(
    string Kind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? CarrierId,
    string? CarrierPositionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LinkMaterialGenealogyApiRequest(
    Guid LinkId,
    Guid ParentProductionUnitId,
    Guid ChildProductionUnitId,
    string Relationship,
    string OperationId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record MaterialLocationApiResponse(
    string Kind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? CarrierId,
    string? CarrierPositionId);

public sealed record ProductionUnitApiResponse(
    Guid ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    string? LotId,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset LastLocationTransitionAtUtc,
    DateTimeOffset LastDispositionTransitionAtUtc,
    string Disposition,
    string? DispositionBeforeHold,
    Guid? ActiveProductionRunId,
    Guid? LastProductionRunId,
    long LastProductionRunRevision,
    string? DispositionReason,
    MaterialLocationApiResponse? Location);

public sealed record ProductionLotApiResponse(
    string LotId,
    string ProductModelId,
    int? DeclaredQuantity,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc);

public sealed record ProductionCarrierApiResponse(
    string CarrierId,
    string CarrierTypeId,
    int Capacity,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    MaterialLocationApiResponse? Location);

public sealed record SlotOccupancyApiResponse(
    string LineId,
    string StationSystemId,
    string SlotId,
    string Status,
    string? MaterialKind,
    string? MaterialId,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc);

public sealed record MaterialGenealogyApiResponse(
    Guid LinkId,
    Guid ParentProductionUnitId,
    Guid ChildProductionUnitId,
    string Relationship,
    string OperationId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);
