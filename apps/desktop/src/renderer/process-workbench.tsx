import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as Blockly from 'blockly';
import { pythonGenerator } from 'blockly/python';
import {
  ArrowRight,
  Braces,
  CheckCircle2,
  Code2,
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
  Sparkles,
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
  PublishedProjectSnapshotResponse,
  RegisterProcessBlocklyBlockDefinitionRequest,
  StartedProcessRuntimeSessionResponse,
  StartProcessRuntimeSessionRequest
} from './contracts';
import {
  createProcessDefinition,
  getAutomationTopology,
  getProcessDefinition,
  linkProjectProcessDefinition,
  listProcessBlocklyBlocks,
  listProcessBlocklyBlockVersions,
  listProcessDefinitions,
  publishProjectSnapshot,
  publishProcessDefinition,
  registerProcessBlocklyBlock,
  saveAutomationProjectManifest,
  startProjectSnapshotRuntimeSession,
  startProcessRuntimeSession,
  updateProcessDefinition,
  validateProcessDefinition
} from './api';

type ProcessNodeKind = 'Start' | 'Command' | 'Decision' | 'Delay' | 'End' | 'PythonScript';
type AddableProcessNodeKind = Exclude<ProcessNodeKind, 'Start'>;
type ScriptEditorMode = 'Blockly' | 'ManualCode';
type TransitionLoopPolicy = 'None' | 'Counted';

interface ProcessWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onWorkspaceChanged(workspace: AutomationProjectWorkspaceResponse): void;
  onMessage(message: string): void;
}

