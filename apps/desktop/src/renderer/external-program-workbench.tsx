import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ArrowRight,
  FileCode2,
  FlaskConical,
  FolderTree,
  Hash,
  Plus,
  RefreshCw,
  Save,
  ShieldCheck,
  Trash2,
  Upload
} from 'lucide-react';
import type {
  AutomationProjectWorkspaceResponse,
  AutomationTopologyResponse,
  ExternalProgramResourceResponse,
  ExternalProgramTrialInputKind,
  ExternalProgramTrialResponse,
  ProductionContextValueKind,
  SaveExternalProgramResourceRequest
} from './contracts';
import { externalProgramDirectoryImportLimits } from '../shared/external-program-directory-import-contract';
import {
  deleteExternalProgramResource,
  getAutomationTopology,
  getProductionLine,
  importExternalProgramDirectory,
  listExternalProgramResources,
  listProductionLines,
  saveExternalProgramResource,
  trialExternalProgramResource,
  type ProjectApplicationApiScope
} from './api';
import { desktop } from './desktop-bridge';
import { useEditorDocument } from './editor-workspace';
import {
  DraftTransitionDialog,
  useDraftTransitionGuard
} from './draft-transition-guard';
import {
  readEditorConflictRevision,
  type EditorDocumentConflict,
  type EditorProblem
} from './editor-workspace-model';
import {
  chooseExternalProgramEntryPoint,
  isSupportedExternalProgramEntryPoint,
  requireExternalProgramDirectorySelection,
  type ExternalProgramDirectorySelection
} from './external-program-directory-selection';
import {
  loadExternalProgramResourceCore,
  runLatestExternalProgramRequest
} from './external-program-resource-loader';
import { LatestRequestLease } from './latest-request-lease';

interface ExternalProgramWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface ResourceDraft extends SaveExternalProgramResourceRequest {
  persisted: boolean;
  dirty: boolean;
  files: ExternalProgramResourceResponse['files'];
  contentSha256: string;
  revision: string;
}

const valueKinds: ProductionContextValueKind[] = [
  'Text', 'Boolean', 'WholeNumber', 'FixedPoint', 'DateTimeUtc'
];
const trialKinds: ExternalProgramTrialInputKind[] = [
  'Text', 'IntegralNumber', 'FractionalNumber', 'Logical'
];

