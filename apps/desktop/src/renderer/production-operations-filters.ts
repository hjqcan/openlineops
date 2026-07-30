import type {
  ProductionLineRuntimeStateResponse,
  ProductionOperationsFilters
} from './contracts';

export interface ProductionOperationsSlotOption {
  resourceId: string;
  stationSystemId: string;
  slotId: string;
  label: string;
}

interface ProductionOperationsStation {
  stationSystemId: string;
  slots: ReadonlyArray<{ slotId: string }>;
}

export function buildSlotResourceId(
  productionLineDefinitionId: string,
  stationSystemId: string,
  slotId: string
): string {
  const segments = [productionLineDefinitionId, stationSystemId, slotId];
  if (segments.some(segment => (
    !segment
    || segment.trim() !== segment
    || segment.includes('/')))) {
    throw new Error('Slot resource identity must contain exact Line, Station, and Slot segments.');
  }
  return segments.join('/');
}

export function isCanonicalSlotResourceId(value: string): boolean {
  const segments = value.split('/');
  return segments.length === 3
    && segments.every(segment => Boolean(segment) && segment.trim() === segment);
}

export function slotResourceIdMatchesScope(
  slotResourceId: string,
  productionLineDefinitionId: string,
  stationSystemId: string
): boolean {
  if (!isCanonicalSlotResourceId(slotResourceId)) {
    return false;
  }
  const [lineSegment, stationSegment] = slotResourceId.split('/');
  return (!productionLineDefinitionId || lineSegment === productionLineDefinitionId)
    && (!stationSystemId || stationSegment === stationSystemId);
}

export function buildProductionOperationsSlotOptions(
  lineState: ProductionLineRuntimeStateResponse | null,
  stationSystemId: string
): ProductionOperationsSlotOption[] {
  if (!lineState) {
    return [];
  }

  const options = new Map<string, ProductionOperationsSlotOption>();
  for (const slot of lineState.slots) {
    if (stationSystemId && slot.stationSystemId !== stationSystemId) {
      continue;
    }
    const resourceId = buildSlotResourceId(
      lineState.productionLineDefinitionId,
      slot.stationSystemId,
      slot.slotId);
    options.set(resourceId, {
      resourceId,
      stationSystemId: slot.stationSystemId,
      slotId: slot.slotId,
      label: stationSystemId ? slot.slotId : `${slot.stationSystemId} / ${slot.slotId}`
    });
  }

  return [...options.values()].sort((left, right) => (
    left.stationSystemId.localeCompare(right.stationSystemId)
    || left.slotId.localeCompare(right.slotId)));
}

export function stationMatchesProductionOperationsFilters(
  productionLineDefinitionId: string,
  station: ProductionOperationsStation,
  filters: ProductionOperationsFilters
): boolean {
  if (filters.stationSystemId
      && station.stationSystemId !== filters.stationSystemId) {
    return false;
  }
  if (filters.slotResourceId && !productionLineDefinitionId) {
    return false;
  }
  return !filters.slotResourceId || station.slots.some(slot => (
    buildSlotResourceId(
      productionLineDefinitionId,
      station.stationSystemId,
      slot.slotId) === filters.slotResourceId));
}

export function buildActiveProductionRunsQuery(
  filters: ProductionOperationsFilters
): string {
  const query: string[] = [];
  if (filters.productionLineDefinitionId) {
    query.push(
      `productionLineDefinitionId=${encodeURIComponent(filters.productionLineDefinitionId)}`);
  }
  if (filters.stationSystemId) {
    query.push(`stationSystemId=${encodeURIComponent(filters.stationSystemId)}`);
  }
  if (filters.slotResourceId) {
    const encodedSlotResourceId = encodeURIComponent(filters.slotResourceId)
      .replaceAll('%2F', '/');
    query.push(`slotResourceId=${encodedSlotResourceId}`);
  }
  return query.join('&');
}
