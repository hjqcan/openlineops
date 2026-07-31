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
  ShieldAlert,
  XCircle
} from 'lucide-react';
import type {
  ArtifactRecordResponse,
  EngineeringTraceSearchResponse,
  EngineeringTraceSearchRowResponse,
  ProductionUnitMaterialLifecycleResponse,
  TraceFacetCountResponse,
  TraceOperationExecutionResponse,
  TraceRecordExportPackageResponse,
  TraceRecordResponse,
  StationEmergencyStopResponse
} from './contracts';
import { desktop } from './desktop-bridge';
import {
  exportTraceRecord,
  getProductionUnitMaterialLifecycle,
  getTraceRecord,
  searchEngineeringTrace,
  searchStationSafetyTrace
} from './api';

interface TraceWorkbenchProps {
  isBackendHealthy: boolean;
  projectId: string | null;
  applicationId: string | null;
  onMessage(message: string): void;
}

interface TraceFilters {
  productionUnitIdentityValue: string;
  lotId: string;
  carrierId: string;
  stationSystemId: string;
  deviceId: string;
  executionStatus: string;
  judgement: string;
  disposition: string;
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
    executionStatuses: [],
    dispositions: [],
    stationSystems: [],
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
  const [materialLifecycle, setMaterialLifecycle] = useState<ProductionUnitMaterialLifecycleResponse | null>(null);
  const [exportPackage, setExportPackage] = useState<TraceRecordExportPackageResponse | null>(null);
  const [safetyEvidence, setSafetyEvidence] = useState<StationEmergencyStopResponse[]>([]);
  const [busy, setBusy] = useState(false);
  const selectedRow = useMemo(
    () => searchResult.results.items.find(row => row.traceRecordId === selectedTraceId) ?? null,
    [searchResult.results.items, selectedTraceId]);

  useEffect(() => {
    setFilters(createScopedFilters(projectId, applicationId));
    setSelectedTraceId('');
    setDetails(null);
    setMaterialLifecycle(null);
    setExportPackage(null);
    setSafetyEvidence([]);
  }, [applicationId, projectId]);

