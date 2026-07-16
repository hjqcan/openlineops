import React, { memo, useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowRight,
  Boxes,
  CircleDot,
  Factory,
  Flag,
  GitBranch,
  Hand,
  ListFilter,
  LockOpen,
  Map as MapIcon,
  Pause,
  Play,
  Radio,
  RefreshCw,
  RotateCcw,
  ShieldAlert,
  Square,
  Trash2
} from 'lucide-react';
import type {
  OperatorProductionRunCommand,
  ProductionLineRuntimeStateResponse,
  ProductionContextValueRequest,
  ProductionOperationsFilters,
  ProductionOperationRunReadModel,
  ProductionRunReadModel,
  StationEmergencyStopResponse
} from './contracts';
import {
  commandProductionRun,
  getProjectSnapshotProductionRunContext,
  getStationSafetyEvents,
  requestStationEmergencyStop
} from './api';
import {
  buildProductionLineRuntimeView,
  type ProductionLineCarrierView,
  type ProductionLineStationView
} from './production-line-runtime-view';
import { canIssueProductionRunCommand } from './production-run-command-policy';
import {
  buildProductionRouteRuntimeProjection,
  type ProductionRouteRuntimeMovement,
  type ProductionRouteRuntimeProjection
} from './production-route-runtime-projection';

interface OperationsWorkbenchProps {
  activeRuns: ProductionRunReadModel[];
  lineState: ProductionLineRuntimeStateResponse | null;
  filters: ProductionOperationsFilters;
  selectedLineId: string;
  connected: boolean;
  refreshing: boolean;
  isBackendHealthy: boolean;
  lastSynchronizedAtUtc: string | null;
  projectId: string | null;
  applicationId: string | null;
  projectSnapshotId: string | null;
  actorId: string;
  onFilterChanged(filter: keyof ProductionOperationsFilters, value: string): void;
  onRefresh(): Promise<void>;
  onOpenTopology(): void;
  onActiveRunChanged(productionRunId: string): void;
  onMessage(message: string): void;
}

interface CommandDefinition {
  command: OperatorProductionRunCommand;
  label: string;
  description: string;
  icon: React.ComponentType<{ size?: number }>;
  tone: 'normal' | 'warning' | 'danger';
}

const commandDefinitions: CommandDefinition[] = [
  { command: 'Pause', label: 'Pause', description: 'Pause after the current safe boundary.', icon: Pause, tone: 'normal' },
  { command: 'Continue', label: 'Continue', description: 'Resume a paused production run.', icon: Play, tone: 'normal' },
  { command: 'Stop', label: 'Stop', description: 'Finish the current Operation, then stop at its safe boundary.', icon: Square, tone: 'warning' },
  { command: 'Cancel', label: 'Cancel', description: 'Immediately terminate the active Operation and its vendor process tree.', icon: AlertTriangle, tone: 'danger' },
  { command: 'Hold', label: 'Hold', description: 'Hold this product for operator disposition.', icon: Hand, tone: 'warning' },
  { command: 'Release', label: 'Release', description: 'Release a held product back to its route.', icon: LockOpen, tone: 'normal' },
  { command: 'Rework', label: 'Rework', description: 'Return to one explicit earlier Operation.', icon: RotateCcw, tone: 'warning' },
  { command: 'Scrap', label: 'Scrap', description: 'Terminally scrap this product and preserve evidence.', icon: Trash2, tone: 'danger' },
  { command: 'SafeStop', label: 'Safe Stop', description: 'Request the Agent safety stop path.', icon: ShieldAlert, tone: 'danger' },
  { command: 'Reconcile', label: 'Reconcile', description: 'Record observed completion without replaying hardware.', icon: CircleDot, tone: 'warning' },
  { command: 'Retry', label: 'Retry', description: 'Release recovery hold and retry one Operation.', icon: RotateCcw, tone: 'warning' },
  { command: 'Abort', label: 'Abort', description: 'Abort this run from Recovery Required with evidence.', icon: AlertTriangle, tone: 'danger' }
];

