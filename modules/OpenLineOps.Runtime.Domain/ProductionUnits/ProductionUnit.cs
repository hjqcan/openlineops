using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.ProductionUnits;

public readonly record struct ProductionUnitId
{
    public ProductionUnitId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Production Unit id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ProductionUnitId New()
    {
        return new ProductionUnitId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public sealed class ProductionUnit : AggregateRoot<ProductionUnitId>
{
    private ProductionUnit(
        ProductionUnitId id,
        string productModelId,
        string identityKey,
        string identityValue,
        ProductionLotId? lotId,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
        : base(id)
    {
        ProductModelId = ProductionMaterialGuard.Canonical(productModelId, nameof(productModelId));
        IdentityKey = ProductionMaterialGuard.Canonical(identityKey, nameof(identityKey));
        IdentityValue = ProductionMaterialGuard.Canonical(identityValue, nameof(identityValue));
        LotId = lotId;
        RegisteredBy = ProductionMaterialGuard.Canonical(registeredBy, nameof(registeredBy));
        RegisteredAtUtc = ProductionMaterialGuard.Utc(registeredAtUtc, nameof(registeredAtUtc));
        LastLocationTransitionAtUtc = registeredAtUtc;
        LastDispositionTransitionAtUtc = registeredAtUtc;
        Disposition = ProductDisposition.InProcess;
    }

    public string ProductModelId { get; }

    public string IdentityKey { get; }

    public string IdentityValue { get; }

    public ProductionLotId? LotId { get; }

    public string RegisteredBy { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public DateTimeOffset LastLocationTransitionAtUtc { get; private set; }

    public DateTimeOffset LastDispositionTransitionAtUtc { get; private set; }

    public DateTimeOffset LastTransitionAtUtc =>
        LastLocationTransitionAtUtc >= LastDispositionTransitionAtUtc
            ? LastLocationTransitionAtUtc
            : LastDispositionTransitionAtUtc;

    public ProductDisposition Disposition { get; private set; }

    public ProductDisposition? DispositionBeforeHold { get; private set; }

    public ProductionRunId? ActiveProductionRunId { get; private set; }

    public ProductionRunId? LastProductionRunId { get; private set; }

    public long LastProductionRunRevision { get; private set; }

    public string? DispositionReason { get; private set; }

    public MaterialLocation? Location { get; private set; }

    public static ProductionUnit Register(
        ProductionUnitId id,
        string productModelId,
        string identityKey,
        string identityValue,
        ProductionLotId? lotId,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
    {
        return new ProductionUnit(
            id,
            productModelId,
            identityKey,
            identityValue,
            lotId,
            registeredBy,
            registeredAtUtc);
    }

    public static ProductionUnit Restore(ProductionUnitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var unit = new ProductionUnit(
            snapshot.Id,
            snapshot.ProductModelId,
            snapshot.IdentityKey,
            snapshot.IdentityValue,
            snapshot.LotId,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc)
        {
            LastLocationTransitionAtUtc = snapshot.LastLocationTransitionAtUtc,
            LastDispositionTransitionAtUtc = snapshot.LastDispositionTransitionAtUtc,
            Disposition = snapshot.Disposition,
            DispositionBeforeHold = snapshot.DispositionBeforeHold,
            ActiveProductionRunId = snapshot.ActiveProductionRunId,
            LastProductionRunId = snapshot.LastProductionRunId,
            LastProductionRunRevision = snapshot.LastProductionRunRevision,
            DispositionReason = ProductionMaterialGuard.OptionalCanonical(
                snapshot.DispositionReason,
                nameof(snapshot.DispositionReason)),
            Location = snapshot.Location
        };
        unit.ValidateState();
        unit.ClearDomainEvents();
        return unit;
    }

    public RuntimeOperationResult Arrive(
        MaterialLocation stationLocation,
        DateTimeOffset arrivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stationLocation);
        if (stationLocation.Kind != MaterialLocationKind.StationQueue)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialArrivalLocationInvalid",
                "A Production Unit arrival must target a Station queue.");
        }

        if (Location is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialAlreadyLocated",
                $"Production Unit {Id} already has location {Location}.");
        }

        var movable = RequireMovable();
        if (!movable.Succeeded)
        {
            return movable;
        }

        ProductionMaterialGuard.RequireMonotonic(
            arrivedAtUtc,
            LastLocationTransitionAtUtc,
            $"Production Unit {Id}");
        Location = stationLocation;
        LastLocationTransitionAtUtc = arrivedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit arrived at Station queue.");
    }

    public RuntimeOperationResult Transfer(
        MaterialLocation expectedLocation,
        MaterialLocation destination,
        DateTimeOffset transferredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(expectedLocation);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Equals(Location, expectedLocation))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialSourceMismatch",
                $"Production Unit {Id} is not at the expected source location.");
        }

        if (Equals(expectedLocation, destination))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialDestinationUnchanged",
                "A material transfer requires a different destination.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            transferredAtUtc,
            LastLocationTransitionAtUtc,
            $"Production Unit {Id}");
        Location = destination;
        LastLocationTransitionAtUtc = transferredAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit location transferred.");
    }

    public RuntimeOperationResult ReserveProductionRun(
        ProductionRunId productionRunId,
        DateTimeOffset reservedAtUtc)
    {
        if (productionRunId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production Run id cannot be empty.", nameof(productionRunId));
        }

        if (ActiveProductionRunId is { } activeRunId)
        {
            return activeRunId == productionRunId
                ? RuntimeOperationResult.Accepted("Production Unit is already reserved for this run.")
                : RuntimeOperationResult.Rejected(
                    "Runtime.ProductionUnitAlreadyActive",
                    $"Production Unit {Id} is already reserved for Production Run {activeRunId}.");
        }

        if (Disposition != ProductDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunReservationRejected",
                $"Production Unit {Id} cannot enter a run while its disposition is {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            reservedAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        ActiveProductionRunId = productionRunId;
        LastProductionRunId = productionRunId;
        LastProductionRunRevision = 0;
        LastDispositionTransitionAtUtc = reservedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit reserved for Production Run.");
    }

    public RuntimeOperationResult SynchronizeProductionRun(
        ProductionRunId productionRunId,
        long runRevision,
        ProductDisposition disposition,
        bool isTerminal,
        string? reason,
        DateTimeOffset occurredAtUtc)
    {
        if (ActiveProductionRunId != productionRunId)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunReservationMismatch",
                $"Production Unit {Id} is not reserved for Production Run {productionRunId}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runRevision);
        if (LastProductionRunId != productionRunId
            || runRevision <= LastProductionRunRevision)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunRevisionRejected",
                $"Production Unit {Id} cannot apply Production Run {productionRunId} revision {runRevision} "
                + $"after {LastProductionRunId}/{LastProductionRunRevision}.");
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }


        if (!isTerminal && disposition is ProductDisposition.Completed or ProductDisposition.Scrapped)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunStateInvalid",
                $"Active Production Run {productionRunId} cannot project terminal disposition {disposition}.");
        }

        if (isTerminal && disposition == ProductDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunStateInvalid",
                $"Terminal Production Run {productionRunId} cannot retain InProcess disposition.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            occurredAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = disposition;
        DispositionBeforeHold = null;
        DispositionReason = disposition is ProductDisposition.Nonconforming
            or ProductDisposition.Held
            or ProductDisposition.Scrapped
                ? ProductionMaterialGuard.Canonical(
                    reason ?? $"Production Run {productionRunId} entered {disposition}.",
                    nameof(reason))
                : null;
        ActiveProductionRunId = isTerminal ? null : productionRunId;
        LastProductionRunId = productionRunId;
        LastProductionRunRevision = runRevision;
        LastDispositionTransitionAtUtc = occurredAtUtc;
        ValidateState();
        return RuntimeOperationResult.Accepted("Production Unit synchronized with Production Run.");
    }

    public RuntimeOperationResult Hold(string reason, DateTimeOffset heldAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (ActiveProductionRunId is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitActiveRunConflict",
                $"Production Unit {Id} disposition is controlled by active Production Run {ActiveProductionRunId}.");
        }

        if (Disposition is ProductDisposition.Completed
            or ProductDisposition.Scrapped
            or ProductDisposition.Held)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitHoldRejected",
                $"Production Unit {Id} cannot be held from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            heldAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        DispositionBeforeHold = Disposition;
        Disposition = ProductDisposition.Held;
        DispositionReason = normalizedReason;
        LastDispositionTransitionAtUtc = heldAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit held.");
    }

    public RuntimeOperationResult Release(DateTimeOffset releasedAtUtc)
    {
        if (ActiveProductionRunId is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitActiveRunConflict",
                $"Production Unit {Id} disposition is controlled by active Production Run {ActiveProductionRunId}.");
        }

        if (Disposition != ProductDisposition.Held || DispositionBeforeHold is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitReleaseRejected",
                $"Production Unit {Id} is not held.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            releasedAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = DispositionBeforeHold.Value;
        DispositionBeforeHold = null;
        DispositionReason = null;
        LastDispositionTransitionAtUtc = releasedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit released.");
    }

    public RuntimeOperationResult MarkNonconforming(string reason, DateTimeOffset markedAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (ActiveProductionRunId is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitActiveRunConflict",
                $"Production Unit {Id} disposition is controlled by active Production Run {ActiveProductionRunId}.");
        }

        if (Disposition != ProductDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitNonconformingRejected",
                $"Production Unit {Id} cannot be marked nonconforming from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            markedAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductDisposition.Nonconforming;
        DispositionReason = normalizedReason;
        LastDispositionTransitionAtUtc = markedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit marked nonconforming.");
    }

    public RuntimeOperationResult Complete(DateTimeOffset completedAtUtc)
    {
        if (ActiveProductionRunId is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitActiveRunConflict",
                $"Production Unit {Id} disposition is controlled by active Production Run {ActiveProductionRunId}.");
        }

        if (Disposition != ProductDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitCompleteRejected",
                $"Production Unit {Id} cannot complete from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            completedAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductDisposition.Completed;
        DispositionReason = null;
        LastDispositionTransitionAtUtc = completedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit completed.");
    }

    public RuntimeOperationResult Scrap(string reason, DateTimeOffset scrappedAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (ActiveProductionRunId is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitActiveRunConflict",
                $"Production Unit {Id} disposition is controlled by active Production Run {ActiveProductionRunId}.");
        }

        if (Disposition is ProductDisposition.Completed or ProductDisposition.Scrapped)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitScrapRejected",
                $"Production Unit {Id} cannot be scrapped from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            scrappedAtUtc,
            LastDispositionTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductDisposition.Scrapped;
        DispositionBeforeHold = null;
        DispositionReason = normalizedReason;
        LastDispositionTransitionAtUtc = scrappedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit scrapped.");
    }

    public ProductionUnitSnapshot ToSnapshot()
    {
        return new ProductionUnitSnapshot(
            Id,
            ProductModelId,
            IdentityKey,
            IdentityValue,
            LotId,
            RegisteredBy,
            RegisteredAtUtc,
            LastLocationTransitionAtUtc,
            LastDispositionTransitionAtUtc,
            Disposition,
            DispositionBeforeHold,
            ActiveProductionRunId,
            LastProductionRunId,
            LastProductionRunRevision,
            DispositionReason,
            Location);
    }

    private RuntimeOperationResult RequireMovable()
    {
        return Disposition is ProductDisposition.InProcess
            or ProductDisposition.Nonconforming
            ? RuntimeOperationResult.Accepted()
            : RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitMovementRejected",
                $"Production Unit {Id} cannot move while its disposition is {Disposition}.");
    }

    private void ValidateState()
    {
        ProductionMaterialGuard.Utc(
            LastLocationTransitionAtUtc,
            nameof(LastLocationTransitionAtUtc));
        ProductionMaterialGuard.Utc(
            LastDispositionTransitionAtUtc,
            nameof(LastDispositionTransitionAtUtc));
        if (LastLocationTransitionAtUtc < RegisteredAtUtc
            || LastDispositionTransitionAtUtc < RegisteredAtUtc)
        {
            throw new InvalidOperationException(
                "Production Unit transition time precedes registration time.");
        }


        if (LastProductionRunRevision < 0
            || LastProductionRunId is null && LastProductionRunRevision != 0
            || ActiveProductionRunId is not null && LastProductionRunId != ActiveProductionRunId)
        {
            throw new InvalidOperationException("Production Unit run reservation state is inconsistent.");
        }

        if (ActiveProductionRunId is not null
            && Disposition is (ProductDisposition.Completed or ProductDisposition.Scrapped))
        {
            throw new InvalidOperationException(
                "An active Production Run cannot coexist with a terminal Production Unit disposition.");
        }

        if (!Enum.IsDefined(Disposition)
            || DispositionBeforeHold is { } previous && !Enum.IsDefined(previous))
        {
            throw new InvalidOperationException("Production Unit disposition is invalid.");
        }

        var holdStateIsValid = Disposition == ProductDisposition.Held
            ? DispositionReason is not null
              && (DispositionBeforeHold is null && LastProductionRunId is not null
                  || ActiveProductionRunId is null
                  && DispositionBeforeHold is (ProductDisposition.InProcess
                      or ProductDisposition.Nonconforming))
            : DispositionBeforeHold is null;
        if (!holdStateIsValid)
        {
            throw new InvalidOperationException("Production Unit hold state is inconsistent.");
        }

        if (Disposition is ProductDisposition.Nonconforming
            or ProductDisposition.Scrapped
            && DispositionReason is null)
        {
            throw new InvalidOperationException(
                $"Production Unit disposition {Disposition} requires a reason.");
        }

        if (Disposition is ProductDisposition.InProcess
            or ProductDisposition.Completed
            && DispositionReason is not null)
        {
            throw new InvalidOperationException(
                $"Production Unit disposition {Disposition} cannot retain a reason.");
        }
    }
}

public sealed record ProductionUnitSnapshot(
    ProductionUnitId Id,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    ProductionLotId? LotId,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastLocationTransitionAtUtc,
    DateTimeOffset LastDispositionTransitionAtUtc,
    ProductDisposition Disposition,
    ProductDisposition? DispositionBeforeHold,
    ProductionRunId? ActiveProductionRunId,
    ProductionRunId? LastProductionRunId,
    long LastProductionRunRevision,
    string? DispositionReason,
    MaterialLocation? Location)
{
    public DateTimeOffset LastTransitionAtUtc =>
        LastLocationTransitionAtUtc >= LastDispositionTransitionAtUtc
            ? LastLocationTransitionAtUtc
            : LastDispositionTransitionAtUtc;
}
