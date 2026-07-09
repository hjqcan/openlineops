import React, { useCallback, useEffect, useMemo, useState } from 'react';
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
  SiteLayoutResponse
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
  saveAutomationProjectManifest
} from './api';

interface TopologyDesignerProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
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
  isBackendHealthy,
  onWorkspaceChanged,
  onMessage
}: TopologyDesignerProps): React.ReactElement {
  const [topology, setTopology] = useState<AutomationTopologyResponse | null>(null);
  const [layout, setLayout] = useState<SiteLayoutResponse | null>(null);
  const [topologyCount, setTopologyCount] = useState(0);
  const [busy, setBusy] = useState(false);

  const activeApplication = activeWorkspace?.project.applications[0] ?? null;
  const canCallBackend = isBackendHealthy && !busy;
  const canSeed = canCallBackend && activeWorkspace !== null && activeApplication !== null && !activeApplication.topologyId;
  const canRefresh = canCallBackend && activeWorkspace !== null;
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

    const summaries = await listAutomationTopologies();
    setTopologyCount(summaries.length);

    if (!activeWorkspace || !activeApplication?.topologyId) {
      setTopology(null);
      setLayout(null);
      return;
    }

    const topologyResponse = await getAutomationTopology(activeApplication.topologyId);
    setTopology(topologyResponse.ok && topologyResponse.body ? topologyResponse.body : null);

    const layoutResponse = await getSiteLayout(createLayoutId(activeWorkspace.project.projectId));
    setLayout(layoutResponse.ok && layoutResponse.body ? layoutResponse.body : null);
  }, [activeApplication?.topologyId, activeWorkspace, isBackendHealthy]);

  useEffect(() => {
    refresh().catch(error => onMessage(`Topology refresh failed: ${String(error)}`));
  }, [onMessage, refresh]);

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
        }),
        'Create topology');

      for (const node of model.nodes) {
        nextTopology = await requireBody(addEquipmentNode(model.topologyId, node), `Add node ${node.nodeId}`);
      }

      for (const capability of model.capabilities) {
        nextTopology = await requireBody(
          addCapabilityContract(model.topologyId, capability),
          `Add capability ${capability.capabilityId}`);
      }

      for (const module of model.modules) {
        nextTopology = await requireBody(addAutomationModule(model.topologyId, module), `Add module ${module.moduleId}`);
      }

      for (const binding of model.bindings) {
        nextTopology = await requireBody(addDriverBinding(model.topologyId, binding), `Bind driver ${binding.bindingId}`);
      }

      for (const group of model.slotGroups) {
        nextTopology = await requireBody(addSlotGroup(model.topologyId, group), `Add group ${group.slotGroupId}`);
      }

      for (const slot of model.slots) {
        nextTopology = await requireBody(addSlotDefinition(model.topologyId, slot), `Add slot ${slot.slotId}`);
      }

      let nextLayout = await requireBody(
        createSiteLayout({
          layoutId: model.layoutId,
          topologyId: model.topologyId,
          displayName: 'Main Site Layout',
          canvasWidth: 1200,
          canvasHeight: 680,
          units: 'px'
        }),
        'Create layout');

      for (const element of model.layoutElements) {
        nextLayout = await requireBody(
          addSiteLayoutElement(model.layoutId, element),
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
  }, [activeApplication, activeWorkspace, onMessage, onWorkspaceChanged]);

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
          <SiteLayoutCanvas layout={layout} />
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
  layout
}: {
  layout: SiteLayoutResponse | null;
}): React.ReactElement {
  return (
    <div className="site-layout-shell">
      <div className="site-layout-header">
        <div>
          <Map size={15} />
          <strong>Top-down Layout</strong>
        </div>
        <span>{layout ? `${layout.canvasWidth}x${layout.canvasHeight} ${layout.units}` : 'draft'}</span>
      </div>
      <div className="site-layout-canvas" data-testid="site-layout-canvas">
        {layout ? layout.elements.map(element => (
          <LayoutElement key={element.elementId} element={element} layout={layout} />
        )) : (
          <div className="site-layout-placeholder">
            <Route size={24} />
            <span>No layout elements</span>
          </div>
        )}
      </div>
    </div>
  );
}

function LayoutElement({
  element,
  layout
}: {
  element: SiteLayoutElementResponse;
  layout: SiteLayoutResponse;
}): React.ReactElement {
  const style = {
    left: `${Math.max(0, (element.x / layout.canvasWidth) * 100)}%`,
    top: `${Math.max(0, (element.y / layout.canvasHeight) * 100)}%`,
    width: `${Math.max(4, (element.width / layout.canvasWidth) * 100)}%`,
    height: `${Math.max(4, (element.height / layout.canvasHeight) * 100)}%`,
    transform: `rotate(${element.rotationDegrees}deg)`
  };

  return (
    <div className={`site-layout-element ${toElementClass(element.kind)}`} style={style} title={element.targetId}>
      {element.kind === 'ModuleShape' ? <Box size={13} /> : null}
      <span>{element.label}</span>
    </div>
  );
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