export function OperationsWorkbench({
  activeRuns,
  lineState,
  filters,
  selectedLineId,
  connected,
  refreshing,
  isBackendHealthy,
  lastSynchronizedAtUtc,
  projectId,
  applicationId,
  projectSnapshotId,
  actorId,
  onFilterChanged,
  onRefresh,
  onOpenTopology,
  onActiveRunChanged,
  onMessage
}: OperationsWorkbenchProps): React.ReactElement {
  const [selectedRunId, setSelectedRunId] = useState('');
  const [pendingCommand, setPendingCommand] = useState<OperatorProductionRunCommand | null>(null);
  const [reason, setReason] = useState('');
  const [operationId, setOperationId] = useState('');
  const [recoveryOperationRunId, setRecoveryOperationRunId] = useState('');
  const [recoveryJudgement, setRecoveryJudgement] = useState<'Passed' | 'Failed' | 'NotApplicable'>('Passed');
  const [recoveryOutputsJson, setRecoveryOutputsJson] = useState('{}');
  const [evidenceReference, setEvidenceReference] = useState('');
  const [commandBusy, setCommandBusy] = useState(false);
  const [safetyEvents, setSafetyEvents] = useState<StationEmergencyStopResponse[]>([]);
  const [safetyError, setSafetyError] = useState('');
  const [safetyBusy, setSafetyBusy] = useState(false);
  const [deployedStationSystemIds, setDeployedStationSystemIds] = useState<string[]>([]);
  const [deploymentStationError, setDeploymentStationError] = useState('');
  const [pendingEmergencyStationSystemId, setPendingEmergencyStationSystemId] = useState('');
  const [emergencyReason, setEmergencyReason] = useState('');
  const [emergencyConfirmation, setEmergencyConfirmation] = useState('');
  const visibleRuns = activeRuns;
  const visibleRunIds = useMemo(
    () => new Set(visibleRuns.map(run => run.productionRunId)),
    [visibleRuns]);
  const lineRuns = useMemo(
    () => (lineState?.activeRuns ?? []).filter(run => visibleRunIds.has(run.productionRunId)),
    [lineState, visibleRunIds]);
  const runtimeView = useMemo(
    () => lineState ? buildProductionLineRuntimeView(lineState) : null,
    [lineState]);
  const runById = useMemo(
    () => new Map([...lineRuns, ...visibleRuns].map(run => [run.productionRunId, run])),
    [lineRuns, visibleRuns]);
  const selectedRun = runById.get(selectedRunId) ?? null;
  const stationOverviews = useMemo(() => {
    const runtimeStations = new Map(
      (runtimeView?.stations ?? []).map(station => [station.stationSystemId, station]));
    return uniqueSorted([
      ...deployedStationSystemIds,
      ...runtimeStations.keys()
    ])
      .map(stationSystemId => runtimeStations.get(stationSystemId)
        ?? idleStationView(stationSystemId))
      .filter(station => (
        (!filters.stationSystemId || station.stationSystemId === filters.stationSystemId)
        && (!filters.slotId || station.slots.some(slot => slot.slotId === filters.slotId))));
  }, [deployedStationSystemIds, filters.slotId, filters.stationSystemId, runtimeView]);
  const lineIds = useMemo(
    () => uniqueSorted([
      selectedLineId,
      lineState?.productionLineDefinitionId ?? '',
      ...lineRuns.map(run => run.productionLineDefinitionId),
      ...visibleRuns.map(run => run.productionLineDefinitionId)
    ]),
    [lineRuns, lineState?.productionLineDefinitionId, selectedLineId, visibleRuns]);
  const stationIds = useMemo(
    () => uniqueSorted([
      ...deployedStationSystemIds,
      ...(lineState?.stations.map(station => station.stationSystemId) ?? [])
    ]),
    [deployedStationSystemIds, lineState]);
  const slotIds = useMemo(
    () => uniqueSorted((lineState?.slots ?? [])
      .filter(slot => !filters.stationSystemId || slot.stationSystemId === filters.stationSystemId)
      .map(slot => slot.slotId)),
    [filters.stationSystemId, lineState]);
  const pendingDefinition = pendingCommand
    ? commandDefinitions.find(definition => definition.command === pendingCommand) ?? null
    : null;
  const visibleCommandDefinitions = selectedRun?.controlState === 'RecoveryRequired'
    ? commandDefinitions.filter(definition => ['Reconcile', 'Retry', 'Abort', 'Scrap'].includes(definition.command))
    : commandDefinitions.filter(definition => !['Reconcile', 'Retry', 'Abort'].includes(definition.command));

  useEffect(() => {
    setSelectedRunId(current => {
      if (runById.has(current)) {
        return current;
      }
      return visibleRuns[0]?.productionRunId ?? lineRuns[0]?.productionRunId ?? '';
    });
  }, [lineRuns, runById, visibleRuns]);

  useEffect(() => {
    if (selectedRunId) {
      onActiveRunChanged(selectedRunId);
    }
  }, [onActiveRunChanged, selectedRunId]);

  useEffect(() => {
    if (!selectedRun) {
      setPendingCommand(null);
      return;
    }
    setOperationId(current => selectedRun.operations.some(operation => operation.operationId === current)
      ? current
      : currentOperation(selectedRun)?.operationId ?? selectedRun.entryOperationId);
    const running = selectedRun.operations.find(operation => operation.executionStatus === 'Running');
    setRecoveryOperationRunId(current => selectedRun.operations.some(operation => (
      operation.operationRunId === current && operation.executionStatus === 'Running'))
      ? current
      : running?.operationRunId ?? '');
  }, [selectedRun]);

  useEffect(() => {
    let canceled = false;
    setDeployedStationSystemIds([]);
    setDeploymentStationError('');
    if (!isBackendHealthy || !projectId || !applicationId || !projectSnapshotId) {
      return () => {
        canceled = true;
      };
    }

    void (async () => {
      try {
        const response = await getProjectSnapshotProductionRunContext(
          projectId,
          projectSnapshotId);
        if (!response.ok || !response.body) {
          throw new Error(`${response.status} ${response.text}`);
        }
        const context = response.body;
        if (context.projectId !== projectId
            || context.applicationId !== applicationId
            || context.snapshotId !== projectSnapshotId) {
          throw new Error('Frozen Station deployment context identity does not match the active workspace.');
        }
        const stationSystemIds = uniqueSorted(context.stationSystemIds);
        if (stationSystemIds.length === 0
            || stationSystemIds.some(stationSystemId => !isCanonical(stationSystemId))
            || stationSystemIds.length !== context.stationSystemIds.length
            || stationSystemIds.some((stationSystemId, index) => (
              stationSystemId !== context.stationSystemIds[index]))) {
          throw new Error('Frozen Station deployment context is not canonical, unique, and sorted.');
        }
        if (!canceled) {
          setDeployedStationSystemIds(stationSystemIds);
        }
      } catch (error) {
        if (!canceled) {
          setDeploymentStationError(String(error));
        }
      }
    })();

    return () => {
      canceled = true;
    };
  }, [applicationId, isBackendHealthy, projectId, projectSnapshotId]);

  const refreshSafetyEvents = useCallback(async (): Promise<void> => {
    if (!isBackendHealthy || !projectId || !applicationId) {
      setSafetyEvents([]);
      return;
    }
    try {
      const response = await getStationSafetyEvents({
        projectId,
        applicationId,
        projectSnapshotId: projectSnapshotId ?? undefined
      });
      if (!response.ok || !response.body) {
        throw new Error(`${response.status} ${response.text}`);
      }
      setSafetyEvents(response.body.events);
      setSafetyError('');
    } catch (error) {
      setSafetyError(String(error));
    }
  }, [applicationId, isBackendHealthy, projectId, projectSnapshotId]);

  useEffect(() => {
    void refreshSafetyEvents();
  }, [lineState?.generatedAtUtc, refreshSafetyEvents]);

  const submitCommand = async (): Promise<void> => {
    if (!selectedRun || !pendingCommand || !pendingDefinition) {
      return;
    }
    if (requiresReason(pendingCommand) && !isCanonical(reason)) {
      onMessage(`${pendingDefinition.label} requires a canonical operator reason.`);
      return;
    }
    if (pendingCommand === 'Rework' && !isCanonical(operationId)) {
      onMessage('Rework requires one explicit Operation ID.');
      return;
    }
    const recovery = requiresRecoveryDecision(pendingCommand, selectedRun);
    if (recovery && !isCanonical(evidenceReference)) {
      onMessage(`${pendingDefinition.label} requires an immutable evidence reference.`);
      return;
    }
    if (pendingCommand === 'Reconcile' && !selectedRun.operations.some(operation => (
      operation.operationRunId === recoveryOperationRunId
      && operation.executionStatus === 'Running'))) {
      onMessage('Reconcile requires the exact running Operation Run ID.');
      return;
    }
    if (pendingCommand === 'Retry' && !isCanonical(operationId)) {
      onMessage('Retry requires one explicit Operation ID.');
      return;
    }

    let observedOutputs: Record<string, ProductionContextValueRequest> = {};
    if (pendingCommand === 'Reconcile') {
      try {
        observedOutputs = parseTypedOutputs(recoveryOutputsJson);
      } catch (error) {
        onMessage(`Observed outputs are invalid: ${String(error)}`);
        return;
      }
    }
    const recoveryDecision = recovery ? {
      decisionId: crypto.randomUUID(),
      evidenceReference,
      decidedAtUtc: new Date().toISOString(),
      operationRunId: pendingCommand === 'Reconcile' ? recoveryOperationRunId : null,
      operationId: pendingCommand === 'Retry' ? operationId : null,
      observedJudgement: pendingCommand === 'Reconcile' ? recoveryJudgement : null,
      observedOutputs: pendingCommand === 'Reconcile' ? observedOutputs : {}
    } : null;

    setCommandBusy(true);
    try {
      const response = await commandProductionRun(selectedRun.productionRunId, pendingCommand, {
        reason: reason || null,
        operationId: pendingCommand === 'Rework' ? operationId : null,
        recoveryDecision
      });
      if (!response.ok || !response.body) {
        onMessage(`${pendingDefinition.label} rejected: ${response.status} ${response.text}`);
        return;
      }
      onMessage(`${pendingDefinition.label} accepted for ${response.body.productionUnitIdentity.value}`);
      setPendingCommand(null);
      setReason('');
      setEvidenceReference('');
      setRecoveryOutputsJson('{}');
      await onRefresh();
    } catch (error) {
      onMessage(`${pendingDefinition.label} failed: ${String(error)}`);
    } finally {
      setCommandBusy(false);
    }
  };

  const issueEmergencyStop = async (
    existing: StationEmergencyStopResponse | null = null
  ): Promise<void> => {
    const stationSystemId = existing?.stationSystemId ?? pendingEmergencyStationSystemId;
    if (!isCanonical(stationSystemId)
        || !projectId
        || !applicationId
        || !projectSnapshotId) {
      onMessage('Emergency Stop requires one explicit Station and frozen Project snapshot.');
      return;
    }
    if (!existing) {
      if (!isCanonical(emergencyReason)) {
        onMessage('Emergency Stop requires an operator reason.');
        return;
      }
      if (emergencyConfirmation !== stationSystemId) {
        onMessage(`Type the exact Station System ID '${stationSystemId}' to confirm Emergency Stop.`);
        return;
      }
    }

    setSafetyBusy(true);
    try {
      const request = existing ? {
        messageId: existing.messageId,
        idempotencyKey: existing.idempotencyKey,
        projectId: existing.projectId,
        applicationId: existing.applicationId,
        projectSnapshotId: existing.projectSnapshotId,
        reason: existing.reason,
        requestedAtUtc: existing.requestedAtUtc
      } : {
        messageId: crypto.randomUUID(),
        idempotencyKey: crypto.randomUUID(),
        projectId,
        applicationId,
        projectSnapshotId,
        reason: emergencyReason,
        requestedAtUtc: new Date().toISOString()
      };
      const response = await requestStationEmergencyStop(stationSystemId, request);
      if (!response.ok || !response.body) {
        onMessage(`Emergency Stop rejected: ${response.status} ${response.text}`);
        return;
      }
      onMessage(`Emergency Stop ${response.body.status} for ${stationSystemId}.`);
      setPendingEmergencyStationSystemId('');
      setEmergencyReason('');
      setEmergencyConfirmation('');
      await refreshSafetyEvents();
    } catch (error) {
      onMessage(`Emergency Stop failed: ${String(error)}`);
    } finally {
      setSafetyBusy(false);
    }
  };

  return (
    <section className="operations-workbench" data-testid="operations-workbench">
      <header className="operations-command-bar">
        <div className="operations-title">
          <Radio size={18} />
          <span>
            <strong>Line Operations</strong>
            <small>{selectedLineId || 'Waiting for an active production line'}</small>
          </span>
        </div>
        <div className="operations-connection">
          <i className={connected ? 'connected' : 'disconnected'} />
          <span>
            <strong>{connected ? 'Projection connected' : 'Projection disconnected'}</strong>
            <small>{lastSynchronizedAtUtc ? `Synced ${formatTimestamp(lastSynchronizedAtUtc)}` : 'No synchronized state'}</small>
          </span>
        </div>
        <div className="operations-actions">
          <button type="button" className="button ghost" onClick={() => void onRefresh()} disabled={refreshing || !isBackendHealthy}>
            <RefreshCw size={14} className={refreshing ? 'spin' : ''} /> Refresh
          </button>
          <button type="button" className="button primary" onClick={onOpenTopology} disabled={!lineState}>
            <MapIcon size={14} /> Live Topology
          </button>
        </div>
      </header>

      <section className="operations-filter-bar">
        <div><ListFilter size={14} /><strong>Active Run Filters</strong></div>
        <label>
          <span>Line</span>
          <select
            value={filters.productionLineDefinitionId}
            onChange={event => onFilterChanged('productionLineDefinitionId', event.target.value)}
            data-testid="operations-filter-line"
          >
            <option value="">All active lines</option>
            {lineIds.map(lineId => <option key={lineId} value={lineId}>{lineId}</option>)}
          </select>
        </label>
        <label>
          <span>Station</span>
          <select
            value={filters.stationSystemId}
            onChange={event => onFilterChanged('stationSystemId', event.target.value)}
            data-testid="operations-filter-station"
          >
            <option value="">All stations</option>
            {stationIds.map(stationId => <option key={stationId} value={stationId}>{stationId}</option>)}
          </select>
        </label>
        <label>
          <span>Slot</span>
          <select
            value={filters.slotId}
            onChange={event => onFilterChanged('slotId', event.target.value)}
            data-testid="operations-filter-slot"
          >
            <option value="">All slots</option>
            {slotIds.map(slotId => <option key={slotId} value={slotId}>{slotId}</option>)}
          </select>
        </label>
        <button
          type="button"
          className="button ghost"
          onClick={() => onFilterChanged('productionLineDefinitionId', '')}
          disabled={!filters.productionLineDefinitionId && !filters.stationSystemId && !filters.slotId}
        >
          Clear
        </button>
      </section>

      {pendingEmergencyStationSystemId ? (
        <section className="operations-emergency-confirmation" role="alertdialog" aria-modal="true" data-testid="emergency-stop-confirmation">
          <div>
            <ShieldAlert size={24} />
            <span>
              <strong>EMERGENCY STOP · {pendingEmergencyStationSystemId}</strong>
              <small>This uses the independent Station safety channel. It is not Safe Stop and does not wait for a Production Run boundary.</small>
            </span>
          </div>
          <label>
            <span>Operator reason</span>
            <input value={emergencyReason} onChange={event => setEmergencyReason(event.target.value)} autoFocus data-testid="emergency-stop-reason" />
          </label>
          <label>
            <span>Type the exact Station System ID to confirm</span>
            <input value={emergencyConfirmation} onChange={event => setEmergencyConfirmation(event.target.value)} data-testid="emergency-stop-confirmation-text" />
          </label>
          <div>
            <button type="button" className="button ghost" onClick={() => setPendingEmergencyStationSystemId('')} disabled={safetyBusy} data-testid="cancel-emergency-stop">Cancel</button>
            <button type="button" className="button danger" onClick={() => void issueEmergencyStop()} disabled={safetyBusy || emergencyConfirmation !== pendingEmergencyStationSystemId} data-testid="confirm-emergency-stop">
              <ShieldAlert size={14} /> {safetyBusy ? 'Sending…' : 'Trigger Emergency Stop'}
            </button>
          </div>
        </section>
      ) : null}

      <div className="operations-layout">
        <aside className="operations-run-browser">
          <header><span>ACTIVE RUNS</span><small>{visibleRuns.length}</small></header>
          <div className="operations-run-list" data-testid="active-runs-list">
            {visibleRuns.map(run => (
              <RunListItem
                key={run.productionRunId}
                run={run}
                selected={run.productionRunId === selectedRunId}
                onSelect={() => setSelectedRunId(run.productionRunId)}
              />
            ))}
            {visibleRuns.length === 0 ? (
              <div className="operations-empty-list">
                <CircleDot size={20} />
                <strong>No matching active runs</strong>
                <span>Filters are applied by the Coordinator read model.</span>
              </div>
            ) : null}
          </div>
        </aside>

        <main className="operations-line-board">
          <header>
            <div>
              <Factory size={16} />
              <span>
                <strong>Line State</strong>
                <small>{lineState?.productionUnits.length ?? 0} WIP materials · {lineState?.activeRunCount ?? 0} active runs</small>
              </span>
            </div>
            <span data-testid="line-state-generated-at">{lineState ? formatTimestamp(lineState.generatedAtUtc) : 'not synchronized'}</span>
          </header>
          {deploymentStationError ? (
            <p className="operations-safety-error" data-testid="station-deployment-context-error">
              Frozen Station deployment context unavailable: {deploymentStationError}
            </p>
          ) : null}
          <div className="operations-station-grid">
            {stationOverviews.map(station => (
              <StationCard
                key={station.stationSystemId}
                station={station}
                onEmergencyStop={() => {
                  setPendingEmergencyStationSystemId(station.stationSystemId);
                  setEmergencyReason('');
                  setEmergencyConfirmation('');
                }}
              />
            ))}
            {stationOverviews.length === 0 ? (
              <div className="operations-line-empty">
                <Factory size={30} />
                <strong>The selected line has no persisted Station state</strong>
                <span>Queue, Slot occupancy and leases are restored from the Coordinator projection.</span>
              </div>
            ) : null}
          </div>
          {runtimeView && runtimeView.carriers.length > 0 ? (
            <section className="operations-carrier-board" data-testid="line-carriers">
              <header><Boxes size={14} /><strong>CARRIERS</strong><small>{runtimeView.carriers.length}</small></header>
              <div>
                {runtimeView.carriers.map(carrier => (
                  <CarrierCard key={carrier.carrierId} carrier={carrier} />
                ))}
              </div>
            </section>
          ) : null}
          <section className="operations-safety-board" data-testid="station-safety-events">
            <header>
              <span><ShieldAlert size={14} /><strong>STATION SAFETY EVIDENCE</strong></span>
              <small>{safetyEvents.length} persisted · independent channel</small>
            </header>
            {safetyError ? <p className="operations-safety-error">Safety evidence unavailable: {safetyError}</p> : null}
            <div>
              {safetyEvents.slice(0, 8).map(event => (
                <article key={event.idempotencyKey} className={`status-${event.status.toLowerCase()}`} data-testid={`emergency-stop-event-${event.idempotencyKey}`}>
                  <i />
                  <span>
                    <strong>{event.stationSystemId}</strong>
                    <small>{event.actorId} · {formatTimestamp(event.requestedAtUtc)}</small>
                    <em>{event.reason}</em>
                  </span>
                  <span>
                    <b>{event.status}</b>
                    <small>{event.failureCode ?? event.lastDispatchFailure ?? `${event.dispatchAttemptCount} dispatch attempt(s)`}</small>
                    {event.status === 'Pending' ? (
                      <button type="button" className="button danger" onClick={() => void issueEmergencyStop(event)} disabled={safetyBusy} data-testid={`retry-emergency-stop-${event.idempotencyKey}`}>
                        Retry same message
                      </button>
                    ) : null}
                  </span>
                </article>
              ))}
              {safetyEvents.length === 0 && !safetyError ? <p>No Emergency Stop evidence for this frozen Application snapshot.</p> : null}
            </div>
          </section>
        </main>

        <aside className="operations-run-detail">
          {selectedRun ? (
            <>
              <RunDetail run={selectedRun} />
              <section className="operations-command-section">
                <header><span>OPERATOR COMMANDS</span><small>{selectedRun.controlState}</small></header>
                <div className="operations-command-grid">
                  {visibleCommandDefinitions.map(definition => {
                    const Icon = definition.icon;
                    return (
                      <button
                        type="button"
                        key={definition.command}
                        className={`${definition.tone} ${pendingCommand === definition.command ? 'selected' : ''}`}
                        disabled={!canIssueProductionRunCommand(definition.command, selectedRun) || commandBusy}
                        onClick={() => {
                          setPendingCommand(definition.command);
                          setReason('');
                          setEvidenceReference('');
                          setRecoveryOutputsJson('{}');
                        }}
                        data-testid={`production-command-${definition.command}`}
                      >
                        <Icon size={14} />
                        <span><strong>{definition.label}</strong><small>{definition.description}</small></span>
                      </button>
                    );
                  })}
                </div>
                {pendingDefinition ? (
                  <div className={`operations-command-composer ${pendingDefinition.tone}`}>
                    <strong>{pendingDefinition.label} · confirmation</strong>
                    <label><span>Authenticated Actor</span><input value={actorId} readOnly data-testid="production-command-actor" /></label>
                    {requiresReason(pendingDefinition.command) ? (
                      <label><span>Reason</span><input value={reason} onChange={event => setReason(event.target.value)} autoFocus data-testid="production-command-reason" /></label>
                    ) : null}
                    {pendingDefinition.command === 'Rework' ? (
                      <label>
                        <span>Return to Operation</span>
                        <select value={operationId} onChange={event => setOperationId(event.target.value)} data-testid="production-command-operation">
                          {selectedRun.operations.map(operation => (
                            <option key={operation.operationRunId} value={operation.operationId}>
                              {operation.operationId} · attempt {operation.attempt}
                            </option>
                          ))}
                        </select>
                      </label>
                    ) : null}
                    {requiresRecoveryDecision(pendingDefinition.command, selectedRun) ? (
                      <label>
                        <span>Evidence reference</span>
                        <input value={evidenceReference} onChange={event => setEvidenceReference(event.target.value)} data-testid="recovery-evidence-reference" />
                      </label>
                    ) : null}
                    {pendingDefinition.command === 'Reconcile' ? (
                      <>
                        <label>
                          <span>Running Operation Run</span>
                          <select value={recoveryOperationRunId} onChange={event => setRecoveryOperationRunId(event.target.value)} data-testid="recovery-operation-run">
                            {selectedRun.operations
                              .filter(operation => operation.executionStatus === 'Running')
                              .map(operation => (
                                <option key={operation.operationRunId} value={operation.operationRunId}>
                                  {operation.operationRunId} · {operation.operationId}
                                </option>
                              ))}
                          </select>
                        </label>
                        <label>
                          <span>Observed judgement</span>
                          <select value={recoveryJudgement} onChange={event => setRecoveryJudgement(event.target.value as typeof recoveryJudgement)} data-testid="recovery-observed-judgement">
                            <option value="Passed">Passed</option>
                            <option value="Failed">Failed</option>
                            <option value="NotApplicable">NotApplicable</option>
                          </select>
                        </label>
                        <label>
                          <span>Typed observed outputs (JSON)</span>
                          <textarea value={recoveryOutputsJson} onChange={event => setRecoveryOutputsJson(event.target.value)} rows={5} spellCheck={false} data-testid="recovery-observed-outputs" />
                        </label>
                      </>
                    ) : null}
                    {pendingDefinition.command === 'Retry' ? (
                      <label>
                        <span>Retry Operation</span>
                        <select value={operationId} onChange={event => setOperationId(event.target.value)} data-testid="recovery-operation-id">
                          {uniqueOperations(selectedRun).map(operation => (
                            <option key={operation.operationId} value={operation.operationId}>
                              {operation.operationId}
                            </option>
                          ))}
                        </select>
                      </label>
                    ) : null}
                    <div>
                      <button type="button" className="button ghost" onClick={() => setPendingCommand(null)} disabled={commandBusy}>Cancel</button>
                      <button type="button" className="button primary" onClick={() => void submitCommand()} disabled={commandBusy} data-testid="confirm-production-command">
                        {commandBusy ? 'Issuing…' : `Issue ${pendingDefinition.label}`}
                      </button>
                    </div>
                  </div>
                ) : null}
              </section>
            </>
          ) : (
            <div className="operations-detail-empty"><ArrowRight size={22} /><span>Select an active run for evidence and controls.</span></div>
          )}
        </aside>
      </div>
    </section>
  );
}

