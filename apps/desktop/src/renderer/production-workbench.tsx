import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowRight,
  Boxes,
  CheckCircle2,
  CircleDot,
  Cpu,
  Factory,
  FileCode2,
  GitBranch,
  Link2,
  Plus,
  RefreshCw,
  RotateCcw,
  Save,
  Trash2,
  Workflow
} from 'lucide-react';
import type {
  AutomationProjectWorkspaceResponse,
  AutomationTopologyResponse,
  ConfigurationSnapshotResponse,
  ExternalTestProgramAdapterRequest,
  ExternalTestProgramInputMappingRequest,
  ExternalTestProgramResultMappingRequest,
  ProcessDefinitionSummary,
  ProductionContextValueKind,
  ProductionLineResponse,
  ProductionLineSummaryResponse,
  ProductionOperationRequest,
  RouteJudgement,
  RouteTransitionKind,
  RouteTransitionRequest,
  SaveProductionLineRequest,
  StationProfileResponse
} from './contracts';
import {
  createProductionLine,
  getAutomationTopology,
  getProductionLine,
  listEngineeringProjects,
  listProcessDefinitions,
  listProductionLines,
  listStationProfiles,
  publishProjectSnapshot,
  replaceProductionLine,
  saveAutomationProjectManifest,
  type ProjectApplicationApiScope
} from './api';
import {
  createAutoRouteLayout,
  ProductionRouteGraph,
  type DesignerSelection,
  type GraphPoint
} from './production-route-graph';
import {
  validateProductionLine,
  type ProductionDesignerProblem
} from './production-route-validation';

export interface ProductionWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
  onProblemsChanged?(problems: ProductionDesignerProblem[]): void;
}

interface ProductionLineDraft extends Omit<SaveProductionLineRequest, 'externalTestProgramAdapters'> {
  persisted: boolean;
  dirty: boolean;
  externalTestProgramAdapters: ExternalAdapterDraft[];
}

type AdapterLaunchKind = 'Provider' | 'ApplicationExecutable';
type DesignerView = 'route' | 'resources';

interface ExternalAdapterDraft extends ExternalTestProgramAdapterRequest {
  launchKind: AdapterLaunchKind;
}

const transitionKinds: RouteTransitionKind[] = [
  'Sequence',
  'Judgement',
  'Condition',
  'Rework',
  'ParallelFork',
  'ParallelJoin'
];
const routeJudgements: RouteJudgement[] = [
  'Passed',
  'Failed',
  'Aborted',
  'Unknown',
  'NotApplicable'
];
const productionContextValueKinds: ProductionContextValueKind[] = [
  'Text',
  'Boolean',
  'WholeNumber',
  'FixedPoint',
  'DateTimeUtc'
];

