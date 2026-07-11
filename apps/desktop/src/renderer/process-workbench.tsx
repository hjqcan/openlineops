import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as Blockly from 'blockly';
import {
  ArrowRight,
  Braces,
  CheckCircle2,
  FilePlus2,
  GitBranch,
  History,
  ListChecks,
  MapPin,
  Network,
  Play,
  Plus,
  RotateCcw,
  Save,
  Trash2,
  Workflow
} from 'lucide-react';
import type {
  AutomationProjectWorkspaceResponse,
  AutomationTopologyResponse,
  CreateProcessDefinitionRequest,
  CreateProcessNodeRequest,
  ProcessBlocklyBlockDefinition,
  ProcessDefinitionResponse,
  ProcessDefinitionSummary,
  ProcessGraphValidationReport,
  ProcessNodeResponse,
  RegisterProcessBlocklyBlockDefinitionRequest
} from './contracts';
import {
  createProcessDefinition,
  getAutomationTopology,
  getProcessDefinition,
  listProcessBlocklyBlocks,
  listProcessBlocklyBlockVersions,
  listProcessDefinitions,
  publishProcessDefinition,
  registerProcessBlocklyBlock,
  updateProcessDefinition,
  validateProcessDefinition
} from './api';

type ProcessNodeKind = 'Start' | 'Command' | 'Decision' | 'Delay' | 'End' | 'Blockly' | 'PythonScript';
type AddableProcessNodeKind = Exclude<ProcessNodeKind, 'Start'>;
type TransitionLoopPolicy = 'None' | 'Counted';
type ProcessTargetKind = 'System' | 'SlotGroup' | 'Slot' | 'ProductionUnit' | 'Capability' | 'Driver';

interface ProcessWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface ProcessNodeDraft {
  nodeId: string;
  kind: ProcessNodeKind;
  displayName: string;
  requiredCapability: string;
  commandName: string;
  targetKind: ProcessTargetKind;
  targetId: string;
  timeoutSeconds: number;
  inputPayload: string;
  scriptVersion: string;
  blocklyWorkspaceJson: string;
  manualSourceCode: string;
}

interface ProcessTransitionDraft {
  transitionId: string;
  fromNodeId: string;
  toNodeId: string;
  label: string;
  loopPolicy: TransitionLoopPolicy;
  maxTraversals: number;
}

interface ProcessDraft {
  processDefinitionId: string;
  versionId: string;
  displayName: string;
  selectedNodeId: string;
  nodes: ProcessNodeDraft[];
  transitions: ProcessTransitionDraft[];
}

interface ProjectTargetOption {
  testId: string;
  targetKind: ProcessTargetKind;
  targetId: string;
  label: string;
  detail: string;
  capabilities: ProjectTargetCapabilityOption[];
}

interface ProjectTargetCapabilityOption {
  capabilityId: string;
  commandName: string;
  providerKey: string;
}

const projectTargetFieldType = 'field_openlineops_project_target';
const projectCapabilityFieldType = 'field_openlineops_project_capability';
const projectTargetsByWorkspace = new WeakMap<Blockly.Workspace, ProjectTargetOption[]>();
const registeredProcessBlockTypes = new Set<string>();

class ProjectTargetDropdown extends Blockly.FieldDropdown {
  constructor() {
    super(function (this: Blockly.FieldDropdown): Blockly.MenuOption[] {
      const sourceBlock = this.getSourceBlock();
      if (!sourceBlock) {
        return [['Select target', '']];
      }

      const targetKind = String(sourceBlock.getFieldValue('TARGET_KIND') ?? '');
      const targets = (projectTargetsByWorkspace.get(sourceBlock.workspace) ?? [])
        .filter(target => target.targetKind === targetKind);
      const currentValue = this.getValue();
      const options: Blockly.MenuOption[] = targets.map(target => [
        target.label,
        target.targetId
      ]);

      if (currentValue && !targets.some(target => target.targetId === currentValue)) {
        options.push([`Missing target: ${currentValue}`, currentValue]);
      }

      return options.length > 0
        ? options
        : [[`No ${targetKind || 'matching'} targets`, '']];
    });
  }

  static fromJson(): ProjectTargetDropdown {
    return new ProjectTargetDropdown();
  }

  protected override doClassValidation_(newValue?: string): string | null {
    return typeof newValue === 'string' ? newValue : null;
  }
}

class ProjectCapabilityDropdown extends Blockly.FieldDropdown {
  constructor() {
    super(function (this: Blockly.FieldDropdown): Blockly.MenuOption[] {
      const sourceBlock = this.getSourceBlock();
      if (!sourceBlock) {
        return [['Select capability', '']];
      }

      const targetKind = String(sourceBlock.getFieldValue('TARGET_KIND') ?? '');
      const targetId = String(sourceBlock.getFieldValue('TARGET_ID') ?? '');
      const target = (projectTargetsByWorkspace.get(sourceBlock.workspace) ?? [])
        .find(candidate => candidate.targetKind === targetKind && candidate.targetId === targetId);
      const currentValue = this.getValue();
      const options: Blockly.MenuOption[] = (target?.capabilities ?? []).map(capability => [
        `${capability.commandName} · ${capability.capabilityId}`,
        capability.capabilityId
      ]);

      if (currentValue && !target?.capabilities.some(capability => capability.capabilityId === currentValue)) {
        options.push([`Missing capability: ${currentValue}`, currentValue]);
      }

      return options.length > 0
        ? options
        : [['No capability for selected target', '']];
    });
  }

  static fromJson(): ProjectCapabilityDropdown {
    return new ProjectCapabilityDropdown();
  }

  protected override doClassValidation_(newValue?: string): string | null {
    return typeof newValue === 'string' ? newValue : null;
  }
}

interface CustomBlocklyBlockDraft {
  blockType: string;
  category: string;
  displayName: string;
  blocklyJsonText: string;
  runtimeActionContractText: string;
}

const addableNodeKinds: AddableProcessNodeKind[] = [
  'Blockly',
  'PythonScript',
  'Command',
  'Decision',
  'Delay',
  'End'
];
const processTargetKinds: ProcessTargetKind[] = [
  'System',
  'Capability',
  'Driver',
  'SlotGroup',
  'Slot',
  'ProductionUnit'
];

