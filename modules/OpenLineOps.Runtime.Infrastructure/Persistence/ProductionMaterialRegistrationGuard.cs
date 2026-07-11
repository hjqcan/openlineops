using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionMaterialRegistrationGuard
{
    public static void RequireInitial(ProductionUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Location is not null
            || unit.Disposition != ProductDisposition.InProcess
            || unit.DispositionBeforeHold is not null
            || unit.ActiveProductionRunId is not null
            || unit.LastProductionRunId is not null
            || unit.LastProductionRunRevision != 0
            || unit.DispositionReason is not null
            || unit.LastLocationTransitionAtUtc != unit.RegisteredAtUtc
            || unit.LastDispositionTransitionAtUtc != unit.RegisteredAtUtc)
        {
            throw new ArgumentException(
                "A Production Unit must be persisted at registration before any state transition; "
                + "use ProductionMaterialService so every transition receives timeline evidence.",
                nameof(unit));
        }
    }

    public static void RequireInitial(Carrier carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        if (carrier.Location is not null || carrier.LastTransitionAtUtc != carrier.RegisteredAtUtc)
        {
            throw new ArgumentException(
                "A Carrier must be persisted at registration before any location transition; "
                + "use ProductionMaterialService so every transition receives timeline evidence.",
                nameof(carrier));
        }
    }

    public static void RequireInitial(SlotOccupancy slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Status != SlotOccupancyStatus.Available
            || slot.Material is not null
            || slot.LastTransitionAtUtc != slot.RegisteredAtUtc)
        {
            throw new ArgumentException(
                "A Slot must be persisted as Available before any occupancy transition; "
                + "use ProductionMaterialService so every transition receives timeline evidence.",
                nameof(slot));
        }
    }
}
