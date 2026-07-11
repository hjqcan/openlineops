import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  CheckCircle2,
  Factory,
  FolderTree,
  GitBranch,
  Layers3,
  RefreshCw,
  Send,
  SlidersHorizontal
} from 'lucide-react';
import type {
  AutomationSystemResponse,
  AutomationTopologyResponse,
  AutomationProjectWorkspaceResponse,
  EngineeringProjectResponse,
  ProcessDefinitionSummary,
  RecipeResponse,
  StationProfileResponse,
  WorkspaceResponse
} from './contracts';
import {
  createEngineeringProject,
  createRecipe,
  createStationProfile,
  createWorkspace,
  getAutomationTopology,
  listEngineeringProjects,
  listProcessDefinitions,
  listRecipes,
  listStationProfiles,
  listWorkspaces,
  publishConfigurationSnapshot,
  publishRecipe
} from './api';

interface EngineeringWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface EngineeringResources {
  topology: AutomationTopologyResponse | null;
  workspaces: WorkspaceResponse[];
  projects: EngineeringProjectResponse[];
  recipes: RecipeResponse[];
  stations: StationProfileResponse[];
  processDefinitions: ProcessDefinitionSummary[];
}

interface EngineeringDraft {
  recipeId: string;
  recipeVersionId: string;
  recipeName: string;
  stationProfileId: string;
  stationSystemId: string;
  stationName: string;
  deviceBindingId: string;
  deviceOwnerSystemId: string;
  capabilityId: string;
  deviceKey: string;
  snapshotId: string;
  processDefinitionId: string;
  processVersionId: string;
}

const emptyResources: EngineeringResources = {
  topology: null,
  workspaces: [],
  projects: [],
  recipes: [],
  stations: [],
  processDefinitions: []
};

