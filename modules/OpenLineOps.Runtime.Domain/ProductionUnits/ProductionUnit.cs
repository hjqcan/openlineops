using OpenLineOps.Domain.Abstractions.Entities;
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

public enum ProductionUnitDisposition
{
    InProcess,
    Completed,
    Nonconforming,
    Held,
    Scrapped
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
        LastTransitionAtUtc = registeredAtUtc;
        Disposition = ProductionUnitDisposition.InProcess;
    }

    public string ProductModelId { get; }

    public string IdentityKey { get; }

    public string IdentityValue { get; }

    public ProductionLotId? LotId { get; }

    public string RegisteredBy { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public ProductionUnitDisposition Disposition { get; private set; }

    public ProductionUnitDisposition? DispositionBeforeHold { get; private set; }

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
            LastTransitionAtUtc = snapshot.LastTransitionAtUtc,
            Disposition = snapshot.Disposition,
            DispositionBeforeHold = snapshot.DispositionBeforeHold,
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
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Location = stationLocation;
        LastTransitionAtUtc = arrivedAtUtc;
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
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Location = destination;
        LastTransitionAtUtc = transferredAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit location transferred.");
    }

    public RuntimeOperationResult Hold(string reason, DateTimeOffset heldAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (Disposition is ProductionUnitDisposition.Completed
            or ProductionUnitDisposition.Scrapped
            or ProductionUnitDisposition.Held)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitHoldRejected",
                $"Production Unit {Id} cannot be held from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            heldAtUtc,
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        DispositionBeforeHold = Disposition;
        Disposition = ProductionUnitDisposition.Held;
        DispositionReason = normalizedReason;
        LastTransitionAtUtc = heldAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit held.");
    }

    public RuntimeOperationResult Release(DateTimeOffset releasedAtUtc)
    {
        if (Disposition != ProductionUnitDisposition.Held || DispositionBeforeHold is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitReleaseRejected",
                $"Production Unit {Id} is not held.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            releasedAtUtc,
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = DispositionBeforeHold.Value;
        DispositionBeforeHold = null;
        DispositionReason = null;
        LastTransitionAtUtc = releasedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit released.");
    }

    public RuntimeOperationResult MarkNonconforming(string reason, DateTimeOffset markedAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (Disposition != ProductionUnitDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitNonconformingRejected",
                $"Production Unit {Id} cannot be marked nonconforming from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            markedAtUtc,
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductionUnitDisposition.Nonconforming;
        DispositionReason = normalizedReason;
        LastTransitionAtUtc = markedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit marked nonconforming.");
    }

    public RuntimeOperationResult Complete(DateTimeOffset completedAtUtc)
    {
        if (Disposition != ProductionUnitDisposition.InProcess)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitCompleteRejected",
                $"Production Unit {Id} cannot complete from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            completedAtUtc,
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductionUnitDisposition.Completed;
        DispositionReason = null;
        LastTransitionAtUtc = completedAtUtc;
        return RuntimeOperationResult.Accepted("Production Unit completed.");
    }

    public RuntimeOperationResult Scrap(string reason, DateTimeOffset scrappedAtUtc)
    {
        var normalizedReason = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (Disposition is ProductionUnitDisposition.Completed or ProductionUnitDisposition.Scrapped)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitScrapRejected",
                $"Production Unit {Id} cannot be scrapped from {Disposition}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            scrappedAtUtc,
            LastTransitionAtUtc,
            $"Production Unit {Id}");
        Disposition = ProductionUnitDisposition.Scrapped;
        DispositionBeforeHold = null;
        DispositionReason = normalizedReason;
        LastTransitionAtUtc = scrappedAtUtc;
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
            LastTransitionAtUtc,
            Disposition,
            DispositionBeforeHold,
            DispositionReason,
            Location);
    }

    private RuntimeOperationResult RequireMovable()
    {
        return Disposition is ProductionUnitDisposition.InProcess
            or ProductionUnitDisposition.Nonconforming
            ? RuntimeOperationResult.Accepted()
            : RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitMovementRejected",
                $"Production Unit {Id} cannot move while its disposition is {Disposition}.");
    }

    private void ValidateState()
    {
        ProductionMaterialGuard.Utc(LastTransitionAtUtc, nameof(LastTransitionAtUtc));
        if (LastTransitionAtUtc < RegisteredAtUtc)
        {
            throw new InvalidOperationException(
                "Production Unit transition time precedes registration time.");
        }

        if (!Enum.IsDefined(Disposition)
            || DispositionBeforeHold is { } previous && !Enum.IsDefined(previous))
        {
            throw new InvalidOperationException("Production Unit disposition is invalid.");
        }

        var holdStateIsValid = Disposition == ProductionUnitDisposition.Held
            ? DispositionBeforeHold is ProductionUnitDisposition.InProcess
                or ProductionUnitDisposition.Nonconforming
                && DispositionReason is not null
            : DispositionBeforeHold is null;
        if (!holdStateIsValid)
        {
            throw new InvalidOperationException("Production Unit hold state is inconsistent.");
        }

        if (Disposition is ProductionUnitDisposition.Nonconforming
            or ProductionUnitDisposition.Scrapped
            && DispositionReason is null)
        {
            throw new InvalidOperationException(
                $"Production Unit disposition {Disposition} requires a reason.");
        }

        if (Disposition is ProductionUnitDisposition.InProcess
            or ProductionUnitDisposition.Completed
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
    DateTimeOffset LastTransitionAtUtc,
    ProductionUnitDisposition Disposition,
    ProductionUnitDisposition? DispositionBeforeHold,
    string? DispositionReason,
    MaterialLocation? Location);