export function ExternalProgramWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onMessage
}: ExternalProgramWorkbenchProps): React.ReactElement {
  const application = activeWorkspace?.project.applications.find(
    candidate => candidate.applicationId === activeApplicationId)
    ?? activeWorkspace?.project.applications[0]
    ?? null;
  const scope = useMemo<ProjectApplicationApiScope | null>(() => activeWorkspace && application
    ? { projectId: activeWorkspace.project.projectId, applicationId: application.applicationId }
    : null, [activeWorkspace, application]);
  const scopeKey = scope ? `${scope.projectId}\0${scope.applicationId}` : null;
  const currentScopeKey = useRef(scopeKey);
  currentScopeKey.current = scopeKey;
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [productionInputKeys, setProductionInputKeys] = useState<string[]>([]);
  const [resources, setResources] = useState<ExternalProgramResourceResponse[]>([]);
  const [draft, setDraft] = useState<ResourceDraft>(() => createDraft(null, false));
  const editorGeneration = useRef(0);
  const refreshRequestEpoch = useRef(0);
  const resourceLoadLease = useRef(new LatestRequestLease());
  const currentDraftDirty = useRef(draft.dirty);
  currentDraftDirty.current = draft.dirty;
  const updateDraft = useCallback<React.Dispatch<React.SetStateAction<ResourceDraft>>>(update => {
    editorGeneration.current++;
    setDraft(current => {
      const next = typeof update === 'function' ? update(current) : update;
      return { ...next, dirty: true };
    });
  }, []);
  const [busy, setBusy] = useState(false);
  const [resourceLoading, setResourceLoading] = useState(false);
  const operationInFlight = useRef(false);
  const [trialKindsByTarget, setTrialKindsByTarget] = useState<Record<string, ExternalProgramTrialInputKind>>({});
  const [trialValues, setTrialValues] = useState<Record<string, string>>({});
  const [trialResult, setTrialResult] = useState<ExternalProgramTrialResponse | null>(null);
  const [conflict, setConflict] = useState<EditorDocumentConflict | null>(null);
  const [pendingDirectory, setPendingDirectory] = useState<ExternalProgramDirectorySelection | null>(null);
  const latestSaveRef = useRef<(force?: boolean) => Promise<void>>(async () => undefined);
  const beginOperation = useCallback((description: string): boolean => {
    if (operationInFlight.current || resourceLoadLease.current.busy) {
      onMessage(`${description} was ignored because another Program Resource operation is still running.`);
      return false;
    }
    operationInFlight.current = true;
    setBusy(true);
    return true;
  }, [onMessage]);
  const endOperation = useCallback((): void => {
    operationInFlight.current = false;
    setBusy(false);
  }, []);
  const editorIdentity = `${scopeKey ?? ''}\0${draft.persisted ? 'persisted' : 'new'}\0${draft.revision}`
    + `\0${pendingDirectory?.selectionId ?? ''}\0${JSON.stringify(toRequest(draft))}`;
  const currentEditorIdentity = useRef(editorIdentity);
  currentEditorIdentity.current = editorIdentity;
  const previousRefreshScopeKey = useRef<string | null>(null);
  const isCurrentEditorOperation = useCallback(
    (expectedScopeKey: string | null, expectedEditorIdentity: string): boolean =>
      currentScopeKey.current === expectedScopeKey
      && currentEditorIdentity.current === expectedEditorIdentity,
    []);

  useEffect(() => {
    const selectionId = pendingDirectory?.selectionId;
    return () => {
      if (selectionId) {
        void desktop.releaseExternalProgramDirectorySelection(selectionId).catch(() => undefined);
      }
    };
  }, [pendingDirectory?.selectionId]);

  const selectResource = useCallback((resource: ExternalProgramResourceResponse) => {
    editorGeneration.current++;
    setPendingDirectory(null);
    setDraft(fromResponse(resource));
    setTrialKindsByTarget(Object.fromEntries(resource.inputMappings.map(item => [item.target, 'Text'])));
    setTrialValues(Object.fromEntries(resource.inputMappings.map(item => [item.target, 'value'])));
    setTrialResult(null);
    setConflict(null);
  }, []);

  const loadResource = useCallback(async (resourceId: string) => {
    if (!scope) {
      throw new Error('Open an Application before loading a Program Resource.');
    }
    if (operationInFlight.current) {
      throw new Error('Another Program Resource operation is still running.');
    }
    const requestedResourceLoadEpoch = resourceLoadLease.current.start();
    editorGeneration.current++;
    const startedScopeKey = currentScopeKey.current;
    const startedEditorIdentity = currentEditorIdentity.current;
    setResourceLoading(true);
    try {
      const loaded = await loadExternalProgramResourceCore(
        resourceId,
        () => listExternalProgramResources(scope),
        () => {
          if (currentScopeKey.current !== startedScopeKey) {
            throw new Error('The active Application or Program Resource changed while the resource was loading.');
          }
          if (!resourceLoadLease.current.isCurrent(requestedResourceLoadEpoch)) {
            return false;
          }
          if (currentEditorIdentity.current !== startedEditorIdentity) {
            throw new Error('The Program Resource editor changed while the resource was loading.');
          }
          return true;
        });
      if (loaded) {
        setResources(loaded.resources);
        selectResource(loaded.resource);
      }
    } finally {
      if (resourceLoadLease.current.finish(requestedResourceLoadEpoch)) {
        setResourceLoading(false);
      }
    }
  }, [scope, selectResource]);

  const refresh = useCallback(async (preserveDirty = false) => {
    const requestedScopeKey = scopeKey;
    const requestedRefreshEpoch = ++refreshRequestEpoch.current;
    const startedEditorGeneration = editorGeneration.current;
    if (!scope || !isBackendHealthy) {
      editorGeneration.current++;
      setResources([]);
      setTopology(null);
      setProductionInputKeys([]);
      setPendingDirectory(null);
      setConflict(null);
      return;
    }
    const refreshed = await runLatestExternalProgramRequest(async () => {
      const [nextResources, topologyResponse, lineSummaries] = await Promise.all([
        listExternalProgramResources(scope),
        application?.topologyId
          ? getAutomationTopology(application.topologyId, scope)
          : Promise.resolve(null),
        listProductionLines(scope)
      ]);
      const lineResponses = await Promise.all(lineSummaries.map(line =>
        getProductionLine(line.lineDefinitionId, scope)));
      return { nextResources, topologyResponse, lineResponses };
    }, () => currentScopeKey.current === requestedScopeKey
      && refreshRequestEpoch.current === requestedRefreshEpoch);
    if (refreshed === null) {
      return;
    }
    const { nextResources, topologyResponse, lineResponses } = refreshed;
    setProductionInputKeys(Array.from(new Set(lineResponses.flatMap(response =>
      response.ok && response.body
        ? response.body.operations.flatMap(operation =>
            operation.inputMappings.map(mapping => mapping.targetInputKey))
        : []))).sort((left, right) => left.localeCompare(right)));
    const nextTopology = topologyResponse?.ok && topologyResponse.body ? topologyResponse.body : null;
    setResources(nextResources);
    setTopology(nextTopology);
    if (editorGeneration.current !== startedEditorGeneration
        || preserveDirty && currentDraftDirty.current) {
      return;
    }
    editorGeneration.current++;
    setPendingDirectory(null);
    setConflict(null);
    setDraft(current => {
      const currentResource = nextResources.find(item => item.resourceId === current.resourceId);
      return currentResource ? fromResponse(currentResource) : createDraft(nextTopology, false);
    });
  }, [application?.topologyId, isBackendHealthy, scope, scopeKey]);

  useEffect(() => {
    const preserveDirty = previousRefreshScopeKey.current === scopeKey && scopeKey !== null;
    previousRefreshScopeKey.current = scopeKey;
    refresh(preserveDirty).catch(error => onMessage(`External program resources refresh failed: ${String(error)}`));
  }, [onMessage, refresh, scopeKey]);

  const newResource = useCallback(() => {
    editorGeneration.current++;
    setPendingDirectory(null);
    setConflict(null);
    setDraft(createDraft(topology));
    setTrialKindsByTarget({ serialNumber: 'Text', modelCode: 'Text' });
    setTrialValues({ serialNumber: 'sample-001', modelCode: 'MODEL-A' });
    setTrialResult(null);
  }, [topology]);

  const reloadResource = useCallback(async () => {
    if (!beginOperation('Reload')) {
      throw new Error('A Program Resource operation is already running.');
    }
    try {
      if (!scope || !draft.persisted) {
        newResource();
        return;
      }
      const startedScopeKey = currentScopeKey.current;
      const startedEditorIdentity = currentEditorIdentity.current;
      const loaded = await loadExternalProgramResourceCore(
        draft.resourceId,
        () => listExternalProgramResources(scope),
        () => {
          if (!isCurrentEditorOperation(startedScopeKey, startedEditorIdentity)) {
            throw new Error('The active Application or Program Resource changed while the resource was reloading.');
          }
          return true;
        });
      if (loaded) {
        setResources(loaded.resources);
        selectResource(loaded.resource);
      }
    } finally {
      endOperation();
    }
  }, [beginOperation, draft.persisted, draft.resourceId, endOperation, isCurrentEditorOperation, newResource, scope, selectResource]);

  const save = useCallback(async (force = false) => {
    if (!scope || !isBackendHealthy) throw new Error('Backend is required to save the program resource.');
    if (!beginOperation('Save')) {
      throw new Error('A Program Resource operation is already running.');
    }
    if (draft.launchKind === 'ApplicationExecutable' && !pendingDirectory && !draft.persisted) {
      onMessage('Select the complete program directory before saving this executable resource.');
      endOperation();
      throw new Error('Select the program directory before saving this resource.');
    }
    const startedScopeKey = currentScopeKey.current;
    const startedEditorIdentity = currentEditorIdentity.current;
    try {
      const response = pendingDirectory
        ? await importExternalProgramDirectory(
          toRequest(draft),
          pendingDirectory.selectionId,
          scope,
          draft.persisted ? { revision: draft.revision, force } : undefined)
        : await saveExternalProgramResource(
          draft.resourceId,
          toRequest(draft),
          scope,
          draft.persisted ? { revision: draft.revision, force } : undefined);
      if (!isCurrentEditorOperation(startedScopeKey, startedEditorIdentity)) {
        throw new Error('The active Application or Program Resource changed while the save was running. The original scoped save completed without replacing the current editor.');
      }
      if (!response.ok || !response.body) {
        if (response.status === 412) {
          const currentRevision = readEditorConflictRevision(response.text);
          if (currentRevision) {
            setConflict({
              loadedRevision: draft.revision,
              currentRevision,
              reload: async () => {
                await reloadResource();
                setConflict(null);
              },
              overwrite: async () => {
                await latestSaveRef.current(true);
                setConflict(null);
              }
            });
          }
        }
        throw new Error(`External program save failed: ${response.status} ${response.text}`);
      }
      selectResource(response.body);
      setConflict(null);
      await refresh().catch(error => {
        onMessage(`External program list refresh failed after save: ${String(error)}`);
      });
      onMessage(pendingDirectory
        ? `Imported ${pendingDirectory.files.length} files from ${pendingDirectory.directoryName}; resource ${response.body.resourceId} saved atomically.`
        : `External program resource saved ${response.body.resourceId}`);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      onMessage(message.startsWith('External program save failed:')
        ? message
        : `External program save failed: ${message}`);
      throw error;
    } finally {
      endOperation();
    }
  }, [beginOperation, draft, endOperation, isBackendHealthy, isCurrentEditorOperation, onMessage, pendingDirectory, refresh, reloadResource, scope, selectResource]);
  latestSaveRef.current = save;

  const selectProgramDirectory = useCallback(async () => {
    if (!scope || !isBackendHealthy) return;
    if (!beginOperation('Directory selection')) return;
    const startedScopeKey = scopeKey;
    const startedEditorIdentity = editorIdentity;
    try {
      const selection = requireExternalProgramDirectorySelection(
        await desktop.selectExternalProgramDirectory(
          scope.projectId,
          scope.applicationId,
          draft.resourceId));
      if (!selection) return;
      if (currentScopeKey.current !== startedScopeKey
          || currentEditorIdentity.current !== startedEditorIdentity) {
        await desktop.releaseExternalProgramDirectorySelection(selection.selectionId);
        throw new Error('The active Application or Program Resource changed while the directory was being selected. Select it again in the current editor.');
      }
      const entryPoint = chooseExternalProgramEntryPoint(selection, draft.entryPoint);
      editorGeneration.current++;
      setPendingDirectory(selection);
      setDraft(current => ({
        ...current,
        launchKind: 'ApplicationExecutable',
        entryPoint,
        providerKind: null,
        providerKey: null,
        dirty: true
      }));
      onMessage(
        `Validated ${selection.files.length} files (${formatBytes(selection.totalBytes)}) from ${selection.directoryName}. Choose the entry point, then Save.`);
    } catch (error) {
      onMessage(`Program directory validation failed: ${String(error)}`);
    } finally {
      endOperation();
    }
  }, [beginOperation, draft.entryPoint, draft.resourceId, editorIdentity, endOperation, isBackendHealthy, onMessage, scope, scopeKey]);

  const remove = useCallback(async () => {
    if (!scope || !draft.persisted || !window.confirm(`Delete ${draft.resourceId}?`)) return;
    if (!beginOperation('Delete')) return;
    const startedScopeKey = currentScopeKey.current;
    const startedEditorIdentity = currentEditorIdentity.current;
    try {
      const response = await deleteExternalProgramResource(
        draft.resourceId,
        scope,
        { revision: draft.revision });
      if (!isCurrentEditorOperation(startedScopeKey, startedEditorIdentity)) {
        return;
      }
      if (!response.ok) {
        onMessage(`External program delete failed: ${response.status} ${response.text}`);
        return;
      }
      await refresh();
      newResource();
      onMessage(`External program resource deleted ${draft.resourceId}`);
    } finally {
      endOperation();
    }
  }, [beginOperation, draft.persisted, draft.resourceId, draft.revision, endOperation, isCurrentEditorOperation, newResource, onMessage, refresh, scope]);

  const runTrial = useCallback(async () => {
    if (!scope || !draft.persisted) return;
    if (draft.dirty || pendingDirectory) {
      onMessage('Save the Program Resource before running a trial so the frozen program and protocol match the editor.');
      return;
    }
    if (!beginOperation('Protocol trial')) return;
    const startedScopeKey = currentScopeKey.current;
    const startedEditorIdentity = currentEditorIdentity.current;
    try {
      const response = await trialExternalProgramResource(draft.resourceId, {
        inputs: Object.fromEntries(draft.inputMappings.map(mapping => [mapping.target, {
          kind: trialKindsByTarget[mapping.target] ?? 'Text',
          canonicalValue: trialValues[mapping.target] ?? ''
        }]))
      }, scope);
      if (!isCurrentEditorOperation(startedScopeKey, startedEditorIdentity)) {
        return;
      }
      if (!response.ok || !response.body) {
        onMessage(`Protocol trial failed: ${response.status} ${response.text}`);
        return;
      }
      setTrialResult(response.body);
      onMessage(`Protocol trial ${response.body.executionStatus} / ${response.body.judgement}`);
    } finally {
      endOperation();
    }
  }, [beginOperation, draft.dirty, draft.inputMappings, draft.persisted, draft.resourceId, endOperation, isCurrentEditorOperation, onMessage, pendingDirectory, scope, trialKindsByTarget, trialValues]);

  const editorProblems = useMemo<EditorProblem[]>(() => {
    const problems: EditorProblem[] = [];
    if (!draft.resourceId.trim()) problems.push({ id: 'program-resource-id', severity: 'Error', message: 'Resource ID is required.', targetId: 'external-program-resource-id' });
    if (!draft.capabilityId.trim()) problems.push({ id: 'program-capability', severity: 'Error', message: 'A topology capability is required.', targetId: 'external-program-capability' });
    if (!draft.commandName.trim()) problems.push({ id: 'program-command', severity: 'Error', message: 'Command name is required.', targetId: 'external-program-command' });
    if (draft.launchKind === 'ApplicationExecutable') {
      if (!pendingDirectory && !draft.persisted) {
        problems.push({ id: 'program-directory', severity: 'Error', message: 'Select the complete external program directory.', targetId: 'select-external-program-directory' });
      }
      const availableResourcePaths = pendingDirectory
        ? pendingDirectory.files.map(file => file.resourceRelativePath)
        : draft.files.map(file => file.relativePath);
      if (!draft.entryPoint
          || !isSupportedExternalProgramEntryPoint(draft.entryPoint)
          || !availableResourcePaths.includes(draft.entryPoint)) {
        problems.push({ id: 'program-entry-point', severity: 'Error', message: 'Choose a Windows executable entry point from the imported directory.', targetId: 'external-program-entry-point' });
      }
    }
    draft.inputMappings.forEach((mapping, index) => {
      if (!mapping.source.startsWith('$production.')) return;
      const inputKey = mapping.source.slice('$production.'.length);
      if (!inputKey || !productionInputKeys.includes(inputKey)) {
        problems.push({
          id: `program-production-input-${index}`,
          severity: 'Error',
          message: `Production input '${inputKey || mapping.source}' is not declared by an Operation in this Application.`,
          targetId: `external-program-input-source-${index}`
        });
      }
    });
    return problems;
  }, [draft, pendingDirectory, productionInputKeys]);
  useEditorDocument({
    dirty: draft.dirty,
    editRevision: draft,
    busy: busy || resourceLoading,
    canSave: isBackendHealthy && !busy && !resourceLoading && editorProblems.length === 0,
    save: () => save(),
    revert: reloadResource,
    focus: targetId => {
      if (targetId) document.querySelector<HTMLElement>(`[data-testid="${targetId}"]`)?.focus();
    },
    problems: editorProblems,
    conflict
  });
  const draftTransitionGuard = useDraftTransitionGuard({
    dirty: draft.dirty,
    canSave: isBackendHealthy && !busy && !resourceLoading && editorProblems.length === 0,
    save: () => save(),
    onError: onMessage
  });
  const currentDraftLabel = draft.displayName.trim()
    || draft.resourceId.trim()
    || 'Untitled Program Resource';
  const previewFiles = useMemo(() => pendingDirectory
    ? pendingDirectory.files.map(file => ({
      relativePath: file.resourceRelativePath,
      sizeBytes: file.sizeBytes,
      sha256: file.sha256
    }))
    : draft.files, [draft.files, pendingDirectory]);
  const executableEntryPoints = useMemo(() => previewFiles
    .filter(file => isSupportedExternalProgramEntryPoint(file.relativePath)), [previewFiles]);

  if (!activeWorkspace || !application) {
    return <section className="external-program-workbench empty"><FileCode2 size={28} /><h2>Open an Application to manage its program resources.</h2></section>;
  }

  return (
    <section className="external-program-workbench" data-testid="external-program-workbench">
      <header className="external-program-command-bar">
        <div><FileCode2 size={20} /><span><strong>Program Resources</strong><small>{activeWorkspace.project.displayName} / {application.displayName}</small></span></div>
        <div>
          <button
            className="button ghost"
            type="button"
            onClick={() => draftTransitionGuard.request({
              id: 'external-program:refresh',
              title: 'Save changes before refreshing Program Resources?',
              detail: 'Refresh reloads the selected resource and its Application metadata from disk.',
              currentDocumentLabel: currentDraftLabel,
              targetLabel: 'the latest Program Resources',
              proceed: () => refresh()
            })}
            disabled={busy || resourceLoading || draftTransitionGuard.busy}
            data-testid="refresh-external-program-resources"
          ><RefreshCw size={14} /> Refresh</button>
          <button
            className="button"
            type="button"
            onClick={() => draftTransitionGuard.request({
              id: 'external-program:new',
              title: 'Save changes before creating a Program Resource?',
              detail: 'The selected Program Resource has unsaved changes. Save them, discard them, or cancel and keep editing.',
              currentDocumentLabel: currentDraftLabel,
              targetLabel: 'a new Program Resource',
              proceed: newResource
            })}
            disabled={busy || resourceLoading || draftTransitionGuard.busy}
            data-testid="new-external-program-resource"
          ><Plus size={14} /> New Resource</button>
          <button className="button primary" type="button" onClick={() => void save().catch(() => undefined)} disabled={busy || resourceLoading || !isBackendHealthy || editorProblems.length > 0} data-testid="save-external-program-resource"><Save size={14} /> Save</button>
        </div>
      </header>

      <div className="external-program-shell">
        <aside className="external-program-browser">
          <header><span>APPLICATION RESOURCES</span><small>{resources.length}</small></header>
          {resources.map(resource => (
            <button
              key={resource.resourceId}
              type="button"
              className={draft.persisted && draft.resourceId === resource.resourceId ? 'active' : ''}
              disabled={busy || draftTransitionGuard.busy}
              onClick={() => {
                if (draft.persisted && draft.resourceId === resource.resourceId) {
                  if (resourceLoadLease.current.cancel()) {
                    editorGeneration.current++;
                    setResourceLoading(false);
                  }
                  return;
                }
                draftTransitionGuard.request({
                  id: `external-program:open:${resource.resourceId}`,
                  title: 'Save changes before opening another Program Resource?',
                  detail: 'Opening another Program Resource replaces the current editor draft.',
                  currentDocumentLabel: currentDraftLabel,
                  targetLabel: resource.displayName || resource.resourceId,
                  proceed: () => loadResource(resource.resourceId)
                });
              }}
              data-testid={`external-program-resource-${resource.resourceId}`}
            >
              <FileCode2 size={15} /><span><strong>{resource.displayName}</strong><small>{resource.resourceId} · {resource.launchKind}</small></span>
            </button>
          ))}
          {resources.length === 0 ? <p>No resources in this Application.</p> : null}
        </aside>

        <fieldset className="external-program-editor" disabled={busy || resourceLoading} aria-busy={busy || resourceLoading}>
          <section className="external-program-card identity">
            <header><span><small>APPLICATION-PORTABLE RESOURCE</small><strong>{draft.displayName}</strong></span>{draft.persisted ? <code>{draft.contentSha256.slice(0, 12)}</code> : <em>Not saved</em>}</header>
            <div className="external-program-grid">
              <Field label="Resource ID"><input value={draft.resourceId} disabled={draft.persisted || pendingDirectory !== null || busy} onChange={event => updateDraft(current => ({ ...current, resourceId: event.target.value }))} data-testid="external-program-resource-id" /></Field>
              <Field label="Display Name"><input value={draft.displayName} onChange={event => updateDraft(current => ({ ...current, displayName: event.target.value }))} data-testid="external-program-display-name" /></Field>
              <Field label="Capability">
                <select value={draft.capabilityId} onChange={event => setCapability(event.target.value, topology, updateDraft)} data-testid="external-program-capability">
                  <option value="">Select capability</option>
                  {(topology?.capabilities ?? []).map(capability => <option key={capability.capabilityId} value={capability.capabilityId}>{capability.capabilityId} · {capability.commandName}</option>)}
                </select>
              </Field>
              <Field label="Command"><input value={draft.commandName} onChange={event => updateDraft(current => ({ ...current, commandName: event.target.value }))} data-testid="external-program-command" /></Field>
              <Field label="Launch Boundary">
                <select value={draft.launchKind} onChange={event => {
                  if (event.target.value === 'Provider') setPendingDirectory(null);
                  changeLaunchKind(event.target.value, topology, updateDraft);
                }} data-testid="external-program-launch-kind">
                  <option value="Provider">Provider plugin</option>
                  <option value="ApplicationExecutable">Application executable</option>
                </select>
              </Field>
              {draft.launchKind === 'Provider' ? <>
                <Field label="Provider Kind"><input value={draft.providerKind ?? ''} onChange={event => updateDraft(current => ({ ...current, providerKind: event.target.value }))} /></Field>
                <Field label="Provider Key"><input value={draft.providerKey ?? ''} onChange={event => updateDraft(current => ({ ...current, providerKey: event.target.value }))} /></Field>
              </> : <Field label="Entry Point">
                <select
                  value={draft.entryPoint ?? ''}
                  onChange={event => updateDraft(current => ({ ...current, entryPoint: event.target.value }))}
                  disabled={executableEntryPoints.length === 0}
                  data-testid="external-program-entry-point"
                >
                  <option value="">Choose entry point</option>
                  {executableEntryPoints.map(file => <option key={file.relativePath} value={file.relativePath}>{file.relativePath}</option>)}
                </select>
              </Field>}
            </div>
            {pendingDirectory ? <div className="external-program-directory-stage" data-testid="external-program-directory-stage">
              <FolderTree size={18} />
              <span><strong>{pendingDirectory.directoryName}</strong><small>{pendingDirectory.files.length} / {externalProgramDirectoryImportLimits.maximumFileCount} files · {formatBytes(pendingDirectory.totalBytes)} / {formatBytes(externalProgramDirectoryImportLimits.maximumTotalBytes)}</small></span>
              <em>Validated locally. Save atomically replaces the complete frozen file set.</em>
            </div> : null}
            <div className="external-program-actions">
              <button type="button" className="button" onClick={() => void selectProgramDirectory()} disabled={busy} data-testid="select-external-program-directory"><Upload size={14} /> {draft.files.length > 0 || pendingDirectory ? 'Replace Program Directory' : 'Select Program Directory'}</button>
              <button type="button" className="button danger" onClick={() => void remove()} disabled={busy || !draft.persisted} data-testid="delete-external-program-resource"><Trash2 size={14} /> Delete</button>
            </div>
          </section>

          <section className="external-program-card">
            <header><span><small>PROTOCOL</small><strong>Typed inputs and results</strong></span></header>
            <MappingRows draft={draft} productionInputKeys={productionInputKeys} onChange={updateDraft} />
          </section>

          <section className="external-program-card permission" data-testid="external-program-permission-profile">
            <header><ShieldCheck size={18} /><span><small>SECURITY BOUNDARY</small><strong>Restricted process policy</strong></span></header>
            <div className="external-program-grid">
              <Field label="Profile"><input value="Restricted" readOnly /></Field>
              <Field label="Network capability">
                <select
                  value={draft.permissionProfile.networkAccessAllowed ? 'internetClient' : 'none'}
                  onChange={event => updateDraft(current => ({
                    ...current,
                    permissionProfile: {
                      ...current.permissionProfile,
                      networkAccessAllowed: event.target.value === 'internetClient'
                    }
                  }))}
                  data-testid="external-program-network-capability"
                >
                  <option value="none">Denied</option>
                  <option value="internetClient">Allow outbound internet (high risk)</option>
                </select>
              </Field>
              <Field label="Allowed environment variables"><textarea value={draft.permissionProfile.allowedEnvironmentVariables.join('\n')} onChange={event => updateDraft(current => ({ ...current, permissionProfile: { ...current.permissionProfile, allowedEnvironmentVariables: canonicalLines(event.target.value) } }))} /></Field>
              <Field label="Argument templates"><textarea value={draft.argumentTemplates.join('\n')} onChange={event => updateDraft(current => ({ ...current, argumentTemplates: canonicalLines(event.target.value) }))} /></Field>
              <Limit label="Timeout (ms)" value={draft.executionLimits.timeoutMilliseconds} onChange={value => updateLimit('timeoutMilliseconds', value, updateDraft)} />
              <Limit label="Process count" value={draft.executionLimits.maximumProcessCount} onChange={value => updateLimit('maximumProcessCount', value, updateDraft)} />
              <Limit label="Working set bytes" value={draft.executionLimits.maximumWorkingSetBytes} onChange={value => updateLimit('maximumWorkingSetBytes', value, updateDraft)} />
              <Limit label="CPU time (ms)" value={draft.executionLimits.maximumCpuTimeMilliseconds} onChange={value => updateLimit('maximumCpuTimeMilliseconds', value, updateDraft)} />
            </div>
            {draft.permissionProfile.networkAccessAllowed ? <p className="external-program-security-warning" role="alert">High risk: this program receives only the Windows internetClient capability. Local/private network access remains denied.</p> : null}
          </section>

          <section className="external-program-card evidence" data-testid="external-program-hash-preview">
            <header><Hash size={18} /><span><small>FROZEN CONTENT PREVIEW</small><strong>{draft.dirty ? 'Pending changes — save to update hash' : draft.contentSha256 || 'Hash generated after save'}</strong></span></header>
            <div className="external-program-files">
              {previewFiles.map(file => <div key={file.relativePath} data-testid={`external-program-file-${file.relativePath}`}><code>{file.relativePath}</code><span>{formatBytes(file.sizeBytes)}</span><code>{file.sha256}</code></div>)}
              {previewFiles.length === 0 ? <p>No imported files.</p> : null}
            </div>
          </section>

          <section className="external-program-card trial">
            <header><FlaskConical size={18} /><span><small>PROTOCOL TRIAL</small><strong>Run through the production host boundary</strong></span></header>
            <div className="external-program-trial-inputs">
              {draft.inputMappings.map(mapping => <div key={mapping.target}>
                <code>{mapping.target}</code>
                <select value={trialKindsByTarget[mapping.target] ?? 'Text'} disabled={busy} onChange={event => {
                  setTrialResult(null);
                  setTrialKindsByTarget(current => ({ ...current, [mapping.target]: event.target.value as ExternalProgramTrialInputKind }));
                }}>{trialKinds.map(kind => <option key={kind}>{kind}</option>)}</select>
                <input value={trialValues[mapping.target] ?? ''} disabled={busy} onChange={event => {
                  setTrialResult(null);
                  setTrialValues(current => ({ ...current, [mapping.target]: event.target.value }));
                }} />
              </div>)}
            </div>
            {draft.dirty || pendingDirectory ? <p data-testid="external-program-trial-save-required">Save changes before running a trial. Trials always execute the frozen Program Resource.</p> : null}
            <button type="button" className="button primary" onClick={() => void runTrial()} disabled={busy || !draft.persisted || draft.dirty || pendingDirectory !== null} data-testid="trial-external-program-resource"><FlaskConical size={14} /> Run Trial</button>
            {trialResult ? <div className={`external-program-trial-result ${trialResult.executionStatus.toLowerCase()}`}><strong>{trialResult.executionStatus} / {trialResult.judgement}</strong><code>{trialResult.failureReason ?? trialResult.resultPayload ?? 'No payload'}</code><small>{trialResult.artifacts.length} artifacts</small></div> : null}
          </section>
        </fieldset>
      </div>
      <DraftTransitionDialog controller={draftTransitionGuard} testIdPrefix="external-program-draft-transition" />
    </section>
  );
}

