import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity,
  AlertTriangle,
  Blocks,
  CheckCircle2,
  CirclePlay,
  Code2,
  Database,
  FileSearch,
  FolderKanban,
  GitBranch,
  Gauge,
  Layers3,
  LayoutDashboard,
  MonitorCog,
  Package,
  PanelBottom,
  Play,
  PlugZap,
  RefreshCw,
  Settings,
  Square,
  X,
  Zap
} from 'lucide-react';
import type { BackendStatus, DesktopConfig } from '../shared/desktop-api';
import type {
  AutomationProjectWorkspaceResponse,
  PlatformResponse,
  PublishedProjectSnapshotResponse,
  RuntimeAlarm,
  RuntimeSessionRunResponse,
  RuntimeStationStatus,
  RuntimeTimelineEntry,
  StartedProjectSnapshotRuntimeSessionResponse,
  TraceRecordSummary
} from './contracts';
import { desktop } from './desktop-bridge';
import {
  acknowledgeAlarm,
  createRuntimeHubConnection,
  getAlarms,
  getHealth,
  getPlatform,
  getStationStatuses,
  getTimeline,
  getTraceRecords,
  startProjectSnapshotRuntimeSession,
  startDemoRuntimeSession
} from './api';
import { DevicesWorkbench } from './devices-workbench';
import { EngineeringWorkbench } from './engineering-workbench';
import { PluginsWorkbench } from './plugins-workbench';
import { ProjectsWorkbench } from './projects-workbench';
import { TraceWorkbench } from './trace-workbench';
import './styles.css';

const ProcessWorkbench = React.lazy(async () => {
  const module = await import('./process-workbench');
  return { default: module.ProcessWorkbench };
});

const navItems = [
  { id: 'projects', label: 'Explorer', icon: FolderKanban },
  { id: 'processes', label: 'Flow Designer', icon: Blocks },
  { id: 'engineering', label: 'Configuration', icon: MonitorCog },
  { id: 'devices', label: 'Devices', icon: PlugZap },
  { id: 'dashboard', label: 'Run and Monitor', icon: Play },
  { id: 'trace', label: 'Traceability', icon: FileSearch },
  { id: 'plugins', label: 'Extensions', icon: Package }
] as const;

type NavId = (typeof navItems)[number]['id'];
type HubState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting';

declare global {
  interface Window {
    __openlineopsSmokeEvents?: Record<string, number>;
  }
}