const RunListItem = memo(function RunListItem({
  run,
  selected,
  onSelect
}: {
  run: ProductionRunReadModel;
  selected: boolean;
  onSelect(): void;
}): React.ReactElement {
  const operation = currentOperation(run);
  const routeProjection = buildProductionRouteRuntimeProjection(run);
  const movement = routeProjection.currentMovements[0] ?? routeProjection.latestDecision;
  return (
    <button type="button" className={selected ? 'selected' : ''} onClick={onSelect} data-testid={`active-run-${run.productionRunId}`}>
      <span className={`operations-status-pip status-${statusTone(run.executionStatus, run.controlState)}`} />
      <span>
        <strong>{run.productionUnitIdentity.value}</strong>
        <small>{run.productionUnitIdentity.modelId}</small>
        <em>{movement
          ? compactRouteMovement(movement)
          : `${operation?.stationSystemId ?? 'Awaiting route'} · ${operation?.operationId ?? run.entryOperationId}`}</em>
      </span>
      <span className="operations-run-axes">
        <b>{run.executionStatus}</b>
        <i>{run.judgement}</i>
      </span>
    </button>
  );
});

const StationCard = memo(function StationCard({
  station,
  onEmergencyStop
}: {
  station: ProductionLineStationView;
  onEmergencyStop(): void;
}): React.ReactElement {
  const tone = stationTone(station.status);
  return (
    <article
      className={`operations-station-card status-${tone}`}
      data-testid={`line-station-${station.stationSystemId}`}
      data-station-status={station.status}
    >
      <header>
        <span><Factory size={14} /><strong>{station.stationSystemId}</strong></span>
        <span className="operations-station-safety-actions">
          <small>
            {station.status} · {station.activeOperations.length} active · {station.queue.length} queued
            {' · '}{station.agentId
              ? `${station.agentId}/${station.stationId} ${station.agentPresenceHealth}`
              : `Agent ${station.agentPresenceHealth}`}
          </small>
          <button type="button" onClick={onEmergencyStop} title={`Emergency Stop ${station.stationSystemId}`} data-testid={`emergency-stop-${station.stationSystemId}`}>
            <ShieldAlert size={12} /> E-STOP
          </button>
        </span>
      </header>
      <div className="operations-product-lanes">
        <ProductLane
          label="ACTIVE OPERATIONS"
          products={station.activeOperations.map(operation => (
            `${operation.productionUnitLabel} / ${operation.operationId}`))}
          testId={`line-station-active-${station.stationSystemId}`}
        />
        <ProductLane
          label="STATION QUEUE"
          products={station.queue.map(material => `${material.label} [${material.kind}]`)}
          testId={`line-station-queue-${station.stationSystemId}`}
        />
        <ProductLane
          label="LOCATED WIP"
          products={station.productionUnits.map(unit => (
            `${unit.identityValue} / ${unit.disposition}`))}
          testId={`line-station-wip-${station.stationSystemId}`}
        />
      </div>
      <div className="operations-slot-strip">
        {station.slots.map(slot => (
          <div
            key={slot.slotId}
            className={`status-${slot.status.toLowerCase()}`}
            title={`${slot.slotId} · ${slot.status}`}
            data-testid={`line-slot-${station.stationSystemId}-${slot.slotId}`}
            data-slot-status={slot.status}
          >
            <i />
            <span>
              <strong>{slot.slotId}</strong>
              <small>{slot.status}{slot.materialLabel ? ` · ${slot.materialLabel}` : ''}</small>
            </span>
          </div>
        ))}
        {station.slots.length === 0 ? <span className="operations-no-slots">No persisted Slots</span> : null}
      </div>
      {station.resources.length > 0 ? (
        <div className="operations-resource-strip" data-testid={`line-station-resources-${station.stationSystemId}`}>
          {station.resources.map(resource => (
            <div
              key={`${resource.operationRunId}:${resource.kind}:${resource.resourceId}`}
              className={`status-${resourceTone(resource.status)}`}
              data-testid={`line-resource-${resource.operationRunId}-${resource.kind}-${resource.resourceId}`}
              data-resource-status={resource.status}
              data-fencing-token={resource.fencingToken ?? ''}
            >
              <span>{resource.kind}</span>
              <strong>{resource.resourceId}</strong>
              <small>
                {resource.status}
                {resource.fencingToken === null ? '' : ` · fence ${resource.fencingToken}`}
              </small>
            </div>
          ))}
        </div>
      ) : null}
      {station.carriers.length > 0 ? (
        <div className="operations-station-carriers">
          {station.carriers.map(carrier => (
            <span key={carrier.carrierId}>
              <Boxes size={12} /> {carrier.carrierId} · {carrier.productionUnits.length}/{carrier.capacity}
            </span>
          ))}
        </div>
      ) : null}
    </article>
  );
});

