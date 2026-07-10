import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Boxes,
  ChevronRight,
  Clock3,
  FileJson,
  Folder,
  FolderKanban,
  FolderOpen,
  FolderPlus,
  GitBranch,
  RefreshCw,
  Save,
  Search,
  X
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
  addProjectApplication,
  createAutomationProjectWorkspace,
  importProjectApplication,
  listAutomationProjects,
  openAutomationProjectWorkspace,
  saveAutomationProjectManifest
} from './api';
import { TopologyDesigner } from './topology-designer';

interface ProjectsWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  statusMessage: string;
  onActiveApplicationChanged(applicationId: string): void;
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
}

interface ProjectDraft {
  projectId: string;
  displayName: string;
  projectPath: string;
  defaultApplicationId: string;
  defaultApplicationName: string;
}

interface ApplicationDraft {
  applicationId: string;
  displayName: string;
}

interface RecentProjectEntry {
  projectPath: string;
  lastOpenedAtUtc: string | null;
  projectId?: string;
  displayName?: string;
  activeSnapshotId?: string | null;
}

interface StartProjectItem {
  projectPath: string;
  projectId: string | null;
  displayName: string;
  activeSnapshotId: string | null;
  lastOpenedAtUtc: string | null;
}

interface StartProjectGroup {
  label: string;
  items: StartProjectItem[];
}

type StartDialog = 'new-project' | 'open-path' | null;

const recentProjectsStorageKey = 'openlineops.recentProjects.v2';

