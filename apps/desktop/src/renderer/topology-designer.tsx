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
  ProductionLineRuntimeStateResponse,
  ProductionOperationRunReadModel,
  ProductionRunReadModel,
  ProjectApplicationResponse,
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
type TopologyDimension = '2d' | '3d';
type PaletteItemKind = 'Station' | 'System' | 'Group' | 'Slot';
type OperationalState = 'Idle' | 'Running' | 'Completed' | 'Failed' | 'Offline';
type RuntimeSlotState = 'Available' | 'Reserved' | 'Occupied' | 'Running' | 'Blocked' | 'Offline';

interface TopologyRuntimeStatus {
  operationalState: OperationalState;
  products: string[];
  queueCount: number;
  operationIds: string[];
  slotState?: RuntimeSlotState;
}

interface TopologySlotRuntimeStatus extends TopologyRuntimeStatus {
  slotId: string;
  stationSystemId: string;
  slotState: RuntimeSlotState;
  fencingToken: number | null;
}

interface TopologyRuntimeView {
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
  slots: TopologySlotRuntimeStatus[];
}

interface TopologyCamera {
  yawDegrees: number;
  pitchDegrees: number;
  zoom: number;
}

interface TopologyDesignerProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  projectSnapshotId: string | null;
  isBackendHealthy: boolean;
  workspaceMode: WorkspaceMode;
  projectionConnected: boolean;
  runtimeProjection: ProductionLineRuntimeStateResponse | null;
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

interface Topology3DRenderElement {
  element: SiteLayoutElementResponse;
  descriptor: ElementDescriptor;
  state: OperationalState;
  absoluteX: number;
  absoluteY: number;
  baseZ: number;
  depth: number;
  containerWidth: number;
  containerHeight: number;
}

interface Topology3DDragState {
  pointerId: number;
  startClientX: number;
  startClientY: number;
  viewportWidthPx: number;
  viewportHeightPx: number;
  previousGeometry: UpdateSiteLayoutElementGeometryRequest;
  latestGeometry: UpdateSiteLayoutElementGeometryRequest;
  moved: boolean;
}

interface Topology3DPoint {
  x: number;
  y: number;
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
  'ProductionUnit',
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
  { kind: 'Slot', label: 'Production Unit Slot', detail: 'Observable work position', icon: CircleDot }
];

const statusLegend: Array<{ state: OperationalState; label: string }> = [
  { state: 'Idle', label: 'Idle' },
  { state: 'Running', label: 'Working' },
  { state: 'Completed', label: 'Completed' },
  { state: 'Failed', label: 'Failed' },
  { state: 'Offline', label: 'Offline' }
];

const defaultTopologyCamera: TopologyCamera = {
  yawDegrees: -32,
  pitchDegrees: 52,
  zoom: 0.9
};

const topology3DViewWidth = 1200;
const topology3DViewHeight = 680;
const topology3DViewCenter = { x: topology3DViewWidth / 2, y: 370 };

