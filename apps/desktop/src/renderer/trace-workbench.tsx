import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Boxes,
  CheckCircle2,
  Download,
  FileSearch,
  Filter,
  ListFilter,
  RefreshCw,
  Search,
  XCircle
} from 'lucide-react';
import type {
  EngineeringTraceSearchResponse,
  EngineeringTraceSearchRowResponse,
  TraceFacetCountResponse,
  TraceRecordExportPackageResponse,
  TraceRecordResponse
} from './contracts';
import {
  exportTraceRecord,
  getTraceRecord,
  searchEngineeringTrace
} from './api';

interface TraceWorkbenchProps {
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface TraceFilters {
  serialNumber: string;
  batchId: string;
  stationId: string;
  deviceId: string;
  judgement: string;
  processVersionId: string;
}

const emptySearch: EngineeringTraceSearchResponse = {
  results: {
    items: [],
    pageNumber: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0
  },
  facets: {
    judgements: [],
    stations: [],
    devices: [],
    processVersions: []
  },
  areFacetsTruncated: false
};

export function TraceWorkbench({
  isBackendHealthy,
  onMessage
}: TraceWorkbenchProps): React.ReactElement {
  const [filters, setFilters] = useState<TraceFilters>(createEmptyFilters);
  const [searchResult, setSearchResult] = useState<EngineeringTraceSearchResponse>(emptySearch);
  const [selectedTraceId, setSelectedTraceId] = useState('');
  const [details, setDetails] = useState<TraceRecordResponse | null>(null);
  const [exportPackage, setExportPackage] = useState<TraceRecordExportPackageResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const selectedRow = useMemo(
    () => searchResult.results.items.find(row => row.traceRecordId === selectedTraceId) ?? null,
    [searchResult.results.items, selectedTraceId]);
  const canQuery = isBackendHealthy && !busy;

  const runSearch = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    setBusy(true);
    try {
      const result = await searchEngineeringTrace({
        serialNumber: filters.serialNumber,
        batchId: filters.batchId,
        stationId: filters.stationId,
        deviceId: filters.deviceId,
        judgement: filters.judgement,
        processVersionId: filters.processVersionId,
        pageNumber: 1,
        pageSize: 25
      });
      const next = result ?? emptySearch;
      setSearchResult(next);
      setExportPackage(null);
      setSelectedTraceId(current => {
        if (next.results.items.some(row => row.traceRecordId === current)) {
          return current;
        }

        return next.results.items[0]?.traceRecordId ?? '';
      });
      onMessage(`Trace search returned ${next.results.totalCount} records`);
    } finally {
      setBusy(false);
    }
  }, [filters, isBackendHealthy, onMessage]);

  useEffect(() => {
    runSearch().catch(error => onMessage(`Trace search failed: ${String(error)}`));
  }, [runSearch, onMessage]);

  useEffect(() => {
    if (!isBackendHealthy || !selectedTraceId) {
      setDetails(null);
      return;
    }

    let disposed = false;
    getTraceRecord(selectedTraceId)
      .then(trace => {
        if (!disposed) {
          setDetails(trace);
          setExportPackage(null);
        }
      })
      .catch(error => onMessage(`Trace detail failed: ${String(error)}`));

    return () => {
      disposed = true;
    };
  }, [isBackendHealthy, onMessage, selectedTraceId]);

  const clearFilters = useCallback(() => {
    setFilters(createEmptyFilters());
  }, []);

  const loadExportPackage = useCallback(async () => {
    if (!details) {
      return;
    }

    setBusy(true);
    try {
      const result = await exportTraceRecord(details.traceRecordId);
      setExportPackage(result);
      onMessage(result
        ? `Trace export loaded ${result.packageFormatVersion}`
        : 'Trace export returned no package');
    } finally {
      setBusy(false);
    }
  }, [details, onMessage]);

  return (
    <section className="trace-workbench" data-testid="trace-search-workbench">
      <div className="panel trace-search-panel">
        <div className="panel-title">
          <div>
            <FileSearch size={17} />
            <h2>Traceability Search</h2>
          </div>
          <span>{searchResult.results.totalCount} records</span>
        </div>

        <div className="trace-toolbar">
          <button type="button" className="button ghost" onClick={() => { void runSearch(); }} disabled={!canQuery} data-testid="trace-search-run">
            <Search size={15} />
            Search
          </button>
          <button type="button" className="button ghost" onClick={clearFilters} disabled={busy}>
            <RefreshCw size={15} />
            Clear
          </button>
        </div>

        <div className="trace-layout">
          <TraceFilterForm filters={filters} onChange={setFilters} />
          <TraceResults
            rows={searchResult.results.items}
            selectedTraceId={selectedTraceId}
            onSelect={setSelectedTraceId}
          />
        </div>
      </div>

      <div className="panel trace-detail-panel">
        <div className="panel-title">
          <div>
            <Boxes size={17} />
            <h2>Record Detail</h2>
          </div>
          <span>{selectedRow?.judgement ?? 'none'}</span>
        </div>
        {details ? (
          <TraceDetails
            details={details}
            exportPackage={exportPackage}
            onExport={loadExportPackage}
            disabled={busy}
          />
        ) : (
          <div className="trace-empty">Select a trace record to inspect measurements, artifacts, and audit entries.</div>
        )}
      </div>

      <div className="panel trace-facet-panel">
        <div className="panel-title">
          <div>
            <ListFilter size={17} />
            <h2>Facets</h2>
          </div>
          <span>{searchResult.areFacetsTruncated ? 'truncated' : 'complete'}</span>
        </div>
        <div className="trace-facets">
          <FacetGroup title="Judgement" facets={searchResult.facets.judgements} />
          <FacetGroup title="Station" facets={searchResult.facets.stations} />
          <FacetGroup title="Device" facets={searchResult.facets.devices} />
          <FacetGroup title="Process Version" facets={searchResult.facets.processVersions} />
        </div>
      </div>
    </section>
  );
}

