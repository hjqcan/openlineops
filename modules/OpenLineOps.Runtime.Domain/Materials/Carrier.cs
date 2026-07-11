using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Materials;

public sealed record CarrierId
{
    public CarrierId(string value)
    {
        Value = ProductionMaterialGuard.Canonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}

public sealed class Carrier : AggregateRoot<CarrierId>
{
    private Carrier(
        CarrierId id,
        string carrierTypeId,
        int capacity,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        CarrierTypeId = ProductionMaterialGuard.Canonical(carrierTypeId, nameof(carrierTypeId));
        Capacity = capacity;
        RegisteredBy = ProductionMaterialGuard.Canonical(registeredBy, nameof(registeredBy));
        RegisteredAtUtc = ProductionMaterialGuard.Utc(registeredAtUtc, nameof(registeredAtUtc));
        LastTransitionAtUtc = registeredAtUtc;
    }

    public string CarrierTypeId { get; }

    public int Capacity { get; }

    public string RegisteredBy { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public MaterialLocation? Location { get; private set; }

    public static Carrier Register(
        CarrierId id,
        string carrierTypeId,
        int capacity,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
    {
        return new Carrier(id, carrierTypeId, capacity, registeredBy, registeredAtUtc);
    }

    public static Carrier Restore(CarrierSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var carrier = new Carrier(
            snapshot.Id,
            snapshot.CarrierTypeId,
            snapshot.Capacity,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc)
        {
            LastTransitionAtUtc = snapshot.LastTransitionAtUtc,
            Location = snapshot.Location
        };
        ProductionMaterialGuard.Utc(carrier.LastTransitionAtUtc, nameof(snapshot.LastTransitionAtUtc));
        if (carrier.LastTransitionAtUtc < carrier.RegisteredAtUtc)
        {
            throw new InvalidOperationException("Carrier transition time precedes registration time.");
        }

        carrier.ClearDomainEvents();
        return carrier;
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
                "A Carrier arrival must target a Station queue.");
        }

        if (Location is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialAlreadyLocated",
                $"Carrier {Id} already has location {Location}.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            arrivedAtUtc,
            LastTransitionAtUtc,
            $"Carrier {Id}");
        Location = stationLocation;
        LastTransitionAtUtc = arrivedAtUtc;
        return RuntimeOperationResult.Accepted("Carrier arrived at Station queue.");
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
                $"Carrier {Id} is not at the expected source location.");
        }

        if (Equals(expectedLocation, destination))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialDestinationUnchanged",
                "A material transfer requires a different destination.");
        }

        if (destination.Kind == MaterialLocationKind.CarrierPosition)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CarrierNestingRejected",
                "A Carrier cannot be placed inside another Carrier.");
        }

        ProductionMaterialGuard.RequireMonotonic(
            transferredAtUtc,
            LastTransitionAtUtc,
            $"Carrier {Id}");
        Location = destination;
        LastTransitionAtUtc = transferredAtUtc;
        return RuntimeOperationResult.Accepted("Carrier location transferred.");
    }

    public CarrierSnapshot ToSnapshot()
    {
        return new CarrierSnapshot(
            Id,
            CarrierTypeId,
            Capacity,
            RegisteredBy,
            RegisteredAtUtc,
            LastTransitionAtUtc,
            Location);
    }
}

public sealed record CarrierSnapshot(
    CarrierId Id,
    string CarrierTypeId,
    int Capacity,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    MaterialLocation? Location);