const CarrierCard = memo(function CarrierCard({
  carrier
}: {
  carrier: ProductionLineCarrierView;
}): React.ReactElement {
  return (
    <article data-testid={`line-carrier-${carrier.carrierId}`}>
      <header>
        <span><Boxes size={13} /><strong>{carrier.carrierId}</strong></span>
        <small>{carrier.carrierTypeId} · {carrier.productionUnits.length}/{carrier.capacity}</small>
      </header>
      <p>{formatMaterialLocation(carrier.location)}</p>
      <div>
        {carrier.productionUnits.map(position => (
          <span
            key={position.carrierPositionId}
            data-testid={`line-carrier-position-${carrier.carrierId}-${position.carrierPositionId}`}
          >
            <b>{position.carrierPositionId}</b>
            <strong>{position.productionUnitLabel}</strong>
            <small>{position.disposition} · {position.judgement}</small>
          </span>
        ))}
        {carrier.productionUnits.length === 0 ? <small>Carrier is empty</small> : null}
      </div>
    </article>
  );
});

function ProductLane({
  label,
  products,
  testId
}: {
  label: string;
  products: string[];
  testId: string;
}): React.ReactElement {
  return (
    <div data-testid={testId}>
      <span>{label}</span>
      <div>{products.length > 0
        ? products.map(product => <b key={product}>{product}</b>)
        : <small>none</small>}</div>
    </div>
  );
}

