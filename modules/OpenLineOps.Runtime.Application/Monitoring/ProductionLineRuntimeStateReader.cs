using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Monitoring;

public interface IProductionLineRuntimeStateReader
{
    ValueTask<ProductionLineRuntimeState> ReadAsync(
        string productionLineDefinitionId,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionLineRuntimeStateReader(
    IProductionRunRepository productionRuns,
    IProductionMaterialRepository materials,
    IResourceLeaseRepository resourceLeases,
    IAgentPresenceRepository agentPresences,
    AgentPresenceMonitoringOptions presenceOptions,
    IClock clock) : IProductionLineRuntimeStateReader
{
    public async ValueTask<ProductionLineRuntimeState> ReadAsync(
        string productionLineDefinitionId,
        CancellationToken cancellationToken = default)
    {
        RequireCanonical(productionLineDefinitionId, nameof(productionLineDefinitionId));
        presenceOptions.Validate();
        var generatedAtUtc = clock.UtcNow;
        var runTask = productionRuns.ListActiveAsync(
            productionLineDefinitionId: productionLineDefinitionId,
            cancellationToken: cancellationToken).AsTask();
        var unitTask = materials.ListProductionUnitsAsync(cancellationToken).AsTask();
        var carrierTask = materials.ListCarriersAsync(cancellationToken).AsTask();
        var slotTask = materials.ListSlotsAsync(
            lineId: productionLineDefinitionId,
            cancellationToken: cancellationToken).AsTask();
        var leaseTask = resourceLeases.ListAsync(cancellationToken).AsTask();
        var presenceTask = agentPresences.ListAsync(cancellationToken).AsTask();
        await Task.WhenAll(runTask, unitTask, carrierTask, slotTask, leaseTask, presenceTask)
            .ConfigureAwait(false);

        var runs = runTask.Result
            .Select(static entry => entry.Run.ToSnapshot())
            .OrderBy(static run => run.CreatedAtUtc)
            .ThenBy(static run => run.RunId.Value)
            .ToArray();
        var allUnits = unitTask.Result.Select(static entry => entry.Aggregate).ToArray();
        var allCarriers = carrierTask.Result.Select(static entry => entry.Aggregate).ToArray();
        var slots = slotTask.Result
            .Select(static entry => entry.Aggregate)
            .OrderBy(static slot => slot.Address.StationSystemId, StringComparer.Ordinal)
            .ThenBy(static slot => slot.Address.SlotId, StringComparer.Ordinal)
            .Select(ToSlotState)
            .ToArray();

        var carrierIds = SelectLineCarrierIds(
            productionLineDefinitionId,
            runs,
            allCarriers,
            slots);
        var carriers = allCarriers
            .Where(carrier => carrierIds.Contains(carrier.Id.Value))
            .OrderBy(static carrier => carrier.Id.Value, StringComparer.Ordinal)
            .Select(carrier => ToCarrierState(carrier, allUnits, runs))
            .ToArray();

        var units = allUnits
            .Where(IsActive)
            .Where(unit => IsOnLine(unit, productionLineDefinitionId, carrierIds)
                || FindRun(unit, runs) is not null)
            .OrderBy(static unit => unit.RegisteredAtUtc)
            .ThenBy(static unit => unit.Id.Value)
            .Select(unit => ToUnitState(unit, runs))
            .ToArray();

        var leases = leaseTask.Result
            .Where(lease => runs.Any(run => run.RunId == lease.ProductionRunId))
            .ToDictionary(
                static lease => new LeaseOwnerKey(
                    lease.ProductionRunId,
                    lease.OperationRunId,
                    lease.Resource),
                static lease => lease);
        var stations = BuildStationStates(
            productionLineDefinitionId,
            generatedAtUtc,
            runs,
            units,
            carriers,
            slots,
            allUnits,
            leases,
            presenceTask.Result,
            presenceOptions.TimeToLive,
            presenceOptions.PresenceRequired);

        return new ProductionLineRuntimeState(
            productionLineDefinitionId,
            generatedAtUtc,
            runs,
            units,
            stations,
            slots,
            carriers);
    }

    private static ProductionLineStationState[] BuildStationStates(
        string lineId,
        DateTimeOffset generatedAtUtc,
        IReadOnlyCollection<ProductionRunSnapshot> runs,
        IReadOnlyCollection<ProductionLineProductionUnitState> units,
        IReadOnlyCollection<ProductionLineCarrierState> carriers,
        IReadOnlyCollection<ProductionLineSlotState> slots,
        IReadOnlyCollection<ProductionUnit> allUnits,
        IReadOnlyDictionary<LeaseOwnerKey, ResourceLease> leases,
        IReadOnlyCollection<AgentPresenceSnapshot> agentPresences,
        TimeSpan presenceTimeToLive,
        bool presenceRequired)
    {
        var stationIds = slots.Select(static slot => slot.StationSystemId)
            .Concat(units
                .Where(unit => unit.Location is
                { Kind: MaterialLocationKind.StationQueue } location
                    && string.Equals(location.LineId, lineId, StringComparison.Ordinal))
                .Select(unit => unit.Location!.StationSystemId!))
            .Concat(carriers
                .Where(carrier => carrier.Location is
                { Kind: MaterialLocationKind.StationQueue } location
                    && string.Equals(location.LineId, lineId, StringComparison.Ordinal))
                .Select(carrier => carrier.Location!.StationSystemId!))
            .Concat(runs.SelectMany(static run =>
                run.OperationDefinitions.Select(static definition => definition.StationSystemId)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static stationId => stationId, StringComparer.Ordinal)
            .ToArray();

        return stationIds.Select(stationId =>
        {
            var presence = agentPresences
                .Where(candidate => string.Equals(
                    candidate.StationSystemId,
                    stationId,
                    StringComparison.Ordinal))
                .OrderByDescending(static candidate => candidate.ReceivedAtUtc)
                .ThenBy(static candidate => candidate.AgentId, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.StationId, StringComparer.Ordinal)
                .FirstOrDefault();
            var presenceAge = presence is null
                ? (TimeSpan?)null
                : generatedAtUtc >= presence.ReceivedAtUtc
                    ? generatedAtUtc - presence.ReceivedAtUtc
                    : TimeSpan.Zero;
            var presenceHealth = !presenceRequired
                ? ProductionLineAgentPresenceHealth.NotApplicable
                : presence switch
                {
                    null => ProductionLineAgentPresenceHealth.Missing,
                    { State: AgentPresenceState.Stopping } =>
                        ProductionLineAgentPresenceHealth.Stopping,
                    _ when presenceAge > presenceTimeToLive =>
                        ProductionLineAgentPresenceHealth.Expired,
                    _ => ProductionLineAgentPresenceHealth.Online
                };
            var stationSlots = slots
                .Where(slot => string.Equals(
                    slot.StationSystemId,
                    stationId,
                    StringComparison.Ordinal))
                .ToArray();
            var queue = units
                .Where(unit => IsQueuedAt(unit.Location, lineId, stationId))
                .Select(unit => new ProductionLineQueuedMaterial(
                    MaterialKind.ProductionUnit,
                    unit.ProductionUnitId.ToString(),
                    unit.LastTransitionAtUtc))
                .Concat(carriers
                    .Where(carrier => IsQueuedAt(carrier.Location, lineId, stationId))
                    .Select(carrier => new ProductionLineQueuedMaterial(
                        MaterialKind.Carrier,
                        carrier.CarrierId,
                        carrier.LastTransitionAtUtc)))
                .OrderBy(static material => material.QueuedAtUtc)
                .ThenBy(static material => material.MaterialKind)
                .ThenBy(static material => material.MaterialId, StringComparer.Ordinal)
                .ToArray();
            var operations = runs.SelectMany(run => run.Operations
                    .Where(operation => !IsTerminal(operation.ExecutionStatus)
                        && string.Equals(
                            operation.Definition.StationSystemId,
                            stationId,
                            StringComparison.Ordinal))
                    .Select(operation => ToStationOperation(
                        run,
                        operation,
                        allUnits,
                        leases,
                        generatedAtUtc)))
                .OrderBy(static operation => operation.StartedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(static operation => operation.ProductionRunId)
                .ThenBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
                .ToArray();
            var operationalStatus = operations.Any(operation =>
                    operation.ExecutionStatus == ExecutionStatus.Running)
                ? ProductionLineStationRuntimeStatus.Running
                : operations.Length > 0
                    ? ProductionLineStationRuntimeStatus.WaitingForResources
                    : queue.Length > 0
                        ? ProductionLineStationRuntimeStatus.Queued
                        : stationSlots.Length > 0
                            && stationSlots.All(slot => slot.Status == SlotOccupancyStatus.Offline)
                                ? ProductionLineStationRuntimeStatus.Offline
                                : stationSlots.Length > 0
                                    && stationSlots.All(slot => slot.Status is
                                        SlotOccupancyStatus.Blocked or SlotOccupancyStatus.Offline)
                                        ? ProductionLineStationRuntimeStatus.Blocked
                                        : ProductionLineStationRuntimeStatus.Idle;
            var status = presenceHealth is ProductionLineAgentPresenceHealth.Online
                or ProductionLineAgentPresenceHealth.NotApplicable
                ? operationalStatus
                : ProductionLineStationRuntimeStatus.Offline;
            return new ProductionLineStationState(
                stationId,
                status,
                presence?.AgentId,
                presence?.StationId,
                presence?.SessionId,
                presence?.Sequence,
                presence?.State,
                presenceHealth,
                presence?.ReceivedAtUtc,
                presenceAge,
                queue,
                operations);
        }).ToArray();
    }

    private static ProductionLineStationOperationState ToStationOperation(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        IReadOnlyCollection<ProductionUnit> allUnits,
        IReadOnlyDictionary<LeaseOwnerKey, ResourceLease> leases,
        DateTimeOffset generatedAtUtc)
    {
        var unit = FindUnit(run, allUnits);
        var resources = operation.Definition.ResourceRequirements
            .Concat(operation.FencingTokens.Keys)
            .Distinct()
            .Select(requirement => ToResourceState(
                run,
                operation,
                requirement,
                leases,
                generatedAtUtc))
            .ToArray();
        return new ProductionLineStationOperationState(
            run.RunId,
            unit?.Id,
            run.ProductionUnitIdentity,
            operation.OperationRunId,
            operation.Definition.OperationId,
            operation.ExecutionStatus,
            operation.Judgement,
            operation.StartedAtUtc,
            resources);
    }

    private static ProductionLineResourceState ToResourceState(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        ResourceRequirement requirement,
        IReadOnlyDictionary<LeaseOwnerKey, ResourceLease> leases,
        DateTimeOffset generatedAtUtc)
    {
        if (operation.ExecutionStatus == ExecutionStatus.Pending)
        {
            return new ProductionLineResourceState(
                requirement.Kind,
                requirement.ResourceId,
                ProductionLineResourceRuntimeStatus.Waiting,
                null,
                null,
                null);
        }

        var key = new LeaseOwnerKey(run.RunId, operation.OperationRunId, requirement);
        var snapshotToken = operation.FencingTokens.GetValueOrDefault(requirement);
        if (!leases.TryGetValue(key, out var lease))
        {
            return new ProductionLineResourceState(
                requirement.Kind,
                requirement.ResourceId,
                ProductionLineResourceRuntimeStatus.Missing,
                snapshotToken > 0 ? snapshotToken : null,
                null,
                null);
        }

        var status = lease.ExpiresAtUtc == DateTimeOffset.MaxValue
            ? ProductionLineResourceRuntimeStatus.RecoveryHeld
            : lease.ExpiresAtUtc <= generatedAtUtc
                ? ProductionLineResourceRuntimeStatus.Expired
                : ProductionLineResourceRuntimeStatus.Leased;
        return new ProductionLineResourceState(
            requirement.Kind,
            requirement.ResourceId,
            status,
            lease.FencingToken,
            lease.AcquiredAtUtc,
            lease.ExpiresAtUtc);
    }

    private static ProductionLineProductionUnitState ToUnitState(
        ProductionUnit unit,
        IReadOnlyCollection<ProductionRunSnapshot> runs)
    {
        var run = FindRun(unit, runs);
        return new ProductionLineProductionUnitState(
            unit.Id,
            unit.ProductModelId,
            unit.IdentityKey,
            unit.IdentityValue,
            unit.Disposition,
            run is null ? ResultJudgement.Unknown : ResolveCurrentJudgement(run),
            run?.RunId,
            unit.Location,
            unit.LastTransitionAtUtc,
            run?.Operations
                .Where(operation => !IsTerminal(operation.ExecutionStatus))
                .Select(static operation => operation.OperationRunId)
                .OrderBy(static operationRunId => operationRunId, StringComparer.Ordinal)
                .ToArray() ?? []);
    }

    private static ProductionLineCarrierState ToCarrierState(
        Carrier carrier,
        IReadOnlyCollection<ProductionUnit> units,
        IReadOnlyCollection<ProductionRunSnapshot> runs)
    {
        var positions = units
            .Where(unit => unit.Location is
            { Kind: MaterialLocationKind.CarrierPosition } location
                && Equals(location.CarrierId, carrier.Id))
            .OrderBy(unit => unit.Location!.CarrierPositionId, StringComparer.Ordinal)
            .Select(unit => new ProductionLineCarrierPositionState(
                unit.Location!.CarrierPositionId!,
                unit.Id,
                unit.Disposition,
                FindRun(unit, runs) is { } run
                    ? ResolveCurrentJudgement(run)
                    : ResultJudgement.Unknown))
            .ToArray();
        return new ProductionLineCarrierState(
            carrier.Id.Value,
            carrier.CarrierTypeId,
            carrier.Capacity,
            carrier.Location,
            carrier.LastTransitionAtUtc,
            positions);
    }

    private static ProductionLineSlotState ToSlotState(
        SlotOccupancy slot) => new(
            slot.Address.StationSystemId,
            slot.Address.SlotId,
            slot.Status,
            slot.Material,
            slot.LastTransitionAtUtc);

    private static HashSet<string> SelectLineCarrierIds(
        string lineId,
        IReadOnlyCollection<ProductionRunSnapshot> runs,
        IReadOnlyCollection<Carrier> carriers,
        IReadOnlyCollection<ProductionLineSlotState> slots)
    {
        var ids = carriers
            .Where(carrier => carrier.Location?.LineId is { } carrierLineId
                && string.Equals(carrierLineId, lineId, StringComparison.Ordinal))
            .Select(static carrier => carrier.Id.Value)
            .Concat(slots
                .Where(static slot => slot.Material?.Kind == MaterialKind.Carrier)
                .Select(static slot => slot.Material!.Value))
            .Concat(runs
                .Where(static run => run.CarrierId is not null)
                .Select(static run => run.CarrierId!));
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static ProductionRunSnapshot? FindRun(
        ProductionUnit unit,
        IReadOnlyCollection<ProductionRunSnapshot> runs) =>
        runs.Where(run => run.ProductionUnitId == unit.Id)
            .OrderByDescending(static run => run.LastTransitionAtUtc)
            .ThenByDescending(static run => run.RunId.Value)
            .FirstOrDefault();

    private static ResultJudgement ResolveCurrentJudgement(ProductionRunSnapshot run)
    {
        if (run.Judgement != ResultJudgement.Unknown)
        {
            return run.Judgement;
        }

        return run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Completed
                && operation.Judgement != ResultJudgement.Unknown)
            .OrderByDescending(static operation => operation.CompletedAtUtc)
            .ThenByDescending(static operation => operation.Attempt)
            .Select(static operation => operation.Judgement)
            .FirstOrDefault(ResultJudgement.Unknown);
    }

    private static ProductionUnit? FindUnit(
        ProductionRunSnapshot run,
        IReadOnlyCollection<ProductionUnit> units) =>
        units.SingleOrDefault(unit => unit.Id == run.ProductionUnitId);

    private static bool IsOnLine(
        ProductionUnit unit,
        string lineId,
        HashSet<string> lineCarrierIds) =>
        unit.Location switch
        {
            { Kind: MaterialLocationKind.StationQueue or MaterialLocationKind.Slot } location =>
                string.Equals(location.LineId, lineId, StringComparison.Ordinal),
            { Kind: MaterialLocationKind.CarrierPosition } location =>
                location.CarrierId is not null && lineCarrierIds.Contains(location.CarrierId.Value),
            _ => false
        };

    private static bool IsActive(ProductionUnit unit) => unit.Disposition is
        ProductDisposition.InProcess
        or ProductDisposition.Nonconforming
        or ProductDisposition.Held;

    private static bool IsQueuedAt(MaterialLocation? location, string lineId, string stationId) =>
        location is { Kind: MaterialLocationKind.StationQueue }
        && string.Equals(location.LineId, lineId, StringComparison.Ordinal)
        && string.Equals(location.StationSystemId, stationId, StringComparison.Ordinal);

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private static void RequireCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName);
        }
    }

    private sealed record LeaseOwnerKey(
        ProductionRunId RunId,
        string OperationRunId,
        ResourceRequirement Resource);
}

public sealed record ProductionLineRuntimeState(
    string ProductionLineDefinitionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ProductionRunSnapshot> ActiveRuns,
    IReadOnlyList<ProductionLineProductionUnitState> ProductionUnits,
    IReadOnlyList<ProductionLineStationState> Stations,
    IReadOnlyList<ProductionLineSlotState> Slots,
    IReadOnlyList<ProductionLineCarrierState> Carriers);

public sealed record ProductionLineProductionUnitState(
    ProductionUnitId ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    ProductDisposition Disposition,
    ResultJudgement Judgement,
    ProductionRunId? ProductionRunId,
    MaterialLocation? Location,
    DateTimeOffset LastTransitionAtUtc,
    IReadOnlyList<string> ActiveOperationRunIds);

public enum ProductionLineStationRuntimeStatus
{
    Idle,
    Queued,
    WaitingForResources,
    Running,
    Blocked,
    Offline
}

public sealed record ProductionLineStationState(
    string StationSystemId,
    ProductionLineStationRuntimeStatus Status,
    string? AgentId,
    string? StationId,
    Guid? AgentPresenceSessionId,
    long? AgentPresenceSequence,
    AgentPresenceState? AgentPresenceState,
    ProductionLineAgentPresenceHealth AgentPresenceHealth,
    DateTimeOffset? AgentPresenceLastSeenAtUtc,
    TimeSpan? AgentPresenceAge,
    IReadOnlyList<ProductionLineQueuedMaterial> Queue,
    IReadOnlyList<ProductionLineStationOperationState> ActiveOperations);

public enum ProductionLineAgentPresenceHealth
{
    NotApplicable,
    Missing,
    Online,
    Expired,
    Stopping
}

public sealed record ProductionLineQueuedMaterial(
    MaterialKind MaterialKind,
    string MaterialId,
    DateTimeOffset QueuedAtUtc);

public sealed record ProductionLineStationOperationState(
    ProductionRunId ProductionRunId,
    ProductionUnitId? ProductionUnitId,
    ProductionUnitIdentity ProductionUnitIdentity,
    string OperationRunId,
    string OperationId,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    DateTimeOffset? StartedAtUtc,
    IReadOnlyList<ProductionLineResourceState> Resources);

public enum ProductionLineResourceRuntimeStatus
{
    Waiting,
    Leased,
    RecoveryHeld,
    Expired,
    Missing
}

public sealed record ProductionLineResourceState(
    ResourceKind Kind,
    string ResourceId,
    ProductionLineResourceRuntimeStatus Status,
    long? FencingToken,
    DateTimeOffset? AcquiredAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ProductionLineSlotState(
    string StationSystemId,
    string SlotId,
    SlotOccupancyStatus Status,
    MaterialReference? Material,
    DateTimeOffset LastTransitionAtUtc);

public sealed record ProductionLineCarrierState(
    string CarrierId,
    string CarrierTypeId,
    int Capacity,
    MaterialLocation? Location,
    DateTimeOffset LastTransitionAtUtc,
    IReadOnlyList<ProductionLineCarrierPositionState> ProductionUnits);

public sealed record ProductionLineCarrierPositionState(
    string CarrierPositionId,
    ProductionUnitId ProductionUnitId,
    ProductDisposition Disposition,
    ResultJudgement Judgement);