export function ProductionWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onWorkspaceChanged,
  onMessage,
  onProblemsChanged
}: ProductionWorkbenchProps): React.ReactElement {
  const [lines, setLines] = useState<ProductionLineSummaryResponse[]>([]);
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [flows, setFlows] = useState<ProcessDefinitionSummary[]>([]);
  const [configurationSnapshots, setConfigurationSnapshots] = useState<ConfigurationSnapshotResponse[]>([]);
  const [stationProfiles, setStationProfiles] = useState<StationProfileResponse[]>([]);
  const [draft, setDraft] = useState<ProductionLineDraft>(() => createEmptyDraft(null, [], [], []));
  const [nodePositions, setNodePositions] = useState<Record<string, GraphPoint>>({});
  const [selection, setSelection] = useState<DesignerSelection>(null);
  const [view, setView] = useState<DesignerView>('route');
  const [busy, setBusy] = useState(false);

  const activeApplication = activeWorkspace?.project.applications.find(
    application => application.applicationId === activeApplicationId)
    ?? activeWorkspace?.project.applications[0]
    ?? null;
  const scope = useMemo<ProjectApplicationApiScope | null>(
    () => activeWorkspace && activeApplication
      ? {
        projectId: activeWorkspace.project.projectId,
        applicationId: activeApplication.applicationId
      }
      : null,
    [activeApplication, activeWorkspace]);
  const publishedFlows = useMemo(
    () => flows.filter(flow => flow.status === 'Published'),
    [flows]);
  const stationSystems = useMemo(
    () => topology?.systems.filter(system => system.kind === 'Station') ?? [],
    [topology]);
  const publishedSnapshot = useMemo(
    () => activeWorkspace?.project.snapshots
      .filter(snapshot => snapshot.applicationId === activeApplication?.applicationId
        && snapshot.productionLineDefinitionId === draft.lineDefinitionId)
      .slice()
      .sort((left, right) => right.publishedAtUtc.localeCompare(left.publishedAtUtc))[0]
      ?? null,
    [activeApplication?.applicationId, activeWorkspace?.project.snapshots, draft.lineDefinitionId]);
  const request = useMemo(() => toRequest(draft), [draft]);
  const problems = useMemo(
    () => validateProductionLine(request, {
      topology,
      publishedFlows,
      configurationSnapshots,
      stationProfiles
    }),
    [configurationSnapshots, publishedFlows, request, stationProfiles, topology]);
  const errorCount = useMemo(
    () => problems.filter(problem => problem.severity === 'Error').length,
    [problems]);
  const operationProblemIds = useMemo(
    () => new Set(problems
      .filter(problem => problem.scope === 'Operation')
      .map(problem => problem.entityId)),
    [problems]);
  const transitionProblemIds = useMemo(
    () => new Set(problems
      .filter(problem => problem.scope === 'Transition')
      .map(problem => problem.entityId)),
    [problems]);

  const refresh = useCallback(async () => {
    if (!scope || !isBackendHealthy) {
      setLines([]);
      setTopology(null);
      setFlows([]);
      setConfigurationSnapshots([]);
      setStationProfiles([]);
      return;
    }

    const [nextLines, nextFlows, engineeringProjects, nextStationProfiles, topologyResponse] = await Promise.all([
      listProductionLines(scope),
      listProcessDefinitions(scope),
      listEngineeringProjects(scope),
      listStationProfiles(scope),
      activeApplication?.topologyId
        ? getAutomationTopology(activeApplication.topologyId, scope)
        : Promise.resolve(null)
    ]);
    const snapshots = engineeringProjects.flatMap(project => project.snapshots);
    const snapshotCounts = new Map<string, number>();
    for (const snapshot of snapshots) {
      snapshotCounts.set(snapshot.snapshotId, (snapshotCounts.get(snapshot.snapshotId) ?? 0) + 1);
    }
    setLines(nextLines);
    setFlows(nextFlows);
    setConfigurationSnapshots(snapshots.filter(snapshot => (
      snapshot.status === 'Published'
      && snapshotCounts.get(snapshot.snapshotId) === 1)));
    setStationProfiles(nextStationProfiles);
    setTopology(topologyResponse?.ok && topologyResponse.body ? topologyResponse.body : null);
  }, [activeApplication?.topologyId, isBackendHealthy, scope]);

  useEffect(() => {
    refresh().catch(error => onMessage(`Production line refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

  useEffect(() => {
    const next = createEmptyDraft(null, [], [], []);
    setDraft(next);
    setNodePositions({});
    setSelection(null);
    setView('route');
  }, [scope?.applicationId]);

  useEffect(() => {
    if (!topology) {
      return;
    }
    setDraft(current => {
      if (current.persisted || current.topologyId) {
        return current;
      }
      const next = createEmptyDraft(topology, publishedFlows, configurationSnapshots, stationProfiles);
      setNodePositions(createAutoRouteLayout(next.operations, next.transitions, topology).positions);
      setSelection({ kind: 'operation', id: next.entryOperationId });
      return next;
    });
  }, [configurationSnapshots, publishedFlows, stationProfiles, topology]);

  useEffect(() => {
    onProblemsChanged?.(problems);
  }, [onProblemsChanged, problems]);

  useEffect(() => () => onProblemsChanged?.([]), [onProblemsChanged]);

  useEffect(() => {
    if (selection?.kind === 'operation'
        && !draft.operations.some(operation => operation.operationId === selection.id)) {
      setSelection(null);
    }
    if (selection?.kind === 'transition'
        && !draft.transitions.some(transition => transition.transitionId === selection.id)) {
      setSelection(null);
    }
  }, [draft.operations, draft.transitions, selection]);

  const createNew = useCallback(() => {
    const next = createEmptyDraft(topology, publishedFlows, configurationSnapshots, stationProfiles);
    setDraft(next);
    setNodePositions(createAutoRouteLayout(next.operations, next.transitions, topology).positions);
    setSelection({ kind: 'operation', id: next.entryOperationId });
    setView('route');
    onMessage('New production line route created');
  }, [configurationSnapshots, onMessage, publishedFlows, stationProfiles, topology]);

  const openLine = useCallback(async (line: ProductionLineSummaryResponse) => {
    if (!scope) {
      return;
    }
    setBusy(true);
    try {
      const response = await getProductionLine(line.lineDefinitionId, scope);
      if (!response.ok || !response.body) {
        onMessage(`Production line load failed: ${response.status} ${response.text}`);
        return;
      }
      const next = fromResponse(response.body);
      setDraft(next);
      setNodePositions(createAutoRouteLayout(next.operations, next.transitions, topology).positions);
      setSelection({ kind: 'operation', id: next.entryOperationId });
      setView('route');
      onMessage(`Production line opened ${response.body.lineDefinitionId}`);
    } catch (error) {
      onMessage(`Production line load failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [onMessage, scope, topology]);

  const save = useCallback(async () => {
    if (!scope || !isBackendHealthy) {
      onMessage('Open an Application with a healthy backend before saving a production line');
      return;
    }
    const firstError = problems.find(problem => problem.severity === 'Error');
    if (firstError) {
      onMessage(`Cannot save: ${firstError.message}`);
      focusProblem(firstError, setView, setSelection);
      return;
    }

    setBusy(true);
    try {
      const response = draft.persisted
        ? await replaceProductionLine(draft.lineDefinitionId, request, scope)
        : await createProductionLine(request, scope);
      if (!response.ok || !response.body) {
        onMessage(`Production line save failed: ${response.status} ${response.text}`);
        return;
      }
      setDraft(fromResponse(response.body));
      await refresh();
      onMessage(`Production line saved ${response.body.lineDefinitionId}`);
    } catch (error) {
      onMessage(`Production line save failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [draft.lineDefinitionId, draft.persisted, isBackendHealthy, onMessage, problems, refresh, request, scope]);

  const publishSnapshot = useCallback(async () => {
    if (!scope || !activeWorkspace || !isBackendHealthy) {
      onMessage('Open an Application with a healthy backend before publishing a snapshot');
      return;
    }
    if (!draft.persisted || draft.dirty) {
      onMessage('Save all Line Designer changes before publishing an immutable snapshot');
      return;
    }
    if (errorCount > 0) {
      onMessage('Resolve Line Designer Problems before publishing');
      return;
    }

    setBusy(true);
    try {
      const snapshotId = `project-snapshot-${Date.now().toString(36)}`;
      const publishResponse = await publishProjectSnapshot(activeWorkspace.project.projectId, {
        snapshotId,
        applicationId: scope.applicationId,
        productionLineDefinitionId: draft.lineDefinitionId
      });
      if (!publishResponse.ok || !publishResponse.body) {
        onMessage(`Project snapshot publish failed: ${publishResponse.status} ${publishResponse.text}`);
        return;
      }
      const savedWorkspace = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
      if (!savedWorkspace.ok || !savedWorkspace.body) {
        onMessage(`Project manifest save failed: ${savedWorkspace.status} ${savedWorkspace.text}`);
        return;
      }
      onWorkspaceChanged(savedWorkspace.body);
      onMessage(`Project snapshot published ${snapshotId} from line ${draft.lineDefinitionId}`);
    } catch (error) {
      onMessage(`Project snapshot publish failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, draft.dirty, draft.lineDefinitionId, draft.persisted, errorCount, isBackendHealthy, onMessage, onWorkspaceChanged, scope]);

  const mutateDraft = useCallback((mutator: (current: ProductionLineDraft) => ProductionLineDraft) => {
    setDraft(current => ({ ...mutator(current), dirty: true }));
  }, []);

  const addOperation = useCallback(() => {
    const operationId = nextPortableId('operation', draft.operations.map(operation => operation.operationId));
    const selectedOperation = selection?.kind === 'operation'
      ? draft.operations.find(operation => operation.operationId === selection.id)
      : undefined;
    const stationSystemId = selectedOperation?.stationSystemId
      ?? stationSystems[draft.operations.length % Math.max(stationSystems.length, 1)]?.systemId
      ?? '';
    const flowDefinitionId = selectedOperation?.flowDefinitionId ?? publishedFlows[0]?.processDefinitionId ?? '';
    const configurationSnapshotId = findMatchingConfigurationSnapshots(
      stationSystemId,
      flowDefinitionId,
      publishedFlows,
      configurationSnapshots,
      stationProfiles)[0]?.snapshotId ?? '';
    const operation: ProductionOperationRequest = {
      operationId,
      displayName: `Operation ${draft.operations.length + 1}`,
      stationSystemId,
      flowDefinitionId,
      configurationSnapshotId
    };
    const candidateSource = selectedOperation ?? draft.operations[draft.operations.length - 1];
    const canAutoConnect = candidateSource
      && !draft.transitions.some(transition => transition.sourceOperationId === candidateSource.operationId);
    const transition: RouteTransitionRequest | null = canAutoConnect
      ? {
        transitionId: nextPortableId(
          'route',
          draft.transitions.map(candidate => candidate.transitionId)),
        sourceOperationId: candidateSource.operationId,
        targetOperationId: operationId,
        kind: 'Sequence',
        requiredJudgement: null,
        maxTraversals: null,
        parallelGroupId: null,
        outputKey: null,
        expectedOutputKind: null,
        expectedOutputValue: null
      }
      : null;
    const nextOperations = [...draft.operations, operation];
    const nextTransitions = transition ? [...draft.transitions, transition] : draft.transitions;
    mutateDraft(current => ({
      ...current,
      entryOperationId: current.operations.length === 0 ? operationId : current.entryOperationId,
      operations: nextOperations,
      transitions: nextTransitions
    }));
    const auto = createAutoRouteLayout(nextOperations, nextTransitions, topology).positions;
    setNodePositions(current => ({ ...auto, ...current, [operationId]: auto[operationId] }));
    setSelection({ kind: 'operation', id: operationId });
    setView('route');
  }, [configurationSnapshots, draft.operations, draft.transitions, mutateDraft, publishedFlows, selection, stationProfiles, stationSystems, topology]);

  const addTransition = useCallback(() => {
    if (draft.operations.length < 2) {
      onMessage('Add at least two Operations before creating a Route Transition');
      return;
    }
    const sourceOperationId = selection?.kind === 'operation'
      ? selection.id
      : draft.entryOperationId;
    const targetOperationId = draft.operations.find(operation => operation.operationId !== sourceOperationId)?.operationId
      ?? '';
    const transition: RouteTransitionRequest = {
      transitionId: nextPortableId('route', draft.transitions.map(candidate => candidate.transitionId)),
      sourceOperationId,
      targetOperationId,
      kind: 'Sequence',
      requiredJudgement: null,
      maxTraversals: null,
      parallelGroupId: null,
      outputKey: null,
      expectedOutputKind: null,
      expectedOutputValue: null
    };
    mutateDraft(current => ({ ...current, transitions: [...current.transitions, transition] }));
    setSelection({ kind: 'transition', id: transition.transitionId });
    setView('route');
  }, [draft.entryOperationId, draft.operations, draft.transitions, mutateDraft, onMessage, selection]);

  const addAdapter = useCallback(() => {
    const adapter = createExternalAdapter(
      nextPortableId('external-program', draft.externalTestProgramAdapters.map(candidate => candidate.adapterId)),
      topology);
    mutateDraft(current => ({
      ...current,
      externalTestProgramAdapters: [...current.externalTestProgramAdapters, adapter]
    }));
    setView('resources');
  }, [draft.externalTestProgramAdapters, mutateDraft, topology]);

  const updateOperation = useCallback((originalId: string, next: ProductionOperationRequest) => {
    mutateDraft(current => ({
      ...current,
      entryOperationId: current.entryOperationId === originalId ? next.operationId : current.entryOperationId,
      operations: current.operations.map(operation => operation.operationId === originalId ? next : operation),
      transitions: current.transitions.map(transition => ({
        ...transition,
        sourceOperationId: transition.sourceOperationId === originalId
          ? next.operationId
          : transition.sourceOperationId,
        targetOperationId: transition.targetOperationId === originalId
          ? next.operationId
          : transition.targetOperationId
      }))
    }));
    if (originalId !== next.operationId) {
      setNodePositions(current => {
        const renamed = { ...current, [next.operationId]: current[originalId] ?? { x: 40, y: 40 } };
        delete renamed[originalId];
        return renamed;
      });
      setSelection({ kind: 'operation', id: next.operationId });
    }
  }, [mutateDraft]);

  const removeOperation = useCallback((operationId: string) => {
    mutateDraft(current => {
      const operations = current.operations.filter(operation => operation.operationId !== operationId);
      return {
        ...current,
        operations,
        transitions: current.transitions.filter(transition => (
          transition.sourceOperationId !== operationId && transition.targetOperationId !== operationId)),
        entryOperationId: current.entryOperationId === operationId
          ? operations[0]?.operationId ?? ''
          : current.entryOperationId
      };
    });
    setNodePositions(current => {
      const next = { ...current };
      delete next[operationId];
      return next;
    });
    setSelection(null);
  }, [mutateDraft]);

  const updateTransition = useCallback((originalId: string, next: RouteTransitionRequest) => {
    mutateDraft(current => ({
      ...current,
      transitions: current.transitions.map(transition => transition.transitionId === originalId ? next : transition)
    }));
    if (originalId !== next.transitionId) {
      setSelection({ kind: 'transition', id: next.transitionId });
    }
  }, [mutateDraft]);

  const removeTransition = useCallback((transitionId: string) => {
    mutateDraft(current => ({
      ...current,
      transitions: current.transitions.filter(transition => transition.transitionId !== transitionId)
    }));
    setSelection(null);
  }, [mutateDraft]);

  const autoArrange = useCallback(() => {
    setNodePositions(createAutoRouteLayout(draft.operations, draft.transitions, topology).positions);
    onMessage('Route graph arranged by Station and forward order');
  }, [draft.operations, draft.transitions, onMessage, topology]);

  const selectedOperation = selection?.kind === 'operation'
    ? draft.operations.find(operation => operation.operationId === selection.id) ?? null
    : null;
  const selectedTransition = selection?.kind === 'transition'
    ? draft.transitions.find(transition => transition.transitionId === selection.id) ?? null
    : null;

  if (!activeWorkspace || !activeApplication) {
    return (
      <section className="production-workbench production-empty">
        <Factory size={28} />
        <h2>Open an Application to design its production route.</h2>
      </section>
    );
  }

  return (
    <section className="production-workbench" data-testid="production-workbench">
      <header className="production-command-bar">
        <div>
          <Factory size={19} />
          <span>
            <strong>Line Designer</strong>
            <small>{activeWorkspace.project.displayName} / {activeApplication.displayName}</small>
          </span>
        </div>
        <div className="production-command-actions">
          <span className={draft.dirty ? 'production-dirty-badge dirty' : 'production-dirty-badge'}>
            {draft.dirty ? 'Unsaved' : 'Saved'}
          </span>
          <button type="button" className="button ghost" onClick={() => void refresh()} disabled={busy}>
            <RefreshCw size={14} /> Refresh
          </button>
          <button type="button" className="button" onClick={createNew} disabled={busy} data-testid="new-production-line">
            <Plus size={14} /> New Line
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => void save()}
            disabled={busy || !isBackendHealthy || errorCount > 0}
            data-testid="save-production-line"
            title={errorCount > 0 ? `Resolve ${errorCount} Line Designer errors before saving` : 'Save line'}
          >
            <Save size={14} /> Save Line
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => void publishSnapshot()}
            disabled={busy || !isBackendHealthy || !draft.persisted || draft.dirty || errorCount > 0}
            title="Publish the saved, valid production route as an immutable snapshot"
            data-testid="publish-production-line-snapshot"
          >
            <GitBranch size={14} /> Publish Snapshot
          </button>
        </div>
      </header>

      <section className="production-release-strip" data-testid="production-snapshot-result">
        <div className="production-release-mark"><GitBranch size={17} /></div>
        <div>
          <small>IMMUTABLE APPLICATION SNAPSHOT</small>
          <strong>{publishedSnapshot?.snapshotId ?? 'Route not published'}</strong>
          <span>Source line {draft.lineDefinitionId || 'not selected'}</span>
        </div>
        <div className="production-release-evidence">
          <span>{draft.operations.length} Operations · {draft.transitions.length} Route Transitions</span>
          <small>
            {publishedSnapshot
              ? `Release ${publishedSnapshot.releaseContentSha256.slice(0, 12)}`
              : 'Station, Flow and Configuration references remain Application-portable'}
          </small>
        </div>
      </section>

      <div className="production-shell">
        <aside className="production-line-browser">
          <div className="production-browser-heading">
            <span>PRODUCTION LINES</span>
            <small>{lines.length}</small>
          </div>
          {lines.length === 0 ? (
            <div className="production-browser-empty">No saved lines in this Application.</div>
          ) : lines.map(line => (
            <button
              type="button"
              key={line.lineDefinitionId}
              className={draft.persisted && draft.lineDefinitionId === line.lineDefinitionId ? 'active' : ''}
              onClick={() => void openLine(line)}
              data-testid={`production-line-${line.lineDefinitionId}`}
            >
              <Workflow size={15} />
              <span>
                <strong>{line.displayName}</strong>
                <small>{line.productModelCode} · {line.operationCount} Operations</small>
              </span>
            </button>
          ))}
        </aside>

        <main className="production-editor">
          <section className="production-identity-card">
            <div className="production-line-identity-heading">
              <div>
                <span>LINE DEFINITION</span>
                <strong>{draft.displayName || 'Untitled Production Line'}</strong>
              </div>
              <div className="production-route-health">
                {errorCount === 0
                  ? <><CheckCircle2 size={14} /> Route valid</>
                  : <><AlertTriangle size={14} /> {errorCount} errors</>}
              </div>
            </div>
            <div className="production-identity-grid">
              <Field label="Line ID">
                <input
                  value={draft.lineDefinitionId}
                  disabled={draft.persisted}
                  onChange={event => mutateDraft(current => ({ ...current, lineDefinitionId: event.target.value }))}
                  data-testid="production-line-id"
                />
              </Field>
              <Field label="Display Name">
                <input
                  value={draft.displayName}
                  onChange={event => mutateDraft(current => ({ ...current, displayName: event.target.value }))}
                  data-testid="production-line-name"
                />
              </Field>
              <Field label="Topology">
                <input value={draft.topologyId} readOnly />
              </Field>
              <Field label="Product Model ID">
                <input
                  value={draft.productModel.productModelId}
                  onChange={event => mutateDraft(current => ({
                    ...current,
                    productModel: { ...current.productModel, productModelId: event.target.value }
                  }))}
                />
              </Field>
              <Field label="Model Code">
                <input
                  value={draft.productModel.modelCode}
                  onChange={event => mutateDraft(current => ({
                    ...current,
                    productModel: { ...current.productModel, modelCode: event.target.value }
                  }))}
                  data-testid="production-product-model-code"
                />
              </Field>
              <Field label="Identity Input Key">
                <input
                  value={draft.productModel.identityInputKey}
                  onChange={event => mutateDraft(current => ({
                    ...current,
                    productModel: { ...current.productModel, identityInputKey: event.target.value }
                  }))}
                />
              </Field>
            </div>
          </section>

          <div className="production-designer-tabs">
            <button
              type="button"
              className={view === 'route' ? 'active' : ''}
              onClick={() => setView('route')}
            >
              <Workflow size={14} /> Route Graph
              <small>{draft.operations.length}</small>
            </button>
            <button
              type="button"
              className={view === 'resources' ? 'active' : ''}
              onClick={() => setView('resources')}
            >
              <Boxes size={14} /> External Program Resources
              <small>{draft.externalTestProgramAdapters.length}</small>
            </button>
            <button
              type="button"
              className={errorCount > 0 ? 'problems' : ''}
              onClick={() => {
                setView('route');
                const first = problems[0];
                if (first) {
                  focusProblem(first, setView, setSelection);
                }
              }}
            >
              <AlertTriangle size={14} /> Problems
              <small>{problems.length}</small>
            </button>
          </div>

          {view === 'route' ? (
            <section className="production-route-workspace">
              <div className="production-graph-panel">
                <header className="production-graph-toolbar">
                  <div>
                    <button type="button" className="button ghost" onClick={addOperation} data-testid="add-production-operation">
                      <Plus size={14} /> Operation
                    </button>
                    <button type="button" className="button ghost" onClick={addTransition} data-testid="add-route-transition">
                      <Link2 size={14} /> Transition
                    </button>
                    <button type="button" className="button ghost" onClick={autoArrange}>
                      <RotateCcw size={14} /> Auto arrange
                    </button>
                  </div>
                  <span>Drag Operation headers to arrange · semantic references stay unchanged</span>
                </header>
                <ProductionRouteGraph
                  operations={draft.operations}
                  transitions={draft.transitions}
                  topology={topology}
                  flows={publishedFlows}
                  entryOperationId={draft.entryOperationId}
                  selection={selection}
                  positions={nodePositions}
                  operationProblemIds={operationProblemIds}
                  transitionProblemIds={transitionProblemIds}
                  onSelect={setSelection}
                  onMove={(operationId, position) => setNodePositions(current => ({
                    ...current,
                    [operationId]: position
                  }))}
                />
              </div>

              <aside className="production-inspector">
                {selectedOperation ? (
                  <OperationInspector
                    operation={selectedOperation}
                    operationIndex={draft.operations.findIndex(operation => operation.operationId === selectedOperation.operationId)}
                    entryOperationId={draft.entryOperationId}
                    transitions={draft.transitions}
                    stations={stationSystems}
                    flows={publishedFlows}
                    configurationSnapshots={configurationSnapshots}
                    stationProfiles={stationProfiles}
                    onChange={next => updateOperation(selectedOperation.operationId, next)}
                    onMakeEntry={() => mutateDraft(current => ({
                      ...current,
                      entryOperationId: selectedOperation.operationId
                    }))}
                    onMarkTerminal={() => mutateDraft(current => ({
                      ...current,
                      transitions: current.transitions.filter(transition => !(
                        transition.sourceOperationId === selectedOperation.operationId
                        && transition.kind !== 'Rework'))
                    }))}
                    onRemove={() => removeOperation(selectedOperation.operationId)}
                  />
                ) : selectedTransition ? (
                  <TransitionInspector
                    transition={selectedTransition}
                    operations={draft.operations}
                    onChange={next => updateTransition(selectedTransition.transitionId, next)}
                    onRemove={() => removeTransition(selectedTransition.transitionId)}
                  />
                ) : (
                  <RouteOverview
                    entryOperationId={draft.entryOperationId}
                    operationCount={draft.operations.length}
                    transitionCount={draft.transitions.length}
                  />
                )}
                <ProblemsPanel
                  problems={problems}
                  onSelect={problem => focusProblem(problem, setView, setSelection)}
                />
              </aside>
            </section>
          ) : (
            <section className="production-resource-library">
              <header>
                <div>
                  <FileCode2 size={19} />
                  <span>
                    <strong>Application External Program Resources</strong>
                    <small>Flows reference Adapter IDs. Programs and provider bindings travel with this Application without file rewriting.</small>
                  </span>
                </div>
                <button type="button" className="button ghost" onClick={addAdapter} data-testid="add-external-program-adapter">
                  <Plus size={14} /> Add Resource
                </button>
              </header>
              <div className="production-adapter-list">
                {draft.externalTestProgramAdapters.map((adapter, index) => (
                  <AdapterCard
                    key={`${adapter.adapterId}-${index}`}
                    adapter={adapter}
                    index={index}
                    topology={topology}
                    onChange={next => mutateDraft(current => ({
                      ...current,
                      externalTestProgramAdapters: replaceAt(
                        current.externalTestProgramAdapters,
                        index,
                        next)
                    }))}
                    onRemove={() => mutateDraft(current => ({
                      ...current,
                      externalTestProgramAdapters: current.externalTestProgramAdapters
                        .filter((_, candidateIndex) => candidateIndex !== index)
                    }))}
                  />
                ))}
                {draft.externalTestProgramAdapters.length === 0 ? (
                  <div className="production-inline-empty">
                    <Boxes size={22} />
                    <strong>No external program resources</strong>
                    <span>Add one only when a Flow action needs to invoke a vendor program or provider.</span>
                  </div>
                ) : null}
              </div>
            </section>
          )}
        </main>
      </div>
    </section>
  );
}

function OperationInspector({
  operation,
  operationIndex,
  entryOperationId,
  transitions,
  stations,
  flows,
  configurationSnapshots,
  stationProfiles,
  onChange,
  onMakeEntry,
  onMarkTerminal,
  onRemove
}: {
  operation: ProductionOperationRequest;
  operationIndex: number;
  entryOperationId: string;
  transitions: RouteTransitionRequest[];
  stations: AutomationTopologyResponse['systems'];
  flows: ProcessDefinitionSummary[];
  configurationSnapshots: ConfigurationSnapshotResponse[];
  stationProfiles: StationProfileResponse[];
  onChange(next: ProductionOperationRequest): void;
  onMakeEntry(): void;
  onMarkTerminal(): void;
  onRemove(): void;
}): React.ReactElement {
  const matchingConfigurations = findMatchingConfigurationSnapshots(
    operation.stationSystemId,
    operation.flowDefinitionId,
    flows,
    configurationSnapshots,
    stationProfiles);
  const forwardTransitions = transitions.filter(transition => (
    transition.sourceOperationId === operation.operationId && transition.kind !== 'Rework'));
  const updateBinding = (changes: Partial<Pick<ProductionOperationRequest, 'stationSystemId' | 'flowDefinitionId'>>): void => {
    const next = { ...operation, ...changes };
    const matches = findMatchingConfigurationSnapshots(
      next.stationSystemId,
      next.flowDefinitionId,
      flows,
      configurationSnapshots,
      stationProfiles);
    onChange({
      ...next,
      configurationSnapshotId: matches.some(snapshot => snapshot.snapshotId === operation.configurationSnapshotId)
        ? operation.configurationSnapshotId
        : matches[0]?.snapshotId ?? ''
    });
  };
  return (
    <section className="production-inspector-section" data-testid="production-operation-inspector">
      <header>
        <CircleDot size={16} />
        <span><small>OPERATION</small><strong>{operation.displayName || operation.operationId}</strong></span>
      </header>
      <div className="production-inspector-fields">
        <Field label="Operation ID">
          <input value={operation.operationId} onChange={event => onChange({ ...operation, operationId: event.target.value })} />
        </Field>
        <Field label="Display Name">
          <input value={operation.displayName} onChange={event => onChange({ ...operation, displayName: event.target.value })} />
        </Field>
        <Field label="Station System">
          <select
            value={operation.stationSystemId}
            onChange={event => updateBinding({ stationSystemId: event.target.value })}
          >
            <option value="">Select Station</option>
            {stations.map(station => (
              <option key={station.systemId} value={station.systemId}>{station.displayName} · {station.systemType}</option>
            ))}
          </select>
        </Field>
        <Field label="Published Flow">
          <select
            value={operation.flowDefinitionId}
            onChange={event => updateBinding({ flowDefinitionId: event.target.value })}
          >
            <option value="">Select Flow</option>
            {flows.map(flow => (
              <option key={flow.processDefinitionId} value={flow.processDefinitionId}>
                {flow.displayName} · {flow.versionId}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Published Configuration">
          <select
            value={operation.configurationSnapshotId}
            onChange={event => onChange({ ...operation, configurationSnapshotId: event.target.value })}
            data-testid={`production-operation-configuration-${operationIndex}`}
          >
            <option value="">Create a matching published configuration</option>
            {matchingConfigurations.map(snapshot => (
              <option key={snapshot.snapshotId} value={snapshot.snapshotId}>
                {snapshot.snapshotId} · {snapshot.recipeVersionId}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <div className="production-disposition-card">
        <span>ROUTE DISPOSITION</span>
        <strong>{forwardTransitions.length === 0 ? 'Completed (terminal)' : 'In process'}</strong>
        <small>
          {forwardTransitions.length === 0
            ? 'No forward route leaves this Operation; the product completes here.'
            : `${forwardTransitions.length} forward Route Transition${forwardTransitions.length === 1 ? '' : 's'} continue execution.`}
        </small>
        <button type="button" className="button ghost" onClick={onMarkTerminal} disabled={forwardTransitions.length === 0}>
          Mark terminal
        </button>
      </div>
      <div className="production-reference-note">
        <Cpu size={14} />
        <span>Station, Flow and Configuration are explicit Application resource IDs; copying the Application preserves them unchanged.</span>
      </div>
      <footer>
        <button type="button" className="button ghost" onClick={onMakeEntry} disabled={entryOperationId === operation.operationId}>
          <GitBranch size={13} /> {entryOperationId === operation.operationId ? 'Route entry' : 'Make entry'}
        </button>
        <button type="button" className="button danger" onClick={onRemove}>
          <Trash2 size={13} /> Remove
        </button>
      </footer>
    </section>
  );
}

function TransitionInspector({
  transition,
  operations,
  onChange,
  onRemove
}: {
  transition: RouteTransitionRequest;
  operations: ProductionOperationRequest[];
  onChange(next: RouteTransitionRequest): void;
  onRemove(): void;
}): React.ReactElement {
  return (
    <section className="production-inspector-section" data-testid="production-transition-inspector">
      <header>
        <Link2 size={16} />
        <span><small>ROUTE TRANSITION</small><strong>{transition.transitionId || 'Untitled Transition'}</strong></span>
      </header>
      <div className="production-inspector-fields">
        <Field label="Transition ID">
          <input
            value={transition.transitionId}
            onChange={event => onChange({ ...transition, transitionId: event.target.value })}
          />
        </Field>
        <Field label="Source Operation">
          <select
            value={transition.sourceOperationId}
            onChange={event => onChange({ ...transition, sourceOperationId: event.target.value })}
          >
            {operations.map(operation => (
              <option key={operation.operationId} value={operation.operationId}>{operation.displayName}</option>
            ))}
          </select>
        </Field>
        <Field label="Target Operation">
          <select
            value={transition.targetOperationId}
            onChange={event => onChange({ ...transition, targetOperationId: event.target.value })}
          >
            {operations.map(operation => (
              <option key={operation.operationId} value={operation.operationId}>{operation.displayName}</option>
            ))}
          </select>
        </Field>
        <Field label="Transition Kind">
          <select
            value={transition.kind}
            data-testid="route-transition-kind"
            onChange={event => onChange(normalizeTransitionKind(
              transition,
              event.target.value as RouteTransitionKind))}
          >
            {transitionKinds.map(kind => <option key={kind} value={kind}>{transitionKindLabel(kind)}</option>)}
          </select>
        </Field>
        {transition.kind === 'Judgement' || transition.kind === 'Rework' ? (
          <Field label="Required Result Judgement">
            <select
              value={transition.requiredJudgement ?? ''}
              onChange={event => onChange({
                ...transition,
                requiredJudgement: event.target.value as RouteJudgement
              })}
            >
              {routeJudgements.map(judgement => <option key={judgement} value={judgement}>{judgement}</option>)}
            </select>
          </Field>
        ) : null}
        {transition.kind === 'Condition' ? (
          <>
            <Field label="Production Context Output Key">
              <input
                value={transition.outputKey ?? ''}
                onChange={event => onChange({ ...transition, outputKey: event.target.value })}
                data-testid="route-condition-output-key"
              />
            </Field>
            <Field label="Expected Output Kind">
              <select
                value={transition.expectedOutputKind ?? ''}
                data-testid="route-condition-expected-kind"
                onChange={event => onChange({
                  ...transition,
                  expectedOutputKind: event.target.value as ProductionContextValueKind
                })}
              >
                {productionContextValueKinds.map(kind => <option key={kind} value={kind}>{kind}</option>)}
              </select>
            </Field>
            <Field label="Expected Canonical Value">
              <input
                value={transition.expectedOutputValue ?? ''}
                onChange={event => onChange({ ...transition, expectedOutputValue: event.target.value })}
                placeholder={conditionValuePlaceholder(transition.expectedOutputKind)}
                data-testid="route-condition-expected-value"
              />
            </Field>
          </>
        ) : null}
        {transition.kind === 'Rework' ? (
          <Field label="Maximum Traversals">
            <input
              type="number"
              min={1}
              step={1}
              value={transition.maxTraversals ?? 1}
              onChange={event => onChange({ ...transition, maxTraversals: Number(event.target.value) })}
            />
          </Field>
        ) : null}
        {transition.kind === 'ParallelFork' || transition.kind === 'ParallelJoin' ? (
          <Field label="Parallel Group ID">
            <input
              value={transition.parallelGroupId ?? ''}
              onChange={event => onChange({ ...transition, parallelGroupId: event.target.value })}
            />
          </Field>
        ) : null}
      </div>
      <div className="production-reference-note">
        <Workflow size={14} />
        <span>{transitionKindDescription(transition.kind)}</span>
      </div>
      <footer>
        <span />
        <button type="button" className="button danger" onClick={onRemove}>
          <Trash2 size={13} /> Remove
        </button>
      </footer>
    </section>
  );
}

function RouteOverview({
  entryOperationId,
  operationCount,
  transitionCount
}: {
  entryOperationId: string;
  operationCount: number;
  transitionCount: number;
}): React.ReactElement {
  return (
    <section className="production-inspector-section production-route-overview">
      <header>
        <Workflow size={16} />
        <span><small>ROUTE GRAPH</small><strong>Line structure</strong></span>
      </header>
      <dl>
        <div><dt>Entry</dt><dd>{entryOperationId || 'not assigned'}</dd></div>
        <div><dt>Operations</dt><dd>{operationCount}</dd></div>
        <div><dt>Transitions</dt><dd>{transitionCount}</dd></div>
      </dl>
      <p>Select an Operation or Route Transition to edit its formal production contract.</p>
    </section>
  );
}

function ProblemsPanel({
  problems,
  onSelect
}: {
  problems: ProductionDesignerProblem[];
  onSelect(problem: ProductionDesignerProblem): void;
}): React.ReactElement {
  return (
    <section className="production-problems-panel" data-testid="production-designer-problems">
      <header>
        <span>PROBLEMS</span>
        <small>{problems.length}</small>
      </header>
      {problems.length === 0 ? (
        <div className="production-problems-empty"><CheckCircle2 size={16} /> No route problems</div>
      ) : (
        <div className="production-problems-list">
          {problems.map(problem => (
            <button type="button" key={problem.id} onClick={() => onSelect(problem)}>
              <AlertTriangle size={13} />
              <span><strong>{problem.scope} · {problem.entityId}</strong><small>{problem.message}</small></span>
            </button>
          ))}
        </div>
      )}
    </section>
  );
}

function AdapterCard({
  adapter,
  index,
  topology,
  onChange,
  onRemove
}: {
  adapter: ExternalAdapterDraft;
  index: number;
  topology: AutomationTopologyResponse | null;
  onChange(next: ExternalAdapterDraft): void;
  onRemove(): void;
}): React.ReactElement {
  const selectCapability = (capabilityId: string): void => {
    const capability = topology?.capabilities.find(candidate => candidate.capabilityId === capabilityId);
    const binding = topology?.driverBindings.find(candidate => candidate.capabilityId === capabilityId);
    onChange({
      ...adapter,
      capabilityId,
      commandName: capability?.commandName ?? '',
      providerKey: adapter.launchKind === 'Provider' ? binding?.providerKey ?? '' : null,
      timeoutMilliseconds: (capability?.timeoutSeconds ?? 30) * 1000
    });
  };
  return (
    <article className="production-adapter-card" data-testid={`production-external-program-${index}`}>
      <header>
        <div><FileCode2 size={15} /><strong>{adapter.displayName || adapter.adapterId}</strong></div>
        <button type="button" className="icon-button danger" onClick={onRemove} title="Remove external program resource">
          <Trash2 size={14} />
        </button>
      </header>
      <div className="production-adapter-grid">
        <Field label="Adapter ID">
          <input value={adapter.adapterId} onChange={event => onChange({ ...adapter, adapterId: event.target.value })} />
        </Field>
        <Field label="Display Name">
          <input value={adapter.displayName} onChange={event => onChange({ ...adapter, displayName: event.target.value })} />
        </Field>
        <Field label="Capability">
          <select value={adapter.capabilityId} onChange={event => selectCapability(event.target.value)}>
            <option value="">Select Capability</option>
            {(topology?.capabilities ?? []).map(capability => (
              <option key={capability.capabilityId} value={capability.capabilityId}>
                {capability.capabilityId} · {capability.commandName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Command Name">
          <input value={adapter.commandName} onChange={event => onChange({ ...adapter, commandName: event.target.value })} />
        </Field>
        <Field label="Launch Boundary">
          <select
            value={adapter.launchKind}
            onChange={event => {
              const launchKind = event.target.value as AdapterLaunchKind;
              onChange({
                ...adapter,
                launchKind,
                executable: launchKind === 'ApplicationExecutable'
                  ? adapter.executable ?? 'programs/vendor/helper.exe'
                  : null,
                providerKey: launchKind === 'Provider'
                  ? adapter.providerKey ?? ''
                  : null
              });
            }}
          >
            <option value="Provider">Provider plugin</option>
            <option value="ApplicationExecutable">Application executable</option>
          </select>
        </Field>
        {adapter.launchKind === 'Provider' ? (
          <Field label="Provider Key">
            <input value={adapter.providerKey ?? ''} onChange={event => onChange({ ...adapter, providerKey: event.target.value })} />
          </Field>
        ) : (
          <Field label="Portable Executable Path">
            <input value={adapter.executable ?? ''} onChange={event => onChange({ ...adapter, executable: event.target.value })} />
          </Field>
        )}
        <Field label="Timeout (ms)">
          <input
            type="number"
            min={1}
            step={1}
            value={adapter.timeoutMilliseconds}
            onChange={event => onChange({ ...adapter, timeoutMilliseconds: Number(event.target.value) })}
          />
        </Field>
        <Field label="Argument Templates (one per line)">
          <textarea
            value={adapter.argumentTemplates.join('\n')}
            onChange={event => onChange({ ...adapter, argumentTemplates: event.target.value.split('\n') })}
          />
        </Field>
      </div>

      <MappingEditor
        title="Typed Product Inputs"
        sourceLabel="Production Context source"
        targetLabel="Program input"
        rows={adapter.inputMappings}
        onChange={inputMappings => onChange({ ...adapter, inputMappings })}
      />
      <ResultMappingEditor
        rows={adapter.resultMappings}
        onChange={resultMappings => onChange({ ...adapter, resultMappings })}
      />
      <div className="production-outcome-grid">
        <Field label="Judgement JSON Path">
          <input
            value={adapter.outcomeMapping.sourcePath}
            onChange={event => onChange({
              ...adapter,
              outcomeMapping: { ...adapter.outcomeMapping, sourcePath: event.target.value }
            })}
          />
        </Field>
        <Field label="Passed Token">
          <input
            value={adapter.outcomeMapping.passedToken}
            onChange={event => onChange({
              ...adapter,
              outcomeMapping: { ...adapter.outcomeMapping, passedToken: event.target.value }
            })}
          />
        </Field>
        <Field label="Failed Token">
          <input
            value={adapter.outcomeMapping.failedToken}
            onChange={event => onChange({
              ...adapter,
              outcomeMapping: { ...adapter.outcomeMapping, failedToken: event.target.value }
            })}
          />
        </Field>
        <Field label="Aborted Token">
          <input
            value={adapter.outcomeMapping.abortedToken}
            onChange={event => onChange({
              ...adapter,
              outcomeMapping: { ...adapter.outcomeMapping, abortedToken: event.target.value }
            })}
          />
        </Field>
      </div>
    </article>
  );
}

function MappingEditor({
  title,
  sourceLabel,
  targetLabel,
  rows,
  onChange
}: {
  title: string;
  sourceLabel: string;
  targetLabel: string;
  rows: ExternalTestProgramInputMappingRequest[];
  onChange(rows: ExternalTestProgramInputMappingRequest[]): void;
}): React.ReactElement {
  return (
    <div className="production-mapping-editor">
      <header>
        <span>{title}</span>
        <button type="button" onClick={() => onChange([...rows, { source: '', target: '' }])}>
          <Plus size={12} /> Add mapping
        </button>
      </header>
      {rows.map((row, index) => (
        <div className="production-mapping-row" key={index}>
          <input
            aria-label={sourceLabel}
            value={row.source}
            onChange={event => onChange(replaceAt(rows, index, { ...row, source: event.target.value }))}
          />
          <ArrowRight size={13} />
          <input
            aria-label={targetLabel}
            value={row.target}
            onChange={event => onChange(replaceAt(rows, index, { ...row, target: event.target.value }))}
          />
          <button type="button" className="icon-button danger" onClick={() => onChange(rows.filter((_, candidate) => candidate !== index))}>
            <Trash2 size={13} />
          </button>
        </div>
      ))}
    </div>
  );
}

function ResultMappingEditor({
  rows,
  onChange
}: {
  rows: ExternalTestProgramResultMappingRequest[];
  onChange(rows: ExternalTestProgramResultMappingRequest[]): void;
}): React.ReactElement {
  return (
    <div className="production-mapping-editor">
      <header>
        <span>Typed Production Context Results</span>
        <button type="button" onClick={() => onChange([...rows, { sourcePath: '', targetKey: '' }])}>
          <Plus size={12} /> Add mapping
        </button>
      </header>
      {rows.map((row, index) => (
        <div className="production-mapping-row" key={index}>
          <input
            aria-label="Program result JSON path"
            value={row.sourcePath}
            onChange={event => onChange(replaceAt(rows, index, { ...row, sourcePath: event.target.value }))}
          />
          <ArrowRight size={13} />
          <input
            aria-label="Production Context target key"
            value={row.targetKey}
            onChange={event => onChange(replaceAt(rows, index, { ...row, targetKey: event.target.value }))}
          />
          <button type="button" className="icon-button danger" onClick={() => onChange(rows.filter((_, candidate) => candidate !== index))}>
            <Trash2 size={13} />
          </button>
        </div>
      ))}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return <label><span>{label}</span>{children}</label>;
}

function createEmptyDraft(
  topology: AutomationTopologyResponse | null,
  publishedFlows: ProcessDefinitionSummary[],
  configurationSnapshots: ConfigurationSnapshotResponse[],
  stationProfiles: StationProfileResponse[]
): ProductionLineDraft {
  const seed = Date.now().toString(36);
  const station = topology?.systems.find(system => system.kind === 'Station');
  const flowDefinitionId = publishedFlows[0]?.processDefinitionId ?? '';
  const configurationSnapshotId = findMatchingConfigurationSnapshots(
    station?.systemId ?? '',
    flowDefinitionId,
    publishedFlows,
    configurationSnapshots,
    stationProfiles)[0]?.snapshotId ?? '';
  const operation: ProductionOperationRequest = {
    operationId: 'operation-1',
    displayName: 'Operation 1',
    stationSystemId: station?.systemId ?? '',
    flowDefinitionId,
    configurationSnapshotId
  };
  return {
    persisted: false,
    dirty: true,
    lineDefinitionId: `line-${seed}`,
    displayName: 'New Production Line',
    topologyId: topology?.topologyId ?? '',
    productModel: {
      productModelId: 'product-mainboard',
      modelCode: 'MAINBOARD-A',
      identityInputKey: 'serialNumber'
    },
    entryOperationId: operation.operationId,
    operations: [operation],
    transitions: [],
    externalTestProgramAdapters: []
  };
}

function createExternalAdapter(
  adapterId: string,
  topology: AutomationTopologyResponse | null
): ExternalAdapterDraft {
  const capability = topology?.capabilities[0];
  const binding = topology?.driverBindings.find(candidate => candidate.capabilityId === capability?.capabilityId);
  return {
    adapterId,
    displayName: 'External Program',
    capabilityId: capability?.capabilityId ?? '',
    commandName: capability?.commandName ?? '',
    launchKind: 'Provider',
    executable: null,
    providerKey: binding?.providerKey ?? '',
    argumentTemplates: ['--serial={{product.identity}}', '--model={{product.model}}'],
    inputMappings: [
      { source: '$product.identity', target: 'serialNumber' },
      { source: '$product.model', target: 'modelCode' }
    ],
    resultMappings: [{ sourcePath: '$.judgement', targetKey: 'test.judgement' }],
    outcomeMapping: {
      sourcePath: '$.judgement',
      passedToken: 'Passed',
      failedToken: 'Failed',
      abortedToken: 'Aborted'
    },
    timeoutMilliseconds: (capability?.timeoutSeconds ?? 30) * 1000
  };
}

function fromResponse(response: ProductionLineResponse): ProductionLineDraft {
  return {
    persisted: true,
    dirty: false,
    lineDefinitionId: response.lineDefinitionId,
    displayName: response.displayName,
    topologyId: response.topologyId,
    productModel: { ...response.productModel },
    entryOperationId: response.entryOperationId,
    operations: response.operations.map(operation => ({ ...operation })),
    transitions: response.transitions.map(transition => ({ ...transition })),
    externalTestProgramAdapters: response.externalTestProgramAdapters.map(adapter => ({
      adapterId: adapter.adapterId,
      displayName: adapter.displayName,
      capabilityId: adapter.capabilityId,
      commandName: adapter.commandName,
      launchKind: parseAdapterLaunchKind(adapter.launchKind),
      executable: adapter.executable,
      providerKey: adapter.providerKey,
      argumentTemplates: [...adapter.argumentTemplates],
      inputMappings: adapter.inputMappings.map(mapping => ({ ...mapping })),
      resultMappings: adapter.resultMappings.map(mapping => ({ ...mapping })),
      outcomeMapping: { ...adapter.outcomeMapping },
      timeoutMilliseconds: adapter.timeoutMilliseconds
    }))
  };
}

function toRequest(draft: ProductionLineDraft): SaveProductionLineRequest {
  return {
    lineDefinitionId: draft.lineDefinitionId,
    displayName: draft.displayName,
    topologyId: draft.topologyId,
    productModel: { ...draft.productModel },
    entryOperationId: draft.entryOperationId,
    operations: draft.operations.map(operation => ({ ...operation })),
    transitions: draft.transitions.map(transition => ({ ...transition })),
    externalTestProgramAdapters: draft.externalTestProgramAdapters.map(adapter => ({
      adapterId: adapter.adapterId,
      displayName: adapter.displayName,
      capabilityId: adapter.capabilityId,
      commandName: adapter.commandName,
      executable: adapter.launchKind === 'ApplicationExecutable' && adapter.executable !== ''
        ? adapter.executable
        : null,
      providerKey: adapter.launchKind === 'Provider' && adapter.providerKey !== ''
        ? adapter.providerKey
        : null,
      argumentTemplates: [...adapter.argumentTemplates],
      inputMappings: adapter.inputMappings.map(mapping => ({ ...mapping })),
      resultMappings: adapter.resultMappings.map(mapping => ({ ...mapping })),
      outcomeMapping: { ...adapter.outcomeMapping },
      timeoutMilliseconds: adapter.timeoutMilliseconds
    }))
  };
}

function parseAdapterLaunchKind(value: string): AdapterLaunchKind {
  if (value === 'Provider' || value === 'ApplicationExecutable') {
    return value;
  }
  throw new Error(`Unsupported external program launch kind: ${value}`);
}

function findMatchingConfigurationSnapshots(
  stationSystemId: string,
  flowDefinitionId: string,
  flows: ProcessDefinitionSummary[],
  configurationSnapshots: ConfigurationSnapshotResponse[],
  stationProfiles: StationProfileResponse[]
): ConfigurationSnapshotResponse[] {
  const flow = flows.find(candidate => candidate.processDefinitionId === flowDefinitionId);
  if (!stationSystemId || !flow) {
    return [];
  }
  const stationProfileIds = new Set(stationProfiles
    .filter(profile => profile.stationSystemId === stationSystemId)
    .map(profile => profile.stationProfileId));
  return configurationSnapshots
    .filter(snapshot => (
      snapshot.processDefinitionId === flow.processDefinitionId
      && snapshot.processVersionId === flow.versionId
      && stationProfileIds.has(snapshot.stationProfileId)))
    .sort((left, right) => left.snapshotId.localeCompare(right.snapshotId));
}

function normalizeTransitionKind(
  transition: RouteTransitionRequest,
  kind: RouteTransitionKind
): RouteTransitionRequest {
  if (kind === 'Judgement') {
    return {
      ...transition,
      kind,
      requiredJudgement: transition.requiredJudgement ?? 'Passed',
      maxTraversals: null,
      parallelGroupId: null,
      outputKey: null,
      expectedOutputKind: null,
      expectedOutputValue: null
    };
  }
  if (kind === 'Condition') {
    return {
      ...transition,
      kind,
      requiredJudgement: null,
      maxTraversals: null,
      parallelGroupId: null,
      outputKey: transition.outputKey ?? 'result.code',
      expectedOutputKind: transition.expectedOutputKind ?? 'Text',
      expectedOutputValue: transition.expectedOutputValue ?? 'PASS'
    };
  }
  if (kind === 'Rework') {
    return {
      ...transition,
      kind,
      requiredJudgement: transition.requiredJudgement ?? 'Failed',
      maxTraversals: transition.maxTraversals ?? 1,
      parallelGroupId: null,
      outputKey: null,
      expectedOutputKind: null,
      expectedOutputValue: null
    };
  }
  if (kind === 'ParallelFork' || kind === 'ParallelJoin') {
    return {
      ...transition,
      kind,
      requiredJudgement: null,
      maxTraversals: null,
      parallelGroupId: transition.parallelGroupId ?? 'parallel-1',
      outputKey: null,
      expectedOutputKind: null,
      expectedOutputValue: null
    };
  }
  return {
    ...transition,
    kind,
    requiredJudgement: null,
    maxTraversals: null,
    parallelGroupId: null,
    outputKey: null,
    expectedOutputKind: null,
    expectedOutputValue: null
  };
}

function transitionKindLabel(kind: RouteTransitionKind): string {
  switch (kind) {
    case 'ParallelFork': return 'Parallel Fork';
    case 'ParallelJoin': return 'Parallel Join';
    default: return kind;
  }
}

function transitionKindDescription(kind: RouteTransitionKind): string {
  switch (kind) {
    case 'Sequence': return 'Continue unconditionally to exactly one next Operation.';
    case 'Judgement': return 'Route on one explicit Result Judgement; sibling judgement branches must be unique.';
    case 'Condition': return 'Compare one typed Production Context output; sibling branches share the key and use unique typed values.';
    case 'Rework': return 'Return to an earlier Operation with an explicit Result Judgement and traversal limit.';
    case 'ParallelFork': return 'Start two or more distinct branches in the same Parallel Group.';
    case 'ParallelJoin': return 'Join every branch into one target Operation using the matching Parallel Group.';
  }
}

function conditionValuePlaceholder(kind: ProductionContextValueKind | null): string {
  switch (kind) {
    case 'Boolean': return 'true or false';
    case 'WholeNumber': return '-12';
    case 'FixedPoint': return '12.50';
    case 'DateTimeUtc': return '2026-07-11T08:00:00.0000000+00:00';
    default: return 'exact text';
  }
}

function focusProblem(
  problem: ProductionDesignerProblem,
  setView: React.Dispatch<React.SetStateAction<DesignerView>>,
  setSelection: React.Dispatch<React.SetStateAction<DesignerSelection>>
): void {
  if (problem.scope === 'Operation') {
    setView('route');
    setSelection({ kind: 'operation', id: problem.entityId });
  } else if (problem.scope === 'Transition') {
    setView('route');
    setSelection({ kind: 'transition', id: problem.entityId });
  } else if (problem.scope === 'Resource') {
    setView('resources');
  } else {
    setView('route');
    setSelection(null);
  }
}

function nextPortableId(prefix: string, existingIds: string[]): string {
  const existing = new Set(existingIds);
  let index = existingIds.length + 1;
  while (existing.has(`${prefix}-${index}`)) {
    index += 1;
  }
  return `${prefix}-${index}`;
}

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, candidateIndex) => candidateIndex === index ? value : item);
}
