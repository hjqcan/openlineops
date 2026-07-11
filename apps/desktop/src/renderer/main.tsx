import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Blocks,
  CirclePlay,
  Code2,
  FileSearch,
  Factory,
  FileCode2,
  FolderKanban,
  GitBranch,
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
  X
} from 'lucide-react';
import type { BackendStatus, DesktopConfig } from '../shared/desktop-api';
import type {
  AutomationProjectWorkspaceResponse,
  PlatformResponse,
  PublishedProjectSnapshotResponse,
  RuntimeAlarm,
  RuntimeCommandStatus,
  RuntimeMonitoringScope,
  RuntimeSessionStatus,
  RuntimeStationStatus,
  RuntimeTargetStatus,
  RuntimeTimelineEntry,
  ProductionRunReadModel,
  SubmitProductionRunRequest,
  TraceRecordSummary
} from './contracts';
import { desktop } from './desktop-bridge';
import {
  createRuntimeHubConnection,
  arriveProductionUnit,
  getAlarms,
  getHealth,
  getPlatform,
  getProjectSnapshotProductionRunContext,
  getProductionRun,
  getProductionUnit,
  getStationStatuses,
  getTargetStatuses,
  getTimeline,
  getTraceRecords,
  registerProductionUnit,
  submitProductionRun
} from './api';
import { DevicesWorkbench } from './devices-workbench';
import { EngineeringWorkbench } from './engineering-workbench';
import { PluginsWorkbench } from './plugins-workbench';
import { ProjectsWorkbench } from './projects-workbench';
import { TraceWorkbench } from './trace-workbench';
import { useProductionOperations } from './use-production-operations';
import {
  EditorConflictNotice,
  EditorDocumentHost,
  EditorProblemsPanel,
  EditorTabStrip,
  SaveAllButton,
  useDocumentRegistrySnapshot
} from './editor-workspace';
import {
  activateEditorTab,
  closeEditorTab,
  DirtyDocumentRegistry,
  openEditorTab,
  type EditorTabModel,
  type EditorTabState
} from './editor-workspace-model';
import './styles.css';
import './production.css';
import './operations.css';
import './topology.css';

const ProcessWorkbench = React.lazy(async () => {
  const module = await import('./process-workbench');
  return { default: module.ProcessWorkbench };
});

const ProductionWorkbench = React.lazy(async () => {
  const module = await import('./production-workbench');
  return { default: module.ProductionWorkbench };
});

const OperationsWorkbench = React.lazy(async () => {
  const module = await import('./operations-workbench');
  return { default: module.OperationsWorkbench };
});

const ExternalProgramWorkbench = React.lazy(async () => {
  const module = await import('./external-program-workbench');
  return { default: module.ExternalProgramWorkbench };
});

const TopologyDesigner = React.lazy(async () => {
  const module = await import('./topology-designer');
  return { default: module.TopologyDesigner };
});

const navItems = [
  { id: 'projects', label: 'Explorer', icon: FolderKanban },
  { id: 'topology', label: '2D Layout', icon: LayoutDashboard },
  { id: 'production', label: 'Line Designer', icon: Factory },
  { id: 'processes', label: 'Flow Designer', icon: Blocks },
  { id: 'programs', label: 'Program Resources', icon: FileCode2 },
  { id: 'engineering', label: 'Configuration', icon: MonitorCog },
  { id: 'devices', label: 'Devices', icon: PlugZap },
  { id: 'dashboard', label: 'Run and Monitor', icon: Play },
  { id: 'trace', label: 'Traceability', icon: FileSearch },
  { id: 'plugins', label: 'Extensions', icon: Package }
] as const;

type NavId = (typeof navItems)[number]['id'];
type HubState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting';

interface ProductionRunFormState {
  productionUnitId: string;
  productionUnitIdentityValue: string;
  actorId: string;
}

interface PendingUnsavedGuard {
  title: string;
  detail: string;
  documentIds: ReadonlySet<string> | null;
  proceed(): void;
  cancel?(): void;
}

