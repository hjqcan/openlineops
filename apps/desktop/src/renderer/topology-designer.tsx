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
  DriverBindingRouteResponse,
  ProductionLineRuntimeStateResponse,
  ProjectApplicationResponse,
  SiteLayoutElementResponse,
  SiteLayoutResponse,
  UpdateSiteLayoutElementGeometryRequest
} from './contracts';
import {
  addAutomationSystem,
  addDriverBinding,
  addSiteLayoutElement,
  addSlotDefinition,
  addSlotGroup,
  createAutomationTopology,
  createSiteLayout,
  deleteAutomationSystem,
  deleteDriverBinding,
  deleteSlotDefinition,
  deleteSlotGroup,
  getAutomationTopology,
  getSiteLayout,
  linkProjectTopology,
  saveAutomationProjectManifest,
  updateAutomationSystem,
  updateDriverBinding,
  updateSiteLayoutElementGeometry,
  updateSlotDefinition,
  updateSlotGroup
} from './api';
import { buildProductionLineRuntimeView } from './production-line-runtime-view';
import { useEditorDocument } from './editor-workspace';
import type { EditorProblem } from './editor-workspace-model';
import {
  buildTopologyRuntimeView,
  describeRuntimeMonitorState,
  emptyTopologyRuntimeView,
  runtimeTargetStatusKey,
  toOperationalState,
  toTargetOperationalState
} from './topology-runtime-view';
import type {
  OperationalState,
  TopologyRuntimeStatus,
  TopologyRuntimeView
} from './topology-runtime-view';

type WorkspaceMode = 'edit' | 'run';
type CanvasMode = 'edit' | 'monitor';
type TopologyDimension = '2d' | '3d';
type PaletteItemKind = 'Station' | 'System' | 'Group' | 'Slot';
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