  const runSearch = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }
    setBusy(true);
    try {
      const [result, safetyResult] = await Promise.all([searchEngineeringTrace({
        productionUnitIdentityValue: filters.productionUnitIdentityValue,
        lotId: filters.lotId,
        carrierId: filters.carrierId,
        stationSystemId: filters.stationSystemId,
        deviceId: filters.deviceId,
        executionStatus: filters.executionStatus,
        judgement: filters.judgement,
        disposition: filters.disposition,
        processVersionId: filters.processVersionId,
        projectId: filters.projectId,
        applicationId: filters.applicationId,
        projectSnapshotId: filters.projectSnapshotId,
        productionLineDefinitionId: filters.productionLineDefinitionId,
        pageNumber: 1,
        pageSize: 25
      }), filters.projectId && filters.applicationId
        ? searchStationSafetyTrace({
            projectId: filters.projectId,
            applicationId: filters.applicationId,
            projectSnapshotId: filters.projectSnapshotId || undefined,
            stationSystemId: filters.stationSystemId || undefined
          })
        : Promise.resolve(null)]);
      const next = result ?? emptySearch;
      setSearchResult(next);
      setSafetyEvidence(safetyResult?.items ?? []);
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
  }, [onMessage, runSearch]);

  useEffect(() => {
    if (!isBackendHealthy || !selectedTraceId) {
      setDetails(null);
      setMaterialLifecycle(null);
      return;
    }
    let disposed = false;
    getTraceRecord(selectedTraceId)
      .then(async trace => {
        if (disposed) {
          return;
        }
        setDetails(trace);
        setMaterialLifecycle(null);
        setExportPackage(null);
        if (!trace) {
          return;
        }
        const lifecycle = await getProductionUnitMaterialLifecycle(trace.productionUnitId);
        if (!disposed) {
          setMaterialLifecycle(lifecycle);
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
        ? `Production trace export loaded ${result.packageFormat}`
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
          <button type="button" className="button ghost" onClick={() => void runSearch()} disabled={!isBackendHealthy || busy} data-testid="trace-search-run">
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
            <FacetGroup title="Execution Status" facets={searchResult.facets.executionStatuses} />
            <FacetGroup title="Disposition" facets={searchResult.facets.dispositions} />
            <FacetGroup title="Production Line" facets={searchResult.facets.productionLines} />
            <FacetGroup title="Station" facets={searchResult.facets.stationSystems} />
            <FacetGroup title="Device" facets={searchResult.facets.devices} />
            <FacetGroup title="Process Version" facets={searchResult.facets.processVersions} />
            <FacetGroup title="Project Snapshot" facets={searchResult.facets.projectSnapshots} />
          </div>
        </details>
        <details className="trace-safety-evidence" open data-testid="trace-station-safety-evidence">
          <summary>
            <div><ShieldAlert size={15} /><strong>Station safety evidence</strong></div>
            <span>{safetyEvidence.length} events</span>
          </summary>
          <div>
            {safetyEvidence.map(event => (
              <article key={event.idempotencyKey} className={`status-${event.status.toLowerCase()}`}>
                <span><strong>{event.status}</strong><small>{event.stationSystemId} · {formatDateTime(event.requestedAtUtc)}</small></span>
                <p>{event.reason}</p>
                <small>{event.actorId} · {event.evidence.length} evidence records · {event.dispatchAttemptCount} dispatch attempts</small>
                {event.relatedProductionRunIds.map(runId => (
                  <button type="button" key={runId} onClick={() => setSelectedTraceId(runId)}>
                    Open related run {runId}
                  </button>
                ))}
              </article>
            ))}
            {safetyEvidence.length === 0 ? <p>No Station Emergency Stop evidence matches this Trace scope.</p> : null}
          </div>
        </details>
      </div>

      <div className="panel trace-detail-panel">
        <div className="panel-title">
          <div><Boxes size={17} /><h2>Run Evidence</h2></div>
          <span>{selectedRow?.executionStatus ?? 'none'}</span>
        </div>
        {details ? (
          <TraceDetails
            details={details}
            materialLifecycle={materialLifecycle}
            exportPackage={exportPackage}
            onExport={loadExportPackage}
            onMessage={onMessage}
            disabled={busy}
          />
        ) : (
          <div className="trace-empty">Select a production run to inspect its Operation evidence.</div>
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
        <TraceTextField label="Production Unit identity" value={filters.productionUnitIdentityValue} onChange={value => update('productionUnitIdentityValue', value)} />
        <TraceTextField label="Lot" value={filters.lotId} onChange={value => update('lotId', value)} />
        <TraceTextField label="Carrier" value={filters.carrierId} onChange={value => update('carrierId', value)} />
        <TraceTextField label="Production line" value={filters.productionLineDefinitionId} onChange={value => update('productionLineDefinitionId', value)} />
        <TraceTextField label="Execution status" value={filters.executionStatus} onChange={value => update('executionStatus', value)} />
        <TraceTextField label="Judgement" value={filters.judgement} onChange={value => update('judgement', value)} />
        <TraceTextField label="Disposition" value={filters.disposition} onChange={value => update('disposition', value)} />
        <TraceTextField label="Station System" value={filters.stationSystemId} onChange={value => update('stationSystemId', value)} />
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
      <div className="trace-result-head"><span>Production runs</span><span>{rows.length} loaded</span></div>
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
            <strong title={`${row.productModelId} · ${row.productionUnitIdentityInputKey}`}>{row.productionUnitIdentityValue}</strong>
            <small>{row.productionLineDefinitionId}</small>
          </span>
          <span className="trace-result-state"><RunStatus value={row.executionStatus} /><TraceJudgement value={row.judgement} /></span>
          <small className="trace-result-id">{row.productionRunId}</small>
          <span className="trace-result-counts">{row.operationCount} Operations · {row.commandCount} commands</span>
        </button>
      ))}
    </div>
  );
}

function TraceDetails({
  details,
  materialLifecycle,
  exportPackage,
  onExport,
  onMessage,
  disabled
}: {
  details: TraceRecordResponse;
  materialLifecycle: ProductionUnitMaterialLifecycleResponse | null;
  exportPackage: TraceRecordExportPackageResponse | null;
  onExport(): Promise<void>;
  onMessage(message: string): void;
  disabled: boolean;
}): React.ReactElement {
  return (
    <div className="trace-detail-card" data-testid="trace-detail-panel">
      <div className="trace-detail-header">
        <TraceJudgement value={details.judgement} />
        <div>
          <strong>{details.productionUnitIdentityValue}</strong>
          <span>{details.productModelId} · {details.productionUnitIdentityInputKey} · {details.productionRunId}</span>
        </div>
      </div>
      <div className="trace-run-state">
        <RunStatus value={details.executionStatus} />
        <span>{details.judgement}</span><span>{details.disposition}</span><span>{details.operations.length} Operations</span>
      </div>
      <dl>
        <dt>Project</dt><dd>{details.projectId} / {details.applicationId}</dd>
        <dt>Snapshot</dt><dd>{details.projectSnapshotId}</dd>
        <dt>Topology</dt><dd>{details.topologyId}</dd>
        <dt>Lot</dt><dd>{details.lotId ?? 'n/a'}</dd>
        <dt>Carrier</dt><dd>{details.carrierId ?? 'n/a'}</dd>
        <dt>Actor</dt><dd>{details.actorId}</dd>
        <dt>Started</dt><dd>{formatOptionalDateTime(details.startedAtUtc)}</dd>
        <dt>Completed</dt><dd>{formatDateTime(details.completedAtUtc)}</dd>
        {details.failureCode ? <><dt>Failure</dt><dd>{details.failureCode} · {details.failureReason}</dd></> : null}
      </dl>
      <button type="button" className="button ghost" onClick={() => void onExport()} disabled={disabled} data-testid="trace-export-package">
        <Download size={15} />Export Immutable Package
      </button>
      {exportPackage ? (
        <div className="trace-export-box"><strong>{exportPackage.packageFormat}</strong><span>{formatDateTime(exportPackage.exportedAtUtc)}</span></div>
      ) : null}
      <TraceRunMaterialEvidence details={details} />
      <TraceProductMaterialLifecycle
        lifecycle={materialLifecycle}
        runCompletedAtUtc={details.completedAtUtc}
      />
      <div className="trace-operation-stack">
        {details.operations.map(operation => (
          <TraceOperationCard
            key={operation.operationRunId}
            operation={operation}
            onMessage={onMessage}
          />
        ))}
      </div>
      <section className="trace-route-decisions">
        <strong>Route decisions</strong>
        {details.routeDecisions.map(decision => (
          <article key={`${decision.sourceOperationRunId}-${decision.transitionId}-${decision.traversal}`}>
            <span>{decision.transitionId}</span>
            <strong>
              {decision.sourceJudgement} → {decision.targetOperationId
                ?? `Terminal: ${decision.terminalDisposition}`}
            </strong>
            <small>traversal {decision.traversal} · {formatDateTime(decision.decidedAtUtc)}</small>
          </article>
        ))}
        {details.routeDecisions.length === 0 ? <p>No route decisions</p> : null}
      </section>
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

function TraceOperationCard({
  operation,
  onMessage
}: {
  operation: TraceOperationExecutionResponse;
  onMessage(message: string): void;
}): React.ReactElement {
  const isFailed = operation.executionStatus === 'Failed'
    || operation.executionStatus === 'Canceled'
    || operation.executionStatus === 'TimedOut'
    || operation.executionStatus === 'Rejected';
  return (
    <details
      className={isFailed ? 'trace-operation-card failed' : 'trace-operation-card'}
      open={operation.attempt === 1 || isFailed}
      data-testid="trace-operation-card"
    >
      <summary>
        <span className="trace-operation-attempt">{String(operation.attempt).padStart(2, '0')}</span>
        <div><strong>{operation.operationId}</strong><small>{operation.stationSystemId} · {operation.stationId}</small></div>
        <RunStatus value={operation.executionStatus} />
        <TraceJudgement value={operation.judgement} />
        <ChevronRight size={14} />
      </summary>
      <div className="trace-operation-body">
        <div className="trace-operation-metadata">
          <span><small>Process</small><strong>{operation.processVersionId}</strong></span>
          <span><small>Configuration</small><strong>{operation.configurationSnapshotId}</strong></span>
          <span><small>Recipe</small><strong>{operation.recipeSnapshotId}</strong></span>
          <span><small>Session</small><strong>{operation.runtimeSessionStatus ?? 'not started'}</strong></span>
        </div>
        {operation.failureCode ? (
          <div className="trace-operation-failure"><AlertTriangle size={14} /><span>{operation.failureCode} · {operation.failureReason}</span></div>
        ) : null}
        <div className="trace-operation-counts">
          <span>{operation.completedStepCount} steps</span><span>{operation.commandCount} commands</span>
          <span>{operation.incidentCount} incidents</span><span>{operation.outputs.length} outputs</span>
        </div>
        <div className="trace-evidence-grid">
          <EvidenceSection title="Commands" emptyText="No commands">
            {operation.commands.slice(0, 8).map(command => (
              <article key={command.runtimeCommandId}>
                <span>{command.commandName}</span><strong>{command.executionStatus} · {command.resultJudgement}</strong>
                <small>{command.actionId} · {command.targetKind}:{command.targetId}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Production Context Outputs" emptyText="No outputs">
            {operation.outputs.slice(0, 8).map(output => (
              <article key={output.key}><span>{output.key}</span><strong>{output.valueKind}</strong><small>{output.canonicalJson}</small></article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Measurements" emptyText="No measurements">
            {operation.measurements.slice(0, 8).map(measurement => (
              <article key={measurement.measurementRecordId}>
                <span>{measurement.name}</span>
                <strong>{formatMeasurementValue(measurement.numericValue, measurement.textValue, measurement.unit)}</strong>
                <small>{measurement.commandExecutionStatus} · {measurement.commandResultJudgement} · {measurement.passed === false ? 'failed' : measurement.passed === true ? 'passed' : 'indeterminate'}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Resource Fencing" emptyText="No fencing tokens">
            {operation.fencingTokens.slice(0, 8).map(resource => (
              <article key={`${resource.resourceKind}:${resource.resourceId}`}>
                <span>{resource.resourceKind}</span><strong>{resource.resourceId}</strong><small>token {resource.fencingToken}</small>
              </article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Incidents" emptyText="No incidents">
            {operation.incidents.slice(0, 8).map(incident => (
              <article key={incident.runtimeIncidentId}><span>{incident.code}</span><strong>{incident.severity}</strong><small>{incident.message}</small></article>
            ))}
          </EvidenceSection>
          <EvidenceSection title="Artifacts" emptyText="No artifacts">
            {operation.artifacts.slice(0, 8).map(artifact => (
              <article key={artifact.artifactRecordId}>
                <span>{artifact.name}</span><strong>{artifact.kind}</strong>
                <small>{artifact.storageKey}</small>
                <button
                  type="button"
                  className="button ghost"
                  disabled={!artifact.sha256}
                  data-testid={`trace-artifact-save-${artifact.artifactRecordId}`}
                  onClick={() => void saveTraceArtifact(artifact, onMessage)}
                >
                  <Download size={13} />Save verified evidence
                </button>
              </article>
            ))}
          </EvidenceSection>
        </div>
      </div>
    </details>
  );
}

function TraceRunMaterialEvidence({
  details
}: {
  details: TraceRecordResponse;
}): React.ReactElement {
  const evidenceCount = details.genealogy.length
    + details.materialLocationTransitions.length
    + details.slotOccupancyTransitions.length
    + details.dispositionTransitions.length;
  return (
    <details className="trace-material-evidence" open data-testid="trace-run-material-evidence">
      <summary>
        <span>
          <strong>Immutable run material evidence</strong>
          <small>Frozen through {formatDateTime(details.completedAtUtc)}</small>
        </span>
        <em>{evidenceCount} records</em>
      </summary>
      <div className="trace-material-evidence-grid">
        <EvidenceSection title="Material movement" emptyText="No material movement">
          {details.materialLocationTransitions.map(transition => (
            <article key={transition.evidenceId}>
              <span>{transition.materialKind} {transition.materialId}</span>
              <strong>{formatMaterialLocation(transition.source)} → {formatMaterialLocation(transition.destination)}</strong>
              <small>{formatDateTime(transition.occurredAtUtc)} · {transition.actorId}</small>
            </article>
          ))}
        </EvidenceSection>
        <EvidenceSection title="Slot occupancy" emptyText="No Slot transitions">
          {details.slotOccupancyTransitions.map(transition => (
            <article key={transition.evidenceId}>
              <span>{transition.stationSystemId} / {transition.slotId}</span>
              <strong>{transition.previousStatus} → {transition.currentStatus}</strong>
              <small>{transition.materialKind ?? 'Empty'} {transition.materialId ?? ''} · {formatDateTime(transition.occurredAtUtc)}</small>
            </article>
          ))}
        </EvidenceSection>
        <EvidenceSection title="Disposition" emptyText="No disposition transitions">
          {details.dispositionTransitions.map(transition => (
            <article key={transition.evidenceId}>
              <span>{transition.previousDisposition} → {transition.currentDisposition}</span>
              <strong>{transition.reason ?? 'No reason recorded'}</strong>
              <small>{formatDateTime(transition.occurredAtUtc)} · {transition.actorId}</small>
            </article>
          ))}
        </EvidenceSection>
        <EvidenceSection title="Genealogy" emptyText="No genealogy links">
          {details.genealogy.map(link => (
            <article key={link.linkId}>
              <span>{link.parentProductionUnitId} → {link.childProductionUnitId}</span>
              <strong>{link.relationship}</strong>
              <small>{link.operationId} · {formatDateTime(link.linkedAtUtc)}</small>
            </article>
          ))}
        </EvidenceSection>
      </div>
    </details>
  );
}

function TraceProductMaterialLifecycle({
  lifecycle,
  runCompletedAtUtc
}: {
  lifecycle: ProductionUnitMaterialLifecycleResponse | null;
  runCompletedAtUtc: string;
}): React.ReactElement {
  if (!lifecycle) {
    return (
      <section className="trace-product-lifecycle loading" data-testid="trace-product-material-lifecycle">
        <strong>Product material lifecycle</strong>
        <span>Loading the latest persisted material projection…</span>
      </section>
    );
  }
  const isPostRun = (occurredAtUtc: string): boolean => (
    Date.parse(occurredAtUtc) > Date.parse(runCompletedAtUtc));
  const postRunCount = lifecycle.materialLocationTransitions
    .filter(transition => isPostRun(transition.occurredAtUtc)).length
    + lifecycle.slotOccupancyTransitions
      .filter(transition => isPostRun(transition.occurredAtUtc)).length
    + lifecycle.dispositionTransitions
      .filter(transition => isPostRun(transition.occurredAtUtc)).length;
  return (
    <section className="trace-product-lifecycle" data-testid="trace-product-material-lifecycle">
      <header>
        <span>
          <strong>Product material lifecycle</strong>
          <small>Observed through {formatDateTime(lifecycle.observedThroughUtc)}</small>
        </span>
        <em>{postRunCount} after-run records</em>
      </header>
      <dl>
        <dt>Current location</dt><dd>{formatMaterialLocation(lifecycle.currentLocation)}</dd>
        <dt>Carrier location</dt><dd>{formatMaterialLocation(lifecycle.currentCarrierLocation)}</dd>
        <dt>Disposition</dt><dd>{lifecycle.currentDisposition}{lifecycle.dispositionReason ? ` · ${lifecycle.dispositionReason}` : ''}</dd>
      </dl>
      <div className="trace-material-evidence-grid">
        <EvidenceSection title="Complete movement timeline" emptyText="No material movement">
          {lifecycle.materialLocationTransitions.map(transition => {
            const afterRun = isPostRun(transition.occurredAtUtc);
            return (
              <article
                key={transition.evidenceId}
                className={afterRun ? 'post-run' : undefined}
                data-testid={afterRun ? 'trace-product-post-run-location-transition' : undefined}
              >
                <span>{transition.materialKind} {transition.materialId}</span>
                <strong>{formatMaterialLocation(transition.source)} → {formatMaterialLocation(transition.destination)}</strong>
                <small>{afterRun ? 'After run · ' : ''}{formatDateTime(transition.occurredAtUtc)} · {transition.actorId}</small>
              </article>
            );
          })}
        </EvidenceSection>
        <EvidenceSection title="Complete Slot timeline" emptyText="No Slot transitions">
          {lifecycle.slotOccupancyTransitions.map(transition => {
            const afterRun = isPostRun(transition.occurredAtUtc);
            return (
              <article
                key={transition.evidenceId}
                className={afterRun ? 'post-run' : undefined}
                data-testid={afterRun ? 'trace-product-post-run-slot-transition' : undefined}
              >
                <span>{transition.stationSystemId} / {transition.slotId}</span>
                <strong>{transition.previousStatus} → {transition.currentStatus}</strong>
                <small>{afterRun ? 'After run · ' : ''}{transition.materialKind ?? 'Empty'} {transition.materialId ?? ''} · {formatDateTime(transition.occurredAtUtc)}</small>
              </article>
            );
          })}
        </EvidenceSection>
        <EvidenceSection title="Complete disposition timeline" emptyText="No disposition transitions">
          {lifecycle.dispositionTransitions.map(transition => {
            const afterRun = isPostRun(transition.occurredAtUtc);
            return (
              <article key={transition.evidenceId} className={afterRun ? 'post-run' : undefined}>
                <span>{transition.previousDisposition} → {transition.currentDisposition}</span>
                <strong>{transition.reason ?? 'No reason recorded'}</strong>
                <small>{afterRun ? 'After run · ' : ''}{formatDateTime(transition.occurredAtUtc)} · {transition.actorId}</small>
              </article>
            );
          })}
        </EvidenceSection>
        <EvidenceSection title="Complete genealogy" emptyText="No genealogy links">
          {lifecycle.genealogy.map(link => (
            <article key={link.linkId}>
              <span>{link.parentProductionUnitId} → {link.childProductionUnitId}</span>
              <strong>{link.relationship}</strong>
              <small>{link.operationId} · {formatDateTime(link.linkedAtUtc)}</small>
            </article>
          ))}
        </EvidenceSection>
      </div>
    </section>
  );
}

function formatMaterialLocation(
  location: TraceRecordResponse['materialLocationTransitions'][number]['destination'] | null
): string {
  if (!location) {
    return 'Unlocated';
  }
  return [
    location.kind,
    location.lineId,
    location.stationSystemId,
    location.slotId,
    location.carrierId,
    location.carrierPositionId
  ].filter((value): value is string => Boolean(value)).join(' / ');
}

async function saveTraceArtifact(
  artifact: ArtifactRecordResponse,
  onMessage: (message: string) => void
): Promise<void> {
  if (!artifact.sha256) {
    onMessage(`Artifact ${artifact.name} has no immutable SHA-256 and cannot be saved`);
    return;
  }

  try {
    const result = await desktop.saveTraceArtifact({
      storageKey: artifact.storageKey,
      fileName: artifact.name,
      expectedSizeBytes: artifact.sizeBytes,
      expectedSha256: artifact.sha256
    });
    onMessage(result.canceled
      ? `Artifact save canceled: ${artifact.name}`
      : `Verified artifact saved: ${result.path}`);
  } catch (error) {
    onMessage(`Artifact save failed: ${String(error)}`);
  }
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
    : value === 'Failed' || value === 'TimedOut' || value === 'Rejected'
      ? 'failed'
      : value === 'Canceled'
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
    productionUnitIdentityValue: '',
    lotId: '',
    carrierId: '',
    stationSystemId: '',
    deviceId: '',
    executionStatus: '',
    judgement: '',
    disposition: '',
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