const emptyProductionRunForm: ProductionRunFormState = {
  productionUnitId: '',
  productionUnitIdentityValue: '',
  actorId: ''
};

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
  const [targetStatuses, setTargetStatuses] = useState<RuntimeTargetStatus[]>([]);
  const [timeline, setTimeline] = useState<RuntimeTimelineEntry[]>([]);
  const [alarms, setAlarms] = useState<RuntimeAlarm[]>([]);
  const [traceRows, setTraceRows] = useState<TraceRecordSummary[]>([]);
  const [lastProjectRun, setLastProjectRun] = useState<ProductionRunReadModel | null>(null);
  const [activeProductionRunId, setActiveProductionRunId] = useState<string | null>(null);
  const [runDialogOpen, setRunDialogOpen] = useState(false);
  const [productionRunForm, setProductionRunForm] =
    useState<ProductionRunFormState>(emptyProductionRunForm);
  const [activeWorkspace, setActiveWorkspace] = useState<AutomationProjectWorkspaceResponse | null>(null);
  const [activeApplicationId, setActiveApplicationId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('Ready');
  const documentRegistry = useMemo(() => new DirtyDocumentRegistry(), []);
  const [editorTabState, setEditorTabState] = useState<EditorTabState>({ tabs: [], activeId: null });
  const [pendingUnsavedGuard, setPendingUnsavedGuard] = useState<PendingUnsavedGuard | null>(null);
  useDocumentRegistrySnapshot(documentRegistry);

  const activeApplication = useMemo(
    () => activeWorkspace?.project.applications.find(
      application => application.applicationId === activeApplicationId)
      ?? activeWorkspace?.project.applications[0]
      ?? null,
    [activeApplicationId, activeWorkspace]);
  const activeApplicationSnapshot = useMemo(
    () => selectApplicationSnapshot(activeWorkspace, activeApplication?.applicationId ?? null),
    [activeApplication?.applicationId, activeWorkspace]);
  const activeMonitoringScope = useMemo<RuntimeMonitoringScope | null>(
    () => workspaceMode === 'run'
      && activeProductionRunId
      && activeWorkspace
      && activeApplicationSnapshot
      ? {
        projectId: activeWorkspace.project.projectId,
        applicationId: activeApplicationSnapshot.applicationId,
        projectSnapshotId: activeApplicationSnapshot.snapshotId,
        topologyId: activeApplicationSnapshot.topologyId,
        productionRunId: activeProductionRunId
      }
      : null,
    [activeApplicationSnapshot, activeProductionRunId, activeWorkspace, workspaceMode]);
  const operationsProjection = useProductionOperations({
    isBackendHealthy: backendStatus?.health === 'Healthy',
    projectId: activeWorkspace?.project.projectId ?? null,
    applicationId: activeApplication?.applicationId ?? null,
    preferredLineId: activeApplicationSnapshot?.productionLineDefinitionId ?? null,
    onMessage: setMessage
  });
  const runtimeHubConnectionRef =
    useRef<ReturnType<typeof createRuntimeHubConnection> | null>(null);
  const runDialogRef = useRef<HTMLDialogElement>(null);
  const monitoringScopeRef = useRef<RuntimeMonitoringScope | null>(activeMonitoringScope);
  const joinedMonitoringScopeRef = useRef<RuntimeMonitoringScope | null>(null);
  monitoringScopeRef.current = activeMonitoringScope;
  const lastProjectRunSessionId = useMemo(
    () => latestProductionRunSessionId(lastProjectRun),
    [lastProjectRun]);

  const latestStation = stations[0] ?? null;
  const activeNavLabel = navItems.find(item => item.id === activeNav)?.label ?? 'Explorer';
  const activeTitle = activeNav === 'dashboard'
    ? 'Line Operations'
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
        activeMonitoringScope
          ? getStationStatuses(activeMonitoringScope)
          : Promise.resolve([]),
        getAlarms(),
        getTraceRecords()
      ]);
      const scopedStationRows = stationRows.filter(statusRow =>
        runtimeStatusMatchesScope(statusRow, activeMonitoringScope)
        && isRuntimeSessionStatus(statusRow.sessionStatus));
      setStations(scopedStationRows);
      setAlarms(alarmRows);
      setTraceRows(traceResponse?.items ?? []);
      const scopedTargetRows = activeMonitoringScope
        ? (await getTargetStatuses(
          activeMonitoringScope,
          scopedStationRows.map(station => station.stationSystemId)))
          .filter(statusRow => runtimeStatusMatchesScope(statusRow, activeMonitoringScope)
            && isRuntimeCommandStatus(statusRow.commandStatus))
        : [];
      setTargetStatuses(scopedTargetRows);

      const selectedSessionId = activeMonitoringScope
        ? lastProjectRunSessionId ?? scopedStationRows[0]?.latestSessionId
        : null;
      if (selectedSessionId && activeMonitoringScope) {
        setTimeline(await getTimeline(selectedSessionId, activeMonitoringScope));
      } else {
        setTimeline([]);
      }
    } else {
      setStations([]);
      setTargetStatuses([]);
      setTimeline([]);
    }
  }, [activeMonitoringScope, lastProjectRunSessionId]);
  const refreshRef = useRef(refresh);
  refreshRef.current = refresh;

  useEffect(() => {
    refresh().catch(error => setMessage(`Refresh failed: ${String(error)}`));
  }, [refresh]);

  useEffect(() => {
    if (!backendStatus?.isRunning || backendStatus.health === 'Healthy') {
      return;
    }

    const timer = window.setInterval(() => {
      refresh().catch(error => setMessage(`Refresh failed: ${String(error)}`));
    }, 1000);

    return () => window.clearInterval(timer);
  }, [backendStatus?.health, backendStatus?.isRunning, refresh]);

  useEffect(() => {
    if (backendStatus?.health !== 'Healthy') {
      return;
    }

    setMessage(current => current === 'Backend start requested' || current === 'Starting OpenLineOps.Api'
      ? 'Runtime ready'
      : current);
  }, [backendStatus?.health]);

  useEffect(() => {
    setActiveApplicationId(current => {
      const applications = activeWorkspace?.project.applications ?? [];
      return applications.some(application => application.applicationId === current)
        ? current
        : applications[0]?.applicationId ?? null;
    });
  }, [activeWorkspace]);

  useEffect(() => {
    if (!lastProjectRun || !activeWorkspace || !activeApplicationSnapshot) {
      return;
    }

    if (lastProjectRun.projectId === activeWorkspace.project.projectId
        && lastProjectRun.applicationId === activeApplicationSnapshot.applicationId
        && lastProjectRun.projectSnapshotId === activeApplicationSnapshot.snapshotId
        && lastProjectRun.topologyId === activeApplicationSnapshot.topologyId) {
      return;
    }

    setLastProjectRun(null);
    setActiveProductionRunId(null);
    setStations([]);
    setTargetStatuses([]);
    setTimeline([]);
  }, [activeApplicationSnapshot, activeWorkspace, lastProjectRun]);

  useEffect(() => {
    if (!activeProductionRunId || backendStatus?.health !== 'Healthy') {
      return;
    }

    let disposed = false;
    let timer: number | undefined;
    const synchronizeRun = async (): Promise<void> => {
      try {
        const response = await getProductionRun(activeProductionRunId);
        if (disposed) {
          return;
        }
        if (response.status === 404) {
          timer = window.setTimeout(() => void synchronizeRun(), 500);
          return;
        }
        if (!response.ok || !response.body) {
          setMessage(`Production run synchronization failed: ${response.status} ${response.text}`);
          timer = window.setTimeout(() => void synchronizeRun(), 1500);
          return;
        }

        const run = response.body;
        setLastProjectRun(run);
        if (run.isTerminal) {
          setMessage(`Production run ${run.executionStatus}: ${run.productionRunId}`);
          return;
        }
        timer = window.setTimeout(() => void synchronizeRun(), 750);
      } catch (error) {
        if (disposed) {
          return;
        }
        setMessage(`Production run synchronization failed: ${String(error)}`);
        timer = window.setTimeout(() => void synchronizeRun(), 1500);
      }
    };

    void synchronizeRun();

    return () => {
      disposed = true;
      if (timer !== undefined) {
        window.clearTimeout(timer);
      }
    };
  }, [activeProductionRunId, backendStatus?.health]);

  useEffect(() => {
    const dialog = runDialogRef.current;
    if (!dialog) {
      return;
    }

    if (runDialogOpen && !dialog.open) {
      dialog.showModal();
    } else if (!runDialogOpen && dialog.open) {
      dialog.close();
    }
  }, [runDialogOpen]);

  useEffect(() => {
    if (!config?.apiBaseUrl) {
      return;
    }

    if (backendStatus?.health !== 'Healthy') {
      setHubState('Disconnected');
      return;
    }

    const connection = createRuntimeHubConnection(config.apiBaseUrl);
    runtimeHubConnectionRef.current = connection;
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
          const scope = monitoringScopeRef.current;
          if (scope) {
            await joinRuntimeProductionRunGroup(connection, scope);
            joinedMonitoringScopeRef.current = scope;
          }
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
      if (!runtimeStatusMatchesScope(status, monitoringScopeRef.current)
          || !isRuntimeSessionStatus(status.sessionStatus)) {
        return;
      }

      setStations(current => upsertBy(current, status, runtimeStationStatusKey));
      setTargetStatuses(current => current.filter(candidate =>
        !sameRuntimeStationScope(candidate, status)
        || candidate.sessionId === status.latestSessionId));
    });
    connection.on('TargetStatusChanged', (status: RuntimeTargetStatus) => {
      recordSmokeEvent('TargetStatusChanged');
      if (!runtimeStatusMatchesScope(status, monitoringScopeRef.current)
          || !isRuntimeCommandStatus(status.commandStatus)) {
        return;
      }

      setTargetStatuses(current => upsertRuntimeTargetStatus(current, status));
    });
    connection.on('RuntimeEvent', (entry: RuntimeTimelineEntry) => {
      recordSmokeEvent('RuntimeEvent');
      if (!runtimeTimelineMatchesScope(entry, monitoringScopeRef.current)
          || !isRuntimeSessionStatus(entry.sessionStatus)) {
        return;
      }

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
    connection.onreconnected(async () => {
      const scope = monitoringScopeRef.current;
      if (scope) {
        await joinRuntimeProductionRunGroup(connection, scope);
        joinedMonitoringScopeRef.current = scope;
      } else {
        joinedMonitoringScopeRef.current = null;
      }
      setHubState('Connected');
      refreshRef.current().catch(error => setMessage(`Resync failed: ${String(error)}`));
    });
    connection.onclose(() => setHubState('Disconnected'));

    void startConnection();

    return () => {
      disposed = true;
      if (retryTimer !== undefined) {
        window.clearTimeout(retryTimer);
      }
      if (runtimeHubConnectionRef.current === connection) {
        runtimeHubConnectionRef.current = null;
      }
      joinedMonitoringScopeRef.current = null;
      void connection.stop();
    };
  }, [backendStatus?.health, config?.apiBaseUrl]);

  useEffect(() => {
    const connection = runtimeHubConnectionRef.current;
    if (!connection || hubState !== 'Connected') {
      return;
    }

    const previousScope = joinedMonitoringScopeRef.current;
    if (runtimeMonitoringScopesEqual(previousScope, activeMonitoringScope)) {
      return;
    }

    void synchronizeRuntimeProductionRunGroup(
      connection,
      previousScope,
      activeMonitoringScope)
      .then(() => {
        joinedMonitoringScopeRef.current = activeMonitoringScope;
      })
      .catch(error => setMessage(`Runtime monitor subscription failed: ${String(error)}`));
  }, [activeMonitoringScope, hubState]);

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

  const runWithUnsavedGuard = useCallback((
    title: string,
    detail: string,
    proceed: () => void,
    documentIds: ReadonlySet<string> | null = null,
    cancel?: () => void
  ) => {
    if (documentRegistry.dirtyEntries(documentIds ?? undefined).length === 0) {
      proceed();
      return;
    }
    setPendingUnsavedGuard({ title, detail, documentIds, proceed, cancel });
  }, [documentRegistry]);

  const applyWorkspaceSelection = useCallback((workspace: AutomationProjectWorkspaceResponse) => {
    const applicationId = workspace.project.applications[0]?.applicationId ?? null;
    setActiveWorkspace(workspace);
    setActiveApplicationId(applicationId);
    setWorkspaceMode('edit');
    setActiveNav('projects');
    setEditorTabState(applicationId
      ? openEditorTab(
        { tabs: [], activeId: null },
        createEditorTab(workspace, applicationId, 'projects'))
      : { tabs: [], activeId: null });
    setLastProjectRun(null);
    setActiveProductionRunId(null);
    setStations([]);
    setTargetStatuses([]);
    setTimeline([]);
  }, []);

  const selectWorkspace = useCallback((workspace: AutomationProjectWorkspaceResponse) => {
    runWithUnsavedGuard(
      'Open another project?',
      'The current project contains unsaved editor changes.',
      () => applyWorkspaceSelection(workspace));
  }, [applyWorkspaceSelection, runWithUnsavedGuard]);

  const applyWorkspaceClose = useCallback(() => {
    setActiveWorkspace(null);
    setActiveApplicationId(null);
    setEditorTabState({ tabs: [], activeId: null });
    setWorkspaceMode('edit');
    setActiveNav('projects');
    setLastProjectRun(null);
    setActiveProductionRunId(null);
    setStations([]);
    setTargetStatuses([]);
    setTimeline([]);
    setMessage('Project closed. Select a project to continue.');
  }, []);

  const closeWorkspace = useCallback(() => {
    runWithUnsavedGuard(
      'Close project?',
      'Unsaved changes must be saved or discarded before the project can close.',
      applyWorkspaceClose);
  }, [applyWorkspaceClose, runWithUnsavedGuard]);

  const changeWorkspaceMode = useCallback((mode: 'edit' | 'run') => {
    if (mode === 'run' && !activeWorkspace) {
      setMessage('Open a project before entering Run mode.');
      setActiveNav('projects');
      return;
    }

    if (mode === 'run' && !activeApplicationSnapshot) {
      setMessage('Publish an immutable production-line snapshot for the selected Application before entering Run mode.');
      setActiveNav('production');
      return;
    }

    setWorkspaceMode(mode);
    setActiveNav(mode === 'run' ? 'topology' : activeNav === 'dashboard' ? 'projects' : activeNav);
  }, [activeApplicationSnapshot, activeNav, activeWorkspace]);

  const openRunProjectDialog = useCallback(() => {
    if (!activeWorkspace) {
      setMessage('Open a project before running.');
      setActiveNav('projects');
      return;
    }

    const snapshotId = activeApplicationSnapshot?.snapshotId ?? null;
    if (!snapshotId) {
      setMessage('Publish a production-line snapshot before running.');
      setActiveNav('production');
      return;
    }

    if (backendStatus?.health !== 'Healthy') {
      setMessage('Start the runtime backend before running the project.');
      return;
    }

    if (hubState !== 'Connected' || !runtimeHubConnectionRef.current) {
      setMessage('Wait for the production monitor connection before running the project.');
      return;
    }

    setProductionRunForm({
      ...emptyProductionRunForm,
      productionUnitId: crypto.randomUUID()
    });
    setRunDialogOpen(true);
  }, [activeApplicationSnapshot?.snapshotId, activeWorkspace, backendStatus?.health, hubState]);

  const runActiveProject = useCallback(async () => {
    if (!activeWorkspace || !activeApplicationSnapshot) {
      setMessage('Open a published Application snapshot before running.');
      setRunDialogOpen(false);
      return;
    }

    const validationError = validateProductionRunForm(productionRunForm);
    if (validationError) {
      setMessage(validationError);
      return;
    }

    const connection = runtimeHubConnectionRef.current;
    if (backendStatus?.health !== 'Healthy' || hubState !== 'Connected' || !connection) {
      setMessage('The runtime and production monitor connection must be healthy before starting.');
      return;
    }

    setBusy(true);
    const snapshotId = activeApplicationSnapshot.snapshotId;
    const productionRunId = crypto.randomUUID();
    const runScope: RuntimeMonitoringScope = {
      projectId: activeWorkspace.project.projectId,
      applicationId: activeApplicationSnapshot.applicationId,
      projectSnapshotId: snapshotId,
      topologyId: activeApplicationSnapshot.topologyId,
      productionRunId
    };
    const request: SubmitProductionRunRequest = {
      projectId: activeWorkspace.project.projectId,
      projectSnapshotId: snapshotId,
      productionRunId,
      productionUnitId: productionRunForm.productionUnitId,
      actorId: productionRunForm.actorId
    };
    setMessage(`Starting published snapshot ${snapshotId}`);
    try {
      const contextResponse = await getProjectSnapshotProductionRunContext(
        activeWorkspace.project.projectId,
        snapshotId);
      if (!contextResponse.ok || !contextResponse.body) {
        throw new Error(`Cannot read immutable production context: ${contextResponse.status} ${contextResponse.text}`);
      }

      const context = contextResponse.body;
      if (context.projectId !== activeWorkspace.project.projectId
          || context.applicationId !== activeApplicationSnapshot.applicationId
          || context.snapshotId !== snapshotId
          || context.topologyId !== activeApplicationSnapshot.topologyId
          || context.productionLineDefinitionId !== activeApplicationSnapshot.productionLineDefinitionId
          || context.productModelId.length === 0
          || context.productModelIdentityInputKey.length === 0
          || context.entryOperationId.length === 0
          || context.entryStationSystemId.length === 0) {
        throw new Error('Immutable production context identity differs from the selected snapshot.');
      }

      let unitResponse = await getProductionUnit(productionRunForm.productionUnitId);
      if (unitResponse.status === 404) {
        unitResponse = await registerProductionUnit({
          productionUnitId: productionRunForm.productionUnitId,
          productModelId: context.productModelId,
          identityKey: context.productModelIdentityInputKey,
          identityValue: productionRunForm.productionUnitIdentityValue,
          lotId: null,
          actorId: productionRunForm.actorId,
          occurredAtUtc: new Date().toISOString()
        });
      }

      if (!unitResponse.ok || !unitResponse.body) {
        throw new Error(`Cannot prepare Production Unit: ${unitResponse.status} ${unitResponse.text}`);
      }

      if (unitResponse.body.productionUnitId !== productionRunForm.productionUnitId
          || unitResponse.body.productModelId !== context.productModelId
          || unitResponse.body.identityKey !== context.productModelIdentityInputKey
          || unitResponse.body.identityValue !== productionRunForm.productionUnitIdentityValue) {
        throw new Error('Persisted Production Unit identity differs from the immutable production context.');
      }

      if (!unitResponse.body.location) {
        unitResponse = await arriveProductionUnit(productionRunForm.productionUnitId, {
          lineId: context.productionLineDefinitionId,
          stationSystemId: context.entryStationSystemId,
          actorId: productionRunForm.actorId,
          occurredAtUtc: new Date().toISOString()
        });
        if (!unitResponse.ok || !unitResponse.body) {
          throw new Error(`Cannot arrive Production Unit: ${unitResponse.status} ${unitResponse.text}`);
        }
      } else if (unitResponse.body.location.kind !== 'StationQueue'
          || unitResponse.body.location.lineId !== context.productionLineDefinitionId
          || unitResponse.body.location.stationSystemId !== context.entryStationSystemId) {
        throw new Error('Production Unit is not queued at the immutable release entry Station.');
      }

      monitoringScopeRef.current = runScope;
      await synchronizeRuntimeProductionRunGroup(
        connection,
        joinedMonitoringScopeRef.current,
        runScope);
      joinedMonitoringScopeRef.current = runScope;
      setActiveProductionRunId(productionRunId);
      setLastProjectRun(null);
      setWorkspaceMode('run');
      setActiveNav('topology');
      setStations([]);
      setTargetStatuses([]);
      setTimeline([]);

      const response = await submitProductionRun(request);
      if (!response.ok || !response.body) {
        throw new Error(`${response.status} ${response.text}`);
      }

      assertProductionRunResponseIdentity(
        response.body,
        runScope,
        request,
        productionRunForm.productionUnitIdentityValue);

      setLastProjectRun(response.body);
      setRunDialogOpen(false);
      setMessage(`Production run ${response.body.executionStatus}: ${response.body.productionRunId}`);
      const stationRows = (await getStationStatuses(runScope))
        .filter(status => runtimeStatusMatchesScope(status, runScope)
          && isRuntimeSessionStatus(status.sessionStatus));
      setStations(stationRows);
      const targetRows = await getTargetStatuses(
        runScope,
        stationRows.map(station => station.stationSystemId));
      setTargetStatuses(targetRows.filter(status =>
        runtimeStatusMatchesScope(status, runScope)
        && isRuntimeCommandStatus(status.commandStatus)));
      const runtimeSessionId = latestProductionRunSessionId(response.body);
      if (runtimeSessionId) {
        setTimeline((await getTimeline(runtimeSessionId, runScope))
          .filter(entry => runtimeTimelineMatchesScope(entry, runScope)));
      }
    } catch (error) {
      setMessage(`Production run failed: ${String(error)}`);
      monitoringScopeRef.current = null;
      await synchronizeRuntimeProductionRunGroup(
        connection,
        joinedMonitoringScopeRef.current,
        null).catch(() => undefined);
      joinedMonitoringScopeRef.current = null;
      setActiveProductionRunId(null);
      setWorkspaceMode('edit');
      setStations([]);
      setTargetStatuses([]);
      setTimeline([]);
    } finally {
      setBusy(false);
    }
  }, [activeApplicationSnapshot, activeWorkspace, backendStatus?.health, hubState, productionRunForm]);

  const applyApplicationSelection = useCallback((applicationId: string) => {
    if (!applicationId) {
      return;
    }

    setActiveApplicationId(applicationId);
    if (activeWorkspace) {
      setEditorTabState(openEditorTab(
        { tabs: [], activeId: null },
        createEditorTab(activeWorkspace, applicationId, 'projects')));
    }
    setActiveNav('projects');
    setLastProjectRun(null);
    setActiveProductionRunId(null);
    setStations([]);
    setTargetStatuses([]);
    setTimeline([]);
    setWorkspaceMode('edit');
    setMessage(`Application selected ${applicationId}`);
  }, [activeWorkspace]);

  const selectApplication = useCallback((applicationId: string) => {
    if (!applicationId || applicationId === activeApplication?.applicationId) {
      return;
    }
    runWithUnsavedGuard(
      'Switch Application?',
      'Open editors in this Application contain unsaved changes.',
      () => applyApplicationSelection(applicationId));
  }, [activeApplication?.applicationId, applyApplicationSelection, runWithUnsavedGuard]);

  const openEditor = useCallback((nav: NavId) => {
    if (!activeWorkspace || !activeApplication) {
      setActiveNav('projects');
      return;
    }
    const tab = createEditorTab(activeWorkspace, activeApplication.applicationId, nav);
    setEditorTabState(current => openEditorTab(current, tab));
    setActiveNav(nav);
    setWorkspaceMode(current => nav === 'dashboard'
      ? 'run'
      : nav === 'topology'
        ? current
        : 'edit');
  }, [activeApplication, activeWorkspace]);

  const activateEditor = useCallback((tab: EditorTabModel) => {
    setEditorTabState(current => activateEditorTab(current, tab.id));
    const nav = tab.kind as NavId;
    setActiveNav(nav);
    setWorkspaceMode(current => nav === 'dashboard'
      ? 'run'
      : nav === 'topology'
        ? current
        : 'edit');
  }, []);

  const requestCloseEditor = useCallback((tab: EditorTabModel) => {
    const applyClose = (): void => {
      const next = closeEditorTab(editorTabState, tab.id);
      setEditorTabState(next);
      const nextTab = next.tabs.find(candidate => candidate.id === next.activeId);
      setActiveNav((nextTab?.kind as NavId | undefined) ?? 'projects');
    };
    runWithUnsavedGuard(
      `Close ${tab.label}?`,
      'This editor contains unsaved changes.',
      applyClose,
      new Set([tab.id]));
  }, [editorTabState, runWithUnsavedGuard]);

  const activateEditorById = useCallback((documentId: string) => {
    const tab = editorTabState.tabs.find(candidate => candidate.id === documentId);
    if (tab) activateEditor(tab);
  }, [activateEditor, editorTabState.tabs]);

  useEffect(() => desktop.onCloseRequested(requestId => {
    runWithUnsavedGuard(
      'Close OpenLineOps?',
      'Open editors contain unsaved changes.',
      () => desktop.respondToCloseRequest(requestId, true),
      null,
      () => desktop.respondToCloseRequest(requestId, false));
  }), [runWithUnsavedGuard]);

  useEffect(() => {
    if (!activeWorkspace || !activeApplication) {
      return;
    }
    const matchingTab = editorTabState.tabs.find(tab => tab.kind === activeNav);
    if (!matchingTab) {
      setEditorTabState(current => openEditorTab(
        current,
        createEditorTab(activeWorkspace, activeApplication.applicationId, activeNav)));
      return;
    }
    if (editorTabState.activeId !== matchingTab.id) {
      setEditorTabState(current => activateEditorTab(current, matchingTab.id));
    }
  }, [activeApplication, activeNav, activeWorkspace, editorTabState.activeId, editorTabState.tabs]);

  const renderEditorPanel = useCallback((panelNav: NavId) => {
    if (panelNav === 'topology') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="2D layout" />}>
          <TopologyDesigner
            activeWorkspace={activeWorkspace}
            activeApplicationId={activeApplication?.applicationId ?? null}
            projectSnapshotId={activeApplicationSnapshot?.snapshotId ?? null}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            workspaceMode={workspaceMode}
            projectionConnected={operationsProjection.connected}
            runtimeProjection={operationsProjection.lineState}
            onWorkspaceChanged={setActiveWorkspace}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (panelNav === 'dashboard') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="line operations" />}>
          <OperationsWorkbench
            activeRuns={operationsProjection.activeRuns}
            lineState={operationsProjection.lineState}
            filters={operationsProjection.filters}
            selectedLineId={operationsProjection.selectedLineId}
            connected={operationsProjection.connected}
            refreshing={operationsProjection.refreshing}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            lastSynchronizedAtUtc={operationsProjection.lastSynchronizedAtUtc}
            projectId={activeWorkspace?.project.projectId ?? null}
            applicationId={activeApplication?.applicationId ?? null}
            projectSnapshotId={activeApplicationSnapshot?.snapshotId ?? null}
            onFilterChanged={operationsProjection.setFilter}
            onRefresh={operationsProjection.refresh}
            onOpenTopology={() => {
              setWorkspaceMode('run');
              setActiveNav('topology');
            }}
            onActiveRunChanged={setActiveProductionRunId}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (panelNav === 'processes') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="Processes" />}>
          <ProcessWorkbench
            activeWorkspace={activeWorkspace}
            activeApplicationId={activeApplication?.applicationId ?? null}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (panelNav === 'production') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="Production lines" />}>
          <ProductionWorkbench
            activeWorkspace={activeWorkspace}
            activeApplicationId={activeApplication?.applicationId ?? null}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            onWorkspaceChanged={setActiveWorkspace}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (panelNav === 'programs') {
      return (
        <React.Suspense fallback={<WorkbenchLoading label="program resources" />}>
          <ExternalProgramWorkbench
            activeWorkspace={activeWorkspace}
            activeApplicationId={activeApplication?.applicationId ?? null}
            isBackendHealthy={backendStatus?.health === 'Healthy'}
            onMessage={setMessage}
          />
        </React.Suspense>
      );
    }

    if (panelNav === 'projects') {
      return (
        <ProjectsWorkbench
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          onActiveApplicationChanged={selectApplication}
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          statusMessage={message}
          onWorkspaceChanged={selectWorkspace}
          onMessage={setMessage}
        />
      );
    }

    if (panelNav === 'engineering') {
      return (
        <EngineeringWorkbench
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    if (panelNav === 'devices') {
      return (
        <DevicesWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    if (panelNav === 'trace') {
      return (
        <TraceWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          projectId={activeWorkspace?.project.projectId ?? null}
          applicationId={activeApplication?.applicationId ?? null}
          onMessage={setMessage}
        />
      );
    }

    if (panelNav === 'plugins') {
      return (
        <PluginsWorkbench
          isBackendHealthy={backendStatus?.health === 'Healthy'}
          onMessage={setMessage}
        />
      );
    }

    return <SecondaryView activeNav={panelNav} traceRows={traceRows} stations={stations} />;
  }, [activeApplication?.applicationId, activeApplicationSnapshot?.snapshotId, activeWorkspace, backendStatus?.health, message, operationsProjection, selectApplication, selectWorkspace, stations, traceRows, workspaceMode]);

  const activeEditorTab = editorTabState.tabs.find(tab => tab.id === editorTabState.activeId) ?? null;
  const activeEditorDocument = activeEditorTab ? documentRegistry.get(activeEditorTab.id) : null;
  const editorProblemCount = documentRegistry.problems().length;

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
            onClick={openRunProjectDialog}
            disabled={busy || !activeWorkspace || !activeApplicationSnapshot || backendStatus?.health !== 'Healthy' || hubState !== 'Connected'}
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
                onClick={() => openEditor(item.id)}
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
          onNavigate={openEditor}
          onSelectApplication={selectApplication}
          onClose={closeWorkspace}
        />
      ) : null}

      <section className={activeWorkspace ? 'ide-editor-area' : 'ide-editor-area start-center-mode'}>
        {activeWorkspace ? (
          <>
            <EditorTabStrip
              tabs={editorTabState.tabs}
              activeId={editorTabState.activeId}
              registry={documentRegistry}
              iconFor={editorTabIcon}
              onActivate={activateEditor}
              onClose={requestCloseEditor}
            />

            <div className="ide-editor-toolbar">
          <div>
            <strong>{activeTitle}</strong>
            <span>{message}</span>
          </div>
          <div className="ide-editor-toolbar-actions">
            <button
              type="button"
              className="ide-save-current"
              disabled={!activeEditorDocument?.dirty || !activeEditorDocument.canSave}
              onClick={() => {
                if (activeEditorTab) void documentRegistry.save(activeEditorTab.id);
              }}
              data-testid="save-active-editor"
              title="Save active editor"
            >
              Save
            </button>
            <SaveAllButton
              registry={documentRegistry}
              onResult={success => setMessage(success ? 'All open editors saved.' : 'Save All stopped on a failed editor.')}
            />
          </div>
          {activeNav === 'dashboard' ? (
            <div className="ide-editor-toolbar-actions">
              <span>Latest: {lastProjectRun?.productionRunId ?? latestStation?.latestSessionId ?? 'none'}</span>
            </div>
          ) : null}
            </div>

            <EditorConflictNotice document={activeEditorDocument} />

            <div className="ide-editor-surface">
              {editorTabState.tabs.map(tab => (
                <EditorDocumentHost
                  key={tab.id}
                  documentId={tab.id}
                  title={tab.label}
                  registry={documentRegistry}
                  active={tab.id === editorTabState.activeId}
                >
                  {renderEditorPanel(tab.kind as NavId)}
                </EditorDocumentHost>
              ))}
            </div>

            <div className={`ide-bottom-panel ${workspaceMode === 'run' ? 'expanded' : ''}`}>
          <div className="ide-bottom-panel-title">
            <PanelBottom size={14} />
            <strong>
              {workspaceMode === 'run'
                ? 'Runtime Output'
                : `Problems (${editorProblemCount}) · Output · Terminal`}
            </strong>
            <span>{message}</span>
          </div>
          {workspaceMode === 'run' ? (
            <div className="ide-runtime-summary">
              <InfoCell label="Production Run" value={lastProjectRun?.productionRunId ?? 'waiting'} />
              <InfoCell label="Execution" value={lastProjectRun?.executionStatus ?? 'Idle'} />
              <InfoCell
                label="Production Unit"
                value={lastProjectRun
                  ? `${lastProjectRun.productionUnitIdentity.modelId} / ${lastProjectRun.productionUnitIdentity.inputKey}=${lastProjectRun.productionUnitIdentity.value}`
                  : 'waiting'}
              />
              <InfoCell label="Judgement" value={lastProjectRun?.judgement ?? 'Unknown'} />
              <InfoCell label="Disposition" value={lastProjectRun?.disposition ?? 'InProcess'} />
              <InfoCell label="Actor" value={lastProjectRun?.actorId ?? 'waiting'} />
              <InfoCell label="Lot" value={lastProjectRun?.lotId ?? 'none'} />
              <InfoCell label="Carrier" value={lastProjectRun?.carrierId ?? 'none'} />
              <InfoCell
                label="Operations"
                value={lastProjectRun
                  ? `${lastProjectRun.operations.filter(operation => operation.executionStatus === 'Completed').length}/${lastProjectRun.operations.length}`
                  : 'waiting'}
              />
              <InfoCell label="Runtime Session" value={lastProjectRunSessionId ?? 'waiting'} />
              <InfoCell label="Timeline" value={`${timeline.length} events`} />
              <InfoCell label="Alarms" value={`${alarms.filter(alarm => !alarm.isAcknowledged).length} open`} />
            </div>
          ) : (
            <EditorProblemsPanel registry={documentRegistry} onActivateDocument={activateEditorById} />
          )}
            </div>
          </>
        ) : (
          <div className="ide-start-surface">{renderEditorPanel('projects')}</div>
        )}
      </section>

      <footer className="ide-statusbar">
        <span><Layers3 size={12} /> {activeWorkspace?.project.projectId ?? 'No project open'}{activeApplication ? ` / ${activeApplication.applicationId}` : ''}</span>
        <span>{activeApplicationSnapshot ? `Snapshot ${activeApplicationSnapshot.snapshotId}` : 'Draft workspace'}</span>
        <span>{platform ? `${platform.serviceName} ${platform.version}` : 'OpenLineOps.Api'}</span>
        <span>{platform?.environment ?? 'local'} · PID {backendStatus?.pid ?? '—'} · {healthStatus}</span>
      </footer>

      {runDialogOpen ? (
      <dialog
        ref={runDialogRef}
        className="project-start-dialog production-run-dialog"
        aria-labelledby="production-run-dialog-title"
        aria-busy={busy}
        onCancel={event => {
          event.preventDefault();
          if (!busy) {
            setRunDialogOpen(false);
          }
        }}
        data-testid="production-run-dialog"
      >
        <header>
          <div>
            <span>Immutable production snapshot</span>
            <h2 id="production-run-dialog-title">Run Project</h2>
          </div>
          <button
            type="button"
            onClick={() => setRunDialogOpen(false)}
            disabled={busy}
            aria-label="Close Run Project"
          >
            <X size={17} />
          </button>
        </header>
        <form
          id="production-run-form"
          className="project-start-dialog-body production-run-dialog-form"
          onSubmit={event => {
            event.preventDefault();
            void runActiveProject();
          }}
        >
          <div className="production-run-context">
            <span>Snapshot</span>
            <strong>{activeApplicationSnapshot?.snapshotId ?? 'Unavailable'}</strong>
            <small>{activeWorkspace?.project.projectId} / {activeApplication?.applicationId}</small>
          </div>
          <label>
            <span>Production Unit ID</span>
            <input
              value={productionRunForm.productionUnitId}
              onChange={event => setProductionRunForm(current => ({
                ...current,
                productionUnitId: event.target.value
              }))}
              required
              autoComplete="off"
              data-testid="production-run-unit-id"
            />
          </label>
          <label>
            <span>Production Unit identity</span>
            <input
              value={productionRunForm.productionUnitIdentityValue}
              onChange={event => setProductionRunForm(current => ({
                ...current,
                productionUnitIdentityValue: event.target.value
              }))}
              autoFocus
              required
              autoComplete="off"
              data-testid="production-run-unit-identity"
            />
          </label>
          <label>
            <span>Actor</span>
            <input
              value={productionRunForm.actorId}
              onChange={event => setProductionRunForm(current => ({
                ...current,
                actorId: event.target.value
              }))}
              required
              autoComplete="off"
              data-testid="production-run-actor"
            />
          </label>
          <p>The Unit is registered and arrived at the entry Station before the immutable run is submitted. Resources come only from the frozen Operation definition.</p>
        </form>
        <footer>
          <button
            type="button"
            className="button ghost"
            onClick={() => setRunDialogOpen(false)}
            disabled={busy}
            data-testid="cancel-production-run"
          >
            Cancel
          </button>
          <button
            type="submit"
            form="production-run-form"
            className="button primary"
            disabled={busy}
            data-testid="confirm-production-run"
          >
            <Play size={14} />
            Start Run
          </button>
        </footer>
      </dialog>
      ) : null}
      {pendingUnsavedGuard ? (
        <dialog open className="unsaved-guard-dialog" data-testid="unsaved-changes-dialog">
          <header>
            <div>
              <span>UNSAVED EDITORS</span>
              <h2>{pendingUnsavedGuard.title}</h2>
            </div>
          </header>
          <div className="unsaved-guard-body">
            <p>{pendingUnsavedGuard.detail}</p>
            <ul>
              {documentRegistry.dirtyEntries(pendingUnsavedGuard.documentIds ?? undefined).map(([id, document]) => (
                <li key={id}>
                  <span>{document.title}</span>
                  <small>{document.saveError ?? 'Unsaved changes'}</small>
                </li>
              ))}
            </ul>
          </div>
          <footer>
            <button
              type="button"
              className="button ghost"
              onClick={() => {
                pendingUnsavedGuard.cancel?.();
                setPendingUnsavedGuard(null);
              }}
              data-testid="unsaved-cancel"
            >
              Cancel
            </button>
            <button
              type="button"
              className="button danger"
              onClick={() => {
                documentRegistry.discardAll(pendingUnsavedGuard.documentIds ?? undefined);
                const proceed = pendingUnsavedGuard.proceed;
                setPendingUnsavedGuard(null);
                proceed();
              }}
              data-testid="unsaved-discard"
            >
              Discard Changes
            </button>
            <button
              type="button"
              className="button primary"
              onClick={() => void documentRegistry
                .saveAll(pendingUnsavedGuard.documentIds ?? undefined)
                .then(success => {
                  if (!success) {
                    setMessage('Save failed. The editor remains open with its draft intact.');
                    return;
                  }
                  const proceed = pendingUnsavedGuard.proceed;
                  setPendingUnsavedGuard(null);
                  proceed();
                })}
              data-testid="unsaved-save"
            >
              Save &amp; Continue
            </button>
          </footer>
        </dialog>
      ) : null}
    </main>
  );
}

