using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Occupancy;

public enum SlotOccupancyStatus
{
    Available,
    Reserved,
    Occupied,
    Running,
    Blocked,
    Offline
}

public sealed class SlotOccupancy : AggregateRoot<SlotAddress>
{
    private SlotOccupancy(SlotAddress address, DateTimeOffset registeredAtUtc)
        : base(address)
    {
        ArgumentNullException.ThrowIfNull(address);
        RegisteredAtUtc = ProductionMaterialGuard.Utc(registeredAtUtc, nameof(registeredAtUtc));
        LastTransitionAtUtc = registeredAtUtc;
        Status = SlotOccupancyStatus.Available;
    }

    public SlotAddress Address => Id;

    public SlotOccupancyStatus Status { get; private set; }

    public MaterialReference? Material { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public static SlotOccupancy Register(SlotAddress address, DateTimeOffset registeredAtUtc)
    {
        return new SlotOccupancy(address, registeredAtUtc);
    }

    public static SlotOccupancy Restore(SlotOccupancySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var occupancy = new SlotOccupancy(snapshot.Address, snapshot.RegisteredAtUtc)
        {
            Status = snapshot.Status,
            Material = snapshot.Material,
            LastTransitionAtUtc = snapshot.LastTransitionAtUtc
        };
        occupancy.ValidateState();
        occupancy.ClearDomainEvents();
        return occupancy;
    }

    public RuntimeOperationResult Reserve(
        MaterialReference material,
        DateTimeOffset reservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (Status != SlotOccupancyStatus.Available)
        {
            return InvalidTransition("reserve", SlotOccupancyStatus.Available);
        }

        Transition(SlotOccupancyStatus.Reserved, material, reservedAtUtc);
        return RuntimeOperationResult.Accepted("Slot reserved for material.");
    }

    public RuntimeOperationResult ReleaseReservation(
        MaterialReference material,
        DateTimeOffset releasedAtUtc)
    {
        var binding = RequireBinding(material, SlotOccupancyStatus.Reserved, "release reservation");
        if (!binding.Succeeded)
        {
            return binding;
        }

        Transition(SlotOccupancyStatus.Available, null, releasedAtUtc);
        return RuntimeOperationResult.Accepted("Slot reservation released.");
    }

    public RuntimeOperationResult Load(MaterialReference material, DateTimeOffset loadedAtUtc)
    {
        var binding = RequireBinding(material, SlotOccupancyStatus.Reserved, "load");
        if (!binding.Succeeded)
        {
            return binding;
        }

        Transition(SlotOccupancyStatus.Occupied, material, loadedAtUtc);
        return RuntimeOperationResult.Accepted("Material loaded into Slot.");
    }

    public RuntimeOperationResult Start(MaterialReference material, DateTimeOffset startedAtUtc)
    {
        var binding = RequireBinding(material, SlotOccupancyStatus.Occupied, "start");
        if (!binding.Succeeded)
        {
            return binding;
        }

        Transition(SlotOccupancyStatus.Running, material, startedAtUtc);
        return RuntimeOperationResult.Accepted("Slot processing started.");
    }

    public RuntimeOperationResult Complete(MaterialReference material, DateTimeOffset completedAtUtc)
    {
        var binding = RequireBinding(material, SlotOccupancyStatus.Running, "complete");
        if (!binding.Succeeded)
        {
            return binding;
        }

        Transition(SlotOccupancyStatus.Occupied, material, completedAtUtc);
        return RuntimeOperationResult.Accepted("Slot processing completed.");
    }

    public RuntimeOperationResult Unload(MaterialReference material, DateTimeOffset unloadedAtUtc)
    {
        var binding = RequireBinding(material, SlotOccupancyStatus.Occupied, "unload");
        if (!binding.Succeeded)
        {
            return binding;
        }

        Transition(SlotOccupancyStatus.Available, null, unloadedAtUtc);
        return RuntimeOperationResult.Accepted("Material unloaded from Slot.");
    }

    public RuntimeOperationResult Block(string reason, DateTimeOffset blockedAtUtc)
    {
        _ = ProductionMaterialGuard.Canonical(reason, nameof(reason));
        if (Status != SlotOccupancyStatus.Available)
        {
            return InvalidTransition("block", SlotOccupancyStatus.Available);
        }

        Transition(SlotOccupancyStatus.Blocked, null, blockedAtUtc);
        return RuntimeOperationResult.Accepted("Slot blocked.");
    }

    public RuntimeOperationResult Unblock(DateTimeOffset unblockedAtUtc)
    {
        if (Status != SlotOccupancyStatus.Blocked)
        {
            return InvalidTransition("unblock", SlotOccupancyStatus.Blocked);
        }

        Transition(SlotOccupancyStatus.Available, null, unblockedAtUtc);
        return RuntimeOperationResult.Accepted("Slot unblocked.");
    }

    public RuntimeOperationResult SetOffline(DateTimeOffset offlineAtUtc)
    {
        if (Status is not SlotOccupancyStatus.Available and not SlotOccupancyStatus.Blocked)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SlotOfflineRejected",
                $"Slot {Address} cannot go offline from {Status}.");
        }