export function TopologyDesigner({
  activeWorkspace,
  activeApplicationId,
  projectSnapshotId,
  isBackendHealthy,
  workspaceMode,
  projectionConnected,
  runtimeProjection,
  onWorkspaceChanged,
  onMessage
}: TopologyDesignerProps): React.ReactElement {
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [layout, setLayout] = useState<SiteLayoutResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [saveState, setSaveState] = useState<'saved' | 'saving' | 'error'>('saved');
  const [selectedElementId, setSelectedElementId] = useState<string | null>(null);
  const [canvasMode, setCanvasMode] = useState<CanvasMode>('edit');
  const [dimension, setDimension] = useState<TopologyDimension>('2d');
  const [camera, setCamera] = useState<TopologyCamera>(defaultTopologyCamera);
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
  const runtimeView = useMemo(
    () => effectiveMode === 'monitor'
      ? buildTopologyRuntimeView(topology, runtimeProjection)
      : emptyTopologyRuntimeView(),
    [effectiveMode, runtimeProjection, topology]);
  const stationStatuses = runtimeView.stationStatuses;
  const targetStatusByKey = runtimeView.targetStatusByKey;
  const runtimeMonitorLabel = useMemo(() => {
    if (effectiveMode !== 'monitor') {
      return null;
    }

    const stationSystemIds = new Set(
      topology?.systems
        .filter(system => system.kind === 'Station')
        .map(system => system.systemId) ?? []);
    const scopedStatuses = [...stationSystemIds]
      .map(stationSystemId => stationStatuses.get(stationSystemId))
      .filter((status): status is TopologyRuntimeStatus => status !== undefined);
    return describeRuntimeMonitorState(scopedStatuses, runtimeProjection?.activeRunCount ?? 0);
  }, [effectiveMode, runtimeProjection?.activeRunCount, stationStatuses, topology?.systems]);

  const refresh = useCallback(async () => {
    if (!activeApplication || !apiScope) {
      setTopology(null);
      setLayout(null);
      return;
    }

    if (!isBackendHealthy) {
      return;
    }

    if (effectiveMode === 'edit' && !activeApplication.topologyId) {
      setTopology(null);
      setLayout(null);
      return;
    }

    if (effectiveMode === 'monitor' && !projectSnapshotId) {
      setTopology(null);
      setLayout(null);
      onMessage('Publish an immutable project snapshot before opening Monitor mode.');
      return;
    }

    const releaseSnapshot = effectiveMode === 'monitor' && projectSnapshotId
      ? activeWorkspace?.project.snapshots.find(snapshot => (
        snapshot.snapshotId === projectSnapshotId
        && snapshot.applicationId === activeApplication.applicationId)) ?? null
      : null;
    if (effectiveMode === 'monitor' && projectSnapshotId && !releaseSnapshot) {
      setTopology(null);
      setLayout(null);
      onMessage(`Published snapshot ${projectSnapshotId} does not belong to the active Application.`);
      return;
    }

    const topologyId = releaseSnapshot?.topologyId ?? activeApplication.topologyId;
    if (!topologyId) {
      setTopology(null);
      setLayout(null);
      onMessage('The selected Application has no topology in the active editing or release scope.');
      return;
    }
    const expectedLayoutId = createLayoutId(activeApplication.applicationId);
    const layoutId = releaseSnapshot
      ? releaseSnapshot.layoutIds.find(candidate => candidate === expectedLayoutId) ?? null
      : expectedLayoutId;
    if (!layoutId) {
      setTopology(null);
      setLayout(null);
      onMessage(`Published snapshot ${releaseSnapshot!.snapshotId} does not contain the Application line layout.`);
      return;
    }

    const [topologyResponse, layoutResponse] = await Promise.all([
      getAutomationTopology(topologyId, apiScope, releaseSnapshot?.snapshotId),
      getSiteLayout(layoutId, apiScope, releaseSnapshot?.snapshotId)
    ]);
    setTopology(topologyResponse.ok && topologyResponse.body ? topologyResponse.body : null);
    setLayout(layoutResponse.ok && layoutResponse.body ? layoutResponse.body : null);
    if (releaseSnapshot && (!topologyResponse.ok || !layoutResponse.ok)) {
      onMessage(
        `Published topology load failed for ${releaseSnapshot.snapshotId}: topology ${topologyResponse.status}, layout ${layoutResponse.status}.`);
    }
  }, [activeApplication, activeWorkspace?.project.snapshots, apiScope, effectiveMode, isBackendHealthy, onMessage, projectSnapshotId]);

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

    const selection = rootPlacement
      ? resolveDropInsertionElement(kind, rootPlacement, layout, topology)
      : selectedElement
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
        throw new Error('Select a Slot Group before adding a Production Unit Slot.');
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
          materialKind: 'ProductionUnit',
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
        { appearance: 'production-unit-slot' });
      const nextLayout = await requireBody(
        addSiteLayoutElement(layout.layoutId, element, apiScope),
        `Place Slot ${slotId}`);
      setTopology(nextTopology);
      setLayout(nextLayout);
      setSelectedElementId(element.elementId);
      onMessage(`Production Unit Slot added ${slotId}`);
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
            <strong>{activeApplication?.displayName ?? 'Topology Layout'}</strong>
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
          <button
            type="button"
            className={dimension === '2d' ? 'active' : ''}
            aria-pressed={dimension === '2d'}
            onClick={() => setDimension('2d')}
            data-testid="topology-dimension-2d"
          >
            2D
          </button>
          <button
            type="button"
            className={dimension === '3d' ? 'active' : ''}
            aria-pressed={dimension === '3d'}
            onClick={() => setDimension('3d')}
            data-testid="topology-dimension-3d"
          >
            <Cuboid size={13} /> 3D
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
              <strong>{dimension.toUpperCase()} {effectiveMode === 'edit' ? 'Layout editor' : 'Live line overview'}</strong>
              <small>{topology ? `${topology.systems.length} systems · ${topology.capabilities.length} capabilities · ${topology.slotGroups.length} groups · ${topology.slots.length} slots` : 'No topology'}</small>
            </div>
            {runtimeMonitorLabel ? (
              <span className="topology-runtime-lock" data-testid="topology-runtime-state-label">
                <LockKeyhole size={12} /> {runtimeMonitorLabel}
              </span>
            ) : null}
          </div>

          <div className="topology-canvas-stage">
            {layout && topology ? (
              dimension === '2d' ? (
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
                    runtimeConnected={projectionConnected}
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
                  <div className="topology-canvas-scale">{layout.canvasWidth} × {layout.canvasHeight} · local coordinates</div>
                </div>
              ) : (
                <SemanticTopology3D
                  layout={layout}
                  topology={topology}
                  stationStatuses={stationStatuses}
                  targetStatusByKey={targetStatusByKey}
                  runtimeConnected={projectionConnected}
                  selectedElementId={selectedElementId}
                  editable={editable}
                  camera={camera}
                  onCameraChange={setCamera}
                  onSelect={setSelectedElementId}
                  onPreviewGeometry={previewGeometry}
                  onCommitGeometry={commitGeometry}
                  onAddAt={(kind, placement) => void addPaletteItem(kind, placement)}
                />
              )
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
            {effectiveMode === 'monitor' && topology ? (
              <TopologyLiveOverlay
                topology={topology}
                lineState={runtimeProjection}
                runtimeView={runtimeView}
                connected={projectionConnected}
              />
            ) : null}
          </div>

          <footer className="topology-canvas-footer">
            <span><MousePointer2 size={12} /> {dimension === '3d' ? 'Drag floor to orbit · Wheel to zoom · Drag blocks to move' : 'Drag elements · Arrow keys nudge · Shift = 10 px'}</span>
            <span>{dimension === '3d' ? '3D projects the same persisted local coordinates' : 'Children use parent-local coordinates'}</span>
          </footer>
        </main>

        <InspectorPanel
          layout={layout}
          topology={topology}
          selectedElement={selectedElement}
          stationStatuses={stationStatuses}
          targetStatusByKey={targetStatusByKey}
          runtimeConnected={projectionConnected}
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

function SemanticTopology3D({
  layout,
  topology,
  stationStatuses,
  targetStatusByKey,
  runtimeConnected,
  selectedElementId,
  editable,
  camera,
  onCameraChange,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry,
  onAddAt
}: {
  layout: SiteLayoutResponse;
  topology: AutomationTopologyResponse;
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
  runtimeConnected: boolean;
  selectedElementId: string | null;
  editable: boolean;
  camera: TopologyCamera;
  onCameraChange(camera: TopologyCamera): void;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
  onAddAt(kind: PaletteItemKind, placement: { x: number; y: number }): void;
}): React.ReactElement {
  const orbitState = useRef<{
    pointerId: number;
    clientX: number;
    clientY: number;
    camera: TopologyCamera;
  } | null>(null);
  const [orbiting, setOrbiting] = useState(false);
  const renderElements = useMemo(
    () => flattenTopology3DElements(
      layout,
      topology,
      stationStatuses,
      targetStatusByKey,
      runtimeConnected),
    [layout, runtimeConnected, stationStatuses, targetStatusByKey, topology]);
  const floorCorners = useMemo(() => [
    projectTopology3DPoint(0, 0, 0, layout, camera),
    projectTopology3DPoint(layout.canvasWidth, 0, 0, layout, camera),
    projectTopology3DPoint(layout.canvasWidth, layout.canvasHeight, 0, layout, camera),
    projectTopology3DPoint(0, layout.canvasHeight, 0, layout, camera)
  ], [camera, layout]);
  const gridLines = useMemo(
    () => createTopology3DGridLines(layout, camera),
    [camera, layout]);

  const adjustCamera = (change: Partial<TopologyCamera>): void => {
    onCameraChange(normalizeTopologyCamera({ ...camera, ...change }));
  };

  const handleOrbitStart = (event: React.PointerEvent<SVGSVGElement>): void => {
    if (event.button !== 0) {
      return;
    }

    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    orbitState.current = {
      pointerId: event.pointerId,
      clientX: event.clientX,
      clientY: event.clientY,
      camera
    };
    setOrbiting(true);
    onSelect('');
  };

  const handleOrbitMove = (event: React.PointerEvent<SVGSVGElement>): void => {
    const orbit = orbitState.current;
    if (!orbit || orbit.pointerId !== event.pointerId) {
      return;
    }

    onCameraChange(normalizeTopologyCamera({
      ...orbit.camera,
      yawDegrees: orbit.camera.yawDegrees + (event.clientX - orbit.clientX) * 0.32,
      pitchDegrees: orbit.camera.pitchDegrees - (event.clientY - orbit.clientY) * 0.22
    }));
  };

  const finishOrbit = (event: React.PointerEvent<SVGSVGElement>): void => {
    const orbit = orbitState.current;
    if (!orbit || orbit.pointerId !== event.pointerId) {
      return;
    }

    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    orbitState.current = null;
    setOrbiting(false);
  };

  const handleDrop = (event: React.DragEvent<HTMLDivElement>): void => {
    if (!editable) {
      return;
    }

    const kind = event.dataTransfer.getData('application/x-openlineops-topology') as PaletteItemKind;
    if (!paletteItems.some(item => item.kind === kind)) {
      return;
    }

    event.preventDefault();
    const bounds = event.currentTarget.getBoundingClientRect();
    const projectedPoint = {
      x: ((event.clientX - bounds.left) / bounds.width) * topology3DViewWidth,
      y: ((event.clientY - bounds.top) / bounds.height) * topology3DViewHeight
    };
    const world = unprojectTopology3DPoint(projectedPoint, layout, camera);
    onAddAt(kind, {
      x: clamp(world.x, 0, layout.canvasWidth),
      y: clamp(world.y, 0, layout.canvasHeight)
    });
  };

  return (
    <div
      className={`topology-3d-viewport ${editable ? 'editable' : 'locked'}${orbiting ? ' orbiting' : ''}`}
      style={{ aspectRatio: `${layout.canvasWidth} / ${layout.canvasHeight}` }}
      onDragOver={event => {
        if (editable) {
          event.preventDefault();
          event.dataTransfer.dropEffect = 'copy';
        }
      }}
      onDrop={handleDrop}
      data-testid="topology-3d-viewport"
    >
      <div className="topology-3d-atmosphere" aria-hidden="true" />
      <svg
        className="topology-3d-scene"
        viewBox={`0 0 ${topology3DViewWidth} ${topology3DViewHeight}`}
        role="application"
        aria-label="Semantic 3D automation line layout"
        onPointerDown={handleOrbitStart}
        onPointerMove={handleOrbitMove}
        onPointerUp={finishOrbit}
        onPointerCancel={finishOrbit}
        onWheel={event => {
          event.preventDefault();
          adjustCamera({ zoom: camera.zoom * (event.deltaY > 0 ? 0.92 : 1.08) });
        }}
      >
        <defs>
          <filter id="topology-3d-shadow" x="-30%" y="-30%" width="160%" height="180%">
            <feDropShadow dx="0" dy="9" stdDeviation="8" floodColor="#060b0e" floodOpacity="0.48" />
          </filter>
          <filter id="topology-3d-selected" x="-30%" y="-30%" width="160%" height="180%">
            <feDropShadow dx="0" dy="0" stdDeviation="5" floodColor="#ff9b5f" floodOpacity="0.85" />
          </filter>
          <linearGradient id="topology-3d-floor" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#233139" />
            <stop offset="1" stopColor="#172126" />
          </linearGradient>
        </defs>

        <polygon
          className="topology-3d-floor"
          points={toSvgPoints(floorCorners)}
          fill="url(#topology-3d-floor)"
        />
        <g className="topology-3d-grid" aria-hidden="true">
          {gridLines.map((line, index) => (
            <line
              key={`${line.axis}-${index}`}
              x1={line.start.x}
              y1={line.start.y}
              x2={line.end.x}
              y2={line.end.y}
              className={line.major ? 'major' : ''}
            />
          ))}
        </g>

        {renderElements.map(renderElement => (
          <Topology3DPrism
            key={renderElement.element.elementId}
            renderElement={renderElement}
            layout={layout}
            camera={camera}
            selected={renderElement.element.elementId === selectedElementId}
            editable={editable}
            onSelect={onSelect}
            onPreviewGeometry={onPreviewGeometry}
            onCommitGeometry={onCommitGeometry}
          />
        ))}

        <g className="topology-3d-axis" aria-hidden="true" transform="translate(64 598)">
          <circle r="26" />
          <line x1="0" y1="0" x2="22" y2="8" className="x" />
          <line x1="0" y1="0" x2="-15" y2="16" className="y" />
          <line x1="0" y1="0" x2="0" y2="-23" className="z" />
          <text x="26" y="13">X</text>
          <text x="-25" y="23">Y</text>
          <text x="5" y="-23">Z</text>
        </g>
      </svg>

      {layout.elements.length === 0 ? (
        <div className="topology-3d-empty">
          <Cuboid size={31} />
          <strong>Place the first Station</strong>
          <span>The 3D scene is generated from the same semantic layout.</span>
        </div>
      ) : null}

      <div className="topology-3d-camera-controls" aria-label="3D camera controls">
        <span className="topology-3d-camera-readout">
          <Cuboid size={13} />
          <strong>ORBIT</strong>
          <small>{Math.round(camera.yawDegrees)}° / {Math.round(camera.pitchDegrees)}°</small>
        </span>
        <button
          type="button"
          onClick={() => adjustCamera({ yawDegrees: camera.yawDegrees - 15 })}
          aria-label="Rotate camera left"
          data-testid="topology-3d-rotate-left"
        >
          −15°
        </button>
        <button
          type="button"
          onClick={() => adjustCamera({ yawDegrees: camera.yawDegrees + 15 })}
          aria-label="Rotate camera right"
          data-testid="topology-3d-rotate-right"
        >
          +15°
        </button>
        <button
          type="button"
          onClick={() => adjustCamera({ zoom: camera.zoom / 1.12 })}
          aria-label="Zoom out"
          data-testid="topology-3d-zoom-out"
        >
          −
        </button>
        <button
          type="button"
          onClick={() => adjustCamera({ zoom: camera.zoom * 1.12 })}
          aria-label="Zoom in"
          data-testid="topology-3d-zoom-in"
        >
          +
        </button>
        <button
          type="button"
          onClick={() => onCameraChange(defaultTopologyCamera)}
          data-testid="topology-3d-reset-camera"
        >
          Reset
        </button>
      </div>

      <div className="topology-3d-coordinate-badge">
        <span>SEMANTIC 3D</span>
        <strong>{layout.canvasWidth} × {layout.canvasHeight}</strong>
        <small>same persisted layout</small>
      </div>
    </div>
  );
}

function Topology3DPrism({
  renderElement,
  layout,
  camera,
  selected,
  editable,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry
}: {
  renderElement: Topology3DRenderElement;
  layout: SiteLayoutResponse;
  camera: TopologyCamera;
  selected: boolean;
  editable: boolean;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
}): React.ReactElement {
  const dragState = useRef<Topology3DDragState | null>(null);
  const [dragging, setDragging] = useState(false);
  const { element, descriptor, state, absoluteX, absoluteY, baseZ, depth } = renderElement;
  const baseCorners = [
    projectTopology3DPoint(absoluteX, absoluteY, baseZ, layout, camera),
    projectTopology3DPoint(absoluteX + element.width, absoluteY, baseZ, layout, camera),
    projectTopology3DPoint(absoluteX + element.width, absoluteY + element.height, baseZ, layout, camera),
    projectTopology3DPoint(absoluteX, absoluteY + element.height, baseZ, layout, camera)
  ];
  const topCorners = [
    projectTopology3DPoint(absoluteX, absoluteY, baseZ + depth, layout, camera),
    projectTopology3DPoint(absoluteX + element.width, absoluteY, baseZ + depth, layout, camera),
    projectTopology3DPoint(absoluteX + element.width, absoluteY + element.height, baseZ + depth, layout, camera),
    projectTopology3DPoint(absoluteX, absoluteY + element.height, baseZ + depth, layout, camera)
  ];
  const labelPoint = projectTopology3DPoint(
    absoluteX + element.width / 2,
    absoluteY + element.height / 2,
    baseZ + depth + 3,
    layout,
    camera);
  const elementClass = toElementClass(element.kind);

  const handlePointerDown = (event: React.PointerEvent<SVGGElement>): void => {
    event.stopPropagation();
    onSelect(element.elementId);
    if (!editable || event.button !== 0) {
      return;
    }

    const bounds = event.currentTarget.ownerSVGElement?.getBoundingClientRect();
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) {
      return;
    }

    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    const geometry = toGeometry(element);
    dragState.current = {
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startClientY: event.clientY,
      viewportWidthPx: bounds.width,
      viewportHeightPx: bounds.height,
      previousGeometry: geometry,
      latestGeometry: geometry,
      moved: false
    };
    setDragging(true);
  };

  const handlePointerMove = (event: React.PointerEvent<SVGGElement>): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    const screenDelta = {
      x: ((event.clientX - drag.startClientX) / drag.viewportWidthPx) * topology3DViewWidth,
      y: ((event.clientY - drag.startClientY) / drag.viewportHeightPx) * topology3DViewHeight
    };
    const worldDelta = unprojectTopology3DDelta(screenDelta, layout, camera);
    const next = clampGeometry({
      ...drag.previousGeometry,
      x: drag.previousGeometry.x + worldDelta.x,
      y: drag.previousGeometry.y + worldDelta.y
    }, renderElement.containerWidth, renderElement.containerHeight);
    drag.latestGeometry = next;
    drag.moved = drag.moved || Math.abs(worldDelta.x) > 0.2 || Math.abs(worldDelta.y) > 0.2;
    onPreviewGeometry(element.elementId, next);
  };

  const finishDrag = (event: React.PointerEvent<SVGGElement>, cancelled: boolean): void => {
    event.stopPropagation();
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

  const handleKeyDown = (event: React.KeyboardEvent<SVGGElement>): void => {
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
    }, renderElement.containerWidth, renderElement.containerHeight);
    onPreviewGeometry(element.elementId, next);
    onCommitGeometry(element.elementId, next, previous);
  };

  const labelWidth = descriptor.system?.kind === 'Station'
    ? 148
    : element.kind === 'SlotShape'
      ? 62
      : 108;

  return (
    <g
      className={`topology-3d-prism ${elementClass} status-${state.toLowerCase()}${selected ? ' selected' : ''}${dragging ? ' dragging' : ''}`}
      role="button"
      tabIndex={editable ? 0 : -1}
      aria-label={`${descriptor.displayName}, ${state}`}
      aria-pressed={selected}
      data-operational-state={state}
      data-testid={`topology-3d-element-${element.elementId}`}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={event => finishDrag(event, false)}
      onPointerCancel={event => finishDrag(event, true)}
      onKeyDown={handleKeyDown}
      filter={selected ? 'url(#topology-3d-selected)' : descriptor.system?.kind === 'Station' ? 'url(#topology-3d-shadow)' : undefined}
    >
      <title>{descriptor.displayName} · {descriptor.detail} · {state}</title>
      <polygon
        className="topology-3d-face side side-south"
        points={toSvgPoints([baseCorners[3], baseCorners[2], topCorners[2], topCorners[3]])}
      />
      <polygon
        className="topology-3d-face side side-east"
        points={toSvgPoints([baseCorners[1], baseCorners[2], topCorners[2], topCorners[1]])}
      />
      <polygon
        className="topology-3d-face side side-north"
        points={toSvgPoints([baseCorners[0], baseCorners[1], topCorners[1], topCorners[0]])}
      />
      <polygon
        className="topology-3d-face side side-west"
        points={toSvgPoints([baseCorners[0], baseCorners[3], topCorners[3], topCorners[0]])}
      />
      <polygon className="topology-3d-face top" points={toSvgPoints(topCorners)} />
      <g className="topology-3d-label" transform={`translate(${labelPoint.x} ${labelPoint.y})`}>
        <rect x={-labelWidth / 2} y="-12" width={labelWidth} height="24" rx="3" />
        <circle cx={-labelWidth / 2 + 10} cy="0" r="3.5" />
        <text x={-labelWidth / 2 + 19} y="1.5">{truncateTopologyLabel(descriptor.displayName, labelWidth)}</text>
      </g>
    </g>
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
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
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
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
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
  stationStatuses: Map<string, TopologyRuntimeStatus>;
  targetStatusByKey: Map<string, TopologyRuntimeStatus>;
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

function flattenTopology3DElements(
  layout: SiteLayoutResponse,
  topology: AutomationTopologyResponse,
  stationStatuses: Map<string, TopologyRuntimeStatus>,
  targetStatusByKey: Map<string, TopologyRuntimeStatus>,
  runtimeConnected: boolean
): Topology3DRenderElement[] {
  const childrenByParent = new Map<string | null, SiteLayoutElementResponse[]>();
  for (const element of layout.elements) {
    const siblings = childrenByParent.get(element.parentElementId) ?? [];
    siblings.push(element);
    childrenByParent.set(element.parentElementId, siblings);
  }
  for (const siblings of childrenByParent.values()) {
    siblings.sort((left, right) => left.zIndex - right.zIndex || left.elementId.localeCompare(right.elementId));
  }

  const result: Topology3DRenderElement[] = [];
  const visit = (
    parentElementId: string | null,
    originX: number,
    originY: number,
    parentTopZ: number,
    stationSystemId: string | null,
    containerWidth: number,
    containerHeight: number
  ): void => {
    for (const element of childrenByParent.get(parentElementId) ?? []) {
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
      const baseZ = parentElementId === null ? 0 : parentTopZ + 4;
      const depth = topology3DDepth(element, descriptor);
      const absoluteX = originX + element.x;
      const absoluteY = originY + element.y;
      result.push({
        element,
        descriptor,
        state,
        absoluteX,
        absoluteY,
        baseZ,
        depth,
        containerWidth,
        containerHeight
      });
      visit(
        element.elementId,
        absoluteX,
        absoluteY,
        baseZ + depth,
        elementStationSystemId,
        element.width,
        element.height);
    }
  };

  visit(null, 0, 0, 0, null, layout.canvasWidth, layout.canvasHeight);
  return result.sort((left, right) =>
    left.baseZ - right.baseZ
    || left.element.zIndex - right.element.zIndex
    || left.absoluteY - right.absoluteY
    || left.element.elementId.localeCompare(right.element.elementId));
}

function topology3DDepth(
  element: SiteLayoutElementResponse,
  descriptor: ElementDescriptor
): number {
  if (descriptor.system?.kind === 'Station') {
    return 28;
  }

  if (element.kind === 'SystemShape') {
    return 20;
  }

  return element.kind === 'GroupRegion' ? 8 : 15;
}

function projectTopology3DPoint(
  worldX: number,
  worldY: number,
  worldZ: number,
  layout: SiteLayoutResponse,
  camera: TopologyCamera
): Topology3DPoint {
  const yaw = camera.yawDegrees * Math.PI / 180;
  const pitch = camera.pitchDegrees * Math.PI / 180;
  const deltaX = worldX - layout.canvasWidth / 2;
  const deltaY = worldY - layout.canvasHeight / 2;
  const rotatedX = deltaX * Math.cos(yaw) - deltaY * Math.sin(yaw);
  const rotatedY = deltaX * Math.sin(yaw) + deltaY * Math.cos(yaw);
  const scale = topology3DProjectionScale(layout, camera);
  return {
    x: topology3DViewCenter.x + rotatedX * scale,
    y: topology3DViewCenter.y
      + rotatedY * Math.sin(pitch) * scale
      - worldZ * Math.cos(pitch) * scale * 2.35
  };
}

function unprojectTopology3DPoint(
  projected: Topology3DPoint,
  layout: SiteLayoutResponse,
  camera: TopologyCamera
): Topology3DPoint {
  const delta = unprojectTopology3DDelta({
    x: projected.x - topology3DViewCenter.x,
    y: projected.y - topology3DViewCenter.y
  }, layout, camera);
  return {
    x: layout.canvasWidth / 2 + delta.x,
    y: layout.canvasHeight / 2 + delta.y
  };
}

function unprojectTopology3DDelta(
  projectedDelta: Topology3DPoint,
  layout: SiteLayoutResponse,
  camera: TopologyCamera
): Topology3DPoint {
  const yaw = camera.yawDegrees * Math.PI / 180;
  const pitch = camera.pitchDegrees * Math.PI / 180;
  const scale = topology3DProjectionScale(layout, camera);
  const rotatedX = projectedDelta.x / scale;
  const rotatedY = projectedDelta.y / (scale * Math.sin(pitch));
  return {
    x: rotatedX * Math.cos(yaw) + rotatedY * Math.sin(yaw),
    y: -rotatedX * Math.sin(yaw) + rotatedY * Math.cos(yaw)
  };
}

function topology3DProjectionScale(
  layout: SiteLayoutResponse,
  camera: TopologyCamera
): number {
  const diagonal = Math.hypot(layout.canvasWidth, layout.canvasHeight);
  return (1000 / Math.max(diagonal, 1)) * camera.zoom;
}

function createTopology3DGridLines(
  layout: SiteLayoutResponse,
  camera: TopologyCamera
): Array<{
  axis: 'x' | 'y';
  major: boolean;
  start: Topology3DPoint;
  end: Topology3DPoint;
}> {
  const lines: Array<{
    axis: 'x' | 'y';
    major: boolean;
    start: Topology3DPoint;
    end: Topology3DPoint;
  }> = [];
  const step = Math.max(50, Math.round(Math.max(layout.canvasWidth, layout.canvasHeight) / 12 / 50) * 50);
  let index = 0;
  for (let x = 0; x <= layout.canvasWidth; x += step) {
    lines.push({
      axis: 'x',
      major: index % 5 === 0,
      start: projectTopology3DPoint(x, 0, 0.5, layout, camera),
      end: projectTopology3DPoint(x, layout.canvasHeight, 0.5, layout, camera)
    });
    index += 1;
  }

  index = 0;
  for (let y = 0; y <= layout.canvasHeight; y += step) {
    lines.push({
      axis: 'y',
      major: index % 5 === 0,
      start: projectTopology3DPoint(0, y, 0.5, layout, camera),
      end: projectTopology3DPoint(layout.canvasWidth, y, 0.5, layout, camera)
    });
    index += 1;
  }
  return lines;
}

function normalizeTopologyCamera(camera: TopologyCamera): TopologyCamera {
  const yaw = ((camera.yawDegrees + 180) % 360 + 360) % 360 - 180;
  return {
    yawDegrees: yaw,
    pitchDegrees: clamp(camera.pitchDegrees, 28, 72),
    zoom: clamp(camera.zoom, 0.52, 1.45)
  };
}

function toSvgPoints(points: Topology3DPoint[]): string {
  return points.map(point => `${point.x.toFixed(2)},${point.y.toFixed(2)}`).join(' ');
}

function truncateTopologyLabel(label: string, labelWidth: number): string {
  const maximumCharacters = Math.max(5, Math.floor((labelWidth - 28) / 6.4));
  return label.length <= maximumCharacters
    ? label
    : `${label.slice(0, Math.max(1, maximumCharacters - 1))}…`;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
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

function TopologyLiveOverlay({
  topology,
  lineState,
  runtimeView,
  connected
}: {
  topology: AutomationTopologyResponse;
  lineState: ProductionLineRuntimeStateResponse | null;
  runtimeView: TopologyRuntimeView;
  connected: boolean;
}): React.ReactElement {
  const stationNameById = new Map(topology.systems
    .filter(system => system.kind === 'Station')
    .map(system => [system.systemId, system.displayName]));
  return (
    <aside className={`topology-live-overlay ${connected ? 'connected' : 'stale'}`} data-testid="topology-live-projection">
      <header>
        <div><CircleDot size={13} /><strong>LIVE WIP</strong></div>
        <span>{connected ? 'CONNECTED' : 'STALE'} · {lineState?.activeRunCount ?? 0}</span>
      </header>
      <div className="topology-live-scroll">
        <section>
          <h3>ACTIVE PRODUCTS</h3>
          {(lineState?.activeRuns ?? []).map(run => {
            const activeOperations = run.operations.filter(operation => operation.executionStatus === 'Running');
            const locatedOperations = activeOperations.length > 0
              ? activeOperations
              : run.operations.filter(operation => operation.executionStatus === 'Pending').slice(0, 1);
            return (
              <div className="topology-live-product" key={run.productionRunId}>
                <i className={`status-${run.executionStatus.toLowerCase()}`} />
                <span>
                  <strong>{run.productionUnitIdentity.value}</strong>
                  <small>{locatedOperations.map(operation => (
                    `${stationNameById.get(operation.stationSystemId) ?? operation.stationSystemId} / ${operation.operationId}`
                  )).join(' + ') || run.entryOperationId}</small>
                </span>
                <em>{run.controlState}</em>
              </div>
            );
          })}
          {(lineState?.activeRuns.length ?? 0) === 0 ? <p>No active products</p> : null}
        </section>

        <section>
          <h3>STATIONS & QUEUES</h3>
          {[...runtimeView.stationStatuses.entries()].map(([stationSystemId, status]) => (
            <div className="topology-live-station" key={stationSystemId}>
              <i className={`status-${status.operationalState.toLowerCase()}`} />
              <span><strong>{stationNameById.get(stationSystemId) ?? stationSystemId}</strong><small>{status.products.length} located · {status.queueCount} queued</small></span>
              <em>{status.operationalState}</em>
            </div>
          ))}
        </section>

        <section>
          <h3>SLOT OCCUPANCY</h3>
          {runtimeView.slots.map(slot => (
            <div className="topology-live-slot" key={slot.slotId}>
              <i className={`status-${slot.operationalState.toLowerCase()}`} />
              <span><strong>{slot.slotId}</strong><small>{slot.products.join(', ') || 'empty'}</small></span>
              <em>{slot.slotState}</em>
            </div>
          ))}
          {runtimeView.slots.length === 0 ? <p>No Slots in this topology</p> : null}
        </section>
      </div>
    </aside>
  );
}

function runtimeTargetStatusKey(
  stationSystemId: string,
  targetKind: string,
  targetId: string
): string {
  return JSON.stringify([stationSystemId, targetKind, targetId]);
}

function toTargetOperationalState(
  status: TopologyRuntimeStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }
  return status?.operationalState ?? 'Idle';
}

function describeRuntimeMonitorState(
  statuses: TopologyRuntimeStatus[],
  activeRunCount: number
): string {
  if (activeRunCount > 0) {
    const queued = statuses.reduce((total, status) => total + status.queueCount, 0);
    return `${activeRunCount} active product${activeRunCount === 1 ? '' : 's'} · ${queued} queued`;
  }
  return 'Line idle · projection connected';
}

function toOperationalState(
  status: TopologyRuntimeStatus | undefined,
  runtimeConnected: boolean
): OperationalState {
  if (!runtimeConnected) {
    return 'Offline';
  }
  return status?.operationalState ?? 'Idle';
}

function emptyTopologyRuntimeView(): TopologyRuntimeView {
  return {
    stationStatuses: new Map(),
    targetStatusByKey: new Map(),
    slots: []
  };
}

function buildTopologyRuntimeView(
  topology: AutomationTopologyResponse | null,
  lineState: ProductionLineRuntimeStateResponse | null
): TopologyRuntimeView {
  if (!topology) {
    return emptyTopologyRuntimeView();
  }

  const stationAccumulators = new Map<string, RuntimeAccumulator>();
  for (const station of topology.systems.filter(system => system.kind === 'Station')) {
    stationAccumulators.set(station.systemId, createRuntimeAccumulator());
  }
  const slots = new Map<string, TopologySlotRuntimeStatus>();
  for (const slot of topology.slots) {
    const slotState: RuntimeSlotState = slot.isEnabled ? 'Available' : 'Offline';
    slots.set(slot.slotId, {
      slotId: slot.slotId,
      stationSystemId: findStationAncestor(slot.parentSystemId, topology) ?? slot.parentSystemId,
      slotState,
      operationalState: slotOperationalState(slotState),
      products: [],
      queueCount: 0,
      operationIds: [],
      fencingToken: null
    });
  }

  for (const run of lineState?.activeRuns ?? []) {
    accumulateRun(stationAccumulators, slots, run);
  }

  const stationStatuses = new Map<string, TopologyRuntimeStatus>();
  for (const [stationSystemId, accumulator] of stationAccumulators) {
    stationStatuses.set(stationSystemId, freezeAccumulator(accumulator));
  }

  const targetStatusByKey = new Map<string, TopologyRuntimeStatus>();
  for (const slot of slots.values()) {
    targetStatusByKey.set(
      runtimeTargetStatusKey(slot.stationSystemId, 'Slot', slot.slotId),
      slot);
  }
  for (const group of topology.slotGroups) {
    const groupSlots = group.slotIds
      .map(slotId => slots.get(slotId))
      .filter((slot): slot is TopologySlotRuntimeStatus => slot !== undefined);
    const groupState = groupSlots.reduce<OperationalState>(
      (current, slot) => operationalStatePriority(slot.operationalState) > operationalStatePriority(current)
        ? slot.operationalState
        : current,
      'Idle');
    targetStatusByKey.set(
      runtimeTargetStatusKey(
        findStationAncestor(group.parentSystemId, topology) ?? group.parentSystemId,
        'SlotGroup',
        group.slotGroupId),
      {
        operationalState: groupState,
        products: unique(groupSlots.flatMap(slot => slot.products)),
        queueCount: groupSlots.reduce((total, slot) => total + slot.queueCount, 0),
        operationIds: unique(groupSlots.flatMap(slot => slot.operationIds))
      });
  }
  for (const system of topology.systems.filter(candidate => candidate.kind === 'System')) {
    const stationSystemId = findStationAncestor(system.systemId, topology);
    if (!stationSystemId) {
      continue;
    }
    const stationStatus = stationStatuses.get(stationSystemId);
    if (stationStatus) {
      targetStatusByKey.set(
        runtimeTargetStatusKey(stationSystemId, 'System', system.systemId),
        stationStatus);
    }
  }

  return {
    stationStatuses,
    targetStatusByKey,
    slots: [...slots.values()].sort((left, right) => left.slotId.localeCompare(right.slotId))
  };
}

interface RuntimeAccumulator {
  products: Set<string>;
  operationIds: Set<string>;
  queueProducts: Set<string>;
  runningCount: number;
  completedCount: number;
  failedCount: number;
}

function createRuntimeAccumulator(): RuntimeAccumulator {
  return {
    products: new Set(),
    operationIds: new Set(),
    queueProducts: new Set(),
    runningCount: 0,
    completedCount: 0,
    failedCount: 0
  };
}

function accumulateRun(
  stations: Map<string, RuntimeAccumulator>,
  slots: Map<string, TopologySlotRuntimeStatus>,
  run: ProductionRunReadModel
): void {
  const product = run.productionUnitIdentity.value;
  const runningOperations = run.operations.filter(operation => operation.executionStatus === 'Running');
  const locatedOperations = runningOperations.length > 0
    ? runningOperations
    : run.operations.filter(operation => operation.executionStatus === 'Pending').slice(0, 1);
  const lastLocatedOperation = locatedOperations.length === 0
    ? run.operations[run.operations.length - 1]
    : null;
  const locatedOperationRunIds = new Set([
    ...locatedOperations.map(operation => operation.operationRunId),
    ...(lastLocatedOperation ? [lastLocatedOperation.operationRunId] : [])
  ]);
  for (const operation of run.operations) {
    const station = stations.get(operation.stationSystemId) ?? createRuntimeAccumulator();
    stations.set(operation.stationSystemId, station);
    station.operationIds.add(operation.operationId);
    if (operation.executionStatus === 'Pending') {
      station.queueProducts.add(product);
    }
    if (locatedOperationRunIds.has(operation.operationRunId)) {
      station.products.add(product);
      if (operation.executionStatus === 'Running' || run.controlState === 'Held' || run.controlState === 'Paused') {
        station.runningCount += 1;
      } else if (operation.executionStatus === 'Completed') {
        station.completedCount += 1;
      } else if (operation.executionStatus === 'Failed'
          || operation.executionStatus === 'TimedOut'
          || operation.executionStatus === 'Rejected'
          || operation.incidentCount > 0) {
        station.failedCount += 1;
      }
    }

    for (const resource of operation.resources.filter(candidate => candidate.kind === 'Slot')) {
      const existing = slots.get(resource.resourceId);
      const slotState = runtimeSlotState(operation, resource.fencingToken);
      const candidate: TopologySlotRuntimeStatus = {
        slotId: resource.resourceId,
        stationSystemId: existing?.stationSystemId ?? operation.stationSystemId,
        slotState,
        operationalState: slotOperationalState(slotState),
        products: resource.fencingToken === null ? [] : [product],
        queueCount: operation.executionStatus === 'Pending' && resource.fencingToken !== null ? 1 : 0,
        operationIds: [operation.operationId],
        fencingToken: resource.fencingToken
      };
      if (!existing || slotStatePriority(candidate.slotState) >= slotStatePriority(existing.slotState)) {
        slots.set(resource.resourceId, candidate);
      }
    }
  }
}

function freezeAccumulator(accumulator: RuntimeAccumulator): TopologyRuntimeStatus {
  const operationalState: OperationalState = accumulator.failedCount > 0
    ? 'Failed'
    : accumulator.runningCount > 0
      ? 'Running'
      : accumulator.completedCount > 0
        ? 'Completed'
        : 'Idle';
  return {
    operationalState,
    products: [...accumulator.products].sort(),
    queueCount: accumulator.queueProducts.size,
    operationIds: [...accumulator.operationIds].sort()
  };
}

function runtimeSlotState(
  operation: ProductionOperationRunReadModel,
  fencingToken: number | null
): RuntimeSlotState {
  if (fencingToken === null) {
    return 'Available';
  }
  if (operation.executionStatus === 'Running') {
    return 'Running';
  }
  if (operation.executionStatus === 'Pending') {
    return 'Reserved';
  }
  if (operation.executionStatus === 'Failed'
      || operation.executionStatus === 'TimedOut'
      || operation.executionStatus === 'Rejected') {
    return 'Blocked';
  }
  return 'Occupied';
}

function slotOperationalState(state: RuntimeSlotState): OperationalState {
  switch (state) {
    case 'Running':
    case 'Reserved':
    case 'Occupied':
      return 'Running';
    case 'Blocked':
      return 'Failed';
    case 'Offline':
      return 'Offline';
    default:
      return 'Idle';
  }
}

function slotStatePriority(state: RuntimeSlotState): number {
  return { Available: 0, Offline: 1, Reserved: 2, Occupied: 3, Running: 4, Blocked: 5 }[state];
}

function operationalStatePriority(state: OperationalState): number {
  return { Idle: 0, Completed: 1, Running: 2, Offline: 3, Failed: 4 }[state];
}

function findStationAncestor(systemId: string, topology: AutomationTopologyResponse): string | null {
  const systemById = new Map(topology.systems.map(system => [system.systemId, system]));
  let current = systemById.get(systemId);
  while (current) {
    if (current.kind === 'Station') {
      return current.systemId;
    }
    current = current.parentSystemId ? systemById.get(current.parentSystemId) : undefined;
  }
  return null;
}

function unique(values: string[]): string[] {
  return [...new Set(values)].sort();
}

function resolveDropInsertionElement(
  kind: PaletteItemKind,
  point: { x: number; y: number },
  layout: SiteLayoutResponse,
  topology: AutomationTopologyResponse
): SiteLayoutElementResponse | null {
  if (kind === 'Station') {
    return null;
  }

  const candidates = layout.elements.filter(element => {
    if (!containsAbsolutePoint(element, point, layout)) {
      return false;
    }

    if (kind === 'Slot') {
      return element.kind === 'GroupRegion'
        && topology.slotGroups.some(group => group.slotGroupId === element.target.targetId);
    }

    return element.kind === 'SystemShape'
      && topology.systems.some(system => (
        system.systemId === element.target.targetId
        && system.kind === 'Station'));
  });

  return candidates.sort((left, right) => (
    elementDepth(right, layout) - elementDepth(left, layout)
    || right.zIndex - left.zIndex
    || right.elementId.localeCompare(left.elementId)
  ))[0] ?? null;
}

function containsAbsolutePoint(
  element: SiteLayoutElementResponse,
  point: { x: number; y: number },
  layout: SiteLayoutResponse
): boolean {
  const origin = absoluteElementOrigin(element, layout);
  return point.x >= origin.x
    && point.x <= origin.x + element.width
    && point.y >= origin.y
    && point.y <= origin.y + element.height;
}

function elementDepth(element: SiteLayoutElementResponse, layout: SiteLayoutResponse): number {
  let depth = 0;
  let parentElementId = element.parentElementId;
  const visited = new Set<string>([element.elementId]);
  while (parentElementId) {
    if (visited.has(parentElementId)) {
      return depth;
    }
    visited.add(parentElementId);
    const parent = layout.elements.find(candidate => candidate.elementId === parentElementId);
    if (!parent) {
      return depth;
    }
    depth += 1;
    parentElementId = parent.parentElementId;
  }
  return depth;
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
