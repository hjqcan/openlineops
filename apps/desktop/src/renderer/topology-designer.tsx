import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  BoxSelect,
  Boxes,
  ChevronRight,
  CircleDot,
  Cuboid,
  Factory,
  Grid2X2,
  Layers3,
  LockKeyhole,
  MousePointer2,
  Plus,
  RefreshCw,
  Save,
  Settings2,
  Trash2,
  Waypoints
} from 'lucide-react';
import type {
  AddSiteLayoutElementRequest,
  AutomationProjectWorkspaceResponse,
  AutomationSystemResponse,
  AutomationTopologyResponse,
  ProjectApplicationResponse,
  RuntimeStationStatus,
  RuntimeTargetStatus,
  SiteLayoutElementResponse,
  SiteLayoutResponse,
  UpdateSiteLayoutElementGeometryRequest
} from './contracts';
import {
  addAutomationSystem,
  addSiteLayoutElement,
  addSlotDefinition,
  addSlotGroup,
  createAutomationTopology,
  createSiteLayout,
  deleteAutomationSystem,
  deleteSlotDefinition,
  deleteSlotGroup,
  getAutomationTopology,
  getSiteLayout,
  linkProjectTopology,
  saveAutomationProjectManifest,
  updateAutomationSystem,
  updateSiteLayoutElementGeometry,
  updateSlotDefinition,
  updateSlotGroup
} from './api';

type WorkspaceMode = 'edit' | 'run';
type CanvasMode = 'edit' | 'monitor';
type PaletteItemKind = 'Station' | 'System' | 'Group' | 'Slot';
type OperationalState = 'Idle' | 'Running' | 'Completed' | 'Failed' | 'Offline';

interface TopologyDesignerProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  workspaceMode: WorkspaceMode;
  runtimeConnected: boolean;
  stations: RuntimeStationStatus[];
  targetStatuses: RuntimeTargetStatus[];
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
}

interface ElementDescriptor {
  displayName: string;
  detail: string;
  system?: AutomationSystemResponse;
}

interface ElementDragState {
  pointerId: number;
  startClientX: number;
  startClientY: number;
  containerWidthPx: number;
  containerHeightPx: number;
  previousGeometry: UpdateSiteLayoutElementGeometryRequest;
  latestGeometry: UpdateSiteLayoutElementGeometryRequest;
  moved: boolean;
}

interface SemanticPropertiesDraft {
  displayName: string;
  systemType: string;
  groupKind: string;
  groupCapacity: number;
  slotAddress: string;
  slotMaterialKind: string;
  slotEnabled: boolean;
}

const slotGroupKinds = [
  'FixtureNest',
  'TesterBank',
  'TrayRow',
  'BufferLane',
  'RobotPickGroup',
  'LogicalBatch'
];

const slotMaterialKinds = [
  'Dut',
  'Carrier',
  'FixturePosition',
  'TrayPosition',
  'LogicalWorkItem'
];

const paletteItems: Array<{
  kind: PaletteItemKind;
  label: string;
  detail: string;
  icon: React.ComponentType<{ size?: number }>;
}> = [
  { kind: 'Station', label: 'Station', detail: 'Runnable System boundary', icon: Factory },
  { kind: 'System', label: 'System', detail: 'Station component or tester', icon: Cuboid },
  { kind: 'Group', label: 'Slot Group', detail: 'Fixture or carrier region', icon: Grid2X2 },
  { kind: 'Slot', label: 'DUT Slot', detail: 'Observable work position', icon: CircleDot }
];

const statusLegend: Array<{ state: OperationalState; label: string }> = [
  { state: 'Idle', label: 'Idle' },
  { state: 'Running', label: 'Working' },
  { state: 'Completed', label: 'Completed' },
  { state: 'Failed', label: 'Failed' },
  { state: 'Offline', label: 'Offline' }
];