export function ProcessWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onMessage
}: ProcessWorkbenchProps): React.ReactElement {
  const [definitions, setDefinitions] = useState<ProcessDefinitionSummary[]>([]);
  const [selectedDefinition, setSelectedDefinition] = useState<ProcessDefinitionResponse | null>(null);
  const [editingDefinitionId, setEditingDefinitionId] = useState<string | null>(null);
  const [loadedDefinitionStatus, setLoadedDefinitionStatus] = useState<string | null>(null);
  const [validationReport, setValidationReport] = useState<ProcessGraphValidationReport | null>(null);
  const [draft, setDraft] = useState<ProcessDraft>(() => createDraft());
  const [blockCatalog, setBlockCatalog] =
    useState<ProcessBlocklyBlockDefinition[]>([]);
  const [customBlockDraft, setCustomBlockDraft] =
    useState<CustomBlocklyBlockDraft>(() => createCustomBlocklyBlockDraft());
  const [selectedBlockHistoryType, setSelectedBlockHistoryType] = useState('');
  const [blockHistory, setBlockHistory] = useState<ProcessBlocklyBlockDefinition[]>([]);
  const [blockHistoryBusy, setBlockHistoryBusy] = useState(false);
  const [projectTopology, setProjectTopology] = useState<AutomationTopologyResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const activeApplication = activeWorkspace?.project.applications.find(
    application => application.applicationId === activeApplicationId)
    ?? activeWorkspace?.project.applications[0]
    ?? null;
  const projectApplicationApiScope = useMemo(
    () => activeWorkspace && activeApplication
      ? {
        projectId: activeWorkspace.project.projectId,
        applicationId: activeApplication.applicationId
      }
      : undefined,
    [activeApplication, activeWorkspace]);
  const editorScopeKey = activeWorkspace && activeApplication
    ? `${activeWorkspace.project.projectId}\u0000${activeApplication.applicationId}`
    : null;
  const blockScopeKeyRef = useRef(editorScopeKey);
  blockScopeKeyRef.current = editorScopeKey;
  const isLoadedDefinitionReadOnly = editingDefinitionId !== null
    && loadedDefinitionStatus !== 'Draft';
  const selectedNode = useMemo(
    () => draft.nodes.find(node => node.nodeId === draft.selectedNodeId) ?? draft.nodes[0] ?? null,
    [draft.nodes, draft.selectedNodeId]);
  const previewWorkspaceJson = selectedNode?.kind === 'Blockly'
    ? selectedNode.blocklyWorkspaceJson
    : '';
  const orderedNodes = useMemo(
    () => orderNodesByTransitions(draft.nodes, draft.transitions),
    [draft.nodes, draft.transitions]);
  const customBlocks = useMemo(
    () => blockCatalog
      .filter(block => !block.isBuiltIn)
      .sort((left, right) => left.displayName.localeCompare(right.displayName)),
    [blockCatalog]);
  const projectTargets = useMemo(
    () => createProjectTargetOptions(activeWorkspace, activeApplication, projectTopology),
    [activeApplication, activeWorkspace, projectTopology]);

  const loadDefinitions = useCallback(async () => {
    if (!isBackendHealthy || !projectApplicationApiScope) {
      return;
    }

    const rows = await listProcessDefinitions(projectApplicationApiScope);
    setDefinitions(rows);
  }, [isBackendHealthy, projectApplicationApiScope]);

  const loadBlocklyBlocks = useCallback(async () => {
    const requestedScopeKey = editorScopeKey;
    if (!isBackendHealthy || !projectApplicationApiScope) {
      if (blockScopeKeyRef.current === requestedScopeKey) {
        setBlockCatalog([]);
      }
      return;
    }

    const rows = await listProcessBlocklyBlocks(projectApplicationApiScope);
    if (blockScopeKeyRef.current === requestedScopeKey) {
      setBlockCatalog(rows);
    }
  }, [editorScopeKey, isBackendHealthy, projectApplicationApiScope]);

  const loadProjectTopology = useCallback(async () => {
    if (!isBackendHealthy || !activeApplication?.topologyId || !projectApplicationApiScope) {
      setProjectTopology(null);
      return;
    }

    const response = await getAutomationTopology(activeApplication.topologyId, projectApplicationApiScope);
    setProjectTopology(response.ok && response.body ? response.body : null);
  }, [activeApplication?.topologyId, isBackendHealthy, projectApplicationApiScope]);

  useEffect(() => {
    loadDefinitions().catch(error => onMessage(`Process list failed: ${String(error)}`));
  }, [loadDefinitions, onMessage]);

  useEffect(() => {
    setSelectedDefinition(null);
    setEditingDefinitionId(null);
    setLoadedDefinitionStatus(null);
    setValidationReport(null);
    setDraft(createDraft());
    setBlockCatalog([]);
    setSelectedBlockHistoryType('');
    setBlockHistory([]);
    setBlockHistoryBusy(false);
  }, [editorScopeKey]);

  useEffect(() => {
    let isCurrent = true;
    loadBlocklyBlocks().catch(error => {
      if (isCurrent) {
        onMessage(`Blockly block list failed: ${String(error)}`);
      }
    });

    return () => {
      isCurrent = false;
    };
  }, [loadBlocklyBlocks, onMessage]);

  useEffect(() => {
    loadProjectTopology().catch(error => onMessage(`Project topology failed: ${String(error)}`));
  }, [loadProjectTopology, onMessage]);

  useEffect(() => {
    if (customBlocks.length === 0) {
      setSelectedBlockHistoryType('');
      setBlockHistory([]);
      return;
    }

    setSelectedBlockHistoryType(current => customBlocks.some(block => block.blockType === current)
      ? current
      : customBlocks[0].blockType);
  }, [customBlocks]);

  useEffect(() => {
    if (!isBackendHealthy || !selectedBlockHistoryType || !projectApplicationApiScope) {
      setBlockHistory([]);
      setBlockHistoryBusy(false);
      return;
    }

    let isCurrent = true;
    const requestedScopeKey = editorScopeKey;
    setBlockHistoryBusy(true);
    listProcessBlocklyBlockVersions(selectedBlockHistoryType, projectApplicationApiScope)
      .then(rows => {
        if (isCurrent && blockScopeKeyRef.current === requestedScopeKey) {
          setBlockHistory(rows);
        }
      })
      .catch(error => {
        if (isCurrent && blockScopeKeyRef.current === requestedScopeKey) {
          onMessage(`Blockly block history failed: ${String(error)}`);
        }
      })
      .finally(() => {
        if (isCurrent && blockScopeKeyRef.current === requestedScopeKey) {
          setBlockHistoryBusy(false);
        }
      });

    return () => {
      isCurrent = false;
    };
  }, [
    editorScopeKey,
    isBackendHealthy,
    onMessage,
    projectApplicationApiScope,
    selectedBlockHistoryType
  ]);

  const mutateDraft = useCallback((updater: (current: ProcessDraft) => ProcessDraft) => {
    setDraft(updater);
    setSelectedDefinition(null);
    setValidationReport(null);
  }, []);

  const resetDraft = useCallback(() => {
    setDraft(createDraft());
    setSelectedDefinition(null);
    setEditingDefinitionId(null);
    setLoadedDefinitionStatus(null);
    setValidationReport(null);
  }, [blockCatalog]);

  const saveDraft = useCallback(async () => {
    if (!projectApplicationApiScope) {
      onMessage('Open a project Application before saving a process.');
      return;
    }

    if (isLoadedDefinitionReadOnly) {
      onMessage('Published process definitions cannot be overwritten. Create a new draft to continue.');
      return;
    }

    setBusy(true);
    setValidationReport(null);

    try {
      const request = toCreateRequest(draft);
      const response = editingDefinitionId
        ? await updateProcessDefinition(editingDefinitionId, request, projectApplicationApiScope)
        : await createProcessDefinition(request, projectApplicationApiScope);
      if (!response.ok || !response.body) {
        onMessage(`Process save failed: ${response.status} ${response.text}`);
        return;
      }

      setSelectedDefinition(response.body);
      setEditingDefinitionId(response.body.processDefinitionId);
      setLoadedDefinitionStatus(response.body.status);
      onMessage(`Saved ${response.body.processDefinitionId}`);
      await loadDefinitions();
    } finally {
      setBusy(false);
    }
  }, [
    blockCatalog,
    draft,
    editingDefinitionId,
    isLoadedDefinitionReadOnly,
    loadDefinitions,
    onMessage,
    projectApplicationApiScope
  ]);

  const validateSelected = useCallback(async () => {
    if (!selectedDefinition || !projectApplicationApiScope) {
      return;
    }

    const report = await validateProcessDefinition(
      selectedDefinition.processDefinitionId,
      projectApplicationApiScope);
    setValidationReport(report);
    onMessage(report?.isValid ? 'Process graph is valid' : 'Process graph has validation issues');
  }, [onMessage, projectApplicationApiScope, selectedDefinition]);

  const publishSelected = useCallback(async () => {
    if (!selectedDefinition || !projectApplicationApiScope) {
      return;
    }

    setBusy(true);
    try {
      const response = await publishProcessDefinition(
        selectedDefinition.processDefinitionId,
        projectApplicationApiScope);
      if (!response.ok || !response.body) {
        onMessage(`Publish failed: ${response.status} ${response.text}`);
        return;
      }

      setSelectedDefinition(response.body);
      setEditingDefinitionId(response.body.processDefinitionId);
      setLoadedDefinitionStatus(response.body.status);
      setDraft(fromProcessDefinition(response.body));
      onMessage(`Published ${response.body.processDefinitionId}`);
      await loadDefinitions();
    } finally {
      setBusy(false);
    }
  }, [blockCatalog, loadDefinitions, onMessage, projectApplicationApiScope, selectedDefinition]);

  const loadExisting = useCallback(async (summary: ProcessDefinitionSummary) => {
    if (!projectApplicationApiScope) {
      return;
    }

    setBusy(true);
    setValidationReport(null);
    try {
      const response = await getProcessDefinition(summary.processDefinitionId, projectApplicationApiScope);
      if (!response.ok || !response.body) {
        onMessage(`Process load failed: ${response.status} ${response.text}`);
        return;
      }

      setSelectedDefinition(response.body);
      setEditingDefinitionId(response.body.processDefinitionId);
      setLoadedDefinitionStatus(response.body.status);
      setDraft(fromProcessDefinition(response.body));
      onMessage(`${response.body.processDefinitionId} loaded`);
    } finally {
      setBusy(false);
    }
  }, [blockCatalog, onMessage, projectApplicationApiScope]);

  const updateDraftField = useCallback(<TKey extends keyof ProcessDraft>(
    key: TKey,
    value: ProcessDraft[TKey]
  ) => {
    mutateDraft(current => ({ ...current, [key]: value }));
  }, [mutateDraft]);

  const selectNode = useCallback((nodeId: string) => {
    setDraft(current => ({ ...current, selectedNodeId: nodeId }));
  }, []);

  const addNode = useCallback((kind: AddableProcessNodeKind) => {
    mutateDraft(current => {
      const newNode = createNode(kind, createUniqueNodeId(kind, current.nodes));
      const fromNode = findInsertionSource(current) ?? current.nodes[0];
      const transitionPatch = createInsertionTransitions(current, fromNode?.nodeId ?? 'start', newNode);

      return {
        ...current,
        selectedNodeId: newNode.nodeId,
        nodes: [...current.nodes, newNode],
        transitions: transitionPatch
      };
    });
  }, [blockCatalog, mutateDraft]);

  const removeSelectedNode = useCallback(() => {
    if (!selectedNode || selectedNode.kind === 'Start') {
      return;
    }

    mutateDraft(current => removeNode(current, selectedNode.nodeId));
  }, [mutateDraft, selectedNode]);

  const updateSelectedNode = useCallback((
    updater: (node: ProcessNodeDraft) => ProcessNodeDraft
  ) => {
    if (!selectedNode) {
      return;
    }

    mutateDraft(current => ({
      ...current,
      nodes: current.nodes.map(node => node.nodeId === selectedNode.nodeId
        ? updater(node)
        : node)
    }));
  }, [mutateDraft, selectedNode]);

  const applyProjectTarget = useCallback((target: ProjectTargetOption) => {
    updateSelectedNode(current => applyProjectTargetToNode(current, target));
    onMessage(`Project target applied ${target.label}`);
  }, [onMessage, updateSelectedNode]);

  const updateTransition = useCallback((
    transitionId: string,
    updater: (transition: ProcessTransitionDraft) => ProcessTransitionDraft
  ) => {
    mutateDraft(current => ({
      ...current,
      transitions: current.transitions.map(transition => transition.transitionId === transitionId
        ? updater(transition)
        : transition)
    }));
  }, [mutateDraft]);

  const addTransition = useCallback(() => {
    mutateDraft(current => {
      const fromNode = current.nodes.find(node => node.nodeId === current.selectedNodeId)
        ?? current.nodes[0];
      const targetNode = current.nodes.find(node => node.kind === 'End' && node.nodeId !== fromNode?.nodeId)
        ?? current.nodes.find(node => node.nodeId !== fromNode?.nodeId);
      if (!fromNode || !targetNode) {
        return current;
      }

      const transitionId = createUniqueTransitionId(fromNode.nodeId, targetNode.nodeId, current.transitions);
      return {
        ...current,
        transitions: [
          ...current.transitions,
          {
            transitionId,
            fromNodeId: fromNode.nodeId,
            toNodeId: targetNode.nodeId,
            label: '',
            loopPolicy: 'None',
            maxTraversals: 1
          }
        ]
      };
    });
  }, [mutateDraft]);

  const registerCustomBlocklyBlock = useCallback(async () => {
    if (!isBackendHealthy || !projectApplicationApiScope) {
      onMessage('Backend is required to register Blockly blocks');
      return;
    }

    let blocklyJson: Record<string, unknown>;
    let runtimeActionContract: Record<string, unknown>;
    try {
      const parsed = JSON.parse(customBlockDraft.blocklyJsonText) as unknown;
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        onMessage('Blockly JSON must be an object');
        return;
      }

      blocklyJson = parsed as Record<string, unknown>;
    } catch (error) {
      onMessage(`Blockly JSON parse failed: ${String(error)}`);
      return;
    }

    try {
      const parsed = JSON.parse(customBlockDraft.runtimeActionContractText) as unknown;
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        onMessage('Runtime Action Contract must be a JSON object');
        return;
      }

      runtimeActionContract = parsed as Record<string, unknown>;
    } catch (error) {
      onMessage(`Runtime Action Contract parse failed: ${String(error)}`);
      return;
    }

    const request: RegisterProcessBlocklyBlockDefinitionRequest = {
      blockType: customBlockDraft.blockType,
      category: customBlockDraft.category,
      displayName: customBlockDraft.displayName,
      blocklyJson,
      runtimeActionContractSchemaVersion: 'openlineops.runtime-action-contract',
      runtimeActionContract
    };

    const requestedScopeKey = editorScopeKey;
    setBusy(true);
    try {
      const response = await registerProcessBlocklyBlock(request, projectApplicationApiScope);
      if (!response.ok || !response.body) {
        onMessage(`Blockly block registration failed: ${response.status} ${response.text}`);
        return;
      }
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }

      setBlockCatalog(current => upsertBlocklyBlock(current, response.body!));
      await loadBlocklyBlocks();
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }
      setSelectedBlockHistoryType(response.body.blockType);
      const versions = await listProcessBlocklyBlockVersions(
        response.body.blockType,
        projectApplicationApiScope);
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }
      setBlockHistory(versions);
      setCustomBlockDraft(createCustomBlocklyBlockDraft());
      onMessage(`Registered Blockly block ${response.body.blockType} revision ${response.body.version}`);
    } finally {
      setBusy(false);
    }
  }, [
    customBlockDraft,
    editorScopeKey,
    isBackendHealthy,
    loadBlocklyBlocks,
    onMessage,
    projectApplicationApiScope
  ]);

  const restoreBlocklyBlockVersion = useCallback(async (
    block: ProcessBlocklyBlockDefinition
  ) => {
    if (!isBackendHealthy || !projectApplicationApiScope) {
      onMessage('Backend is required to restore Blockly blocks');
      return;
    }

    const requestedScopeKey = editorScopeKey;
    setBusy(true);
    try {
      const response = await registerProcessBlocklyBlock({
        blockType: block.blockType,
        category: block.category,
        displayName: block.displayName,
        blocklyJson: block.blocklyJson,
        runtimeActionContractSchemaVersion: block.runtimeActionContractSchemaVersion,
        runtimeActionContract: block.runtimeActionContract
      }, projectApplicationApiScope);
      if (!response.ok || !response.body) {
        onMessage(`Blockly block restore failed: ${response.status} ${response.text}`);
        return;
      }
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }

      setBlockCatalog(current => upsertBlocklyBlock(current, response.body!));
      await loadBlocklyBlocks();
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }
      setSelectedBlockHistoryType(response.body.blockType);
      const versions = await listProcessBlocklyBlockVersions(
        response.body.blockType,
        projectApplicationApiScope);
      if (blockScopeKeyRef.current !== requestedScopeKey) {
        return;
      }
      setBlockHistory(versions);
      onMessage(`Restored ${block.blockType} revision ${block.version} as revision ${response.body.version}`);
    } finally {
      setBusy(false);
    }
  }, [
    editorScopeKey,
    isBackendHealthy,
    loadBlocklyBlocks,
    onMessage,
    projectApplicationApiScope
  ]);

  const insertBlocklyBlock = useCallback((block: ProcessBlocklyBlockDefinition) => {
    if (selectedNode?.kind !== 'Blockly') {
      onMessage('Select a Blockly node before inserting an automation block');
      return;
    }

    try {
      const workspaceJson = appendBlocklyBlock(
        selectedNode.blocklyWorkspaceJson,
        block,
        projectTargets);
      updateSelectedNode(current => ({ ...current, blocklyWorkspaceJson: workspaceJson }));
      onMessage(`Blockly block inserted ${block.displayName}`);
    } catch (error) {
      onMessage(`Blockly block insert failed: ${String(error)}`);
    }
  }, [
    onMessage,
    projectTargets,
    selectedNode?.blocklyWorkspaceJson,
    selectedNode?.kind,
    updateSelectedNode
  ]);

  const removeTransition = useCallback((transitionId: string) => {
    mutateDraft(current => ({
      ...current,
      transitions: current.transitions.filter(transition => transition.transitionId !== transitionId)
    }));
  }, [mutateDraft]);

  return (
    <section className="process-workbench">
      <div className="panel process-editor-panel">
        <div className="panel-title">
          <div>
            <Workflow size={17} />
            <h2>Process Builder</h2>
          </div>
          <span>{selectedNode?.kind ?? 'Graph'}</span>
        </div>

        <div className="process-toolbar">
          <button type="button" className="button ghost" onClick={resetDraft} title="New draft">
            <FilePlus2 size={16} />
            New
          </button>
          <button
            type="button"
            className="button primary"
            onClick={saveDraft}
            disabled={!isBackendHealthy || busy || isLoadedDefinitionReadOnly}
            title={isLoadedDefinitionReadOnly
              ? 'Published definitions are read-only. Create a new draft to continue.'
              : 'Save process definition'}
            data-testid="save-process-definition"
          >
            <Save size={16} />
            Save Draft
          </button>
          <button
            type="button"
            className="button"
            onClick={validateSelected}
            disabled={!selectedDefinition || busy}
            title="Validate graph"
          >
            <ListChecks size={16} />
            Validate
          </button>
          <button
            type="button"
            className="button"
            onClick={publishSelected}
            disabled={!selectedDefinition || selectedDefinition.status === 'Published' || busy}
            title="Publish process"
            data-testid="publish-process-definition"
          >
            <Play size={16} />
            Publish
          </button>
        </div>

        <div className="process-layout">
          <aside className="process-form">
            <GraphMetadataEditor
              draft={draft}
              definitionIdDisabled={editingDefinitionId !== null}
              onChange={updateDraftField}
            />
            <NodeToolbox onAddNode={addNode} />
            <NodeInspector
              node={selectedNode}
              nodes={draft.nodes}
              transitions={draft.transitions}
              onSelectNode={selectNode}
              onRemoveNode={removeSelectedNode}
              onUpdateNode={updateSelectedNode}
            />
          </aside>

          <section className="process-stage">
            <details className="process-overview" open>
              <summary>
                <div>
                  <Workflow size={15} />
                  <strong>Flow Overview</strong>
                </div>
                <span>{draft.nodes.length} nodes / {draft.transitions.length} transitions</span>
              </summary>
              <div className="process-overview-content">
                <ProcessGraphCanvas
                  nodes={orderedNodes}
                  transitions={draft.transitions}
                  selectedNodeId={draft.selectedNodeId}
                  onSelectNode={selectNode}
                />
                <TransitionEditor
                  nodes={draft.nodes}
                  transitions={draft.transitions}
                  onAddTransition={addTransition}
                  onRemoveTransition={removeTransition}
                  onUpdateTransition={updateTransition}
                />
              </div>
            </details>

            <NodeActionEditor
              node={selectedNode}
              transitions={draft.transitions}
              blockCatalog={blockCatalog}
              projectTargets={projectTargets}
              onUpdateNode={updateSelectedNode}
            />

            <div className="process-diagnostics">
              <div className="flow-ir-note">
                <Braces size={15} />
                <div>
                  <strong>{selectedNode?.kind === 'PythonScript' ? 'Frozen Python Action' : 'Direct Flow IR'}</strong>
                  <span>{selectedNode?.kind === 'PythonScript'
                    ? 'The advanced Python source is frozen into the published process release.'
                    : 'Visual actions compile directly into immutable Flow IR when published.'}</span>
                </div>
              </div>
              {selectedNode?.kind === 'Blockly' ? (
                <details className="workspace-preview">
                  <summary>
                    <strong>Workspace JSON</strong>
                    <span>{previewWorkspaceJson.length} bytes / diagnostics</span>
                  </summary>
                  <code>{previewWorkspaceJson}</code>
                </details>
              ) : null}
            </div>
          </section>
        </div>
      </div>

      <div className="panel process-side-panel">
        <div className="panel-title">
          <div>
            <GitBranch size={17} />
            <h2>Definitions</h2>
          </div>
          <span>{definitions.length} saved</span>
        </div>
        <div className="definition-list">
          {definitions.length === 0 ? (
            <div className="empty-state">No process definitions saved</div>
          ) : definitions.map(definition => (
            <button
              type="button"
              key={definition.processDefinitionId}
              className="definition-row"
              data-testid={`process-definition-${definition.processDefinitionId}`}
              onClick={() => {
                void loadExisting(definition);
              }}
            >
              <span>{definition.displayName}</span>
              <strong>{definition.status}</strong>
              <small>{definition.processDefinitionId}</small>
            </button>
          ))}
        </div>

        <div className="validation-box">
          <div>
            <CheckCircle2 size={16} />
            <strong>{selectedDefinition?.status ?? 'Unsaved graph'}</strong>
          </div>
          <p>{selectedDefinition?.processDefinitionId ?? 'Save the graph to validate or publish.'}</p>
          {validationReport ? (
            <ul>
              {validationReport.issues.length === 0 ? (
                <li>Graph validation passed.</li>
              ) : validationReport.issues.map(issue => (
                <li key={`${issue.code}-${issue.message}`}>{issue.code}: {issue.message}</li>
              ))}
            </ul>
          ) : null}
        </div>

        <ProjectTargetPanel
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          topology={projectTopology}
          targets={projectTargets}
          selectedNode={selectedNode}
          busy={busy}
          isBackendHealthy={isBackendHealthy}
          onRefresh={loadProjectTopology}
          onApplyTarget={applyProjectTarget}
        />

        <ProcessBlocklyBlockCatalogPanel
          blockCatalog={blockCatalog}
          customBlocks={customBlocks}
          draft={customBlockDraft}
          selectedBlockHistoryType={selectedBlockHistoryType}
          blockHistory={blockHistory}
          blockHistoryBusy={blockHistoryBusy}
          busy={busy}
          isBackendHealthy={isBackendHealthy}
          onChange={setCustomBlockDraft}
          onSelectBlockHistory={setSelectedBlockHistoryType}
          onRegister={registerCustomBlocklyBlock}
          onRestoreVersion={restoreBlocklyBlockVersion}
          onInsert={insertBlocklyBlock}
        />

      </div>
    </section>
  );
}