        Transition(SlotOccupancyStatus.Offline, null, offlineAtUtc);
        return RuntimeOperationResult.Accepted("Slot is offline.");
    }

    public RuntimeOperationResult BringOnline(DateTimeOffset onlineAtUtc)
    {
        if (Status != SlotOccupancyStatus.Offline)
        {
            return InvalidTransition("bring online", SlotOccupancyStatus.Offline);
        }

        Transition(SlotOccupancyStatus.Available, null, onlineAtUtc);
        return RuntimeOperationResult.Accepted("Slot is available.");
    }

    public SlotOccupancySnapshot ToSnapshot()
    {
        return new SlotOccupancySnapshot(
            Address,
            Status,
            Material,
            RegisteredAtUtc,
            LastTransitionAtUtc);
    }

    private RuntimeOperationResult RequireBinding(
        MaterialReference material,
        SlotOccupancyStatus requiredStatus,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (Status != requiredStatus)
        {
            return InvalidTransition(operation, requiredStatus);
        }

        return Equals(Material, material)
            ? RuntimeOperationResult.Accepted()
            : RuntimeOperationResult.Rejected(
                "Runtime.SlotMaterialMismatch",
                $"Slot {Address} is bound to {Material}, not {material}.");
    }

    private RuntimeOperationResult InvalidTransition(
        string operation,
        SlotOccupancyStatus requiredStatus)
    {
        return RuntimeOperationResult.Rejected(
            "Runtime.SlotOccupancyTransitionRejected",
            $"Slot {Address} must be {requiredStatus} to {operation}; current status is {Status}.");
    }

    private void Transition(
        SlotOccupancyStatus status,
        MaterialReference? material,
        DateTimeOffset occurredAtUtc)
    {
        ProductionMaterialGuard.RequireMonotonic(
            occurredAtUtc,
            LastTransitionAtUtc,
            $"Slot {Address}");
        Status = status;
        Material = material;
        LastTransitionAtUtc = occurredAtUtc;
        ValidateState();
    }

    private void ValidateState()
    {
        ProductionMaterialGuard.Utc(LastTransitionAtUtc, nameof(LastTransitionAtUtc));
        if (LastTransitionAtUtc < RegisteredAtUtc)
        {
            throw new InvalidOperationException("Slot transition time precedes registration time.");
        }

        if (!Enum.IsDefined(Status))
        {
            throw new InvalidOperationException("Slot occupancy status is invalid.");
        }

        var requiresMaterial = Status is SlotOccupancyStatus.Reserved
            or SlotOccupancyStatus.Occupied
            or SlotOccupancyStatus.Running;
        if (requiresMaterial != (Material is not null))
        {
            throw new InvalidOperationException(
                $"Slot status {Status} has an inconsistent material binding.");
        }
    }
}

public sealed record SlotOccupancySnapshot(
    SlotAddress Address,
    SlotOccupancyStatus Status,
    MaterialReference? Material,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc);
