import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  Boxes,
  CheckCircle2,
  ChevronRight,
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
  TraceRecordResponse,
  TraceStageExecutionResponse
} from './contracts';
import { exportTraceRecord, getTraceRecord, searchEngineeringTrace } from './api';

interface TraceWorkbenchProps {
  isBackendHealthy: boolean;
  projectId: string | null;
  applicationId: string | null;
  onMessage(message: string): void;
}

interface TraceFilters {
  dutIdentityValue: string;
  batchId: string;
  stationId: string;
  deviceId: string;
  runStatus: string;
  judgement: string;
  processVersionId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  productionLineDefinitionId: string;
}

const emptySearch: EngineeringTraceSearchResponse = {
  results: { items: [], pageNumber: 1, pageSize: 25, totalCount: 0, totalPages: 0 },
  facets: {
    judgements: [],
    runStatuses: [],
    stations: [],
    devices: [],
    productionLines: [],
    processVersions: [],
    projectSnapshots: []
  },
  areFacetsTruncated: false
};

export function TraceWorkbench({
  isBackendHealthy,
  projectId,
  applicationId,
  onMessage
}: TraceWorkbenchProps): React.ReactElement {
  const [filters, setFilters] = useState<TraceFilters>(
    () => createScopedFilters(projectId, applicationId));
  const [searchResult, setSearchResult] = useState<EngineeringTraceSearchResponse>(emptySearch);
  const [selectedTraceId, setSelectedTraceId] = useState('');
  const [details, setDetails] = useState<TraceRecordResponse | null>(null);
  const [exportPackage, setExportPackage] = useState<TraceRecordExportPackageResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const selectedRow = useMemo(
    () => searchResult.results.items.find(row => row.traceRecordId === selectedTraceId) ?? null,
    [searchResult.results.items, selectedTraceId]);

  useEffect(() => {
    setFilters(createScopedFilters(projectId, applicationId));
    setSelectedTraceId('');
    setDetails(null);
    setExportPackage(null);
  }, [applicationId, projectId]);

  const runSearch = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    setBusy(true);
    try {
      const result = await searchEngineeringTrace({
        dutIdentityValue: filters.dutIdentityValue,
        batchId: filters.batchId,
        stationId: filters.stationId,
        deviceId: filters.deviceId,
        runStatus: filters.runStatus,
        judgement: filters.judgement,
        processVersionId: filters.processVersionId,
        projectId: filters.projectId,
        applicationId: filters.applicationId,
        projectSnapshotId: filters.projectSnapshotId,
        productionLineDefinitionId: filters.productionLineDefinitionId,
        pageNumber: 1,
        pageSize: 25
      });
      const next = result ?? emptySearch;
      setSearchResult(next);
      setExportPackage(null);
      setSelectedTraceId(current => next.results.items.some(row => row.traceRecordId === current)
        ? current
        : next.results.items[0]?.traceRecordId ?? '');
      onMessage(`Production trace search returned ${next.results.totalCount} runs`);
    } finally {
      setBusy(false);
    }
  }, [filters, isBackendHealthy, onMessage]);

  useEffect(() => {
    runSearch().catch(error => onMessage(`Production trace search failed: ${String(error)}`));
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
      .catch(error => onMessage(`Production trace detail failed: ${String(error)}`));
    return () => { disposed = true; };
  }, [isBackendHealthy, onMessage, selectedTraceId]);

  const loadExportPackage = useCallback(async () => {
    if (!details) {
      return;
    }

    setBusy(true);
    try {
      const result = await exportTraceRecord(details.traceRecordId);
      setExportPackage(result);
      onMessage(result
        ? `Production trace export loaded ${result.packageFormatVersion}`
        : 'Production trace export returned no package');
    } finally {
      setBusy(false);
    }
  }, [details, onMessage]);

  return (
    <section className="trace-workbench" data-testid="trace-search-workbench">
      <div className="panel trace-search-panel">
        <div className="panel-title">
          <div><FileSearch size={17} /><h2>Production Trace</h2></div>
          <span>{searchResult.results.totalCount} runs</span>
        </div>
        <div className="trace-toolbar">
          <button type="button" className="button ghost" onClick={() => { void runSearch(); }} disabled={!isBackendHealthy || busy} data-testid="trace-search-run">
            <Search size={15} />Search
          </button>
          <button type="button" className="button ghost" onClick={() => setFilters(createScopedFilters(projectId, applicationId))} disabled={busy}>
            <RefreshCw size={15} />Reset
          </button>
        </div>
        <div className="trace-layout">
          <TraceFilterForm filters={filters} onChange={setFilters} />
          <TraceResults rows={searchResult.results.items} selectedTraceId={selectedTraceId} onSelect={setSelectedTraceId} />
        </div>
        <details className="trace-facet-drawer">
          <summary>
            <div><ListFilter size={15} /><strong>Evidence facets</strong></div>
            <span>{searchResult.areFacetsTruncated ? 'Truncated' : 'Complete'}</span>
          </summary>
          <div className="trace-facets">
            <FacetGroup title="Judgement" facets={searchResult.facets.judgements} />
            <FacetGroup title="Run Status" facets={searchResult.facets.runStatuses} />
            <FacetGroup title="Production Line" facets={searchResult.facets.productionLines} />
            <FacetGroup title="Station" facets={searchResult.facets.stations} />
            <FacetGroup title="Device" facets={searchResult.facets.devices} />
            <FacetGroup title="Process Version" facets={searchResult.facets.processVersions} />
            <FacetGroup title="Project Snapshot" facets={searchResult.facets.projectSnapshots} />
          </div>
        </details>
      </div>

      <div className="panel trace-detail-panel">
        <div className="panel-title">
          <div><Boxes size={17} /><h2>Run Evidence</h2></div>
          <span>{selectedRow?.runStatus ?? 'none'}</span>
        </div>
        {details ? (
          <TraceDetails details={details} exportPackage={exportPackage} onExport={loadExportPackage} disabled={busy} />
        ) : (
          <div className="trace-empty">Select a production run to inspect its ordered stage evidence.</div>
        )}
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
  const update = (key: keyof TraceFilters, value: string): void => onChange({ ...filters, [key]: value });
  const activeFilterCount = Object.values(filters).filter(value => value.length > 0).length;
  return (
    <details className="trace-filter-form">
      <summary>
        <div><Filter size={15} /><strong>Run filters</strong></div>
        <span>{activeFilterCount === 0 ? 'All runs' : `${activeFilterCount} active`}</span>
      </summary>
      <div className="trace-filter-fields">
        <TraceTextField label="DUT identity" value={filters.dutIdentityValue} onChange={value => update('dutIdentityValue', value)} />
        <TraceTextField label="Batch" value={filters.batchId} onChange={value => update('batchId', value)} />
        <TraceTextField label="Production line" value={filters.productionLineDefinitionId} onChange={value => update('productionLineDefinitionId', value)} />
        <TraceTextField label="Run status" value={filters.runStatus} onChange={value => update('runStatus', value)} />
        <TraceTextField label="Judgement" value={filters.judgement} onChange={value => update('judgement', value)} />
        <TraceTextField label="Station" value={filters.stationId} onChange={value => update('stationId', value)} />
        <TraceTextField label="Process version" value={filters.processVersionId} onChange={value => update('processVersionId', value)} />
        <TraceTextField label="Device" value={filters.deviceId} onChange={value => update('deviceId', value)} />
        <TraceTextField label="Project" value={filters.projectId} onChange={value => update('projectId', value)} />
        <TraceTextField label="Application" value={filters.applicationId} onChange={value => update('applicationId', value)} />
        <TraceTextField label="Project snapshot" value={filters.projectSnapshotId} onChange={value => update('projectSnapshotId', value)} />
      </div>
    </details>
  );
}