export function ProjectsWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  statusMessage,
  onActiveApplicationChanged,
  onWorkspaceChanged,
  onMessage
}: ProjectsWorkbenchProps): React.ReactElement {
  const [draft, setDraft] = useState<ProjectDraft>(() => createProjectDraft());
  const [openPath, setOpenPath] = useState('');
  const [applicationDraft, setApplicationDraft] = useState<ApplicationDraft>(() => createApplicationDraft());
  const [projects, setProjects] = useState<AutomationProjectSummaryResponse[]>([]);
  const [recentProjects, setRecentProjects] = useState<RecentProjectEntry[]>(() => readRecentProjects());
  const [showStartCenter, setShowStartCenter] = useState(activeWorkspace === null);
  const [startDialog, setStartDialog] = useState<StartDialog>(null);
  const [projectSearch, setProjectSearch] = useState('');
  const [busy, setBusy] = useState(false);
  const projectSearchRef = useRef<HTMLInputElement>(null);
  const startDialogRef = useRef<HTMLDialogElement>(null);
  const dialogReturnFocusRef = useRef<HTMLElement | null>(null);

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
  const startProjectGroups = useMemo(
    () => buildStartProjectGroups(projects, recentProjects, projectSearch),
    [projectSearch, projects, recentProjects]);

  const refreshProjects = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    setProjects(await listAutomationProjects());
  }, [isBackendHealthy]);

  useEffect(() => {
    refreshProjects().catch(error => onMessage(`Project list failed: ${String(error)}`));
  }, [onMessage, refreshProjects]);

  useEffect(() => {
    if (!activeWorkspace) {
      setShowStartCenter(true);
    }
  }, [activeWorkspace]);

  useEffect(() => {
    if (!showStartCenter) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent): void => {
      if (!startDialog && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        projectSearchRef.current?.focus();
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [showStartCenter, startDialog]);

  const openStartDialog = useCallback((dialog: Exclude<StartDialog, null>) => {
    dialogReturnFocusRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    setStartDialog(dialog);
  }, []);

  const closeStartDialog = useCallback(() => {
    if (!busy) {
      setStartDialog(null);
    }
  }, [busy]);

  useEffect(() => {
    const dialog = startDialogRef.current;
    if (!startDialog || !dialog) {
      return;
    }

    if (!dialog.open) {
      dialog.showModal();
    }
    const focusFrame = window.requestAnimationFrame(() => {
      dialog.querySelector<HTMLElement>('[data-dialog-initial-focus]')?.focus();
    });

    return () => {
      window.cancelAnimationFrame(focusFrame);
      if (dialog.open) {
        dialog.close();
      }
      const returnTarget = dialogReturnFocusRef.current;
      if (returnTarget?.isConnected) {
        returnTarget.focus();
      }
    };
  }, [startDialog]);

  const rememberProject = useCallback((workspace: AutomationProjectWorkspaceResponse) => {
    const next = rememberRecentProject({
      projectPath: workspace.manifestPath,
      projectId: workspace.project.projectId,
      displayName: workspace.project.displayName,
      activeSnapshotId: workspace.project.activeSnapshotId,
      lastOpenedAtUtc: new Date().toISOString()
    });
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

      onWorkspaceChanged(response.body);
      setShowStartCenter(false);
      setStartDialog(null);
      setOpenPath(response.body.project.projectPath);
      rememberProject(response.body);
      onMessage(`Project created ${response.body.project.projectId}`);
      await refreshProjects().catch(error => onMessage(`Project list refresh failed: ${String(error)}`));
    } catch (error) {
      onMessage(`Project create failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [draft, onMessage, onWorkspaceChanged, refreshProjects, rememberProject]);

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

      onWorkspaceChanged(response.body);
      setShowStartCenter(false);
      setStartDialog(null);
      setOpenPath(response.body.project.projectPath);
      rememberProject(response.body);
      onMessage(`Project opened ${response.body.project.projectId}`);
      await refreshProjects().catch(error => onMessage(`Project list refresh failed: ${String(error)}`));
    } catch (error) {
      onMessage(`Project open failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [onMessage, onWorkspaceChanged, openPath, refreshProjects, rememberProject]);

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

      onWorkspaceChanged(response.body);
      rememberProject(response.body);
      onMessage(`Manifest saved ${response.body.manifestPath}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, onMessage, onWorkspaceChanged, rememberProject]);

  const addApplication = useCallback(async () => {
    if (!activeWorkspace) {
      return;
    }

    const applicationId = applicationDraft.applicationId.trim();
    const displayName = applicationDraft.displayName.trim();
    if (!applicationId || !displayName) {
      onMessage('Application id and display name are required');
      return;
    }

    setBusy(true);
    try {
      const response = await addProjectApplication(
        activeWorkspace.project.projectId,
        { applicationId, displayName });
      if (!response.ok || !response.body) {
        onMessage(`Application create failed: ${response.status} ${response.text}`);
        return;
      }

      const savedWorkspace = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
      if (!savedWorkspace.ok || !savedWorkspace.body) {
        onMessage(`Application manifest save failed: ${savedWorkspace.status} ${savedWorkspace.text}`);
        return;
      }

      onWorkspaceChanged(savedWorkspace.body);
      onActiveApplicationChanged(applicationId);
      setApplicationDraft(createApplicationDraft());
      onMessage(`Application created ${applicationId}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, applicationDraft, onActiveApplicationChanged, onMessage, onWorkspaceChanged]);

  const importApplication = useCallback(async () => {
    if (!activeWorkspace) {
      return;
    }

    const selected = await desktop.selectApplicationProjectFile({
      title: 'Add existing OpenLineOps Application',
      defaultPath: `${activeWorkspace.project.projectPath}\\applications`,
      buttonLabel: 'Add Application'
    });
    if (selected.canceled || !selected.path) {
      return;
    }

    setBusy(true);
    try {
      const existingIds = new Set(activeWorkspace.project.applications.map(application => application.applicationId));
      const response = await importProjectApplication(
        activeWorkspace.project.projectId,
        { projectFilePath: selected.path });
      if (!response.ok || !response.body) {
        onMessage(`Application import failed: ${response.status} ${response.text}`);
        return;
      }

      const imported = response.body.project.applications.find(application => !existingIds.has(application.applicationId));
      onWorkspaceChanged(response.body);
      rememberProject(response.body);
      if (imported) {
        onActiveApplicationChanged(imported.applicationId);
      }
      onMessage(`Application imported ${imported?.applicationId ?? selected.path}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, onActiveApplicationChanged, onMessage, onWorkspaceChanged, rememberProject]);

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

  const chooseAndOpenDirectory = useCallback(async () => {
    const result = await desktop.selectDirectory({
      title: 'Open automation project folder',
      defaultPath: openPath || draft.projectPath,
      buttonLabel: 'Open Project'
    });
    if (!result.canceled && result.path) {
      setOpenPath(result.path);
      await openWorkspace(result.path);
    }
  }, [draft.projectPath, openPath, openWorkspace]);

  const chooseAndOpenProjectFile = useCallback(async () => {
    const result = await desktop.selectProjectFile({
      title: 'Open OpenLineOps project',
      defaultPath: openPath || draft.projectPath,
      buttonLabel: 'Open Project'
    });
    if (!result.canceled && result.path) {
      setOpenPath(result.path);
      await openWorkspace(result.path);
    }
  }, [draft.projectPath, openPath, openWorkspace]);

  const newSeed = useCallback(() => {
    const nextDraft = createProjectDraft();
    setDraft(nextDraft);
    openStartDialog('new-project');
  }, [openStartDialog]);

  if (activeWorkspace && !showStartCenter) {
    return (
      <section className="projects-workbench project-overview-workbench" data-testid="project-workspace-panel">
        <div className="panel project-overview-panel">
          <div className="panel-title">
            <div>
              <FileJson size={17} />
              <h2>Project Explorer</h2>
            </div>
            <span>{activeWorkspace.project.projectId}</span>
          </div>

          <div className="projects-toolbar project-overview-toolbar">
            <button
              type="button"
              className="button ghost"
              onClick={() => setShowStartCenter(true)}
              data-testid="switch-project-workspace"
            >
              <FolderOpen size={15} />
              Open Another Project
            </button>
            <button
              type="button"
              className="button primary"
              onClick={saveManifest}
              disabled={!canCallBackend}
              data-testid="save-project-manifest"
            >
              <Save size={15} />
              Save Project
            </button>
          </div>

          <div className="project-overview-content">
            <aside className="project-overview-summary">
              <ProjectIdentity workspace={activeWorkspace} />
              <ManifestFacts rows={manifestRows} />
              <ProjectApplications
                applications={activeApplications}
                activeApplicationId={activeApplicationId}
                onSelect={onActiveApplicationChanged}
              />
              <div className="project-application-add">
                <input
                  value={applicationDraft.applicationId}
                  onChange={event => setApplicationDraft(current => ({
                    ...current,
                    applicationId: event.target.value
                  }))}
                  placeholder="application id"
                  aria-label="New application id"
                  data-testid="new-application-id"
                />
                <input
                  value={applicationDraft.displayName}
                  onChange={event => setApplicationDraft(current => ({
                    ...current,
                    displayName: event.target.value
                  }))}
                  placeholder="display name"
                  aria-label="New application display name"
                  data-testid="new-application-name"
                />
                <button
                  type="button"
                  className="button"
                  onClick={addApplication}
                  disabled={!canCallBackend}
                  data-testid="add-project-application"
                >
                  <FolderPlus size={14} />
                  Add Application
                </button>
                <button
                  type="button"
                  className="button ghost"
                  onClick={() => { void importApplication(); }}
                  disabled={!canCallBackend}
                  title="Copy an Application folder into this project's applications directory, then add its .oloapp file"
                  data-testid="import-project-application"
                >
                  <FolderOpen size={14} />
                  Add Existing .oloapp
                </button>
              </div>
              <ProjectSnapshots snapshots={activeSnapshots} />
            </aside>
            <section className="project-overview-designer">
              <TopologyDesigner
                activeWorkspace={activeWorkspace}
                activeApplicationId={activeApplicationId}
                isBackendHealthy={isBackendHealthy}
                onWorkspaceChanged={onWorkspaceChanged}
                onMessage={onMessage}
              />
            </section>
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className="projects-workbench project-start-center" data-testid="project-workspace-panel">
      <div className="project-start-shell">
        <header className="project-start-header">
          <div className="project-start-product-mark">OL</div>
          <div>
            <span>OPENLINEOPS STUDIO</span>
            <h1>Automation Projects</h1>
            <p>Build, validate, publish, and operate automation lines.</p>
          </div>
          <div className={isBackendHealthy ? 'project-start-runtime healthy' : 'project-start-runtime'}>
            <i />
            <span>Runtime {isBackendHealthy ? 'ready' : 'offline'}</span>
          </div>
        </header>

        <div className="project-start-status" role="status" aria-live="polite">
          {statusMessage}
        </div>

        <div className="project-start-main">
          <section className="project-start-recent-pane" aria-label="Recent automation projects">
            <div className="project-start-section-heading">
              <div>
                <h2>Recent projects</h2>
                <span>{startProjectGroups.reduce((count, group) => count + group.items.length, 0)} shown</span>
              </div>
              <button
                type="button"
                className="project-start-icon-button"
                onClick={() => {
                  void refreshProjects().catch(error => onMessage(`Project list failed: ${String(error)}`));
                }}
                disabled={!canCallBackend}
                title="Refresh indexed projects"
              >
                <RefreshCw size={15} />
              </button>
            </div>

            <label className="project-start-search">
              <Search size={16} />
              <input
                ref={projectSearchRef}
                value={projectSearch}
                onChange={event => setProjectSearch(event.target.value)}
                placeholder="Search by project name, id, or path"
                aria-label="Search recent projects"
                data-testid="project-search"
              />
              {projectSearch ? (
                <button type="button" onClick={() => setProjectSearch('')} title="Clear search">
                  <X size={14} />
                </button>
              ) : <kbd>Ctrl K</kbd>}
            </label>

            <div className="project-start-projects">
              {startProjectGroups.length === 0 ? (
                <div className="project-start-empty">
                  <FolderOpen size={26} />
                  <strong>{projectSearch ? 'No matching projects' : 'No recent projects yet'}</strong>
                  <span>{projectSearch ? 'Try a different name or path.' : 'Create a project or open an existing project folder.'}</span>
                </div>
              ) : startProjectGroups.map(group => (
                <section className="project-start-group" key={group.label}>
                  <h3>{group.label}</h3>
                  {group.items.map(item => (
                    <button
                      type="button"
                      className="project-start-project-row"
                      key={item.projectPath}
                      onClick={() => {
                        setOpenPath(item.projectPath);
                        void openWorkspace(item.projectPath);
                      }}
                      disabled={!canCallBackend}
                    >
                      <FolderKanban size={20} />
                      <span>
                        <strong>{item.displayName}</strong>
                        <small>{item.projectPath}</small>
                      </span>
                      <span className="project-start-project-meta">
                        {item.lastOpenedAtUtc ? <time>{formatRecentTime(item.lastOpenedAtUtc)}</time> : null}
                        <small>{item.activeSnapshotId ? 'Published' : item.projectId ?? 'Project folder'}</small>
                      </span>
                      <ChevronRight size={16} />
                    </button>
                  ))}
                </section>
              ))}
            </div>
          </section>

          <aside className="project-start-command-pane" aria-label="Start actions">
            <div className="project-start-section-heading">
              <div>
                <h2>Start</h2>
                <span>Choose what you want to do</span>
              </div>
            </div>

            <div className="project-start-commands">
              <button type="button" onClick={newSeed} disabled={busy} data-testid="start-create-project">
                <FolderPlus size={20} />
                <span>
                  <strong>Create a new project</strong>
                  <small>Start from an automation-line project template</small>
                </span>
                <ChevronRight size={16} />
              </button>
              <button
                type="button"
                onClick={() => { void chooseAndOpenProjectFile(); }}
                disabled={!canCallBackend}
                data-testid="start-open-project-file"
              >
                <FileJson size={20} />
                <span>
                  <strong>Open a project</strong>
                  <small>Select an OpenLineOps .oloproj project file</small>
                </span>
                <ChevronRight size={16} />
              </button>
              <button
                type="button"
                onClick={() => { void chooseAndOpenDirectory(); }}
                disabled={!canCallBackend}
                data-testid="start-open-project-folder"
              >
                <FolderOpen size={20} />
                <span>
                  <strong>Open a project folder</strong>
                  <small>Select a self-contained OpenLineOps project</small>
                </span>
                <ChevronRight size={16} />
              </button>
              <button
                type="button"
                onClick={() => openStartDialog('open-path')}
                disabled={!canCallBackend}
                data-testid="start-open-project-by-path"
              >
                <Folder size={20} />
                <span>
                  <strong>Open using a path</strong>
                  <small>Paste a local or mapped project directory</small>
                </span>
                <ChevronRight size={16} />
              </button>
            </div>

            {activeWorkspace ? (
              <button
                type="button"
                className="project-start-back-button"
                onClick={() => setShowStartCenter(false)}
                disabled={busy}
              >
                Back to {activeWorkspace.project.displayName}
              </button>
            ) : null}

            <div className="project-start-context-card">
              <Clock3 size={18} />
              <div>
                <strong>Project source stays portable</strong>
                <span>Topology, layout, Blockly, Python, blocks, and configuration live with the project.</span>
              </div>
            </div>
          </aside>
        </div>

        <footer className="project-start-footer">
          <span>Text project source</span>
          <span>Blockly-first flow design</span>
          <span>Controlled Python extension</span>
          <b>{recentProjects.length} recent · {projects.length} indexed</b>
        </footer>
      </div>

      {startDialog ? (
          <dialog
            ref={startDialogRef}
            className="project-start-dialog"
            aria-labelledby="project-start-dialog-title"
            aria-busy={busy}
            onCancel={event => {
              event.preventDefault();
              closeStartDialog();
            }}
          >
            <header>
              <div>
                <span>{startDialog === 'new-project' ? 'PROJECT TEMPLATE' : 'OPEN PROJECT'}</span>
                <h2 id="project-start-dialog-title">
                  {startDialog === 'new-project' ? 'Create an automation project' : 'Open a project by path'}
                </h2>
              </div>
              <button type="button" onClick={closeStartDialog} disabled={busy} title="Close" aria-label="Close dialog">
                <X size={17} />
              </button>
            </header>

            {startDialog === 'new-project' ? (
              <div className="project-start-dialog-body project-start-new-project">
                <aside className="project-template-card selected">
                  <FolderKanban size={24} />
                  <strong>Automation Line Project</strong>
                  <span>Root .oloproj plus one isolated .oloapp folder for every Application.</span>
                  <small>OPENLINEOPS · PORTABLE PROJECT FORMAT</small>
                </aside>
                <div className="project-start-dialog-form">
                  <TextField
                    label="Project ID"
                    value={draft.projectId}
                    initialFocus
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
                  <div className="project-application-fields">
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
                  </div>
                </div>
              </div>
            ) : (
              <div className="project-start-dialog-body project-start-open-path">
                <p>Open an <code>.oloproj</code> file or its containing project folder.</p>
                <PathField
                  label="Project Path"
                  value={openPath}
                  initialFocus
                  testId="open-project-directory"
                  inputTestId="open-project-path-input"
                  onBrowse={chooseOpenDirectory}
                  onChange={setOpenPath}
                />
              </div>
            )}

            <footer>
              <button type="button" className="button ghost" onClick={closeStartDialog} disabled={busy}>
                Cancel
              </button>
              {startDialog === 'new-project' ? (
                <button
                  type="button"
                  className="button primary"
                  onClick={createWorkspace}
                  disabled={!canCallBackend}
                  data-testid="create-project-workspace"
                >
                  <FolderPlus size={15} />
                  Create Project
                </button>
              ) : (
                <button
                  type="button"
                  className="button primary"
                  onClick={() => { void openWorkspace(); }}
                  disabled={!canCallBackend}
                  data-testid="open-project-workspace"
                >
                  <FolderOpen size={15} />
                  Open Project
                </button>
              )}
            </footer>
          </dialog>
      ) : null}
    </section>
  );
}

function TextField({
  label,
  value,
  initialFocus = false,
  onChange
}: {
  label: string;
  value: string;
  initialFocus?: boolean;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input
        value={value}
        onChange={event => onChange(event.target.value)}
        data-dialog-initial-focus={initialFocus ? true : undefined}
      />
    </label>
  );
}

function PathField({
  label,
  value,
  testId,
  inputTestId,
  initialFocus = false,
  onBrowse,
  onChange
}: {
  label: string;
  value: string;
  testId: string;
  inputTestId: string;
  initialFocus?: boolean;
  onBrowse(): void;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <div className="path-field">
        <input
          value={value}
          onChange={event => onChange(event.target.value)}
          data-testid={inputTestId}
          data-dialog-initial-focus={initialFocus ? true : undefined}
        />
        <button type="button" className="icon-button" onClick={onBrowse} title="Browse" data-testid={testId}>
          <Folder size={15} />
        </button>
      </div>
    </label>
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
  applications,
  activeApplicationId,
  onSelect
}: {
  applications: ProjectApplicationResponse[];
  activeApplicationId: string | null;
  onSelect(applicationId: string): void;
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
        <button
          type="button"
          className={application.applicationId === activeApplicationId ? 'active' : ''}
          key={application.applicationId}
          onClick={() => onSelect(application.applicationId)}
        >
          <strong>{application.displayName}</strong>
          <span>{application.applicationId}</span>
          <small>{application.projectFilePath ?? `${application.processDefinitionIds.length} processes`}</small>
        </button>
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
          <span>{snapshot.processVersionId} · {snapshot.layoutIds.length} layouts</span>
          <small>
            Release {snapshot.releaseContentSha256.slice(0, 12)} · {formatDate(snapshot.publishedAtUtc)}
          </small>
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

function createApplicationDraft(): ApplicationDraft {
  const seed = Date.now().toString(36);
  return {
    applicationId: `application-${seed}`,
    displayName: `Application ${seed.toUpperCase()}`
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

function buildStartProjectGroups(
  projects: AutomationProjectSummaryResponse[],
  recentProjects: RecentProjectEntry[],
  search: string
): StartProjectGroup[] {
  const projectsByPath = new Map(
    projects.map(project => [normalizeProjectPath(project.projectPath), project] as const));
  const projectsById = new Map(projects.map(project => [project.projectId, project] as const));
  const seenPaths = new Set<string>();
  const seenProjectIds = new Set<string>();
  const items: StartProjectItem[] = [];

  for (const recent of recentProjects) {
    const pathKey = normalizeProjectPath(recent.projectPath);
    if (!pathKey || seenPaths.has(pathKey)) {
      continue;
    }

    seenPaths.add(pathKey);
    const project = (recent.projectId ? projectsById.get(recent.projectId) : undefined)
      ?? projectsByPath.get(pathKey);
    const projectId = project?.projectId ?? recent.projectId ?? null;
    if (projectId && seenProjectIds.has(projectId)) {
      continue;
    }

    if (projectId) {
      seenProjectIds.add(projectId);
    }
    items.push({
      projectPath: recent.projectPath,
      projectId,
      displayName: project?.displayName ?? recent.displayName ?? projectFolderName(recent.projectPath),
      activeSnapshotId: project?.activeSnapshotId ?? recent.activeSnapshotId ?? null,
      lastOpenedAtUtc: recent.lastOpenedAtUtc
    });
  }

  for (const project of projects) {
    const pathKey = normalizeProjectPath(project.projectPath);
    if (seenPaths.has(pathKey) || seenProjectIds.has(project.projectId)) {
      continue;
    }

    seenPaths.add(pathKey);
    seenProjectIds.add(project.projectId);
    items.push({
      projectPath: project.projectPath,
      projectId: project.projectId,
      displayName: project.displayName,
      activeSnapshotId: project.activeSnapshotId,
      lastOpenedAtUtc: null
    });
  }

  const normalizedSearch = search.trim().toLocaleLowerCase();
  const filtered = normalizedSearch
    ? items.filter(item => [item.displayName, item.projectId ?? '', item.projectPath]
      .some(value => value.toLocaleLowerCase().includes(normalizedSearch)))
    : items;
  const today: StartProjectItem[] = [];
  const previousWeek: StartProjectItem[] = [];
  const earlier: StartProjectItem[] = [];
  const indexed: StartProjectItem[] = [];
  const now = new Date();
  const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const sevenDaysAgo = todayStart - (6 * 24 * 60 * 60 * 1000);

  for (const item of filtered) {
    if (!item.lastOpenedAtUtc) {
      indexed.push(item);
      continue;
    }

    const openedAt = new Date(item.lastOpenedAtUtc).getTime();
    if (!Number.isFinite(openedAt)) {
      earlier.push(item);
    } else if (openedAt >= todayStart) {
      today.push(item);
    } else if (openedAt >= sevenDaysAgo) {
      previousWeek.push(item);
    } else {
      earlier.push(item);
    }
  }

  return [
    { label: 'Today', items: today },
    { label: 'Previous 7 days', items: previousWeek },
    { label: 'Earlier', items: earlier },
    { label: 'Indexed in this session', items: indexed }
  ].filter(group => group.items.length > 0);
}

function normalizeProjectPath(projectPath: string): string {
  return projectPath.trim().replace(/[\\/]+$/, '').toLocaleLowerCase();
}

function projectFolderName(projectPath: string): string {
  return projectPath
    .trim()
    .split(/[\\/]/)
    .filter(Boolean)
    .at(-1)
    ?? 'Automation Project';
}

function readRecentProjects(): RecentProjectEntry[] {
  try {
    const value = window.localStorage.getItem(recentProjectsStorageKey);
    const parsed = value ? JSON.parse(value) as unknown : null;
    if (value && Array.isArray(parsed)) {
      return parsed
        .filter((item): item is RecentProjectEntry => Boolean(
          item
          && typeof item === 'object'
          && typeof (item as RecentProjectEntry).projectPath === 'string'
          && (
            (item as RecentProjectEntry).lastOpenedAtUtc === null
            || typeof (item as RecentProjectEntry).lastOpenedAtUtc === 'string'
          )
          && (
            (item as RecentProjectEntry).projectId === undefined
            || typeof (item as RecentProjectEntry).projectId === 'string'
          )
          && (
            (item as RecentProjectEntry).displayName === undefined
            || typeof (item as RecentProjectEntry).displayName === 'string'
          )
          && (
            (item as RecentProjectEntry).activeSnapshotId === undefined
            || (item as RecentProjectEntry).activeSnapshotId === null
            || typeof (item as RecentProjectEntry).activeSnapshotId === 'string'
          )))
        .slice(0, 12);
    }
  } catch {
    return [];
  }

  return [];
}

function rememberRecentProject(entry: RecentProjectEntry): RecentProjectEntry[] {
  const trimmedPath = entry.projectPath.trim();
  if (!trimmedPath) {
    return readRecentProjects();
  }

  const pathKey = normalizeProjectPath(trimmedPath);
  const next = [
    { ...entry, projectPath: trimmedPath },
    ...readRecentProjects().filter(item =>
      normalizeProjectPath(item.projectPath) !== pathKey
      && (!entry.projectId || item.projectId !== entry.projectId))
  ].slice(0, 12);

  try {
    window.localStorage.setItem(recentProjectsStorageKey, JSON.stringify(next));
  } catch {
    // Browser storage can be unavailable in restricted preview environments.
  }

  return next;
}

function formatRecentTime(value: string): string {
  const openedAt = new Date(value);
  if (Number.isNaN(openedAt.getTime())) {
    return 'Recently';
  }

  const now = new Date();
  const sameDay = openedAt.getFullYear() === now.getFullYear()
    && openedAt.getMonth() === now.getMonth()
    && openedAt.getDate() === now.getDate();

  return sameDay
    ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(openedAt)
    : new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' }).format(openedAt);
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}