function GraphMetadataEditor({
  draft,
  definitionIdDisabled,
  onChange
}: {
  draft: ProcessDraft;
  definitionIdDisabled: boolean;
  onChange<TKey extends keyof ProcessDraft>(key: TKey, value: ProcessDraft[TKey]): void;
}): React.ReactElement {
  return (
    <div className="process-meta-grid">
      <label>
        <span>Definition ID</span>
        <input
          value={draft.processDefinitionId}
          disabled={definitionIdDisabled}
          onChange={event => onChange('processDefinitionId', event.target.value)}
          data-testid="process-definition-id"
        />
      </label>
      <label>
        <span>Version ID</span>
        <input
          value={draft.versionId}
          onChange={event => onChange('versionId', event.target.value)}
          data-testid="process-version-id"
        />
      </label>
      <label>
        <span>Display Name</span>
        <input
          value={draft.displayName}
          onChange={event => onChange('displayName', event.target.value)}
          data-testid="process-display-name"
        />
      </label>
    </div>
  );
}

function NodeToolbox({
  onAddNode
}: {
  onAddNode(kind: AddableProcessNodeKind): void;
}): React.ReactElement {
  return (
    <div className="node-toolbox">
      {addableNodeKinds.map(kind => (
        <button
          type="button"
          key={kind}
          onClick={() => onAddNode(kind)}
          data-testid={`add-${kind.toLowerCase()}-node`}
        >
          <Plus size={14} />
          {kind}
        </button>
      ))}
    </div>
  );
}

