import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Box,
  Boxes,
  Cpu,
  GitBranch,
  Layers3,
  Map,
  Network,
  RefreshCw,
  Route,
  Waypoints
} from 'lucide-react';
import type {
  AddAutomationModuleRequest,
  AddCapabilityContractRequest,
  AddDriverBindingRequest,
  AddEquipmentNodeRequest,
  AddSiteLayoutElementRequest,
  AddSlotDefinitionRequest,
  AddSlotGroupRequest,
  AutomationProjectWorkspaceResponse,
  AutomationTopologyResponse,
  ProjectApplicationResponse,
  SiteLayoutElementResponse,
  SiteLayoutResponse,
  UpdateSiteLayoutElementGeometryRequest
} from './contracts';
import {
  addAutomationModule,
  addCapabilityContract,
  addDriverBinding,
  addEquipmentNode,
  addSiteLayoutElement,
  addSlotDefinition,
  addSlotGroup,
  createAutomationTopology,
  createSiteLayout,
  getAutomationTopology,
  getSiteLayout,
  linkProjectTopology,
  listAutomationTopologies,
  saveAutomationProjectManifest,
  updateSiteLayoutElementGeometry
} from './api';

interface TopologyDesignerProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
}

interface SeedTopologyModel {
  topologyId: string;
  layoutId: string;
  nodes: AddEquipmentNodeRequest[];
  capabilities: AddCapabilityContractRequest[];
  modules: AddAutomationModuleRequest[];
  bindings: AddDriverBindingRequest[];
  slotGroups: AddSlotGroupRequest[];
  slots: AddSlotDefinitionRequest[];
  layoutElements: AddSiteLayoutElementRequest[];
}