interface ProcessNodeDraft {
  nodeId: string;
  kind: ProcessNodeKind;
  displayName: string;
  requiredCapability: string;
  commandName: string;
  timeoutSeconds: number;
  inputPayload: string;
  scriptEditorMode: ScriptEditorMode;
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

interface RuntimeLaunchDraft {
  configurationSnapshotId: string;
  serialNumber: string;
  batchId: string;
  fixtureId: string;
  deviceId: string;
  actorId: string;
}

interface ProjectTargetOption {
  testId: string;
  targetKind: string;
  targetId: string;
  label: string;
  detail: string;
  capabilityId: string;
  commandName: string;
  providerKey: string;
  inputPayload: string;
}

interface CustomBlocklyBlockDraft {
  blockType: string;
  category: string;
  displayName: string;
  blocklyJsonText: string;
  pythonCodeTemplate: string;
}

const addableNodeKinds: AddableProcessNodeKind[] = [
  'PythonScript',
  'Command',
  'Decision',
  'Delay',
  'End'
];

const openLineOpsResultBlockType = 'openlineops_result_from_input';
const openLineOpsMoveAxisBlockType = 'openlineops_move_axis';
const openLineOpsSetLightBlockType = 'openlineops_set_light';
const openLineOpsRotateMotorBlockType = 'openlineops_rotate_motor';
const openLineOpsWaitBlockType = 'openlineops_wait';
const builtInBlocklyCatalogTimestamp = '2026-06-30T00:00:00+00:00';

const fallbackBlocklyBlockCatalog: ProcessBlocklyBlockDefinition[] = [
  {
    blockType: openLineOpsMoveAxisBlockType,
    category: 'Motion',
    displayName: 'Move Axis',
    blocklyJson: {
      type: openLineOpsMoveAxisBlockType,
      message0: 'move axis %1 to %2 %3 speed %4',
      args0: [
        {
          type: 'field_dropdown',
          name: 'AXIS',
          options: [
            ['X', 'X'],
            ['Y', 'Y'],
            ['Z', 'Z']
          ]
        },
        {
          type: 'field_number',
          name: 'POSITION',
          value: 10,
          precision: 0.001
        },
        {
          type: 'field_dropdown',
          name: 'UNIT',
          options: [
            ['mm', 'mm'],
            ['deg', 'deg']
          ]
        },
        {
          type: 'field_number',
          name: 'SPEED',
          value: 5,
          min: 0,
          precision: 0.001
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 176,
      tooltip: 'Append an axis movement command to the automation plan.'
    },
    pythonCodeTemplate: "automation_plan.append({'type': 'axis.move', 'axis': {{AXIS}}, 'position': {{number:POSITION}}, 'unit': {{UNIT}}, 'speed': {{number:SPEED}}})",
    isBuiltIn: true,
    version: 1,
    createdAtUtc: builtInBlocklyCatalogTimestamp,
    updatedAtUtc: builtInBlocklyCatalogTimestamp
  },
  {
    blockType: openLineOpsSetLightBlockType,
    category: 'I/O',
    displayName: 'Set Light',
    blocklyJson: {
      type: openLineOpsSetLightBlockType,
      message0: 'set light %1 %2',
      args0: [
        {
          type: 'field_input',
          name: 'CHANNEL',
          text: 'tower.green'
        },
        {
          type: 'field_dropdown',
          name: 'STATE',
          options: [
            ['on', 'On'],
            ['off', 'Off']
          ]
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 205,
      tooltip: 'Append a digital output command for a light channel.'
    },
    pythonCodeTemplate: "automation_plan.append({'type': 'io.light', 'channel': {{CHANNEL}}, 'state': {{STATE}}})",
    isBuiltIn: true,
    version: 1,
    createdAtUtc: builtInBlocklyCatalogTimestamp,
    updatedAtUtc: builtInBlocklyCatalogTimestamp
  },
  {
    blockType: openLineOpsRotateMotorBlockType,
    category: 'Motion',
    displayName: 'Rotate Motor',
    blocklyJson: {
      type: openLineOpsRotateMotorBlockType,
      message0: 'rotate motor %1 at %2 rpm for %3 ms',
      args0: [
        {
          type: 'field_input',
          name: 'MOTOR',
          text: 'motor.main'
        },
        {
          type: 'field_number',
          name: 'RPM',
          value: 1200,
          precision: 1
        },
        {
          type: 'field_number',
          name: 'DURATION_MS',
          value: 500,
          min: 0,
          precision: 1
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 176,
      tooltip: 'Append a timed motor rotation command.'
    },
    pythonCodeTemplate: "automation_plan.append({'type': 'motor.rotate', 'motor': {{MOTOR}}, 'rpm': {{number:RPM}}, 'duration_ms': {{number:DURATION_MS}}})",
    isBuiltIn: true,
    version: 1,
    createdAtUtc: builtInBlocklyCatalogTimestamp,
    updatedAtUtc: builtInBlocklyCatalogTimestamp
  },
  {
    blockType: openLineOpsWaitBlockType,
    category: 'Flow',
    displayName: 'Wait',
    blocklyJson: {
      type: openLineOpsWaitBlockType,
      message0: 'wait %1 ms',
      args0: [
        {
          type: 'field_number',
          name: 'DURATION_MS',
          value: 250,
          min: 0,
          precision: 1
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 48,
      tooltip: 'Append a deterministic wait step.'
    },
    pythonCodeTemplate: "automation_plan.append({'type': 'flow.wait', 'duration_ms': {{number:DURATION_MS}}})",
    isBuiltIn: true,
    version: 1,
    createdAtUtc: builtInBlocklyCatalogTimestamp,
    updatedAtUtc: builtInBlocklyCatalogTimestamp
  },
  {
    blockType: openLineOpsResultBlockType,
    category: 'Result',
    displayName: 'Result From Input',
    blocklyJson: {
      type: openLineOpsResultBlockType,
      message0: 'result key %1 from input %2 status %3 include node %4 include timestamp %5',
      args0: [
        {
          type: 'field_input',
          name: 'OUTPUT_KEY',
          text: 'normalized'
        },
        {
          type: 'field_input',
          name: 'INPUT_PAYLOAD',
          text: 'scan-ok'
        },
        {
          type: 'field_input',
          name: 'STATUS',
          text: 'ok'
        },
        {
          type: 'field_checkbox',
          name: 'INCLUDE_NODE_ID',
          checked: true
        },
        {
          type: 'field_checkbox',
          name: 'INCLUDE_TIMESTAMP',
          checked: false
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 176,
      tooltip: 'Write the PythonScript result payload used by runtime traceability.'
    },
    pythonCodeTemplate: [
      'input_payload = {{INPUT_PAYLOAD}}',
      'result[{{OUTPUT_KEY}}] = input_payload',
      "result['status'] = {{STATUS}}",
      "if {{INCLUDE_TIMESTAMP}} == 'TRUE':",
      "    result['timestamp_utc'] = 'runtime-provided'",
      "if {{INCLUDE_NODE_ID}} == 'TRUE':",
      "    result['node'] = node_id"
    ].join('\n'),
    isBuiltIn: true,
    version: 1,
    createdAtUtc: builtInBlocklyCatalogTimestamp,
    updatedAtUtc: builtInBlocklyCatalogTimestamp
  }
];

export function ProcessWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onWorkspaceChanged,
  onMessage
}: ProcessWorkbenchProps): React.ReactElement {
  const [definitions, setDefinitions] = useState<ProcessDefinitionSummary[]>([]);
  const [selectedDefinition, setSelectedDefinition] = useState<ProcessDefinitionResponse | null>(null);
  const [editingDefinitionId, setEditingDefinitionId] = useState<string | null>(null);
  const [loadedDefinitionStatus, setLoadedDefinitionStatus] = useState<string | null>(null);
  const [validationReport, setValidationReport] = useState<ProcessGraphValidationReport | null>(null);
  const [draft, setDraft] = useState<ProcessDraft>(() => createDraft());
  const [launchDraft, setLaunchDraft] = useState<RuntimeLaunchDraft>(() => createRuntimeLaunchDraft());
  const [lastStartedSession, setLastStartedSession] =
    useState<StartedProcessRuntimeSessionResponse | null>(null);
  const [blockCatalog, setBlockCatalog] =
    useState<ProcessBlocklyBlockDefinition[]>(() => fallbackBlocklyBlockCatalog);
  const [customBlockDraft, setCustomBlockDraft] =
    useState<CustomBlocklyBlockDraft>(() => createCustomBlocklyBlockDraft());
  const [selectedBlockHistoryType, setSelectedBlockHistoryType] = useState('');
  const [blockHistory, setBlockHistory] = useState<ProcessBlocklyBlockDefinition[]>([]);
  const [blockHistoryBusy, setBlockHistoryBusy] = useState(false);
  const [projectTopology, setProjectTopology] = useState<AutomationTopologyResponse | null>(null);
  const [lastProjectSnapshot, setLastProjectSnapshot] = useState<PublishedProjectSnapshotResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const activeApplication = activeWorkspace?.project.applications.find(
    application => application.applicationId === activeApplicationId)
    ?? activeWorkspace?.project.applications[0]
    ?? null;
  const selectedApplicationSnapshot = useMemo(
    () => findApplicationSnapshot(
      activeWorkspace,
      activeApplication?.applicationId ?? null,
      lastProjectSnapshot),
    [activeApplication?.applicationId, activeWorkspace, lastProjectSnapshot]);
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
  const previewScriptNode = selectedNode?.kind === 'PythonScript'
    ? selectedNode
    : draft.nodes.find(node => node.kind === 'PythonScript') ?? null;
  const previewWorkspaceJson = previewScriptNode
    ? previewScriptNode.blocklyWorkspaceJson
    : '';
  const previewSourceCode = previewScriptNode
    ? getNodeSourceCode(previewScriptNode, blockCatalog)
    : '# Select or add a PythonScript node to preview source.';
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
    if (!isBackendHealthy) {
      return;
    }

    const rows = await listProcessDefinitions(projectApplicationApiScope);
    setDefinitions(rows);
  }, [isBackendHealthy, projectApplicationApiScope]);

  const loadBlocklyBlocks = useCallback(async () => {
    const requestedScopeKey = editorScopeKey;
    if (!isBackendHealthy) {
      if (blockScopeKeyRef.current === requestedScopeKey) {
        setBlockCatalog(fallbackBlocklyBlockCatalog);
      }
      return;
    }

    const rows = await listProcessBlocklyBlocks(projectApplicationApiScope);
    if (blockScopeKeyRef.current === requestedScopeKey) {
      setBlockCatalog(rows.length > 0 ? rows : fallbackBlocklyBlockCatalog);
    }
  }, [editorScopeKey, isBackendHealthy, projectApplicationApiScope]);

  const loadProjectTopology = useCallback(async () => {
    if (!isBackendHealthy || !activeApplication?.topologyId) {
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
    setBlockCatalog(fallbackBlocklyBlockCatalog);
    setSelectedBlockHistoryType('');
    setBlockHistory([]);
    setBlockHistoryBusy(false);
    setLastStartedSession(null);
    setLastProjectSnapshot(null);
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
    if (!isBackendHealthy || !selectedBlockHistoryType) {
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
    setLastStartedSession(null);
    setLastProjectSnapshot(null);
  }, []);

  const resetDraft = useCallback(() => {
    setDraft(createDraft(blockCatalog));
    setSelectedDefinition(null);
    setEditingDefinitionId(null);
    setLoadedDefinitionStatus(null);
    setValidationReport(null);
    setLaunchDraft(createRuntimeLaunchDraft());
    setLastStartedSession(null);
    setLastProjectSnapshot(null);
  }, [blockCatalog]);

  const saveDraft = useCallback(async () => {
    if (isLoadedDefinitionReadOnly) {
      onMessage('Published process definitions cannot be overwritten. Create a new draft to continue.');
      return;
    }

    setBusy(true);
    setValidationReport(null);

    try {
      const request = toCreateRequest(draft, blockCatalog);
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
      setLastProjectSnapshot(null);
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
    if (!selectedDefinition) {
      return;
    }

    const report = await validateProcessDefinition(
      selectedDefinition.processDefinitionId,
      projectApplicationApiScope);
    setValidationReport(report);
    onMessage(report?.isValid ? 'Process graph is valid' : 'Process graph has validation issues');
  }, [onMessage, projectApplicationApiScope, selectedDefinition]);

  const publishSelected = useCallback(async () => {
    if (!selectedDefinition) {
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
      setDraft(fromProcessDefinition(response.body, blockCatalog));
      setLastStartedSession(null);
      setLastProjectSnapshot(null);
      onMessage(`Published ${response.body.processDefinitionId}`);
      await loadDefinitions();
    } finally {
      setBusy(false);
    }
  }, [blockCatalog, loadDefinitions, onMessage, projectApplicationApiScope, selectedDefinition]);

  const loadExisting = useCallback(async (summary: ProcessDefinitionSummary) => {
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
      setDraft(fromProcessDefinition(response.body, blockCatalog));
      setLastStartedSession(null);
      setLastProjectSnapshot(null);
      onMessage(`${response.body.processDefinitionId} loaded`);
    } finally {
      setBusy(false);
    }
  }, [blockCatalog, onMessage, projectApplicationApiScope]);

  const startPublishedRuntimeSession = useCallback(async () => {
    const projectSnapshotId = selectedApplicationSnapshot?.snapshotId ?? null;

    if (activeWorkspace && !projectSnapshotId) {
      onMessage('Publish a project snapshot before starting runtime');
      return;
    }

    if (!projectSnapshotId && !selectedDefinition) {
      return;
    }

    setBusy(true);
    try {
      const response = activeWorkspace
        ? await startProjectSnapshotRuntimeSession(
          activeWorkspace.project.projectId,
          projectSnapshotId!,
          toStartProjectSnapshotRuntimeSessionRequest(launchDraft))
        : await startProcessRuntimeSession(
          selectedDefinition!.processDefinitionId,
          toStartRuntimeSessionRequest(launchDraft));
      if (!response.ok || !response.body) {
        onMessage(`Runtime start failed: ${response.status} ${response.text}`);
        return;
      }

      setLastStartedSession(response.body);
      onMessage(`Started ${response.body.sessionId} ${response.body.status}`);
    } finally {
      setBusy(false);
    }
  }, [activeWorkspace, launchDraft, onMessage, selectedApplicationSnapshot?.snapshotId, selectedDefinition]);

  const publishCurrentProjectSnapshot = useCallback(async () => {
    if (!activeWorkspace || !activeApplication || !activeApplication.topologyId || !selectedDefinition || !projectTopology) {
      onMessage('Open a project with topology and publish a process before publishing a project snapshot');
      return;
    }

    if (selectedDefinition.status !== 'Published') {
      onMessage('Publish the process definition before publishing a project snapshot');
      return;
    }

    if (!launchDraft.configurationSnapshotId.trim()) {
      onMessage('Configuration snapshot id is required before publishing a project snapshot');
      return;
    }

    setBusy(true);
    try {
      const linkResponse = await linkProjectProcessDefinition(
        activeWorkspace.project.projectId,
        activeApplication.applicationId,
        selectedDefinition.processDefinitionId);
      if (!linkResponse.ok || !linkResponse.body) {
        onMessage(`Project process link failed: ${linkResponse.status} ${linkResponse.text}`);
        return;
      }

      const snapshotId = `project-snapshot-${Date.now().toString(36)}`;
      const publishResponse = await publishProjectSnapshot(activeWorkspace.project.projectId, {
        snapshotId,
        applicationId: activeApplication.applicationId,
        processDefinitionId: selectedDefinition.processDefinitionId,
        configurationSnapshotId: launchDraft.configurationSnapshotId.trim()
      });
      if (!publishResponse.ok || !publishResponse.body) {
        onMessage(`Project snapshot publish failed: ${publishResponse.status} ${publishResponse.text}`);
        return;
      }

      const savedWorkspace = await saveAutomationProjectManifest(activeWorkspace.project.projectId);
      if (!savedWorkspace.ok || !savedWorkspace.body) {
        onMessage(`Project manifest save failed: ${savedWorkspace.status} ${savedWorkspace.text}`);
        return;
      }

      const snapshot = publishResponse.body.snapshots
        .find(item => item.snapshotId === snapshotId)
        ?? publishResponse.body.snapshots.at(-1)
        ?? null;
      setLastProjectSnapshot(snapshot);
      onWorkspaceChanged(savedWorkspace.body);
      onMessage(`Project snapshot published ${snapshotId}`);
    } finally {
      setBusy(false);
    }
  }, [
    activeApplication,
    activeWorkspace,
    launchDraft.configurationSnapshotId,
    onMessage,
    onWorkspaceChanged,
    projectTopology,
    selectedDefinition
  ]);

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
      const newNode = createNode(kind, createUniqueNodeId(kind, current.nodes), blockCatalog);
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
    if (!isBackendHealthy) {
      onMessage('Backend is required to register Blockly blocks');
      return;
    }

    let blocklyJson: Record<string, unknown>;
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

    const request: RegisterProcessBlocklyBlockDefinitionRequest = {
      blockType: customBlockDraft.blockType,
      category: customBlockDraft.category,
      displayName: customBlockDraft.displayName,
      blocklyJson,
      pythonCodeTemplate: customBlockDraft.pythonCodeTemplate
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
      onMessage(`Registered Blockly block ${response.body.blockType} v${response.body.version}`);
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
    if (!isBackendHealthy) {
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
        pythonCodeTemplate: block.pythonCodeTemplate
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
      onMessage(`Restored ${block.blockType} v${block.version} as v${response.body.version}`);
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
          <section className="process-form">
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
              blockCatalog={blockCatalog}
              onSelectNode={selectNode}
              onRemoveNode={removeSelectedNode}
              onUpdateNode={updateSelectedNode}
            />
          </section>

          <section className="process-preview">
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
            <div className="source-preview">
              <div className="source-header">
                <Braces size={16} />
                <span>Generated Python</span>
              </div>
              <pre>{previewSourceCode}</pre>
            </div>
            <div className="workspace-preview">
              <strong>Workspace JSON</strong>
              <code>{previewWorkspaceJson || 'No Blockly workspace in the selected graph.'}</code>
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
        />

        <RuntimeLaunchPanel
          activeWorkspace={activeWorkspace}
          activeApplicationId={activeApplication?.applicationId ?? null}
          topology={projectTopology}
          isBackendHealthy={isBackendHealthy}
          busy={busy}
          selectedDefinition={selectedDefinition}
          launchDraft={launchDraft}
          lastStartedSession={lastStartedSession}
          lastProjectSnapshot={lastProjectSnapshot}
          onChange={setLaunchDraft}
          onPublishProjectSnapshot={publishCurrentProjectSnapshot}
          onStart={startPublishedRuntimeSession}
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
  blockCatalog,
  onSelectNode,
  onRemoveNode,
  onUpdateNode
}: {
  node: ProcessNodeDraft | null;
  nodes: ProcessNodeDraft[];
  transitions: ProcessTransitionDraft[];
  blockCatalog: ProcessBlocklyBlockDefinition[];
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

      <label>
        <span>Selected Node</span>
        <select value={node.nodeId} onChange={event => onSelectNode(event.target.value)}>
          {nodes.map(item => (
            <option key={item.nodeId} value={item.nodeId}>
              {item.displayName} ({item.kind})
            </option>
          ))}
        </select>
      </label>

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

      <NodeKindFields
        node={node}
        blockCatalog={blockCatalog}
        onUpdateNode={onUpdateNode}
      />

      <div className="node-degree-list">
        <span>Transitions</span>
        <strong>{transitions.filter(transition => transition.fromNodeId === node.nodeId).length} out</strong>
        <strong>{transitions.filter(transition => transition.toNodeId === node.nodeId).length} in</strong>
      </div>
    </div>
  );
}

function NodeKindFields({
  node,
  blockCatalog,
  onUpdateNode
}: {
  node: ProcessNodeDraft;
  blockCatalog: ProcessBlocklyBlockDefinition[];
  onUpdateNode(updater: (node: ProcessNodeDraft) => ProcessNodeDraft): void;
}): React.ReactElement | null {
  if (node.kind === 'Command') {
    return (
      <>
        <label>
          <span>Required Capability</span>
          <input
            value={node.requiredCapability}
            data-testid="process-node-required-capability"
            onChange={event => onUpdateNode(current => ({
              ...current,
              requiredCapability: event.target.value
            }))}
          />
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
        <label>
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
      </>
    );
  }

  if (node.kind === 'PythonScript') {
    return (
      <>
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
        <div className="segmented-control" role="group" aria-label="Script editor mode">
          <button
            type="button"
            className={node.scriptEditorMode === 'Blockly' ? 'selected' : ''}
            onClick={() => onUpdateNode(current => ({
              ...current,
              scriptEditorMode: 'Blockly'
            }))}
          >
            <Sparkles size={15} />
            Blockly
          </button>
          <button
            type="button"
            className={node.scriptEditorMode === 'ManualCode' ? 'selected' : ''}
            onClick={() => onUpdateNode(current => ({
              ...current,
              scriptEditorMode: 'ManualCode',
              manualSourceCode: current.manualSourceCode || createPythonSource(current.blocklyWorkspaceJson, blockCatalog)
            }))}
          >
            <Code2 size={15} />
            Code
          </button>
        </div>

        {node.scriptEditorMode === 'Blockly' ? (
          <BlocklyWorkspaceEditor
            workspaceJson={node.blocklyWorkspaceJson}
            blockCatalog={blockCatalog}
            onChange={change => onUpdateNode(current => ({
              ...current,
              blocklyWorkspaceJson: change.workspaceJson,
              inputPayload: change.inputPayload,
              manualSourceCode: current.manualSourceCode || change.sourceCode
            }))}
          />
        ) : (
          <>
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
            <label className="source-editor">
              <span>Manual Python</span>
              <textarea
                value={node.manualSourceCode}
                onChange={event => onUpdateNode(current => ({
                  ...current,
                  manualSourceCode: event.target.value
                }))}
              />
            </label>
          </>
        )}
      </>
    );
  }

  return null;
}

function BlocklyWorkspaceEditor({
  workspaceJson,
  blockCatalog,
  onChange
}: {
  workspaceJson: string;
  blockCatalog: ProcessBlocklyBlockDefinition[];
  onChange(change: {
    workspaceJson: string;
    sourceCode: string;
    inputPayload: string;
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
        startScale: 0.85,
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
    loadWorkspaceState(workspaceJson, workspace);
    Blockly.svgResize(workspace);

    const changeListener = (event: Blockly.Events.Abstract) => {
      if (event.isUiEvent) {
        return;
      }

      const nextJson = saveWorkspaceState(workspace);
      lastAppliedJsonRef.current = nextJson;
      onChangeRef.current({
        workspaceJson: nextJson,
        sourceCode: createPythonSource(nextJson, blockCatalog),
        inputPayload: getWorkspaceInputPayload(nextJson)
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
  }, [blockCatalog, toolbox]);

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
        <span>Motion, I/O, wait, and result blocks generate PythonScript source.</span>
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
  const canApply = selectedNode?.kind === 'Command' || selectedNode?.kind === 'PythonScript';

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
              <em>{target.capabilityId || target.targetKind}</em>
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
  onRestoreVersion
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
          <span key={block.blockType}>
            {block.displayName}
            {block.isBuiltIn ? ' v1' : ` v${block.version}`}
          </span>
        ))}
        {blockCatalog.length > 7 ? <span>+{blockCatalog.length - 7} more</span> : null}
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
          value={draft.pythonCodeTemplate}
          onChange={event => onChange(current => ({
            ...current,
            pythonCodeTemplate: event.target.value
          }))}
          aria-label="Python code template"
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
                  {block.displayName} v{block.version}
                </option>
              ))}
            </select>
            <div className="block-version-list" data-testid="block-version-history">
              {blockHistory.length > 0 ? blockHistory.map(version => (
                <div className="block-version-row" key={`${version.blockType}-${version.version}`}>
                  <div>
                    <strong>v{version.version}</strong>
                    <span>{formatCompactDateTime(version.updatedAtUtc)}</span>
                    <small>{version.displayName}</small>
                  </div>
                  <button
                    type="button"
                    className="button ghost"
                    onClick={() => onRestoreVersion(version)}
                    disabled={!isBackendHealthy || busy || blockHistoryBusy || version.version === latestVersion}
                    data-testid={`restore-blockly-block-v${version.version}`}
                    title={`Restore ${version.blockType} v${version.version}`}
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
    <div className="transition-editor">
      <div className="transition-editor-header">
        <strong>Transitions</strong>
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
    </div>
  );
}

function RuntimeLaunchPanel({
  activeWorkspace,
  activeApplicationId,
  topology,
  isBackendHealthy,
  busy,
  selectedDefinition,
  launchDraft,
  lastStartedSession,
  lastProjectSnapshot,
  onChange,
  onPublishProjectSnapshot,
  onStart
}: {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  topology: AutomationTopologyResponse | null;
  isBackendHealthy: boolean;
  busy: boolean;
  selectedDefinition: ProcessDefinitionResponse | null;
  launchDraft: RuntimeLaunchDraft;
  lastStartedSession: StartedProcessRuntimeSessionResponse | null;
  lastProjectSnapshot: PublishedProjectSnapshotResponse | null;
  onChange(updater: (current: RuntimeLaunchDraft) => RuntimeLaunchDraft): void;
  onPublishProjectSnapshot(): void;
  onStart(): void;
}): React.ReactElement {
  const isPublished = selectedDefinition?.status === 'Published';
  const launchSnapshotId = findApplicationSnapshot(
    activeWorkspace,
    activeApplicationId,
    lastProjectSnapshot)?.snapshotId ?? null;
  const canPublishProjectSnapshot = isBackendHealthy
    && activeWorkspace !== null
    && activeApplicationId !== null
    && topology !== null
    && isPublished
    && launchDraft.configurationSnapshotId.trim().length > 0
    && !busy;
  const canStart = isBackendHealthy
    && (activeWorkspace
      ? launchSnapshotId !== null
      : isPublished && launchDraft.configurationSnapshotId.trim().length > 0)
    && !busy;

  return (
    <div className="runtime-launch-box">
      <div className="runtime-launch-header">
        <div>
          <strong>Published Runtime</strong>
          <span>{isPublished ? selectedDefinition.processDefinitionId : 'Publish a graph first'}</span>
        </div>
        <button
          type="button"
          className="button primary"
          disabled={!canStart}
          onClick={onStart}
          data-testid="start-published-process-session"
        >
          <Play size={15} />
          Start
        </button>
      </div>
      <div className="project-snapshot-actions">
        <button
          type="button"
          className="button"
          disabled={!canPublishProjectSnapshot}
          onClick={onPublishProjectSnapshot}
          data-testid="publish-project-snapshot"
        >
          <GitBranch size={15} />
          Publish Snapshot
        </button>
        <span>{launchSnapshotId ?? 'No project snapshot'}</span>
      </div>
      <label>
        <span>Configuration Snapshot ID</span>
        <input
          value={launchDraft.configurationSnapshotId}
          onChange={event => onChange(current => ({
            ...current,
            configurationSnapshotId: event.target.value
          }))}
          data-testid="process-runtime-snapshot-id"
        />
      </label>
      <div className="runtime-launch-grid">
        <label>
          <span>Serial</span>
          <input
            value={launchDraft.serialNumber}
            onChange={event => onChange(current => ({
              ...current,
              serialNumber: event.target.value
            }))}
          />
        </label>
        <label>
          <span>Batch</span>
          <input
            value={launchDraft.batchId}
            onChange={event => onChange(current => ({
              ...current,
              batchId: event.target.value
            }))}
          />
        </label>
        <label>
          <span>Fixture</span>
          <input
            value={launchDraft.fixtureId}
            onChange={event => onChange(current => ({
              ...current,
              fixtureId: event.target.value
            }))}
          />
        </label>
        <label>
          <span>Device</span>
          <input
            value={launchDraft.deviceId}
            onChange={event => onChange(current => ({
              ...current,
              deviceId: event.target.value
            }))}
          />
        </label>
      </div>
      <label>
        <span>Actor</span>
        <input
          value={launchDraft.actorId}
          onChange={event => onChange(current => ({
            ...current,
            actorId: event.target.value
          }))}
        />
      </label>
      {lastProjectSnapshot ? (
        <div className="project-snapshot-result" data-testid="project-snapshot-result">
          <strong>{lastProjectSnapshot.snapshotId}</strong>
          <span>{lastProjectSnapshot.processVersionId}</span>
          <small>{lastProjectSnapshot.configurationSnapshotId}</small>
          {lastProjectSnapshot.releaseContentSha256 ? (
            <small>Release {lastProjectSnapshot.releaseContentSha256.slice(0, 12)}</small>
          ) : null}
        </div>
      ) : null}
      {lastStartedSession ? (
        <div className="runtime-start-result" data-testid="runtime-start-result">
          <strong>{lastStartedSession.status}</strong>
          <span>{lastStartedSession.sessionId}</span>
          {lastStartedSession.snapshotId ? (
            <small>{lastStartedSession.snapshotId}</small>
          ) : null}
          <small>
            {lastStartedSession.completedSteps} steps,
            {' '}
            {lastStartedSession.commandCount} commands,
            {' '}
            {lastStartedSession.incidentCount} incidents
          </small>
        </div>
      ) : null}
    </div>
  );
}

function findApplicationSnapshot(
  workspace: AutomationProjectWorkspaceResponse | null,
  applicationId: string | null,
  preferredSnapshot: PublishedProjectSnapshotResponse | null
): PublishedProjectSnapshotResponse | null {
  if (!workspace || !applicationId) {
    return null;
  }

  if (preferredSnapshot?.applicationId === applicationId) {
    return preferredSnapshot;
  }

  const snapshots = workspace.project.snapshots.filter(
    snapshot => snapshot.applicationId === applicationId);
  return snapshots.find(snapshot => snapshot.snapshotId === workspace.project.activeSnapshotId)
    ?? snapshots
      .slice()
      .sort((left, right) => right.publishedAtUtc.localeCompare(left.publishedAtUtc))[0]
    ?? null;
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
  const bindingsByCapabilityId = new Map(topology.driverBindings.map(binding => [binding.capabilityId, binding]));
  const createOption = (
    testId: string,
    targetKind: string,
    targetId: string,
    label: string,
    detail: string,
    capabilityId = ''
  ): ProjectTargetOption => {
    const capability = capabilityId ? capabilitiesById.get(capabilityId) : null;
    const binding = capabilityId ? bindingsByCapabilityId.get(capabilityId) : null;
    const commandName = capability?.commandName ?? '';
    const providerKey = binding?.providerKey ?? '';

    return {
      testId,
      targetKind,
      targetId,
      label,
      detail,
      capabilityId,
      commandName,
      providerKey,
      inputPayload: JSON.stringify({
        projectId: workspace.project.projectId,
        applicationId: application.applicationId,
        topologyId: topology.topologyId,
        targetKind,
        targetId,
        targetLabel: label,
        capabilityId: capabilityId || null,
        commandName: commandName || null,
        providerKey: providerKey || null
      })
    };
  };

  const moduleTargets = topology.modules.map((module, index) => createOption(
    `module-${index}`,
    'AutomationModule',
    module.moduleId,
    module.displayName,
    `${module.moduleKind} on ${module.nodeId}`,
    module.providedCapabilityIds[0] ?? module.requiredCapabilityIds[0] ?? ''));

  const slotGroupTargets = topology.slotGroups.map((group, index) => createOption(
    `slot-group-${index}`,
    'SlotGroup',
    group.slotGroupId,
    group.displayName,
    `${group.kind}, ${group.slotIds.length}/${group.capacity} slots`));

  const slotTargets = topology.slots.map((slot, index) => createOption(
    `slot-${index}`,
    'Slot',
    slot.slotId,
    slot.displayName,
    `${slot.address}, ${slot.materialKind}`));

  const capabilityTargets = topology.capabilities.map((capability, index) => createOption(
    `capability-${index}`,
    'CapabilityContract',
    capability.capabilityId,
    capability.commandName,
    `${capability.safetyClass}, ${capability.timeoutSeconds}s`,
    capability.capabilityId));

  return [
    ...moduleTargets,
    ...slotGroupTargets,
    ...slotTargets,
    ...capabilityTargets
  ];
}

function applyProjectTargetToNode(
  node: ProcessNodeDraft,
  target: ProjectTargetOption
): ProcessNodeDraft {
  if (node.kind === 'Command') {
    return {
      ...node,
      displayName: target.commandName ? `Execute ${target.label}` : node.displayName,
      requiredCapability: target.capabilityId || node.requiredCapability,
      commandName: target.commandName || node.commandName,
      inputPayload: target.inputPayload
    };
  }

  if (node.kind === 'PythonScript') {
    return {
      ...node,
      inputPayload: target.inputPayload,
      blocklyWorkspaceJson: setWorkspaceInputPayload(node.blocklyWorkspaceJson, target.inputPayload)
    };
  }

  return node;
}

function setWorkspaceInputPayload(workspaceJson: string, inputPayload: string): string {
  try {
    const parsed = JSON.parse(workspaceJson) as {
      blocks?: {
        blocks?: WorkspaceBlockState[];
      };
    };
    const resultBlock = findWorkspaceBlock(parsed.blocks?.blocks ?? [], openLineOpsResultBlockType);
    if (!resultBlock) {
      return workspaceJson;
    }

    resultBlock.fields = {
      ...resultBlock.fields,
      INPUT_PAYLOAD: inputPayload
    };

    return JSON.stringify(parsed);
  } catch {
    return workspaceJson;
  }
}

function createDraft(
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): ProcessDraft {
  const seed = Date.now().toString(36);
  const pythonNode = createNode('PythonScript', 'normalize', blockCatalog);

  return {
    processDefinitionId: `desktop-python-${seed}`,
    versionId: `desktop-python-${seed}@1.0.0`,
    displayName: 'Desktop Python Script Process',
    selectedNodeId: pythonNode.nodeId,
    nodes: [
      createNode('Start', 'start', blockCatalog),
      pythonNode,
      createNode('End', 'end', blockCatalog)
    ],
    transitions: [
      {
        transitionId: 'start-to-normalize',
        fromNodeId: 'start',
        toNodeId: 'normalize',
        label: '',
        loopPolicy: 'None',
        maxTraversals: 1
      },
      {
        transitionId: 'normalize-to-end',
        fromNodeId: 'normalize',
        toNodeId: 'end',
        label: 'done',
        loopPolicy: 'None',
        maxTraversals: 1
      }
    ]
  };
}

function createRuntimeLaunchDraft(): RuntimeLaunchDraft {
  const seed = Date.now().toString(36);

  return {
    configurationSnapshotId: '',
    serialNumber: `SN-${seed.toUpperCase()}`,
    batchId: `batch-${seed}`,
    fixtureId: 'fixture-desktop',
    deviceId: 'device-desktop',
    actorId: 'desktop-operator'
  };
}

function createCustomBlocklyBlockDraft(): CustomBlocklyBlockDraft {
  return {
    blockType: 'user_fixture_action',
    category: 'Fixture',
    displayName: 'Fixture Action',
    blocklyJsonText: JSON.stringify({
      type: 'user_fixture_action',
      message0: 'fixture action %1',
      args0: [
        {
          type: 'field_input',
          name: 'ACTION',
          text: 'open'
        }
      ],
      previousStatement: null,
      nextStatement: null,
      colour: 130,
      tooltip: 'Run a user-defined fixture action.'
    }, null, 2),
    pythonCodeTemplate: "automation_plan.append({'type': 'fixture.action', 'action': {{ACTION}}})"
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

function fromProcessDefinition(
  definition: ProcessDefinitionResponse,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): ProcessDraft {
  const nodes = definition.nodes.map(node => fromNodeResponse(node, blockCatalog));
  const selectedNodeId = nodes.find(node => node.kind === 'PythonScript')?.nodeId
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

function fromNodeResponse(
  node: ProcessNodeResponse,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): ProcessNodeDraft {
  const kind = parseNodeKind(node.kind);
  const scriptEditorMode = parseScriptEditorMode(node.scriptEditorMode);
  const blocklyWorkspaceJson = normalizeBlocklyWorkspaceJson(
    node.blocklyWorkspaceJson,
    node.inputPayload ?? defaultInputPayload(kind));
  const manualSourceCode = node.scriptSourceCode ?? createPythonSource(blocklyWorkspaceJson, blockCatalog);

  return {
    nodeId: node.nodeId,
    kind,
    displayName: node.displayName,
    requiredCapability: node.requiredCapability ?? defaultRequiredCapability(kind),
    commandName: node.commandName ?? defaultCommandName(kind),
    timeoutSeconds: node.timeoutSeconds ?? defaultTimeoutSeconds(kind),
    inputPayload: node.inputPayload ?? getWorkspaceInputPayload(blocklyWorkspaceJson),
    scriptEditorMode,
    scriptVersion: node.scriptVersion ?? '1',
    blocklyWorkspaceJson,
    manualSourceCode
  };
}

function createNode(
  kind: ProcessNodeKind,
  nodeId: string,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): ProcessNodeDraft {
  const blocklyWorkspaceJson = createDefaultBlocklyWorkspaceJson(defaultInputPayload(kind));

  return {
    nodeId,
    kind,
    displayName: defaultDisplayName(kind),
    requiredCapability: defaultRequiredCapability(kind),
    commandName: defaultCommandName(kind),
    timeoutSeconds: defaultTimeoutSeconds(kind),
    inputPayload: getWorkspaceInputPayload(blocklyWorkspaceJson),
    scriptEditorMode: 'Blockly',
    scriptVersion: '1',
    blocklyWorkspaceJson,
    manualSourceCode: createPythonSource(blocklyWorkspaceJson, blockCatalog)
  };
}

function toCreateRequest(
  draft: ProcessDraft,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): CreateProcessDefinitionRequest {
  return {
    processDefinitionId: draft.processDefinitionId,
    versionId: draft.versionId,
    displayName: draft.displayName,
    nodes: draft.nodes.map(node => toCreateNodeRequest(node, blockCatalog)),
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

function toStartRuntimeSessionRequest(draft: RuntimeLaunchDraft): StartProcessRuntimeSessionRequest {
  return {
    configurationSnapshotId: draft.configurationSnapshotId,
    serialNumber: toOptionalString(draft.serialNumber),
    batchId: toOptionalString(draft.batchId),
    fixtureId: toOptionalString(draft.fixtureId),
    deviceId: toOptionalString(draft.deviceId),
    actorId: toOptionalString(draft.actorId)
  };
}

function toStartProjectSnapshotRuntimeSessionRequest(draft: RuntimeLaunchDraft): {
  serialNumber: string | null;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  actorId: string | null;
} {
  return {
    serialNumber: toOptionalString(draft.serialNumber),
    batchId: toOptionalString(draft.batchId),
    fixtureId: toOptionalString(draft.fixtureId),
    deviceId: toOptionalString(draft.deviceId),
    actorId: toOptionalString(draft.actorId)
  };
}

function toCreateNodeRequest(
  node: ProcessNodeDraft,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): CreateProcessNodeRequest {
  if (node.kind === 'Command') {
    return {
      ...baseNodeRequest(node),
      requiredCapability: toOptionalString(node.requiredCapability),
      commandName: toOptionalString(node.commandName),
      timeoutSeconds: node.timeoutSeconds,
      inputPayload: toOptionalString(node.inputPayload)
    };
  }

  if (node.kind === 'PythonScript') {
    return {
      ...baseNodeRequest(node),
      timeoutSeconds: node.timeoutSeconds,
      inputPayload: toOptionalString(node.inputPayload),
      scriptEditorMode: node.scriptEditorMode,
      blocklyWorkspaceJson: node.scriptEditorMode === 'Blockly'
        ? node.blocklyWorkspaceJson
        : null,
      scriptSourceCode: getNodeSourceCode(node, blockCatalog),
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
    timeoutSeconds: null,
    inputPayload: null,
    scriptEditorMode: null,
    blocklyWorkspaceJson: null,
    scriptSourceCode: null,
    scriptVersion: null
  };
}

function getNodeSourceCode(
  node: ProcessNodeDraft,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): string {
  return node.scriptEditorMode === 'Blockly'
    ? createPythonSource(node.blocklyWorkspaceJson, blockCatalog)
    : node.manualSourceCode;
}

function createPythonSource(
  workspaceJson: string,
  blockCatalog: ProcessBlocklyBlockDefinition[] = fallbackBlocklyBlockCatalog
): string {
  registerProcessBlocklyBlocks(blockCatalog);
  const workspace = new Blockly.Workspace();
  try {
    loadWorkspaceState(workspaceJson, workspace);
    const generatedCode = pythonGenerator.workspaceToCode(workspace).trim();
    const lines = [
      'automation_plan = []',
      "result = {'automation_plan': automation_plan}"
    ];

    if (generatedCode) {
      lines.push(generatedCode);
    }

    lines.push("result['command_count'] = len(automation_plan)");
    return lines.join('\n');
  } finally {
    workspace.dispose();
  }
}

function createDefaultBlocklyWorkspaceJson(inputPayload: string): string {
  return JSON.stringify({
    blocks: {
      languageVersion: 0,
      blocks: [
        {
          id: 'move-x-axis',
          type: openLineOpsMoveAxisBlockType,
          x: 32,
          y: 32,
          fields: {
            AXIS: 'X',
            POSITION: 10,
            SPEED: 5,
            UNIT: 'mm'
          },
          next: {
            block: {
              id: 'turn-light-on',
              type: openLineOpsSetLightBlockType,
              fields: {
                CHANNEL: 'tower.green',
                STATE: 'On'
              },
              next: {
                block: {
                  id: 'spin-motor',
                  type: openLineOpsRotateMotorBlockType,
                  fields: {
                    MOTOR: 'motor.main',
                    RPM: 1200,
                    DURATION_MS: 500
                  },
                  next: {
                    block: {
                      id: 'normalize-result',
                      type: openLineOpsResultBlockType,
                      fields: {
                        OUTPUT_KEY: 'normalized',
                        INPUT_PAYLOAD: inputPayload,
                        STATUS: 'ok',
                        INCLUDE_NODE_ID: 'TRUE',
                        INCLUDE_TIMESTAMP: 'FALSE'
                      }
                    }
                  }
                }
              }
            }
          }
        }
      ]
    }
  });
}

function normalizeBlocklyWorkspaceJson(value: string | null, inputPayload: string): string {
  if (!value) {
    return createDefaultBlocklyWorkspaceJson(inputPayload);
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (isLegacyBlocklyResultState(parsed)) {
      return createDefaultBlocklyWorkspaceJson(getLegacyInputPayload(parsed) ?? inputPayload);
    }

    return JSON.stringify(parsed);
  } catch {
    return createDefaultBlocklyWorkspaceJson(inputPayload);
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
  try {
    Blockly.serialization.workspaces.load(JSON.parse(workspaceJson), workspace);
  } catch {
    Blockly.serialization.workspaces.load(
      JSON.parse(createDefaultBlocklyWorkspaceJson(defaultInputPayload('PythonScript'))),
      workspace);
  }
}

function getWorkspaceInputPayload(workspaceJson: string): string {
  try {
    const parsed = JSON.parse(workspaceJson) as {
      blocks?: {
        blocks?: WorkspaceBlockState[];
      };
    };
    const resultBlock = findWorkspaceBlock(parsed.blocks?.blocks ?? [], openLineOpsResultBlockType);
    return getTextField(resultBlock, 'INPUT_PAYLOAD')
      ?? getTextField(resultBlock, 'inputPayload')
      ?? defaultInputPayload('PythonScript');
  } catch {
    return defaultInputPayload('PythonScript');
  }
}

function registerProcessBlocklyBlocks(blockCatalog: ProcessBlocklyBlockDefinition[]): void {
  const blockRegistry = Blockly.Blocks as Record<string, unknown>;
  const blockDefinitions = blockCatalog
    .map(block => block.blocklyJson)
    .filter(blockJson => {
      const type = blockJson.type;
      return typeof type === 'string' && !blockRegistry[type];
    });

  if (blockDefinitions.length > 0) {
    Blockly.common.defineBlocksWithJsonArray(
      blockDefinitions as Parameters<typeof Blockly.common.defineBlocksWithJsonArray>[0]);
  }

  for (const definition of blockCatalog) {
    pythonGenerator.forBlock[definition.blockType] = block =>
      ensureTrailingNewline(renderPythonTemplate(definition.pythonCodeTemplate, block));
  }
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

function renderPythonTemplate(template: string, block: Blockly.Block): string {
  return template.replace(
    /\{\{\s*(?:(raw|number|string):)?([A-Za-z_][A-Za-z0-9_]*)\s*\}\}/g,
    (_match: string, mode: string | undefined, fieldName: string) => {
      const fieldValue = block.getFieldValue(fieldName);
      const value = typeof fieldValue === 'string'
        ? fieldValue
        : String(fieldValue ?? '');

      if (mode === 'raw') {
        return value;
      }

      if (mode === 'number') {
        return toPythonNumberLiteral(value, 0);
      }

      return toPythonStringLiteral(value);
    });
}

function ensureTrailingNewline(value: string): string {
  return value.endsWith('\n') ? value : `${value}\n`;
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

interface WorkspaceBlockState {
  type?: string;
  fields?: Record<string, unknown>;
  next?: {
    block?: WorkspaceBlockState;
  };
}

function isLegacyBlocklyResultState(value: unknown): value is {
  blocks: {
    blocks: WorkspaceBlockState[];
  };
} {
  const blocks = (value as { blocks?: { blocks?: WorkspaceBlockState[] } }).blocks?.blocks;
  const firstBlock = blocks?.[0];
  return firstBlock?.type === openLineOpsResultBlockType
    && getTextField(firstBlock, 'inputPayload') !== null;
}

function getLegacyInputPayload(value: {
  blocks: {
    blocks: WorkspaceBlockState[];
  };
}): string | null {
  return getTextField(value.blocks.blocks[0], 'inputPayload');
}

function findWorkspaceBlock(
  blocks: WorkspaceBlockState[],
  type: string
): WorkspaceBlockState | null {
  for (const block of blocks) {
    if (block.type === type) {
      return block;
    }

    const nested = block.next?.block
      ? findWorkspaceBlock([block.next.block], type)
      : null;
    if (nested) {
      return nested;
    }
  }

  return null;
}

function getTextField(block: WorkspaceBlockState | null, fieldName: string): string | null {
  const value = block?.fields?.[fieldName];
  return typeof value === 'string' && value.trim().length > 0
    ? value
    : null;
}

function findInsertionSource(draft: ProcessDraft): ProcessNodeDraft | null {
  const selected = draft.nodes.find(node => node.nodeId === draft.selectedNodeId);
  if (selected && selected.kind !== 'End') {
    return selected;
  }

  const selectedIncoming = draft.transitions.find(transition => transition.toNodeId === selected?.nodeId);
  return draft.nodes.find(node => node.nodeId === selectedIncoming?.fromNodeId)
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
    selectedNodeId: nodes.find(candidate => candidate.kind === 'PythonScript')?.nodeId
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
  const baseId = kind === 'PythonScript'
    ? 'python'
    : kind.toLowerCase();
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
    || value === 'PythonScript'
    ? value
    : 'Command';
}

function parseScriptEditorMode(value: string | null): ScriptEditorMode {
  return value === 'ManualCode' ? 'ManualCode' : 'Blockly';
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
    case 'PythonScript':
      return 'Automation Sequence';
  }
}

function defaultRequiredCapability(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'device.loopback' : '';
}

function defaultCommandName(kind: ProcessNodeKind): string {
  return kind === 'Command' ? 'Echo' : '';
}

function defaultTimeoutSeconds(kind: ProcessNodeKind): number {
  return kind === 'Command' || kind === 'PythonScript' ? 15 : 1;
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

function toPythonStringLiteral(value: string): string {
  return `'${value.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
}

function toPythonNumberLiteral(value: string | number | null, fallback: number): string {
  const parsed = typeof value === 'number'
    ? value
    : Number.parseFloat(value ?? '');
  return Number.isFinite(parsed) ? String(parsed) : String(fallback);
}
