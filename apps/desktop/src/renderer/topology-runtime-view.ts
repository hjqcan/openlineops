import type {
  AutomationTopologyResponse,
  ProductionLineRuntimeStateResponse
} from './contracts';
import { buildProductionLineRuntimeView } from './production-line-runtime-view';

export type OperationalState = 'Idle' | 'Running' | 'Completed' | 'Failed' | 'Offline';
export type RuntimeSlotState =
  | 'Available'
  | 'Reserved'
  | 'Occupied'
  | 'Running'
  | 'Blocked'
  | 'Offline';

export interface TopologyRuntimeStatus {
  operationalState: OperationalState;
  products: string[];
  queueCount: number;
  operationIds: string[];
  slotState?: RuntimeSlotState;
}

export interface TopologySlotRuntimeStatus extends TopologyRuntimeStatus {
  slotId: string;
  stationSystemId: string;
  slotState: RuntimeSlotState;
  fencingToken: number | null;
  materialKind: 'ProductionUnit' | 'Carrier' | null;
  materialId: string | null;
}

export interface TopologyRuntimeView {
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
  slots: TopologySlotRuntimeStatus[];
}

export function runtimeTargetStatusKey(
  stationSystemId: string,
  targetKind: string,
  targetId: string
): string {
  return JSON.stringify([stationSystemId, targetKind, targetId]);
}

export function toTargetOperationalState(
  status: TopologyRuntimeStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }
  return status?.operationalState ?? 'Idle';
}

export function describeRuntimeMonitorState(
  statuses: TopologyRuntimeStatus[],
  activeRunCount: number
): string {
  if (activeRunCount > 0) {
    const queued = statuses.reduce((total, status) => total + status.queueCount, 0);
    return `${activeRunCount} active product${activeRunCount === 1 ? '' : 's'} · ${queued} queued`;
  }
  return 'Line idle · projection connected';
}

export function toOperationalState(
  status: TopologyRuntimeStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }
  return status?.operationalState ?? 'Idle';
}

export function emptyTopologyRuntimeView(): TopologyRuntimeView {
  return {
    stationStatuses: new Map(),
    targetStatusByKey: new Map(),
    slots: []
  };
}