export function TopologyDesigner({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  workspaceMode,
  runtimeConnected,
  stations,
  targetStatuses,
  onWorkspaceChanged,
  onMessage
}: TopologyDesignerProps): React.ReactElement {
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [layout, setLayout] = useState<SiteLayoutResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [saveState, setSaveState] = useState<'saved' | 'saving' | 'error'>('saved');
  const [selectedElementId, setSelectedElementId] = useState<string | null>(null);
  const [canvasMode, setCanvasMode] = useState<CanvasMode>('edit');
  const [deleteConfirmElementId, setDeleteConfirmElementId] = useState<string | null>(null);

  const activeApplication = activeWorkspace?.project.applications.find(
    application => application.applicationId === activeApplicationId)
    ?? activeWorkspace?.project.applications[0]
    ?? null;
  const apiScope = useMemo(
    () => activeWorkspace && activeApplication
      ? {
        projectId: activeWorkspace.project.projectId,
        applicationId: activeApplication.applicationId
      }
      : null,
    [activeApplication, activeWorkspace]);
  const effectiveMode: CanvasMode = workspaceMode === 'run' ? 'monitor' : canvasMode;
  const editable = effectiveMode === 'edit' && isBackendHealthy && !busy;
  const selectedElement = useMemo(
    () => layout?.elements.find(element => element.elementId === selectedElementId) ?? null,
    [layout, selectedElementId]);
  const stationStatuses = useMemo(
    () => new Map(stations.map(status => [status.stationSystemId, status])),
    [stations]);
  const targetStatusByKey = useMemo(
    () => new Map(targetStatuses.map(status => [
      runtimeTargetStatusKey(status.stationSystemId, status.targetKind, status.targetId),
      status
    ])),
    [targetStatuses]);

  const refresh = useCallback(async () => {
    if (!activeApplication || !apiScope) {
      setTopology(null);
      setLayout(null);
      return;
    }

    if (!isBackendHealthy) {
      return;
    }

    if (!activeApplication.topologyId) {
      setTopology(null);
      setLayout(null);
      return;
    }

    const [topologyResponse, layoutResponse] = await Promise.all([
      getAutomationTopology(activeApplication.topologyId, apiScope),
      getSiteLayout(createLayoutId(activeApplication.applicationId), apiScope)
    ]);
    setTopology(topologyResponse.ok && topologyResponse.body ? topologyResponse.body : null);
    setLayout(layoutResponse.ok && layoutResponse.body ? layoutResponse.body : null);
  }, [activeApplication, apiScope, isBackendHealthy]);

  useEffect(() => {
    refresh().catch(error => onMessage(`2D layout refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

  useEffect(() => {
    setSelectedElementId(current => layout?.elements.some(element => element.elementId === current)
      ? current
      : layout?.elements[0]?.elementId ?? null);
  }, [layout]);

  useEffect(() => {
    setDeleteConfirmElementId(null);
  }, [effectiveMode, selectedElementId]);

  const newLayout = useCallback(async () => {
    if (!activeWorkspace || !activeApplication || !apiScope) {
      onMessage('Open a project application before creating a layout.');
      return;
    }

    setBusy(true);
    try {
      let nextTopology = topology;
      let linkedWorkspace: AutomationProjectWorkspaceResponse | null = null;
      if (!nextTopology) {
        const topologyId = activeApplication.topologyId
          ?? createTopologyId(activeApplication.applicationId);
        nextTopology = await requireBody(
          createAutomationTopology({
            topologyId,
            displayName: `${activeApplication.displayName} Systems`
          }, apiScope),
          'Create topology');

        const linkedProject = await linkProjectTopology(
          activeWorkspace.project.projectId,
          activeApplication.applicationId,
          { topologyId });
        if (!linkedProject.ok || !linkedProject.body) {
          throw new Error(`Link topology failed: ${linkedProject.status} ${linkedProject.text}`);
        }

        const savedWorkspace = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
        if (!savedWorkspace.ok || !savedWorkspace.body) {
          throw new Error(`Save manifest failed: ${savedWorkspace.status} ${savedWorkspace.text}`);
        }
        linkedWorkspace = savedWorkspace.body;
      }

      const nextLayout = layout ?? await requireBody(
        createSiteLayout({
          layoutId: createLayoutId(activeApplication.applicationId),
          topologyId: nextTopology.topologyId,
          displayName: `${activeApplication.displayName} 2D Layout`,
          canvasWidth: 1200,
          canvasHeight: 680,
          units: 'px'
        }, apiScope),
        'Create 2D layout');

      setTopology(nextTopology);
      setLayout(nextLayout);
      setSelectedElementId(nextLayout.elements[0]?.elementId ?? null);
      if (linkedWorkspace) {
        onWorkspaceChanged(linkedWorkspace);
      }
      onMessage(`2D layout ready ${nextLayout.layoutId}`);
    } catch (error) {
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [activeApplication, activeWorkspace, apiScope, layout, onMessage, onWorkspaceChanged, topology]);

  const addPaletteItem = useCallback(async (
    kind: PaletteItemKind,
    rootPlacement?: { x: number; y: number }
  ) => {
    if (!editable || !topology || !layout || !apiScope || !activeApplication) {
      onMessage('Create a 2D layout and enter Edit mode before adding elements.');
      return;
    }

    const selection = selectedElement
      ?? layout.elements.find(element => element.kind === 'SystemShape')
      ?? null;
    setBusy(true);
    try {
      if (kind === 'Station') {
        const prefix = `${activeApplication.applicationId}.station.`;
        const sequence = nextSequence(topology.systems.map(system => system.systemId), prefix);
        const systemId = `${prefix}${sequence}`;
        const initialCapabilityIds = topology.capabilities[0]
          ? [topology.capabilities[0].capabilityId]
          : [];
        const nextTopology = await requireBody(
          addAutomationSystem(topology.topologyId, {
            systemId,
            parentSystemId: null,
            kind: 'Station',
            systemType: 'automation.station',
            displayName: `Station ${String(sequence).padStart(2, '0')}`,
            requiredCapabilityIds: initialCapabilityIds,
            providedCapabilityIds: initialCapabilityIds,
            metadata: {}
          }, apiScope),
          `Add Station ${systemId}`);
        const geometry = clampGeometry({
          x: rootPlacement ? rootPlacement.x - 200 : 48 + ((sequence - 1) % 2) * 480,
          y: rootPlacement ? rootPlacement.y - 145 : 54 + Math.floor((sequence - 1) / 2) * 330,
          width: 400,
          height: 290,
          rotationDegrees: 0
        }, layout.canvasWidth, layout.canvasHeight);
        const element = createElementRequest(
          `${systemId}.shape`,
          'SystemShape',
          'System',
          systemId,
          null,
          geometry,
          10,
          { appearance: 'station' });
        const nextLayout = await requireBody(
          addSiteLayoutElement(layout.layoutId, element, apiScope),
          `Place Station ${systemId}`);
        setTopology(nextTopology);
        setLayout(nextLayout);
        setSelectedElementId(element.elementId);
        onMessage(`Station added ${systemId}`);
        return;
      }

      const stationElement = resolveStationElement(selection, layout, topology);
      if (!stationElement) {
        throw new Error(`Select a Station before adding a ${kind}.`);
      }
      const stationSystem = topology.systems.find(
        system => system.systemId === stationElement.target.targetId && system.kind === 'Station');
      if (!stationSystem) {
        throw new Error('The selected layout element is not a Station System.');
      }

      if (kind === 'System') {
        const prefix = `${stationSystem.systemId}.component.`;
        const sequence = nextSequence(topology.systems.map(system => system.systemId), prefix);
        const systemId = `${prefix}${sequence}`;
        const initialCapabilityIds = topology.capabilities[0]
          ? [topology.capabilities[0].capabilityId]
          : [];
        const nextTopology = await requireBody(
          addAutomationSystem(topology.topologyId, {
            systemId,
            parentSystemId: stationSystem.systemId,
            kind: 'System',
            systemType: 'automation.component',
            displayName: `System ${sequence}`,
            requiredCapabilityIds: initialCapabilityIds,
            providedCapabilityIds: initialCapabilityIds,
            metadata: {}
          }, apiScope),
          `Add System ${systemId}`);
        const stationOrigin = absoluteElementOrigin(stationElement, layout);
        const geometry = clampGeometry({
          x: rootPlacement ? rootPlacement.x - stationOrigin.x - 75 : 20 + ((sequence - 1) % 2) * 165,
          y: rootPlacement ? rootPlacement.y - stationOrigin.y - 28 : 58 + Math.floor((sequence - 1) / 2) * 66,
          width: 150,
          height: 56,
          rotationDegrees: 0
        }, stationElement.width, stationElement.height);
        const element = createElementRequest(
          `${systemId}.shape`,
          'SystemShape',
          'System',
          systemId,
          stationElement.elementId,
          geometry,
          15,
          { appearance: 'component' });
        const nextLayout = await requireBody(
          addSiteLayoutElement(layout.layoutId, element, apiScope),
          `Place System ${systemId}`);
        setTopology(nextTopology);
        setLayout(nextLayout);
        setSelectedElementId(element.elementId);
        onMessage(`System added ${systemId}`);
        return;
      }

      if (kind === 'Group') {
        const prefix = `${stationSystem.systemId}.group.`;
        const sequence = nextSequence(topology.slotGroups.map(group => group.slotGroupId), prefix);
        const slotGroupId = `${prefix}${sequence}`;
        const nextTopology = await requireBody(
          addSlotGroup(topology.topologyId, {
            slotGroupId,
            parentSystemId: stationSystem.systemId,
            displayName: `Fixture Group ${sequence}`,
            kind: 'FixtureNest',
            capacity: 8
          }, apiScope),
          `Add Slot Group ${slotGroupId}`);
        const stationOrigin = absoluteElementOrigin(stationElement, layout);
        const geometry = clampGeometry({
          x: rootPlacement ? rootPlacement.x - stationOrigin.x - 90 : 20 + ((sequence - 1) % 2) * 190,
          y: rootPlacement ? rootPlacement.y - stationOrigin.y - 60 : 145 + Math.floor((sequence - 1) / 2) * 130,
          width: 180,
          height: 120,
          rotationDegrees: 0
        }, stationElement.width, stationElement.height);
        const element = createElementRequest(
          `${slotGroupId}.region`,
          'GroupRegion',
          'SlotGroup',
          slotGroupId,
          stationElement.elementId,
          geometry,
          20,
          { appearance: 'fixture-group' });
        const nextLayout = await requireBody(
          addSiteLayoutElement(layout.layoutId, element, apiScope),
          `Place Slot Group ${slotGroupId}`);
        setTopology(nextTopology);
        setLayout(nextLayout);
        setSelectedElementId(element.elementId);
        onMessage(`Slot Group added ${slotGroupId}`);
        return;
      }

      const groupElement = resolveGroupElement(selection, layout);
      if (!groupElement) {
        throw new Error('Select a Slot Group before adding a DUT Slot.');
      }
      const group = topology.slotGroups.find(candidate => candidate.slotGroupId === groupElement.target.targetId);
      if (!group) {
        throw new Error('The selected layout element does not reference a Slot Group.');
      }
      if (group.slotIds.length >= group.capacity) {
        throw new Error(`Slot Group ${group.displayName} is full (${group.capacity}/${group.capacity}).`);
      }
      const sequence = nextSequence(group.slotIds, `${group.slotGroupId}.slot.`);
      const slotId = `${group.slotGroupId}.slot.${sequence}`;
      const nextTopology = await requireBody(
        addSlotDefinition(topology.topologyId, {
          slotGroupId: group.slotGroupId,
          slotId,
          parentSystemId: group.parentSystemId,
          address: `S${sequence}`,
          displayName: `Slot ${sequence}`,
          materialKind: 'Dut',
          isEnabled: true
        }, apiScope),
        `Add Slot ${slotId}`);
      const groupOrigin = absoluteElementOrigin(groupElement, layout);
      const slotGeometry = clampGeometry({
        x: rootPlacement ? rootPlacement.x - groupOrigin.x - 14 : 10 + ((sequence - 1) % 4) * 38,
        y: rootPlacement ? rootPlacement.y - groupOrigin.y - 13 : 42 + Math.floor((sequence - 1) / 4) * 34,
        width: 28,
        height: 26,
        rotationDegrees: 0
      }, groupElement.width, groupElement.height);
      const element = createElementRequest(
        `${slotId}.shape`,
        'SlotShape',
        'Slot',
        slotId,
        groupElement.elementId,
        slotGeometry,
        30,
        { appearance: 'dut-slot' });
      const nextLayout = await requireBody(
        addSiteLayoutElement(layout.layoutId, element, apiScope),
        `Place Slot ${slotId}`);
      setTopology(nextTopology);
      setLayout(nextLayout);
      setSelectedElementId(element.elementId);
      onMessage(`DUT Slot added ${slotId}`);
    } catch (error) {
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [activeApplication, apiScope, editable, layout, onMessage, selectedElement, topology]);

  const previewGeometry = useCallback((
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest
  ) => {
    setLayout(current => updateLayoutGeometry(current, elementId, geometry));
  }, []);

  const commitGeometry = useCallback(async (
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ) => {
    if (!layout || !apiScope || effectiveMode !== 'edit' || !isBackendHealthy) {
      return;
    }

    setSaveState('saving');
    setLayout(current => updateLayoutGeometry(current, elementId, geometry));
    try {
      const response = await updateSiteLayoutElementGeometry(
        layout.layoutId,
        elementId,
        normalizeGeometry(geometry),
        apiScope);
      if (!response.ok || !response.body) {
        setLayout(current => updateLayoutGeometry(current, elementId, previousGeometry));
        setSaveState('error');
        onMessage(`Layout save failed: ${response.status} ${response.text}`);
        return;
      }

      setLayout(response.body);
      setSelectedElementId(elementId);
      setSaveState('saved');
      onMessage(`Layout saved ${elementId}`);
    } catch (error) {
      setLayout(current => updateLayoutGeometry(current, elementId, previousGeometry));
      setSaveState('error');
      onMessage(`Layout save failed: ${String(error)}`);
    }
  }, [apiScope, effectiveMode, isBackendHealthy, layout, onMessage]);

  const saveSemanticTarget = useCallback(async (
    element: SiteLayoutElementResponse,
    draft: SemanticPropertiesDraft
  ) => {
    if (!editable || !topology || !apiScope) {
      onMessage('Target properties can only be changed in Edit mode.');
      return;
    }

    setBusy(true);
    setSaveState('saving');
    try {
      let nextTopology: AutomationTopologyResponse;
      if (element.target.kind === 'System') {
        nextTopology = await requireBody(
          updateAutomationSystem(topology.topologyId, element.target.targetId, {
            displayName: requireText(draft.displayName, 'Display Name'),
            systemType: requireText(draft.systemType, 'System Type'),
            metadata: topology.systems.find(
              system => system.systemId === element.target.targetId)?.metadata ?? {}
          }, apiScope),
          `Save System ${element.target.targetId}`);
      } else if (element.target.kind === 'SlotGroup') {
        if (!Number.isInteger(draft.groupCapacity) || draft.groupCapacity <= 0) {
          throw new Error('Slot Group capacity must be a positive whole number.');
        }
        nextTopology = await requireBody(
          updateSlotGroup(topology.topologyId, element.target.targetId, {
            displayName: requireText(draft.displayName, 'Display Name'),
            kind: draft.groupKind,
            capacity: draft.groupCapacity
          }, apiScope),
          `Save Slot Group ${element.target.targetId}`);
      } else {
        nextTopology = await requireBody(
          updateSlotDefinition(topology.topologyId, element.target.targetId, {
            displayName: requireText(draft.displayName, 'Display Name'),
            address: requireText(draft.slotAddress, 'Slot Address'),
            materialKind: draft.slotMaterialKind,
            isEnabled: draft.slotEnabled
          }, apiScope),
          `Save Slot ${element.target.targetId}`);
      }

      setTopology(nextTopology);
      setSaveState('saved');
      onMessage(`Properties saved ${element.target.targetId}`);
    } catch (error) {
      setSaveState('error');
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [apiScope, editable, onMessage, topology]);

  const deleteSemanticTarget = useCallback(async (element: SiteLayoutElementResponse) => {
    if (!editable || !topology || !layout || !apiScope) {
      onMessage('Targets can only be deleted in Edit mode.');
      return;
    }

    if (deleteConfirmElementId !== element.elementId) {
      setDeleteConfirmElementId(element.elementId);
      onMessage(`Confirm deletion of ${element.target.targetId}. Its placed layout subtree will also be removed.`);
      return;
    }

    setBusy(true);
    setSaveState('saving');
    const parentElementId = element.parentElementId;
    try {
      const deletion = element.target.kind === 'System'
        ? await requireBody(
          deleteAutomationSystem(topology.topologyId, element.target.targetId, apiScope),
          `Delete System ${element.target.targetId}`)
        : element.target.kind === 'SlotGroup'
          ? await requireBody(
            deleteSlotGroup(topology.topologyId, element.target.targetId, apiScope),
            `Delete Slot Group ${element.target.targetId}`)
          : await requireBody(
            deleteSlotDefinition(topology.topologyId, element.target.targetId, apiScope),
            `Delete Slot ${element.target.targetId}`);

      setTopology(deletion.topology);
      setLayout(current => removeLayoutElementSubtree(current, element.elementId));
      setSelectedElementId(parentElementId);
      setDeleteConfirmElementId(null);

      const refreshedLayout = await getSiteLayout(layout.layoutId, apiScope);
      if (refreshedLayout.ok && refreshedLayout.body) {
        setLayout(refreshedLayout.body);
      }
      setSaveState('saved');
      onMessage(
        `Deleted ${element.target.targetId}; ${deletion.removedLayoutElementCount} layout element(s) removed. Stale Production references will fail publication.`);
    } catch (error) {
      setSaveState('error');
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [apiScope, deleteConfirmElementId, editable, layout, onMessage, topology]);

  const handleCanvasDrop = useCallback((event: React.DragEvent<HTMLDivElement>) => {
    if (!editable || !layout) {
      return;
    }

    const kind = event.dataTransfer.getData('application/x-openlineops-topology') as PaletteItemKind;
    if (!paletteItems.some(item => item.kind === kind)) {
      return;
    }

    event.preventDefault();
    const bounds = event.currentTarget.getBoundingClientRect();
    const x = ((event.clientX - bounds.left) / bounds.width) * layout.canvasWidth;
    const y = ((event.clientY - bounds.top) / bounds.height) * layout.canvasHeight;
    void addPaletteItem(kind, { x, y });
  }, [addPaletteItem, editable, layout]);

  return (
    <section className="topology-ide" data-testid="topology-workbench">
      <header className="topology-command-bar">
        <div className="topology-document-identity">
          <Waypoints size={17} />
          <div>
            <strong>{activeApplication?.displayName ?? '2D Layout'}</strong>
            <span>{layout?.layoutId ?? 'No layout created'}</span>
          </div>
        </div>

        <div className="topology-segmented" role="group" aria-label="Topology editor mode">
          <button
            type="button"
            className={effectiveMode === 'edit' ? 'active' : ''}
            onClick={() => setCanvasMode('edit')}
            disabled={workspaceMode === 'run'}
            data-testid="topology-mode-edit"
          >
            <MousePointer2 size={13} /> Edit
          </button>
          <button
            type="button"
            className={effectiveMode === 'monitor' ? 'active' : ''}
            onClick={() => setCanvasMode('monitor')}
            data-testid="topology-mode-monitor"
          >
            <CircleDot size={13} /> Monitor
          </button>
        </div>

        <div className="topology-dimension-switch" role="group" aria-label="Topology dimension">
          <button type="button" className="active" aria-pressed="true">2D</button>
          <button type="button" disabled title="3D topology is planned after the semantic 2D editor is complete">
            <Cuboid size={13} /> 3D · Planned
          </button>
        </div>

        <div className="topology-command-actions">
          <span className={`topology-save-state ${saveState}`}>
            {saveState === 'saving' ? <RefreshCw size={12} /> : <Save size={12} />}
            {saveState === 'saving' ? 'Saving' : saveState === 'error' ? 'Save failed' : 'Saved'}
          </span>
          <button type="button" className="button ghost" onClick={() => void refresh()} disabled={!isBackendHealthy || busy} data-testid="refresh-topology-layout">
            <RefreshCw size={14} /> Refresh
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => void newLayout()}
            disabled={!isBackendHealthy || busy || !activeApplication || Boolean(layout)}
            data-testid="new-topology-layout"
          >
            <Plus size={14} /> New Layout
          </button>
        </div>
      </header>

      <div className="topology-workspace">
        <PalettePanel
          selectedElement={selectedElement}
          topology={topology}
          editable={editable && Boolean(layout)}
          onAdd={kind => void addPaletteItem(kind)}
        />

        <main className="topology-canvas-column">
          <div className="topology-canvas-meta">
            <div>
              <span className={`topology-mode-indicator ${effectiveMode}`} />
              <strong>{effectiveMode === 'edit' ? 'Layout editor' : 'Live line overview'}</strong>
              <small>{topology ? `${topology.systems.length} systems · ${topology.capabilities.length} capabilities · ${topology.slotGroups.length} groups · ${topology.slots.length} slots` : 'No topology'}</small>
            </div>
            {workspaceMode === 'run' ? <span className="topology-runtime-lock"><LockKeyhole size={12} /> Project is running</span> : null}
          </div>

          <div className="topology-canvas-stage">
            {layout && topology ? (
              <div
                className={`topology-canvas ${effectiveMode}`}
                style={{ aspectRatio: `${layout.canvasWidth} / ${layout.canvasHeight}` }}
                onDragOver={event => {
                  if (editable) {
                    event.preventDefault();
                    event.dataTransfer.dropEffect = 'copy';
                  }
                }}
                onDrop={handleCanvasDrop}
                onClick={() => setSelectedElementId(null)}
                data-testid="topology-canvas"
              >
                <TopologyElementTree
                  layout={layout}
                  topology={topology}
                  stationStatuses={stationStatuses}
                  targetStatusByKey={targetStatusByKey}
                  runtimeConnected={runtimeConnected}
                  selectedElementId={selectedElementId}
                  editable={editable}
                  onSelect={setSelectedElementId}
                  onPreviewGeometry={previewGeometry}
                  onCommitGeometry={commitGeometry}
                />
                {layout.elements.length === 0 ? (
                  <div className="topology-canvas-empty">
                    <BoxSelect size={28} />
                    <strong>Start with a Station</strong>
                    <span>Drag it here or click the Station tile in the palette.</span>
                  </div>
                ) : null}
                <div className="topology-canvas-scale">1200 × 680 · local coordinates</div>
              </div>
            ) : (
              <div className="topology-layout-empty">
                <Layers3 size={34} />
                <strong>Create the Application layout</strong>
                <p>One semantic canvas is shared by engineering and runtime monitoring.</p>
                <button
                  type="button"
                  className="button primary"
                  onClick={() => void newLayout()}
                  disabled={!isBackendHealthy || busy || !activeApplication}
                  data-testid="new-topology-layout-empty"
                >
                  <Plus size={14} /> New 2D Layout
                </button>
              </div>
            )}
          </div>

          <footer className="topology-canvas-footer">
            <span><MousePointer2 size={12} /> Drag elements · Arrow keys nudge · Shift = 10 px</span>
            <span>Children use parent-local coordinates</span>
          </footer>
        </main>

        <InspectorPanel
          layout={layout}
          topology={topology}
          selectedElement={selectedElement}
          stationStatuses={stationStatuses}
          targetStatusByKey={targetStatusByKey}
          runtimeConnected={runtimeConnected}
          editable={editable}
          deleteConfirming={deleteConfirmElementId === selectedElement?.elementId}
          onSelect={setSelectedElementId}
          onCommit={commitGeometry}
          onSaveSemantic={saveSemanticTarget}
          onDelete={deleteSemanticTarget}
        />
      </div>
    </section>
  );
}

function PalettePanel({
  selectedElement,
  topology,
  editable,
  onAdd
}: {
  selectedElement: SiteLayoutElementResponse | null;
  topology: AutomationTopologyResponse | null;
  editable: boolean;
  onAdd(kind: PaletteItemKind): void;
}): React.ReactElement {
  const selectedDescriptor = selectedElement && topology
    ? describeElement(selectedElement, topology)
    : null;

  return (
    <aside className="topology-palette">
      <div className="topology-side-heading">
        <div><Boxes size={14} /><strong>Elements</strong></div>
        <span>Drag or click</span>
      </div>
      <div className="topology-palette-list">
        {paletteItems.map(item => {
          const Icon = item.icon;
          const contextUnavailable = item.kind === 'System' || item.kind === 'Group'
            ? selectedElement?.kind !== 'SystemShape'
            : item.kind === 'Slot'
              ? selectedElement?.kind !== 'GroupRegion' && selectedElement?.kind !== 'SlotShape'
              : false;
          const disabled = !editable || contextUnavailable;
          return (
            <button
              type="button"
              key={item.kind}
              className={`topology-palette-item ${item.kind.toLowerCase()}`}
              draggable={!disabled}
              disabled={disabled}
              onDragStart={event => {
                event.dataTransfer.setData('application/x-openlineops-topology', item.kind);
                event.dataTransfer.effectAllowed = 'copy';
              }}
              onClick={() => onAdd(item.kind)}
              data-testid={`add-topology-${item.kind.toLowerCase()}`}
            >
              <span><Icon size={16} /></span>
              <div><strong>{item.label}</strong><small>{item.detail}</small></div>
              <Plus size={13} />
            </button>
          );
        })}
      </div>
      <div className="topology-context-card">
        <span>INSERT CONTEXT</span>
        <strong>{selectedDescriptor?.displayName ?? 'Canvas root'}</strong>
        <small>{selectedDescriptor?.detail ?? 'Stations are placed at the Application root.'}</small>
      </div>
      <div className="topology-palette-note">
        <LockKeyhole size={13} />
        <span>Monitor mode locks all structural and geometry changes.</span>
      </div>
    </aside>
  );
}

function TopologyElementTree({
  layout,
  topology,
  stationStatuses,
  targetStatusByKey,
  runtimeConnected,
  selectedElementId,
  editable,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry
}: {
  layout: SiteLayoutResponse;
  topology: AutomationTopologyResponse;
  stationStatuses: Map<string, RuntimeStationStatus>;
  targetStatusByKey: Map<string, RuntimeTargetStatus>;
  runtimeConnected: boolean;
  selectedElementId: string | null;
  editable: boolean;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
}): React.ReactElement {
  const childrenByParent = useMemo(() => {
    const map = new Map<string | null, SiteLayoutElementResponse[]>();
    for (const element of layout.elements) {
      const children = map.get(element.parentElementId) ?? [];
      children.push(element);
      map.set(element.parentElementId, children);
    }
    for (const children of map.values()) {
      children.sort((left, right) => left.zIndex - right.zIndex || left.elementId.localeCompare(right.elementId));
    }
    return map;
  }, [layout.elements]);

  const renderBranch = (
    parentElementId: string | null,
    containerWidth: number,
    containerHeight: number,
    stationSystemId: string | null = null
  ): React.ReactNode => (childrenByParent.get(parentElementId) ?? []).map(element => {
    const descriptor = describeElement(element, topology);
    const elementStationSystemId = descriptor.system?.kind === 'Station'
      ? descriptor.system.systemId
      : stationSystemId;
    const state = descriptor.system?.kind === 'Station'
      ? toOperationalState(stationStatuses.get(descriptor.system.systemId), runtimeConnected)
      : toTargetOperationalState(
        elementStationSystemId
          ? targetStatusByKey.get(runtimeTargetStatusKey(
            elementStationSystemId,
            element.target.kind,
            element.target.targetId))
          : undefined,
        runtimeConnected);
    return (
      <TopologyCanvasElement
        key={element.elementId}
        element={element}
        descriptor={descriptor}
        state={state}
        containerWidth={containerWidth}
        containerHeight={containerHeight}
        selected={element.elementId === selectedElementId}
        editable={editable}
        onSelect={onSelect}
        onPreviewGeometry={onPreviewGeometry}
        onCommitGeometry={onCommitGeometry}
      >
        {renderBranch(element.elementId, element.width, element.height, elementStationSystemId)}
      </TopologyCanvasElement>
    );
  });

  return <>{renderBranch(null, layout.canvasWidth, layout.canvasHeight)}</>;
}

function TopologyCanvasElement({
  element,
  descriptor,
  state,
  containerWidth,
  containerHeight,
  selected,
  editable,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry,
  children
}: {
  element: SiteLayoutElementResponse;
  descriptor: ElementDescriptor;
  state: OperationalState;
  containerWidth: number;
  containerHeight: number;
  selected: boolean;
  editable: boolean;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
  children: React.ReactNode;
}): React.ReactElement {
  const dragState = useRef<ElementDragState | null>(null);
  const [dragging, setDragging] = useState(false);
  const style: React.CSSProperties = {
    left: `${(element.x / containerWidth) * 100}%`,
    top: `${(element.y / containerHeight) * 100}%`,
    width: `${(element.width / containerWidth) * 100}%`,
    height: `${(element.height / containerHeight) * 100}%`,
    zIndex: element.zIndex
  };

  const handlePointerDown = (event: React.PointerEvent<HTMLDivElement>): void => {
    if (!editable || event.button !== 0) {
      return;
    }

    const bounds = event.currentTarget.parentElement?.getBoundingClientRect();
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture(event.pointerId);
    const geometry = toGeometry(element);
    dragState.current = {
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startClientY: event.clientY,
      containerWidthPx: bounds.width,
      containerHeightPx: bounds.height,
      previousGeometry: geometry,
      latestGeometry: geometry,
      moved: false
    };
    setDragging(true);
    onSelect(element.elementId);
  };

  const handlePointerMove = (event: React.PointerEvent<HTMLDivElement>): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    const deltaX = ((event.clientX - drag.startClientX) / drag.containerWidthPx) * containerWidth;
    const deltaY = ((event.clientY - drag.startClientY) / drag.containerHeightPx) * containerHeight;
    const next = clampGeometry({
      ...drag.previousGeometry,
      x: drag.previousGeometry.x + deltaX,
      y: drag.previousGeometry.y + deltaY
    }, containerWidth, containerHeight);
    drag.latestGeometry = next;
    drag.moved = drag.moved || Math.abs(deltaX) > 0.2 || Math.abs(deltaY) > 0.2;
    onPreviewGeometry(element.elementId, next);
  };

  const finishDrag = (event: React.PointerEvent<HTMLDivElement>, cancelled: boolean): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    dragState.current = null;
    setDragging(false);
    if (cancelled) {
      onPreviewGeometry(element.elementId, drag.previousGeometry);
    } else if (drag.moved) {
      onCommitGeometry(element.elementId, drag.latestGeometry, drag.previousGeometry);
    }
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>): void => {
    if (!editable || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    const step = event.shiftKey ? 10 : 1;
    const previous = toGeometry(element);
    const next = clampGeometry({
      ...previous,
      x: previous.x + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0),
      y: previous.y + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0)
    }, containerWidth, containerHeight);
    onPreviewGeometry(element.elementId, next);
    onCommitGeometry(element.elementId, next, previous);
  };

  return (
    <div
      className={`topology-layout-element ${toElementClass(element.kind)}${descriptor.system ? ` ${descriptor.system.kind.toLowerCase()}-system` : ''} status-${state.toLowerCase()}${selected ? ' selected' : ''}${dragging ? ' dragging' : ''}`}
      style={style}
      role="button"
      tabIndex={editable ? 0 : -1}
      aria-label={`${descriptor.displayName}, ${state}`}
      aria-pressed={selected}
      data-operational-state={state}
      onClick={event => {
        event.stopPropagation();
        onSelect(element.elementId);
      }}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={event => finishDrag(event, false)}
      onPointerCancel={event => finishDrag(event, true)}
      onKeyDown={handleKeyDown}
      data-testid={`layout-element-${element.elementId}`}
    >
      <ElementFace element={element} descriptor={descriptor} state={state} />
      {children ? <div className="topology-element-children">{children}</div> : null}
    </div>
  );
}

function ElementFace({
  element,
  descriptor,
  state
}: {
  element: SiteLayoutElementResponse;
  descriptor: ElementDescriptor;
  state: OperationalState;
}): React.ReactElement {
  if (element.kind === 'SystemShape') {
    if (descriptor.system?.kind !== 'Station') {
      return (
        <div className="topology-component-face">
          <Cuboid size={13} />
          <div>
            <strong>{descriptor.displayName}</strong>
            <span>{descriptor.system?.systemType ?? 'System'}</span>
          </div>
          <i className={`topology-inline-status status-${state.toLowerCase()}`} title={state} />
        </div>
      );
    }
    return (
      <div className="topology-station-face">
        <div className="topology-station-index">ST</div>
        <div>
          <strong>{descriptor.displayName}</strong>
          <span>{descriptor.detail}</span>
        </div>
        <em className={`topology-status-badge status-${state.toLowerCase()}`}><i />{state}</em>
      </div>
    );
  }

  if (element.kind === 'GroupRegion') {
    return (
      <div className="topology-group-face">
        <Grid2X2 size={12} />
        <strong>{descriptor.displayName}</strong>
        <i className={`topology-inline-status status-${state.toLowerCase()}`} title={state} />
      </div>
    );
  }

  return (
    <div className="topology-slot-face" title={`${descriptor.detail} - ${state}`}>
      <i />
      <strong>{descriptor.displayName}</strong>
    </div>
  );
}

function InspectorPanel({
  layout,
  topology,
  selectedElement,
  stationStatuses,
  targetStatusByKey,
  runtimeConnected,
  editable,
  deleteConfirming,
  onSelect,
  onCommit,
  onSaveSemantic,
  onDelete
}: {
  layout: SiteLayoutResponse | null;
  topology: AutomationTopologyResponse | null;
  selectedElement: SiteLayoutElementResponse | null;
  stationStatuses: Map<string, RuntimeStationStatus>;
  targetStatusByKey: Map<string, RuntimeTargetStatus>;
  runtimeConnected: boolean;
  editable: boolean;
  deleteConfirming: boolean;
  onSelect(elementId: string): void;
  onCommit(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
  onSaveSemantic(element: SiteLayoutElementResponse, draft: SemanticPropertiesDraft): void;
  onDelete(element: SiteLayoutElementResponse): void;
}): React.ReactElement {
  return (
    <aside className="topology-inspector">
      <div className="topology-side-heading">
        <div><Settings2 size={14} /><strong>Properties</strong></div>
        <span>{selectedElement?.kind ?? 'Nothing selected'}</span>
      </div>
      <GeometryProperties
        layout={layout}
        topology={topology}
        element={selectedElement}
        stationStatuses={stationStatuses}
        targetStatusByKey={targetStatusByKey}
        runtimeConnected={runtimeConnected}
        editable={editable}
        deleteConfirming={deleteConfirming}
        onCommit={onCommit}
        onSaveSemantic={onSaveSemantic}
        onDelete={onDelete}
      />
      <HierarchyTree
        layout={layout}
        topology={topology}
        selectedElementId={selectedElement?.elementId ?? null}
        onSelect={onSelect}
      />
      <section className="topology-legend">
        <header><strong>Status legend</strong><span>live state</span></header>
        <div>
          {statusLegend.map(item => (
            <span key={item.state}><i className={`status-${item.state.toLowerCase()}`} />{item.label}</span>
          ))}
        </div>
      </section>
    </aside>
  );
}

function GeometryProperties({
  layout,
  topology,
  element,
  stationStatuses,
  targetStatusByKey,
  runtimeConnected,
  editable,
  deleteConfirming,
  onCommit,
  onSaveSemantic,
  onDelete
}: {
  layout: SiteLayoutResponse | null;
  topology: AutomationTopologyResponse | null;
  element: SiteLayoutElementResponse | null;
  stationStatuses: Map<string, RuntimeStationStatus>;
  targetStatusByKey: Map<string, RuntimeTargetStatus>;
  runtimeConnected: boolean;
  editable: boolean;
  deleteConfirming: boolean;
  onCommit(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
  onSaveSemantic(element: SiteLayoutElementResponse, draft: SemanticPropertiesDraft): void;
  onDelete(element: SiteLayoutElementResponse): void;
}): React.ReactElement {
  const [draft, setDraft] = useState<UpdateSiteLayoutElementGeometryRequest | null>(
    element ? toGeometry(element) : null);
  const [semanticDraft, setSemanticDraft] = useState<SemanticPropertiesDraft | null>(
    element && topology ? toSemanticDraft(element, topology) : null);

  useEffect(() => {
    setDraft(element ? toGeometry(element) : null);
    setSemanticDraft(element && topology ? toSemanticDraft(element, topology) : null);
  }, [element, topology]);

  if (!layout || !topology || !element || !draft || !semanticDraft) {
    return (
      <div className="topology-properties-empty">
        <MousePointer2 size={21} />
        <span>Select an element to inspect its semantic target and local geometry.</span>
      </div>
    );
  }

  const descriptor = describeElement(element, topology);
  const stationElement = resolveStationElement(element, layout, topology);
  const stationSystemId = stationElement?.target.targetId ?? null;
  const operationalState = descriptor.system?.kind === 'Station'
    ? toOperationalState(stationStatuses.get(descriptor.system.systemId), runtimeConnected)
    : toTargetOperationalState(
      stationSystemId
        ? targetStatusByKey.get(runtimeTargetStatusKey(
          stationSystemId,
          element.target.kind,
          element.target.targetId))
        : undefined,
      runtimeConnected);
  const parent = element.parentElementId
    ? layout.elements.find(candidate => candidate.elementId === element.parentElementId) ?? null
    : null;

  return (
    <div className="topology-properties-form">
      <div className="topology-selected-summary">
        <strong>{descriptor.displayName}</strong>
        <span>{descriptor.detail}</span>
        <em className={`topology-status-badge status-${operationalState.toLowerCase()}`}><i />{operationalState}</em>
      </div>
      <dl className="topology-target-facts">
        <dt>Target</dt><dd>{element.target.kind}</dd>
        <dt>ID</dt><dd title={element.target.targetId}>{element.target.targetId}</dd>
        <dt>Parent</dt><dd title={element.parentElementId ?? 'Canvas'}>{element.parentElementId ?? 'Canvas'}</dd>
        <dt>Rotate</dt><dd>0 deg (locked)</dd>
      </dl>
      <form
        className="topology-semantic-form"
        onSubmit={event => {
          event.preventDefault();
          onSaveSemantic(element, semanticDraft);
        }}
      >
        <label>
          <span>Display Name</span>
          <input
            value={semanticDraft.displayName}
            disabled={!editable}
            onChange={event => setSemanticDraft(current => current
              ? { ...current, displayName: event.target.value }
              : current)}
            data-testid="topology-property-display-name"
          />
        </label>
        {element.target.kind === 'System' ? (
          <label>
            <span>System Type</span>
            <input
              value={semanticDraft.systemType}
              disabled={!editable}
              onChange={event => setSemanticDraft(current => current
                ? { ...current, systemType: event.target.value }
                : current)}
              data-testid="topology-property-system-type"
            />
          </label>
        ) : null}
        {element.target.kind === 'SlotGroup' ? (
          <div className="topology-semantic-pair">
            <label>
              <span>Group Kind</span>
              <select
                value={semanticDraft.groupKind}
                disabled={!editable}
                onChange={event => setSemanticDraft(current => current
                  ? { ...current, groupKind: event.target.value }
                  : current)}
                data-testid="topology-property-group-kind"
              >
                {slotGroupKinds.map(kind => <option key={kind} value={kind}>{kind}</option>)}
              </select>
            </label>
            <label>
              <span>Capacity</span>
              <input
                type="number"
                min="1"
                step="1"
                value={semanticDraft.groupCapacity}
                disabled={!editable}
                onChange={event => setSemanticDraft(current => current
                  ? { ...current, groupCapacity: Number(event.target.value) }
                  : current)}
                data-testid="topology-property-group-capacity"
              />
            </label>
          </div>
        ) : null}
        {element.target.kind === 'Slot' ? (
          <>
            <label>
              <span>Slot Address</span>
              <input
                value={semanticDraft.slotAddress}
                disabled={!editable}
                onChange={event => setSemanticDraft(current => current
                  ? { ...current, slotAddress: event.target.value }
                  : current)}
                data-testid="topology-property-slot-address"
              />
            </label>
            <div className="topology-semantic-pair">
              <label>
                <span>Material</span>
                <select
                  value={semanticDraft.slotMaterialKind}
                  disabled={!editable}
                  onChange={event => setSemanticDraft(current => current
                    ? { ...current, slotMaterialKind: event.target.value }
                    : current)}
                  data-testid="topology-property-slot-material"
                >
                  {slotMaterialKinds.map(kind => <option key={kind} value={kind}>{kind}</option>)}
                </select>
              </label>
              <label className="topology-enabled-toggle">
                <span>Enabled</span>
                <input
                  type="checkbox"
                  checked={semanticDraft.slotEnabled}
                  disabled={!editable}
                  onChange={event => setSemanticDraft(current => current
                    ? { ...current, slotEnabled: event.target.checked }
                    : current)}
                  data-testid="topology-property-slot-enabled"
                />
              </label>
            </div>
          </>
        ) : null}
        <button type="submit" className="button primary" disabled={!editable} data-testid="save-topology-target">
          <Save size={13} /> Save Properties
        </button>
      </form>
      <form
        className="topology-geometry-form"
        onSubmit={event => {
          event.preventDefault();
          const containerWidth = parent?.width ?? layout.canvasWidth;
          const containerHeight = parent?.height ?? layout.canvasHeight;
          onCommit(
            element.elementId,
            clampGeometry(draft, containerWidth, containerHeight),
            toGeometry(element));
        }}
      >
        <div className="topology-property-section-title">Local Geometry</div>
        <div className="topology-geometry-grid">
          {([
            ['x', 'X'],
            ['y', 'Y'],
            ['width', 'W'],
            ['height', 'H']
          ] as Array<[keyof UpdateSiteLayoutElementGeometryRequest, string]>).map(([key, label]) => (
            <label key={key}>
              <span>{label}</span>
              <input
                type="number"
                step="0.1"
                value={draft[key]}
                disabled={!editable}
                onChange={event => setDraft(current => current
                  ? { ...current, [key]: Number(event.target.value), rotationDegrees: 0 }
                  : current)}
                data-testid={`layout-geometry-${key}`}
              />
            </label>
          ))}
        </div>
        <button type="submit" className="button ghost" disabled={!editable} data-testid="save-layout-geometry">
          <Save size={13} /> Apply Geometry
        </button>
      </form>
      <button
        type="button"
        className={`button topology-delete-target${deleteConfirming ? ' confirming' : ''}`}
        disabled={!editable}
        onClick={() => onDelete(element)}
        data-testid="delete-topology-target"
      >
        <Trash2 size={13} /> {deleteConfirming ? 'Confirm Delete' : 'Delete Target'}
      </button>
      {deleteConfirming ? (
        <p className="topology-delete-warning">
          The semantic target and its complete placed subtree will be deleted. Production references are not rewritten.
        </p>
      ) : null}
    </div>
  );
}

function HierarchyTree({
  layout,
  topology,
  selectedElementId,
  onSelect
}: {
  layout: SiteLayoutResponse | null;
  topology: AutomationTopologyResponse | null;
  selectedElementId: string | null;
  onSelect(elementId: string): void;
}): React.ReactElement {
  const childrenByParent = useMemo(() => {
    const map = new Map<string | null, SiteLayoutElementResponse[]>();
    for (const element of layout?.elements ?? []) {
      const children = map.get(element.parentElementId) ?? [];
      children.push(element);
      map.set(element.parentElementId, children);
    }
    return map;
  }, [layout]);

  if (!layout || !topology) {
    return <section className="topology-hierarchy"><header><strong>Hierarchy</strong><span>0</span></header></section>;
  }

  const renderBranch = (parentId: string | null, depth: number): React.ReactNode => (
    childrenByParent.get(parentId) ?? []
  ).map(element => {
    const descriptor = describeElement(element, topology);
    return (
      <React.Fragment key={element.elementId}>
        <button
          type="button"
          className={selectedElementId === element.elementId ? 'active' : ''}
          style={{ paddingLeft: `${9 + depth * 14}px` }}
          onClick={() => onSelect(element.elementId)}
        >
          <ChevronRight size={11} />
          <span><strong>{descriptor.displayName}</strong><small>{element.target.kind}</small></span>
        </button>
        {renderBranch(element.elementId, depth + 1)}
      </React.Fragment>
    );
  });

  return (
    <section className="topology-hierarchy">
      <header><strong>Hierarchy</strong><span>{layout.elements.length}</span></header>
      <div>{renderBranch(null, 0)}</div>
    </section>
  );
}

function describeElement(
  element: SiteLayoutElementResponse,
  topology: AutomationTopologyResponse
): ElementDescriptor {
  if (element.target.kind === 'System') {
    const system = topology.systems.find(candidate => candidate.systemId === element.target.targetId);
    return {
      displayName: system?.displayName ?? element.target.targetId,
      detail: system ? `${system.kind} · ${system.systemType}` : 'Missing System target',
      system
    };
  }

  if (element.target.kind === 'SlotGroup') {
    const group = topology.slotGroups.find(candidate => candidate.slotGroupId === element.target.targetId);
    return {
      displayName: group?.displayName ?? element.target.targetId,
      detail: group ? `${group.kind} · ${group.slotIds.length}/${group.capacity} slots` : 'Missing Slot Group target'
    };
  }

  const slot = topology.slots.find(candidate => candidate.slotId === element.target.targetId);
  return {
    displayName: slot?.address ?? slot?.displayName ?? element.target.targetId,
    detail: slot ? `${slot.displayName} · ${slot.materialKind}` : 'Missing Slot target'
  };
}

function toSemanticDraft(
  element: SiteLayoutElementResponse,
  topology: AutomationTopologyResponse
): SemanticPropertiesDraft {
  const system = element.target.kind === 'System'
    ? topology.systems.find(candidate => candidate.systemId === element.target.targetId)
    : null;
  const group = element.target.kind === 'SlotGroup'
    ? topology.slotGroups.find(candidate => candidate.slotGroupId === element.target.targetId)
    : null;
  const slot = element.target.kind === 'Slot'
    ? topology.slots.find(candidate => candidate.slotId === element.target.targetId)
    : null;
  return {
    displayName: system?.displayName ?? group?.displayName ?? slot?.displayName ?? '',
    systemType: system?.systemType ?? '',
    groupKind: group?.kind ?? slotGroupKinds[0],
    groupCapacity: group?.capacity ?? 1,
    slotAddress: slot?.address ?? '',
    slotMaterialKind: slot?.materialKind ?? slotMaterialKinds[0],
    slotEnabled: slot?.isEnabled ?? true
  };
}

function resolveStationElement(
  selected: SiteLayoutElementResponse | null,
  layout: SiteLayoutResponse,
  topology: AutomationTopologyResponse
): SiteLayoutElementResponse | null {
  let current = selected;
  while (current) {
    if (current.kind === 'SystemShape') {
      const system = topology.systems.find(candidate => candidate.systemId === current?.target.targetId);
      if (system?.kind === 'Station') {
        return current;
      }
    }
    current = current.parentElementId
      ? layout.elements.find(element => element.elementId === current?.parentElementId) ?? null
      : null;
  }
  return null;
}

function resolveGroupElement(
  selected: SiteLayoutElementResponse | null,
  layout: SiteLayoutResponse
): SiteLayoutElementResponse | null {
  let current = selected;
  while (current) {
    if (current.kind === 'GroupRegion') {
      return current;
    }
    current = current.parentElementId
      ? layout.elements.find(element => element.elementId === current?.parentElementId) ?? null
      : null;
  }
  return null;
}

function runtimeTargetStatusKey(
  stationSystemId: string,
  targetKind: string,
  targetId: string
): string {
  return `${stationSystemId}\u0000${targetKind}\u0000${targetId}`;
}

function toTargetOperationalState(
  status: RuntimeTargetStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }

  switch (status?.commandStatus) {
    case 'Pending':
    case 'Accepted':
    case 'InProgress':
      return 'Running';
    case 'Completed':
      return 'Completed';
    case 'Failed':
    case 'TimedOut':
    case 'Rejected':
    case 'Canceled':
      return 'Failed';
    default:
      return 'Idle';
  }
}

function toOperationalState(
  status: RuntimeStationStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }
  if (!status) {
    return 'Idle';
  }

  const normalized = status.sessionStatus.trim().toLowerCase();
  if (normalized.includes('offline') || normalized.includes('disconnect')) {
    return 'Offline';
  }
  if (status.incidentCount > 0
    || normalized.includes('fail')
    || normalized.includes('fault')
    || normalized.includes('reject')
    || normalized.includes('timedout')
    || normalized.includes('timed-out')
    || normalized.includes('timeout')
    || normalized.includes('abort')
    || normalized.includes('cancel')) {
    return 'Failed';
  }
  if (normalized.includes('complete') || normalized.includes('success') || normalized.includes('pass')) {
    return 'Completed';
  }
  if (status.runningStepCount > 0 || !status.isTerminal) {
    return 'Running';
  }
  return 'Idle';
}

function absoluteElementOrigin(
  element: SiteLayoutElementResponse,
  layout: SiteLayoutResponse
): { x: number; y: number } {
  let x = element.x;
  let y = element.y;
  let parentId = element.parentElementId;
  const visited = new Set<string>([element.elementId]);
  while (parentId) {
    if (visited.has(parentId)) {
      break;
    }
    visited.add(parentId);
    const parent = layout.elements.find(candidate => candidate.elementId === parentId);
    if (!parent) {
      break;
    }
    x += parent.x;
    y += parent.y;
    parentId = parent.parentElementId;
  }
  return { x, y };
}

function createElementRequest(
  elementId: string,
  kind: AddSiteLayoutElementRequest['kind'],
  targetKind: AddSiteLayoutElementRequest['target']['kind'],
  targetId: string,
  parentElementId: string | null,
  geometry: UpdateSiteLayoutElementGeometryRequest,
  zIndex: number,
  style: Record<string, string>
): AddSiteLayoutElementRequest {
  return {
    elementId,
    kind,
    target: { kind: targetKind, targetId },
    parentElementId,
    ...normalizeGeometry(geometry),
    zIndex,
    style
  };
}

function toGeometry(element: SiteLayoutElementResponse): UpdateSiteLayoutElementGeometryRequest {
  return {
    x: element.x,
    y: element.y,
    width: element.width,
    height: element.height,
    rotationDegrees: 0
  };
}

function clampGeometry(
  geometry: UpdateSiteLayoutElementGeometryRequest,
  containerWidth: number,
  containerHeight: number
): UpdateSiteLayoutElementGeometryRequest {
  const width = clampFinite(geometry.width, 1, containerWidth);
  const height = clampFinite(geometry.height, 1, containerHeight);
  return normalizeGeometry({
    x: clampFinite(geometry.x, 0, containerWidth - width),
    y: clampFinite(geometry.y, 0, containerHeight - height),
    width,
    height,
    rotationDegrees: 0
  });
}

function normalizeGeometry(
  geometry: UpdateSiteLayoutElementGeometryRequest
): UpdateSiteLayoutElementGeometryRequest {
  const round = (value: number): number => {
    const rounded = Math.round(value * 1000) / 1000;
    return Object.is(rounded, -0) ? 0 : rounded;
  };
  return {
    x: round(geometry.x),
    y: round(geometry.y),
    width: round(geometry.width),
    height: round(geometry.height),
    rotationDegrees: 0
  };
}

function clampFinite(value: number, minimum: number, maximum: number): number {
  const finiteValue = Number.isFinite(value) ? value : minimum;
  return Math.min(Math.max(finiteValue, minimum), Math.max(minimum, maximum));
}

function updateLayoutGeometry(
  layout: SiteLayoutResponse | null,
  elementId: string,
  geometry: UpdateSiteLayoutElementGeometryRequest
): SiteLayoutResponse | null {
  return layout
    ? {
      ...layout,
      elements: layout.elements.map(element => element.elementId === elementId
        ? { ...element, ...normalizeGeometry(geometry) }
        : element)
    }
    : null;
}

function removeLayoutElementSubtree(
  layout: SiteLayoutResponse | null,
  elementId: string
): SiteLayoutResponse | null {
  if (!layout) {
    return null;
  }

  const removedIds = new Set<string>([elementId]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const element of layout.elements) {
      if (element.parentElementId && removedIds.has(element.parentElementId) && !removedIds.has(element.elementId)) {
        removedIds.add(element.elementId);
        changed = true;
      }
    }
  }
  return {
    ...layout,
    elements: layout.elements.filter(element => !removedIds.has(element.elementId))
  };
}

function nextSequence(ids: string[], prefix: string): number {
  const existing = new Set(ids);
  let sequence = 1;
  while (existing.has(`${prefix}${sequence}`)) {
    sequence += 1;
  }
  return sequence;
}

async function requireBody<T>(
  responsePromise: Promise<{ ok: boolean; status: number; text: string; body: T | null }>,
  action: string
): Promise<T> {
  const response = await responsePromise;
  if (!response.ok || !response.body) {
    throw new Error(`${action} failed: ${response.status} ${response.text}`);
  }
  return response.body;
}

function requireText(value: string, fieldName: string): string {
  const normalized = value.trim();
  if (!normalized) {
    throw new Error(`${fieldName} is required.`);
  }
  return normalized;
}

function createTopologyId(applicationId: string): string {
  return `${applicationId}.topology.main`;
}

function createLayoutId(applicationId: string): string {
  return `${applicationId}.layout.main`;
}

function toElementClass(kind: SiteLayoutElementResponse['kind']): string {
  return kind.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
}
