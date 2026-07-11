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
  const [filters, setFilters] = useState<ProductionOperationsFilters>(emptyFilters);
  const [selectedLineId, setSelectedLineId] = useState('');
  const [connected, setConnected] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [lastSynchronizedAtUtc, setLastSynchronizedAtUtc] = useState<string | null>(null);
  const inFlightRef = useRef(false);
  const lastErrorRef = useRef('');
  const selectedLineIdRef = useRef(selectedLineId);
  selectedLineIdRef.current = selectedLineId;
  const storageKey = useMemo(
    () => projectId && applicationId
      ? `openlineops.operations.filters.${projectId}.${applicationId}`
      : null,
    [applicationId, projectId]);

  useEffect(() => {
    setActiveRuns([]);
    setLineState(null);
    setConnected(false);
    setLastSynchronizedAtUtc(null);
    if (!storageKey) {
      setFilters(emptyFilters);
      setSelectedLineId('');
      return;
    }

    const restored = readFilters(storageKey);
    const nextFilters = restored ?? emptyFilters;
    setFilters(nextFilters);
    setSelectedLineId(nextFilters.productionLineDefinitionId || preferredLineId || '');
  }, [preferredLineId, storageKey]);

  useEffect(() => {
    if (storageKey) {
      window.localStorage.setItem(storageKey, JSON.stringify(filters));
    }
  }, [filters, storageKey]);

  const refresh = useCallback(async () => {
    if (!isBackendHealthy || !projectId || !applicationId || inFlightRef.current) {
      if (!isBackendHealthy) {
        setConnected(false);
      }
      return;
    }

    inFlightRef.current = true;
    setRefreshing(true);
    try {
      const anticipatedLineId = filters.productionLineDefinitionId
        || selectedLineIdRef.current
        || preferredLineId
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

      const resolvedLineState = resolvedLineResponse?.body ?? null;
      const scopedLineRuns = resolvedLineState?.activeRuns.filter(run => (
        run.projectId === projectId && run.applicationId === applicationId)) ?? [];
      const scopedLineState = resolvedLineState
        ? {
          ...resolvedLineState,
          activeRunCount: scopedLineRuns.length,
          activeRuns: scopedLineRuns
        }
        : null;
      setActiveRuns(scopedRuns);
      setSelectedLineId(resolvedLineId);
      setLineState(scopedLineState);
      setConnected(true);
      setLastSynchronizedAtUtc(scopedLineState?.generatedAtUtc ?? new Date().toISOString());
      lastErrorRef.current = '';
    } catch (error) {
      const message = String(error);
      setConnected(false);
      if (lastErrorRef.current !== message) {
        lastErrorRef.current = message;
        onMessage(`Operations projection unavailable: ${message}`);
      }
    } finally {
      inFlightRef.current = false;
      setRefreshing(false);
    }
  }, [applicationId, filters, isBackendHealthy, onMessage, preferredLineId, projectId]);

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
    if (filter === 'productionLineDefinitionId') {
      setSelectedLineId(value);
    }
    setFilters(current => {
      if (filter === 'productionLineDefinitionId') {
        return {
          productionLineDefinitionId: value,
          stationSystemId: '',
          slotId: ''
        };
      }
      if (filter === 'stationSystemId') {
        return { ...current, stationSystemId: value, slotId: '' };
      }
      return { ...current, slotId: value };
    });
  }, []);

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
