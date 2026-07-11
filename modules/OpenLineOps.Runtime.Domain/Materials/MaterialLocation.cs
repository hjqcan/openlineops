namespace OpenLineOps.Runtime.Domain.Materials;

public enum MaterialLocationKind
{
    StationQueue,
    Slot,
    CarrierPosition
}

public sealed record SlotAddress
{
    public SlotAddress(string lineId, string stationSystemId, string slotId)
    {
        LineId = ProductionMaterialGuard.Canonical(lineId, nameof(lineId));
        StationSystemId = ProductionMaterialGuard.Canonical(
            stationSystemId,
            nameof(stationSystemId));
        SlotId = ProductionMaterialGuard.Canonical(slotId, nameof(slotId));
    }

    public string LineId { get; }

    public string StationSystemId { get; }

    public string SlotId { get; }

    public override string ToString()
    {
        return $"{LineId}/{StationSystemId}/{SlotId}";
    }
}

public sealed record MaterialLocation
{
    private MaterialLocation(
        MaterialLocationKind kind,
        string? lineId,
        string? stationSystemId,
        string? slotId,
        CarrierId? carrierId,
        string? carrierPositionId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Material location kind is invalid.");
        }

        Kind = kind;
        LineId = lineId;
        StationSystemId = stationSystemId;
        SlotId = slotId;
        CarrierId = carrierId;
        CarrierPositionId = carrierPositionId;
        Validate();
    }

    public MaterialLocationKind Kind { get; }

    public string? LineId { get; }

    public string? StationSystemId { get; }

    public string? SlotId { get; }

    public CarrierId? CarrierId { get; }

    public string? CarrierPositionId { get; }

    public SlotAddress? SlotAddress => Kind == MaterialLocationKind.Slot
        ? new SlotAddress(LineId!, StationSystemId!, SlotId!)
        : null;

    public static MaterialLocation AtStation(string lineId, string stationSystemId)
    {
        return new MaterialLocation(
            MaterialLocationKind.StationQueue,
            ProductionMaterialGuard.Canonical(lineId, nameof(lineId)),
            ProductionMaterialGuard.Canonical(stationSystemId, nameof(stationSystemId)),
            null,
            null,
            null);
    }

    public static MaterialLocation InSlot(SlotAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new MaterialLocation(
            MaterialLocationKind.Slot,
            address.LineId,
            address.StationSystemId,
            address.SlotId,
            null,
            null);
    }

    public static MaterialLocation OnCarrier(CarrierId carrierId, string carrierPositionId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);
        return new MaterialLocation(
            MaterialLocationKind.CarrierPosition,
            null,
            null,
            null,
            carrierId,
            ProductionMaterialGuard.Canonical(carrierPositionId, nameof(carrierPositionId)));
    }

    private void Validate()
    {
        switch (Kind)
        {
            case MaterialLocationKind.StationQueue:
                RequirePhysicalIdentity(requireSlot: false);
                Require(CarrierId is null && CarrierPositionId is null,
                    "A StationQueue location cannot declare Carrier identity.");
                break;
            case MaterialLocationKind.Slot:
                RequirePhysicalIdentity(requireSlot: true);
                Require(CarrierId is null && CarrierPositionId is null,
                    "A Slot location cannot declare Carrier identity.");
                break;
            case MaterialLocationKind.CarrierPosition:
                Require(LineId is null && StationSystemId is null && SlotId is null,
                    "A CarrierPosition location cannot declare physical Slot identity.");
                Require(CarrierId is not null && CarrierPositionId is not null,
                    "A CarrierPosition location requires Carrier and position identity.");
                _ = ProductionMaterialGuard.Canonical(CarrierPositionId!, nameof(CarrierPositionId));
                break;
            default:
                throw new InvalidOperationException($"Unsupported material location kind {Kind}.");
        }
    }

    private void RequirePhysicalIdentity(bool requireSlot)
    {
        _ = ProductionMaterialGuard.Canonical(LineId!, nameof(LineId));
        _ = ProductionMaterialGuard.Canonical(StationSystemId!, nameof(StationSystemId));
        if (requireSlot)
        {
            _ = ProductionMaterialGuard.Canonical(SlotId!, nameof(SlotId));
        }
        else
        {
            Require(SlotId is null, "A StationQueue location cannot declare Slot identity.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