function RunDetail({ run }: { run: ProductionRunReadModel }): React.ReactElement {
  const routeProjection = useMemo(
    () => buildProductionRouteRuntimeProjection(run),
    [run]);
  return (
    <section className="operations-run-evidence">
      <header>
        <span>PRODUCTION UNIT</span>
        <strong>{run.productionUnitIdentity.value}</strong>
        <small>{run.productionRunId}</small>
      </header>
      <div className="operations-axis-grid">
        <div><span>Execution</span><strong>{run.executionStatus}</strong></div>
        <div><span>Judgement</span><strong>{run.judgement}</strong></div>
        <div><span>Disposition</span><strong>{run.disposition}</strong></div>
        <div><span>Control</span><strong>{run.controlState}</strong></div>
      </div>
      <RouteRuntimeEvidence projection={routeProjection} entryOperationId={run.entryOperationId} />
      <div className="operations-operation-list">
        {run.operations.map(operation => (
          <div key={operation.operationRunId} className={`status-${statusTone(operation.executionStatus, run.controlState)}`}>
            <i />
            <span>
              <strong>{operation.operationId}</strong>
              <small>{operation.stationSystemId} · attempt {operation.attempt}</small>
            </span>
            <span><b>{operation.executionStatus}</b><small>{operation.judgement}</small></span>
          </div>
        ))}
      </div>
      {run.recoveryDecisions.length > 0 ? (
        <div className="operations-recovery-evidence" data-testid="production-recovery-decisions">
          <strong>RECOVERY DECISIONS</strong>
          {run.recoveryDecisions.map(decision => (
            <span key={decision.decisionId}>
              <b>{decision.kind}</b>
              <small>{decision.actorId} · {decision.evidenceReference}</small>
              <em>{decision.reason}</em>
            </span>
          ))}
        </div>
      ) : null}
      {run.failureReason ? (
        <div className="operations-failure"><AlertTriangle size={13} /><span>{run.failureCode} · {run.failureReason}</span></div>
      ) : null}
    </section>
  );
}