function TraceFilterForm({
  filters,
  onChange
}: {
  filters: TraceFilters;
  onChange(filters: TraceFilters): void;
}): React.ReactElement {
  const update = (key: keyof TraceFilters, value: string): void => {
    onChange({ ...filters, [key]: value });
  };

  return (
    <div className="trace-filter-form">
      <div>
        <Filter size={15} />
        <strong>Filters</strong>
      </div>
      <TraceTextField label="Serial" value={filters.serialNumber} onChange={value => update('serialNumber', value)} />
      <TraceTextField label="Batch" value={filters.batchId} onChange={value => update('batchId', value)} />
      <TraceTextField label="Station" value={filters.stationId} onChange={value => update('stationId', value)} />
      <TraceTextField label="Device" value={filters.deviceId} onChange={value => update('deviceId', value)} />
      <TraceTextField label="Judgement" value={filters.judgement} onChange={value => update('judgement', value)} />
      <TraceTextField label="Process Version" value={filters.processVersionId} onChange={value => update('processVersionId', value)} />
    </div>
  );
}

function TraceTextField({
  label,
  value,
  onChange
}: {
  label: string;
  value: string;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input value={value} onChange={event => onChange(event.target.value)} />
    </label>
  );
}

function TraceResults({
  rows,
  selectedTraceId,
  onSelect
}: {
  rows: EngineeringTraceSearchRowResponse[];
  selectedTraceId: string;
  onSelect(traceRecordId: string): void;
}): React.ReactElement {
  return (
    <div className="trace-results">
      <div className="trace-result-head">
        <span>Serial</span>
        <span>Station</span>
        <span>Judgement</span>
        <span>Measurements</span>
      </div>
      {rows.length === 0 ? (
        <div className="trace-empty">No trace records match the current filters.</div>
      ) : rows.map(row => (
        <button
          type="button"
          key={row.traceRecordId}
          className={row.traceRecordId === selectedTraceId ? 'trace-result-row selected' : 'trace-result-row'}
          onClick={() => onSelect(row.traceRecordId)}
          data-testid="trace-result-row"
        >
          <strong>{row.serialNumber}</strong>
          <span>{row.stationId}</span>
          <TraceJudgement value={row.judgement} />
          <span>{row.failedMeasurementCount}/{row.measurementCount}</span>
        </button>
      ))}
    </div>
  );
}