export function TopologyDesigner({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onWorkspaceChanged,
  onMessage
}: TopologyDesignerProps): React.ReactElement {
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [layout, setLayout] = useState<SiteLayoutResponse | null>(null);
  const [topologyCount, setTopologyCount] = useState(0);
  const [busy, setBusy] = useState(false);
  const [layoutSaving, setLayoutSaving] = useState(false);
  const [selectedElementId, setSelectedElementId] = useState<string | null>(null);

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
      : undefined,
    [activeApplication, activeWorkspace]);
  const canCallBackend = isBackendHealthy && !busy;
  const canSeed = canCallBackend && activeWorkspace !== null && activeApplication !== null && !activeApplication.topologyId;
  const canRefresh = canCallBackend && activeWorkspace !== null;
  const selectedElement = useMemo(
    () => layout?.elements.find(element => element.elementId === selectedElementId)
      ?? layout?.elements[0]
      ?? null,
    [layout, selectedElementId]);
  const facts = useMemo(
    () => topology
      ? [
        { label: 'Nodes', value: topology.nodes.length.toString() },
        { label: 'Modules', value: topology.modules.length.toString() },
        { label: 'Capabilities', value: topology.capabilities.length.toString() },
        { label: 'Bindings', value: topology.driverBindings.length.toString() },
        { label: 'Groups', value: topology.slotGroups.length.toString() },
        { label: 'Slots', value: topology.slots.length.toString() },
        { label: 'Layout', value: layout ? layout.elements.length.toString() : '0' }
      ]
      : [],
    [layout, topology]);

  const refresh = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    const summaries = await listAutomationTopologies(apiScope);
    setTopologyCount(summaries.length);

    if (!activeWorkspace || !activeApplication?.topologyId) {
      setTopology(null);
      setLayout(null);
      return;
    }

    const topologyResponse = await getAutomationTopology(activeApplication.topologyId, apiScope);
    setTopology(topologyResponse.ok && topologyResponse.body ? topologyResponse.body : null);

    const layoutResponse = await getSiteLayout(createLayoutId(activeWorkspace.project.projectId), apiScope);
    setLayout(layoutResponse.ok && layoutResponse.body ? layoutResponse.body : null);
  }, [activeApplication?.topologyId, activeWorkspace, apiScope, isBackendHealthy]);

  useEffect(() => {
    refresh().catch(error => onMessage(`Topology refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

  useEffect(() => {
    setSelectedElementId(current => layout?.elements.some(element => element.elementId === current)
      ? current
      : layout?.elements[0]?.elementId ?? null);
  }, [layout]);

  const seedTopology = useCallback(async () => {
    if (!activeWorkspace || !activeApplication) {
      onMessage('Open a project with an application before seeding topology');
      return;
    }

    setBusy(true);
    try {
      const model = createSeedTopologyModel(activeWorkspace.project.projectId);
      let nextTopology = await requireBody(
        createAutomationTopology({
          topologyId: model.topologyId,
          displayName: `${activeWorkspace.project.displayName} Main Topology`
        }, apiScope),
        'Create topology');

      for (const node of model.nodes) {
        nextTopology = await requireBody(
          addEquipmentNode(model.topologyId, node, apiScope),
          `Add node ${node.nodeId}`);
      }

      for (const capability of model.capabilities) {
        nextTopology = await requireBody(
          addCapabilityContract(model.topologyId, capability, apiScope),
          `Add capability ${capability.capabilityId}`);
      }

      for (const module of model.modules) {
        nextTopology = await requireBody(
          addAutomationModule(model.topologyId, module, apiScope),
          `Add module ${module.moduleId}`);
      }

      for (const binding of model.bindings) {
        nextTopology = await requireBody(
          addDriverBinding(model.topologyId, binding, apiScope),
          `Bind driver ${binding.bindingId}`);
      }

      for (const group of model.slotGroups) {
        nextTopology = await requireBody(
          addSlotGroup(model.topologyId, group, apiScope),
          `Add group ${group.slotGroupId}`);
      }

      for (const slot of model.slots) {
        nextTopology = await requireBody(
          addSlotDefinition(model.topologyId, slot, apiScope),
          `Add slot ${slot.slotId}`);
      }

      let nextLayout = await requireBody(
        createSiteLayout({
          layoutId: model.layoutId,
          topologyId: model.topologyId,
          displayName: 'Main Site Layout',
          canvasWidth: 1200,
          canvasHeight: 680,
          units: 'px'
        }, apiScope),
        'Create layout');

      for (const element of model.layoutElements) {
        nextLayout = await requireBody(
          addSiteLayoutElement(model.layoutId, element, apiScope),
          `Place layout element ${element.elementId}`);
      }

      const linkedProject = await linkProjectTopology(
        activeWorkspace.project.projectId,
        activeApplication.applicationId,
        { topologyId: model.topologyId });
      if (!linkedProject.ok || !linkedProject.body) {
        throw new Error(`Link topology failed: ${linkedProject.status} ${linkedProject.text}`);
      }

      const savedWorkspace = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
      if (!savedWorkspace.ok || !savedWorkspace.body) {
        throw new Error(`Save manifest failed: ${savedWorkspace.status} ${savedWorkspace.text}`);
      }

      setTopology(nextTopology);
      setLayout(nextLayout);
      onWorkspaceChanged(savedWorkspace.body);
      onMessage(`Topology seeded ${model.topologyId}`);
    } catch (error) {
      onMessage(String(error));
    } finally {
      setBusy(false);
    }
  }, [activeApplication, activeWorkspace, apiScope, onMessage, onWorkspaceChanged]);

  const previewLayoutGeometry = useCallback((
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest
  ) => {
    setLayout(current => updateLayoutGeometry(current, elementId, geometry));
  }, []);

  const commitLayoutGeometry = useCallback(async (
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ) => {
    if (!layout || !apiScope || !isBackendHealthy || layoutSaving) {
      return;
    }

    setLayoutSaving(true);
    setLayout(current => updateLayoutGeometry(current, elementId, geometry));
    try {
      const response = await updateSiteLayoutElementGeometry(
        layout.layoutId,
        elementId,
        normalizeGeometry(geometry),
        apiScope);
      if (!response.ok || !response.body) {
        setLayout(current => updateLayoutGeometry(current, elementId, previousGeometry));
        onMessage(`Layout update failed: ${response.status} ${response.text}`);
        return;
      }

      setLayout(response.body);
      setSelectedElementId(elementId);
      onMessage(`Layout geometry saved ${elementId}`);
    } catch (error) {
      setLayout(current => updateLayoutGeometry(current, elementId, previousGeometry));
      onMessage(`Layout update failed: ${String(error)}`);
    } finally {
      setLayoutSaving(false);
    }
  }, [apiScope, isBackendHealthy, layout, layoutSaving, onMessage]);

  return (
    <section className="topology-designer" data-testid="project-topology-designer">
      <div className="project-detail-section-title">
        <div>
          <Network size={15} />
          <strong>Topology Designer</strong>
          <span>{topologyCount} indexed</span>
        </div>
        <div className="topology-actions">
          <button type="button" className="button ghost" onClick={refresh} disabled={!canRefresh}>
            <RefreshCw size={14} />
            Refresh
          </button>
          <button
            type="button"
            className="button primary"
            onClick={seedTopology}
            disabled={!canSeed}
            data-testid="seed-project-topology"
          >
            <Waypoints size={14} />
            Seed Topology
          </button>
        </div>
      </div>

      {!activeWorkspace ? (
        <div className="projects-empty">No project workspace is open.</div>
      ) : !activeApplication ? (
        <div className="projects-empty">No application is available.</div>
      ) : (
        <div className="topology-designer-grid">
          <div className="topology-model-panel">
            <TopologyApplication application={activeApplication} topology={topology} />
            <TopologyFacts rows={facts} />
            <TopologyCollections topology={topology} />
          </div>
          <SiteLayoutCanvas
            layout={layout}
            selectedElement={selectedElement}
            disabled={!isBackendHealthy || layoutSaving}
            onSelect={setSelectedElementId}
            onPreviewGeometry={previewLayoutGeometry}
            onCommitGeometry={commitLayoutGeometry}
          />
        </div>
      )}
    </section>
  );
}

function TopologyApplication({
  application,
  topology
}: {
  application: ProjectApplicationResponse;
  topology: AutomationTopologyResponse | null;
}): React.ReactElement {
  return (
    <article className="topology-application">
      <GitBranch size={17} />
      <div>
        <strong>{application.displayName}</strong>
        <span>{topology?.topologyId ?? application.topologyId ?? 'no topology linked'}</span>
      </div>
    </article>
  );
}

function TopologyFacts({
  rows
}: {
  rows: Array<{ label: string; value: string }>;
}): React.ReactElement {
  return (
    <dl className="topology-facts">
      {rows.length === 0 ? (
        <>
          <dt>Status</dt>
          <dd>Draft</dd>
        </>
      ) : rows.map(row => (
        <React.Fragment key={row.label}>
          <dt>{row.label}</dt>
          <dd>{row.value}</dd>
        </React.Fragment>
      ))}
    </dl>
  );
}

function TopologyCollections({
  topology
}: {
  topology: AutomationTopologyResponse | null;
}): React.ReactElement {
  if (!topology) {
    return (
      <div className="topology-collections">
        <CollectionBlock icon={Layers3} title="Structure" rows={['site', 'line', 'station']} />
        <CollectionBlock icon={Cpu} title="Provider" rows={['capability', 'driver binding']} />
        <CollectionBlock icon={Boxes} title="Material" rows={['slot group', 'slot']} />
      </div>
    );
  }

  return (
    <div className="topology-collections">
      <CollectionBlock
        icon={Layers3}
        title="Structure"
        rows={topology.nodes.map(node => `${node.kind}: ${node.displayName}`)}
      />
      <CollectionBlock
        icon={Cpu}
        title="Capabilities"
        rows={topology.capabilities.map(capability => `${capability.commandName} v${capability.version}`)}
      />
      <CollectionBlock
        icon={Boxes}
        title="Slots"
        rows={topology.slots.map(slot => `${slot.address}: ${slot.displayName}`)}
      />
    </div>
  );
}

function CollectionBlock({
  icon: Icon,
  title,
  rows
}: {
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  rows: string[];
}): React.ReactElement {
  return (
    <section className="topology-collection">
      <div>
        <Icon size={15} />
        <strong>{title}</strong>
        <span>{rows.length}</span>
      </div>
      {rows.slice(0, 5).map(row => (
        <span key={row}>{row}</span>
      ))}
    </section>
  );
}

function SiteLayoutCanvas({
  layout,
  selectedElement,
  disabled,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry
}: {
  layout: SiteLayoutResponse | null;
  selectedElement: SiteLayoutElementResponse | null;
  disabled: boolean;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
}): React.ReactElement {
  return (
    <div className="site-layout-shell">
      <div className="site-layout-header">
        <div>
          <Map size={15} />
          <strong>Top-down Layout</strong>
          <em>{selectedElement ? selectedElement.label : 'Select an element'}</em>
        </div>
        <span>{layout ? `${layout.canvasWidth}x${layout.canvasHeight} ${layout.units}` : 'draft'}</span>
      </div>
      <div className="site-layout-canvas" data-testid="site-layout-canvas">
        {layout ? layout.elements.map(element => (
          <LayoutElement
            key={element.elementId}
            element={element}
            layout={layout}
            selected={element.elementId === selectedElement?.elementId}
            disabled={disabled}
            onSelect={onSelect}
            onPreviewGeometry={onPreviewGeometry}
            onCommitGeometry={onCommitGeometry}
          />
        )) : (
          <div className="site-layout-placeholder">
            <Route size={24} />
            <span>No layout elements</span>
          </div>
        )}
        {layout ? (
          <div className="site-layout-canvas-hint">
            Drag to move · Arrow keys nudge · Shift moves 10 {layout.units}
          </div>
        ) : null}
      </div>
      <LayoutGeometryInspector
        layout={layout}
        element={selectedElement}
        disabled={disabled}
        onCommit={onCommitGeometry}
      />
    </div>
  );
}

interface ElementDragState {
  pointerId: number;
  startClientX: number;
  startClientY: number;
  canvasWidthPx: number;
  canvasHeightPx: number;
  previousGeometry: UpdateSiteLayoutElementGeometryRequest;
  latestGeometry: UpdateSiteLayoutElementGeometryRequest;
  moved: boolean;
}

function LayoutElement({
  element,
  layout,
  selected,
  disabled,
  onSelect,
  onPreviewGeometry,
  onCommitGeometry
}: {
  element: SiteLayoutElementResponse;
  layout: SiteLayoutResponse;
  selected: boolean;
  disabled: boolean;
  onSelect(elementId: string): void;
  onPreviewGeometry(elementId: string, geometry: UpdateSiteLayoutElementGeometryRequest): void;
  onCommitGeometry(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
}): React.ReactElement {
  const dragState = useRef<ElementDragState | null>(null);
  const [dragging, setDragging] = useState(false);
  const style = {
    left: `${Math.max(0, (element.x / layout.canvasWidth) * 100)}%`,
    top: `${Math.max(0, (element.y / layout.canvasHeight) * 100)}%`,
    width: `${Math.max(2, (element.width / layout.canvasWidth) * 100)}%`,
    height: `${Math.max(2, (element.height / layout.canvasHeight) * 100)}%`,
    transform: `rotate(${element.rotationDegrees}deg)`
  };

  const handlePointerDown = (event: React.PointerEvent<HTMLButtonElement>): void => {
    if (disabled || event.button !== 0) {
      return;
    }

    const canvas = event.currentTarget.parentElement;
    const bounds = canvas?.getBoundingClientRect();
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
      canvasWidthPx: bounds.width,
      canvasHeightPx: bounds.height,
      previousGeometry: geometry,
      latestGeometry: geometry,
      moved: false
    };
    setDragging(true);
    onSelect(element.elementId);
  };

  const handlePointerMove = (event: React.PointerEvent<HTMLButtonElement>): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    const deltaX = ((event.clientX - drag.startClientX) / drag.canvasWidthPx) * layout.canvasWidth;
    const deltaY = ((event.clientY - drag.startClientY) / drag.canvasHeightPx) * layout.canvasHeight;
    const nextGeometry = clampGeometry(
      {
        ...drag.previousGeometry,
        x: drag.previousGeometry.x + deltaX,
        y: drag.previousGeometry.y + deltaY
      },
      layout);
    drag.latestGeometry = nextGeometry;
    drag.moved = drag.moved || Math.abs(deltaX) > 0.2 || Math.abs(deltaY) > 0.2;
    onPreviewGeometry(element.elementId, nextGeometry);
  };

  const handlePointerUp = (event: React.PointerEvent<HTMLButtonElement>): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    dragState.current = null;
    setDragging(false);
    if (drag.moved) {
      onCommitGeometry(element.elementId, drag.latestGeometry, drag.previousGeometry);
    }
  };

  const handlePointerCancel = (event: React.PointerEvent<HTMLButtonElement>): void => {
    const drag = dragState.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    dragState.current = null;
    setDragging(false);
    onPreviewGeometry(element.elementId, drag.previousGeometry);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>): void => {
    if (disabled || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      return;
    }

    event.preventDefault();
    const step = event.shiftKey ? 10 : 1;
    const previousGeometry = toGeometry(element);
    const nextGeometry = clampGeometry({
      ...previousGeometry,
      x: previousGeometry.x + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0),
      y: previousGeometry.y + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0)
    }, layout);
    onPreviewGeometry(element.elementId, nextGeometry);
    onCommitGeometry(element.elementId, nextGeometry, previousGeometry);
  };

  return (
    <button
      type="button"
      className={`site-layout-element ${toElementClass(element.kind)}${selected ? ' selected' : ''}${dragging ? ' dragging' : ''}`}
      style={style}
      title={`${element.label} · ${element.targetId}`}
      aria-pressed={selected}
      disabled={disabled}
      onClick={() => onSelect(element.elementId)}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerCancel}
      onKeyDown={handleKeyDown}
      data-testid={`layout-element-${element.elementId}`}
    >
      {element.kind === 'ModuleShape' ? <Box size={13} /> : null}
      <span>{element.label}</span>
    </button>
  );
}

function LayoutGeometryInspector({
  layout,
  element,
  disabled,
  onCommit
}: {
  layout: SiteLayoutResponse | null;
  element: SiteLayoutElementResponse | null;
  disabled: boolean;
  onCommit(
    elementId: string,
    geometry: UpdateSiteLayoutElementGeometryRequest,
    previousGeometry: UpdateSiteLayoutElementGeometryRequest
  ): void;
}): React.ReactElement {
  const [draft, setDraft] = useState<UpdateSiteLayoutElementGeometryRequest | null>(
    element ? toGeometry(element) : null);

  useEffect(() => {
    setDraft(element ? toGeometry(element) : null);
  }, [element]);

  if (!layout || !element || !draft) {
    return <div className="layout-geometry-inspector empty">Select a layout element to edit geometry.</div>;
  }

  const fields: Array<{
    key: keyof UpdateSiteLayoutElementGeometryRequest;
    label: string;
  }> = [
    { key: 'x', label: 'X' },
    { key: 'y', label: 'Y' },
    { key: 'width', label: 'W' },
    { key: 'height', label: 'H' },
    { key: 'rotationDegrees', label: 'ROT' }
  ];

  return (
    <form
      className="layout-geometry-inspector"
      onSubmit={event => {
        event.preventDefault();
        onCommit(element.elementId, clampGeometry(draft, layout), toGeometry(element));
      }}
    >
      <div className="layout-inspector-target">
        <strong>{element.label}</strong>
        <span>{element.kind} · {element.targetId}</span>
      </div>
      {fields.map(field => (
        <label key={field.key}>
          <span>{field.label}</span>
          <input
            type="number"
            step="0.1"
            value={draft[field.key]}
            disabled={disabled}
            data-testid={`layout-geometry-${field.key}`}
            onChange={event => setDraft(current => current
              ? { ...current, [field.key]: Number(event.target.value) }
              : current)}
          />
        </label>
      ))}
      <button type="submit" className="button primary" disabled={disabled} data-testid="save-layout-geometry">
        Apply
      </button>
    </form>
  );
}

function toGeometry(element: SiteLayoutElementResponse): UpdateSiteLayoutElementGeometryRequest {
  return {
    x: element.x,
    y: element.y,
    width: element.width,
    height: element.height,
    rotationDegrees: element.rotationDegrees
  };
}

function clampGeometry(
  geometry: UpdateSiteLayoutElementGeometryRequest,
  layout: SiteLayoutResponse
): UpdateSiteLayoutElementGeometryRequest {
  const width = clampFinite(geometry.width, 1, layout.canvasWidth);
  const height = clampFinite(geometry.height, 1, layout.canvasHeight);

  return normalizeGeometry({
    x: clampFinite(geometry.x, 0, layout.canvasWidth - width),
    y: clampFinite(geometry.y, 0, layout.canvasHeight - height),
    width,
    height,
    rotationDegrees: Number.isFinite(geometry.rotationDegrees) ? geometry.rotationDegrees : 0
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
    rotationDegrees: round(geometry.rotationDegrees)
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

function createSeedTopologyModel(projectId: string): SeedTopologyModel {
  const topologyId = createTopologyId(projectId);
  const layoutId = createLayoutId(projectId);

  return {
    topologyId,
    layoutId,
    nodes: [
      {
        nodeId: `${projectId}.site`,
        parentNodeId: null,
        kind: 'Site',
        displayName: 'Factory Site'
      },
      {
        nodeId: `${projectId}.line.1`,
        parentNodeId: `${projectId}.site`,
        kind: 'Line',
        displayName: 'Line 1'
      },
      {
        nodeId: `${projectId}.station.1`,
        parentNodeId: `${projectId}.line.1`,
        kind: 'Station',
        displayName: 'Station 1'
      }
    ],
    capabilities: [
      {
        capabilityId: `${projectId}.motion.axis.move`,
        commandName: 'motion.axis.move',
        version: '1.0.0',
        inputSchema: '{"axis":"string","position":"number","unit":"string"}',
        outputSchema: '{"completed":"boolean"}',
        timeoutSeconds: 15,
        safetyClass: 'Motion'
      },
      {
        capabilityId: `${projectId}.io.light.write`,
        commandName: 'io.light.write',
        version: '1.0.0',
        inputSchema: '{"channel":"string","enabled":"boolean"}',
        outputSchema: '{"completed":"boolean"}',
        timeoutSeconds: 5,
        safetyClass: 'Normal'
      }
    ],
    modules: [
      {
        moduleId: `${projectId}.module.axis.x`,
        nodeId: `${projectId}.station.1`,
        moduleKind: 'AxisMotion',
        displayName: 'X Axis',
        requiredCapabilityIds: [`${projectId}.motion.axis.move`],
        providedCapabilityIds: [`${projectId}.motion.axis.move`]
      },
      {
        moduleId: `${projectId}.module.light.top`,
        nodeId: `${projectId}.station.1`,
        moduleKind: 'Lighting',
        displayName: 'Top Light',
        requiredCapabilityIds: [`${projectId}.io.light.write`],
        providedCapabilityIds: [`${projectId}.io.light.write`]
      }
    ],
    bindings: [
      {
        bindingId: `${projectId}.binding.axis.x.simulator`,
        capabilityId: `${projectId}.motion.axis.move`,
        providerKind: 'Simulator',
        providerKey: 'simulator.axis.x'
      },
      {
        bindingId: `${projectId}.binding.light.top.simulator`,
        capabilityId: `${projectId}.io.light.write`,
        providerKind: 'Simulator',
        providerKey: 'simulator.light.top'
      }
    ],
    slotGroups: [
      {
        slotGroupId: `${projectId}.slot-group.left-nest`,
        parentNodeId: `${projectId}.station.1`,
        displayName: 'Left Nest',
        kind: 'FixtureNest',
        capacity: 2
      }
    ],
    slots: [
      {
        slotGroupId: `${projectId}.slot-group.left-nest`,
        slotId: `${projectId}.slot.left-nest.1`,
        parentNodeId: `${projectId}.station.1`,
        address: 'L1',
        displayName: 'Left Nest Slot 1',
        materialKind: 'Dut',
        isEnabled: true
      },
      {
        slotGroupId: `${projectId}.slot-group.left-nest`,
        slotId: `${projectId}.slot.left-nest.2`,
        parentNodeId: `${projectId}.station.1`,
        address: 'L2',
        displayName: 'Left Nest Slot 2',
        materialKind: 'Dut',
        isEnabled: true
      }
    ],
    layoutElements: [
      {
        elementId: `${projectId}.element.station.1`,
        kind: 'NodeShape',
        targetKind: 'EquipmentNode',
        targetId: `${projectId}.station.1`,
        x: 120,
        y: 120,
        width: 620,
        height: 360,
        rotationDegrees: 0,
        layerId: 'equipment',
        label: 'Station 1'
      },
      {
        elementId: `${projectId}.element.axis.x`,
        kind: 'ModuleShape',
        targetKind: 'AutomationModule',
        targetId: `${projectId}.module.axis.x`,
        x: 190,
        y: 205,
        width: 430,
        height: 54,
        rotationDegrees: 0,
        layerId: 'equipment',
        label: 'X Axis'
      },
      {
        elementId: `${projectId}.element.left-nest`,
        kind: 'GroupRegion',
        targetKind: 'SlotGroup',
        targetId: `${projectId}.slot-group.left-nest`,
        x: 235,
        y: 310,
        width: 250,
        height: 110,
        rotationDegrees: 0,
        layerId: 'material',
        label: 'Left Nest'
      },
      {
        elementId: `${projectId}.element.slot.1`,
        kind: 'SlotShape',
        targetKind: 'Slot',
        targetId: `${projectId}.slot.left-nest.1`,
        x: 275,
        y: 342,
        width: 68,
        height: 54,
        rotationDegrees: 0,
        layerId: 'material',
        label: 'L1'
      },
      {
        elementId: `${projectId}.element.slot.2`,
        kind: 'SlotShape',
        targetKind: 'Slot',
        targetId: `${projectId}.slot.left-nest.2`,
        x: 375,
        y: 342,
        width: 68,
        height: 54,
        rotationDegrees: 0,
        layerId: 'material',
        label: 'L2'
      }
    ]
  };
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

function createTopologyId(projectId: string): string {
  return `${projectId}.topology.main`;
}

function createLayoutId(projectId: string): string {
  return `${projectId}.layout.main`;
}

function toElementClass(kind: string): string {
  return kind.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
}
