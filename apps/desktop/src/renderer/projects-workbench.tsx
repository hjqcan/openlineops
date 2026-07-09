import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Boxes,
  FileJson,
  Folder,
  FolderKanban,
  FolderOpen,
  FolderPlus,
  GitBranch,
  LayoutGrid,
  RefreshCw,
  Save,
  Workflow
} from 'lucide-react';
import { desktop } from './desktop-bridge';
import type {
  AutomationProjectSummaryResponse,
  AutomationProjectWorkspaceResponse,
  CreateAutomationProjectWorkspaceRequest,
  ProjectApplicationResponse,
  PublishedProjectSnapshotResponse
} from './contracts';
import {
  createAutomationProjectWorkspace,
  listAutomationProjects,
  openAutomationProjectWorkspace,
  saveAutomationProjectManifest
} from './api';
import { TopologyDesigner } from './topology-designer';

interface ProjectsWorkbenchProps {
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface ProjectDraft {
  projectId: string;
  displayName: string;
  projectPath: string;
  defaultApplicationId: string;
  defaultApplicationName: string;
}

const recentProjectsStorageKey = 'openlineops.recentProjectPaths.v1';

export function ProjectsWorkbench({
  isBackendHealthy,
  onMessage
}: ProjectsWorkbenchProps): React.ReactElement {
  const [draft, setDraft] = useState<ProjectDraft>(() => createProjectDraft());
  const [openPath, setOpenPath] = useState('');
  const [projects, setProjects] = useState<AutomationProjectSummaryResponse[]>([]);
  const [activeWorkspace, setActiveWorkspace] = useState<AutomationProjectWorkspaceResponse | null>(null);
  const [recentProjects, setRecentProjects] = useState<string[]>(() => readRecentProjects());
  const [busy, setBusy] = useState(false);

  const activeApplications = activeWorkspace?.project.applications ?? [];
  const activeSnapshots = activeWorkspace?.project.snapshots ?? [];
  const canCallBackend = isBackendHealthy && !busy;
  const manifestRows = useMemo(
    () => activeWorkspace
      ? [
        { label: 'Format', value: `v${activeWorkspace.manifest.formatVersion}` },
        { label: 'Product', value: activeWorkspace.manifest.product },
        { label: 'Updated', value: formatDate(activeWorkspace.manifest.updatedAtUtc) },
        { label: 'Manifest', value: activeWorkspace.manifestPath }
      ]
      : [],
    [activeWorkspace]);

  const refreshProjects = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    setProjects(await listAutomationProjects());
  }, [isBackendHealthy]);

  useEffect(() => {
    refreshProjects().catch(error => onMessage(`Project list failed: ${String(error)}`));
  }, [onMessage, refreshProjects]);

  const rememberProject = useCallback((projectPath: string) => {
    const next = rememberRecentProject(projectPath);
    setRecentProjects(next);
  }, []);

  const createWorkspace = useCallback(async () => {
    const request = toCreateRequest(draft);
    if (!request.projectId || !request.displayName || !request.projectPath) {
      onMessage('Project id, display name, and path are required');
      return;
    }

    setBusy(true);
    try {
      const response = await createAutomationProjectWorkspace(request);
      if (!response.ok || !response.body) {
        onMessage(`Project create failed: ${response.status} ${response.text}`);
        return;
      }

      setActiveWorkspace(response.body);
      setOpenPath(response.body.project.projectPath);
      rememberProject(response.body.project.projectPath);
      onMessage(`Project created ${response.body.project.projectId}`);
      await refreshProjects();
    } finally {
      setBusy(false);
    }
  }, [draft, onMessage, refreshProjects, rememberProject]);

  const openWorkspace = useCallback(async (projectPath = openPath) => {
    const normalizedPath = projectPath.trim();
    if (!normalizedPath) {
      onMessage('Project path is required');
      return;
    }

    setBusy(true);
    try {
      const response = await openAutomationProjectWorkspace({ projectPath: normalizedPath });
      if (!response.ok || !response.body) {
        onMessage(`Project open failed: ${response.status} ${response.text}`);
        return;
      }

      setActiveWorkspace(response.body);
      setOpenPath(response.body.project.projectPath);
      rememberProject(response.body.project.projectPath);
      onMessage(`Project opened ${response.body.project.projectId}`);
      await refreshProjects();
    } finally {
      setBusy(false);
    }
  }, [onMessage, openPath, refreshProjects, rememberProject]);

  const saveManifest = useCallback(async () => {
    if (!activeWorkspace) {
      return;
    }

    setBusy(true);
    try {
      const response = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
      if (!response.ok || !response.body) {
        onMessage(`Manifest save failed: ${response.status} ${response.text}`);
        return;
      }

      setActiveWorkspace(response.body);
      rememberProject(response.body.project.projectPath);
      onMessage(`Manifest saved ${response.body.manifestPath}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, onMessage, rememberProject]);

  const chooseCreateDirectory = useCallback(async () => {
    const result = await desktop.selectDirectory({
      title: 'Select project folder',
      defaultPath: draft.projectPath,
      buttonLabel: 'Use Folder',
      createDirectory: true
    });
    if (!result.canceled && result.path) {
      setDraft(current => ({ ...current, projectPath: result.path ?? current.projectPath }));
    }
  }, [draft.projectPath]);

  const chooseOpenDirectory = useCallback(async () => {
    const result = await desktop.selectDirectory({
      title: 'Open project folder',
      defaultPath: openPath || draft.projectPath,
      buttonLabel: 'Open'
    });
    if (!result.canceled && result.path) {
      setOpenPath(result.path);
    }
  }, [draft.projectPath, openPath]);

  const newSeed = useCallback(() => {
    const nextDraft = createProjectDraft();
    setDraft(nextDraft);
    setOpenPath(nextDraft.projectPath);
  }, []);

  return (
    <section className="projects-workbench" data-testid="project-workspace-panel">
      <div className="panel projects-builder-panel">
        <div className="panel-title">
          <div>
            <FolderKanban size={17} />
            <h2>Automation Projects</h2>
          </div>
          <span>{projects.length} indexed</span>
        </div>

        <div className="projects-toolbar">
          <button type="button" className="button ghost" onClick={refreshProjects} disabled={!canCallBackend}>
            <RefreshCw size={15} />
            Refresh
          </button>
          <button type="button" className="button ghost" onClick={newSeed} disabled={busy}>
            <FolderPlus size={15} />
            New Seed
          </button>
          <button
            type="button"
            className="button primary"
            onClick={createWorkspace}
            disabled={!canCallBackend}
            data-testid="create-project-workspace"
          >
            <FolderPlus size={15} />
            Create
          </button>
          <button
            type="button"
            className="button"
            onClick={() => { void openWorkspace(); }}
            disabled={!canCallBackend}
            data-testid="open-project-workspace"
          >
            <FolderOpen size={15} />
            Open
          </button>
          <button
            type="button"
            className="button"
            onClick={saveManifest}
            disabled={!canCallBackend || !activeWorkspace}
            data-testid="save-project-manifest"
          >
            <Save size={15} />
            Save
          </button>
        </div>

        <div className="projects-layout">
          <div className="projects-form">
            <fieldset className="projects-fieldset">
              <legend>New Project</legend>
              <TextField
                label="Project ID"
                value={draft.projectId}
                onChange={value => setDraft(current => ({ ...current, projectId: value }))}
              />
              <TextField
                label="Display Name"
                value={draft.displayName}
                onChange={value => setDraft(current => ({ ...current, displayName: value }))}
              />
              <PathField
                label="Project Path"
                value={draft.projectPath}
                testId="select-project-directory"
                inputTestId="project-path-input"
                onBrowse={chooseCreateDirectory}
                onChange={value => setDraft(current => ({ ...current, projectPath: value }))}
              />
              <TextField
                label="Default Application ID"
                value={draft.defaultApplicationId}
                onChange={value => setDraft(current => ({ ...current, defaultApplicationId: value }))}
              />
              <TextField
                label="Default Application Name"
                value={draft.defaultApplicationName}
                onChange={value => setDraft(current => ({ ...current, defaultApplicationName: value }))}
              />
            </fieldset>

            <fieldset className="projects-fieldset">
              <legend>Open Project</legend>
              <PathField
                label="Project Path"
                value={openPath}
                testId="open-project-directory"
                inputTestId="open-project-path-input"
                onBrowse={chooseOpenDirectory}
                onChange={setOpenPath}
              />
              <RecentProjectsList
                recentProjects={recentProjects}
                onOpen={path => {
                  setOpenPath(path);
                  void openWorkspace(path);
                }}
              />
            </fieldset>
          </div>

          <ProjectResourceGrid
            projects={projects}
            activeWorkspace={activeWorkspace}
            onOpen={path => {
              setOpenPath(path);
              void openWorkspace(path);
            }}
          />
        </div>
      </div>

      <div className="panel projects-detail-panel">
        <div className="panel-title">
          <div>
            <FileJson size={17} />
            <h2>Project Explorer</h2>
          </div>
          <span>{activeWorkspace?.project.projectId ?? 'none'}</span>
        </div>

        {activeWorkspace ? (
          <div className="project-detail">
            <ProjectIdentity workspace={activeWorkspace} />
            <ManifestFacts rows={manifestRows} />
            <ProjectApplications applications={activeApplications} />
            <TopologyDesigner
              activeWorkspace={activeWorkspace}
              isBackendHealthy={isBackendHealthy}
              onWorkspaceChanged={setActiveWorkspace}
              onMessage={onMessage}
            />
            <ProjectSnapshots snapshots={activeSnapshots} />
          </div>
        ) : (
          <div className="projects-empty">No project workspace is open.</div>
        )}
      </div>
    </section>
  );
}

function TextField({
  label,
  value,
  onChange
}: {
  label: string;
  value: string;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input value={value} onChange={event => onChange(event.target.value)} />
    </label>
  );
}

function PathField({
  label,
  value,
  testId,
  inputTestId,
  onBrowse,
  onChange
}: {
  label: string;
  value: string;
  testId: string;
  inputTestId: string;
  onBrowse(): void;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <div className="path-field">
        <input value={value} onChange={event => onChange(event.target.value)} data-testid={inputTestId} />
        <button type="button" className="icon-button" onClick={onBrowse} title="Browse" data-testid={testId}>
          <Folder size={15} />
        </button>
      </div>
    </label>
  );
}

function RecentProjectsList({
  recentProjects,
  onOpen
}: {
  recentProjects: string[];
  onOpen(projectPath: string): void;
}): React.ReactElement {
  return (
    <div className="recent-projects">
      <div>
        <FolderOpen size={14} />
        <strong>Recent</strong>
      </div>
      {recentProjects.length === 0 ? (
        <p>No recent project paths</p>
      ) : recentProjects.map(projectPath => (
        <button type="button" key={projectPath} onClick={() => onOpen(projectPath)}>
          <span>{projectPath}</span>
        </button>
      ))}
    </div>
  );
}

function ProjectResourceGrid({
  projects,
  activeWorkspace,
  onOpen
}: {
  projects: AutomationProjectSummaryResponse[];
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  onOpen(projectPath: string): void;
}): React.ReactElement {
  return (
    <div className="project-resource-grid">
      <section className="project-resource-list">
        <div>
          <FolderKanban size={15} />
          <strong>Indexed Projects</strong>
          <span>{projects.length}</span>
        </div>
        {projects.length === 0 ? (
          <p>No indexed projects</p>
        ) : projects.slice(0, 8).map(project => (
          <button
            type="button"
            key={project.projectId}
            className={activeWorkspace?.project.projectId === project.projectId ? 'selected' : ''}
            onClick={() => onOpen(project.projectPath)}
          >
            <strong>{project.displayName}</strong>
            <span>{project.projectId}</span>
            <small>{project.activeSnapshotId ?? project.projectPath}</small>
          </button>
        ))}
      </section>

      <section className="project-resource-list">
        <div>
          <Boxes size={15} />
          <strong>Composition Targets</strong>
          <span>{activeWorkspace ? 'open' : 'idle'}</span>
        </div>
        <CompositionTarget icon={LayoutGrid} title="Site Layout" value="topology projection" />
        <CompositionTarget icon={Workflow} title="Processes" value="Blockly plus PythonScript" />
        <CompositionTarget icon={GitBranch} title="Published Snapshot" value={activeWorkspace?.project.activeSnapshotId ?? 'none'} />
      </section>
    </div>
  );
}

function CompositionTarget({
  icon: Icon,
  title,
  value
}: {
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  value: string;
}): React.ReactElement {
  return (
    <article className="composition-target">
      <Icon size={16} />
      <div>
        <strong>{title}</strong>
        <span>{value}</span>
      </div>
    </article>
  );
}

function ProjectIdentity({
  workspace
}: {
  workspace: AutomationProjectWorkspaceResponse;
}): React.ReactElement {
  return (
    <div className="project-identity">
      <FolderKanban size={22} />
      <div>
        <strong>{workspace.project.displayName}</strong>
        <span>{workspace.project.projectPath}</span>
      </div>
    </div>
  );
}

function ManifestFacts({
  rows
}: {
  rows: Array<{ label: string; value: string }>;
}): React.ReactElement {
  return (
    <dl className="manifest-facts">
      {rows.map(row => (
        <React.Fragment key={row.label}>
          <dt>{row.label}</dt>
          <dd>{row.value}</dd>
        </React.Fragment>
      ))}
    </dl>
  );
}

function ProjectApplications({
  applications
}: {
  applications: ProjectApplicationResponse[];
}): React.ReactElement {
  return (
    <section className="project-detail-list">
      <div>
        <Boxes size={15} />
        <strong>Applications</strong>
        <span>{applications.length}</span>
      </div>
      {applications.length === 0 ? (
        <p>No applications</p>
      ) : applications.map(application => (
        <article key={application.applicationId}>
          <strong>{application.displayName}</strong>
          <span>{application.applicationId}</span>
          <small>{application.topologyId ?? `${application.processDefinitionIds.length} processes`}</small>
        </article>
      ))}
    </section>
  );
}

function ProjectSnapshots({
  snapshots
}: {
  snapshots: PublishedProjectSnapshotResponse[];
}): React.ReactElement {
  return (
    <section className="project-detail-list">
      <div>
        <GitBranch size={15} />
        <strong>Snapshots</strong>
        <span>{snapshots.length}</span>
      </div>
      {snapshots.length === 0 ? (
        <p>No published snapshots</p>
      ) : snapshots.map(snapshot => (
        <article key={snapshot.snapshotId}>
          <strong>{snapshot.snapshotId}</strong>
          <span>{snapshot.processVersionId}</span>
          <small>{formatDate(snapshot.publishedAtUtc)}</small>
        </article>
      ))}
    </section>
  );
}

function createProjectDraft(): ProjectDraft {
  const seed = Date.now().toString(36);
  const projectId = `project-${seed}`;

  return {
    projectId,
    displayName: `Automation Project ${seed.toUpperCase()}`,
    projectPath: `C:\\OpenLineOps\\Projects\\${projectId}`,
    defaultApplicationId: `application-${seed}`,
    defaultApplicationName: 'Default Application'
  };
}

function toCreateRequest(draft: ProjectDraft): CreateAutomationProjectWorkspaceRequest {
  return {
    projectId: draft.projectId.trim(),
    displayName: draft.displayName.trim(),
    projectPath: draft.projectPath.trim(),
    defaultApplicationId: toOptionalString(draft.defaultApplicationId),
    defaultApplicationName: toOptionalString(draft.defaultApplicationName)
  };
}

function toOptionalString(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function readRecentProjects(): string[] {
  try {
    const value = window.localStorage.getItem(recentProjectsStorageKey);
    const parsed = value ? JSON.parse(value) as unknown : [];
    return Array.isArray(parsed)
      ? parsed.filter((item): item is string => typeof item === 'string').slice(0, 8)
      : [];
  } catch {
    return [];
  }
}

function rememberRecentProject(projectPath: string): string[] {
  const trimmedPath = projectPath.trim();
  if (!trimmedPath) {
    return readRecentProjects();
  }

  const next = [
    trimmedPath,
    ...readRecentProjects().filter(item => item !== trimmedPath)
  ].slice(0, 8);

  try {
    window.localStorage.setItem(recentProjectsStorageKey, JSON.stringify(next));
  } catch {
    // Browser storage can be unavailable in restricted preview environments.
  }

  return next;
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}