function TraceDetails({
  details,
  exportPackage,
  onExport,
  disabled
}: {
  details: TraceRecordResponse;
  exportPackage: TraceRecordExportPackageResponse | null;
  onExport(): Promise<void>;
  disabled: boolean;
}): React.ReactElement {
  return (
    <div className="trace-detail-card" data-testid="trace-detail-panel">
      <div className="trace-detail-header">
        <TraceJudgement value={details.judgement} />
        <div>
          <strong>{details.serialNumber}</strong>
          <span>{details.traceRecordId}</span>
        </div>
      </div>

      <dl>
        <dt>Station</dt>
        <dd>{details.stationId}</dd>
        <dt>Device</dt>
        <dd>{details.deviceId}</dd>
        <dt>Process</dt>
        <dd>{details.processVersionId}</dd>
        <dt>Config</dt>
        <dd>{details.configurationSnapshotId}</dd>
        <dt>Completed</dt>
        <dd>{formatDateTime(details.completedAtUtc)}</dd>
        <dt>Recorded By</dt>
        <dd>{details.recordedBy}</dd>
      </dl>

      <button type="button" className="button ghost" onClick={() => { void onExport(); }} disabled={disabled} data-testid="trace-export-package">
        <Download size={15} />
        Export Package
      </button>

      {exportPackage ? (
        <div className="trace-export-box">
          <strong>{exportPackage.packageFormatVersion}</strong>
          <span>{formatDateTime(exportPackage.exportedAtUtc)}</span>
        </div>
      ) : null}

      <TraceMeasurementList details={details} />
    </div>
  );
}

function TraceMeasurementList({ details }: { details: TraceRecordResponse }): React.ReactElement {
  return (
    <div className="trace-detail-lists">
      <section>
        <strong>Measurements</strong>
        {details.measurements.length === 0 ? (
          <p>No measurements</p>
        ) : details.measurements.slice(0, 6).map(measurement => (
          <article key={measurement.measurementRecordId}>
            <span>{measurement.name}</span>
            <strong>{formatMeasurementValue(measurement.numericValue, measurement.textValue, measurement.unit)}</strong>
            <small>{measurement.passed === false ? 'failed' : 'passed'}</small>
          </article>
        ))}
      </section>
      <section>
        <strong>Artifacts</strong>
        {details.artifacts.length === 0 ? (
          <p>No artifacts</p>
        ) : details.artifacts.slice(0, 5).map(artifact => (
          <article key={artifact.artifactRecordId}>
            <span>{artifact.name}</span>
            <strong>{artifact.kind}</strong>
            <small>{artifact.storageKey}</small>
          </article>
        ))}
      </section>
      <section>
        <strong>Audit</strong>
        {details.auditEntries.length === 0 ? (
          <p>No audit entries</p>
        ) : details.auditEntries.slice(0, 5).map(entry => (
          <article key={entry.auditEntryId}>
            <span>{entry.action}</span>
            <strong>{entry.actorId}</strong>
            <small>{entry.detail ?? formatDateTime(entry.occurredAtUtc)}</small>
          </article>
        ))}
      </section>
    </div>
  );
}

function FacetGroup({
  title,
  facets
}: {
  title: string;
  facets: TraceFacetCountResponse[];
}): React.ReactElement {
  return (
    <section className="trace-facet-group">
      <strong>{title}</strong>
      {facets.length === 0 ? (
        <p>None</p>
      ) : facets.slice(0, 5).map(facet => (
        <div key={`${title}-${facet.value}`}>
          <span>{facet.value}</span>
          <small>{facet.count}</small>
        </div>
      ))}
    </section>
  );
}

function TraceJudgement({ value }: { value: string }): React.ReactElement {
  const isPassed = value === 'Passed';
  return (
    <span className={isPassed ? 'trace-judgement passed' : 'trace-judgement failed'}>
      {isPassed ? <CheckCircle2 size={13} /> : <XCircle size={13} />}
      {value}
    </span>
  );
}

function createEmptyFilters(): TraceFilters {
  return {
    serialNumber: '',
    batchId: '',
    stationId: '',
    deviceId: '',
    judgement: '',
    processVersionId: ''
  };
}

function formatMeasurementValue(
  numericValue: number | null,
  textValue: string | null,
  unit: string | null
): string {
  if (numericValue !== null) {
    return `${numericValue}${unit ? ` ${unit}` : ''}`;
  }

  return textValue ?? 'n/a';
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}
