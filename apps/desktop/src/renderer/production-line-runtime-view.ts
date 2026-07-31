import type {
  ProductionLineCarrierPositionStateResponse,
  ProductionLineCarrierStateResponse,
  ProductionLineProductionUnitStateResponse,
  ProductionLineQueuedMaterialResponse,
  ProductionLineResourceStateResponse,
  ProductionLineRuntimeStateResponse,
  ProductionLineSlotStateResponse,
  ProductionLineStationOperationStateResponse,
  ProductionLineStationStateResponse,
  ProductionMaterialKind
} from './contracts';

export interface ProductionLineMaterialView {
  kind: ProductionMaterialKind;
  id: string;
  label: string;
}

export interface ProductionLineQueuedMaterialView extends ProductionLineMaterialView {
  queuedAtUtc: string;
}

export interface ProductionLineSlotView extends ProductionLineSlotStateResponse {
  materialLabel: string | null;
}

export interface ProductionLineCarrierPositionView extends ProductionLineCarrierPositionStateResponse {
  productionUnitLabel: string;
}

export interface ProductionLineCarrierView extends Omit<ProductionLineCarrierStateResponse, 'productionUnits'> {
  productionUnits: ProductionLineCarrierPositionView[];
}

export interface ProductionLineOperationView extends ProductionLineStationOperationStateResponse {
  productionUnitLabel: string;
}

export interface ProductionLineResourceView extends ProductionLineResourceStateResponse {
  operationRunId: string;
  operationId: string;
}

export interface ProductionLineStationView {
  stationSystemId: string;
  status: ProductionLineStationStateResponse['status'];
  agentId: string | null;
  stationId: string | null;
  agentPresenceSessionId: string | null;
  agentPresenceSequence: number | null;
  agentPresenceState: ProductionLineStationStateResponse['agentPresenceState'];
  agentPresenceHealth: ProductionLineStationStateResponse['agentPresenceHealth'];
  agentPresenceLastSeenAtUtc: string | null;
  agentPresenceAgeSeconds: number | null;
  queue: ProductionLineQueuedMaterialView[];
  activeOperations: ProductionLineOperationView[];
  productionUnits: ProductionLineProductionUnitStateResponse[];
  slots: ProductionLineSlotView[];
  carriers: ProductionLineCarrierView[];
  resources: ProductionLineResourceView[];
}

export interface ProductionLineRuntimeView {
  stations: ProductionLineStationView[];
  carriers: ProductionLineCarrierView[];
  productionUnitById: ReadonlyMap<string, ProductionLineProductionUnitStateResponse>;
}

export function buildProductionLineRuntimeView(
  state: ProductionLineRuntimeStateResponse
): ProductionLineRuntimeView {
  const productionUnitById = new Map(
    state.productionUnits.map(unit => [unit.productionUnitId, unit]));
  const carriers = state.carriers.map(carrier => toCarrierView(carrier, productionUnitById));
  const carrierViewById = new Map(carriers.map(carrier => [carrier.carrierId, carrier]));

  const stations = state.stations.map(station => {
    const stationCarriers = carriers.filter(carrier =>
      isLocatedAtStation(carrier.location, station.stationSystemId));
    const stationCarrierIds = new Set(stationCarriers.map(carrier => carrier.carrierId));
    const productionUnits = state.productionUnits.filter(unit =>
      isLocatedAtStation(unit.location, station.stationSystemId)
      || (unit.location?.kind === 'CarrierPosition'
        && unit.location.carrierId !== null
        && stationCarrierIds.has(unit.location.carrierId)));
    const slots = state.slots
      .filter(slot => slot.stationSystemId === station.stationSystemId)
      .map(slot => ({
        ...slot,
        materialLabel: slot.materialKind && slot.materialId
          ? resolveMaterialLabel(slot.materialKind, slot.materialId, productionUnitById)
          : null
      }));
    const activeOperations = station.activeOperations.map(operation => ({
      ...operation,
      productionUnitLabel: operation.productionUnitId
        ? productionUnitById.get(operation.productionUnitId)?.identityValue
          ?? operation.productionUnitIdentity.value
        : operation.productionUnitIdentity.value
    }));
    const resources = activeOperations.flatMap(operation => operation.resources.map(resource => ({
      ...resource,
      operationRunId: operation.operationRunId,
      operationId: operation.operationId
    })));
    return {
      stationSystemId: station.stationSystemId,
      status: station.status,
      agentId: station.agentId,
      stationId: station.stationId,
      agentPresenceSessionId: station.agentPresenceSessionId,
      agentPresenceSequence: station.agentPresenceSequence,
      agentPresenceState: station.agentPresenceState,
      agentPresenceHealth: station.agentPresenceHealth,
      agentPresenceLastSeenAtUtc: station.agentPresenceLastSeenAtUtc,
      agentPresenceAgeSeconds: station.agentPresenceAgeSeconds,
      queue: station.queue.map(material => toQueuedMaterialView(material, productionUnitById)),
      activeOperations,
      productionUnits,
      slots,
      carriers: stationCarriers,
      resources
    };
  });

  return {
    stations,
    carriers: state.carriers.map(carrier =>
      carrierViewById.get(carrier.carrierId) ?? toCarrierView(carrier, productionUnitById)),
    productionUnitById
  };
}

export function resolveMaterialLabel(
  kind: ProductionMaterialKind,
  id: string,
  productionUnitById: ReadonlyMap<string, ProductionLineProductionUnitStateResponse>
): string {
  return kind === 'ProductionUnit'
    ? productionUnitById.get(id)?.identityValue ?? id
    : id;
}

function toQueuedMaterialView(
  material: ProductionLineQueuedMaterialResponse,
  productionUnitById: ReadonlyMap<string, ProductionLineProductionUnitStateResponse>
): ProductionLineQueuedMaterialView {
  return {
    kind: material.materialKind,
    id: material.materialId,
    label: resolveMaterialLabel(material.materialKind, material.materialId, productionUnitById),
    queuedAtUtc: material.queuedAtUtc
  };
}

function toCarrierView(
  carrier: ProductionLineCarrierStateResponse,
  productionUnitById: ReadonlyMap<string, ProductionLineProductionUnitStateResponse>
): ProductionLineCarrierView {
  return {
    ...carrier,
    productionUnits: carrier.productionUnits.map(position => ({
      ...position,
      productionUnitLabel: productionUnitById.get(position.productionUnitId)?.identityValue
        ?? position.productionUnitId
    }))
  };
}

function isLocatedAtStation(
  location: ProductionLineProductionUnitStateResponse['location'],
  stationSystemId: string
): boolean {
  return location !== null
    && (location.kind === 'StationQueue' || location.kind === 'Slot')
    && location.stationSystemId === stationSystemId;
}
