import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  CircleDot,
  Cpu,
  Factory,
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
  LineControllerAuthorization,
  OperationResourceBinding,
  OperationResourceKind,
  OperationResourceResolution,
  ProcessDefinitionSummary,
  ProductionContextValueKind,
  ProductionLineResponse,
  ProductionLineSummaryResponse,
  ProductionOperationRequest,
  ProductionTerminalDisposition,
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
import { useEditorDocument } from './editor-workspace';
import {
  readEditorConflictRevision,
  type EditorDocumentConflict,
  type EditorProblem
} from './editor-workspace-model';

export interface ProductionWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
  onProblemsChanged?(problems: ProductionDesignerProblem[]): void;
}

interface ProductionLineDraft extends SaveProductionLineRequest {
  persisted: boolean;
  dirty: boolean;
  revision: string;
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
const terminalDispositions: ProductionTerminalDisposition[] = [
  'Completed',
  'Nonconforming',
  'Held',
  'Scrapped'
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
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<EditorDocumentConflict | null>(null);

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
    setConflict(null);
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
      setConflict(null);
      onMessage(`Production line opened ${response.body.lineDefinitionId}`);
    } catch (error) {
      onMessage(`Production line load failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [onMessage, scope, topology]);

  const reloadLine = useCallback(async () => {
    if (!scope || !draft.persisted) {
      const next = createEmptyDraft(topology, publishedFlows, configurationSnapshots, stationProfiles);
      setDraft(next);
      setNodePositions(createAutoRouteLayout(next.operations, next.transitions, topology).positions);
      setSelection({ kind: 'operation', id: next.entryOperationId });
      return;
    }

    const response = await getProductionLine(draft.lineDefinitionId, scope);
    if (!response.ok || !response.body) {
      throw new Error(`Production line reload failed: ${response.status} ${response.text}`);
    }
    const next = fromResponse(response.body);
    setDraft(next);
    setNodePositions(createAutoRouteLayout(next.operations, next.transitions, topology).positions);
    setSelection({ kind: 'operation', id: next.entryOperationId });
  }, [configurationSnapshots, draft.lineDefinitionId, draft.persisted, publishedFlows, scope, stationProfiles, topology]);

  const save = useCallback(async (force = false) => {
    if (!scope || !isBackendHealthy) {
      throw new Error('Open an Application with a healthy backend before saving a production line');
    }
    const firstError = problems.find(problem => problem.severity === 'Error');
    if (firstError) {
      onMessage(`Cannot save: ${firstError.message}`);
      focusProblem(firstError, setSelection);
      throw new Error(firstError.message);
    }

    setBusy(true);
    try {
      const response = draft.persisted
        ? await replaceProductionLine(
          draft.lineDefinitionId,
          request,
          scope,
          { revision: draft.revision, force })
        : await createProductionLine(request, scope);
      if (!response.ok || !response.body) {
        if (response.status === 412) {
          const currentRevision = readEditorConflictRevision(response.text);
          if (currentRevision) {
            setConflict({
              loadedRevision: draft.revision,
              currentRevision,
              reload: async () => {
                await reloadLine();
                setConflict(null);
              },
              overwrite: async () => {
                await save(true);
                setConflict(null);
              }
            });
          }
        }
        onMessage(`Production line save failed: ${response.status} ${response.text}`);
        throw new Error(`Production line save failed: ${response.status}`);
      }
      setDraft(fromResponse(response.body));
      setConflict(null);
      await refresh();
      onMessage(`Production line saved ${response.body.lineDefinitionId}`);
    } catch (error) {
      onMessage(`Production line save failed: ${String(error)}`);
      throw error;
    } finally {
      setBusy(false);
    }
  }, [draft.lineDefinitionId, draft.persisted, draft.revision, isBackendHealthy, onMessage, problems, refresh, reloadLine, request, scope]);

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
      configurationSnapshotId,
      resources: [createStationResource(operationId, stationSystemId)]
    };
    const candidateSource = selectedOperation ?? draft.operations[draft.operations.length - 1];
    const canAutoConnect = candidateSource
      && !draft.transitions.some(transition => (
        transition.sourceOperationId === candidateSource.operationId
        && transition.kind !== 'Rework'
        && transition.terminalDisposition === null));
    const transition: RouteTransitionRequest | null = canAutoConnect
      ? {
        transitionId: nextPortableId(
          'route',
          draft.transitions.map(candidate => candidate.transitionId)),
        sourceOperationId: candidateSource.operationId,
        targetOperationId: operationId,
        terminalDisposition: null,
        kind: 'Sequence',
        requiredJudgement: null,
        maxTraversals: null,
        parallelGroupId: null,
        outputKey: null,
        expectedOutputKind: null,
        expectedOutputValue: null
      }
      : null;
    const terminalTransition: RouteTransitionRequest = {
      transitionId: nextPortableId(
        'route-terminal',
        [...draft.transitions, ...(transition ? [transition] : [])]
          .map(candidate => candidate.transitionId)),
      sourceOperationId: operationId,
      targetOperationId: null,
      terminalDisposition: 'Completed',
      kind: 'Sequence',
      requiredJudgement: null,
      maxTraversals: null,
      parallelGroupId: null,
      outputKey: null,
      expectedOutputKind: null,
      expectedOutputValue: null
    };
    const nextOperations = [...draft.operations, operation];
    const priorTransitions = canAutoConnect && candidateSource
      ? draft.transitions.filter(candidate => !(
        candidate.sourceOperationId === candidateSource.operationId
        && candidate.terminalDisposition !== null))
      : draft.transitions;
    const nextTransitions = [
      ...priorTransitions,
      ...(transition ? [transition] : []),
      terminalTransition
    ];
    mutateDraft(current => ({
      ...current,
      entryOperationId: current.operations.length === 0 ? operationId : current.entryOperationId,
      operations: nextOperations,
      transitions: nextTransitions
    }));
    const auto = createAutoRouteLayout(nextOperations, nextTransitions, topology).positions;
    setNodePositions(current => ({ ...auto, ...current, [operationId]: auto[operationId] }));
    setSelection({ kind: 'operation', id: operationId });
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
      terminalDisposition: null,
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
  }, [draft.entryOperationId, draft.operations, draft.transitions, mutateDraft, onMessage, selection]);

  const updateOperation = useCallback((originalId: string, next: ProductionOperationRequest) => {
    mutateDraft(current => ({
      ...current,
      entryOperationId: current.entryOperationId === originalId ? next.operationId : current.entryOperationId,
      operations: current.operations.map(operation => operation.operationId === originalId ? next : operation),
      lineControllerAuthorizations: current.operations.find(operation =>
        operation.operationId === originalId)?.stationSystemId !== next.stationSystemId
        ? current.lineControllerAuthorizations.filter(authorization =>
          authorization.operationId !== originalId)
        : current.lineControllerAuthorizations.map(authorization =>
          authorization.operationId === originalId
            ? { ...authorization, operationId: next.operationId }
            : authorization),
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
        lineControllerAuthorizations: current.lineControllerAuthorizations.filter(
          authorization => authorization.operationId !== operationId),
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

  const editorProblems = useMemo<EditorProblem[]>(() => problems.map(problem => ({
    id: problem.id,
    severity: problem.severity,
    message: problem.message,
    targetId: problem.entityId
  })), [problems]);
  useEditorDocument({
    dirty: draft.dirty,
    canSave: isBackendHealthy && errorCount === 0,
    save: () => save(),
    revert: reloadLine,
    focus: targetId => {
      const problem = problems.find(candidate => candidate.entityId === targetId);
      if (problem) focusProblem(problem, setSelection);
    },
    problems: editorProblems,
    conflict
  });

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
            onClick={() => void save().catch(() => undefined)}
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
                  data-testid="production-product-model-id"
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
            <button type="button" className="active">
              <Workflow size={14} /> Route Graph
              <small>{draft.operations.length}</small>
            </button>
            <button
              type="button"
              className={errorCount > 0 ? 'problems' : ''}
              onClick={() => {
                const first = problems[0];
                if (first) {
                  focusProblem(first, setSelection);
                }
              }}
            >
              <AlertTriangle size={14} /> Problems
              <small>{problems.length}</small>
            </button>
          </div>

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
                    topology={topology}
                    lineControllerAuthorizations={draft.lineControllerAuthorizations.filter(
                      authorization => authorization.operationId === selectedOperation.operationId)}
                    onLineControllerAuthorizationsChange={authorizations => mutateDraft(current => ({
                      ...current,
                      lineControllerAuthorizations: [
                        ...current.lineControllerAuthorizations.filter(authorization =>
                          authorization.operationId !== selectedOperation.operationId),
                        ...authorizations
                      ]
                    }))}
                    onChange={next => updateOperation(selectedOperation.operationId, next)}
                    onMakeEntry={() => mutateDraft(current => ({
                      ...current,
                      entryOperationId: selectedOperation.operationId
                    }))}
                    onMarkTerminal={terminalDisposition => mutateDraft(current => ({
                      ...current,
                      transitions: [
                        ...current.transitions.filter(transition =>
                          transition.sourceOperationId !== selectedOperation.operationId),
                        {
                          transitionId: nextPortableId(
                            'route-terminal',
                            current.transitions.map(transition => transition.transitionId)),
                          sourceOperationId: selectedOperation.operationId,
                          targetOperationId: null,
                          terminalDisposition,
                          kind: 'Sequence',
                          requiredJudgement: null,
                          maxTraversals: null,
                          parallelGroupId: null,
                          outputKey: null,
                          expectedOutputKind: null,
                          expectedOutputValue: null
                        }
                      ]
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
                  onSelect={problem => focusProblem(problem, setSelection)}
                />
              </aside>
          </section>
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
  topology,
  lineControllerAuthorizations,
  onLineControllerAuthorizationsChange,
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
  topology: AutomationTopologyResponse | null;
  lineControllerAuthorizations: LineControllerAuthorization[];
  onLineControllerAuthorizationsChange(authorizations: LineControllerAuthorization[]): void;
  onChange(next: ProductionOperationRequest): void;
  onMakeEntry(): void;
  onMarkTerminal(disposition: ProductionTerminalDisposition): void;
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
  const explicitTerminals = forwardTransitions.filter(transition => transition.terminalDisposition !== null);
  const explicitTerminalDispositions = terminalDispositions.filter(disposition =>
    explicitTerminals.some(transition => transition.terminalDisposition === disposition));
  const terminalReplacement = explicitTerminalDispositions.length === 1
    ? explicitTerminalDispositions[0]
    : 'Completed';
  const updateBinding = (changes: Partial<Pick<ProductionOperationRequest, 'stationSystemId' | 'flowDefinitionId'>>): void => {
    const next = { ...operation, ...changes };
    if (changes.stationSystemId !== undefined
        && changes.stationSystemId !== operation.stationSystemId) {
      next.resources = [createStationResource(operation.operationId, changes.stationSystemId)];
    }
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
      <OperationResourcesEditor
        operation={operation}
        topology={topology}
        onChange={resources => onChange({ ...operation, resources })}
      />
      <LineControllerAuthorizationEditor
        operation={operation}
        topology={topology}
        authorizations={lineControllerAuthorizations}
        onChange={onLineControllerAuthorizationsChange}
      />
      <div className="production-disposition-card">
        <span>ROUTE DISPOSITION</span>
        <strong>
          {explicitTerminalDispositions.length === 0
            ? 'In process'
            : explicitTerminalDispositions.length === 1
              ? `${explicitTerminalDispositions[0]} (terminal)`
              : `${explicitTerminalDispositions.join(' / ')} (conditional terminals)`}
        </strong>
        <small>
          {explicitTerminalDispositions.length > 0
            ? `${explicitTerminals.length} explicit conditional route edge${explicitTerminals.length === 1 ? '' : 's'} determine the product disposition.`
            : `${forwardTransitions.length} forward Route Transition${forwardTransitions.length === 1 ? '' : 's'} continue execution.`}
        </small>
        <label>
          <span>Replace all outgoing routes with terminal</span>
          <select
            value={terminalReplacement}
            onChange={event => onMarkTerminal(event.target.value as ProductionTerminalDisposition)}
          >
            {terminalDispositions.map(disposition => (
              <option key={disposition} value={disposition}>{disposition}</option>
            ))}
          </select>
        </label>
        <button
          type="button"
          className="button ghost"
          onClick={() => onMarkTerminal(terminalReplacement)}
        >
          {forwardTransitions.length > 0 ? 'Replace routes with terminal' : 'Mark terminal'}
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
        <button type="button" className="button danger" onClick={onRemove} data-testid="remove-production-operation">
          <Trash2 size={13} /> Remove
        </button>
      </footer>
    </section>
  );
}

function OperationResourcesEditor({
  operation,
  topology,
  onChange
}: {
  operation: ProductionOperationRequest;
  topology: AutomationTopologyResponse | null;
  onChange(resources: OperationResourceBinding[]): void;
}): React.ReactElement {
  const nonStationResources = operation.resources.filter(resource => resource.kind !== 'Station');
  const addResource = (): void => {
    const bindingId = nextPortableId(
      'resource',
      operation.resources.map(resource => resource.bindingId));
    onChange([
      ...operation.resources,
      {
        bindingId,
        kind: 'Slot',
        topologyTargetId: operation.stationSystemId,
        resolution: 'CurrentMaterialSlot'
      }
    ]);
  };
  const replace = (bindingId: string, next: OperationResourceBinding): void => {
    onChange(operation.resources.map(resource => resource.bindingId === bindingId ? next : resource));
  };
  const remove = (bindingId: string): void => {
    onChange(operation.resources.filter(resource => resource.bindingId !== bindingId));
  };

  return (
    <section className="production-resource-editor" data-testid="operation-resource-editor">
      <header>
        <span><small>RESOURCE AUTHORIZATION</small><strong>Station execution scope</strong></span>
        <button type="button" className="button ghost" onClick={addResource}>
          <Plus size={13} /> Resource
        </button>
      </header>
      <div className="production-resource-station">
        <Cpu size={14} />
        <span>
          <strong>{operation.stationSystemId || 'Select a Station'}</strong>
          <small>Exactly one fixed Station lease is always frozen.</small>
        </span>
      </div>
      {nonStationResources.length === 0 ? (
        <p className="production-resource-empty">
          Add Fixture, Device, Slot Group or material Slot requirements when this Operation needs them.
        </p>
      ) : (
        <div className="production-resource-list">
          {nonStationResources.map(resource => {
            const targets = operationResourceTargets(resource, operation.stationSystemId, topology);
            return (
              <article key={resource.bindingId}>
                <label>
                  <span>Binding ID</span>
                  <input
                    value={resource.bindingId}
                    onChange={event => replace(resource.bindingId, {
                      ...resource,
                      bindingId: event.target.value
                    })}
                  />
                </label>
                <label>
                  <span>Kind</span>
                  <select
                    value={resource.kind}
                    onChange={event => {
                      const kind = event.target.value as OperationResourceKind;
                      const resolution: OperationResourceResolution = kind === 'Slot'
                        ? 'CurrentMaterialSlot'
                        : 'Fixed';
                      const candidate: OperationResourceBinding = {
                        ...resource,
                        kind,
                        resolution,
                        topologyTargetId: kind === 'Slot'
                          ? operation.stationSystemId
                          : ''
                      };
                      const nextTargets = operationResourceTargets(
                        candidate,
                        operation.stationSystemId,
                        topology);
                      replace(resource.bindingId, {
                        ...candidate,
                        topologyTargetId: nextTargets[0]?.id ?? candidate.topologyTargetId
                      });
                    }}
                  >
                    <option value="Fixture">Fixture</option>
                    <option value="Device">Device</option>
                    <option value="SlotGroup">Slot Group</option>
                    <option value="Slot">Slot</option>
                  </select>
                </label>
                <label>
                  <span>Resolution</span>
                  <select
                    value={resource.resolution}
                    disabled={resource.kind !== 'Slot'}
                    onChange={event => {
                      const resolution = event.target.value as OperationResourceResolution;
                      const candidate = {
                        ...resource,
                        resolution,
                        topologyTargetId: resolution === 'CurrentMaterialSlot'
                          ? operation.stationSystemId
                          : ''
                      };
                      const nextTargets = operationResourceTargets(
                        candidate,
                        operation.stationSystemId,
                        topology);
                      replace(resource.bindingId, {
                        ...candidate,
                        topologyTargetId: nextTargets[0]?.id ?? candidate.topologyTargetId
                      });
                    }}
                  >
                    <option value="Fixed">Fixed</option>
                    <option value="CurrentMaterialSlot">Current material Slot</option>
                    <option value="AvailableSlotInGroup">Available Slot in Group</option>
                  </select>
                </label>
                <label>
                  <span>Topology target</span>
                  <select
                    value={resource.topologyTargetId}
                    onChange={event => replace(resource.bindingId, {
                      ...resource,
                      topologyTargetId: event.target.value
                    })}
                  >
                    <option value="">Select target</option>
                    {targets.map(target => (
                      <option key={`${target.kind}:${target.id}`} value={target.id}>
                        {target.label} · {target.kind}
                      </option>
                    ))}
                  </select>
                </label>
                <button
                  type="button"
                  className="button danger"
                  aria-label={`Remove resource ${resource.bindingId}`}
                  onClick={() => remove(resource.bindingId)}
                >
                  <Trash2 size={13} />
                </button>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}

function createStationResource(operationId: string, stationSystemId: string): OperationResourceBinding {
  return {
    bindingId: `resource.station.${operationId}`,
    kind: 'Station',
    topologyTargetId: stationSystemId,
    resolution: 'Fixed'
  };
}

function operationResourceTargets(
  resource: OperationResourceBinding,
  stationSystemId: string,
  topology: AutomationTopologyResponse | null
): Array<{ id: string; label: string; kind: string }> {
  if (!topology) {
    return [];
  }
  if (resource.kind === 'Fixture') {
    return topology.slotGroups
      .filter(group => group.parentSystemId === stationSystemId && group.kind === 'FixtureNest')
      .map(group => ({ id: group.slotGroupId, label: group.displayName, kind: 'FixtureNest' }));
  }
  if (resource.kind === 'Device') {
    return [
      ...topology.systems
        .filter(system => systemWithinStation(system.systemId, stationSystemId, topology))
        .map(system => ({ id: system.systemId, label: system.displayName, kind: 'System' })),
      ...topology.driverBindings
        .filter(binding => systemWithinStation(binding.ownerSystemId, stationSystemId, topology)
          && ['Simulator', 'DeviceInstance', 'PluginCommand', 'ExternalSystem'].includes(
            binding.providerKind))
        .map(binding => ({ id: binding.bindingId, label: binding.providerKey, kind: binding.providerKind }))
    ];
  }
  if (resource.kind === 'SlotGroup'
      || (resource.kind === 'Slot' && resource.resolution === 'AvailableSlotInGroup')) {
    return topology.slotGroups
      .filter(group => group.parentSystemId === stationSystemId)
      .map(group => ({ id: group.slotGroupId, label: group.displayName, kind: group.kind }));
  }
  if (resource.kind === 'Slot' && resource.resolution === 'CurrentMaterialSlot') {
    const station = topology.systems.find(system => system.systemId === stationSystemId);
    return station
      ? [{ id: station.systemId, label: station.displayName, kind: 'Current material' }]
      : [];
  }
  return topology.slots
    .filter(slot => slot.parentSystemId === stationSystemId && slot.isEnabled)
    .map(slot => ({ id: slot.slotId, label: slot.displayName, kind: slot.materialKind }));
}

function LineControllerAuthorizationEditor({
  operation,
  topology,
  authorizations,
  onChange
}: {
  operation: ProductionOperationRequest;
  topology: AutomationTopologyResponse | null;
  authorizations: LineControllerAuthorization[];
  onChange(authorizations: LineControllerAuthorization[]): void;
}): React.ReactElement {
  const controllerResourceIds = new Set(operation.resources
    .filter(resource => resource.kind === 'Device' && resource.resolution === 'Fixed')
    .map(resource => resource.topologyTargetId));
  const controllerBindings = (topology?.driverBindings ?? []).filter(binding => (
    controllerResourceIds.has(binding.bindingId)
    && systemWithinStation(binding.ownerSystemId, operation.stationSystemId, topology)
    && ['Simulator', 'DeviceInstance', 'PluginCommand', 'ExternalSystem'].includes(
      binding.providerKind)));
  const remoteStations = (topology?.systems ?? []).filter(system => (
    system.kind === 'Station' && system.systemId !== operation.stationSystemId));

  const replace = (
    authorizationId: string,
    next: LineControllerAuthorization
  ): void => onChange(authorizations.map(authorization => (
    authorization.authorizationId === authorizationId ? next : authorization)));
  const add = (): void => {
    if (!topology || controllerBindings.length === 0 || remoteStations.length === 0) {
      return;
    }
    const controller = controllerBindings[0];
    const targetStation = remoteStations[0];
    const target = topology.driverBindings.find(binding => systemWithinStation(
      binding.ownerSystemId,
      targetStation.systemId,
      topology));
    if (!target) {
      return;
    }
    const controllerCapability = topology.capabilities.find(capability =>
      capability.capabilityId === controller.capabilityId);
    const targetCapability = topology.capabilities.find(capability =>
      capability.capabilityId === target.capabilityId);
    if (!controllerCapability || !targetCapability) {
      return;
    }
    onChange([
      ...authorizations,
      {
        authorizationId: nextPortableId(
          'line-controller',
          authorizations.map(authorization => authorization.authorizationId)),
        operationId: operation.operationId,
        actionId: nextPortableId(
          'remote-action',
          authorizations.map(authorization => authorization.actionId)),
        controllerSystemId: controller.ownerSystemId,
        controllerBindingId: controller.bindingId,
        controllerCapabilityId: controller.capabilityId,
        controllerAction: controllerCapability.commandName,
        targetStationSystemId: targetStation.systemId,
        targetSystemId: target.ownerSystemId,
        targetBindingId: target.bindingId,
        targetCapabilityId: target.capabilityId,
        targetAction: targetCapability.commandName
      }
    ]);
  };

  return (
    <section className="production-resource-editor" data-testid="line-controller-authorization-editor">
      <header>
        <span><small>CROSS-STATION AUTHORIZATION</small><strong>Line Controller grants</strong></span>
        <button
          type="button"
          className="button ghost"
          onClick={add}
          disabled={controllerBindings.length === 0 || remoteStations.length === 0}
        >
          <Plus size={13} /> Authorization
        </button>
      </header>
      {controllerBindings.length === 0 ? (
        <p className="production-resource-empty">
          Add the local Line Controller Binding as a Fixed Device resource before granting a remote action.
        </p>
      ) : authorizations.length === 0 ? (
        <p className="production-resource-empty">
          No cross-Station access. Flow actions remain confined to this Station subtree.
        </p>
      ) : (
        <div className="production-resource-list">
          {authorizations.map(authorization => {
            const targetBindings = (topology?.driverBindings ?? []).filter(binding =>
              systemWithinStation(
                binding.ownerSystemId,
                authorization.targetStationSystemId,
                topology));
            return (
              <article key={authorization.authorizationId}>
                <label>
                  <span>Authorization ID</span>
                  <input
                    value={authorization.authorizationId}
                    onChange={event => replace(authorization.authorizationId, {
                      ...authorization,
                      authorizationId: event.target.value
                    })}
                  />
                </label>
                <label>
                  <span>Flow Action ID</span>
                  <input
                    value={authorization.actionId}
                    onChange={event => replace(authorization.authorizationId, {
                      ...authorization,
                      actionId: event.target.value
                    })}
                  />
                </label>
                <label>
                  <span>Controller Binding / Capability / Action</span>
                  <select
                    value={authorization.controllerBindingId}
                    onChange={event => {
                      const binding = controllerBindings.find(candidate =>
                        candidate.bindingId === event.target.value);
                      const capability = topology?.capabilities.find(candidate =>
                        candidate.capabilityId === binding?.capabilityId);
                      if (binding && capability) {
                        replace(authorization.authorizationId, {
                          ...authorization,
                          controllerSystemId: binding.ownerSystemId,
                          controllerBindingId: binding.bindingId,
                          controllerCapabilityId: binding.capabilityId,
                          controllerAction: capability.commandName
                        });
                      }
                    }}
                  >
                    {controllerBindings.map(binding => {
                      const capability = topology?.capabilities.find(candidate =>
                        candidate.capabilityId === binding.capabilityId);
                      return (
                        <option key={binding.bindingId} value={binding.bindingId}>
                          {binding.ownerSystemId} / {binding.bindingId} / {binding.capabilityId} / {capability?.commandName}
                        </option>
                      );
                    })}
                  </select>
                </label>
                <label>
                  <span>Target Station</span>
                  <select
                    value={authorization.targetStationSystemId}
                    onChange={event => {
                      const stationSystemId = event.target.value;
                      const binding = topology?.driverBindings.find(candidate =>
                        systemWithinStation(candidate.ownerSystemId, stationSystemId, topology));
                      const capability = topology?.capabilities.find(candidate =>
                        candidate.capabilityId === binding?.capabilityId);
                      if (binding && capability) {
                        replace(authorization.authorizationId, {
                          ...authorization,
                          targetStationSystemId: stationSystemId,
                          targetSystemId: binding.ownerSystemId,
                          targetBindingId: binding.bindingId,
                          targetCapabilityId: binding.capabilityId,
                          targetAction: capability.commandName
                        });
                      }
                    }}
                  >
                    {remoteStations.map(station => (
                      <option key={station.systemId} value={station.systemId}>
                        {station.displayName} / {station.systemId}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  <span>Target System / Binding / Capability / Action</span>
                  <select
                    value={authorization.targetBindingId}
                    onChange={event => {
                      const binding = targetBindings.find(candidate =>
                        candidate.bindingId === event.target.value);
                      const capability = topology?.capabilities.find(candidate =>
                        candidate.capabilityId === binding?.capabilityId);
                      if (binding && capability) {
                        replace(authorization.authorizationId, {
                          ...authorization,
                          targetSystemId: binding.ownerSystemId,
                          targetBindingId: binding.bindingId,
                          targetCapabilityId: binding.capabilityId,
                          targetAction: capability.commandName
                        });
                      }
                    }}
                  >
                    {targetBindings.map(binding => {
                      const capability = topology?.capabilities.find(candidate =>
                        candidate.capabilityId === binding.capabilityId);
                      return (
                        <option key={binding.bindingId} value={binding.bindingId}>
                          {binding.ownerSystemId} / {binding.bindingId} / {binding.capabilityId} / {capability?.commandName}
                        </option>
                      );
                    })}
                  </select>
                </label>
                <button
                  type="button"
                  className="button danger"
                  aria-label={`Remove Line Controller authorization ${authorization.authorizationId}`}
                  onClick={() => onChange(authorizations.filter(candidate =>
                    candidate.authorizationId !== authorization.authorizationId))}
                >
                  <Trash2 size={13} />
                </button>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}

function systemWithinStation(
  systemId: string,
  stationSystemId: string,
  topology: AutomationTopologyResponse | null
): boolean {
  if (!topology) {
    return false;
  }
  let currentId: string | null = systemId;
  for (let depth = 0; depth <= topology.systems.length && currentId; depth += 1) {
    if (currentId === stationSystemId) {
      return true;
    }
    currentId = topology.systems.find(system => system.systemId === currentId)?.parentSystemId ?? null;
  }
  return false;
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
            onChange={event => {
              const sourceOperationId = event.target.value;
              onChange({
                ...transition,
                sourceOperationId,
                targetOperationId: transition.targetOperationId === sourceOperationId
                  ? operations.find(operation => operation.operationId !== sourceOperationId)?.operationId ?? null
                  : transition.targetOperationId
              });
            }}
          >
            {operations.map(operation => (
              <option key={operation.operationId} value={operation.operationId}>{operation.displayName}</option>
            ))}
          </select>
        </Field>
        <Field label="Target type">
          <select
            value={transition.terminalDisposition === null ? 'Operation' : 'Terminal'}
            disabled={transition.kind === 'Rework'
              || transition.kind === 'ParallelFork'
              || transition.kind === 'ParallelJoin'}
            onChange={event => onChange(event.target.value === 'Terminal'
              ? {
                ...transition,
                targetOperationId: null,
                terminalDisposition: 'Completed'
              }
              : {
                ...transition,
                targetOperationId: operations.find(operation => (
                  operation.operationId !== transition.sourceOperationId))?.operationId ?? null,
                terminalDisposition: null
              })}
          >
            <option value="Operation">Operation</option>
            <option value="Terminal">Terminal disposition</option>
          </select>
        </Field>
        {transition.terminalDisposition === null ? (
        <Field label="Target Operation">
          <select
            value={transition.targetOperationId ?? ''}
            onChange={event => onChange({
              ...transition,
              targetOperationId: event.target.value,
              terminalDisposition: null
            })}
          >
            {operations.filter(operation => (
              operation.operationId !== transition.sourceOperationId)).map(operation => (
              <option key={operation.operationId} value={operation.operationId}>{operation.displayName}</option>
            ))}
          </select>
        </Field>
        ) : (
        <Field label="Terminal disposition">
          <select
            value={transition.terminalDisposition}
            onChange={event => onChange({
              ...transition,
              targetOperationId: null,
              terminalDisposition: event.target.value as ProductionTerminalDisposition
            })}
          >
            {terminalDispositions.map(disposition => (
              <option key={disposition} value={disposition}>{disposition}</option>
            ))}
          </select>
        </Field>
        )}
        <Field label="Transition Kind">
          <select
            value={transition.kind}
            data-testid="route-transition-kind"
            onChange={event => {
              const kind = event.target.value as RouteTransitionKind;
              const normalized = normalizeTransitionKind(transition, kind);
              onChange(kind === 'Rework' || kind === 'ParallelFork' || kind === 'ParallelJoin'
                ? {
                  ...normalized,
                  targetOperationId: normalized.targetOperationId
                    ?? operations.find(operation => (
                      operation.operationId !== normalized.sourceOperationId))?.operationId
                    ?? null,
                  terminalDisposition: null
                }
                : normalized);
            }}
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
        <button type="button" className="button danger" onClick={onRemove} data-testid="remove-route-transition">
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
    configurationSnapshotId,
    resources: [createStationResource('operation-1', station?.systemId ?? '')]
  };
  return {
    persisted: false,
    dirty: true,
    revision: '',
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
    lineControllerAuthorizations: []
  };
}

function fromResponse(response: ProductionLineResponse): ProductionLineDraft {
  return {
    persisted: true,
    dirty: false,
    revision: response.revision,
    lineDefinitionId: response.lineDefinitionId,
    displayName: response.displayName,
    topologyId: response.topologyId,
    productModel: { ...response.productModel },
    entryOperationId: response.entryOperationId,
    operations: response.operations.map(operation => ({
      ...operation,
      resources: operation.resources.map(resource => ({ ...resource }))
    })),
    transitions: response.transitions.map(transition => ({ ...transition })),
    lineControllerAuthorizations: response.lineControllerAuthorizations.map(
      authorization => ({ ...authorization }))
  };
}

function toRequest(draft: ProductionLineDraft): SaveProductionLineRequest {
  return {
    lineDefinitionId: draft.lineDefinitionId,
    displayName: draft.displayName,
    topologyId: draft.topologyId,
    productModel: { ...draft.productModel },
    entryOperationId: draft.entryOperationId,
    operations: draft.operations.map(operation => ({
      ...operation,
      resources: operation.resources.map(resource => ({ ...resource }))
    })),
    transitions: draft.transitions.map(transition => ({ ...transition })),
    lineControllerAuthorizations: draft.lineControllerAuthorizations.map(
      authorization => ({ ...authorization }))
  };
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
      targetOperationId: transition.targetOperationId,
      terminalDisposition: null,
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
      targetOperationId: transition.targetOperationId,
      terminalDisposition: null,
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
  setSelection: React.Dispatch<React.SetStateAction<DesignerSelection>>
): void {
  if (problem.scope === 'Operation') {
    setSelection({ kind: 'operation', id: problem.entityId });
  } else if (problem.scope === 'Transition') {
    setSelection({ kind: 'transition', id: problem.entityId });
  } else {
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
