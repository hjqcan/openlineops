import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ArrowRight,
  Boxes,
  ChevronDown,
  ChevronUp,
  Cpu,
  Factory,
  FlaskConical,
  Plus,
  RefreshCw,
  Save,
  Trash2,
  Workflow
} from 'lucide-react';
import type {
  AutomationProjectWorkspaceResponse,
  AutomationTopologyResponse,
  ExternalTestProgramAdapterRequest,
  ProcessDefinitionSummary,
  ProductionLineResponse,
  ProductionLineSummaryResponse,
  ProductionStageRequest,
  ProductionWorkstationRequest,
  SaveProductionLineRequest
} from './contracts';
import {
  createProductionLine,
  getAutomationTopology,
  getProductionLine,
  listProcessDefinitions,
  listProductionLines,
  replaceProductionLine,
  type ProjectApplicationApiScope
} from './api';

interface ProductionWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface ProductionLineDraft extends SaveProductionLineRequest {
  persisted: boolean;
}

type AdapterLaunchKind = 'Provider' | 'ApplicationExecutable';

interface ExternalAdapterDraft extends ExternalTestProgramAdapterRequest {
  launchKind: AdapterLaunchKind;
}

export function ProductionWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onMessage
}: ProductionWorkbenchProps): React.ReactElement {
  const [lines, setLines] = useState<ProductionLineSummaryResponse[]>([]);
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [flows, setFlows] = useState<ProcessDefinitionSummary[]>([]);
  const [draft, setDraft] = useState<ProductionLineDraft>(() => createEmptyDraft(null, []));
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

  const refresh = useCallback(async () => {
    if (!scope || !isBackendHealthy) {
      setLines([]);
      setTopology(null);
      setFlows([]);
      return;
    }

    const [nextLines, nextFlows, topologyResponse] = await Promise.all([
      listProductionLines(scope),
      listProcessDefinitions(scope),
      activeApplication?.topologyId
        ? getAutomationTopology(activeApplication.topologyId, scope)
        : Promise.resolve(null)
    ]);
    setLines(nextLines);
    setFlows(nextFlows);
    setTopology(topologyResponse?.ok && topologyResponse.body ? topologyResponse.body : null);
  }, [activeApplication?.topologyId, isBackendHealthy, scope]);

  useEffect(() => {
    refresh().catch(error => onMessage(`Production line refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

  useEffect(() => {
    setDraft(createEmptyDraft(null, []));
  }, [scope?.applicationId]);

  useEffect(() => {
    if (!topology) {
      return;
    }

    setDraft(current => !current.persisted && !current.topologyId
      ? createEmptyDraft(topology, publishedFlows)
      : current);
  }, [publishedFlows, topology]);

  const createNew = useCallback(() => {
    setDraft(createEmptyDraft(topology, publishedFlows));
    onMessage('New production line draft');
  }, [onMessage, publishedFlows, topology]);

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

      setDraft(fromResponse(response.body));
      onMessage(`Production line opened ${response.body.lineDefinitionId}`);
    } finally {
      setBusy(false);
    }
  }, [onMessage, scope]);

  const save = useCallback(async () => {
    if (!scope || !isBackendHealthy) {
      onMessage('Open an Application with a healthy backend before saving a production line');
      return;
    }

    const request = toRequest(draft);
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
  }, [draft, isBackendHealthy, onMessage, refresh, scope]);

  const addWorkstation = useCallback(() => {
    setDraft(current => {
      const index = current.workstations.length + 1;
      const station = stationSystems[index - 1] ?? stationSystems[0];
      return {
        ...current,
        workstations: [...current.workstations, {
          workstationId: `workstation-${index}`,
          displayName: station?.displayName ?? `Workstation ${index}`,
          stationSystemId: station?.systemId ?? ''
        }]
      };
    });
  }, [stationSystems]);

  const addStage = useCallback(() => {
    setDraft(current => {
      const sequence = current.stages.length + 1;
      return {
        ...current,
        stages: [...current.stages, {
          stageId: `stage-${sequence}`,
          sequence,
          displayName: `Stage ${sequence}`,
          workstationId: current.workstations[0]?.workstationId ?? '',
          flowDefinitionId: publishedFlows[0]?.processDefinitionId ?? '',
          externalTestProgramAdapterId: null
        }]
      };
    });
  }, [publishedFlows]);

  const addAdapter = useCallback(() => {
    setDraft(current => ({
      ...current,
      externalTestProgramAdapters: [
        ...current.externalTestProgramAdapters,
        createExternalAdapter(current.externalTestProgramAdapters.length + 1, topology)
      ]
    }));
  }, [topology]);

  if (!activeWorkspace || !activeApplication) {
    return (
      <section className="production-workbench production-empty">
        <Factory size={28} />
        <h2>Open an Application to design its production line.</h2>
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
          <button type="button" className="button ghost" onClick={() => void refresh()} disabled={busy}>
            <RefreshCw size={14} />
            Refresh
          </button>
          <button type="button" className="button" onClick={createNew} disabled={busy} data-testid="new-production-line">
            <Plus size={14} />
            New Line
          </button>
          <button type="button" className="button primary" onClick={() => void save()} disabled={busy || !isBackendHealthy} data-testid="save-production-line">
            <Save size={14} />
            Save Line
          </button>
        </div>
      </header>

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
                <small>{line.dutModelCode} · {line.stageCount} stages</small>
              </span>
            </button>
          ))}
        </aside>

        <main className="production-editor">
          <section className="production-identity-card">
            <div className="production-section-kicker">LINE DEFINITION</div>
            <div className="production-identity-grid">
              <Field label="Line ID">
                <input
                  value={draft.lineDefinitionId}
                  disabled={draft.persisted}
                  onChange={event => setDraft(current => ({ ...current, lineDefinitionId: event.target.value }))}
                  data-testid="production-line-id"
                />
              </Field>
              <Field label="Display Name">
                <input
                  value={draft.displayName}
                  onChange={event => setDraft(current => ({ ...current, displayName: event.target.value }))}
                  data-testid="production-line-name"
                />
              </Field>
              <Field label="Topology">
                <input value={draft.topologyId} readOnly />
              </Field>
            </div>
            <div className="production-dut-strip">
              <div className="production-dut-mark"><Cpu size={18} /></div>
              <Field label="DUT Model ID">
                <input
                  value={draft.dutModel.dutModelId}
                  onChange={event => setDraft(current => ({
                    ...current,
                    dutModel: { ...current.dutModel, dutModelId: event.target.value }
                  }))}
                />
              </Field>
              <Field label="Model Code">
                <input
                  value={draft.dutModel.modelCode}
                  onChange={event => setDraft(current => ({
                    ...current,
                    dutModel: { ...current.dutModel, modelCode: event.target.value }
                  }))}
                  data-testid="production-dut-model-code"
                />
              </Field>
              <Field label="Runtime Identity Key">
                <input
                  value={draft.dutModel.identityInputKey}
                  onChange={event => setDraft(current => ({
                    ...current,
                    dutModel: { ...current.dutModel, identityInputKey: event.target.value }
                  }))}
                />
              </Field>
            </div>
          </section>

          <StageRibbon stages={draft.stages} workstations={draft.workstations} />

          <EditorSection
            icon={Factory}
            eyebrow="PHYSICAL BINDING"
            title="Workstations"
            detail="Each logical workstation binds to one Station node and one system module in this Application topology."
            actionLabel="Add Workstation"
            onAction={addWorkstation}
          >
            <div className="production-card-grid">
              {draft.workstations.map((workstation, index) => (
                <WorkstationCard
                  key={`${workstation.workstationId}-${index}`}
                  workstation={workstation}
                  index={index}
                  topology={topology}
                  onChange={next => setDraft(current => ({
                    ...current,
                    workstations: replaceAt(current.workstations, index, next)
                  }))}
                  onRemove={() => setDraft(current => ({
                    ...current,
                    workstations: current.workstations.filter((_, candidateIndex) => candidateIndex !== index)
                  }))}
                />
              ))}
            </div>
          </EditorSection>

          <EditorSection
            icon={Workflow}
            eyebrow="PROCESS ROUTE"
            title="Ordered Stages"
            detail="Stages are contiguous and deterministic. Every stage points to a published Flow."
            actionLabel="Add Stage"
            onAction={addStage}
          >
            <div className="production-stage-list">
              {draft.stages.map((stage, index) => (
                <StageCard
                  key={`${stage.stageId}-${index}`}
                  stage={stage}
                  index={index}
                  count={draft.stages.length}
                  workstations={draft.workstations}
                  flows={publishedFlows}
                  adapters={draft.externalTestProgramAdapters}
                  onChange={next => setDraft(current => ({
                    ...current,
                    stages: replaceAt(current.stages, index, next)
                  }))}
                  onMove={direction => setDraft(current => ({
                    ...current,
                    stages: moveStage(current.stages, index, direction)
                  }))}
                  onRemove={() => setDraft(current => ({
                    ...current,
                    stages: resequence(current.stages.filter((_, candidateIndex) => candidateIndex !== index))
                  }))}
                />
              ))}
            </div>
          </EditorSection>

          <EditorSection
            icon={FlaskConical}
            eyebrow="VENDOR TEST INTEGRATION"
            title="External Test Programs"
            detail="Adapters declare launch, DUT inputs and normalized result mappings; execution still enters the standard command lifecycle."
            actionLabel="Add Adapter"
            onAction={addAdapter}
          >
            <div className="production-adapter-list">
              {(draft.externalTestProgramAdapters as ExternalAdapterDraft[]).map((adapter, index) => (
                <AdapterCard
                  key={`${adapter.adapterId}-${index}`}
                  adapter={adapter}
                  index={index}
                  topology={topology}
                  onChange={next => setDraft(current => ({
                    ...current,
                    externalTestProgramAdapters: replaceAt(
                      current.externalTestProgramAdapters,
                      index,
                      next)
                  }))}
                  onRemove={() => setDraft(current => ({
                    ...current,
                    externalTestProgramAdapters: current.externalTestProgramAdapters
                      .filter((_, candidateIndex) => candidateIndex !== index)
                  }))}
                />
              ))}
              {draft.externalTestProgramAdapters.length === 0 ? (
                <div className="production-inline-empty">
                  <Boxes size={20} />
                  <span>No external program is required for this line.</span>
                </div>
              ) : null}
            </div>
          </EditorSection>
        </main>
      </div>
    </section>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return <label><span>{label}</span>{children}</label>;
}

function EditorSection({
  icon: Icon,
  eyebrow,
  title,
  detail,
  actionLabel,
  onAction,
  children
}: {
  icon: React.ComponentType<{ size?: number }>;
  eyebrow: string;
  title: string;
  detail: string;
  actionLabel: string;
  onAction(): void;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <section className="production-editor-section">
      <header>
        <div className="production-section-icon"><Icon size={17} /></div>
        <div>
          <small>{eyebrow}</small>
          <h2>{title}</h2>
          <p>{detail}</p>
        </div>
        <button type="button" className="button ghost" onClick={onAction}>
          <Plus size={14} />
          {actionLabel}
        </button>
      </header>
      {children}
    </section>
  );
}

function StageRibbon({
  stages,
  workstations
}: {
  stages: ProductionStageRequest[];
  workstations: ProductionWorkstationRequest[];
}): React.ReactElement {
  return (
    <section className="production-stage-ribbon" aria-label="Production stage route">
      <div className="production-ribbon-heading">
        <span>BOARD ROUTE</span>
        <small>{stages.length} ordered stages</small>
      </div>
      <div className="production-ribbon-track">
        <div className="production-ribbon-origin"><Cpu size={16} /><span>DUT</span></div>
        {stages.map((stage, index) => {
          const workstation = workstations.find(candidate => candidate.workstationId === stage.workstationId);
          return (
            <React.Fragment key={`${stage.stageId}-${index}`}>
              <ArrowRight size={16} className="production-ribbon-arrow" />
              <div className="production-ribbon-stage">
                <b>{String(index + 1).padStart(2, '0')}</b>
                <span>{stage.displayName || stage.stageId}</span>
                <small>{workstation?.displayName ?? 'Unbound workstation'}</small>
              </div>
            </React.Fragment>
          );
        })}
      </div>
    </section>
  );
}

function WorkstationCard({
  workstation,
  index,
  topology,
  onChange,
  onRemove
}: {
  workstation: ProductionWorkstationRequest;
  index: number;
  topology: AutomationTopologyResponse | null;
  onChange(next: ProductionWorkstationRequest): void;
  onRemove(): void;
}): React.ReactElement {
  const stationSystems = topology?.systems.filter(system => system.kind === 'Station') ?? [];
  return (
    <article className="production-workstation-card" data-testid={`production-workstation-${index}`}>
      <div className="production-card-number">W{String(index + 1).padStart(2, '0')}</div>
      <button type="button" className="icon-button danger" onClick={onRemove} title="Remove workstation">
        <Trash2 size={14} />
      </button>
      <Field label="Workstation ID">
        <input value={workstation.workstationId} onChange={event => onChange({ ...workstation, workstationId: event.target.value })} />
      </Field>
      <Field label="Display Name">
        <input value={workstation.displayName} onChange={event => onChange({ ...workstation, displayName: event.target.value })} />
      </Field>
      <Field label="Station System">
        <select
          value={workstation.stationSystemId}
          onChange={event => onChange({ ...workstation, stationSystemId: event.target.value })}
        >
          <option value="">Select Station</option>
          {stationSystems.map(system => (
            <option key={system.systemId} value={system.systemId}>{system.displayName} · {system.systemType}</option>
          ))}
        </select>
      </Field>
    </article>
  );
}

function StageCard({
  stage,
  index,
  count,
  workstations,
  flows,
  adapters,
  onChange,
  onMove,
  onRemove
}: {
  stage: ProductionStageRequest;
  index: number;
  count: number;
  workstations: ProductionWorkstationRequest[];
  flows: ProcessDefinitionSummary[];
  adapters: ExternalTestProgramAdapterRequest[];
  onChange(next: ProductionStageRequest): void;
  onMove(direction: -1 | 1): void;
  onRemove(): void;
}): React.ReactElement {
  return (
    <article className="production-stage-card" data-testid={`production-stage-${index}`}>
      <div className="production-stage-sequence">{String(index + 1).padStart(2, '0')}</div>
      <div className="production-stage-fields">
        <Field label="Stage ID">
          <input value={stage.stageId} onChange={event => onChange({ ...stage, stageId: event.target.value })} />
        </Field>
        <Field label="Stage Name">
          <input value={stage.displayName} onChange={event => onChange({ ...stage, displayName: event.target.value })} />
        </Field>
        <Field label="Workstation">
          <select value={stage.workstationId} onChange={event => onChange({ ...stage, workstationId: event.target.value })}>
            <option value="">Select Workstation</option>
            {workstations.map(workstation => <option key={workstation.workstationId} value={workstation.workstationId}>{workstation.displayName}</option>)}
          </select>
        </Field>
        <Field label="Published Flow">
          <select value={stage.flowDefinitionId} onChange={event => onChange({ ...stage, flowDefinitionId: event.target.value })}>
            <option value="">Select Flow</option>
            {flows.map(flow => <option key={flow.processDefinitionId} value={flow.processDefinitionId}>{flow.displayName} · {flow.versionId}</option>)}
          </select>
        </Field>
        <Field label="External Test Adapter">
          <select
            value={stage.externalTestProgramAdapterId ?? ''}
            onChange={event => onChange({ ...stage, externalTestProgramAdapterId: event.target.value || null })}
          >
            <option value="">None · authored automation</option>
            {adapters.map(adapter => <option key={adapter.adapterId} value={adapter.adapterId}>{adapter.displayName}</option>)}
          </select>
        </Field>
      </div>
      <div className="production-stage-controls">
        <button type="button" className="icon-button" disabled={index === 0} onClick={() => onMove(-1)} title="Move stage up"><ChevronUp size={14} /></button>
        <button type="button" className="icon-button" disabled={index === count - 1} onClick={() => onMove(1)} title="Move stage down"><ChevronDown size={14} /></button>
        <button type="button" className="icon-button danger" onClick={onRemove} title="Remove stage"><Trash2 size={14} /></button>
      </div>
    </article>
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
  const selectCapability = (capabilityId: string) => {
    const capability = topology?.capabilities.find(candidate => candidate.capabilityId === capabilityId);
    const binding = topology?.driverBindings.find(candidate => candidate.capabilityId === capabilityId);
    onChange({
      ...adapter,
      capabilityId,
      commandName: capability?.commandName ?? '',
      timeoutMilliseconds: (capability?.timeoutSeconds ?? 30) * 1000,
      providerKey: adapter.launchKind === 'Provider' ? binding?.providerKey ?? '' : null
    });
  };

  return (
    <article className="production-adapter-card" data-testid={`production-adapter-${index}`}>
      <header>
        <div><FlaskConical size={16} /><strong>{adapter.displayName || `External Adapter ${index + 1}`}</strong></div>
        <button type="button" className="icon-button danger" onClick={onRemove} title="Remove adapter"><Trash2 size={14} /></button>
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
              <option key={capability.capabilityId} value={capability.capabilityId}>{capability.commandName} · {capability.capabilityId}</option>
            ))}
          </select>
        </Field>
        <Field label="Command">
          <input value={adapter.commandName} readOnly />
        </Field>
        <Field label="Launch">
          <select
            value={adapter.launchKind}
            onChange={event => {
              const launchKind = event.target.value as AdapterLaunchKind;
              const binding = topology?.driverBindings.find(candidate => candidate.capabilityId === adapter.capabilityId);
              onChange({
                ...adapter,
                launchKind,
                executable: launchKind === 'ApplicationExecutable' ? 'programs/vendor-test.exe' : null,
                providerKey: launchKind === 'Provider' ? binding?.providerKey ?? '' : null
              });
            }}
          >
            <option value="Provider">Bound Provider</option>
            <option value="ApplicationExecutable">Application Executable</option>
          </select>
        </Field>
        <Field label={adapter.launchKind === 'Provider' ? 'Provider Key' : 'Executable Path'}>
          <input
            value={adapter.launchKind === 'Provider' ? adapter.providerKey ?? '' : adapter.executable ?? ''}
            onChange={event => onChange(adapter.launchKind === 'Provider'
              ? { ...adapter, providerKey: event.target.value, executable: null }
              : { ...adapter, executable: event.target.value, providerKey: null })}
          />
        </Field>
        <Field label="Timeout (ms)">
          <input
            type="number"
            min="1"
            value={adapter.timeoutMilliseconds}
            onChange={event => onChange({
              ...adapter,
              timeoutMilliseconds: Math.max(1, Number.parseInt(event.target.value, 10) || 1)
            })}
          />
        </Field>
      </div>
      <MappingEditor
        title="Arguments"
        rows={adapter.argumentTemplates}
        onChange={rows => onChange({ ...adapter, argumentTemplates: rows })}
      />
      <PairMappingEditor
        title="DUT Input Mappings"
        leftLabel="Source"
        rightLabel="Program Input"
        rows={adapter.inputMappings.map(mapping => [mapping.source, mapping.target])}
        onChange={rows => onChange({
          ...adapter,
          inputMappings: rows.map(([source, target]) => ({ source, target }))
        })}
      />
      <PairMappingEditor
        title="Result Mappings"
        leftLabel="Vendor Result Path"
        rightLabel="Trace Key"
        rows={adapter.resultMappings.map(mapping => [mapping.sourcePath, mapping.targetKey])}
        onChange={rows => onChange({
          ...adapter,
          resultMappings: rows.map(([sourcePath, targetKey]) => ({ sourcePath, targetKey }))
        })}
      />
    </article>
  );
}

function MappingEditor({
  title,
  rows,
  onChange
}: {
  title: string;
  rows: string[];
  onChange(rows: string[]): void;
}): React.ReactElement {
  return (
    <div className="production-mapping-editor">
      <header><strong>{title}</strong><button type="button" onClick={() => onChange([...rows, ''])}><Plus size={13} /> Add</button></header>
      {rows.map((row, index) => (
        <div key={index} className="production-mapping-row single">
          <input value={row} onChange={event => onChange(replaceAt(rows, index, event.target.value))} />
          <button type="button" className="icon-button danger" onClick={() => onChange(rows.filter((_, candidate) => candidate !== index))}><Trash2 size={13} /></button>
        </div>
      ))}
    </div>
  );
}

function PairMappingEditor({
  title,
  leftLabel,
  rightLabel,
  rows,
  onChange
}: {
  title: string;
  leftLabel: string;
  rightLabel: string;
  rows: Array<[string, string]>;
  onChange(rows: Array<[string, string]>): void;
}): React.ReactElement {
  return (
    <div className="production-mapping-editor">
      <header><strong>{title}</strong><button type="button" onClick={() => onChange([...rows, ['', '']])}><Plus size={13} /> Add</button></header>
      {rows.map((row, index) => (
        <div key={index} className="production-mapping-row">
          <input aria-label={leftLabel} value={row[0]} onChange={event => onChange(replaceAt(rows, index, [event.target.value, row[1]]))} />
          <ArrowRight size={14} />
          <input aria-label={rightLabel} value={row[1]} onChange={event => onChange(replaceAt(rows, index, [row[0], event.target.value]))} />
          <button type="button" className="icon-button danger" onClick={() => onChange(rows.filter((_, candidate) => candidate !== index))}><Trash2 size={13} /></button>
        </div>
      ))}
    </div>
  );
}

function createEmptyDraft(
  topology: AutomationTopologyResponse | null,
  publishedFlows: ProcessDefinitionSummary[]
): ProductionLineDraft {
  const seed = Date.now().toString(36);
  const station = topology?.systems.find(system => system.kind === 'Station');
  const workstation: ProductionWorkstationRequest = {
    workstationId: 'workstation-1',
    displayName: station?.displayName ?? 'Workstation 1',
    stationSystemId: station?.systemId ?? ''
  };

  return {
    persisted: false,
    lineDefinitionId: `line-${seed}`,
    displayName: 'New Production Line',
    topologyId: topology?.topologyId ?? '',
    dutModel: {
      dutModelId: 'dut-mainboard',
      modelCode: 'MAINBOARD-A',
      identityInputKey: 'serialNumber'
    },
    workstations: [workstation],
    stages: [{
      stageId: 'stage-1',
      sequence: 1,
      displayName: 'Stage 1',
      workstationId: workstation.workstationId,
      flowDefinitionId: publishedFlows[0]?.processDefinitionId ?? '',
      externalTestProgramAdapterId: null
    }],
    externalTestProgramAdapters: []
  };
}

function createExternalAdapter(
  index: number,
  topology: AutomationTopologyResponse | null
): ExternalAdapterDraft {
  const capability = topology?.capabilities[0];
  const binding = topology?.driverBindings.find(candidate => candidate.capabilityId === capability?.capabilityId);
  return {
    adapterId: `external-test-${index}`,
    displayName: `External Test ${index}`,
    capabilityId: capability?.capabilityId ?? '',
    commandName: capability?.commandName ?? '',
    launchKind: 'Provider',
    executable: null,
    providerKey: binding?.providerKey ?? '',
    argumentTemplates: ['--serial={{dut.identity}}', '--model={{dut.model}}'],
    inputMappings: [
      { source: '$dut.identity', target: 'serialNumber' },
      { source: '$dut.model', target: 'modelCode' }
    ],
    resultMappings: [{ sourcePath: '$.judgement', targetKey: 'judgement' }],
    timeoutMilliseconds: (capability?.timeoutSeconds ?? 30) * 1000
  };
}

function fromResponse(response: ProductionLineResponse): ProductionLineDraft {
  return {
    persisted: true,
    lineDefinitionId: response.lineDefinitionId,
    displayName: response.displayName,
    topologyId: response.topologyId,
    dutModel: { ...response.dutModel },
    workstations: response.workstations.map(workstation => ({ ...workstation })),
    stages: response.stages.map(stage => ({
      stageId: stage.stageId,
      sequence: stage.sequence,
      displayName: stage.displayName,
      workstationId: stage.workstationId,
      flowDefinitionId: stage.flowDefinitionId,
      externalTestProgramAdapterId: stage.externalTestProgramAdapterId
    })),
    externalTestProgramAdapters: response.externalTestProgramAdapters.map(adapter => ({
      adapterId: adapter.adapterId,
      displayName: adapter.displayName,
      capabilityId: adapter.capabilityId,
      commandName: adapter.commandName,
      launchKind: adapter.launchKind === 'ApplicationExecutable' ? 'ApplicationExecutable' : 'Provider',
      executable: adapter.executable,
      providerKey: adapter.providerKey,
      argumentTemplates: [...adapter.argumentTemplates],
      inputMappings: adapter.inputMappings.map(mapping => ({ ...mapping })),
      resultMappings: adapter.resultMappings.map(mapping => ({ ...mapping })),
      timeoutMilliseconds: adapter.timeoutMilliseconds
    } as ExternalAdapterDraft))
  };
}

function toRequest(draft: ProductionLineDraft): SaveProductionLineRequest {
  return {
    lineDefinitionId: draft.lineDefinitionId.trim(),
    displayName: draft.displayName.trim(),
    topologyId: draft.topologyId.trim(),
    dutModel: {
      dutModelId: draft.dutModel.dutModelId.trim(),
      modelCode: draft.dutModel.modelCode.trim(),
      identityInputKey: draft.dutModel.identityInputKey.trim()
    },
    workstations: draft.workstations.map(workstation => ({
      workstationId: workstation.workstationId.trim(),
      displayName: workstation.displayName.trim(),
      stationSystemId: workstation.stationSystemId
    })),
    stages: resequence(draft.stages).map(stage => ({ ...stage })),
    externalTestProgramAdapters: (draft.externalTestProgramAdapters as ExternalAdapterDraft[]).map(adapter => ({
      adapterId: adapter.adapterId.trim(),
      displayName: adapter.displayName.trim(),
      capabilityId: adapter.capabilityId,
      commandName: adapter.commandName,
      executable: adapter.launchKind === 'ApplicationExecutable' ? adapter.executable?.trim() || null : null,
      providerKey: adapter.launchKind === 'Provider' ? adapter.providerKey?.trim() || null : null,
      argumentTemplates: adapter.argumentTemplates.map(value => value.trim()).filter(Boolean),
      inputMappings: adapter.inputMappings.map(mapping => ({
        source: mapping.source.trim(),
        target: mapping.target.trim()
      })),
      resultMappings: adapter.resultMappings.map(mapping => ({
        sourcePath: mapping.sourcePath.trim(),
        targetKey: mapping.targetKey.trim()
      })),
      timeoutMilliseconds: adapter.timeoutMilliseconds
    }))
  };
}

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, candidateIndex) => candidateIndex === index ? value : item);
}

function moveStage(stages: ProductionStageRequest[], index: number, direction: -1 | 1): ProductionStageRequest[] {
  const target = index + direction;
  if (target < 0 || target >= stages.length) {
    return stages;
  }

  const next = [...stages];
  [next[index], next[target]] = [next[target], next[index]];
  return resequence(next);
}

function resequence(stages: ProductionStageRequest[]): ProductionStageRequest[] {
  return stages.map((stage, index) => ({ ...stage, sequence: index + 1 }));
}