export function EngineeringWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onMessage
}: EngineeringWorkbenchProps): React.ReactElement {
  const activeApplication = useMemo(
    () => activeWorkspace?.project.applications.find(
      candidate => candidate.applicationId === activeApplicationId)
      ?? activeWorkspace?.project.applications[0]
      ?? null,
    [activeApplicationId, activeWorkspace?.project.applications]);
  const activeProjectId = activeWorkspace?.project.projectId ?? null;
  const effectiveApplicationId = activeApplication?.applicationId ?? null;
  const projectApplicationApiScope = useMemo(
    () => activeProjectId && effectiveApplicationId
      ? {
        projectId: activeProjectId,
        applicationId: effectiveApplicationId
      }
      : undefined,
    [activeProjectId, effectiveApplicationId]);
  const engineeringScopeKey = activeProjectId && effectiveApplicationId
    ? `${activeProjectId}\u0000${effectiveApplicationId}`
    : null;
  const engineeringIdentity = useMemo(
    () => effectiveApplicationId
      ? {
        workspaceId: `${effectiveApplicationId}.workspace`,
        workspaceName: `${activeApplication?.displayName ?? effectiveApplicationId} Workspace`,
        projectId: `${effectiveApplicationId}.configuration`,
        projectName: `${activeApplication?.displayName ?? effectiveApplicationId} Configuration`
      }
      : null,
    [activeApplication?.displayName, effectiveApplicationId]);
  const scopeKeyRef = useRef(engineeringScopeKey);
  scopeKeyRef.current = engineeringScopeKey;

  const [resources, setResources] = useState<EngineeringResources>(emptyResources);
  const [draft, setDraft] = useState<EngineeringDraft>(
    () => createEngineeringDraft(undefined, effectiveApplicationId));
  const [createdProject, setCreatedProject] = useState<EngineeringProjectResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const publishedProcesses = useMemo(
    () => resources.processDefinitions.filter(definition => definition.status === 'Published'),
    [resources.processDefinitions]);
  const selectedProcess = useMemo(
    () => publishedProcesses.find(definition => definition.processDefinitionId === draft.processDefinitionId)
      ?? publishedProcesses[0]
      ?? null,
    [draft.processDefinitionId, publishedProcesses]);
  const configurationProject = useMemo(
    () => engineeringIdentity
      ? resources.projects.find(project => project.projectId === engineeringIdentity.projectId) ?? null
      : null,
    [engineeringIdentity, resources.projects]);
  const configurationSnapshots = configurationProject?.snapshots ?? [];
  const stationSystems = useMemo(
    () => resources.topology?.systems.filter(system => system.kind === 'Station') ?? [],
    [resources.topology]);
  const deviceOwnerSystems = useMemo(
    () => resources.topology?.systems.filter(system =>
      isSystemWithinStation(system, draft.stationSystemId, resources.topology!)) ?? [],
    [draft.stationSystemId, resources.topology]);
  const selectedDeviceOwner = deviceOwnerSystems.find(
    system => system.systemId === draft.deviceOwnerSystemId) ?? null;
  const ownerCapabilityIds = selectedDeviceOwner
    ? [...new Set([
      ...selectedDeviceOwner.requiredCapabilityIds,
      ...selectedDeviceOwner.providedCapabilityIds
    ])].sort((left, right) => left.localeCompare(right))
    : [];
  const canPublishSnapshot = isBackendHealthy
    && !busy
    && engineeringIdentity !== null
    && selectedProcess !== null
    && resources.topology !== null
    && selectedDeviceOwner !== null
    && ownerCapabilityIds.includes(draft.capabilityId);

  const loadResources = useCallback(async () => {
    if (!isBackendHealthy || !projectApplicationApiScope || !engineeringIdentity) {
      return;
    }

    const requestedScopeKey = engineeringScopeKey;
    const topologyPromise = activeApplication?.topologyId
      ? getAutomationTopology(activeApplication.topologyId, projectApplicationApiScope)
      : Promise.resolve(null);
    const [workspaces, projects, recipes, stations, processDefinitions, topologyResponse] = await Promise.all([
      listWorkspaces(projectApplicationApiScope),
      listEngineeringProjects(projectApplicationApiScope),
      listRecipes(projectApplicationApiScope),
      listStationProfiles(projectApplicationApiScope),
      listProcessDefinitions(projectApplicationApiScope),
      topologyPromise
    ]);
    if (scopeKeyRef.current !== requestedScopeKey) {
      return;
    }

    setResources({
      topology: topologyResponse?.ok ? topologyResponse.body ?? null : null,
      workspaces,
      projects,
      recipes,
      stations,
      processDefinitions
    });
    setCreatedProject(
      projects.find(project => project.projectId === engineeringIdentity.projectId) ?? null);

    const publishedDefinitions = processDefinitions.filter(
      definition => definition.status === 'Published');
    setDraft(current => {
      const process = publishedDefinitions.find(
        definition => definition.processDefinitionId === current.processDefinitionId)
        ?? publishedDefinitions[0]
        ?? null;
      const topology = topologyResponse?.ok ? topologyResponse.body ?? null : null;
      const stations = topology?.systems.filter(system => system.kind === 'Station') ?? [];
      const stationSystemId = stations.some(system => system.systemId === current.stationSystemId)
        ? current.stationSystemId
        : stations[0]?.systemId ?? current.stationSystemId;
      const owners = topology?.systems.filter(system =>
        isSystemWithinStation(system, stationSystemId, topology)) ?? [];
      const deviceOwnerSystemId = owners.some(system =>
        system.systemId === current.deviceOwnerSystemId)
        ? current.deviceOwnerSystemId
        : owners[0]?.systemId ?? stationSystemId;
      const owner = owners.find(system => system.systemId === deviceOwnerSystemId);
      const capabilities = owner
        ? [...new Set([...owner.requiredCapabilityIds, ...owner.providedCapabilityIds])]
        : [];
      return {
        ...current,
        stationSystemId,
        deviceOwnerSystemId,
        capabilityId: capabilities.includes(current.capabilityId)
          ? current.capabilityId
          : capabilities[0] ?? current.capabilityId,
        processDefinitionId: process?.processDefinitionId ?? '',
        processVersionId: process?.versionId ?? ''
      };
    });
  }, [
    activeApplication?.topologyId,
    engineeringIdentity,
    engineeringScopeKey,
    isBackendHealthy,
    projectApplicationApiScope
  ]);

  useEffect(() => {
    setResources(emptyResources);
    setDraft(createEngineeringDraft(undefined, effectiveApplicationId));
    setCreatedProject(null);
    setBusy(false);
  }, [engineeringScopeKey]);

  useEffect(() => {
    let isCurrent = true;
    loadResources().catch(error => {
      if (isCurrent && scopeKeyRef.current === engineeringScopeKey) {
        onMessage(`Engineering load failed: ${String(error)}`);
      }
    });

    return () => {
      isCurrent = false;
    };
  }, [engineeringScopeKey, loadResources, onMessage]);

  const resetDraft = useCallback(() => {
    setDraft(createEngineeringDraft(selectedProcess ?? undefined, effectiveApplicationId));
  }, [effectiveApplicationId, selectedProcess]);

  const createRuntimeSnapshot = useCallback(async () => {
    const process = selectedProcess;
    if (!process) {
      onMessage('Publish a process definition before creating an engineering snapshot');
      return;
    }
    if (!engineeringIdentity || !projectApplicationApiScope) {
      onMessage('Open a project Application before creating a configuration snapshot');
      return;
    }

    const requestedScopeKey = engineeringScopeKey;
    const isCurrentScope = () => scopeKeyRef.current === requestedScopeKey;
    setBusy(true);
    try {
      const hasWorkspace = resources.workspaces.some(
        workspace => workspace.workspaceId === engineeringIdentity.workspaceId);
      if (!hasWorkspace) {
        const workspace = await createWorkspace({
          workspaceId: engineeringIdentity.workspaceId,
          displayName: engineeringIdentity.workspaceName
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          return;
        }
        if (!workspace.ok) {
          onMessage(`Workspace create failed: ${workspace.status} ${workspace.text}`);
          return;
        }
      }

      const hasProject = resources.projects.some(
        project => project.projectId === engineeringIdentity.projectId);
      if (!hasProject) {
        const project = await createEngineeringProject({
          projectId: engineeringIdentity.projectId,
          workspaceId: engineeringIdentity.workspaceId,
          displayName: engineeringIdentity.projectName
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          return;
        }
        if (!project.ok) {
          onMessage(`Configuration project create failed: ${project.status} ${project.text}`);
          return;
        }
      }

      const existingRecipe = resources.recipes.find(recipe => recipe.recipeId === draft.recipeId);
      let recipeStatus = existingRecipe?.status ?? null;
      if (!existingRecipe) {
        const recipe = await createRecipe({
          recipeId: draft.recipeId,
          versionId: draft.recipeVersionId,
          displayName: draft.recipeName,
          parameters: [
            {
              key: 'inspection.mode',
              value: 'desktop-engineering'
            }
          ]
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          return;
        }
        if (!recipe.ok || !recipe.body) {
          onMessage(`Recipe create failed: ${recipe.status} ${recipe.text}`);
          return;
        }
        recipeStatus = recipe.body.status;
      }

      if (recipeStatus !== 'Published') {
        const publishedRecipe = await publishRecipe(draft.recipeId, projectApplicationApiScope);
        if (!isCurrentScope()) {
          return;
        }
        if (!publishedRecipe.ok) {
          onMessage(`Recipe publish failed: ${publishedRecipe.status} ${publishedRecipe.text}`);
          return;
        }
      }

      const hasStation = resources.stations.some(
        station => station.stationProfileId === draft.stationProfileId);
      if (!hasStation) {
        const station = await createStationProfile({
          stationProfileId: draft.stationProfileId,
          stationSystemId: draft.stationSystemId,
          displayName: draft.stationName,
          deviceBindings: [
            {
              deviceBindingId: draft.deviceBindingId,
              ownerSystemId: draft.deviceOwnerSystemId,
              capabilityId: draft.capabilityId,
              deviceKey: draft.deviceKey
            }
          ]
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          return;
        }
        if (!station.ok) {
          onMessage(`Station create failed: ${station.status} ${station.text}`);
          return;
        }
      }

      const snapshot = await publishConfigurationSnapshot(engineeringIdentity.projectId, {
        snapshotId: draft.snapshotId,
        processDefinitionId: process.processDefinitionId,
        processVersionId: process.versionId,
        recipeId: draft.recipeId,
        stationProfileId: draft.stationProfileId
      }, projectApplicationApiScope);
      if (!isCurrentScope()) {
        return;
      }
      if (!snapshot.ok || !snapshot.body) {
        onMessage(`Snapshot publish failed: ${snapshot.status} ${snapshot.text}`);
        return;
      }

      setCreatedProject(snapshot.body);
      onMessage(`Snapshot published ${draft.snapshotId}`);
      await loadResources();
    } finally {
      if (isCurrentScope()) {
        setBusy(false);
      }
    }
  }, [
    draft,
    engineeringIdentity,
    engineeringScopeKey,
    loadResources,
    onMessage,
    projectApplicationApiScope,
    resources.projects,
    resources.recipes,
    resources.stations,
    resources.workspaces,
    selectedProcess
  ]);

  return (
    <section className="engineering-workbench">
      <div className="panel engineering-builder-panel">
        <div className="panel-title">
          <div>
            <Factory size={17} />
            <h2>Engineering Configuration</h2>
          </div>
          <span>{activeApplication?.displayName ?? 'No application selected'}</span>
        </div>

        <div className="engineering-toolbar">
          <button
            type="button"
            className="button ghost"
            onClick={loadResources}
            disabled={!isBackendHealthy || !engineeringIdentity || busy}
          >
            <RefreshCw size={15} />
            Refresh
          </button>
          <button
            type="button"
            className="button ghost"
            onClick={resetDraft}
            disabled={!engineeringIdentity || busy}
          >
            <SlidersHorizontal size={15} />
            Reset Fields
          </button>
          <button
            type="button"
            className="button primary"
            onClick={createRuntimeSnapshot}
            disabled={!canPublishSnapshot}
            data-testid="create-engineering-bundle"
          >
            <Send size={15} />
            Publish Snapshot
          </button>
        </div>

        <div className="engineering-layout">
          <div className="engineering-form">
            <div className="engineering-application-context" data-testid="engineering-application-context">
              <div>
                <FolderTree size={16} />
                <span>Current Application</span>
                <strong>{activeApplication?.displayName ?? 'Open an Application to continue'}</strong>
              </div>
              <dl>
                <div>
                  <dt>Application</dt>
                  <dd>{effectiveApplicationId ?? '—'}</dd>
                </div>
                <div>
                  <dt>Workspace</dt>
                  <dd>{engineeringIdentity?.workspaceId ?? '—'}</dd>
                </div>
                <div>
                  <dt>Configuration</dt>
                  <dd>{engineeringIdentity?.projectId ?? '—'}</dd>
                </div>
              </dl>
              <small>Workspace and configuration IDs are managed by the Application.</small>
            </div>

            <FieldGroup title="Recipe">
              <TextField
                label="Recipe ID"
                value={draft.recipeId}
                onChange={value => setDraft(current => ({
                  ...current,
                  recipeId: value,
                  recipeVersionId: `${value}@1.0.0`
                }))}
              />
              <TextField
                label="Recipe Version"
                value={draft.recipeVersionId}
                onChange={value => setDraft(current => ({ ...current, recipeVersionId: value }))}
              />
              <TextField
                label="Display Name"
                value={draft.recipeName}
                onChange={value => setDraft(current => ({ ...current, recipeName: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Station Profile">
              <TextField
                label="Station Profile ID"
                value={draft.stationProfileId}
                onChange={value => setDraft(current => ({ ...current, stationProfileId: value }))}
              />
              <label>
                <span>Station System</span>
                <select
                  value={draft.stationSystemId}
                  onChange={event => {
                    const stationSystemId = event.target.value;
                    const owners = resources.topology?.systems.filter(system =>
                      isSystemWithinStation(system, stationSystemId, resources.topology!)) ?? [];
                    const owner = owners[0] ?? null;
                    const capabilities = owner
                      ? [...new Set([
                        ...owner.requiredCapabilityIds,
                        ...owner.providedCapabilityIds
                      ])]
                      : [];
                    setDraft(current => ({
                      ...current,
                      stationSystemId,
                      deviceOwnerSystemId: owner?.systemId ?? '',
                      capabilityId: capabilities[0] ?? ''
                    }));
                  }}
                  data-testid="engineering-station-system"
                >
                  {stationSystems.length === 0 ? (
                    <option value="">Create and link a topology Station first</option>
                  ) : stationSystems.map(system => (
                    <option key={system.systemId} value={system.systemId}>
                      {system.displayName} ({system.systemId})
                    </option>
                  ))}
                </select>
              </label>
              <TextField
                label="Display Name"
                value={draft.stationName}
                onChange={value => setDraft(current => ({ ...current, stationName: value }))}
              />
              <TextField
                label="Device Binding ID"
                value={draft.deviceBindingId}
                onChange={value => setDraft(current => ({ ...current, deviceBindingId: value }))}
              />
              <label>
                <span>Device Owner System</span>
                <select
                  value={draft.deviceOwnerSystemId}
                  onChange={event => {
                    const deviceOwnerSystemId = event.target.value;
                    const owner = deviceOwnerSystems.find(system =>
                      system.systemId === deviceOwnerSystemId) ?? null;
                    const capabilities = owner
                      ? [...new Set([
                        ...owner.requiredCapabilityIds,
                        ...owner.providedCapabilityIds
                      ])]
                      : [];
                    setDraft(current => ({
                      ...current,
                      deviceOwnerSystemId,
                      capabilityId: capabilities.includes(current.capabilityId)
                        ? current.capabilityId
                        : capabilities[0] ?? ''
                    }));
                  }}
                  data-testid="engineering-device-owner-system"
                >
                  {deviceOwnerSystems.length === 0 ? (
                    <option value="">No System in selected Station</option>
                  ) : deviceOwnerSystems.map(system => (
                    <option key={system.systemId} value={system.systemId}>
                      {system.displayName} ({system.systemId})
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>Capability</span>
                <select
                  value={draft.capabilityId}
                  onChange={event => setDraft(current => ({
                    ...current,
                    capabilityId: event.target.value
                  }))}
                  data-testid="engineering-device-capability"
                >
                  {ownerCapabilityIds.length === 0 ? (
                    <option value="">Owner System declares no capabilities</option>
                  ) : ownerCapabilityIds.map(capabilityId => (
                    <option key={capabilityId} value={capabilityId}>{capabilityId}</option>
                  ))}
                </select>
              </label>
              <TextField
                label="Device Key"
                value={draft.deviceKey}
                onChange={value => setDraft(current => ({ ...current, deviceKey: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Configuration Snapshot">
              <label>
                <span>Published Process</span>
                <select
                  value={selectedProcess?.processDefinitionId ?? ''}
                  onChange={event => {
                    const nextProcess = publishedProcesses.find(
                      definition => definition.processDefinitionId === event.target.value);
                    setDraft(current => ({
                      ...current,
                      processDefinitionId: nextProcess?.processDefinitionId ?? '',
                      processVersionId: nextProcess?.versionId ?? ''
                    }));
                  }}
                >
                  {publishedProcesses.length === 0 ? (
                    <option value="">No published process</option>
                  ) : publishedProcesses.map(process => (
                    <option key={process.processDefinitionId} value={process.processDefinitionId}>
                      {process.displayName}
                    </option>
                  ))}
                </select>
              </label>
              <TextField
                label="Snapshot ID"
                value={draft.snapshotId}
                onChange={value => setDraft(current => ({ ...current, snapshotId: value }))}
              />
              <TextField
                label="Process Version"
                value={selectedProcess?.versionId ?? draft.processVersionId}
                onChange={value => setDraft(current => ({ ...current, processVersionId: value }))}
                readOnly
              />
            </FieldGroup>
          </div>

          <div className="engineering-summary">
            <ResourceColumn
              icon={Layers3}
              title="Recipes"
              count={resources.recipes.length}
              rows={resources.recipes.map(recipe => ({
                id: recipe.recipeId,
                title: recipe.displayName,
                meta: `${recipe.status} / ${recipe.versionId}`
              }))}
            />
            <ResourceColumn
              icon={Factory}
              title="Station Profiles"
              count={resources.stations.length}
              rows={resources.stations.map(station => ({
                id: station.stationProfileId,
                title: station.displayName,
                meta: `${station.deviceBindings.length} bindings`
              }))}
            />
            <ResourceColumn
              icon={GitBranch}
              title="Configuration Snapshots"
              count={configurationSnapshots.length}
              rows={configurationSnapshots.map(snapshot => ({
                id: snapshot.snapshotId,
                title: snapshot.processDefinitionId,
                meta: `${snapshot.status} / ${formatDate(snapshot.publishedAtUtc)}`
              }))}
            />
          </div>
        </div>
      </div>

      <div className="panel engineering-snapshot-panel">
        <div className="panel-title">
          <div>
            <GitBranch size={17} />
            <h2>Active Snapshot</h2>
          </div>
          <span>{createdProject?.activeSnapshotId ?? 'waiting'}</span>
        </div>
        {createdProject ? (
          <SnapshotResult project={createdProject} />
        ) : (
          <div className="engineering-empty">
            <p>{engineeringIdentity
              ? 'Select a published process and publish a configuration snapshot.'
              : 'Open a project Application to configure engineering resources.'}</p>
          </div>
        )}
      </div>
    </section>
  );
}

function FieldGroup({
  title,
  children
}: {
  title: string;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <fieldset className="engineering-fieldset">
      <legend>{title}</legend>
      {children}
    </fieldset>
  );
}

function TextField({
  label,
  value,
  readOnly = false,
  onChange
}: {
  label: string;
  value: string;
  readOnly?: boolean;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input
        value={value}
        readOnly={readOnly}
        onChange={event => onChange(event.target.value)}
      />
    </label>
  );
}

function ResourceColumn({
  icon: Icon,
  title,
  count,
  rows
}: {
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  count: number;
  rows: Array<{ id: string; title: string; meta: string }>;
}): React.ReactElement {
  return (
    <section className="engineering-resource-column">
      <div>
        <Icon size={15} />
        <strong>{title}</strong>
        <span>{count}</span>
      </div>
      {rows.length === 0 ? (
        <p>No rows yet</p>
      ) : rows.slice(0, 5).map(row => (
        <article key={row.id}>
          <strong>{row.title}</strong>
          <span>{row.id}</span>
          <small>{row.meta}</small>
        </article>
      ))}
    </section>
  );
}

function SnapshotResult({ project }: { project: EngineeringProjectResponse }): React.ReactElement {
  const snapshot = project.snapshots.find(item => item.snapshotId === project.activeSnapshotId)
    ?? project.snapshots[0]
    ?? null;

  if (!snapshot) {
    return (
      <div className="engineering-empty">
        <p>Project was created without a snapshot response.</p>
      </div>
    );
  }

  return (
    <div className="snapshot-result" data-testid="engineering-result">
      <div>
        <CheckCircle2 size={18} />
        <strong>{snapshot.status}</strong>
      </div>
      <dl>
        <dt>Snapshot</dt>
        <dd>{snapshot.snapshotId}</dd>
        <dt>Project</dt>
        <dd>{snapshot.projectId}</dd>
        <dt>Process</dt>
        <dd>{snapshot.processDefinitionId}</dd>
        <dt>Recipe</dt>
        <dd>{snapshot.recipeVersionId}</dd>
        <dt>Station</dt>
        <dd>{snapshot.stationProfileId}</dd>
      </dl>
    </div>
  );
}

function createEngineeringDraft(
  process?: ProcessDefinitionSummary,
  applicationId?: string | null
): EngineeringDraft {
  const seed = Date.now().toString(36);
  const prefix = (applicationId || 'application').replace(/[^a-zA-Z0-9_-]/g, '-');
  const recipeId = `${prefix}-recipe-${seed}`;

  return {
    recipeId,
    recipeVersionId: `${recipeId}@1.0.0`,
    recipeName: 'Application Runtime Recipe',
    stationProfileId: `${prefix}-station-${seed}`,
    stationSystemId: `${applicationId || 'application'}.station.1`,
    stationName: 'Application Runtime Station',
    deviceBindingId: 'loopback-primary',
    deviceOwnerSystemId: '',
    capabilityId: 'device.loopback',
    deviceKey: 'loopback-01',
    snapshotId: `${prefix}-snapshot-${seed}`,
    processDefinitionId: process?.processDefinitionId ?? '',
    processVersionId: process?.versionId ?? ''
  };
}

function isSystemWithinStation(
  system: AutomationSystemResponse,
  stationSystemId: string,
  topology: AutomationTopologyResponse
): boolean {
  let current: AutomationSystemResponse | undefined = system;
  for (let depth = 0; current && depth <= topology.systems.length; depth += 1) {
    if (current.systemId === stationSystemId) {
      return true;
    }
    current = current.parentSystemId
      ? topology.systems.find(candidate => candidate.systemId === current?.parentSystemId)
      : undefined;
  }
  return false;
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}