function NodeInspector({
  node,
  nodes,
  transitions,
  onSelectNode,
  onRemoveNode,
  onUpdateNode
}: {
  node: ProcessNodeDraft | null;
  nodes: ProcessNodeDraft[];
  transitions: ProcessTransitionDraft[];
  onSelectNode(nodeId: string): void;
  onRemoveNode(): void;
  onUpdateNode(updater: (node: ProcessNodeDraft) => ProcessNodeDraft): void;
}): React.ReactElement {
  if (!node) {
    return <div className="empty-state">No node selected</div>;
  }

  return (
    <div className="node-inspector">
      <div className="inspector-header">
        <div>
          <strong>Node Inspector</strong>
          <span>{node.kind}</span>
        </div>
        <button
          type="button"
          className="icon-button danger"
          onClick={onRemoveNode}
          disabled={node.kind === 'Start'}
          title="Remove selected node"
        >
          <Trash2 size={15} />
        </button>
      </div>

      <div className="node-list" role="list" aria-label="Process nodes">
        {nodes.map(item => (
          <button
            type="button"
            key={item.nodeId}
            className={item.nodeId === node.nodeId ? 'selected' : ''}
            onClick={() => onSelectNode(item.nodeId)}
            role="listitem"
          >
            <span>{item.kind}</span>
            <strong>{item.displayName}</strong>
            <small>{item.nodeId}</small>
          </button>
        ))}
      </div>

      <div className="node-identity-grid">
        <label>
          <span>Node ID</span>
          <input value={node.nodeId} readOnly />
        </label>

        <label>
          <span>Display Name</span>
          <input
            value={node.displayName}
            onChange={event => onUpdateNode(current => ({
              ...current,
              displayName: event.target.value
            }))}
          />
        </label>
      </div>

      <div className="node-degree-list">
        <span>Transitions</span>
        <strong>{transitions.filter(transition => transition.fromNodeId === node.nodeId).length} out</strong>
        <strong>{transitions.filter(transition => transition.toNodeId === node.nodeId).length} in</strong>
      </div>
    </div>
  );
}

function NodeActionEditor({
  node,
  transitions,
  blockCatalog,
  projectTargets,
  onUpdateNode
}: {
  node: ProcessNodeDraft | null;
  transitions: ProcessTransitionDraft[];
  blockCatalog: ProcessBlocklyBlockDefinition[];
  projectTargets: ProjectTargetOption[];
  onUpdateNode(updater: (node: ProcessNodeDraft) => ProcessNodeDraft): void;
}): React.ReactElement {
  if (!node) {
    return <div className="node-action-editor empty-state">Select a node to edit its action.</div>;
  }

  const editorHeader = (
    <div className="node-action-header">
      <div>
        <span>Active Action</span>
        <strong>{node.displayName}</strong>
      </div>
      <span className="node-kind-chip">{node.kind}</span>
    </div>
  );

  if (node.kind === 'Command') {
    const commandTargets = projectTargets.filter(target => target.targetKind === node.targetKind);
    const selectedTarget = commandTargets.find(target => target.targetId === node.targetId);
    const capabilityOptions = selectedTarget?.capabilities ?? [];
    const targetIsMissing = node.targetId.length > 0 && !selectedTarget;
    const capabilityIsMissing = node.requiredCapability.length > 0
      && !capabilityOptions.some(capability => capability.capabilityId === node.requiredCapability);

    return (
      <section className="node-action-editor" data-testid="node-action-editor">
        {editorHeader}
        <div className="action-editor-form command-action-form">
          <label>
            <span>Target Kind</span>
            <select
              value={node.targetKind}
              data-testid="process-node-target-kind"
              onChange={event => onUpdateNode(current => ({
                ...selectCommandTargetKind(current, event.target.value as ProcessTargetKind, projectTargets)
              }))}
            >
              {processTargetKinds.map(kind => <option key={kind} value={kind}>{kind}</option>)}
            </select>
          </label>
          <label>
            <span>Target</span>
            <select
              value={node.targetId}
              data-testid="process-node-target-id"
              onChange={event => onUpdateNode(current =>
                selectCommandTargetId(current, event.target.value, projectTargets))}
            >
              <option value="">Select target</option>
              {targetIsMissing ? <option value={node.targetId}>Missing: {node.targetId}</option> : null}
              {commandTargets.map(target => (
                <option key={target.targetId} value={target.targetId}>{target.label}</option>
              ))}
            </select>
          </label>
          <label>
            <span>Required Capability</span>
            <select
              value={node.requiredCapability}
              data-testid="process-node-required-capability"
              onChange={event => onUpdateNode(current =>
                selectCommandCapability(current, event.target.value, projectTargets))}
            >
              <option value="">Select capability</option>
              {capabilityIsMissing
                ? <option value={node.requiredCapability}>Missing: {node.requiredCapability}</option>
                : null}
              {capabilityOptions.map(capability => (
                <option key={capability.capabilityId} value={capability.capabilityId}>
                  {capability.commandName} · {capability.capabilityId}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Command Name</span>
            <input
              value={node.commandName}
              data-testid="process-node-command-name"
              onChange={event => onUpdateNode(current => ({
                ...current,
                commandName: event.target.value
              }))}
            />
          </label>
          <label>
            <span>Timeout Seconds</span>
            <input
              type="number"
              min="1"
              value={node.timeoutSeconds}
              onChange={event => onUpdateNode(current => ({
                ...current,
                timeoutSeconds: toPositiveInteger(event.target.value, current.timeoutSeconds)
              }))}
            />
          </label>
          <label className="action-editor-wide">
            <span>Input Payload</span>
            <input
              value={node.inputPayload}
              data-testid="process-node-input-payload"
              onChange={event => onUpdateNode(current => ({
                ...current,
                inputPayload: event.target.value
              }))}
            />
          </label>
        </div>
      </section>
    );
  }

  if (node.kind === 'Blockly') {
    return (
      <section className="node-action-editor blockly-action-editor" data-testid="node-action-editor">
        {editorHeader}
        <div className="action-editor-toolbar">
          <label>
            <span>Execution Timeout</span>
            <input
              type="number"
              min="1"
              value={node.timeoutSeconds}
              onChange={event => onUpdateNode(current => ({
                ...current,
                timeoutSeconds: toPositiveInteger(event.target.value, current.timeoutSeconds)
              }))}
            />
            <small>seconds</small>
          </label>
          <span>Drag blocks, bind topology targets, then publish.</span>
        </div>
        <BlocklyWorkspaceEditor
          workspaceJson={node.blocklyWorkspaceJson}
          blockCatalog={blockCatalog}
          projectTargets={projectTargets}
          onChange={change => onUpdateNode(current => ({
            ...current,
            blocklyWorkspaceJson: change.workspaceJson
          }))}
        />
      </section>
    );
  }

  if (node.kind === 'PythonScript') {
    return (
      <section className="node-action-editor python-action-editor" data-testid="node-action-editor">
        {editorHeader}
        <div className="action-editor-form python-action-settings">
          <label>
            <span>Timeout Seconds</span>
            <input
              type="number"
              min="1"
              value={node.timeoutSeconds}
              data-testid="python-timeout-seconds"
              onChange={event => onUpdateNode(current => ({
                ...current,
                timeoutSeconds: toPositiveInteger(event.target.value, current.timeoutSeconds)
              }))}
            />
          </label>
          <label>
            <span>Script Version</span>
            <input
              value={node.scriptVersion}
              onChange={event => onUpdateNode(current => ({
                ...current,
                scriptVersion: event.target.value
              }))}
            />
          </label>
          <label>
            <span>Input Payload</span>
            <input
              value={node.inputPayload}
              onChange={event => onUpdateNode(current => ({
                ...current,
                inputPayload: event.target.value
              }))}
            />
          </label>
        </div>
        <label className="source-editor">
          <span>Python Source</span>
          <textarea
            value={node.manualSourceCode}
            data-testid="python-source-editor"
            spellCheck={false}
            onChange={event => onUpdateNode(current => ({
              ...current,
              manualSourceCode: event.target.value
            }))}
          />
        </label>
      </section>
    );
  }

  const outgoingTransitions = transitions.filter(transition => transition.fromNodeId === node.nodeId);
  const isDecision = node.kind === 'Decision';
  const description = isDecision
    ? 'Decision branches are defined by labeled outgoing transitions. Open Transitions in Flow Overview to edit routing.'
    : node.kind === 'Delay'
      ? 'This routing marker preserves graph order. Timed waits belong in the Blockly workspace as explicit Wait actions.'
      : node.kind === 'Start'
        ? 'The process enters this node once, then follows its configured outgoing transition.'
        : 'The process completes when execution reaches this terminal node.';

  return (
    <section className="node-action-editor routing-action-editor" data-testid="node-action-editor">
      {editorHeader}
      <div className="routing-action-body">
        <div className="routing-action-copy">
          <span>{isDecision ? 'Branch Routing' : `${node.kind} Semantics`}</span>
          <strong>{isDecision ? 'Labels select the next route' : 'Explicit graph control'}</strong>
          <p>{description}</p>
        </div>
        <div className="routing-edge-list">
          <span>Outgoing routes</span>
          {outgoingTransitions.length > 0 ? outgoingTransitions.map(transition => (
            <div key={transition.transitionId}>
              <strong>{transition.label || 'default'}</strong>
              <span>{'->'} {transition.toNodeId}</span>
              <small>{transition.loopPolicy === 'Counted'
                ? `counted / ${transition.maxTraversals}`
                : 'single pass'}</small>
            </div>
          )) : (
            <small>No outgoing transition</small>
          )}
        </div>
      </div>
    </section>
  );
}

function BlocklyWorkspaceEditor({
  workspaceJson,
  blockCatalog,
  projectTargets,
  onChange
}: {
  workspaceJson: string;
  blockCatalog: ProcessBlocklyBlockDefinition[];
  projectTargets: ProjectTargetOption[];
  onChange(change: {
    workspaceJson: string;
  }): void;
}): React.ReactElement {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const workspaceRef = useRef<Blockly.WorkspaceSvg | null>(null);
  const lastAppliedJsonRef = useRef(workspaceJson);
  const onChangeRef = useRef(onChange);
  const toolbox = useMemo(() => createBlocklyToolbox(blockCatalog), [blockCatalog]);

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) {
      return;
    }

    registerProcessBlocklyBlocks(blockCatalog);
    const workspace = Blockly.inject(container, {
      toolbox,
      renderer: 'zelos',
      trashcan: false,
      move: {
        scrollbars: true,
        drag: true,
        wheel: false
      },
      zoom: {
        controls: true,
        wheel: true,
        startScale: 0.72,
        maxScale: 1.4,
        minScale: 0.55
      },
      grid: {
        spacing: 20,
        length: 3,
        colour: '#d1dde8',
        snap: true
      }
    });
    workspaceRef.current = workspace;
    projectTargetsByWorkspace.set(workspace, projectTargets);
    loadWorkspaceState(workspaceJson, workspace);
    Blockly.svgResize(workspace);

    const changeListener = (event: Blockly.Events.Abstract) => {
      if (event.isUiEvent) {
        return;
      }

      synchronizeTargetBindingFields(event, workspace);
      const nextJson = saveWorkspaceState(workspace);
      lastAppliedJsonRef.current = nextJson;
      onChangeRef.current({
        workspaceJson: nextJson
      });
    };
    workspace.addChangeListener(changeListener);

    const resizeObserver = new ResizeObserver(() => {
      Blockly.svgResize(workspace);
    });
    resizeObserver.observe(container);

    return () => {
      resizeObserver.disconnect();
      workspace.removeChangeListener(changeListener);
      workspace.dispose();
      workspaceRef.current = null;
    };
  }, [blockCatalog, projectTargets, toolbox]);

  useEffect(() => {
    const workspace = workspaceRef.current;
    if (!workspace || workspaceJson === lastAppliedJsonRef.current) {
      return;
    }

    loadWorkspaceState(workspaceJson, workspace);
    lastAppliedJsonRef.current = workspaceJson;
    Blockly.svgResize(workspace);
  }, [workspaceJson]);

  return (
    <div className="blockly-surface">
      <div ref={containerRef} className="blockly-workspace-host" data-testid="blockly-workspace" />
      <div className="blockly-summary">
        <strong>Automation Blocks</strong>
        <span>Targets bind to this Application topology and compile directly to Flow IR.</span>
      </div>
    </div>
  );
}

