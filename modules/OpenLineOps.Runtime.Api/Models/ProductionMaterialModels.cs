using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionUnitApiRequest(
    Guid ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    string? LotId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionLotApiRequest(
    string LotId,
    string ProductModelId,
    int? DeclaredQuantity,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterProductionCarrierApiRequest(
    string CarrierId,
    string CarrierTypeId,
    int Capacity,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterSlotOccupancyApiRequest(
    string LineId,
    string StationSystemId,
    string SlotId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterialArrivalApiRequest(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string PackageContentSha256,
    string StationId,
    string LineId,
    string StationSystemId,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionUnitDispositionCommandApiRequest(
    string? Reason,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterialTransferApiRequest(
    MaterialLocationApiRequest ExpectedLocation,
    MaterialLocationApiRequest Destination,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SlotOccupancyCommandApiRequest(
    string? MaterialKind,
    string? MaterialId,
    MaterialLocationApiRequest? Destination,
    string? Reason,
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