function MappingRows({ draft, productionInputKeys, onChange }: { draft: ResourceDraft; productionInputKeys: string[]; onChange: React.Dispatch<React.SetStateAction<ResourceDraft>> }): React.ReactElement {
  return <div className="external-program-mappings">
    <header><span>Inputs</span><button type="button" onClick={() => onChange(current => ({ ...current, inputMappings: [...current.inputMappings, { source: productionInputKeys.length > 0 ? `$production.${productionInputKeys[0]}` : '$product.identity', target: `input${current.inputMappings.length + 1}` }] }))}><Plus size={12} /> Add</button></header>
    <p>Operation inputs are available as <code>$production.&lt;targetInputKey&gt;</code>. The release validator requires the selected key on every referencing Operation.</p>
    {draft.inputMappings.map((mapping, index) => <div key={index}><input list={`external-program-input-sources-${index}`} value={mapping.source} data-testid={`external-program-input-source-${index}`} onChange={event => onChange(current => ({ ...current, inputMappings: replaceAt(current.inputMappings, index, { ...mapping, source: event.target.value }) }))} /><datalist id={`external-program-input-sources-${index}`}><option value="$product.identity" /><option value="$product.model" />{productionInputKeys.map(inputKey => <option key={inputKey} value={`$production.${inputKey}`} />)}</datalist><ArrowRight size={13} /><input value={mapping.target} onChange={event => onChange(current => ({ ...current, inputMappings: replaceAt(current.inputMappings, index, { ...mapping, target: event.target.value }) }))} /><button type="button" onClick={() => onChange(current => ({ ...current, inputMappings: current.inputMappings.filter((_, candidate) => candidate !== index) }))}><Trash2 size={12} /></button></div>)}
    <header><span>Typed results</span><button type="button" onClick={() => onChange(current => ({ ...current, resultMappings: [...current.resultMappings, { sourcePath: '$.value', targetKey: `result.${current.resultMappings.length + 1}`, valueKind: 'Text' }] }))}><Plus size={12} /> Add</button></header>
    {draft.resultMappings.map((mapping, index) => <div key={index}><input value={mapping.sourcePath} onChange={event => onChange(current => ({ ...current, resultMappings: replaceAt(current.resultMappings, index, { ...mapping, sourcePath: event.target.value }) }))} /><ArrowRight size={13} /><input value={mapping.targetKey} onChange={event => onChange(current => ({ ...current, resultMappings: replaceAt(current.resultMappings, index, { ...mapping, targetKey: event.target.value }) }))} /><select value={mapping.valueKind} onChange={event => onChange(current => ({ ...current, resultMappings: replaceAt(current.resultMappings, index, { ...mapping, valueKind: event.target.value as ProductionContextValueKind }) }))}>{valueKinds.map(kind => <option key={kind}>{kind}</option>)}</select><button type="button" onClick={() => onChange(current => ({ ...current, resultMappings: current.resultMappings.filter((_, candidate) => candidate !== index) }))}><Trash2 size={12} /></button></div>)}
    <div className="external-program-outcome">
      <Field label="Judgement path"><input value={draft.outcomeMapping.sourcePath} onChange={event => onChange(current => ({ ...current, outcomeMapping: { ...current.outcomeMapping, sourcePath: event.target.value } }))} /></Field>
      <Field label="Passed token"><input value={draft.outcomeMapping.passedToken} onChange={event => onChange(current => ({ ...current, outcomeMapping: { ...current.outcomeMapping, passedToken: event.target.value } }))} /></Field>
      <Field label="Failed token"><input value={draft.outcomeMapping.failedToken} onChange={event => onChange(current => ({ ...current, outcomeMapping: { ...current.outcomeMapping, failedToken: event.target.value } }))} /></Field>
      <Field label="Aborted token"><input value={draft.outcomeMapping.abortedToken} onChange={event => onChange(current => ({ ...current, outcomeMapping: { ...current.outcomeMapping, abortedToken: event.target.value } }))} /></Field>
    </div>
  </div>;
}

