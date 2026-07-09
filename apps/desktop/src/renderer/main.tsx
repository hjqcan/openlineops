import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  CirclePlay,
  Database,
  FileSearch,
  FolderKanban,
  Gauge,
  GitBranch,
  HeartPulse,
  LayoutDashboard,
  MonitorCog,
  Package,
  PlugZap,
  RefreshCw,
  Server,
  Settings,
  Square,
  Zap
} from 'lucide-react';
import type { BackendStatus, DesktopConfig } from '../shared/desktop-api';
import type {
  AutomationProjectWorkspaceResponse,
  PlatformResponse,
  RuntimeAlarm,
  RuntimeSessionRunResponse,
  RuntimeStationStatus,
  RuntimeTimelineEntry,
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
  { id: 'dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { id: 'projects', label: 'Projects', icon: FolderKanban },
  { id: 'engineering', label: 'Engineering', icon: MonitorCog },
  { id: 'processes', label: 'Processes', icon: GitBranch },
  { id: 'devices', label: 'Devices', icon: PlugZap },
  { id: 'trace', label: 'Trace', icon: FileSearch },
  { id: 'plugins', label: 'Plugins', icon: Package }
] as const;

type NavId = (typeof navItems)[number]['id'];
type HubState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting';

declare global {
  interface Window {
    __openlineopsSmokeEvents?: Record<string, number>;
  }
}

function App(): React.ReactElement {
  const [activeNav, setActiveNav] = useState<NavId>('dashboard');
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
  const [activeWorkspace, setActiveWorkspace] = useState<AutomationProjectWorkspaceResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('Ready');

  const latestStation = stations[0] ?? null;
  const activeNavLabel = navItems.find(item => item.id === activeNav)?.label ?? 'Dashboard';
  const activeTitle = activeNav === 'dashboard'
    ? 'Station Runtime Dashboard'
    : `${activeNavLabel} Workbench`;

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
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onWorkspaceChanged={setActiveWorkspace}
          onMessage={setMessage}
        />
      );
    }

    if (activeNav === 'engineering') {
      return (
        <EngineeringWorkbench
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
  }, [acknowledge, activeNav, activeWorkspace, alarms, backendStatus?.health, latestStation, stations, timeline, traceRows]);

  return (
    <main className="app-shell">
      <aside className="rail">
        <div className="brand">
          <span className="brand-mark">OL</span>
          <span>OpenLineOps</span>
        </div>
        <nav className="nav-list">
          {navItems.map(item => {
            const Icon = item.icon;
            return (
              <button
                type="button"
                className={activeNav === item.id ? 'nav-item active' : 'nav-item'}
                key={item.id}
                onClick={() => setActiveNav(item.id)}
                title={item.label}
                data-testid={`nav-${item.id}`}
              >
                <Icon size={17} />
                <span>{item.label}</span>
              </button>
            );
          })}
        </nav>
        <div className="rail-footer">
          <StatusPill label={hubState} tone={hubState === 'Connected' ? 'good' : 'warn'} />
          <span className="small-path">{config?.logPath ?? 'logs pending'}</span>
        </div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div>
            <h1>{activeTitle}</h1>
            <p>{message}</p>
          </div>
          <div className="top-actions">
            <StatusMetric icon={Server} label="API" value={backendStatus?.health ?? 'Unknown'} tone={backendStatus?.health === 'Healthy' ? 'good' : 'warn'} />
            <StatusMetric icon={HeartPulse} label="Health" value={healthStatus} tone={healthStatus === 'Healthy' ? 'good' : 'warn'} />
            <button type="button" className="button ghost" onClick={refresh} disabled={busy} title="Refresh" data-testid="refresh-backend">
              <RefreshCw size={16} />
              Refresh
            </button>
            <button type="button" className="button" onClick={startBackend} disabled={busy} title="Start backend" data-testid="start-backend">
              <CirclePlay size={16} />
              Start
            </button>
            <button type="button" className="button danger" onClick={stopBackend} disabled={busy} title="Stop backend" data-testid="stop-backend">
              <Square size={15} />
              Stop
            </button>
          </div>
        </header>

        <section className="system-strip">
          <InfoCell label="API base URL" value={config?.apiBaseUrl ?? 'http://localhost:5135'} />
          <InfoCell label="Backend PID" value={backendStatus?.pid?.toString() ?? 'not running'} />
          <InfoCell label="Platform" value={platform ? `${platform.service} ${platform.version}` : 'pending'} />
          <InfoCell label="Environment" value={platform?.environment ?? 'unknown'} />
        </section>

        <section className="command-row">
          <button type="button" className="button primary" onClick={runSimulation} disabled={busy || backendStatus?.health !== 'Healthy'} title="Run simulated station session" data-testid="run-simulation">
            <Zap size={16} />
            Run Simulation
          </button>
          <span className="command-note">
            Latest session: {lastRun?.sessionId ?? latestStation?.latestSessionId ?? 'none'}
          </span>
        </section>

        {visiblePanel}
      </section>
    </main>
  );
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

function StatusMetric({
  icon: Icon,
  label,
  value,
  tone
}: {
  icon: React.ComponentType<{ size?: number }>;
  label: string;
  value: string;
  tone: 'good' | 'warn' | 'bad';
}): React.ReactElement {
  return (
    <div className={`status-metric ${tone}`}>
      <Icon size={16} />
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

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