function createEditorTab(
  workspace: AutomationProjectWorkspaceResponse,
  applicationId: string,
  nav: NavId
): EditorTabModel {
  const navItem = navItems.find(item => item.id === nav);
  return {
    id: `${workspace.project.projectId}\u001f${applicationId}\u001f${nav}`,
    kind: nav,
    label: navItem?.label ?? nav,
    projectId: workspace.project.projectId,
    applicationId
  };
}

function editorTabIcon(kind: string): React.ReactNode {
  const item = navItems.find(candidate => candidate.id === kind);
  const Icon = item?.icon ?? FileSearch;
  return <Icon size={14} />;
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
    { nav: 'topology', label: 'Systems & Layout', detail: application?.topologyId ?? 'not configured', icon: LayoutDashboard },
    { nav: 'production', label: 'Production Lines', detail: 'product models · operations · routes', icon: Factory },
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
    { nav: 'trace', label: 'Trace Evidence', detail: 'production runs · Operations', icon: FileSearch },
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
                  <small>{candidate.projectFilePath ?? candidate.applicationId}</small>
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
          <strong>{workspace.manifestPath.split(/[\\/]/).pop() ?? 'project.oloproj'}</strong>
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
              <div className="surface-row" key={station.stationSystemId}>
                <span>{station.stationSystemId}</span>
                <strong>{station.sessionStatus}</strong>
              </div>
            ))}
            {traceRows.slice(0, 3).map(trace => (
              <div className="surface-row" key={trace.traceRecordId}>
                <span>{trace.productionUnitIdentityValue}</span>
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
  topology: 'The same semantic 2D layout is used for engineering and live production monitoring.',
  production: 'Production lines compose Product Models, Station-bound Operations and typed route graphs.',
  engineering: 'Engineering workspaces, recipes, stations and snapshots use Application-scoped backend contracts.',
  processes: 'Process editing remains API-backed so Electron does not own orchestration rules.',
  programs: 'External programs are Application-owned, hash-frozen resources referenced by ordinary Flow actions.',
  devices: 'Device configuration is read from backend APIs and never from local databases.',
  trace: 'Trace query uses the traceability endpoints and runtime-linked read models.',
  plugins: 'Plugin management uses the canonical manifest and explicit host lifecycle contracts.'
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

function upsertRuntimeTargetStatus(
  items: RuntimeTargetStatus[],
  status: RuntimeTargetStatus
): RuntimeTargetStatus[] {
  const key = runtimeTargetStatusKey(status);
  return [status, ...items.filter(candidate => runtimeTargetStatusKey(candidate) !== key)];
}

function latestProductionRunSessionId(
  run: ProductionRunReadModel | null
): string | null {
  if (!run) {
    return null;
  }

  for (let index = run.operations.length - 1; index >= 0; index -= 1) {
    const runtimeSessionId = run.operations[index]?.runtimeSessionId;
    if (runtimeSessionId) {
      return runtimeSessionId;
    }
  }

  return null;
}

function runtimeTargetStatusKey(status: RuntimeTargetStatus): string {
  return JSON.stringify([
    status.projectId,
    status.applicationId,
    status.projectSnapshotId,
    status.topologyId,
    status.productionRunId,
    status.productionLineDefinitionId,
    status.operationId,
    status.operationAttempt,
    status.runtimeStationId,
    status.stationSystemId,
    status.targetKind,
    status.targetId
  ]);
}

function runtimeStationStatusKey(status: RuntimeStationStatus): string {
  return JSON.stringify([
    status.projectId,
    status.applicationId,
    status.projectSnapshotId,
    status.topologyId,
    status.productionRunId,
    status.productionLineDefinitionId,
    status.operationId,
    status.operationAttempt,
    status.runtimeStationId,
    status.stationSystemId
  ]);
}

function runtimeStatusMatchesScope(
  status: Pick<RuntimeStationStatus, 'projectId' | 'applicationId' | 'projectSnapshotId' | 'topologyId' | 'productionRunId'>,
  scope: RuntimeMonitoringScope | null
): boolean {
  return scope !== null
    && status.projectId === scope.projectId
    && status.applicationId === scope.applicationId
    && status.projectSnapshotId === scope.projectSnapshotId
    && status.topologyId === scope.topologyId
    && status.productionRunId === scope.productionRunId;
}

function runtimeTimelineMatchesScope(
  entry: RuntimeTimelineEntry,
  scope: RuntimeMonitoringScope | null
): boolean {
  return scope !== null
    && entry.projectId === scope.projectId
    && entry.applicationId === scope.applicationId
    && entry.projectSnapshotId === scope.projectSnapshotId
    && entry.topologyId === scope.topologyId
    && entry.productionRunId === scope.productionRunId;
}

function sameRuntimeStationScope(
  target: RuntimeTargetStatus,
  station: RuntimeStationStatus
): boolean {
  return target.projectId === station.projectId
    && target.applicationId === station.applicationId
    && target.projectSnapshotId === station.projectSnapshotId
    && target.topologyId === station.topologyId
    && target.productionRunId === station.productionRunId
    && target.productionLineDefinitionId === station.productionLineDefinitionId
    && target.operationId === station.operationId
    && target.operationAttempt === station.operationAttempt
    && target.runtimeStationId === station.runtimeStationId
    && target.stationSystemId === station.stationSystemId;
}

function runtimeMonitoringScopesEqual(
  left: RuntimeMonitoringScope | null,
  right: RuntimeMonitoringScope | null
): boolean {
  return left === right
    || (left !== null
      && right !== null
      && left.projectId === right.projectId
      && left.applicationId === right.applicationId
      && left.projectSnapshotId === right.projectSnapshotId
      && left.topologyId === right.topologyId
      && left.productionRunId === right.productionRunId);
}

async function joinRuntimeProductionRunGroup(
  connection: ReturnType<typeof createRuntimeHubConnection>,
  scope: RuntimeMonitoringScope
): Promise<void> {
  await connection.invoke(
    'JoinProductionRunGroup',
    scope.projectId,
    scope.applicationId,
    scope.projectSnapshotId,
    scope.topologyId,
    scope.productionRunId);
}

async function synchronizeRuntimeProductionRunGroup(
  connection: ReturnType<typeof createRuntimeHubConnection>,
  previousScope: RuntimeMonitoringScope | null,
  nextScope: RuntimeMonitoringScope | null
): Promise<void> {
  if (runtimeMonitoringScopesEqual(previousScope, nextScope)) {
    return;
  }

  if (previousScope) {
    await connection.invoke(
      'LeaveProductionRunGroup',
      previousScope.projectId,
      previousScope.applicationId,
      previousScope.projectSnapshotId,
      previousScope.topologyId,
      previousScope.productionRunId);
  }

  if (nextScope) {
    await joinRuntimeProductionRunGroup(connection, nextScope);
  }
}

function validateProductionRunForm(form: ProductionRunFormState): string | null {
  if (!isCanonicalUuid(form.productionUnitId)) {
    return 'Production Unit ID must be a non-empty canonical UUID.';
  }

  if (!isCanonicalRunIdentity(form.productionUnitIdentityValue)) {
    return 'Production Unit identity is required and cannot start or end with whitespace.';
  }

  if (!isCanonicalRunIdentity(form.actorId)) {
    return 'Actor is required and cannot start or end with whitespace.';
  }

  return null;
}

function isCanonicalUuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value);
}

function isCanonicalRunIdentity(value: string): boolean {
  return value.length > 0
    && !/\s/u.test(value[0] ?? '')
    && !/\s/u.test(value[value.length - 1] ?? '');
}

function assertProductionRunResponseIdentity(
  response: ProductionRunReadModel,
  scope: RuntimeMonitoringScope,
  request: SubmitProductionRunRequest,
  expectedIdentityValue: string
): void {
  const terminalExecutionStatuses = new Set(['Completed', 'Failed', 'TimedOut', 'Canceled', 'Rejected']);
  if (response.productionRunId !== scope.productionRunId
      || response.projectId !== scope.projectId
      || response.applicationId !== scope.applicationId
      || response.projectSnapshotId !== scope.projectSnapshotId
      || response.topologyId !== scope.topologyId
      || response.productionLineDefinitionId.length === 0
      || response.productionUnitId !== request.productionUnitId
      || response.productionUnitIdentity.value !== expectedIdentityValue
      || response.actorId !== request.actorId
      || response.isTerminal !== terminalExecutionStatuses.has(response.executionStatus)) {
    throw new Error('Production run response identity did not exactly match the submit request.');
  }
}

function isRuntimeSessionStatus(value: string): value is RuntimeSessionStatus {
  return runtimeSessionStatusTokens.has(value);
}

function isRuntimeCommandStatus(value: string): value is RuntimeCommandStatus {
  return runtimeCommandStatusTokens.has(value);
}

const runtimeSessionStatusTokens = new Set<string>([
  'Created',
  'Queued',
  'Running',
  'Pausing',
  'Paused',
  'Stopping',
  'Stopped',
  'Completed',
  'Failed',
  'Canceled'
]);

const runtimeCommandStatusTokens = new Set<string>([
  'Pending',
  'Accepted',
  'InProgress',
  'Completed',
  'Failed',
  'TimedOut',
  'Canceled',
  'Rejected'
]);

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