interface DriverBindingDraft {
  bindingId: string;
  ownerSystemId: string;
  capabilityId: string;
  providerKind: string;
  providerKey: string;
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

const driverProviderKinds = [
  'Simulator',
  'DeviceInstance',
  'PluginCommand',
  'ExternalSystem',
  'ProcessCommandProvider'
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
  const [documentDirty, setDocumentDirty] = useState(false);
  const rootRef = useRef<HTMLElement>(null);
  const saveStateRef = useRef(saveState);
  saveStateRef.current = saveState;

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
    return describeRuntimeMonitorState(scopedStatuses, runtimeProjection?.productionUnits.length ?? 0);
  }, [effectiveMode, runtimeProjection?.productionUnits.length, stationStatuses, topology?.systems]);

  const refresh = useCallback(async (announce = false) => {
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

    if (announce) {
      setBusy(true);
    }
    try {
      const [topologyResponse, layoutResponse] = await Promise.all([
        getAutomationTopology(topologyId, apiScope, releaseSnapshot?.snapshotId),
        getSiteLayout(layoutId, apiScope, releaseSnapshot?.snapshotId)
      ]);
      setTopology(topologyResponse.ok && topologyResponse.body ? topologyResponse.body : null);
      setLayout(layoutResponse.ok && layoutResponse.body ? layoutResponse.body : null);
      if (releaseSnapshot && (!topologyResponse.ok || !layoutResponse.ok)) {
        onMessage(
          `Published topology load failed for ${releaseSnapshot.snapshotId}: topology ${topologyResponse.status}, layout ${layoutResponse.status}.`);
      } else if (announce) {
        const revisionLabel = topologyResponse.body?.revision.slice(0, 12) ?? 'unavailable';
        onMessage(`2D layout refreshed ${layoutId} @ ${revisionLabel}`);
      }
    } finally {
      if (announce) {
        setBusy(false);
      }
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
          }, apiScope, { revision: topology.revision }),
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
          addSiteLayoutElement(layout.layoutId, element, apiScope, { revision: layout.revision }),
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
          }, apiScope, { revision: topology.revision }),
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
          addSiteLayoutElement(layout.layoutId, element, apiScope, { revision: layout.revision }),
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
          }, apiScope, { revision: topology.revision }),
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
          addSiteLayoutElement(layout.layoutId, element, apiScope, { revision: layout.revision }),
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
        }, apiScope, { revision: topology.revision }),
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
        addSiteLayoutElement(layout.layoutId, element, apiScope, { revision: layout.revision }),
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
    setDocumentDirty(true);
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
        apiScope,
        { revision: layout.revision });
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
          }, apiScope, { revision: topology.revision }),
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
          }, apiScope, { revision: topology.revision }),
          `Save Slot Group ${element.target.targetId}`);
      } else {
        nextTopology = await requireBody(
          updateSlotDefinition(topology.topologyId, element.target.targetId, {
            displayName: requireText(draft.displayName, 'Display Name'),
            address: requireText(draft.slotAddress, 'Slot Address'),
            materialKind: draft.slotMaterialKind,
            isEnabled: draft.slotEnabled
          }, apiScope, { revision: topology.revision }),
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

  const saveDriverBinding = useCallback(async (
    originalBindingId: string | null,
    draft: DriverBindingDraft
  ) => {
    if (!editable || !topology || !apiScope) {
      onMessage('Driver bindings can only be changed in Edit mode.');
      return;
    }

    setBusy(true);
    setSaveState('saving');
    try {
      const bindingId = requireText(draft.bindingId, 'Binding ID');
      if (originalBindingId !== null && originalBindingId !== bindingId) {
        throw new Error('Binding identity is immutable; create a new binding to change it.');
      }
      const request = {
        ownerSystemId: requireText(draft.ownerSystemId, 'Owner System'),
        capabilityId: requireText(draft.capabilityId, 'Capability'),
        providerKind: requireText(draft.providerKind, 'Provider Kind'),
        providerKey: requireText(draft.providerKey, 'Provider Key')
      };
      const nextTopology = originalBindingId === null
        ? await requireBody(
          addDriverBinding(topology.topologyId, { bindingId, ...request }, apiScope, { revision: topology.revision }),
          `Create Driver binding ${bindingId}`)
        : await requireBody(
          updateDriverBinding(topology.topologyId, originalBindingId, request, apiScope, { revision: topology.revision }),
          `Update Driver binding ${originalBindingId}`);
      setTopology(nextTopology);
      setSaveState('saved');
      onMessage(`${originalBindingId === null ? 'Created' : 'Updated'} Driver binding ${bindingId}`);
    } catch (error) {
      setSaveState('error');
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [apiScope, editable, onMessage, topology]);

  const removeDriverBinding = useCallback(async (bindingId: string) => {
    if (!editable || !topology || !apiScope) {
      onMessage('Driver bindings can only be deleted in Edit mode.');
      return;
    }

    setBusy(true);
    setSaveState('saving');
    try {
      const nextTopology = await requireBody(
        deleteDriverBinding(topology.topologyId, bindingId, apiScope, { revision: topology.revision }),
        `Delete Driver binding ${bindingId}`);
      setTopology(nextTopology);
      setSaveState('saved');
      onMessage(`Deleted Driver binding ${bindingId}`);
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
          deleteAutomationSystem(topology.topologyId, element.target.targetId, apiScope, { revision: topology.revision }),
          `Delete System ${element.target.targetId}`)
        : element.target.kind === 'SlotGroup'
          ? await requireBody(
            deleteSlotGroup(topology.topologyId, element.target.targetId, apiScope, { revision: topology.revision }),
            `Delete Slot Group ${element.target.targetId}`)
          : await requireBody(
            deleteSlotDefinition(topology.topologyId, element.target.targetId, apiScope, { revision: topology.revision }),
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

  useEffect(() => {
    if (saveState === 'saved') {
      setDocumentDirty(false);
    }
  }, [saveState]);

  const savePendingEdits = useCallback(async () => {
    if (!documentDirty) {
      return;
    }
    const candidates = Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>(
      '[data-testid="save-topology-target"], [data-testid="save-driver-binding"], [data-testid="save-layout-geometry"]') ?? []);
    const saveButton = candidates.find(button => !button.disabled && button.offsetParent !== null);
    if (!saveButton) {
      throw new Error('Select the edited topology target before saving it.');
    }
    saveButton.click();
    await waitForTopologySave(saveStateRef);
  }, [documentDirty]);
  const topologyProblems = useMemo<EditorProblem[]>(() => saveState === 'error'
    ? [{
      id: 'topology-save-error',
      severity: 'Error',
      message: 'The most recent topology change was not saved.',
      targetId: selectedElementId
    }]
    : [], [saveState, selectedElementId]);
  useEditorDocument({
    dirty: documentDirty,
    canSave: editable,
    save: savePendingEdits,
    revert: async () => {
      await refresh();
      setDocumentDirty(false);
    },
    focus: targetId => {
      if (!targetId || !layout) return;
      const element = layout.elements.find(candidate => (
        candidate.elementId === targetId || candidate.target.targetId === targetId));
      if (element) setSelectedElementId(element.elementId);
    },
    problems: topologyProblems
  });

  return (
    <section
      ref={rootRef}
      className="topology-ide"
      data-testid="topology-workbench"
      onInputCapture={() => setDocumentDirty(true)}
    >
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
          <button type="button" className="button ghost" onClick={() => void refresh(true)} disabled={!isBackendHealthy || busy} data-testid="refresh-topology-layout">
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

          <div className="topology-canvas-plane">
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
          onSaveDriverBinding={saveDriverBinding}
          onDeleteDriverBinding={removeDriverBinding}
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
  onSaveDriverBinding,
  onDeleteDriverBinding,
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
  onSaveDriverBinding(originalBindingId: string | null, draft: DriverBindingDraft): void;
  onDeleteDriverBinding(bindingId: string): void;
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
      {selectedElement?.target.kind === 'System' && topology ? (
        <DriverBindingEditor
          topology={topology}
          selectedSystemId={selectedElement.target.targetId}
          editable={editable}
          onSave={onSaveDriverBinding}
          onDelete={onDeleteDriverBinding}
        />
      ) : null}
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

function DriverBindingEditor({
  topology,
  selectedSystemId,
  editable,
  onSave,
  onDelete
}: {
  topology: AutomationTopologyResponse;
  selectedSystemId: string;
  editable: boolean;
  onSave(originalBindingId: string | null, draft: DriverBindingDraft): void;
  onDelete(bindingId: string): void;
}): React.ReactElement {
  const ownedBindings = topology.driverBindings
    .filter(binding => binding.ownerSystemId === selectedSystemId)
    .sort((left, right) => left.bindingId.localeCompare(right.bindingId));
  const [selectedBindingId, setSelectedBindingId] = useState<string | null>(null);
  const [deleteConfirmBindingId, setDeleteConfirmBindingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<DriverBindingDraft>(
    () => newDriverBindingDraft(selectedSystemId, topology));
  const selectedBinding = selectedBindingId
    ? topology.driverBindings.find(binding => binding.bindingId === selectedBindingId) ?? null
    : null;
  const ownerSystem = topology.systems.find(system => system.systemId === draft.ownerSystemId) ?? null;
  const ownerCapabilities = ownerSystem
    ? [...new Set([
      ...ownerSystem.requiredCapabilityIds,
      ...ownerSystem.providedCapabilityIds
    ])].sort((left, right) => left.localeCompare(right))
    : [];

  useEffect(() => {
    const current = selectedBindingId
      ? topology.driverBindings.find(binding => binding.bindingId === selectedBindingId) ?? null
      : null;
    if (current) {
      setDraft(toDriverBindingDraft(current));
    } else {
      setSelectedBindingId(null);
      setDraft(newDriverBindingDraft(selectedSystemId, topology));
    }
    setDeleteConfirmBindingId(null);
  }, [selectedBindingId, selectedSystemId, topology]);

  return (
    <section className="topology-driver-editor" data-testid="topology-driver-binding-editor">
      <header>
        <div><strong>Driver bindings</strong><span>{ownedBindings.length}</span></div>
        <button
          type="button"
          className="button ghost"
          disabled={!editable}
          onClick={() => {
            setSelectedBindingId(null);
            setDraft(newDriverBindingDraft(selectedSystemId, topology));
          }}
          data-testid="new-driver-binding"
        >
          <Plus size={12} /> New
        </button>
      </header>
      {ownedBindings.length > 0 ? (
        <div className="topology-driver-list">
          {ownedBindings.map(binding => (
            <button
              type="button"
              key={binding.bindingId}
              className={selectedBindingId === binding.bindingId ? 'active' : ''}
              onClick={() => setSelectedBindingId(binding.bindingId)}
            >
              <strong>{binding.bindingId}</strong>
              <span>{binding.capabilityId}</span>
              <small>{binding.providerKind} / {binding.providerKey}</small>
            </button>
          ))}
        </div>
      ) : (
        <p className="topology-driver-empty">No Driver is owned by this System.</p>
      )}
      <form
        className="topology-driver-form"
        onSubmit={event => {
          event.preventDefault();
          onSave(selectedBinding?.bindingId ?? null, draft);
        }}
      >
        <label>
          <span>Binding ID</span>
          <input
            value={draft.bindingId}
            disabled={!editable || selectedBinding !== null}
            onChange={event => setDraft(current => ({ ...current, bindingId: event.target.value }))}
            data-testid="driver-binding-id"
          />
        </label>
        <label>
          <span>Owner System</span>
          <select
            value={draft.ownerSystemId}
            disabled={!editable}
            onChange={event => {
              const ownerSystemId = event.target.value;
              const owner = topology.systems.find(system => system.systemId === ownerSystemId);
              const capabilities = owner
                ? [...new Set([...owner.requiredCapabilityIds, ...owner.providedCapabilityIds])]
                : [];
              setDraft(current => ({
                ...current,
                ownerSystemId,
                capabilityId: capabilities.includes(current.capabilityId)
                  ? current.capabilityId
                  : capabilities[0] ?? ''
              }));
            }}
            data-testid="driver-binding-owner-system"
          >
            {topology.systems.map(system => (
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
            disabled={!editable || ownerCapabilities.length === 0}
            onChange={event => setDraft(current => ({
              ...current,
              capabilityId: event.target.value
            }))}
            data-testid="driver-binding-capability"
          >
            {ownerCapabilities.length === 0 ? (
              <option value="">Owner declares no capabilities</option>
            ) : ownerCapabilities.map(capabilityId => (
              <option key={capabilityId} value={capabilityId}>{capabilityId}</option>
            ))}
          </select>
        </label>
        <label>
          <span>Provider Kind</span>
          <select
            value={draft.providerKind}
            disabled={!editable}
            onChange={event => setDraft(current => ({
              ...current,
              providerKind: event.target.value
            }))}
            data-testid="driver-binding-provider-kind"
          >
            {driverProviderKinds.map(kind => <option key={kind} value={kind}>{kind}</option>)}
          </select>
        </label>
        <label>
          <span>Provider Key</span>
          <input
            value={draft.providerKey}
            disabled={!editable}
            onChange={event => setDraft(current => ({ ...current, providerKey: event.target.value }))}
            data-testid="driver-binding-provider-key"
          />
        </label>
        <div className="topology-driver-actions">
          <button
            type="submit"
            className="button primary"
            disabled={!editable || ownerCapabilities.length === 0}
            data-testid="save-driver-binding"
          >
            <Save size={12} /> {selectedBinding ? 'Update' : 'Create'}
          </button>
          {selectedBinding ? (
            <button
              type="button"
              className="button danger"
              disabled={!editable}
              onClick={() => {
                if (deleteConfirmBindingId === selectedBinding.bindingId) {
                  onDelete(selectedBinding.bindingId);
                  setDeleteConfirmBindingId(null);
                } else {
                  setDeleteConfirmBindingId(selectedBinding.bindingId);
                }
              }}
              data-testid="delete-driver-binding"
            >
              <Trash2 size={12} />
              {deleteConfirmBindingId === selectedBinding.bindingId ? 'Confirm delete' : 'Delete'}
            </button>
          ) : null}
        </div>
      </form>
    </section>
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

function toDriverBindingDraft(binding: DriverBindingRouteResponse): DriverBindingDraft {
  return {
    bindingId: binding.bindingId,
    ownerSystemId: binding.ownerSystemId,
    capabilityId: binding.capabilityId,
    providerKind: binding.providerKind,
    providerKey: binding.providerKey
  };
}

function newDriverBindingDraft(
  ownerSystemId: string,
  topology: AutomationTopologyResponse
): DriverBindingDraft {
  const owner = topology.systems.find(system => system.systemId === ownerSystemId) ?? null;
  const capabilities = owner
    ? [...new Set([...owner.requiredCapabilityIds, ...owner.providedCapabilityIds])]
    : [];
  const ownerToken = ownerSystemId.replace(/[^a-zA-Z0-9._-]/g, '-');
  let suffix = topology.driverBindings.length + 1;
  let bindingId = `binding.${ownerToken}.${suffix}`;
  while (topology.driverBindings.some(binding => binding.bindingId === bindingId)) {
    suffix += 1;
    bindingId = `binding.${ownerToken}.${suffix}`;
  }
  return {
    bindingId,
    ownerSystemId,
    capabilityId: capabilities[0] ?? '',
    providerKind: driverProviderKinds[0],
    providerKey: `${ownerToken}.simulator`
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
  const productionView = lineState ? buildProductionLineRuntimeView(lineState) : null;
  const operationByRunId = new Map((lineState?.stations ?? []).flatMap(station =>
    station.activeOperations.map(operation => [operation.operationRunId, {
      stationSystemId: station.stationSystemId,
      operationId: operation.operationId
    }] as const)));
  const resources = (lineState?.stations ?? []).flatMap(station =>
    station.activeOperations.flatMap(operation => operation.resources.map(resource => ({
      ...resource,
      stationSystemId: station.stationSystemId,
      operationRunId: operation.operationRunId
    }))));
  return (
    <aside className={`topology-live-overlay ${connected ? 'connected' : 'stale'}`} data-testid="topology-live-projection">
      <header>
        <div><CircleDot size={13} /><strong>LIVE WIP</strong></div>
        <span>{connected ? 'CONNECTED' : 'STALE'} · {lineState?.productionUnits.length ?? 0}</span>
      </header>
      <div className="topology-live-scroll">
        <section>
          <h3>ACTIVE PRODUCTS</h3>
          {(lineState?.productionUnits ?? []).map(unit => {
            const operations = unit.activeOperationRunIds
              .map(operationRunId => operationByRunId.get(operationRunId))
              .filter((operation): operation is NonNullable<typeof operation> => operation !== undefined);
            return (
              <div
                className="topology-live-product"
                key={unit.productionUnitId}
                data-testid={`topology-production-unit-${unit.productionUnitId}`}
              >
                <i className={`status-${productionUnitTone(unit.disposition, unit.judgement)}`} />
                <span>
                  <strong>{unit.identityValue}</strong>
                  <small>{operations.map(operation => (
                    `${stationNameById.get(operation.stationSystemId) ?? operation.stationSystemId} / ${operation.operationId}`
                  )).join(' + ') || formatTopologyMaterialLocation(unit.location, stationNameById)}</small>
                </span>
                <em>{unit.disposition}</em>
              </div>
            );
          })}
          {(lineState?.productionUnits.length ?? 0) === 0 ? <p>No active products</p> : null}
        </section>

        <section>
          <h3>STATIONS & QUEUES</h3>
          {[...runtimeView.stationStatuses.entries()].map(([stationSystemId, status]) => (
            <div
              className="topology-live-station"
              key={stationSystemId}
              data-testid={`topology-station-${stationSystemId}`}
            >
              <i className={`status-${status.operationalState.toLowerCase()}`} />
              <span><strong>{stationNameById.get(stationSystemId) ?? stationSystemId}</strong><small>{status.products.length} located · {status.queueCount} queued</small></span>
              <em>{status.operationalState}</em>
            </div>
          ))}
        </section>

        <section>
          <h3>SLOT OCCUPANCY</h3>
          {runtimeView.slots.map(slot => (
            <div
              className="topology-live-slot"
              key={`${slot.stationSystemId}:${slot.slotId}`}
              data-testid={`topology-slot-${slot.stationSystemId}-${slot.slotId}`}
              data-slot-status={slot.slotState}
            >
              <i className={`status-${slot.operationalState.toLowerCase()}`} />
              <span>
                <strong>{slot.slotId}</strong>
                <small>{slot.products.join(', ') || 'empty'}</small>
              </span>
              <em>{slot.slotState}{slot.fencingToken === null ? '' : ` #${slot.fencingToken}`}</em>
            </div>
          ))}
          {runtimeView.slots.length === 0 ? <p>No Slots in this topology</p> : null}
        </section>

        <section>
          <h3>CARRIERS</h3>
          {(productionView?.carriers ?? []).map(carrier => (
            <div
              className="topology-live-carrier"
              key={carrier.carrierId}
              data-testid={`topology-carrier-${carrier.carrierId}`}
            >
              <Boxes size={12} />
              <span>
                <strong>{carrier.carrierId}</strong>
                <small>{carrier.productionUnits.map(position => (
                  `${position.carrierPositionId}: ${position.productionUnitLabel}`)).join(', ') || 'empty'}</small>
              </span>
              <em>{carrier.productionUnits.length}/{carrier.capacity}</em>
            </div>
          ))}
          {(productionView?.carriers.length ?? 0) === 0 ? <p>No active Carriers</p> : null}
        </section>

        <section>
          <h3>RESOURCE LEASES</h3>
          {resources.map(resource => (
            <div
              className="topology-live-resource"
              key={`${resource.operationRunId}:${resource.kind}:${resource.resourceId}`}
              data-testid={`topology-resource-${resource.operationRunId}-${resource.kind}-${resource.resourceId}`}
              data-resource-status={resource.status}
              data-fencing-token={resource.fencingToken ?? ''}
            >
              <LockKeyhole size={12} />
              <span>
                <strong>{resource.kind} / {resource.resourceId}</strong>
                <small>{stationNameById.get(resource.stationSystemId) ?? resource.stationSystemId}</small>
              </span>
              <em>{resource.status}{resource.fencingToken === null ? '' : ` #${resource.fencingToken}`}</em>
            </div>
          ))}
          {resources.length === 0 ? <p>No active resource leases</p> : null}
        </section>
      </div>
    </aside>
  );
}

function productionUnitTone(
  disposition: ProductionLineRuntimeStateResponse['productionUnits'][number]['disposition'],
  judgement: ProductionLineRuntimeStateResponse['productionUnits'][number]['judgement']
): string {
  if (disposition === 'Held' || disposition === 'Scrapped' || judgement === 'Failed') {
    return 'failed';
  }
  if (disposition === 'Completed' || judgement === 'Passed') {
    return 'completed';
  }
  return 'running';
}

function formatTopologyMaterialLocation(
  location: ProductionLineRuntimeStateResponse['productionUnits'][number]['location'],
  stationNameById: ReadonlyMap<string, string>
): string {
  if (!location) {
    return 'Location not assigned';
  }
  switch (location.kind) {
    case 'StationQueue':
      return `${stationNameById.get(location.stationSystemId ?? '') ?? location.stationSystemId} / queue`;
    case 'Slot':
      return `${stationNameById.get(location.stationSystemId ?? '') ?? location.stationSystemId} / ${location.slotId}`;
    case 'CarrierPosition':
      return `${location.carrierId} / ${location.carrierPositionId}`;
  }
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

async function waitForTopologySave(
  saveStateRef: React.MutableRefObject<'saved' | 'saving' | 'error'>
): Promise<void> {
  const deadline = Date.now() + 10_000;
  await new Promise(resolve => window.setTimeout(resolve, 50));
  while (Date.now() < deadline) {
    if (saveStateRef.current === 'saved') {
      return;
    }
    if (saveStateRef.current === 'error') {
      throw new Error('Topology save failed. The editor draft was preserved.');
    }
    await new Promise(resolve => window.setTimeout(resolve, 40));
  }
  throw new Error('Topology save did not complete before the editor timeout.');
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