function RouteRuntimeEvidence({
  projection,
  entryOperationId
}: {
  projection: ProductionRouteRuntimeProjection;
  entryOperationId: string;
}): React.ReactElement {
  const currentMovementIds = new Set(projection.currentMovements.map(movement => movement.movementId));
  return (
    <section className="operations-route-runtime" data-testid="production-route-runtime">
      <header>
        <span><GitBranch size={12} /><strong>ROUTE MOVEMENT</strong></span>
        <small>{projection.decisionTrail.length} persisted decision{projection.decisionTrail.length === 1 ? '' : 's'}</small>
      </header>
      <div className="operations-route-current">
        {projection.currentMovements.map(movement => (
          <RouteMovementSummary
            key={movement.movementId}
            movement={movement}
            label={movement.transitionId ? 'CURRENT TRANSITION' : 'CURRENT FLOW'}
          />
        ))}
        {projection.currentMovements.length === 0 && projection.latestDecision ? (
          <RouteMovementSummary movement={projection.latestDecision} label="LATEST TRANSITION" />
        ) : null}
        {projection.currentMovements.length === 0 && !projection.latestDecision ? (
          <div className="operations-route-awaiting">
            <CircleDot size={12} />
            <span><strong>ENTRY · {entryOperationId}</strong><small>No route decision has been persisted yet.</small></span>
          </div>
        ) : null}
      </div>
      {projection.decisionTrail.length > 0 ? (
        <div className="operations-route-trail" data-testid="production-route-decision-trail">
          {projection.decisionTrail.map((movement, index) => (
            <article
              key={movement.movementId}
              className={`tone-${movement.tone}${currentMovementIds.has(movement.movementId) ? ' current' : ''}`}
              data-testid={`production-route-decision-${movement.transitionId}`}
              data-transition-id={movement.transitionId ?? ''}
              data-flow-kind={movement.kind}
            >
              <i />
              <span>
                <small>
                  {index === projection.decisionTrail.length - 1 ? 'LATEST · ' : ''}
                  {movement.transitionId} · traversal {movement.traversal}
                </small>
                <strong>{routeMovementPath(movement)}</strong>
                <em>{routeMovementStations(movement)}</em>
              </span>
              <span>
                <b>{movement.sourceJudgement ?? 'Unknown'}</b>
                <small>{movement.decidedAtUtc ? formatTimestamp(movement.decidedAtUtc) : movement.evidence}</small>
              </span>
            </article>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function RouteMovementSummary({
  movement,
  label
}: {
  movement: ProductionRouteRuntimeMovement;
  label: string;
}): React.ReactElement {
  const Icon = movement.kind === 'Terminal'
    ? Flag
    : movement.kind === 'Rework'
      ? RotateCcw
      : ArrowRight;
  return (
    <div
      className={`operations-route-focus tone-${movement.tone}`}
      data-testid="production-route-current-flow"
      data-transition-id={movement.transitionId ?? ''}
      data-flow-kind={movement.kind}
      data-target-operation-id={movement.targetOperationId ?? ''}
      data-terminal-disposition={movement.terminalDisposition ?? ''}
    >
      <Icon size={14} />
      <span>
        <small>{label}</small>
        <strong>{routeMovementPath(movement)}</strong>
        <em>{movement.transitionId
          ? `${movement.transitionId} · traversal ${movement.traversal}`
          : `${movement.kind} · ${movement.evidence}`}</em>
      </span>
      <b>{movement.kind === 'Terminal'
        ? movement.terminalDisposition
        : movement.sourceJudgement ?? movement.target?.executionStatus ?? 'Pending'}</b>
    </div>
  );
}

function routeMovementPath(movement: ProductionRouteRuntimeMovement): string {
  const source = movement.source
    ? `${movement.source.operationId} #${movement.source.attempt}`
    : 'ENTRY';
  const target = movement.terminalDisposition
    ? `TERMINAL · ${movement.terminalDisposition}`
    : movement.target
      ? `${movement.target.operationId} #${movement.target.attempt}`
      : movement.targetOperationId ?? 'Awaiting target';
  return `${source} → ${target}`;
}

function routeMovementStations(movement: ProductionRouteRuntimeMovement): string {
  const source = movement.source?.stationSystemId ?? 'line entry';
  const target = movement.terminalDisposition
    ? 'product disposition'
    : movement.target?.stationSystemId ?? 'station pending';
  return `${source} → ${target}`;
}

function compactRouteMovement(movement: ProductionRouteRuntimeMovement): string {
  const source = movement.source?.stationSystemId ?? 'ENTRY';
  const target = movement.terminalDisposition
    ? `TERMINAL/${movement.terminalDisposition}`
    : movement.target?.stationSystemId ?? movement.targetOperationId ?? 'Awaiting target';
  return `${source} → ${target}`;
}

function currentOperation(run: ProductionRunReadModel): ProductionOperationRunReadModel | null {
  return run.operations.find(operation => operation.executionStatus === 'Running')
    ?? run.operations.find(operation => operation.executionStatus === 'Pending')
    ?? run.operations[run.operations.length - 1]
    ?? null;
}

function requiresReason(command: OperatorProductionRunCommand): boolean {
  return command === 'Stop'
    || command === 'Cancel'
    || command === 'Hold'
    || command === 'Scrap'
    || command === 'SafeStop'
    || command === 'Reconcile'
    || command === 'Retry'
    || command === 'Abort';
}

function requiresRecoveryDecision(
  command: OperatorProductionRunCommand,
  run: ProductionRunReadModel
): boolean {
  return command === 'Reconcile'
    || command === 'Retry'
    || command === 'Abort'
    || (command === 'Scrap' && run.controlState === 'RecoveryRequired');
}

function uniqueOperations(run: ProductionRunReadModel): ProductionOperationRunReadModel[] {
  const operations = new Map<string, ProductionOperationRunReadModel>();
  run.operations.forEach(operation => {
    if (!operations.has(operation.operationId)) {
      operations.set(operation.operationId, operation);
    }
  });
  return [...operations.values()];
}

function parseTypedOutputs(value: string): Record<string, ProductionContextValueRequest> {
  const parsed = JSON.parse(value) as unknown;
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('expected an object keyed by output name');
  }
  const allowedKinds = new Set<ProductionContextValueRequest['kind']>([
    'Text',
    'Boolean',
    'WholeNumber',
    'FixedPoint',
    'DateTimeUtc'
  ]);
  const outputs: Record<string, ProductionContextValueRequest> = {};
  Object.entries(parsed).forEach(([key, raw]) => {
    if (!isCanonical(key) || !raw || typeof raw !== 'object' || Array.isArray(raw)) {
      throw new Error(`output '${key}' must be a canonical typed object`);
    }
    const candidate = raw as { kind?: unknown; canonicalValue?: unknown };
    if (typeof candidate.kind !== 'string'
        || !allowedKinds.has(candidate.kind as ProductionContextValueRequest['kind'])
        || typeof candidate.canonicalValue !== 'string'
        || !isCanonical(candidate.canonicalValue)) {
      throw new Error(`output '${key}' has an invalid kind or canonicalValue`);
    }
    outputs[key] = {
      kind: candidate.kind as ProductionContextValueRequest['kind'],
      canonicalValue: candidate.canonicalValue
    };
  });
  return outputs;
}

function statusTone(executionStatus: string, controlState: string): string {
  if (controlState === 'Held' || controlState === 'Paused' || controlState === 'StopRequested') {
    return 'held';
  }
  if (['Failed', 'TimedOut', 'Rejected'].includes(executionStatus)) {
    return 'failed';
  }
  if (executionStatus === 'Running') {
    return 'running';
  }
  if (executionStatus === 'Completed') {
    return 'completed';
  }
  return 'queued';
}

function idleStationView(stationSystemId: string): ProductionLineStationView {
  return {
    stationSystemId,
    status: 'Idle',
    agentId: null,
    stationId: null,
    agentPresenceSessionId: null,
    agentPresenceSequence: null,
    agentPresenceState: null,
    agentPresenceHealth: 'NotApplicable',
    agentPresenceLastSeenAtUtc: null,
    agentPresenceAgeSeconds: null,
    queue: [],
    activeOperations: [],
    productionUnits: [],
    slots: [],
    carriers: [],
    resources: []
  };
}

function stationTone(status: ProductionLineStationView['status']): string {
  switch (status) {
    case 'Running': return 'running';
    case 'Queued':
    case 'WaitingForResources': return 'queued';
    case 'Blocked': return 'failed';
    case 'Offline': return 'offline';
    default: return 'completed';
  }
}

function resourceTone(status: ProductionLineStationView['resources'][number]['status']): string {
  switch (status) {
    case 'Leased': return 'running';
    case 'Waiting': return 'queued';
    case 'RecoveryHeld': return 'held';
    default: return 'failed';
  }
}

function formatMaterialLocation(location: ProductionLineCarrierView['location']): string {
  if (!location) {
    return 'Location is not assigned';
  }
  switch (location.kind) {
    case 'StationQueue': return `${location.stationSystemId} / queue`;
    case 'Slot': return `${location.stationSystemId} / ${location.slotId}`;
    case 'CarrierPosition': return `${location.carrierId} / ${location.carrierPositionId}`;
  }
}

function uniqueSorted(values: string[]): string[] {
  return [...new Set(values.filter(Boolean))].sort((left, right) => left.localeCompare(right));
}

function isCanonical(value: string): boolean {
  return value.length > 0 && value.trim() === value;
}

function formatTimestamp(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleTimeString();
}