function Field({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return <label><span>{label}</span>{children}</label>;
}

function Limit({ label, value, onChange }: { label: string; value: number; onChange(value: number): void }): React.ReactElement {
  return <Field label={label}><input type="number" min={1} step={1} value={value} onChange={event => onChange(Number(event.target.value))} /></Field>;
}

function createDraft(topology: AutomationTopologyResponse | null, dirty = true): ResourceDraft {
  const capability = topology?.capabilities[0];
  const binding = topology?.driverBindings.find(item => item.capabilityId === capability?.capabilityId);
  return {
    persisted: false,
    dirty,
    files: [],
    contentSha256: '',
    revision: '',
    resourceId: `external-program-${Date.now().toString(36)}`,
    displayName: 'External Program',
    capabilityId: capability?.capabilityId ?? '',
    commandName: capability?.commandName ?? '',
    launchKind: 'Provider',
    entryPoint: null,
    providerKind: binding?.providerKind ?? '',
    providerKey: binding?.providerKey ?? '',
    argumentTemplates: ['--serial={{product.identity}}', '--model={{product.model}}'],
    inputMappings: [{ source: '$product.identity', target: 'serialNumber' }, { source: '$product.model', target: 'modelCode' }],
    resultMappings: [{ sourcePath: '$.value', targetKey: 'vendor.value', valueKind: 'Text' }],
    outcomeMapping: { sourcePath: '$.judgement', passedToken: 'Passed', failedToken: 'Failed', abortedToken: 'Aborted' },
    permissionProfile: { profileName: 'Restricted', networkAccessAllowed: false, allowedEnvironmentVariables: [] },
    executionLimits: {
      timeoutMilliseconds: 30_000,
      maximumProcessCount: 4,
      maximumWorkingSetBytes: 536_870_912,
      maximumCpuTimeMilliseconds: 30_000,
      maximumStandardOutputBytes: 4_194_304,
      maximumStandardErrorBytes: 4_194_304,
      maximumArtifactCount: 64,
      maximumArtifactBytes: 67_108_864,
      maximumTotalArtifactBytes: 268_435_456
    }
  };
}

function fromResponse(resource: ExternalProgramResourceResponse): ResourceDraft {
  return { ...resource, persisted: true, dirty: false, argumentTemplates: [...resource.argumentTemplates], inputMappings: resource.inputMappings.map(item => ({ ...item })), resultMappings: resource.resultMappings.map(item => ({ ...item })), outcomeMapping: { ...resource.outcomeMapping }, permissionProfile: { ...resource.permissionProfile, allowedEnvironmentVariables: [...resource.permissionProfile.allowedEnvironmentVariables] }, executionLimits: { ...resource.executionLimits }, files: resource.files.map(item => ({ ...item })) };
}

function toRequest(draft: ResourceDraft): SaveExternalProgramResourceRequest {
  return {
    resourceId: draft.resourceId,
    displayName: draft.displayName,
    capabilityId: draft.capabilityId,
    commandName: draft.commandName,
    launchKind: draft.launchKind,
    entryPoint: draft.entryPoint,
    providerKind: draft.providerKind,
    providerKey: draft.providerKey,
    argumentTemplates: [...draft.argumentTemplates],
    inputMappings: draft.inputMappings.map(item => ({ ...item })),
    resultMappings: draft.resultMappings.map(item => ({ ...item })),
    outcomeMapping: { ...draft.outcomeMapping },
    permissionProfile: {
      ...draft.permissionProfile,
      allowedEnvironmentVariables: [...draft.permissionProfile.allowedEnvironmentVariables]
    },
    executionLimits: { ...draft.executionLimits }
  };
}

function setCapability(capabilityId: string, topology: AutomationTopologyResponse | null, setDraft: React.Dispatch<React.SetStateAction<ResourceDraft>>): void {
  const capability = topology?.capabilities.find(item => item.capabilityId === capabilityId);
  const binding = topology?.driverBindings.find(item => item.capabilityId === capabilityId);
  setDraft(current => ({ ...current, capabilityId, commandName: capability?.commandName ?? '', providerKind: current.launchKind === 'Provider' ? binding?.providerKind ?? '' : null, providerKey: current.launchKind === 'Provider' ? binding?.providerKey ?? '' : null }));
}

function changeLaunchKind(value: string, topology: AutomationTopologyResponse | null, setDraft: React.Dispatch<React.SetStateAction<ResourceDraft>>): void {
  if (value !== 'Provider' && value !== 'ApplicationExecutable') return;
  setDraft(current => {
    const binding = topology?.driverBindings.find(item => item.capabilityId === current.capabilityId);
    return { ...current, launchKind: value, entryPoint: value === 'ApplicationExecutable' ? current.entryPoint : null, providerKind: value === 'Provider' ? binding?.providerKind ?? '' : null, providerKey: value === 'Provider' ? binding?.providerKey ?? '' : null };
  });
}

function updateLimit(key: keyof ResourceDraft['executionLimits'], value: number, setDraft: React.Dispatch<React.SetStateAction<ResourceDraft>>): void {
  setDraft(current => ({ ...current, executionLimits: { ...current.executionLimits, [key]: value } }));
}

function canonicalLines(value: string): string[] {
  return value.split(/\r?\n/).map(item => item.trim()).filter(Boolean);
}

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, candidate) => candidate === index ? value : item);
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`;
  if (value >= 1024 * 1024 * 1024) return `${(value / 1024 / 1024 / 1024).toFixed(1)} GiB`;
  return `${(value / 1024 / 1024).toFixed(1)} MiB`;
}