function TraceTextField({ label, value, onChange }: {
  label: string;
  value: string;
  onChange(value: string): void;
}): React.ReactElement {
  return <label><span>{label}</span><input value={value} onChange={event => onChange(event.target.value)} /></label>;
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
        <span>Production runs</span><span>{rows.length} loaded</span>
      </div>
      {rows.length === 0 ? (
        <div className="trace-empty">No production traces match the current filters.</div>
      ) : rows.map(row => (
        <button
          type="button"
          key={row.traceRecordId}
          className={row.traceRecordId === selectedTraceId ? 'trace-result-row selected' : 'trace-result-row'}
          onClick={() => onSelect(row.traceRecordId)}
          data-testid="trace-result-row"
        >
          <span className="trace-result-primary">
            <strong title={`${row.dutModelId} · ${row.dutIdentityInputKey}`}>{row.dutIdentityValue}</strong>
            <small>{row.productionLineDefinitionId}</small>
          </span>
          <span className="trace-result-state">
            <RunStatus value={row.runStatus} />
            <TraceJudgement value={row.judgement} />
          </span>
          <small className="trace-result-id">{row.productionRunId}</small>
          <span className="trace-result-counts">
            {row.stageCount} stages · {row.commandCount} commands
          </span>
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
          <strong>{details.dutIdentityValue}</strong>
          <span>{details.dutModelId} · {details.dutIdentityInputKey} · {details.productionRunId}</span>
        </div>
      </div>

      <div className="trace-run-state">
        <RunStatus value={details.runStatus} />
        <span>{details.stages.length} ordered stages</span>
        <span>{details.productionLineDefinitionId}</span>
      </div>

      <dl>
        <dt>Project</dt><dd>{details.projectId} / {details.applicationId}</dd>
        <dt>Snapshot</dt><dd>{details.projectSnapshotId}</dd>
        <dt>Topology</dt><dd>{details.topologyId}</dd>
        <dt>Batch</dt><dd>{details.batchId ?? 'n/a'}</dd>
        <dt>Fixture</dt><dd>{details.fixtureId ?? 'n/a'}</dd>
        <dt>Device</dt><dd>{details.deviceId ?? 'n/a'}</dd>
        <dt>Actor</dt><dd>{details.actorId}</dd>
        <dt>Started</dt><dd>{formatOptionalDateTime(details.startedAtUtc)}</dd>
        <dt>Completed</dt><dd>{formatDateTime(details.completedAtUtc)}</dd>
        {details.failureCode ? <><dt>Failure</dt><dd>{details.failureCode} · {details.failureReason}</dd></> : null}
      </dl>

      <button type="button" className="button ghost" onClick={() => { void onExport(); }} disabled={disabled} data-testid="trace-export-package">
        <Download size={15} />Export Immutable Package
      </button>
      {exportPackage ? (
        <div className="trace-export-box"><strong>{exportPackage.packageFormatVersion}</strong><span>{formatDateTime(exportPackage.exportedAtUtc)}</span></div>
      ) : null}

      <div className="trace-stage-stack">
        {details.stages.map(stage => <TraceStageCard key={stage.stageId} stage={stage} />)}
      </div>

      <section className="trace-audit-list">
        <strong>Run audit</strong>
        {details.auditEntries.map(entry => (
          <article key={entry.auditEntryId}>
            <span>{entry.action}</span><strong>{entry.actorId}</strong>
            <small>{entry.detail ?? formatDateTime(entry.occurredAtUtc)}</small>
          </article>
        ))}
      </section>
    </div>
  );
}

function TraceStageCard({ stage }: { stage: TraceStageExecutionResponse }): React.ReactElement {
  const isFailed = stage.status === 'Failed' || stage.status === 'Canceled';
  return (
    <details className={isFailed ? 'trace-stage-card failed' : 'trace-stage-card'} open={stage.sequence === 1 || isFailed} data-testid="trace-stage-card">
      <summary>
        <span className="trace-stage-sequence">{String(stage.sequence).padStart(2, '0')}</span>
        <div><strong>{stage.stageId}</strong><small>{stage.workstationId} · {stage.stationId}</small></div>
        <RunStatus value={stage.status} />
        <ChevronRight size={14} />
      </summary>
      <div className="trace-stage-body">
        <div className="trace-stage-metadata">
          <span><small>Process</small><strong>{stage.processVersionId}</strong></span>
          <span><small>Configuration</small><strong>{stage.configurationSnapshotId}</strong></span>
          <span><small>Recipe</small><strong>{stage.recipeSnapshotId}</strong></span>
          <span><small>Session</small><strong>{stage.runtimeSessionStatus ?? 'not started'}</strong></span>
        </div>
        {stage.failureCode ? <div className="trace-stage-failure"><AlertTriangle size={14} /><span>{stage.failureCode} · {stage.failureReason}</span></div> : null}
        <div className="trace-stage-counts">
          <span>{stage.completedStepCount} steps</span><span>{stage.commandCount} commands</span><span>{stage.incidentCount} incidents</span>
        </div>
        <div className="trace-evidence-grid">
          <EvidenceSection title="Commands" emptyText="No commands">
            {stage.commands.slice(0, 8).map(command => (
              <article key={command.runtimeCommandId}>
                <span>{command.commandName}</span><strong>{command.semanticOutcome ?? command.status}</strong>
                <small>{command.actionId} · {command.targetKind}:{command.targetId}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Measurements" emptyText="No measurements">
            {stage.measurements.slice(0, 8).map(measurement => (
              <article key={measurement.measurementRecordId}>
                <span>{measurement.name}</span>
                <strong>{formatMeasurementValue(measurement.numericValue, measurement.textValue, measurement.unit)}</strong>
                <small>{measurement.commandStatus} · {measurement.passed === false ? 'failed' : measurement.passed === true ? 'passed' : 'indeterminate'}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Incidents" emptyText="No incidents">
            {stage.incidents.slice(0, 8).map(incident => (
              <article key={incident.runtimeIncidentId}>
                <span>{incident.code}</span><strong>{incident.severity}</strong><small>{incident.message}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Artifacts" emptyText="No artifacts">
            {stage.artifacts.slice(0, 8).map(artifact => (
              <article key={artifact.artifactRecordId}>
                <span>{artifact.name}</span><strong>{artifact.kind}</strong><small>{artifact.storageKey}</small>
              </article>
            ))}
          </EvidenceSection>
        </div>
      </div>
    </details>
  );
}

function EvidenceSection({
  title,
  emptyText,
  children
}: {
  title: string;
  emptyText: string;
  children: React.ReactNode;
}): React.ReactElement {
  const hasChildren = React.Children.count(children) > 0;
  return <section><strong>{title}</strong>{hasChildren ? children : <p>{emptyText}</p>}</section>;
}

function FacetGroup({ title, facets }: { title: string; facets: TraceFacetCountResponse[] }): React.ReactElement {
  return (
    <section className="trace-facet-group">
      <strong>{title}</strong>
      {facets.length === 0 ? <p>None</p> : facets.slice(0, 5).map(facet => (
        <div key={`${title}-${facet.value}`}><span>{facet.value}</span><small>{facet.count}</small></div>
      ))}
    </section>
  );
}

function RunStatus({ value }: { value: string }): React.ReactElement {
  const className = value === 'Completed'
    ? 'completed'
    : value === 'Failed'
      ? 'failed'
      : value === 'Canceled' || value === 'Skipped'
        ? 'canceled'
        : 'neutral';
  return <span className={`trace-run-status ${className}`}><Activity size={12} />{value}</span>;
}

function TraceJudgement({ value }: { value: string }): React.ReactElement {
  const isPassed = value === 'Passed';
  return (
    <span className={`trace-judgement ${isPassed ? 'passed' : value.toLowerCase()}`}>
      {isPassed ? <CheckCircle2 size={13} /> : <XCircle size={13} />}{value}
    </span>
  );
}

function createScopedFilters(projectId: string | null, applicationId: string | null): TraceFilters {
  return {
    dutIdentityValue: '',
    batchId: '',
    stationId: '',
    deviceId: '',
    runStatus: '',
    judgement: '',
    processVersionId: '',
    projectId: projectId ?? '',
    applicationId: applicationId ?? '',
    projectSnapshotId: '',
    productionLineDefinitionId: ''
  };
}

function formatMeasurementValue(numericValue: number | null, textValue: string | null, unit: string | null): string {
  return numericValue === null ? textValue ?? 'n/a' : `${numericValue}${unit ? ` ${unit}` : ''}`;
}

function formatOptionalDateTime(value: string | null): string {
  return value ? formatDateTime(value) : 'not started';
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
