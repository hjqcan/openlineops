using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Materials;

namespace OpenLineOps.Runtime.Api.Models;

internal static class ProductionLineRuntimeStateResponseMapper
{
    public static ProductionLineRuntimeStateResponse ToResponse(ProductionLineRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ProductionLineRuntimeStateResponse(
            state.ProductionLineDefinitionId,
            state.GeneratedAtUtc,
            state.ActiveRuns.Count,
            state.ActiveRuns.Select(ProductionRunReadModelMapper.ToReadModel).ToArray(),
            state.ProductionUnits.Select(unit => new ProductionLineProductionUnitStateResponse(
                unit.ProductionUnitId.Value,
                unit.ProductModelId,
                unit.IdentityKey,
                unit.IdentityValue,
                unit.Disposition.ToString(),
                unit.Judgement.ToString(),
                unit.ProductionRunId?.Value,
                ToLocationResponse(unit.Location),
                unit.LastTransitionAtUtc,
                unit.ActiveOperationRunIds)).ToArray(),
            state.Stations.Select(station => new ProductionLineStationStateResponse(
                station.StationSystemId,
                station.Status.ToString(),
                station.Queue.Select(material => new ProductionLineQueuedMaterialResponse(
                    material.MaterialKind.ToString(),
                    material.MaterialId,
                    material.QueuedAtUtc)).ToArray(),
                station.ActiveOperations.Select(operation =>
                    new ProductionLineStationOperationStateResponse(
                        operation.ProductionRunId.Value,
                        operation.ProductionUnitId?.Value,
                        new RuntimeProductionUnitIdentityResponse(
                            operation.ProductionUnitIdentity.ModelId,
                            operation.ProductionUnitIdentity.InputKey,
                            operation.ProductionUnitIdentity.Value),
                        operation.OperationRunId,
                        operation.OperationId,
                        operation.ExecutionStatus.ToString(),
                        operation.Judgement.ToString(),
                        operation.StartedAtUtc,
                        operation.Resources.Select(resource =>
                            new ProductionLineResourceStateResponse(
                                resource.Kind.ToString(),
                                resource.ResourceId,
                                resource.Status.ToString(),
                                resource.FencingToken,
                                resource.AcquiredAtUtc,
                                resource.ExpiresAtUtc)).ToArray())).ToArray())).ToArray(),
            state.Slots.Select(slot => new ProductionLineSlotStateResponse(
                slot.StationSystemId,
                slot.SlotId,
                slot.Status.ToString(),
                slot.Material?.Kind.ToString(),
                slot.Material?.Value,
                slot.LastTransitionAtUtc)).ToArray(),
            state.Carriers.Select(carrier => new ProductionLineCarrierStateResponse(
                carrier.CarrierId,
                carrier.CarrierTypeId,
                carrier.Capacity,
                ToLocationResponse(carrier.Location),
                carrier.LastTransitionAtUtc,
                carrier.ProductionUnits.Select(unit =>
                    new ProductionLineCarrierPositionStateResponse(
                        unit.CarrierPositionId,
                        unit.ProductionUnitId.Value,
                        unit.Disposition.ToString(),
                        unit.Judgement.ToString())).ToArray())).ToArray());
    }

    private static MaterialLocationApiResponse? ToLocationResponse(MaterialLocation? location) =>
        location is null
            ? null
            : new MaterialLocationApiResponse(
                location.Kind.ToString(),
                location.LineId,
                location.StationSystemId,
                location.SlotId,
                location.CarrierId?.Value,
                location.CarrierPositionId);
}
