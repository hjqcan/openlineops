import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  ProductionLineRuntimeStateResponse,
  ProductionOperationsFilters,
  ProductionRunReadModel
} from './contracts';
import { getActiveProductionRuns, getProductionLineRuntimeState } from './api';

const emptyFilters: ProductionOperationsFilters = {
  productionLineDefinitionId: '',
  stationSystemId: '',
  slotId: ''
};

interface ScopedOperationsFilters {
  storageKey: string | null;
  value: ProductionOperationsFilters;
}

interface ScopedLineSelection {
  storageKey: string | null;
  value: string;
}

export interface ProductionOperationsProjection {
  activeRuns: ProductionRunReadModel[];
  lineState: ProductionLineRuntimeStateResponse | null;
  filters: ProductionOperationsFilters;
  selectedLineId: string;
  connected: boolean;
  refreshing: boolean;
  lastSynchronizedAtUtc: string | null;
  setFilter(filter: keyof ProductionOperationsFilters, value: string): void;
  refresh(): Promise<void>;
}

export function useProductionOperations({
  isBackendHealthy,
  projectId,
  applicationId,
  preferredLineId,
  onMessage
}: {
  isBackendHealthy: boolean;
  projectId: string | null;
  applicationId: string | null;
  preferredLineId: string | null;
  onMessage(message: string): void;
}): ProductionOperationsProjection {
  const [activeRuns, setActiveRuns] = useState<ProductionRunReadModel[]>([]);
  const [lineState, setLineState] = useState<ProductionLineRuntimeStateResponse | null>(null);
  const [connected, setConnected] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [lastSynchronizedAtUtc, setLastSynchronizedAtUtc] = useState<string | null>(null);
  const requestSequenceRef = useRef(0);
  const lastErrorRef = useRef('');
  const storageKey = useMemo(
    () => projectId && applicationId
      ? `openlineops.operations.filters.${projectId}.${applicationId}`
      : null,
    [applicationId, projectId]);
  const [filterState, setFilterState] = useState<ScopedOperationsFilters>({
    storageKey: null,
    value: emptyFilters
  });
  const [lineSelection, setLineSelection] = useState<ScopedLineSelection>({
    storageKey: null,
    value: ''
  });
  const filters = filterState.storageKey === storageKey ? filterState.value : emptyFilters;
  const selectedLineId = lineSelection.storageKey === storageKey ? lineSelection.value : '';
  const selectedLineIdRef = useRef(selectedLineId);
  selectedLineIdRef.current = selectedLineId;
  const queryContextKey = [
    projectId ?? '',
    applicationId ?? '',
    filters.productionLineDefinitionId,
    filters.stationSystemId,
    filters.slotId,
    preferredLineId ?? '',
    isBackendHealthy ? 'healthy' : 'unhealthy'
  ].join('\u001f');
  const queryContextKeyRef = useRef(queryContextKey);
  queryContextKeyRef.current = queryContextKey;

  useEffect(() => {
    requestSequenceRef.current += 1;
    setActiveRuns([]);
    setLineState(null);
    setConnected(false);
    setRefreshing(false);
    setLastSynchronizedAtUtc(null);
    if (!storageKey) {
      setFilterState({ storageKey: null, value: emptyFilters });
      setLineSelection({ storageKey: null, value: '' });
      return;
    }

    const restored = readFilters(storageKey);
    const nextFilters = restored ?? emptyFilters;
    setFilterState({ storageKey, value: nextFilters });
    setLineSelection({
      storageKey,
      value: nextFilters.productionLineDefinitionId || preferredLineId || ''
    });
  }, [preferredLineId, storageKey]);

  useEffect(() => {
    if (storageKey && filterState.storageKey === storageKey) {
      window.localStorage.setItem(storageKey, JSON.stringify(filterState.value));
    }
  }, [filterState, storageKey]);

  const refresh = useCallback(async () => {
    if (!isBackendHealthy || !projectId || !applicationId) {
      if (!isBackendHealthy) {
        setConnected(false);
        setRefreshing(false);
      }
      return;
    }

    const requestId = ++requestSequenceRef.current;
    const requestContextKey = queryContextKey;
    const isCurrentRequest = (): boolean => (
      requestSequenceRef.current === requestId
      && queryContextKeyRef.current === requestContextKey);
    setRefreshing(true);
    try {
      const anticipatedLineId = filters.productionLineDefinitionId
        || preferredLineId
        || selectedLineIdRef.current
        || '';
      const [activeResponse, anticipatedLineResponse] = await Promise.all([
        getActiveProductionRuns(filters),
        anticipatedLineId
          ? getProductionLineRuntimeState(anticipatedLineId)
          : Promise.resolve(null)
      ]);
      if (!activeResponse.ok || !activeResponse.body) {
        throw new Error(`Active Runs ${activeResponse.status}: ${activeResponse.text}`);
      }

      const scopedRuns = activeResponse.body.runs.filter(run => (
        run.projectId === projectId && run.applicationId === applicationId));
      const resolvedLineId = filters.productionLineDefinitionId
        || preferredLineId
        || selectedLineIdRef.current
        || scopedRuns[0]?.productionLineDefinitionId
        || '';
      let resolvedLineResponse = anticipatedLineResponse;
      if (resolvedLineId && resolvedLineId !== anticipatedLineId) {
        resolvedLineResponse = await getProductionLineRuntimeState(resolvedLineId);
      }
      if (resolvedLineResponse && (!resolvedLineResponse.ok || !resolvedLineResponse.body)) {
        throw new Error(`Line State ${resolvedLineResponse.status}: ${resolvedLineResponse.text}`);
      }
      if (resolvedLineResponse?.body
          && resolvedLineResponse.body.productionLineDefinitionId !== resolvedLineId) {
        throw new Error(
          `Line State identity mismatch: requested ${resolvedLineId}, received ${resolvedLineResponse.body.productionLineDefinitionId}`);
      }
      if (!isCurrentRequest()) {
        return;
      }

      const resolvedLineState = resolvedLineResponse?.body ?? null;
      setActiveRuns(scopedRuns);
      setLineSelection({ storageKey, value: resolvedLineId });
      setLineState(resolvedLineState);
      setConnected(true);
      setLastSynchronizedAtUtc(resolvedLineState?.generatedAtUtc ?? new Date().toISOString());
      lastErrorRef.current = '';
    } catch (error) {
      if (!isCurrentRequest()) {
        return;
      }
      const message = String(error);
      setConnected(false);
      if (lastErrorRef.current !== message) {
        lastErrorRef.current = message;
        onMessage(`Operations projection unavailable: ${message}`);
      }
    } finally {
      if (isCurrentRequest()) {
        setRefreshing(false);
      }
    }
  }, [applicationId, filters, isBackendHealthy, onMessage, preferredLineId, projectId, queryContextKey, storageKey]);

  useEffect(() => {
    void refresh();
    if (!isBackendHealthy || !projectId || !applicationId) {
      return;
    }
    const timer = window.setInterval(() => void refresh(), 2000);
    const refreshVisible = (): void => {
      if (document.visibilityState === 'visible') {
        void refresh();
      }
    };
    window.addEventListener('focus', refreshVisible);
    document.addEventListener('visibilitychange', refreshVisible);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('focus', refreshVisible);
      document.removeEventListener('visibilitychange', refreshVisible);
    };
  }, [applicationId, isBackendHealthy, projectId, refresh]);

  const setFilter = useCallback((filter: keyof ProductionOperationsFilters, value: string) => {
    requestSequenceRef.current += 1;
    if (filter === 'productionLineDefinitionId') {
      setLineSelection({ storageKey, value });
      setLineState(null);
      setConnected(false);
      setLastSynchronizedAtUtc(null);
    }
    setFilterState(current => {
      const currentFilters = current.storageKey === storageKey ? current.value : emptyFilters;
      if (filter === 'productionLineDefinitionId') {
        return {
          storageKey,
          value: {
            productionLineDefinitionId: value,
            stationSystemId: '',
            slotId: ''
          }
        };
      }
      if (filter === 'stationSystemId') {
        return {
          storageKey,
          value: { ...currentFilters, stationSystemId: value, slotId: '' }
        };
      }
      return {
        storageKey,
        value: { ...currentFilters, slotId: value }
      };
    });
  }, [storageKey]);

  return {
    activeRuns,
    lineState,
    filters,
    selectedLineId,
    connected,
    refreshing,
    lastSynchronizedAtUtc,
    setFilter,
    refresh
  };
}

function readFilters(storageKey: string): ProductionOperationsFilters | null {
  try {
    const parsed = JSON.parse(window.localStorage.getItem(storageKey) ?? 'null') as Partial<ProductionOperationsFilters> | null;
    if (!parsed) {
      return null;
    }
    return {
      productionLineDefinitionId: typeof parsed.productionLineDefinitionId === 'string'
        ? parsed.productionLineDefinitionId
        : '',
      stationSystemId: typeof parsed.stationSystemId === 'string' ? parsed.stationSystemId : '',
      slotId: typeof parsed.slotId === 'string' ? parsed.slotId : ''
    };
  } catch {
    return null;
  }
}
