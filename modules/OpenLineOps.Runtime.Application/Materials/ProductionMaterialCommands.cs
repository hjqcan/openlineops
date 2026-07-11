using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Application.Materials;

public sealed record RegisterProductionLotCommand(
    ProductionLotId LotId,
    string ProductModelId,
    int? DeclaredQuantity,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record RegisterProductionUnitCommand(
    ProductionUnitId ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    ProductionLotId? LotId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record RegisterCarrierCommand(
    CarrierId CarrierId,
    string CarrierTypeId,
    int Capacity,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record RegisterSlotCommand(
    SlotAddress Slot,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record ArriveMaterialCommand(
    Guid EvidenceId,
    MaterialReference Material,
    MaterialLocation StationLocation,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record ReserveSlotCommand(
    SlotAddress Slot,
    MaterialReference Material,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record ReleaseSlotReservationCommand(
    SlotAddress Slot,
    MaterialReference Material,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record LoadSlotCommand(
    SlotAddress Slot,
    MaterialReference Material,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record StartSlotCommand(
    SlotAddress Slot,
    MaterialReference Material,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record CompleteSlotCommand(
    SlotAddress Slot,
    MaterialReference Material,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record BlockSlotCommand(
    SlotAddress Slot,
    string Reason,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record UnblockSlotCommand(
    SlotAddress Slot,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record SetSlotOfflineCommand(
    SlotAddress Slot,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record BringSlotOnlineCommand(
    SlotAddress Slot,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record UnloadSlotCommand(
    SlotAddress Slot,
    MaterialReference Material,
    MaterialLocation Destination,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record TransferMaterialCommand(
    MaterialReference Material,
    MaterialLocation ExpectedLocation,
    MaterialLocation Destination,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record HoldProductionUnitCommand(
    ProductionUnitId ProductionUnitId,
    string Reason,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record ReleaseProductionUnitCommand(
    ProductionUnitId ProductionUnitId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record ScrapProductionUnitCommand(
    ProductionUnitId ProductionUnitId,
    string Reason,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record LinkMaterialGenealogyCommand(
    MaterialGenealogyLinkId LinkId,
    ProductionUnitId ParentUnitId,
    ProductionUnitId ChildUnitId,
    string Relationship,
    string OperationId,
    string ActorId,
    DateTimeOffset OccurredAtUtc);
