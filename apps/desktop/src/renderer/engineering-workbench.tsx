import React, { useCallback, useEffect, useMemo, useState } from 'react';
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
  workspaces: WorkspaceResponse[];
  projects: EngineeringProjectResponse[];
  recipes: RecipeResponse[];
  stations: StationProfileResponse[];
  processDefinitions: ProcessDefinitionSummary[];
}

interface EngineeringDraft {
  workspaceId: string;
  workspaceName: string;
  projectId: string;
  projectName: string;
  recipeId: string;
  recipeVersionId: string;
  recipeName: string;
  stationProfileId: string;
  stationName: string;
  deviceBindingId: string;
  capabilityId: string;
  deviceKey: string;
  snapshotId: string;
  processDefinitionId: string;
  processVersionId: string;
}

const emptyResources: EngineeringResources = {
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
  const [resources, setResources] = useState<EngineeringResources>(emptyResources);
  const [draft, setDraft] = useState<EngineeringDraft>(() => createEngineeringDraft());
  const [createdProject, setCreatedProject] = useState<EngineeringProjectResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const projectApplicationApiScope = useMemo(() => {
    const application = activeWorkspace?.project.applications.find(
      candidate => candidate.applicationId === activeApplicationId)
      ?? activeWorkspace?.project.applications[0];
    return activeWorkspace && application
      ? {
        projectId: activeWorkspace.project.projectId,
        applicationId: application.applicationId
      }
      : undefined;
  }, [activeApplicationId, activeWorkspace]);

  const publishedProcesses = useMemo(
    () => resources.processDefinitions.filter(definition => definition.status === 'Published'),
    [resources.processDefinitions]);
  const selectedProcess = useMemo(
    () => publishedProcesses.find(definition => definition.processDefinitionId === draft.processDefinitionId)
      ?? publishedProcesses[0]
      ?? null,
    [draft.processDefinitionId, publishedProcesses]);
  const canPublishSnapshot = isBackendHealthy && !busy && selectedProcess !== null;

  const loadResources = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    const [workspaces, projects, recipes, stations, processDefinitions] = await Promise.all([
      listWorkspaces(),
      listEngineeringProjects(),
      listRecipes(),
      listStationProfiles(),
      listProcessDefinitions(projectApplicationApiScope)
    ]);

    setResources({
      workspaces,
      projects,
      recipes,
      stations,
      processDefinitions
    });

    const firstPublishedProcess = processDefinitions.find(definition => definition.status === 'Published');
    if (firstPublishedProcess) {
      setDraft(current => current.processDefinitionId
        ? current
        : {
          ...current,
          processDefinitionId: firstPublishedProcess.processDefinitionId,
          processVersionId: firstPublishedProcess.versionId
        });
    }
  }, [isBackendHealthy, projectApplicationApiScope]);

  useEffect(() => {
    loadResources().catch(error => onMessage(`Engineering load failed: ${String(error)}`));
  }, [loadResources, onMessage]);

  const resetDraft = useCallback(() => {
    setDraft(createEngineeringDraft(selectedProcess ?? undefined));
    setCreatedProject(null);
  }, [selectedProcess]);

  const createRuntimeSnapshot = useCallback(async () => {
    const process = selectedProcess;
    if (!process) {
      onMessage('Publish a process definition before creating an engineering snapshot');
      return;
    }

    setBusy(true);
    setCreatedProject(null);
    try {
      const workspace = await createWorkspace({
        workspaceId: draft.workspaceId,
        displayName: draft.workspaceName
      });
      if (!workspace.ok) {
        onMessage(`Workspace create failed: ${workspace.status} ${workspace.text}`);
        return;
      }

      const project = await createEngineeringProject({
        projectId: draft.projectId,
        workspaceId: draft.workspaceId,
        displayName: draft.projectName
      });
      if (!project.ok) {
        onMessage(`Project create failed: ${project.status} ${project.text}`);
        return;
      }

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
      });
      if (!recipe.ok) {
        onMessage(`Recipe create failed: ${recipe.status} ${recipe.text}`);
        return;
      }

      const publishedRecipe = await publishRecipe(draft.recipeId);
      if (!publishedRecipe.ok) {
        onMessage(`Recipe publish failed: ${publishedRecipe.status} ${publishedRecipe.text}`);
        return;
      }

      const station = await createStationProfile({
        stationProfileId: draft.stationProfileId,
        displayName: draft.stationName,
        deviceBindings: [
          {
            deviceBindingId: draft.deviceBindingId,
            capabilityId: draft.capabilityId,
            deviceKey: draft.deviceKey
          }
        ]
      });
      if (!station.ok) {
        onMessage(`Station create failed: ${station.status} ${station.text}`);
        return;
      }

      const snapshot = await publishConfigurationSnapshot(draft.projectId, {
        snapshotId: draft.snapshotId,
        processDefinitionId: process.processDefinitionId,
        processVersionId: process.versionId,
        recipeId: draft.recipeId,
        stationProfileId: draft.stationProfileId
      });
      if (!snapshot.ok || !snapshot.body) {
        onMessage(`Snapshot publish failed: ${snapshot.status} ${snapshot.text}`);
        return;
      }

      setCreatedProject(snapshot.body);
      onMessage(`Snapshot published ${draft.snapshotId}`);
      await loadResources();
    } finally {
      setBusy(false);
    }
  }, [draft, loadResources, onMessage, selectedProcess]);

  return (
    <section className="engineering-workbench">
      <div className="panel engineering-builder-panel">
        <div className="panel-title">
          <div>
            <Factory size={17} />
            <h2>Engineering Configuration</h2>
          </div>
          <span>{resources.projects.length} projects</span>
        </div>

        <div className="engineering-toolbar">
          <button type="button" className="button ghost" onClick={loadResources} disabled={!isBackendHealthy || busy}>
            <RefreshCw size={15} />
            Refresh
          </button>
          <button type="button" className="button ghost" onClick={resetDraft} disabled={busy}>
            <SlidersHorizontal size={15} />
            New Seed
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
            <FieldGroup title="Workspace">
              <TextField
                label="Workspace ID"
                value={draft.workspaceId}
                onChange={value => setDraft(current => ({ ...current, workspaceId: value }))}
              />
              <TextField
                label="Display Name"
                value={draft.workspaceName}
                onChange={value => setDraft(current => ({ ...current, workspaceName: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Project">
              <TextField
                label="Project ID"
                value={draft.projectId}
                onChange={value => setDraft(current => ({ ...current, projectId: value }))}
              />
              <TextField
                label="Display Name"
                value={draft.projectName}
                onChange={value => setDraft(current => ({ ...current, projectName: value }))}
              />
            </FieldGroup>

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

            <FieldGroup title="Station">
              <TextField
                label="Station Profile ID"
                value={draft.stationProfileId}
                onChange={value => setDraft(current => ({ ...current, stationProfileId: value }))}
              />
              <TextField
                label="Capability"
                value={draft.capabilityId}
                onChange={value => setDraft(current => ({ ...current, capabilityId: value }))}
              />
              <TextField
                label="Device Key"
                value={draft.deviceKey}
                onChange={value => setDraft(current => ({ ...current, deviceKey: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Snapshot">
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
              icon={FolderTree}
              title="Workspaces"
              count={resources.workspaces.length}
              rows={resources.workspaces.map(workspace => ({
                id: workspace.workspaceId,
                title: workspace.displayName,
                meta: formatDate(workspace.createdAtUtc)
              }))}
            />
            <ResourceColumn
              icon={Layers3}
              title="Projects"
              count={resources.projects.length}
              rows={resources.projects.map(project => ({
                id: project.projectId,
                title: project.displayName,
                meta: project.activeSnapshotId ?? 'no active snapshot'
              }))}
            />
            <ResourceColumn
              icon={SlidersHorizontal}
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
              title="Stations"
              count={resources.stations.length}
              rows={resources.stations.map(station => ({
                id: station.stationProfileId,
                title: station.displayName,
                meta: `${station.deviceBindings.length} bindings`
              }))}
            />
          </div>
        </div>
      </div>

      <div className="panel engineering-snapshot-panel">
        <div className="panel-title">
          <div>
            <GitBranch size={17} />
            <h2>Snapshot Result</h2>
          </div>
          <span>{createdProject?.activeSnapshotId ?? 'waiting'}</span>
        </div>
        {createdProject ? (
          <SnapshotResult project={createdProject} />
        ) : (
          <div className="engineering-empty">
            <p>Select a published process and publish a configuration snapshot.</p>
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

function createEngineeringDraft(process?: ProcessDefinitionSummary): EngineeringDraft {
  const seed = Date.now().toString(36);

  return {
    workspaceId: `workspace-desktop-${seed}`,
    workspaceName: 'Desktop Engineering Workspace',
    projectId: `project-desktop-${seed}`,
    projectName: 'Desktop Runtime Project',
    recipeId: `recipe-desktop-${seed}`,
    recipeVersionId: `recipe-desktop-${seed}@1.0.0`,
    recipeName: 'Desktop Runtime Recipe',
    stationProfileId: `station-desktop-${seed}`,
    stationName: 'Desktop Runtime Station',
    deviceBindingId: 'loopback-primary',
    capabilityId: 'device.loopback',
    deviceKey: 'loopback-01',
    snapshotId: `snapshot-desktop-${seed}`,
    processDefinitionId: process?.processDefinitionId ?? '',
    processVersionId: process?.versionId ?? ''
  };
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}