export function buildTopologyRuntimeView(
  topology: AutomationTopologyResponse | null,
  lineState: ProductionLineRuntimeStateResponse | null
): TopologyRuntimeView {
  if (!topology) {
    return emptyTopologyRuntimeView();
  }

  const productionView = lineState ? buildProductionLineRuntimeView(lineState) : null;
  const stationViewById = new Map(
    (productionView?.stations ?? []).map(station => [station.stationSystemId, station]));
  const stationIds = unique([
    ...topology.systems
      .filter(system => system.kind === 'Station')
      .map(station => station.systemId),
    ...(lineState?.stations.map(station => station.stationSystemId) ?? [])
  ]);
  const stationStatuses = new Map<string, TopologyRuntimeStatus>();
  for (const stationSystemId of stationIds) {
    const station = stationViewById.get(stationSystemId);
    stationStatuses.set(stationSystemId, {
      operationalState: station
        ? stationOperationalState(station.status)
        : 'Idle',
      products: unique([
        ...(station?.productionUnits.map(unit => unit.identityValue) ?? []),
        ...(station?.activeOperations.map(operation => operation.productionUnitLabel) ?? [])
      ]),
      queueCount: station?.queue.length ?? 0,
      operationIds: unique(station?.activeOperations.map(operation => operation.operationId) ?? [])
    });
  }

  const productionUnitById = productionView?.productionUnitById ?? new Map();
  const persistedSlotByKey = new Map((lineState?.slots ?? []).map(slot => [
    slotRuntimeKey(slot.stationSystemId, slot.slotId),
    slot
  ]));
  const slots = new Map<string, TopologySlotRuntimeStatus>();
  const topologySlotKeys = new Set<string>();
  for (const slot of topology.slots) {
    const stationSystemId = findStationAncestor(slot.parentSystemId, topology) ?? slot.parentSystemId;
    const key = slotRuntimeKey(stationSystemId, slot.slotId);
    topologySlotKeys.add(key);
    const persisted = persistedSlotByKey.get(key);
    const slotState: RuntimeSlotState = persisted?.status
      ?? (slot.isEnabled ? 'Available' : 'Offline');
    const resources = stationViewById.get(stationSystemId)?.resources
      .filter(resource => resource.kind === 'Slot' && resource.resourceId === slot.slotId)
      ?? [];
    const fencingToken = maxFencingToken(resources.map(resource => resource.fencingToken));
    slots.set(key, {
      slotId: slot.slotId,
      stationSystemId,
      slotState,
      operationalState: slotOperationalState(slotState),
      products: persisted?.materialKind && persisted.materialId
        ? [persisted.materialKind === 'ProductionUnit'
          ? productionUnitById.get(persisted.materialId)?.identityValue ?? persisted.materialId
          : persisted.materialId]
        : [],
      queueCount: slotState === 'Reserved' ? 1 : 0,
      operationIds: unique(resources.map(resource => resource.operationId)),
      fencingToken,
      materialKind: persisted?.materialKind ?? null,
      materialId: persisted?.materialId ?? null
    });
  }
  for (const persisted of lineState?.slots ?? []) {
    const key = slotRuntimeKey(persisted.stationSystemId, persisted.slotId);
    if (topologySlotKeys.has(key)) {
      continue;
    }
    const resources = stationViewById.get(persisted.stationSystemId)?.resources
      .filter(resource => resource.kind === 'Slot' && resource.resourceId === persisted.slotId)
      ?? [];
    slots.set(key, {
      slotId: persisted.slotId,
      stationSystemId: persisted.stationSystemId,
      slotState: persisted.status,
      operationalState: slotOperationalState(persisted.status),
      products: persisted.materialKind && persisted.materialId
        ? [persisted.materialKind === 'ProductionUnit'
          ? productionUnitById.get(persisted.materialId)?.identityValue ?? persisted.materialId
          : persisted.materialId]
        : [],
      queueCount: persisted.status === 'Reserved' ? 1 : 0,
      operationIds: unique(resources.map(resource => resource.operationId)),
      fencingToken: maxFencingToken(resources.map(resource => resource.fencingToken)),
      materialKind: persisted.materialKind,
      materialId: persisted.materialId
    });
  }

  const targetStatusByKey = new Map<string, TopologyRuntimeStatus>();
  for (const slot of slots.values()) {
    targetStatusByKey.set(
      runtimeTargetStatusKey(slot.stationSystemId, 'Slot', slot.slotId),
      slot);
  }
  for (const group of topology.slotGroups) {
    const stationSystemId = findStationAncestor(group.parentSystemId, topology) ?? group.parentSystemId;
    const groupSlots = group.slotIds
      .map(slotId => slots.get(slotRuntimeKey(stationSystemId, slotId)))
      .filter((slot): slot is TopologySlotRuntimeStatus => slot !== undefined);
    const groupState = groupSlots.reduce<OperationalState>(
      (current, slot) => operationalStatePriority(slot.operationalState) > operationalStatePriority(current)
        ? slot.operationalState
        : current,
      'Idle');
    targetStatusByKey.set(
      runtimeTargetStatusKey(
        stationSystemId,
        'SlotGroup',
        group.slotGroupId),
      {
        operationalState: groupState,
        products: unique(groupSlots.flatMap(slot => slot.products)),
        queueCount: groupSlots.reduce((total, slot) => total + slot.queueCount, 0),
        operationIds: unique(groupSlots.flatMap(slot => slot.operationIds))
      });
  }
  for (const system of topology.systems.filter(candidate => candidate.kind === 'System')) {
    const stationSystemId = findStationAncestor(system.systemId, topology);
    if (!stationSystemId) {
      continue;
    }
    const stationStatus = stationStatuses.get(stationSystemId);
    if (stationStatus) {
      targetStatusByKey.set(
        runtimeTargetStatusKey(stationSystemId, 'System', system.systemId),
        stationStatus);
    }
  }

  return {
    stationStatuses,
    targetStatusByKey,
    slots: [...slots.values()].sort((left, right) => (
      left.stationSystemId.localeCompare(right.stationSystemId)
      || left.slotId.localeCompare(right.slotId)))
  };
}

function slotRuntimeKey(stationSystemId: string, slotId: string): string {
  return JSON.stringify([stationSystemId, slotId]);
}

function maxFencingToken(tokens: Array<number | null>): number | null {
  return tokens.reduce<number | null>((highest, token) => (
    token !== null && (highest === null || token > highest) ? token : highest), null);
}

function stationOperationalState(
  status: ProductionLineRuntimeStateResponse['stations'][number]['status']
): OperationalState {
  switch (status) {
    case 'Running':
    case 'Queued':
    case 'WaitingForResources':
      return 'Running';
    case 'Blocked':
      return 'Failed';
    case 'Offline':
      return 'Offline';
    default:
      return 'Idle';
  }
}

function slotOperationalState(state: RuntimeSlotState): OperationalState {
  switch (state) {
    case 'Running':
    case 'Reserved':
    case 'Occupied':
      return 'Running';
    case 'Blocked':
      return 'Failed';
    case 'Offline':
      return 'Offline';
    default:
      return 'Idle';
  }
}

function operationalStatePriority(state: OperationalState): number {
  return { Idle: 0, Completed: 1, Running: 2, Offline: 3, Failed: 4 }[state];
}

function findStationAncestor(systemId: string, topology: AutomationTopologyResponse): string | null {
  const systemById = new Map(topology.systems.map(system => [system.systemId, system]));
  let current = systemById.get(systemId);
  while (current) {
    if (current.kind === 'Station') {
      return current.systemId;
    }
    current = current.parentSystemId ? systemById.get(current.parentSystemId) : undefined;
  }
  return null;
}

function unique(values: string[]): string[] {
  return [...new Set(values)].sort();
}