function App(): React.ReactElement {
  const [activeNav, setActiveNav] = useState<NavId>('projects');
  const [workspaceMode, setWorkspaceMode] = useState<'edit' | 'run'>('edit');
  const [config, setConfig] = useState<DesktopConfig | null>(null);
  const [backendStatus, setBackendStatus] = useState<BackendStatus | null>(null);
  const [platform, setPlatform] = useState<PlatformResponse | null>(null);
  const [healthStatus, setHealthStatus] = useState('Unknown');
  const [hubState, setHubState] = useState<HubState>('Disconnected');
  const [stations, setStations] = useState<RuntimeStationStatus[]>([]);
  const [timeline, setTimeline] = useState<RuntimeTimelineEntry[]>([]);
  const [alarms, setAlarms] = useState<RuntimeAlarm[]>([]);
  const [traceRows, setTraceRows] = useState<TraceRecordSummary[]>([]);
  const [lastRun, setLastRun] = useState<RuntimeSessionRunResponse | null>(null);
  const [lastProjectRun, setLastProjectRun] =
    useState<StartedProjectSnapshotRuntimeSessionResponse | null>(null);
  const [activeWorkspace, setActiveWorkspace] = useState<AutomationProjectWorkspaceResponse | null>(null);
  const [activeApplicationId, setActiveApplicationId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('Ready');

  const activeApplication = useMemo(
    () => activeWorkspace?.project.applications.find(
      application => application.applicationId === activeApplicationId)
      ?? activeWorkspace?.project.applications[0]
      ?? null,
    [activeApplicationId, activeWorkspace]);
  const activeApplicationSnapshot = useMemo(
    () => selectApplicationSnapshot(activeWorkspace, activeApplication?.applicationId ?? null),
    [activeApplication?.applicationId, activeWorkspace]);

  const latestStation = stations[0] ?? null;
  const activeNavLabel = navItems.find(item => item.id === activeNav)?.label ?? 'Explorer';
  const activeTitle = activeNav === 'dashboard'
    ? 'Station Runtime Dashboard'
    : activeNav === 'projects'
      ? activeWorkspace?.project.displayName ?? 'Automation Projects'
      : activeNavLabel;

  const refresh = useCallback(async () => {
    const [desktopConfig, status] = await Promise.all([
      desktop.getConfig(),
      desktop.getBackendStatus()
    ]);
    setConfig(desktopConfig);
    setBackendStatus(status);

    const [platformResponse, healthResponse] = await Promise.allSettled([
      getPlatform(),
      getHealth()
    ]);

    if (platformResponse.status === 'fulfilled' && platformResponse.value.body) {
      setPlatform(platformResponse.value.body);
    }

    if (healthResponse.status === 'fulfilled' && healthResponse.value.body) {
      setHealthStatus(healthResponse.value.body.status);
    } else {
      setHealthStatus(status.health);
    }

    if (status.health === 'Healthy') {
      const [stationRows, alarmRows, traceResponse] = await Promise.all([
        getStationStatuses(),
        getAlarms(),
        getTraceRecords()
      ]);
      setStations(stationRows);
      setAlarms(alarmRows);
      setTraceRows(traceResponse?.items ?? []);

      const selectedSessionId = lastRun?.sessionId ?? stationRows[0]?.latestSessionId;
      if (selectedSessionId) {
        setTimeline(await getTimeline(selectedSessionId));
      }
    }
  }, [lastRun?.sessionId]);

  useEffect(() => {
    refresh().catch(error => setMessage(`Refresh failed: ${String(error)}`));
  }, [refresh]);

  useEffect(() => {
    setActiveApplicationId(current => {
      const applications = activeWorkspace?.project.applications ?? [];
      return applications.some(application => application.applicationId === current)
        ? current
        : applications[0]?.applicationId ?? null;
    });
  }, [activeWorkspace]);

  useEffect(() => {
    if (!config?.apiBaseUrl) {
      return;
    }

    if (backendStatus?.health !== 'Healthy') {
      setHubState('Disconnected');
      return;
    }

    const connection = createRuntimeHubConnection(config.apiBaseUrl);
    let disposed = false;
    let retryTimer: number | undefined;

    const startConnection = async (): Promise<void> => {
      if (disposed) {
        return;
      }

      setHubState('Connecting');
      try {
        await connection.start();
        if (!disposed) {
          setHubState('Connected');
        }
      } catch {
        if (!disposed) {
          setHubState('Disconnected');
          retryTimer = window.setTimeout(() => {
            void startConnection();
          }, 1200);
        }
      }
    };

    connection.on('StationStatusChanged', (status: RuntimeStationStatus) => {
      recordSmokeEvent('StationStatusChanged');
      setStations(current => upsertBy(current, status, item => item.stationId));
    });
    connection.on('RuntimeEvent', (entry: RuntimeTimelineEntry) => {
      recordSmokeEvent('RuntimeEvent');
      setTimeline(current => [...current.filter(item => item.eventId !== entry.eventId), entry]
        .sort((left, right) => left.sequence - right.sequence)
        .slice(-80));
    });
    connection.on('AlarmRaised', (alarm: RuntimeAlarm) => {
      recordSmokeEvent('AlarmRaised');
      setAlarms(current => upsertBy(current, alarm, item => item.alarmId));
    });
    connection.on('AlarmAcknowledged', (alarm: RuntimeAlarm) => {
      recordSmokeEvent('AlarmAcknowledged');
      setAlarms(current => upsertBy(current, alarm, item => item.alarmId));
    });
    connection.onreconnecting(() => setHubState('Reconnecting'));
    connection.onreconnected(() => {
      setHubState('Connected');
      refresh().catch(error => setMessage(`Resync failed: ${String(error)}`));
    });
    connection.onclose(() => setHubState('Disconnected'));

    void startConnection();

    return () => {
      disposed = true;
      if (retryTimer !== undefined) {
        window.clearTimeout(retryTimer);
      }
      void connection.stop();
    };
  }, [backendStatus?.health, config?.apiBaseUrl, refresh]);

  const startBackend = useCallback(async () => {
    setBusy(true);
    setMessage('Starting OpenLineOps.Api');
    try {
      const status = await desktop.startBackend();
      setBackendStatus(status);
      setMessage('Backend start requested');
      setTimeout(() => {
        refresh().catch(error => setMessage(`Refresh failed: ${String(error)}`));
      }, 1200);
    } finally {
      setBusy(false);
    }
  }, [refresh]);

  const stopBackend = useCallback(async () => {
    setBusy(true);
    try {
      const status = await desktop.stopBackend();
      setBackendStatus(status);
      setMessage('Backend stopped');
    } finally {
      setBusy(false);
    }
  }, []);

  const runSimulation = useCallback(async () => {
    setBusy(true);
    const seed = Date.now().toString(36);
    try {
      const result = await startDemoRuntimeSession(seed);
      setLastRun(result);
      setMessage(result ? `Simulated session ${result.status}` : 'Simulation request returned no body');
      await refresh();
    } catch (error) {
      setMessage(`Simulation failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [refresh]);

  const acknowledge = useCallback(async (alarmId: string) => {
    const alarm = await acknowledgeAlarm(alarmId, 'desktop-operator');
    if (alarm) {
      setAlarms(current => upsertBy(current, alarm, item => item.alarmId));
      setMessage(`Alarm ${alarm.code} acknowledged`);
    }
  }, []);

  const selectWorkspace = useCallback((workspace: AutomationProjectWorkspaceResponse) => {
    setActiveWorkspace(workspace);
    setWorkspaceMode('edit');
    setActiveNav('projects');
    setLastProjectRun(null);
  }, []);

  const closeWorkspace = useCallback(() => {
    setActiveWorkspace(null);
    setActiveApplicationId(null);
    setWorkspaceMode('edit');
    setActiveNav('projects');
    setLastProjectRun(null);
    setMessage('Project closed. Select a project to continue.');
  }, []);

  const changeWorkspaceMode = useCallback((mode: 'edit' | 'run') => {
    if (mode === 'run' && !activeWorkspace) {
      setMessage('Open a project before entering Run mode.');
      setActiveNav('projects');
      return;
    }

    setWorkspaceMode(mode);
    setActiveNav(mode === 'run' ? 'dashboard' : activeNav === 'dashboard' ? 'projects' : activeNav);
  }, [activeNav, activeWorkspace]);

  const runActiveProject = useCallback(async () => {
    if (!activeWorkspace) {
      setMessage('Open a project before running.');
      setActiveNav('projects');
      return;
    }

    const snapshotId = activeApplicationSnapshot?.snapshotId ?? null;
    if (!snapshotId) {
      setMessage('Publish a project snapshot before running.');
      setActiveNav('processes');
      return;
    }

    if (backendStatus?.health !== 'Healthy') {
      setMessage('Start the runtime backend before running the project.');
      return;
    }

    setBusy(true);
    setMessage(`Starting published snapshot ${snapshotId}`);
    try {
      const response = await startProjectSnapshotRuntimeSession(
        activeWorkspace.project.projectId,
        snapshotId,
        {
          serialNumber: null,
          batchId: null,
          fixtureId: null,
          deviceId: null,
          actorId: 'openlineops-ide'
        });
      if (!response.ok || !response.body) {
        setMessage(`Project run failed: ${response.status} ${response.text}`);
        return;
      }

      setLastProjectRun(response.body);
      setWorkspaceMode('run');
      setActiveNav('dashboard');
      setMessage(`Project run ${response.body.status}: ${response.body.sessionId}`);
      await refresh();
    } catch (error) {
      setMessage(`Project run failed: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }, [activeApplicationSnapshot?.snapshotId, activeWorkspace, backendStatus?.health, refresh]);

  const selectApplication = useCallback((applicationId: string) => {
    if (!applicationId) {
      return;
    }

    setActiveApplicationId(applicationId);
    setLastProjectRun(null);
    setWorkspaceMode('edit');
    setMessage(`Application selected ${applicationId}`);
  }, []);

  const visiblePanel = useMemo(() => {
    if (activeNav === 'dashboard') {
      return (
        <DashboardView
          stations={stations}
          timeline={timeline}
          alarms={alarms}
          traceRows={traceRows}
          latestStation={latestStation}
          onAcknowledge={acknowledge}
        />
      );
    }

    if (activeNav === 'processes') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="Processes" />}>
          <ProcessWorkbench
            activeWorkspace={activeWorkspace}
            activeApplicationId={activeApplication?.applicationId ?? null}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            onWorkspaceChanged={setActiveWorkspace}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (activeNav === 'projects') {
      return (
        <ProjectsWorkbench
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          onActiveApplicationChanged={selectApplication}
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onWorkspaceChanged={selectWorkspace}
          onMessage={setMessage}
        />
      );
    }

    if (activeNav === 'engineering') {
      return (
        <EngineeringWorkbench
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    if (activeNav === 'devices') {
      return (
        <DevicesWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    if (activeNav === 'trace') {
      return (
        <TraceWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    if (activeNav === 'plugins') {
      return (
        <PluginsWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    return <SecondaryView activeNav={activeNav} traceRows={traceRows} stations={stations} />;
  }, [acknowledge, activeApplication?.applicationId, activeNav, activeWorkspace, alarms, backendStatus?.health, latestStation, selectApplication, selectWorkspace, stations, timeline, traceRows]);

  return (
    <main
      className={`ide-shell ${activeWorkspace ? 'project-open' : 'start-only'}`}
      data-testid="automation-ide-shell"
    >
      <header className="ide-titlebar">
        <div className="ide-breadcrumb">
          <strong>OpenLineOps</strong>
          <span>/</span>
          <span>{activeWorkspace?.project.displayName ?? 'Start Center'}</span>
          {activeWorkspace ? (
            <>
              <span>/</span>
              <select
                className="ide-application-selector"
                value={activeApplication?.applicationId ?? ''}
                onChange={event => selectApplication(event.target.value)}
                aria-label="Active application"
                data-testid="active-application-selector"
              >
                {activeWorkspace.project.applications.map(application => (
                  <option key={application.applicationId} value={application.applicationId}>
                    {application.displayName}
                  </option>
                ))}
              </select>
              <span>/</span>
              <b>{activeNavLabel}</b>
            </>
          ) : null}
        </div>

        {activeWorkspace ? (
          <div className="ide-mode-switch" role="group" aria-label="Workspace mode">
            <button
              type="button"
              className={workspaceMode === 'edit' ? 'active' : ''}
              onClick={() => changeWorkspaceMode('edit')}
              data-testid="mode-edit"
            >
              <Code2 size={14} />
              Edit
            </button>
            <button
              type="button"
              className={workspaceMode === 'run' ? 'active' : ''}
              onClick={() => changeWorkspaceMode('run')}
              data-testid="mode-run"
            >
              <Play size={14} />
              Run
            </button>
          </div>
        ) : (
          <div className="ide-start-title">AUTOMATION LINE STUDIO</div>
        )}

        <div className="ide-title-actions">
          <span className={`ide-health-dot ${backendStatus?.health === 'Healthy' ? 'good' : 'warn'}`} />
          <span className="ide-health-label">Runtime {backendStatus?.health ?? 'Unknown'}</span>
          <button type="button" className="ide-tool-button" onClick={refresh} disabled={busy} title="Refresh runtime" data-testid="refresh-backend">
            <RefreshCw size={15} />
          </button>
          {backendStatus?.pid ? (
            <button type="button" className="ide-tool-button danger" onClick={stopBackend} disabled={busy} title="Stop runtime" data-testid="stop-backend">
              <Square size={13} />
            </button>
          ) : (
            <button type="button" className="ide-tool-button" onClick={startBackend} disabled={busy} title="Start runtime" data-testid="start-backend">
              <CirclePlay size={15} />
            </button>
          )}
          <button
            type="button"
            className="ide-run-button"
            onClick={runActiveProject}
            disabled={busy || !activeWorkspace || !activeApplicationSnapshot || backendStatus?.health !== 'Healthy'}
            title={activeApplicationSnapshot ? 'Run selected application snapshot' : 'Publish a snapshot for the selected application before running'}
            data-testid="run-active-project"
          >
            <Play size={14} />
            Run Project
          </button>
        </div>
      </header>

      {activeWorkspace ? (
        <aside className="ide-activity-bar" aria-label="Primary tools">
        <div className="brand-mark" title="OpenLineOps Automation IDE">OL</div>
        <nav className="nav-list">
          {navItems.map(item => {
            const Icon = item.icon;
            const disabled = !activeWorkspace && item.id !== 'projects';
            return (
              <button
                type="button"
                className={activeNav === item.id ? 'nav-item active' : 'nav-item'}
                key={item.id}
                onClick={() => {
                  setActiveNav(item.id);
                  setWorkspaceMode(item.id === 'dashboard' ? 'run' : 'edit');
                }}
                title={item.label}
                disabled={disabled}
                data-testid={`nav-${item.id}`}
              >
                <Icon size={20} />
                <span>{item.label}</span>
              </button>
            );
          })}
        </nav>
        <div className="rail-footer">
          <StatusPill label={hubState} tone={hubState === 'Connected' ? 'good' : 'warn'} />
        </div>
        </aside>
      ) : null}

      {activeWorkspace ? (
        <ProjectExplorer
          workspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          activeNav={activeNav}
          onNavigate={nav => {
            setActiveNav(nav);
            setWorkspaceMode(nav === 'dashboard' ? 'run' : 'edit');
          }}
          onSelectApplication={selectApplication}
          onClose={closeWorkspace}
        />
      ) : null}

      <section className={activeWorkspace ? 'ide-editor-area' : 'ide-editor-area start-center-mode'}>
        {activeWorkspace ? (
          <>
            <div className="ide-editor-tabs">
          <div className="ide-editor-tab active">
            {activeNav === 'processes' ? <Blocks size={14} /> : activeNav === 'dashboard' ? <Play size={14} /> : <FileSearch size={14} />}
            <span>{activeTitle}</span>
            {activeWorkspace ? <small>{activeWorkspace.project.projectId}</small> : null}
          </div>
            </div>

            <div className="ide-editor-toolbar">
          <div>
            <strong>{activeTitle}</strong>
            <span>{message}</span>
          </div>
          {activeNav === 'dashboard' ? (
            <div className="ide-editor-toolbar-actions">
              <button
                type="button"
                className="button"
                onClick={runSimulation}
                disabled={busy || backendStatus?.health !== 'Healthy'}
                title="Run simulated station session"
                data-testid="run-simulation"
              >
                <Zap size={15} />
                Run Simulation
              </button>
              <span>Latest: {lastProjectRun?.sessionId ?? lastRun?.sessionId ?? latestStation?.latestSessionId ?? 'none'}</span>
            </div>
          ) : null}
            </div>

            <div className="ide-editor-surface">
              {visiblePanel}
            </div>

            <div className={`ide-bottom-panel ${workspaceMode === 'run' ? 'expanded' : ''}`}>
          <div className="ide-bottom-panel-title">
            <PanelBottom size={14} />
            <strong>{workspaceMode === 'run' ? 'Runtime Output' : 'Problems · Output · Terminal'}</strong>
            <span>{message}</span>
          </div>
          {workspaceMode === 'run' ? (
            <div className="ide-runtime-summary">
              <InfoCell label="Session" value={lastProjectRun?.sessionId ?? lastRun?.sessionId ?? 'waiting'} />
              <InfoCell label="Status" value={lastProjectRun?.status ?? lastRun?.status ?? 'Idle'} />
              <InfoCell label="Timeline" value={`${timeline.length} events`} />
              <InfoCell label="Alarms" value={`${alarms.filter(alarm => !alarm.isAcknowledged).length} open`} />
            </div>
          ) : null}
            </div>
          </>
        ) : (
          <div className="ide-start-surface">{visiblePanel}</div>
        )}
      </section>

      <footer className="ide-statusbar">
        <span><Layers3 size={12} /> {activeWorkspace?.project.projectId ?? 'No project open'}{activeApplication ? ` / ${activeApplication.applicationId}` : ''}</span>
        <span>{activeApplicationSnapshot ? `Snapshot ${activeApplicationSnapshot.snapshotId}` : 'Draft workspace'}</span>
        <span>{platform ? `${platform.serviceName} ${platform.version}` : 'OpenLineOps.Api'}</span>
        <span>{platform?.environment ?? 'local'} · PID {backendStatus?.pid ?? '—'} · {healthStatus}</span>
      </footer>
    </main>
  );
}

function ProjectExplorer({
  workspace,
  activeApplicationId,
  activeNav,
  onNavigate,
  onSelectApplication,
  onClose
}: {
  workspace: AutomationProjectWorkspaceResponse;
  activeApplicationId: string | null;
  activeNav: NavId;
  onNavigate(nav: NavId): void;
  onSelectApplication(applicationId: string): void;
  onClose(): void;
}): React.ReactElement {
  const application = workspace.project.applications.find(
    candidate => candidate.applicationId === activeApplicationId)
    ?? workspace.project.applications[0]
    ?? null;
  const applicationSnapshot = selectApplicationSnapshot(
    workspace,
    application?.applicationId ?? null);
  const explorerItems: Array<{
    nav: NavId;
    label: string;
    detail: string;
    icon: React.ComponentType<{ size?: number }>;
  }> = [
    { nav: 'projects', label: 'Systems & Layout', detail: application?.topologyId ?? 'not configured', icon: LayoutDashboard },
    { nav: 'processes', label: 'Flows & Scripts', detail: `${application?.processDefinitionIds.length ?? 0} linked`, icon: Blocks },
    { nav: 'engineering', label: 'Configuration', detail: 'recipes · stations', icon: MonitorCog },
    { nav: 'devices', label: 'Devices & Drivers', detail: 'capability providers', icon: PlugZap }
  ];
  const operationsItems: Array<{
    nav: NavId;
    label: string;
    detail: string;
    icon: React.ComponentType<{ size?: number }>;
  }> = [
    { nav: 'dashboard', label: 'Run & Monitor', detail: applicationSnapshot?.snapshotId ?? 'publish required', icon: Play },
    { nav: 'trace', label: 'Trace Evidence', detail: 'sessions · results', icon: FileSearch },
    { nav: 'plugins', label: 'Extensions', detail: 'blocks · drivers', icon: Package }
  ];

  const renderItem = (item: (typeof explorerItems)[number]): React.ReactElement => {
    const Icon = item.icon;
    return (
      <button
        type="button"
        className={activeNav === item.nav ? 'ide-explorer-item active' : 'ide-explorer-item'}
        key={item.nav}
        onClick={() => onNavigate(item.nav)}
      >
        <Icon size={15} />
        <span>
          <strong>{item.label}</strong>
          <small>{item.detail}</small>
        </span>
      </button>
    );
  };

  return (
    <aside className="ide-explorer" aria-label="Project explorer">
      <div className="ide-explorer-heading">
        <span>PROJECT</span>
        <button type="button" onClick={onClose} title="Close project" data-testid="close-project-workspace">
          <X size={14} />
        </button>
      </div>

      <button
        type="button"
        className={activeNav === 'projects' ? 'ide-project-root active' : 'ide-project-root'}
        onClick={() => onNavigate('projects')}
      >
        <FolderKanban size={16} />
        <span>
          <strong>{workspace.project.displayName}</strong>
          <small>{workspace.project.projectId}</small>
        </span>
      </button>

      <div className="ide-explorer-branch">
        <div className="ide-explorer-group-title">
          <span>APPLICATION</span>
          <small>{workspace.project.applications.length}</small>
        </div>
        {workspace.project.applications.length > 0 ? (
          <div className="ide-application-list">
            {workspace.project.applications.map(candidate => (
              <button
                type="button"
                className={candidate.applicationId === application?.applicationId
                  ? 'ide-application-row active'
                  : 'ide-application-row'}
                key={candidate.applicationId}
                onClick={() => onSelectApplication(candidate.applicationId)}
                data-testid={`select-application-${candidate.applicationId}`}
              >
                <span className="ide-tree-line" />
                <Blocks size={14} />
                <span>
                  <strong>{candidate.displayName}</strong>
                  <small>{candidate.applicationId}</small>
                </span>
              </button>
            ))}
          </div>
        ) : (
          <p className="ide-explorer-empty">No application configured</p>
        )}
        <div className="ide-explorer-items">
          {explorerItems.map(renderItem)}
        </div>
      </div>

      <div className="ide-explorer-branch">
        <div className="ide-explorer-group-title">
          <span>OPERATIONS</span>
          <small>{workspace.project.snapshots.length} snapshots</small>
        </div>
        <div className="ide-explorer-items">
          {operationsItems.map(renderItem)}
        </div>
      </div>

      <div className="ide-explorer-manifest">
        <FileSearch size={14} />
        <span>
          <strong>openlineops.project.json</strong>
          <small>{workspace.project.projectPath}</small>
        </span>
      </div>
    </aside>
  );
}

function selectApplicationSnapshot(
  workspace: AutomationProjectWorkspaceResponse | null,
  applicationId: string | null
): PublishedProjectSnapshotResponse | null {
  if (!workspace || !applicationId) {
    return null;
  }

  const applicationSnapshots = workspace.project.snapshots
    .filter(snapshot => snapshot.applicationId === applicationId);
  const activeSnapshot = applicationSnapshots.find(
    snapshot => snapshot.snapshotId === workspace.project.activeSnapshotId);

  return activeSnapshot
    ?? applicationSnapshots
      .slice()
      .sort((left, right) => right.publishedAtUtc.localeCompare(left.publishedAtUtc))[0]
    ?? null;
}

interface DashboardViewProps {
  stations: RuntimeStationStatus[];
  timeline: RuntimeTimelineEntry[];
  alarms: RuntimeAlarm[];
  traceRows: TraceRecordSummary[];
  latestStation: RuntimeStationStatus | null;
  onAcknowledge(alarmId: string): void;
}

function DashboardView({
  stations,
  timeline,
  alarms,
  traceRows,
  latestStation,
  onAcknowledge
}: DashboardViewProps): React.ReactElement {
  return (
    <div className="dashboard-grid">
      <section className="panel station-panel">
        <PanelTitle icon={Gauge} title="Stations" action={`${stations.length} active`} />
        <div className="station-table">
          <div className="table-head">
            <span>Station</span>
            <span>Status</span>
            <span>Steps</span>
            <span>Incidents</span>
          </div>
          {stations.length === 0 ? (
            <EmptyState text="No runtime station state yet" />
          ) : stations.map(station => (
            <div className="table-row" key={station.stationId}>
              <strong>{station.stationId}</strong>
              <StatusPill label={station.sessionStatus} tone={station.sessionStatus === 'Completed' ? 'good' : station.sessionStatus === 'Failed' ? 'bad' : 'warn'} />
              <span>{station.completedStepCount}/{station.stepCount}</span>
              <span>{station.incidentCount}</span>
            </div>
          ))}
        </div>
      </section>

      <section className="panel timeline-panel">
        <PanelTitle icon={Activity} title="Runtime Timeline" action={latestStation?.processVersionId ?? 'waiting'} />
        <div className="timeline-list">
          {timeline.length === 0 ? (
            <EmptyState text="No runtime events received" />
          ) : timeline.slice(-14).reverse().map(entry => (
            <article className="timeline-entry" key={`${entry.sequence}-${entry.eventId}`}>
              <span className={entry.severity === 'Error' ? 'event-dot bad' : 'event-dot'} />
              <div>
                <div className="timeline-row">
                  <strong>{entry.eventName}</strong>
                  <time>{formatTime(entry.occurredAtUtc)}</time>
                </div>
                <p>{entry.entityKind} {entry.toStatus ? `${entry.fromStatus ?? 'None'} -> ${entry.toStatus}` : entry.code ?? entry.entityId}</p>
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="panel alarm-panel">
        <PanelTitle icon={AlertTriangle} title="Alarms" action={`${alarms.filter(alarm => !alarm.isAcknowledged).length} open`} />
        <div className="alarm-list">
          {alarms.length === 0 ? (
            <EmptyState text="No open alarms" />
          ) : alarms.map(alarm => (
            <article className={alarm.isAcknowledged ? 'alarm acknowledged' : 'alarm'} key={alarm.alarmId}>
              <div>
                <strong>{alarm.code}</strong>
                <p>{alarm.message}</p>
                <span>{alarm.stationId} - {formatTime(alarm.occurredAtUtc)}</span>
              </div>
              {alarm.isAcknowledged ? (
                <CheckCircle2 size={19} />
              ) : (
                <button type="button" className="icon-button" onClick={() => onAcknowledge(alarm.alarmId)} title="Acknowledge alarm">
                  <CheckCircle2 size={17} />
                </button>
              )}
            </article>
          ))}
        </div>
      </section>

      <section className="panel trace-panel">
        <PanelTitle icon={Database} title="Trace Rows" action={`${traceRows.length} shown`} />
        <div className="trace-strip">
          {traceRows.slice(0, 5).map(trace => (
            <div className="trace-row" key={trace.traceRecordId}>
              <span>{trace.serialNumber}</span>
              <strong>{trace.judgement}</strong>
              <span>{trace.stationId}</span>
              <time>{formatTime(trace.completedAtUtc)}</time>
            </div>
          ))}
          {traceRows.length === 0 ? <EmptyState text="No trace records yet" /> : null}
        </div>
      </section>
    </div>
  );
}

function SecondaryView({
  activeNav,
  traceRows,
  stations
}: {
  activeNav: NavId;
  traceRows: TraceRecordSummary[];
  stations: RuntimeStationStatus[];
}): React.ReactElement {
  const title = navItems.find(item => item.id === activeNav)?.label ?? 'Dashboard';

  return (
    <section className="secondary-grid">
      <div className="panel wide-panel">
        <PanelTitle icon={Settings} title={title} action="contract first" />
        <div className="work-surface">
          <div>
            <h2>{title}</h2>
            <p>{secondaryCopy[activeNav]}</p>
          </div>
          <div className="surface-list">
            {stations.slice(0, 3).map(station => (
              <div className="surface-row" key={station.stationId}>
                <span>{station.stationId}</span>
                <strong>{station.sessionStatus}</strong>
              </div>
            ))}
            {traceRows.slice(0, 3).map(trace => (
              <div className="surface-row" key={trace.traceRecordId}>
                <span>{trace.serialNumber}</span>
                <strong>{trace.judgement}</strong>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}

function WorkbenchLoading({ label }: { label: string }): React.ReactElement {
  return (
    <section className="secondary-grid">
      <div className="panel wide-panel">
        <div className="empty-state">Loading {label}</div>
      </div>
    </section>
  );
}

const secondaryCopy: Record<NavId, string> = {
  dashboard: '',
  projects: 'Automation project workspaces are opened from folder manifests and published through immutable snapshots.',
  engineering: 'Workspace and project selection will use backend engineering contracts.',
  processes: 'Process editing remains API-backed so Electron does not own orchestration rules.',
  devices: 'Device configuration is read from backend APIs and never from local databases.',
  trace: 'Trace query uses the traceability endpoints and runtime-linked read models.',
  plugins: 'Plugin management will stay aligned to manifest and host lifecycle contracts.'
};

function InfoCell({ label, value }: { label: string; value: string }): React.ReactElement {
  return (
    <div className="info-cell">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function PanelTitle({
  icon: Icon,
  title,
  action
}: {
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  action: string;
}): React.ReactElement {
  return (
    <div className="panel-title">
      <div>
        <Icon size={17} />
        <h2>{title}</h2>
      </div>
      <span>{action}</span>
    </div>
  );
}

function StatusPill({
  label,
  tone
}: {
  label: string;
  tone: 'good' | 'warn' | 'bad';
}): React.ReactElement {
  return <span className={`status-pill ${tone}`}>{label}</span>;
}

function EmptyState({ text }: { text: string }): React.ReactElement {
  return <div className="empty-state">{text}</div>;
}

function recordSmokeEvent(eventName: string): void {
  if (!window.__openlineopsSmokeEvents) {
    return;
  }

  window.__openlineopsSmokeEvents[eventName] =
    (window.__openlineopsSmokeEvents[eventName] ?? 0) + 1;
}

function upsertBy<T>(items: T[], item: T, keySelector: (item: T) => string): T[] {
  const key = keySelector(item);
  const next = items.filter(candidate => keySelector(candidate) !== key);
  return [item, ...next].slice(0, 50);
}

function formatTime(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
