import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ArrowRight,
  FileCode2,
  FilePlus2,
  FlaskConical,
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
import {
  deleteExternalProgramResource,
  getAutomationTopology,
  importExternalProgramResource,
  importExternalProgramResourceFile,
  listExternalProgramResources,
  saveExternalProgramResource,
  trialExternalProgramResource,
  type ProjectApplicationApiScope
} from './api';
import { desktop } from './desktop-bridge';
import { useEditorDocument } from './editor-workspace';
import {
  readEditorConflictRevision,
  type EditorDocumentConflict,
  type EditorProblem
} from './editor-workspace-model';

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
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [resources, setResources] = useState<ExternalProgramResourceResponse[]>([]);
  const [draft, setDraft] = useState<ResourceDraft>(() => createDraft(null));
  const [busy, setBusy] = useState(false);
  const [trialKindsByTarget, setTrialKindsByTarget] = useState<Record<string, ExternalProgramTrialInputKind>>({});
  const [trialValues, setTrialValues] = useState<Record<string, string>>({});
  const [trialResult, setTrialResult] = useState<ExternalProgramTrialResponse | null>(null);
  const [conflict, setConflict] = useState<EditorDocumentConflict | null>(null);

  const selectResource = useCallback((resource: ExternalProgramResourceResponse) => {
    setDraft(fromResponse(resource));
    setTrialKindsByTarget(Object.fromEntries(resource.inputMappings.map(item => [item.target, 'Text'])));
    setTrialValues(Object.fromEntries(resource.inputMappings.map(item => [item.target, 'value'])));
    setTrialResult(null);
    setConflict(null);
  }, []);

  const refresh = useCallback(async () => {
    if (!scope || !isBackendHealthy) {
      setResources([]);
      setTopology(null);
      return;
    }
    const [nextResources, topologyResponse] = await Promise.all([
      listExternalProgramResources(scope),
      application?.topologyId
        ? getAutomationTopology(application.topologyId, scope)
        : Promise.resolve(null)
    ]);
    const nextTopology = topologyResponse?.ok && topologyResponse.body ? topologyResponse.body : null;
    setResources(nextResources);
    setTopology(nextTopology);
    setDraft(current => {
      const currentResource = nextResources.find(item => item.resourceId === current.resourceId);
      return currentResource ? fromResponse(currentResource) : createDraft(nextTopology);
    });
  }, [application?.topologyId, isBackendHealthy, scope]);

  useEffect(() => {
    refresh().catch(error => onMessage(`External program resources refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

  const newResource = useCallback(() => {
    setDraft(createDraft(topology));
    setTrialKindsByTarget({ serialNumber: 'Text', modelCode: 'Text' });
    setTrialValues({ serialNumber: 'sample-001', modelCode: 'MODEL-A' });
    setTrialResult(null);
  }, [topology]);

  const save = useCallback(async (force = false) => {
    if (!scope || !isBackendHealthy) throw new Error('Backend is required to save the program resource.');
    if (draft.launchKind === 'ApplicationExecutable' && !draft.persisted) {
      onMessage('Import the executable to create an Application executable resource.');
      throw new Error('Import the executable before saving this resource.');
    }
    setBusy(true);
    try {
      const response = await saveExternalProgramResource(
        draft.resourceId,
        toRequest(draft),
        scope,
        draft.persisted ? { revision: draft.revision, force } : undefined);
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
                await save(true);
                setConflict(null);
              }
            });
          }
        }
        onMessage(`External program save failed: ${response.status} ${response.text}`);
        throw new Error(`External program save failed: ${response.status}`);
      }
      selectResource(response.body);
      setConflict(null);
      await refresh();
      onMessage(`External program resource saved ${response.body.resourceId}`);
    } catch (error) {
      onMessage(`External program save failed: ${String(error)}`);
      throw error;
    } finally {
      setBusy(false);
    }
  }, [draft, isBackendHealthy, onMessage, refresh, scope, selectResource]);

  const reloadResource = useCallback(async () => {
    if (!scope || !draft.persisted) {
      newResource();
      return;
    }
    const nextResources = await listExternalProgramResources(scope);
    const current = nextResources.find(item => item.resourceId === draft.resourceId);
    if (!current) {
      throw new Error(`External program ${draft.resourceId} no longer exists.`);
    }
    setResources(nextResources);
    selectResource(current);
  }, [draft.persisted, draft.resourceId, newResource, scope, selectResource]);

  const importEntryPoint = useCallback(async () => {
    if (!scope || !isBackendHealthy) return;
    const selection = await desktop.selectExternalProgramFiles({
      title: 'Import external program entry point',
      buttonLabel: 'Import Program'
    });
    const sourcePath = selection.paths[0];
    if (selection.canceled || !sourcePath) return;
    const fileName = portableFileName(sourcePath);
    if (!fileName) {
      onMessage('Program file name must use letters, digits, dot, dash, or underscore.');
      return;
    }
    const resourceRelativePath = `files/${fileName}`;
    const nextDraft: ResourceDraft = {
      ...draft,
      launchKind: 'ApplicationExecutable',
      entryPoint: resourceRelativePath,
      providerKind: null,
      providerKey: null
    };
    setBusy(true);
    try {
      const imported = draft.persisted
        ? await importExternalProgramResourceFile(
          draft.resourceId,
          { sourcePath, resourceRelativePath },
          scope,
          { revision: draft.revision })
        : await importExternalProgramResource(
          toRequest(nextDraft),
          [{ sourcePath, resourceRelativePath }],
          scope);
      if (!imported.ok || !imported.body) {
        onMessage(`Program import failed: ${imported.status} ${imported.text}`);
        return;
      }
      const completed = draft.persisted
        ? await saveExternalProgramResource(
          draft.resourceId,
          toRequest(nextDraft),
          scope,
          { revision: imported.body.revision })
        : imported;
      if (!completed.ok || !completed.body) {
        onMessage(`Program metadata save failed: ${completed.status} ${completed.text}`);
        return;
      }
      selectResource(completed.body);
      await refresh();
      onMessage(`Imported ${fileName}; SHA-256 ${completed.body.contentSha256.slice(0, 12)}`);
    } catch (error) {
      onMessage(`Program import failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [draft, isBackendHealthy, onMessage, refresh, scope, selectResource]);

  const importSupportFile = useCallback(async () => {
    if (!scope || !draft.persisted) return;
    const selection = await desktop.selectExternalProgramFiles({
      title: 'Import external program support file',
      buttonLabel: 'Import File'
    });
    const sourcePath = selection.paths[0];
    if (selection.canceled || !sourcePath) return;
    const fileName = portableFileName(sourcePath);
    if (!fileName) {
      onMessage('Support file name must use letters, digits, dot, dash, or underscore.');
      return;
    }
    setBusy(true);
    try {
      const response = await importExternalProgramResourceFile(
        draft.resourceId,
        { sourcePath, resourceRelativePath: `files/${fileName}` },
        scope,
        { revision: draft.revision });
      if (!response.ok || !response.body) {
        onMessage(`Support file import failed: ${response.status} ${response.text}`);
        return;
      }
      selectResource(response.body);
      await refresh();
      onMessage(`Imported support file ${fileName}`);
    } finally {
      setBusy(false);
    }
  }, [draft.persisted, draft.resourceId, draft.revision, onMessage, refresh, scope, selectResource]);

  const remove = useCallback(async () => {
    if (!scope || !draft.persisted || !window.confirm(`Delete ${draft.resourceId}?`)) return;
    setBusy(true);
    try {
      const response = await deleteExternalProgramResource(
        draft.resourceId,
        scope,
        { revision: draft.revision });
      if (!response.ok) {
        onMessage(`External program delete failed: ${response.status} ${response.text}`);
        return;
      }
      await refresh();
      newResource();
      onMessage(`External program resource deleted ${draft.resourceId}`);
    } finally {
      setBusy(false);
    }
  }, [draft.persisted, draft.resourceId, draft.revision, newResource, onMessage, refresh, scope]);

  const runTrial = useCallback(async () => {
    if (!scope || !draft.persisted) return;
    setBusy(true);
    try {
      const response = await trialExternalProgramResource(draft.resourceId, {
        inputs: Object.fromEntries(draft.inputMappings.map(mapping => [mapping.target, {
          kind: trialKindsByTarget[mapping.target] ?? 'Text',
          canonicalValue: trialValues[mapping.target] ?? ''
        }]))
      }, scope);
      if (!response.ok || !response.body) {
        onMessage(`Protocol trial failed: ${response.status} ${response.text}`);
        return;
      }
      setTrialResult(response.body);
      onMessage(`Protocol trial ${response.body.executionStatus} / ${response.body.judgement}`);
    } finally {
      setBusy(false);
    }
  }, [draft.inputMappings, draft.persisted, draft.resourceId, onMessage, scope, trialKindsByTarget, trialValues]);

  const editorProblems = useMemo<EditorProblem[]>(() => {
    const problems: EditorProblem[] = [];
    if (!draft.resourceId.trim()) problems.push({ id: 'program-resource-id', severity: 'Error', message: 'Resource ID is required.', targetId: 'external-program-resource-id' });
    if (!draft.capabilityId.trim()) problems.push({ id: 'program-capability', severity: 'Error', message: 'A topology capability is required.', targetId: 'external-program-capability' });
    if (!draft.commandName.trim()) problems.push({ id: 'program-command', severity: 'Error', message: 'Command name is required.', targetId: 'external-program-command' });
    return problems;
  }, [draft.capabilityId, draft.commandName, draft.resourceId]);
  useEditorDocument({
    dirty: draft.dirty,
    canSave: isBackendHealthy && editorProblems.length === 0,
    save: () => save(),
    revert: reloadResource,
    focus: targetId => {
      if (targetId) document.querySelector<HTMLElement>(`[data-testid="${targetId}"]`)?.focus();
    },
    problems: editorProblems,
    conflict
  });

  if (!activeWorkspace || !application) {
    return <section className="external-program-workbench empty"><FileCode2 size={28} /><h2>Open an Application to manage its program resources.</h2></section>;
  }

  return (
    <section
      className="external-program-workbench"
      data-testid="external-program-workbench"
      onInputCapture={() => setDraft(current => ({ ...current, dirty: true }))}
    >
      <header className="external-program-command-bar">
        <div><FileCode2 size={20} /><span><strong>Program Resources</strong><small>{activeWorkspace.project.displayName} / {application.displayName}</small></span></div>
        <div>
          <button className="button ghost" type="button" onClick={() => void refresh()} disabled={busy}><RefreshCw size={14} /> Refresh</button>
          <button className="button" type="button" onClick={newResource} disabled={busy} data-testid="new-external-program-resource"><Plus size={14} /> New Resource</button>
          <button className="button primary" type="button" onClick={() => void save().catch(() => undefined)} disabled={busy || !isBackendHealthy} data-testid="save-external-program-resource"><Save size={14} /> Save</button>
        </div>
      </header>

      <div className="external-program-shell">
        <aside className="external-program-browser">
          <header><span>APPLICATION RESOURCES</span><small>{resources.length}</small></header>
          {resources.map(resource => (
            <button key={resource.resourceId} type="button" className={draft.persisted && draft.resourceId === resource.resourceId ? 'active' : ''} onClick={() => selectResource(resource)}>
              <FileCode2 size={15} /><span><strong>{resource.displayName}</strong><small>{resource.resourceId} · {resource.launchKind}</small></span>
            </button>
          ))}
          {resources.length === 0 ? <p>No resources in this Application.</p> : null}
        </aside>

        <main className="external-program-editor">
          <section className="external-program-card identity">
            <header><span><small>APPLICATION-PORTABLE RESOURCE</small><strong>{draft.displayName}</strong></span>{draft.persisted ? <code>{draft.contentSha256.slice(0, 12)}</code> : <em>Not saved</em>}</header>
            <div className="external-program-grid">
              <Field label="Resource ID"><input value={draft.resourceId} disabled={draft.persisted} onChange={event => setDraft(current => ({ ...current, resourceId: event.target.value }))} data-testid="external-program-resource-id" /></Field>
              <Field label="Display Name"><input value={draft.displayName} onChange={event => setDraft(current => ({ ...current, displayName: event.target.value }))} /></Field>
              <Field label="Capability">
                <select value={draft.capabilityId} onChange={event => setCapability(event.target.value, topology, setDraft)}>
                  <option value="">Select capability</option>
                  {(topology?.capabilities ?? []).map(capability => <option key={capability.capabilityId} value={capability.capabilityId}>{capability.capabilityId} · {capability.commandName}</option>)}
                </select>
              </Field>
              <Field label="Command"><input value={draft.commandName} onChange={event => setDraft(current => ({ ...current, commandName: event.target.value }))} /></Field>
              <Field label="Launch Boundary">
                <select value={draft.launchKind} onChange={event => changeLaunchKind(event.target.value, topology, setDraft)}>
                  <option value="Provider">Provider plugin</option>
                  <option value="ApplicationExecutable">Application executable</option>
                </select>
              </Field>
              {draft.launchKind === 'Provider' ? <>
                <Field label="Provider Kind"><input value={draft.providerKind ?? ''} onChange={event => setDraft(current => ({ ...current, providerKind: event.target.value }))} /></Field>
                <Field label="Provider Key"><input value={draft.providerKey ?? ''} onChange={event => setDraft(current => ({ ...current, providerKey: event.target.value }))} /></Field>
              </> : <Field label="Frozen Entry Point"><input value={draft.entryPoint ?? ''} readOnly /></Field>}
            </div>
            <div className="external-program-actions">
              <button type="button" className="button" onClick={() => void importEntryPoint()} disabled={busy} data-testid="import-external-program-resource"><Upload size={14} /> {draft.entryPoint ? 'Replace Entry Point' : 'Import Entry Point'}</button>
              <button type="button" className="button ghost" onClick={() => void importSupportFile()} disabled={busy || !draft.persisted}><FilePlus2 size={14} /> Add Support File</button>
              <button type="button" className="button danger" onClick={() => void remove()} disabled={busy || !draft.persisted} data-testid="delete-external-program-resource"><Trash2 size={14} /> Delete</button>
            </div>
          </section>

          <section className="external-program-card">
            <header><span><small>PROTOCOL</small><strong>Typed inputs and results</strong></span></header>
            <MappingRows draft={draft} onChange={setDraft} />
          </section>

          <section className="external-program-card permission" data-testid="external-program-permission-profile">
            <header><ShieldCheck size={18} /><span><small>SECURITY BOUNDARY</small><strong>Restricted process policy</strong></span></header>
            <div className="external-program-grid">
              <Field label="Profile"><input value="Restricted" readOnly /></Field>
              <Field label="Network capability">
                <select
                  value={draft.permissionProfile.networkAccessAllowed ? 'internetClient' : 'none'}
                  onChange={event => setDraft(current => ({
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
              <Field label="Allowed environment variables"><textarea value={draft.permissionProfile.allowedEnvironmentVariables.join('\n')} onChange={event => setDraft(current => ({ ...current, permissionProfile: { ...current.permissionProfile, allowedEnvironmentVariables: canonicalLines(event.target.value) } }))} /></Field>
              <Field label="Argument templates"><textarea value={draft.argumentTemplates.join('\n')} onChange={event => setDraft(current => ({ ...current, argumentTemplates: canonicalLines(event.target.value) }))} /></Field>
              <Limit label="Timeout (ms)" value={draft.executionLimits.timeoutMilliseconds} onChange={value => updateLimit('timeoutMilliseconds', value, setDraft)} />
              <Limit label="Process count" value={draft.executionLimits.maximumProcessCount} onChange={value => updateLimit('maximumProcessCount', value, setDraft)} />
              <Limit label="Working set bytes" value={draft.executionLimits.maximumWorkingSetBytes} onChange={value => updateLimit('maximumWorkingSetBytes', value, setDraft)} />
              <Limit label="CPU time (ms)" value={draft.executionLimits.maximumCpuTimeMilliseconds} onChange={value => updateLimit('maximumCpuTimeMilliseconds', value, setDraft)} />
            </div>
            {draft.permissionProfile.networkAccessAllowed ? <p className="external-program-security-warning" role="alert">High risk: this program receives only the Windows internetClient capability. Local/private network access remains denied.</p> : null}
          </section>

          <section className="external-program-card evidence" data-testid="external-program-hash-preview">
            <header><Hash size={18} /><span><small>FROZEN CONTENT PREVIEW</small><strong>{draft.contentSha256 || 'Hash generated after save'}</strong></span></header>
            <div className="external-program-files">
              {draft.files.map(file => <div key={file.relativePath}><code>{file.relativePath}</code><span>{formatBytes(file.sizeBytes)}</span><code>{file.sha256}</code></div>)}
              {draft.files.length === 0 ? <p>No imported files.</p> : null}
            </div>
          </section>

          <section className="external-program-card trial">
            <header><FlaskConical size={18} /><span><small>PROTOCOL TRIAL</small><strong>Run through the production host boundary</strong></span></header>
            <div className="external-program-trial-inputs">
              {draft.inputMappings.map(mapping => <div key={mapping.target}>
                <code>{mapping.target}</code>
                <select value={trialKindsByTarget[mapping.target] ?? 'Text'} onChange={event => setTrialKindsByTarget(current => ({ ...current, [mapping.target]: event.target.value as ExternalProgramTrialInputKind }))}>{trialKinds.map(kind => <option key={kind}>{kind}</option>)}</select>
                <input value={trialValues[mapping.target] ?? ''} onChange={event => setTrialValues(current => ({ ...current, [mapping.target]: event.target.value }))} />
              </div>)}
            </div>
            <button type="button" className="button primary" onClick={() => void runTrial()} disabled={busy || !draft.persisted} data-testid="trial-external-program-resource"><FlaskConical size={14} /> Run Trial</button>
            {trialResult ? <div className={`external-program-trial-result ${trialResult.executionStatus.toLowerCase()}`}><strong>{trialResult.executionStatus} / {trialResult.judgement}</strong><code>{trialResult.failureReason ?? trialResult.resultPayload ?? 'No payload'}</code><small>{trialResult.artifacts.length} artifacts</small></div> : null}
          </section>
        </main>
      </div>
    </section>
  );
}

function MappingRows({ draft, onChange }: { draft: ResourceDraft; onChange: React.Dispatch<React.SetStateAction<ResourceDraft>> }): React.ReactElement {
  return <div className="external-program-mappings">
    <header><span>Inputs</span><button type="button" onClick={() => onChange(current => ({ ...current, inputMappings: [...current.inputMappings, { source: '$product.identity', target: `input${current.inputMappings.length + 1}` }] }))}><Plus size={12} /> Add</button></header>
    {draft.inputMappings.map((mapping, index) => <div key={index}><input value={mapping.source} onChange={event => onChange(current => ({ ...current, inputMappings: replaceAt(current.inputMappings, index, { ...mapping, source: event.target.value }) }))} /><ArrowRight size={13} /><input value={mapping.target} onChange={event => onChange(current => ({ ...current, inputMappings: replaceAt(current.inputMappings, index, { ...mapping, target: event.target.value }) }))} /><button type="button" onClick={() => onChange(current => ({ ...current, inputMappings: current.inputMappings.filter((_, candidate) => candidate !== index) }))}><Trash2 size={12} /></button></div>)}
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

function createDraft(topology: AutomationTopologyResponse | null): ResourceDraft {
  const capability = topology?.capabilities[0];
  const binding = topology?.driverBindings.find(item => item.capabilityId === capability?.capabilityId);
  return {
    persisted: false,
    dirty: true,
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
  const {
    persisted: _persisted,
    dirty: _dirty,
    files: _files,
    contentSha256: _contentSha256,
    revision: _revision,
    ...request
  } = draft;
  return request;
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

function portableFileName(sourcePath: string): string | null {
  const name = sourcePath.split(/[\\/]/).pop();
  return name && /^[A-Za-z0-9._-]+$/.test(name) && name !== '.' && name !== '..' ? name : null;
}

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, candidate) => candidate === index ? value : item);
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${(value / 1024 / 1024).toFixed(1)} MiB`;
}