function ProjectTargetPanel({
  activeWorkspace,
  activeApplicationId,
  topology,
  targets,
  selectedNode,
  busy,
  isBackendHealthy,
  onRefresh,
  onApplyTarget
}: {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  topology: AutomationTopologyResponse | null;
  targets: ProjectTargetOption[];
  selectedNode: ProcessNodeDraft | null;
  busy: boolean;
  isBackendHealthy: boolean;
  onRefresh(): void;
  onApplyTarget(target: ProjectTargetOption): void;
}): React.ReactElement {
  const canApply = selectedNode?.kind === 'Command';

  return (
    <div className="project-target-panel" data-testid="project-target-panel">
      <div className="project-target-header">
        <div>
          <Network size={16} />
          <strong>Project Targets</strong>
        </div>
        <button
          type="button"
          className="button ghost"
          onClick={onRefresh}
          disabled={!isBackendHealthy || !activeApplicationId}
        >
          <RotateCcw size={14} />
          Refresh
        </button>
      </div>
      <div className="project-target-context">
        <span>{activeWorkspace?.project.displayName ?? 'No project open'}</span>
        <small>{activeApplicationId ?? 'No application selected'}</small>
        <small>{topology?.topologyId ?? 'No topology linked'}</small>
        {selectedNode?.kind === 'Blockly' ? (
          <small>Choose targets directly inside each Blockly action.</small>
        ) : null}
      </div>
      {targets.length === 0 ? (
        <div className="project-target-empty">No topology targets available.</div>
      ) : (
        <div className="project-target-list">
          {targets.slice(0, 8).map(target => (
            <button
              type="button"
              key={`${target.targetKind}-${target.targetId}`}
              className="project-target-row"
              onClick={() => onApplyTarget(target)}
              disabled={!canApply || busy}
              data-testid={`apply-project-target-${target.testId}`}
            >
              <MapPin size={14} />
              <span>
                <strong>{target.label}</strong>
                <small>{target.detail}</small>
              </span>
              <em>{target.capabilities.length === 1
                ? target.capabilities[0].capabilityId
                : `${target.capabilities.length} capabilities`}</em>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function ProcessBlocklyBlockCatalogPanel({
  blockCatalog,
  customBlocks,
  draft,
  selectedBlockHistoryType,
  blockHistory,
  blockHistoryBusy,
  busy,
  isBackendHealthy,
  onChange,
  onSelectBlockHistory,
  onRegister,
  onRestoreVersion,
  onInsert
}: {
  blockCatalog: ProcessBlocklyBlockDefinition[];
  customBlocks: ProcessBlocklyBlockDefinition[];
  draft: CustomBlocklyBlockDraft;
  selectedBlockHistoryType: string;
  blockHistory: ProcessBlocklyBlockDefinition[];
  blockHistoryBusy: boolean;
  busy: boolean;
  isBackendHealthy: boolean;
  onChange(updater: (current: CustomBlocklyBlockDraft) => CustomBlocklyBlockDraft): void;
  onSelectBlockHistory(blockType: string): void;
  onRegister(): void;
  onRestoreVersion(block: ProcessBlocklyBlockDefinition): void;
  onInsert(block: ProcessBlocklyBlockDefinition): void;
}): React.ReactElement {
  const latestVersion = blockHistory[0]?.version ?? 0;

  return (
    <div className="block-catalog-panel">
      <div className="panel-title compact">
        <div>
          <Braces size={16} />
          <h2>Block Catalog</h2>
        </div>
        <span>{blockCatalog.length} blocks</span>
      </div>
      <div className="block-catalog-list">
        {blockCatalog.slice(0, 7).map(block => (
          <button
            type="button"
            key={block.blockType}
            onClick={() => onInsert(block)}
            data-testid={`insert-block-${block.blockType}`}
            title={`Insert ${block.displayName}`}
          >
            <Plus size={12} />
            <span>{block.displayName}</span>
            <small>revision {block.version}</small>
          </button>
        ))}
        {blockCatalog.length > 7 ? <em>+{blockCatalog.length - 7} more</em> : null}
      </div>
      <div className="block-catalog-form">
        <input
          value={draft.blockType}
          onChange={event => onChange(current => ({
            ...current,
            blockType: event.target.value
          }))}
          aria-label="Block type"
        />
        <div className="two-column-fields">
          <input
            value={draft.category}
            onChange={event => onChange(current => ({
              ...current,
              category: event.target.value
            }))}
            aria-label="Block category"
          />
          <input
            value={draft.displayName}
            onChange={event => onChange(current => ({
              ...current,
              displayName: event.target.value
            }))}
            aria-label="Block display name"
          />
        </div>
        <textarea
          value={draft.blocklyJsonText}
          onChange={event => onChange(current => ({
            ...current,
            blocklyJsonText: event.target.value
          }))}
          aria-label="Blockly JSON"
        />
        <textarea
          value={draft.runtimeActionContractText}
          onChange={event => onChange(current => ({
            ...current,
            runtimeActionContractText: event.target.value
          }))}
          aria-label="Runtime Action Contract"
        />
        <button
          type="button"
          className="button"
          onClick={onRegister}
          disabled={!isBackendHealthy || busy}
          data-testid="register-blockly-block"
        >
          <Plus size={15} />
          Register Block
        </button>
      </div>
      <div className="block-version-browser">
        <div className="block-version-header">
          <div>
            <History size={14} />
            <strong>Version History</strong>
          </div>
          <span>{customBlocks.length} custom</span>
        </div>
        {customBlocks.length > 0 ? (
          <>
            <select
              value={selectedBlockHistoryType}
              onChange={event => onSelectBlockHistory(event.target.value)}
              disabled={!isBackendHealthy || blockHistoryBusy}
              aria-label="Custom Blockly block versions"
              data-testid="block-version-selector"
            >
              {customBlocks.map(block => (
                <option key={block.blockType} value={block.blockType}>
                  {block.displayName} revision {block.version}
                </option>
              ))}
            </select>
            <div className="block-version-list" data-testid="block-version-history">
              {blockHistory.length > 0 ? blockHistory.map(version => (
                <div className="block-version-row" key={`${version.blockType}-${version.version}`}>
                  <div>
                    <strong>revision {version.version}</strong>
                    <span>{formatCompactDateTime(version.updatedAtUtc)}</span>
                    <small>{version.displayName}</small>
                  </div>
                  <button
                    type="button"
                    className="button ghost"
                    onClick={() => onRestoreVersion(version)}
                    disabled={!isBackendHealthy || busy || blockHistoryBusy || version.version === latestVersion}
                    data-testid={`restore-blockly-block-revision-${version.version}`}
                    title={`Restore ${version.blockType} revision ${version.version}`}
                  >
                    <RotateCcw size={14} />
                    Restore
                  </button>
                </div>
              )) : (
                <small>{blockHistoryBusy ? 'Loading versions' : 'No versions'}</small>
              )}
            </div>
          </>
        ) : (
          <small>No custom blocks</small>
        )}
      </div>
    </div>
  );
}

function ProcessGraphCanvas({
  nodes,
  transitions,
  selectedNodeId,
  onSelectNode
}: {
  nodes: ProcessNodeDraft[];
  transitions: ProcessTransitionDraft[];
  selectedNodeId: string;
  onSelectNode(nodeId: string): void;
}): React.ReactElement {
  return (
    <div className="process-graph-canvas">
      <div className="process-graph-track">
        {nodes.map((node, index) => (
          <React.Fragment key={node.nodeId}>
            {index > 0 ? (
              <ArrowRight className="graph-arrow" size={18} />
            ) : null}
            <button
              type="button"
              className={node.nodeId === selectedNodeId
                ? 'process-graph-node selected'
                : 'process-graph-node'}
              onClick={() => onSelectNode(node.nodeId)}
              data-testid={`process-node-${node.nodeId}`}
            >
              <span>{node.kind}</span>
              <strong>{node.displayName}</strong>
              <small>{node.nodeId}</small>
            </button>
          </React.Fragment>
        ))}
      </div>
      <div className="transition-pills">
        {transitions.map(transition => (
          <span key={transition.transitionId}>
            {transition.fromNodeId} {'->'} {transition.toNodeId}
            {transition.label ? ` : ${transition.label}` : ''}
          </span>
        ))}
      </div>
    </div>
  );
}

function TransitionEditor({
  nodes,
  transitions,
  onAddTransition,
  onRemoveTransition,
  onUpdateTransition
}: {
  nodes: ProcessNodeDraft[];
  transitions: ProcessTransitionDraft[];
  onAddTransition(): void;
  onRemoveTransition(transitionId: string): void;
  onUpdateTransition(
    transitionId: string,
    updater: (transition: ProcessTransitionDraft) => ProcessTransitionDraft
  ): void;
}): React.ReactElement {
  return (
    <details className="transition-editor">
      <summary>
        <strong>Transitions</strong>
        <span>{transitions.length} routes / click to edit</span>
      </summary>
      <div className="transition-editor-header">
        <span>Routing table</span>
        <button
          type="button"
          className="button ghost"
          onClick={onAddTransition}
          data-testid="add-transition"
        >
          <Plus size={14} />
          Add
        </button>
      </div>
      <div className="transition-grid">
        {transitions.map(transition => (
          <div className="transition-row" key={transition.transitionId}>
            <input
              value={transition.transitionId}
              data-testid={`transition-id-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                transitionId: event.target.value
              }))}
            />
            <select
              value={transition.fromNodeId}
              data-testid={`transition-from-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                fromNodeId: event.target.value
              }))}
            >
              {nodes.map(node => (
                <option key={node.nodeId} value={node.nodeId}>{node.nodeId}</option>
              ))}
            </select>
            <select
              value={transition.toNodeId}
              data-testid={`transition-to-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                toNodeId: event.target.value
              }))}
            >
              {nodes.map(node => (
                <option key={node.nodeId} value={node.nodeId}>{node.nodeId}</option>
              ))}
            </select>
            <input
              value={transition.label}
              placeholder="label"
              data-testid={`transition-label-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                label: event.target.value
              }))}
            />
            <select
              value={transition.loopPolicy}
              data-testid={`transition-loop-policy-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                loopPolicy: parseTransitionLoopPolicy(event.target.value)
              }))}
            >
              <option value="None">None</option>
              <option value="Counted">Counted</option>
            </select>
            <input
              type="number"
              min={1}
              value={transition.maxTraversals}
              disabled={transition.loopPolicy !== 'Counted'}
              placeholder="max"
              data-testid={`transition-max-traversals-${transition.transitionId}`}
              onChange={event => onUpdateTransition(transition.transitionId, current => ({
                ...current,
                maxTraversals: Math.max(1, Number.parseInt(event.target.value, 10) || 1)
              }))}
            />
            <button
              type="button"
              className="icon-button danger"
              onClick={() => onRemoveTransition(transition.transitionId)}
              title="Remove transition"
            >
              <Trash2 size={14} />
            </button>
          </div>
        ))}
      </div>
    </details>
  );
}

function createProjectTargetOptions(
  workspace: AutomationProjectWorkspaceResponse | null,
  application: AutomationProjectWorkspaceResponse['project']['applications'][number] | null,
  topology: AutomationTopologyResponse | null
): ProjectTargetOption[] {
  if (!workspace || !application || !topology) {
    return [];
  }

  const capabilitiesById = new Map(topology.capabilities.map(capability => [capability.capabilityId, capability]));
  const systemsById = new Map(topology.systems.map(system => [system.systemId, system]));
  const capabilitiesForIds = (capabilityIds: string[]): ProjectTargetCapabilityOption[] =>
    [...new Set(capabilityIds)]
      .sort((left, right) => left.localeCompare(right))
      .flatMap(capabilityId => {
        const capability = capabilitiesById.get(capabilityId);
        if (!capability) {
          return [];
        }

        const binding = topology.driverBindings.find(candidate => candidate.capabilityId === capabilityId);
        return [{
          capabilityId,
          commandName: capability.commandName,
          providerKey: binding?.providerKey ?? ''
        }];
      });
  const createOption = (
    testId: string,
    targetKind: ProcessTargetKind,
    targetId: string,
    label: string,
    detail: string,
    capabilityIds: string[] = []
  ): ProjectTargetOption => {
    return {
      testId,
      targetKind,
      targetId,
      label,
      detail,
      capabilities: capabilitiesForIds(capabilityIds)
    };
  };

  const systemTargets = topology.systems.map((system, index) => createOption(
    `system-${index}`,
    'System',
    system.systemId,
    system.displayName,
    `${system.kind} · ${system.systemType}${system.parentSystemId ? ` under ${system.parentSystemId}` : ''}`,
    [...system.providedCapabilityIds, ...system.requiredCapabilityIds]));

  const slotGroupTargets = topology.slotGroups.map((group, index) => createOption(
    `slot-group-${index}`,
    'SlotGroup',
    group.slotGroupId,
    group.displayName,
    `${group.kind}, ${group.slotIds.length}/${group.capacity} slots`,
    capabilitiesForSystem(systemsById, group.parentSystemId)));

  const slotTargets = topology.slots.map((slot, index) => createOption(
    `slot-${index}`,
    'Slot',
    slot.slotId,
    slot.displayName,
    `${slot.address}, ${slot.materialKind}`,
    capabilitiesForSystem(systemsById, slot.parentSystemId)));

  const productionUnitTargets = topology.slots
    .filter(slot => slot.materialKind === 'ProductionUnit')
    .map((slot, index) => createOption(
      `production-unit-${index}`,
      'ProductionUnit',
      slot.slotId,
      slot.displayName,
      `${slot.address}, Production Unit`,
      capabilitiesForSystem(systemsById, slot.parentSystemId)));

  const capabilityTargets = topology.capabilities.map((capability, index) => createOption(
    `capability-${index}`,
    'Capability',
    capability.capabilityId,
    capability.commandName,
    `${capability.safetyClass}, ${capability.timeoutSeconds}s`,
    [capability.capabilityId]));

  const driverTargets = topology.driverBindings.map((binding, index) => createOption(
    `driver-${index}`,
    'Driver',
    binding.bindingId,
    binding.providerKey,
    `${binding.providerKind} for ${binding.capabilityId}`,
    [binding.capabilityId]));

  return [
    ...systemTargets,
    ...capabilityTargets,
    ...driverTargets,
    ...slotGroupTargets,
    ...slotTargets,
    ...productionUnitTargets
  ];
}

function capabilitiesForSystem(
  systemsById: Map<string, AutomationTopologyResponse['systems'][number]>,
  systemId: string
): string[] {
  const system = systemsById.get(systemId);
  return system
    ? [...system.providedCapabilityIds, ...system.requiredCapabilityIds]
    : [];
}

function applyProjectTargetToNode(
  node: ProcessNodeDraft,
  target: ProjectTargetOption
): ProcessNodeDraft {
  if (node.kind === 'Command') {
    const onlyCapability = target.capabilities.length === 1
      ? target.capabilities[0]
      : null;
    return {
      ...node,
      displayName: `Execute ${target.label}`,
      targetKind: target.targetKind,
      targetId: target.targetId,
      requiredCapability: onlyCapability?.capabilityId ?? '',
      commandName: onlyCapability?.commandName ?? ''
    };
  }

  return node;
}

function selectCommandTargetKind(
  node: ProcessNodeDraft,
  targetKind: ProcessTargetKind,
  projectTargets: ProjectTargetOption[]
): ProcessNodeDraft {
  const firstTarget = projectTargets.find(target => target.targetKind === targetKind);
  return firstTarget
    ? applyProjectTargetToNode({ ...node, targetKind }, firstTarget)
    : {
        ...node,
        targetKind,
        targetId: '',
        requiredCapability: '',
        commandName: ''
      };
}

function selectCommandTargetId(
  node: ProcessNodeDraft,
  targetId: string,
  projectTargets: ProjectTargetOption[]
): ProcessNodeDraft {
  const target = projectTargets.find(candidate =>
    candidate.targetKind === node.targetKind && candidate.targetId === targetId);
  return target
    ? applyProjectTargetToNode(node, target)
    : {
        ...node,
        targetId,
        requiredCapability: '',
        commandName: ''
      };
}

function selectCommandCapability(
  node: ProcessNodeDraft,
  capabilityId: string,
  projectTargets: ProjectTargetOption[]
): ProcessNodeDraft {
  const capability = projectTargets
    .find(target => target.targetKind === node.targetKind && target.targetId === node.targetId)
    ?.capabilities
    .find(candidate => candidate.capabilityId === capabilityId);
  return {
    ...node,
    requiredCapability: capabilityId,
    commandName: capability?.commandName ?? ''
  };
}

function createDraft(): ProcessDraft {
  const seed = Date.now().toString(36);
  const blocklyNode = createNode('Blockly', 'automation');

  return {
    processDefinitionId: `desktop-flow-${seed}`,
    versionId: `desktop-flow-${seed}@1.0.0`,
    displayName: 'Desktop Automation Flow',
    selectedNodeId: blocklyNode.nodeId,
    nodes: [
      createNode('Start', 'start'),
      blocklyNode,
      createNode('End', 'end')
    ],
    transitions: [
      {
        transitionId: 'start-to-automation',
        fromNodeId: 'start',
        toNodeId: 'automation',
        label: '',
        loopPolicy: 'None',
        maxTraversals: 1
      },
      {
        transitionId: 'automation-to-end',
        fromNodeId: 'automation',
        toNodeId: 'end',
        label: 'done',
        loopPolicy: 'None',
        maxTraversals: 1
      }
    ]
  };
}

function createCustomBlocklyBlockDraft(): CustomBlocklyBlockDraft {
  return {
    blockType: 'user_fixture_action',
    category: 'Fixture',
    displayName: 'Fixture Action',
    blocklyJsonText: JSON.stringify({
      type: 'user_fixture_action',
      message0: 'fixture target kind %1 target %2 action %3',
      args0: [
        {
          type: 'field_dropdown',
          name: 'TARGET_KIND',
          options: [
            ['System', 'System'],
            ['Slot group', 'SlotGroup'],
            ['Slot', 'Slot'],
            ['Production Unit', 'ProductionUnit'],
            ['Capability', 'Capability'],
            ['Driver', 'Driver']
          ]
        },
        {
          type: 'field_input',
          name: 'TARGET_ID',
          text: 'fixture.module'
        },
        {
          type: 'field_input',
          name: 'ACTION',
          text: 'open'
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 130,
      tooltip: 'Emit a target-bound fixture command through Flow IR.'
    }, null, 2),
    runtimeActionContractText: JSON.stringify({
      schemaVersion: 'openlineops.runtime-action-contract',
      actionType: 'fixture.action',
      fields: {
        ACTION: {
          type: 'string',
          required: true,
          maxLength: 64
        },
        TARGET_ID: {
          type: 'targetReference',
          required: true,
          maxLength: 256
        },
        TARGET_KIND: {
          type: 'string',
          required: true,
          enum: [
            'System',
            'SlotGroup',
            'Slot',
            'ProductionUnit',
            'Capability',
            'Driver'
          ],
          maxLength: 32
        }
      },
      emit: {
        kind: 'deviceCommand',
        targetKind: { source: 'field', name: 'TARGET_KIND' },
        targetId: { source: 'field', name: 'TARGET_ID' },
        capability: { source: 'literal', value: 'fixture.control' },
        commandName: { source: 'literal', value: 'FixtureAction' },
        input: {
          source: 'object',
          properties: {
            action: { source: 'field', name: 'ACTION' }
          }
        },
        timeoutMilliseconds: { source: 'literal', value: 30000 },
        retryLimit: 0
      }
    }, null, 2)
  };
}

function upsertBlocklyBlock(
  current: ProcessBlocklyBlockDefinition[],
  block: ProcessBlocklyBlockDefinition
): ProcessBlocklyBlockDefinition[] {
  const index = current.findIndex(candidate => candidate.blockType === block.blockType);
  if (index < 0) {
    return [...current, block];
  }

  return current.map(candidate => candidate.blockType === block.blockType
    ? block
    : candidate);
}

function formatCompactDateTime(value: string): string {
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime())
    ? value
    : `${timestamp.toISOString().slice(0, 16).replace('T', ' ')}Z`;
}

function fromProcessDefinition(definition: ProcessDefinitionResponse): ProcessDraft {
  const nodes = definition.nodes.map(fromNodeResponse);
  const selectedNodeId = nodes.find(node => node.kind === 'Blockly')?.nodeId
    ?? nodes.find(node => node.kind === 'PythonScript')?.nodeId
    ?? nodes[0]?.nodeId
    ?? '';

  return {
    processDefinitionId: definition.processDefinitionId,
    versionId: definition.versionId,
    displayName: definition.displayName,
    selectedNodeId,
    nodes,
    transitions: definition.transitions.map(transition => ({
      transitionId: transition.transitionId,
      fromNodeId: transition.fromNodeId,
      toNodeId: transition.toNodeId,
      label: transition.label ?? '',
      loopPolicy: parseTransitionLoopPolicy(transition.loopPolicy),
      maxTraversals: transition.maxTraversals ?? 1
    }))
  };
}

function fromNodeResponse(node: ProcessNodeResponse): ProcessNodeDraft {
  const kind = parseNodeKind(node.kind);
  const blocklyWorkspaceJson = normalizeBlocklyWorkspaceJson(node.blocklyWorkspaceJson);
  const manualSourceCode = node.scriptSourceCode ?? createDefaultManualPythonSource();
  const commandTarget = kind === 'Command'
    ? readCommandTarget(node)
    : { targetKind: 'Capability' as const, targetId: '' };

  return {
    nodeId: node.nodeId,
    kind,
    displayName: node.displayName,
    requiredCapability: node.requiredCapability ?? defaultRequiredCapability(kind),
    commandName: node.commandName ?? defaultCommandName(kind),
    targetKind: commandTarget.targetKind,
    targetId: commandTarget.targetId,
    timeoutSeconds: node.timeoutSeconds ?? defaultTimeoutSeconds(kind),
    inputPayload: node.inputPayload ?? defaultInputPayload(kind),
    scriptVersion: node.scriptVersion ?? '1',
    blocklyWorkspaceJson,
    manualSourceCode
  };
}

function createNode(
  kind: ProcessNodeKind,
  nodeId: string
): ProcessNodeDraft {
  const blocklyWorkspaceJson = createEmptyBlocklyWorkspaceJson();

  return {
    nodeId,
    kind,
    displayName: defaultDisplayName(kind),
    requiredCapability: defaultRequiredCapability(kind),
    commandName: defaultCommandName(kind),
    targetKind: defaultTargetKind(kind),
    targetId: defaultTargetId(kind),
    timeoutSeconds: defaultTimeoutSeconds(kind),
    inputPayload: defaultInputPayload(kind),
    scriptVersion: '1',
    blocklyWorkspaceJson,
    manualSourceCode: createDefaultManualPythonSource()
  };
}

function toCreateRequest(draft: ProcessDraft): CreateProcessDefinitionRequest {
  return {
    processDefinitionId: draft.processDefinitionId,
    versionId: draft.versionId,
    displayName: draft.displayName,
    nodes: draft.nodes.map(toCreateNodeRequest),
    transitions: draft.transitions.map(transition => ({
      transitionId: transition.transitionId,
      fromNodeId: transition.fromNodeId,
      toNodeId: transition.toNodeId,
      label: toOptionalString(transition.label),
      loopPolicy: transition.loopPolicy === 'Counted' ? transition.loopPolicy : null,
      maxTraversals: transition.loopPolicy === 'Counted'
        ? Math.max(1, Math.trunc(transition.maxTraversals))
        : null
    }))
  };
}

function toCreateNodeRequest(node: ProcessNodeDraft): CreateProcessNodeRequest {
  if (node.kind === 'Command') {
    return {
      ...baseNodeRequest(node),
      requiredCapability: toOptionalString(node.requiredCapability),
      commandName: toOptionalString(node.commandName),
      targetKind: node.targetKind,
      targetId: toOptionalString(node.targetId),
      timeoutSeconds: node.timeoutSeconds,
      inputPayload: toOptionalString(node.inputPayload)
    };
  }

  if (node.kind === 'Blockly') {
    return {
      ...baseNodeRequest(node),
      timeoutSeconds: node.timeoutSeconds,
      inputPayload: toOptionalString(node.inputPayload),
      blocklyWorkspaceJson: node.blocklyWorkspaceJson,
      scriptSourceCode: null,
      scriptVersion: null
    };
  }

  if (node.kind === 'PythonScript') {
    return {
      ...baseNodeRequest(node),
      timeoutSeconds: node.timeoutSeconds,
      inputPayload: toOptionalString(node.inputPayload),
      blocklyWorkspaceJson: null,
      scriptSourceCode: node.manualSourceCode,
      scriptVersion: toOptionalString(node.scriptVersion) ?? '1'
    };
  }

  return baseNodeRequest(node);
}

function baseNodeRequest(node: ProcessNodeDraft): CreateProcessNodeRequest {
  return {
    nodeId: node.nodeId,
    kind: node.kind,
    displayName: node.displayName,
    requiredCapability: null,
    commandName: null,
    targetKind: null,
    targetId: null,
    timeoutSeconds: null,
    inputPayload: null,
    blocklyWorkspaceJson: null,
    scriptSourceCode: null,
    scriptVersion: null
  };
}

function createDefaultManualPythonSource(): string {
  return [
    "result = {'status': 'ok'}",
    "result['message'] = 'manual Python action completed'"
  ].join('\n');
}

function createEmptyBlocklyWorkspaceJson(): string {
  return '{}';
}

function normalizeBlocklyWorkspaceJson(value: string | null): string {
  if (!value) {
    return createEmptyBlocklyWorkspaceJson();
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (!isRecord(parsed)) {
      throw new Error('Blockly workspace JSON must be an object');
    }
    return JSON.stringify(parsed);
  } catch (error) {
    throw new Error(`Blockly workspace JSON is invalid: ${String(error)}`);
  }
}

function appendBlocklyBlock(
  workspaceJson: string,
  definition: ProcessBlocklyBlockDefinition,
  projectTargets: ProjectTargetOption[]
): string {
  const workspace = JSON.parse(workspaceJson) as unknown;
  if (!isRecord(workspace)) {
    throw new Error('Workspace JSON must be an object');
  }

  let blocksContainer = workspace.blocks;
  if (!Object.hasOwn(workspace, 'blocks')) {
    blocksContainer = {
      languageVersion: 0,
      blocks: []
    };
    workspace.blocks = blocksContainer;
  } else if (!isRecord(blocksContainer)
      || blocksContainer.languageVersion !== 0
      || !Array.isArray(blocksContainer.blocks)) {
    throw new Error('Workspace must use the current Blockly serialization shape');
  }

  if (!isRecord(blocksContainer) || !Array.isArray(blocksContainer.blocks)) {
    throw new Error('Workspace must use the current Blockly serialization shape');
  }

  const args = Array.isArray(definition.blocklyJson.args0)
    ? definition.blocklyJson.args0
    : [];
  const fields: Record<string, string | number> = {};
  for (const argument of args) {
    if (!isRecord(argument) || typeof argument.name !== 'string') {
      continue;
    }

    const defaultValue = getBlocklyArgumentDefault(argument);
    if (defaultValue !== null) {
      fields[argument.name] = defaultValue;
    }
  }

  const targetKind = typeof fields.TARGET_KIND === 'string'
    ? fields.TARGET_KIND
    : '';
  const defaultTarget = projectTargets.find(target => target.targetKind === targetKind);
  if (defaultTarget) {
    fields.TARGET_ID = defaultTarget.targetId;
    const onlyCapability = defaultTarget.capabilities.length === 1
      ? defaultTarget.capabilities[0]
      : null;
    if (Object.hasOwn(fields, 'CAPABILITY')) {
      fields.CAPABILITY = onlyCapability?.capabilityId ?? '';
    }
    if (Object.hasOwn(fields, 'COMMAND')) {
      fields.COMMAND = onlyCapability?.commandName ?? '';
    }
  }

  if (blocksContainer.blocks.length > 1) {
    throw new Error('Connect the existing Blockly stacks before inserting another action');
  }

  const blockCount = blocksContainer.blocks.length === 0
    ? 0
    : countSerializedBlocks(blocksContainer.blocks[0]);
  const nextBlock = {
    id: `${definition.blockType}-${Date.now().toString(36)}-${blockCount + 1}`,
    type: definition.blockType,
    x: 32 + (blockCount % 4) * 24,
    y: 32 + blockCount * 48,
    fields
  };
  if (blocksContainer.blocks.length === 0) {
    blocksContainer.blocks.push(nextBlock);
  } else {
    const tail = findSerializedBlockTail(blocksContainer.blocks[0]);
    tail.next = { block: nextBlock };
  }
  return JSON.stringify(workspace);
}

function countSerializedBlocks(value: unknown): number {
  let count = 0;
  let current: unknown = value;
  while (isRecord(current)) {
    count += 1;
    current = isRecord(current.next) ? current.next.block : null;
  }
  return count;
}

function findSerializedBlockTail(value: unknown): Record<string, unknown> {
  if (!isRecord(value)) {
    throw new Error('Blockly stack contains an invalid block');
  }

  let current = value;
  while (isRecord(current.next) && isRecord(current.next.block)) {
    current = current.next.block;
  }
  return current;
}

function getBlocklyArgumentDefault(argument: Record<string, unknown>): string | number | null {
  switch (argument.type) {
    case 'field_input':
      return typeof argument.text === 'string' ? argument.text : '';
    case 'field_number':
      return typeof argument.value === 'number' ? argument.value : 0;
    case 'field_checkbox':
      return argument.checked === true ? 'TRUE' : 'FALSE';
    case 'field_dropdown': {
      const firstOption = Array.isArray(argument.options) ? argument.options[0] : null;
      return Array.isArray(firstOption) && typeof firstOption[1] === 'string'
        ? firstOption[1]
        : '';
    }
    default:
      return null;
  }
}

function saveWorkspaceState(workspace: Blockly.Workspace): string {
  const state = Blockly.serialization.workspaces.save(workspace);
  return JSON.stringify(state);
}

function loadWorkspaceState(
  workspaceJson: string,
  workspace: Blockly.Workspace | Blockly.WorkspaceSvg
): void {
  Blockly.Events.disable();
  try {
    Blockly.serialization.workspaces.load(JSON.parse(workspaceJson), workspace);
  } finally {
    Blockly.Events.enable();
  }
}

function registerProcessBlocklyBlocks(blockCatalog: ProcessBlocklyBlockDefinition[]): void {
  if (!Blockly.registry.hasItem(Blockly.registry.Type.FIELD, projectTargetFieldType)) {
    Blockly.fieldRegistry.register(projectTargetFieldType, ProjectTargetDropdown);
  }
  if (!Blockly.registry.hasItem(Blockly.registry.Type.FIELD, projectCapabilityFieldType)) {
    Blockly.fieldRegistry.register(projectCapabilityFieldType, ProjectCapabilityDropdown);
  }

  const blockRegistry = Blockly.Blocks as Record<string, unknown>;
  for (const blockType of registeredProcessBlockTypes) {
    delete blockRegistry[blockType];
  }
  registeredProcessBlockTypes.clear();

  const blockDefinitions = blockCatalog
    .map(block => prepareBlocklyBlockJson(block.blocklyJson))
    .filter(blockJson => {
      const type = blockJson.type;
      if (typeof type !== 'string') {
        return false;
      }

      registeredProcessBlockTypes.add(type);
      return true;
    });

  if (blockDefinitions.length > 0) {
    Blockly.common.defineBlocksWithJsonArray(
      blockDefinitions as Parameters<typeof Blockly.common.defineBlocksWithJsonArray>[0]);
  }

}

function prepareBlocklyBlockJson(blocklyJson: Record<string, unknown>): Record<string, unknown> {
  const cloned = JSON.parse(JSON.stringify(blocklyJson)) as Record<string, unknown>;
  if (!Array.isArray(cloned.args0)) {
    return cloned;
  }

  cloned.args0 = cloned.args0.map(argument => {
    if (!isRecord(argument)) {
      return argument;
    }

    if (argument.name === 'TARGET_ID') {
      return {
        ...argument,
        type: projectTargetFieldType
      };
    }

    if (argument.name === 'CAPABILITY') {
      return {
        ...argument,
        type: projectCapabilityFieldType
      };
    }

    return argument;
  });
  return cloned;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function synchronizeTargetBindingFields(
  event: Blockly.Events.Abstract,
  workspace: Blockly.Workspace
): void {
  const change = event as Blockly.Events.BlockChange;
  if (change.type !== Blockly.Events.BLOCK_CHANGE
    || change.element !== 'field'
    || (change.name !== 'TARGET_KIND' && change.name !== 'TARGET_ID' && change.name !== 'CAPABILITY')
    || !change.blockId) {
    return;
  }

  const block = workspace.getBlockById(change.blockId);
  if (!block) {
    return;
  }

  const targetKind = String(block.getFieldValue('TARGET_KIND') ?? '');
  const targets = projectTargetsByWorkspace.get(workspace) ?? [];
  const targetField = block.getField('TARGET_ID');
  if (change.name === 'TARGET_KIND' && targetField) {
    const firstTarget = targets.find(target => target.targetKind === targetKind);
    targetField.setValue(firstTarget?.targetId ?? '');
  }

  const targetId = String(block.getFieldValue('TARGET_ID') ?? '');
  const selectedTarget = targets.find(target =>
    target.targetKind === targetKind && target.targetId === targetId);
  if (!selectedTarget) {
    block.getField('CAPABILITY')?.setValue('');
    block.getField('COMMAND')?.setValue('');
    return;
  }

  if (change.name === 'CAPABILITY') {
    const capabilityId = String(block.getFieldValue('CAPABILITY') ?? '');
    const capability = selectedTarget.capabilities.find(candidate =>
      candidate.capabilityId === capabilityId);
    block.getField('COMMAND')?.setValue(capability?.commandName ?? '');
    return;
  }

  const onlyCapability = selectedTarget.capabilities.length === 1
    ? selectedTarget.capabilities[0]
    : null;
  block.getField('CAPABILITY')?.setValue(onlyCapability?.capabilityId ?? '');
  block.getField('COMMAND')?.setValue(onlyCapability?.commandName ?? '');
}

function createBlocklyToolbox(blockCatalog: ProcessBlocklyBlockDefinition[]): Blockly.utils.toolbox.ToolboxDefinition {
  const categoryOrder = ['Motion', 'I/O', 'Flow', 'Result'];
  const byCategory = new Map<string, ProcessBlocklyBlockDefinition[]>();
  for (const block of blockCatalog) {
    const rows = byCategory.get(block.category) ?? [];
    rows.push(block);
    byCategory.set(block.category, rows);
  }

  const categories = [...byCategory.entries()]
    .sort(([left], [right]) => {
      const leftIndex = categoryOrder.indexOf(left);
      const rightIndex = categoryOrder.indexOf(right);
      if (leftIndex >= 0 || rightIndex >= 0) {
        return (leftIndex >= 0 ? leftIndex : Number.MAX_SAFE_INTEGER)
          - (rightIndex >= 0 ? rightIndex : Number.MAX_SAFE_INTEGER);
      }

      return left.localeCompare(right);
    });

  return {
    kind: 'categoryToolbox',
    contents: categories.map(([category, blocks]) => ({
      kind: 'category',
      name: category,
      colour: blockCategoryColour(category),
      contents: blocks
        .sort((left, right) => left.displayName.localeCompare(right.displayName))
        .map(block => ({
          kind: 'block',
          type: block.blockType
        }))
    }))
  };
}

function blockCategoryColour(category: string): string {
  switch (category) {
    case 'Motion':
      return '#0f8f8a';
    case 'I/O':
      return '#256f9f';
    case 'Flow':
      return '#8a6f20';
    case 'Result':
      return '#0f8f8a';
    default:
      return '#5a6b7a';
  }
}

function findInsertionSource(draft: ProcessDraft): ProcessNodeDraft | null {
  const selected = draft.nodes.find(node => node.nodeId === draft.selectedNodeId);
  if (selected && selected.kind !== 'End') {
    return selected;
  }

  const selectedIncoming = draft.transitions.find(transition => transition.toNodeId === selected?.nodeId);
  return draft.nodes.find(node => node.nodeId === selectedIncoming?.fromNodeId)
    ?? draft.nodes.find(node => node.kind === 'Blockly')
    ?? draft.nodes.find(node => node.kind === 'PythonScript')
    ?? draft.nodes.find(node => node.kind === 'Start')
    ?? null;
}

function createInsertionTransitions(
  draft: ProcessDraft,
  fromNodeId: string,
  newNode: ProcessNodeDraft
): ProcessTransitionDraft[] {
  if (newNode.kind === 'End') {
    return [
      ...draft.transitions,
      {
        transitionId: createUniqueTransitionId(fromNodeId, newNode.nodeId, draft.transitions),
        fromNodeId,
        toNodeId: newNode.nodeId,
        label: 'terminal',
        loopPolicy: 'None',
        maxTraversals: 1
      }
    ];
  }

  const outgoing = draft.transitions.find(transition => transition.fromNodeId === fromNodeId);
  const nextNodeId = outgoing?.toNodeId
    ?? draft.nodes.find(node => node.kind === 'End')?.nodeId
    ?? '';
  const nextTransitions = outgoing
    ? draft.transitions.filter(transition => transition.transitionId !== outgoing.transitionId)
    : [...draft.transitions];

  nextTransitions.push({
    transitionId: createUniqueTransitionId(fromNodeId, newNode.nodeId, nextTransitions),
    fromNodeId,
    toNodeId: newNode.nodeId,
    label: outgoing?.label ?? '',
    loopPolicy: 'None',
    maxTraversals: 1
  });

  if (nextNodeId && nextNodeId !== newNode.nodeId) {
    nextTransitions.push({
      transitionId: createUniqueTransitionId(newNode.nodeId, nextNodeId, nextTransitions),
      fromNodeId: newNode.nodeId,
      toNodeId: nextNodeId,
      label: '',
      loopPolicy: outgoing?.loopPolicy ?? 'None',
      maxTraversals: outgoing?.maxTraversals ?? 1
    });
  }

  return nextTransitions;
}

function removeNode(draft: ProcessDraft, nodeId: string): ProcessDraft {
  const node = draft.nodes.find(candidate => candidate.nodeId === nodeId);
  if (!node || node.kind === 'Start') {
    return draft;
  }

  const incoming = draft.transitions.filter(transition => transition.toNodeId === nodeId);
  const outgoing = draft.transitions.filter(transition => transition.fromNodeId === nodeId);
  const remainingTransitions = draft.transitions.filter(transition =>
    transition.fromNodeId !== nodeId && transition.toNodeId !== nodeId);

  if (incoming.length === 1 && outgoing.length === 1 && incoming[0].fromNodeId !== outgoing[0].toNodeId) {
    remainingTransitions.push({
      transitionId: createUniqueTransitionId(
        incoming[0].fromNodeId,
        outgoing[0].toNodeId,
        remainingTransitions),
      fromNodeId: incoming[0].fromNodeId,
      toNodeId: outgoing[0].toNodeId,
      label: outgoing[0].label || incoming[0].label,
      loopPolicy: outgoing[0].loopPolicy,
      maxTraversals: outgoing[0].maxTraversals
    });
  }

  const nodes = draft.nodes.filter(candidate => candidate.nodeId !== nodeId);

  return {
    ...draft,
    selectedNodeId: nodes.find(candidate => candidate.kind === 'Blockly')?.nodeId
      ?? nodes.find(candidate => candidate.kind === 'PythonScript')?.nodeId
      ?? nodes[0]?.nodeId
      ?? '',
    nodes,
    transitions: remainingTransitions
  };
}

function orderNodesByTransitions(
  nodes: ProcessNodeDraft[],
  transitions: ProcessTransitionDraft[]
): ProcessNodeDraft[] {
  const byId = new Map(nodes.map(node => [node.nodeId, node]));
  const ordered: ProcessNodeDraft[] = [];
  const visited = new Set<string>();
  let current: ProcessNodeDraft | undefined = nodes.find(node => node.kind === 'Start') ?? nodes[0];

  while (current && !visited.has(current.nodeId)) {
    ordered.push(current);
    visited.add(current.nodeId);
    const nextTransition = transitions.find(transition => transition.fromNodeId === current?.nodeId);
    current = nextTransition ? byId.get(nextTransition.toNodeId) : undefined;
  }

  for (const node of nodes) {
    if (!visited.has(node.nodeId)) {
      ordered.push(node);
    }
  }

  return ordered;
}

function createUniqueNodeId(kind: ProcessNodeKind, nodes: ProcessNodeDraft[]): string {
  const baseId = kind.toLowerCase();
  let index = nodes.filter(node => node.kind === kind).length + 1;
  let candidate = `${baseId}-${index}`;
  const existingIds = new Set(nodes.map(node => node.nodeId));

  while (existingIds.has(candidate)) {
    index += 1;
    candidate = `${baseId}-${index}`;
  }

  return candidate;
}

function createUniqueTransitionId(
  fromNodeId: string,
  toNodeId: string,
  transitions: ProcessTransitionDraft[]
): string {
  const baseId = `${fromNodeId}-to-${toNodeId}`;
  let index = 1;
  let candidate = baseId;
  const existingIds = new Set(transitions.map(transition => transition.transitionId));

  while (existingIds.has(candidate)) {
    index += 1;
    candidate = `${baseId}-${index}`;
  }

  return candidate;
}

function parseNodeKind(value: string): ProcessNodeKind {
  return value === 'Start'
    || value === 'Command'
    || value === 'Decision'
    || value === 'Delay'
    || value === 'End'
    || value === 'Blockly'
    || value === 'PythonScript'
    ? value
      : 'Command';
}

function readCommandTarget(node: ProcessNodeResponse): {
  targetKind: ProcessTargetKind;
  targetId: string;
} {
  if (!processTargetKinds.some(kind => kind === node.targetKind)) {
    throw new Error(`Command node ${node.nodeId} has invalid target kind ${node.targetKind ?? '<missing>'}`);
  }

  if (!node.targetId || node.targetId.trim() !== node.targetId) {
    throw new Error(`Command node ${node.nodeId} has an invalid target id`);
  }

  return {
    targetKind: node.targetKind as ProcessTargetKind,
    targetId: node.targetId
  };
}

function parseTransitionLoopPolicy(value: string | null): TransitionLoopPolicy {
  return value === 'Counted' ? 'Counted' : 'None';
}

function defaultDisplayName(kind: ProcessNodeKind): string {
  switch (kind) {
    case 'Start':
      return 'Start';
    case 'Command':
      return 'Execute Device Command';
    case 'Decision':
      return 'Evaluate Decision';
    case 'Delay':
      return 'Delay';
    case 'End':
      return 'End';
    case 'Blockly':
      return 'Blockly Automation';
    case 'PythonScript':
      return 'Python Automation';
  }
}

function defaultRequiredCapability(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'device.loopback' : '';
}

function defaultCommandName(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'Echo' : '';
}

function defaultTargetKind(_kind: ProcessNodeKind): ProcessTargetKind {
  return 'Capability';
}

function defaultTargetId(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'device.loopback' : '';
}

function defaultTimeoutSeconds(kind: ProcessNodeKind): number {
  return kind === 'Command' || kind === 'Blockly' || kind === 'PythonScript' ? 15 : 1;
}

function defaultInputPayload(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'echo-ok' : 'scan-ok';
}

function toPositiveInteger(value: string, fallback: number): number {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function toOptionalString(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
