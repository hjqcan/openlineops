import React, { memo, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowRight,
  Boxes,
  CircleDot,
  Factory,
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
  ProductionOperationsFilters,
  ProductionOperationRunReadModel,
  ProductionRunReadModel
} from './contracts';
import { commandProductionRun } from './api';

interface OperationsWorkbenchProps {
  activeRuns: ProductionRunReadModel[];
  lineState: ProductionLineRuntimeStateResponse | null;
  filters: ProductionOperationsFilters;
  selectedLineId: string;
  connected: boolean;
  refreshing: boolean;
  isBackendHealthy: boolean;
  lastSynchronizedAtUtc: string | null;
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

interface StationOverview {
  stationSystemId: string;
  runningProducts: string[];
  queuedProducts: string[];
  heldProducts: string[];
  failedProducts: string[];
  slots: SlotOverview[];
}

interface SlotOverview {
  slotId: string;
  state: 'Available' | 'Reserved' | 'Occupied' | 'Running' | 'Blocked';
  productIdentity: string | null;
  fencingToken: number | null;
}

const commandDefinitions: CommandDefinition[] = [
  { command: 'Pause', label: 'Pause', description: 'Pause after the current safe boundary.', icon: Pause, tone: 'normal' },
  { command: 'Continue', label: 'Continue', description: 'Resume a paused production run.', icon: Play, tone: 'normal' },
  { command: 'Stop', label: 'Stop', description: 'Stop normal execution with a recorded reason.', icon: Square, tone: 'warning' },
  { command: 'Hold', label: 'Hold', description: 'Hold this product for operator disposition.', icon: Hand, tone: 'warning' },
  { command: 'Release', label: 'Release', description: 'Release a held product back to its route.', icon: LockOpen, tone: 'normal' },
  { command: 'Rework', label: 'Rework', description: 'Return to one explicit earlier Operation.', icon: RotateCcw, tone: 'warning' },
  { command: 'Scrap', label: 'Scrap', description: 'Terminally scrap this product and preserve evidence.', icon: Trash2, tone: 'danger' },
  { command: 'SafeStop', label: 'Safe Stop', description: 'Request the Agent safety stop path.', icon: ShieldAlert, tone: 'danger' }
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
  onFilterChanged,
  onRefresh,
  onOpenTopology,
  onActiveRunChanged,
  onMessage
}: OperationsWorkbenchProps): React.ReactElement {
  const [selectedRunId, setSelectedRunId] = useState('');
  const [pendingCommand, setPendingCommand] = useState<OperatorProductionRunCommand | null>(null);
  const [actorId, setActorId] = useState('desktop-operator');
  const [reason, setReason] = useState('');
  const [operationId, setOperationId] = useState('');
  const [commandBusy, setCommandBusy] = useState(false);
  const visibleRuns = activeRuns;
  const lineRuns = lineState?.activeRuns ?? [];
  const runById = useMemo(
    () => new Map([...lineRuns, ...visibleRuns].map(run => [run.productionRunId, run])),
    [lineRuns, visibleRuns]);
  const selectedRun = runById.get(selectedRunId) ?? null;
  const stationOverviews = useMemo(() => buildStationOverviews(lineRuns), [lineRuns]);
  const lineIds = useMemo(
    () => uniqueSorted([...lineRuns, ...visibleRuns].map(run => run.productionLineDefinitionId)),
    [lineRuns, visibleRuns]);
  const stationIds = useMemo(
    () => uniqueSorted(lineRuns.flatMap(run => run.operations.map(operation => operation.stationSystemId))),
    [lineRuns]);
  const slotIds = useMemo(
    () => uniqueSorted(lineRuns.flatMap(run => run.operations.flatMap(operation => operation.resources
      .filter(resource => resource.kind === 'Slot')
      .map(resource => resource.resourceId)))),
    [lineRuns]);
  const pendingDefinition = pendingCommand
    ? commandDefinitions.find(definition => definition.command === pendingCommand) ?? null
    : null;

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
  }, [selectedRun]);

  const submitCommand = async (): Promise<void> => {
    if (!selectedRun || !pendingCommand || !pendingDefinition) {
      return;
    }
    if (!isCanonical(actorId)) {
      onMessage('Operator Actor ID is required and cannot start or end with whitespace.');
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

    setCommandBusy(true);
    try {
      const response = await commandProductionRun(selectedRun.productionRunId, pendingCommand, {
        actorId,
        reason: reason || null,
        operationId: pendingCommand === 'Rework' ? operationId : null
      });
      if (!response.ok || !response.body) {
        onMessage(`${pendingDefinition.label} rejected: ${response.status} ${response.text}`);
        return;
      }
      onMessage(`${pendingDefinition.label} accepted for ${response.body.productionUnitIdentity.value}`);
      setPendingCommand(null);
      setReason('');
      await onRefresh();
    } catch (error) {
      onMessage(`${pendingDefinition.label} failed: ${String(error)}`);
    } finally {
      setCommandBusy(false);
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
              <span><strong>Line State</strong><small>{lineState?.activeRunCount ?? 0} active products from one projection</small></span>
            </div>
            <span data-testid="line-state-generated-at">{lineState ? formatTimestamp(lineState.generatedAtUtc) : 'not synchronized'}</span>
          </header>
          <div className="operations-station-grid">
            {stationOverviews.map(station => (
              <StationCard key={station.stationSystemId} station={station} />
            ))}
            {stationOverviews.length === 0 ? (
              <div className="operations-line-empty">
                <Factory size={30} />
                <strong>The selected line has no active WIP</strong>
                <span>Studio reconnects by querying Active Runs and the persisted Line State projection.</span>
              </div>
            ) : null}
          </div>
        </main>

        <aside className="operations-run-detail">
          {selectedRun ? (
            <>
              <RunDetail run={selectedRun} />
              <section className="operations-command-section">
                <header><span>OPERATOR COMMANDS</span><small>{selectedRun.controlState}</small></header>
                <div className="operations-command-grid">
                  {commandDefinitions.map(definition => {
                    const Icon = definition.icon;
                    return (
                      <button
                        type="button"
                        key={definition.command}
                        className={`${definition.tone} ${pendingCommand === definition.command ? 'selected' : ''}`}
                        disabled={!canIssue(definition.command, selectedRun) || commandBusy}
                        onClick={() => {
                          setPendingCommand(definition.command);
                          setReason('');
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
                    <label><span>Actor ID</span><input value={actorId} onChange={event => setActorId(event.target.value)} data-testid="production-command-actor" /></label>
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
  return (
    <button type="button" className={selected ? 'selected' : ''} onClick={onSelect} data-testid={`active-run-${run.productionRunId}`}>
      <span className={`operations-status-pip status-${statusTone(run.executionStatus, run.controlState)}`} />
      <span>
        <strong>{run.productionUnitIdentity.value}</strong>
        <small>{run.productionUnitIdentity.modelId}</small>
        <em>{operation?.stationSystemId ?? 'Awaiting route'} · {operation?.operationId ?? run.entryOperationId}</em>
      </span>
      <span className="operations-run-axes">
        <b>{run.executionStatus}</b>
        <i>{run.judgement}</i>
      </span>
    </button>
  );
});

const StationCard = memo(function StationCard({ station }: { station: StationOverview }): React.ReactElement {
  const state = station.failedProducts.length > 0
    ? 'failed'
    : station.runningProducts.length > 0
      ? 'running'
      : station.heldProducts.length > 0
        ? 'held'
        : 'queued';
  return (
    <article className={`operations-station-card status-${state}`}>
      <header>
        <span><Factory size={14} /><strong>{station.stationSystemId}</strong></span>
        <small>{station.runningProducts.length} running · {station.queuedProducts.length} queued</small>
      </header>
      <div className="operations-product-lanes">
        <ProductLane label="RUNNING" products={station.runningProducts} />
        <ProductLane label="QUEUE" products={station.queuedProducts} />
        {station.heldProducts.length > 0 ? <ProductLane label="HELD" products={station.heldProducts} /> : null}
        {station.failedProducts.length > 0 ? <ProductLane label="INCIDENT" products={station.failedProducts} /> : null}
      </div>
      <div className="operations-slot-strip">
        {station.slots.map(slot => (
          <div key={slot.slotId} className={`status-${slot.state.toLowerCase()}`} title={`${slot.slotId} · ${slot.state}`}>
            <i />
            <span><strong>{slot.slotId}</strong><small>{slot.state}{slot.productIdentity ? ` · ${slot.productIdentity}` : ''}</small></span>
          </div>
        ))}
        {station.slots.length === 0 ? <span className="operations-no-slots">No leased Slot resources</span> : null}
      </div>
    </article>
  );
});

function ProductLane({ label, products }: { label: string; products: string[] }): React.ReactElement {
  return (
    <div>
      <span>{label}</span>
      <div>{products.length > 0
        ? products.map(product => <b key={product}>{product}</b>)
        : <small>none</small>}</div>
    </div>
  );
}

function RunDetail({ run }: { run: ProductionRunReadModel }): React.ReactElement {
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
      {run.failureReason ? (
        <div className="operations-failure"><AlertTriangle size={13} /><span>{run.failureCode} · {run.failureReason}</span></div>
      ) : null}
    </section>
  );
}

function buildStationOverviews(runs: ProductionRunReadModel[]): StationOverview[] {
  const stations = new Map<string, {
    running: Set<string>;
    queued: Set<string>;
    held: Set<string>;
    failed: Set<string>;
    slots: Map<string, SlotOverview>;
  }>();
  for (const run of runs) {
    const product = run.productionUnitIdentity.value;
    for (const operation of run.operations) {
      const station = stations.get(operation.stationSystemId) ?? {
        running: new Set<string>(),
        queued: new Set<string>(),
        held: new Set<string>(),
        failed: new Set<string>(),
        slots: new Map<string, SlotOverview>()
      };
      stations.set(operation.stationSystemId, station);
      if (run.controlState === 'Held') {
        station.held.add(product);
      } else if (operation.executionStatus === 'Running') {
        station.running.add(product);
      } else if (operation.executionStatus === 'Pending') {
        station.queued.add(product);
      } else if (['Failed', 'TimedOut', 'Rejected'].includes(operation.executionStatus)) {
        station.failed.add(product);
      }

      for (const resource of operation.resources.filter(candidate => candidate.kind === 'Slot')) {
        const slot = toSlotOverview(resource.resourceId, resource.fencingToken, operation, product);
        const existing = station.slots.get(slot.slotId);
        if (!existing || slotPriority(slot.state) > slotPriority(existing.state)) {
          station.slots.set(slot.slotId, slot);
        }
      }
    }
  }
  return [...stations.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([stationSystemId, station]) => ({
      stationSystemId,
      runningProducts: [...station.running].sort(),
      queuedProducts: [...station.queued].sort(),
      heldProducts: [...station.held].sort(),
      failedProducts: [...station.failed].sort(),
      slots: [...station.slots.values()].sort((left, right) => left.slotId.localeCompare(right.slotId))
    }));
}

function toSlotOverview(
  slotId: string,
  fencingToken: number | null,
  operation: ProductionOperationRunReadModel,
  productIdentity: string
): SlotOverview {
  if (fencingToken === null) {
    return { slotId, state: 'Available', productIdentity: null, fencingToken };
  }
  if (operation.executionStatus === 'Running') {
    return { slotId, state: 'Running', productIdentity, fencingToken };
  }
  if (operation.executionStatus === 'Pending') {
    return { slotId, state: 'Reserved', productIdentity, fencingToken };
  }
  if (['Failed', 'TimedOut', 'Rejected'].includes(operation.executionStatus)) {
    return { slotId, state: 'Blocked', productIdentity, fencingToken };
  }
  return { slotId, state: 'Occupied', productIdentity, fencingToken };
}

function currentOperation(run: ProductionRunReadModel): ProductionOperationRunReadModel | null {
  return run.operations.find(operation => operation.executionStatus === 'Running')
    ?? run.operations.find(operation => operation.executionStatus === 'Pending')
    ?? run.operations[run.operations.length - 1]
    ?? null;
}

function canIssue(command: OperatorProductionRunCommand, run: ProductionRunReadModel): boolean {
  if (run.isTerminal) {
    return false;
  }
  switch (command) {
    case 'Pause': return run.controlState === 'Active';
    case 'Continue': return run.controlState === 'Paused';
    case 'Hold': return run.controlState === 'Active' || run.controlState === 'Paused';
    case 'Release': return run.controlState === 'Held';
    case 'Rework': return run.operations.length > 0 && run.controlState !== 'SafeStopped';
    case 'SafeStop': return run.controlState !== 'SafeStopped';
    default: return true;
  }
}

function requiresReason(command: OperatorProductionRunCommand): boolean {
  return command === 'Stop' || command === 'Hold' || command === 'Scrap' || command === 'SafeStop';
}

function statusTone(executionStatus: string, controlState: string): string {
  if (controlState === 'Held' || controlState === 'Paused') {
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

function slotPriority(state: SlotOverview['state']): number {
  return { Available: 0, Reserved: 1, Occupied: 2, Running: 3, Blocked: 4 }[state];
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
