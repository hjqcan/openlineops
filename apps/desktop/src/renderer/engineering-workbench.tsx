import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  CheckCircle2,
  Factory,
  FolderTree,
  GitBranch,
  Layers3,
  RefreshCw,
  RotateCcw,
  Save,
  Send,
  SlidersHorizontal
} from 'lucide-react';
import type {
  AutomationSystemResponse,
  AutomationTopologyResponse,
  AutomationProjectWorkspaceResponse,
  EngineeringProjectResponse,
  ProcessDefinitionSummary,
  RecipeResponse,
  StationProfileResponse,
  WorkspaceResponse
} from './contracts';
import {
  createEngineeringProject,
  createRecipe,
  createStationProfile,
  createWorkspace,
  getAutomationTopology,
  listEngineeringProjects,
  listProcessDefinitions,
  listRecipes,
  listStationProfiles,
  listWorkspaces,
  publishConfigurationSnapshot,
  publishRecipe
} from './api';
import { useEditorDocument } from './editor-workspace';
import type { EditorProblem } from './editor-workspace-model';
import {
  acceptSubmittedEditorDraft,
  createEditorDraftBaseline,
  isEditorDraftDirty,
  replaceEditorDraft,
  revertEditorDraft,
  synchronizeCleanEditorDraft
} from './editor-draft-baseline-model';
import {
  createEngineeringDeviceOwnerOptions,
  engineeringSourceDraftsEqual,
  type EngineeringDraft
} from './engineering-draft-model';

interface EngineeringWorkbenchProps {
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface EngineeringResources {
  topology: AutomationTopologyResponse | null;
  workspaces: WorkspaceResponse[];
  projects: EngineeringProjectResponse[];
  recipes: RecipeResponse[];
  stations: StationProfileResponse[];
  processDefinitions: ProcessDefinitionSummary[];
}

const emptyResources: EngineeringResources = {
  topology: null,
  workspaces: [],
  projects: [],
  recipes: [],
  stations: [],
  processDefinitions: []
};

export function EngineeringWorkbench({
  activeWorkspace,
  activeApplicationId,
  isBackendHealthy,
  onMessage
}: EngineeringWorkbenchProps): React.ReactElement {
  const activeApplication = useMemo(
    () => activeWorkspace?.project.applications.find(
      candidate => candidate.applicationId === activeApplicationId)
      ?? activeWorkspace?.project.applications[0]
      ?? null,
    [activeApplicationId, activeWorkspace?.project.applications]);
  const activeProjectId = activeWorkspace?.project.projectId ?? null;
  const effectiveApplicationId = activeApplication?.applicationId ?? null;
  const projectApplicationApiScope = useMemo(
    () => activeProjectId && effectiveApplicationId
      ? {
        projectId: activeProjectId,
        applicationId: effectiveApplicationId
      }
      : undefined,
    [activeProjectId, effectiveApplicationId]);
  const engineeringScopeKey = activeProjectId && effectiveApplicationId
    ? `${activeProjectId}\u0000${effectiveApplicationId}`
    : null;
  const engineeringIdentity = useMemo(
    () => effectiveApplicationId
      ? {
        workspaceId: `${effectiveApplicationId}.workspace`,
        workspaceName: `${activeApplication?.displayName ?? effectiveApplicationId} Workspace`,
        projectId: `${effectiveApplicationId}.configuration`,
        projectName: `${activeApplication?.displayName ?? effectiveApplicationId} Configuration`
      }
      : null,
    [activeApplication?.displayName, effectiveApplicationId]);
  const scopeKeyRef = useRef(engineeringScopeKey);
  scopeKeyRef.current = engineeringScopeKey;

  const [resources, setResources] = useState<EngineeringResources>(emptyResources);
  const [resourcesLoaded, setResourcesLoaded] = useState(false);
  const [draftState, setDraftState] = useState(
    () => createEditorDraftBaseline(
      createEngineeringDraft(undefined, effectiveApplicationId)));
  const draft = draftState.current;
  const setDraft = useCallback((update: React.SetStateAction<EngineeringDraft>) => {
    setDraftState(state => replaceEditorDraft(
      state,
      typeof update === 'function'
        ? update(state.current)
        : update));
  }, []);
  const draftDirty = isEditorDraftDirty(draftState, engineeringSourceDraftsEqual);
  const [createdProject, setCreatedProject] = useState<EngineeringProjectResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const publishedProcesses = useMemo(
    () => resources.processDefinitions.filter(definition => definition.status === 'Published'),
    [resources.processDefinitions]);
  const selectedProcess = useMemo(
    () => publishedProcesses.find(definition => definition.processDefinitionId === draft.processDefinitionId)
      ?? publishedProcesses[0]
      ?? null,
    [draft.processDefinitionId, publishedProcesses]);
  const configurationProject = useMemo(
    () => engineeringIdentity
      ? resources.projects.find(project => project.projectId === engineeringIdentity.projectId) ?? null
      : null,
    [engineeringIdentity, resources.projects]);
  const configurationSnapshots = configurationProject?.snapshots ?? [];
  const stationSystems = useMemo(
    () => resources.topology?.systems.filter(system => system.kind === 'Station') ?? [],
    [resources.topology]);
  const deviceOwnerSystems = useMemo(
    () => resources.topology?.systems.filter(system =>
      isSystemWithinStation(system, draft.stationSystemId, resources.topology!)) ?? [],
    [draft.stationSystemId, resources.topology]);
  const deviceOwnerOptions = useMemo(
    () => createEngineeringDeviceOwnerOptions(deviceOwnerSystems),
    [deviceOwnerSystems]);
  const selectedDeviceOwner = deviceOwnerSystems.find(
    system => system.systemId === draft.deviceOwnerSystemId) ?? null;
  const ownerCapabilityIds = useMemo(
    () => selectedDeviceOwner
      ? [...new Set([
        ...selectedDeviceOwner.requiredCapabilityIds,
        ...selectedDeviceOwner.providedCapabilityIds
      ])].sort((left, right) => left.localeCompare(right))
      : [],
    [selectedDeviceOwner]);
  const savedRecipe = resources.recipes.find(recipe => recipe.recipeId === draft.recipeId) ?? null;
  const savedStationProfile = resources.stations.find(
    station => station.stationProfileId === draft.stationProfileId) ?? null;
  const sourcePersisted = savedRecipe !== null
    && savedStationProfile !== null
    && recipeMatchesDraft(savedRecipe, draft)
    && stationProfileMatchesDraft(savedStationProfile, draft);
  const canSaveSource = isBackendHealthy
    && !busy
    && resourcesLoaded
    && engineeringIdentity !== null
    && resources.topology !== null
    && selectedDeviceOwner !== null
    && ownerCapabilityIds.includes(draft.capabilityId);
  const canPublishSnapshot = canSaveSource
    && !draftDirty
    && selectedProcess !== null
    && sourcePersisted;
  const sourceProblems = useMemo<EditorProblem[]>(() => {
    const problems: EditorProblem[] = [];
    const requiredFields: Array<[keyof EngineeringDraft, string, string]> = [
      ['recipeId', 'Recipe ID is required.', 'engineering-recipe-id'],
      ['recipeVersionId', 'Recipe version is required.', 'engineering-recipe-version'],
      ['recipeName', 'Recipe display name is required.', 'engineering-recipe-name'],
      ['stationProfileId', 'Station profile ID is required.', 'engineering-station-profile-id'],
      ['stationSystemId', 'A topology Station is required.', 'engineering-station-system'],
      ['stationName', 'Station display name is required.', 'engineering-station-name'],
      ['deviceBindingId', 'Device binding ID is required.', 'engineering-device-binding-id'],
      ['deviceOwnerSystemId', 'A device owner System is required.', 'engineering-device-owner-system'],
      ['capabilityId', 'A declared device capability is required.', 'engineering-device-capability'],
      ['deviceKey', 'Device key is required.', 'engineering-device-key']
    ];
    for (const [field, message, targetId] of requiredFields) {
      if (!draft[field].trim()) {
        problems.push({ id: `engineering-${field}`, severity: 'Error', message, targetId });
      }
    }
    if (draft.deviceOwnerSystemId && selectedDeviceOwner === null) {
      problems.push({
        id: 'engineering-device-owner-scope',
        severity: 'Error',
        message: 'The device owner must belong to the selected Station subtree.',
        targetId: 'engineering-device-owner-system'
      });
    }
    if (draft.capabilityId && !ownerCapabilityIds.includes(draft.capabilityId)) {
      problems.push({
        id: 'engineering-capability-contract',
        severity: 'Error',
        message: 'The selected owner System does not declare this capability.',
        targetId: 'engineering-device-capability'
      });
    }
    return problems;
  }, [draft, ownerCapabilityIds, selectedDeviceOwner]);
  const publicationProblems = useMemo<EditorProblem[]>(() => {
    const problems: EditorProblem[] = [];
    const requiredFields: Array<[keyof EngineeringDraft, string, string]> = [
      ['snapshotId', 'Configuration snapshot ID is required.', 'engineering-snapshot-id'],
      ['processDefinitionId', 'A published process is required.', 'engineering-process-definition'],
      ['processVersionId', 'The published process version is required.', 'engineering-process-version']
    ];
    for (const [field, message, targetId] of requiredFields) {
      if (!draft[field].trim()) {
        problems.push({ id: `engineering-${field}`, severity: 'Error', message, targetId });
      }
    }
    if (draftDirty) {
      problems.push({
        id: 'engineering-source-unsaved',
        severity: 'Error',
        message: 'Save the mutable Recipe and Station Profile source before publishing a snapshot.',
        targetId: 'save-engineering-source'
      });
    }
    if (!draftDirty && (!savedRecipe || !savedStationProfile)) {
      problems.push({
        id: 'engineering-source-missing',
        severity: 'Error',
        message: 'The selected Recipe and Station Profile must exist before publication.',
        targetId: 'save-engineering-source'
      });
    }
    return problems;
  }, [draft, draftDirty, savedRecipe, savedStationProfile]);
  const engineeringProblems = useMemo(
    () => [...sourceProblems, ...publicationProblems],
    [publicationProblems, sourceProblems]);

  const loadResources = useCallback(async () => {
    if (!isBackendHealthy || !projectApplicationApiScope || !engineeringIdentity) {
      return;
    }

    const requestedScopeKey = engineeringScopeKey;
    const topologyPromise = activeApplication?.topologyId
      ? getAutomationTopology(activeApplication.topologyId, projectApplicationApiScope)
      : Promise.resolve(null);
    const [workspaces, projects, recipes, stations, processDefinitions, topologyResponse] = await Promise.all([
      listWorkspaces(projectApplicationApiScope),
      listEngineeringProjects(projectApplicationApiScope),
      listRecipes(projectApplicationApiScope),
      listStationProfiles(projectApplicationApiScope),
      listProcessDefinitions(projectApplicationApiScope),
      topologyPromise
    ]);
    if (scopeKeyRef.current !== requestedScopeKey) {
      return;
    }

    setResources({
      topology: topologyResponse?.ok ? topologyResponse.body ?? null : null,
      workspaces,
      projects,
      recipes,
      stations,
      processDefinitions
    });
    setResourcesLoaded(true);
    setCreatedProject(
      projects.find(project => project.projectId === engineeringIdentity.projectId) ?? null);

    const publishedDefinitions = processDefinitions.filter(
      definition => definition.status === 'Published');
    setDraftState(state => {
      const current = state.current;
      const recipe = recipes.find(candidate => candidate.recipeId === current.recipeId)
        ?? recipes.find(candidate => candidate.status === 'Draft')
        ?? recipes[0]
        ?? null;
      const stationProfile = stations.find(
        candidate => candidate.stationProfileId === current.stationProfileId)
        ?? stations[0]
        ?? null;
      const process = publishedDefinitions.find(
        definition => definition.processDefinitionId === current.processDefinitionId)
        ?? publishedDefinitions[0]
        ?? null;
      const topology = topologyResponse?.ok ? topologyResponse.body ?? null : null;
      const topologyStations = topology?.systems.filter(system => system.kind === 'Station') ?? [];
      const preferredStationSystemId = stationProfile?.stationSystemId ?? current.stationSystemId;
      const stationSystemId = topologyStations.some(
        system => system.systemId === preferredStationSystemId)
        ? preferredStationSystemId
        : topologyStations[0]?.systemId ?? preferredStationSystemId;
      const owners = topology?.systems.filter(system =>
        isSystemWithinStation(system, stationSystemId, topology)) ?? [];
      const savedBinding = stationProfile?.deviceBindings[0] ?? null;
      const preferredOwnerSystemId = savedBinding?.ownerSystemId ?? current.deviceOwnerSystemId;
      const deviceOwnerSystemId = owners.some(system =>
        system.systemId === preferredOwnerSystemId)
        ? preferredOwnerSystemId
        : owners[0]?.systemId ?? stationSystemId;
      const owner = owners.find(system => system.systemId === deviceOwnerSystemId);
      const capabilities = owner
        ? [...new Set([...owner.requiredCapabilityIds, ...owner.providedCapabilityIds])]
        : [];
      const normalized = {
        ...current,
        recipeId: recipe?.recipeId ?? current.recipeId,
        recipeVersionId: recipe?.versionId ?? current.recipeVersionId,
        recipeName: recipe?.displayName ?? current.recipeName,
        stationProfileId: stationProfile?.stationProfileId ?? current.stationProfileId,
        stationSystemId,
        stationName: stationProfile?.displayName ?? current.stationName,
        deviceBindingId: savedBinding?.deviceBindingId ?? current.deviceBindingId,
        deviceOwnerSystemId,
        capabilityId: capabilities.includes(savedBinding?.capabilityId ?? current.capabilityId)
          ? savedBinding?.capabilityId ?? current.capabilityId
          : capabilities[0] ?? savedBinding?.capabilityId ?? current.capabilityId,
        deviceKey: savedBinding?.deviceKey ?? current.deviceKey,
        processDefinitionId: process?.processDefinitionId ?? '',
        processVersionId: process?.versionId ?? ''
      };
      return synchronizeCleanEditorDraft(state, normalized, engineeringSourceDraftsEqual);
    });
  }, [
    activeApplication?.topologyId,
    engineeringIdentity,
    engineeringScopeKey,
    isBackendHealthy,
    projectApplicationApiScope
  ]);

  useEffect(() => {
    setResources(emptyResources);
    setResourcesLoaded(false);
    setDraftState(createEditorDraftBaseline(
      createEngineeringDraft(undefined, effectiveApplicationId)));
    setCreatedProject(null);
    setBusy(false);
  }, [engineeringScopeKey]);

  useEffect(() => {
    let isCurrent = true;
    loadResources().catch(error => {
      if (isCurrent && scopeKeyRef.current === engineeringScopeKey) {
        onMessage(`Engineering load failed: ${String(error)}`);
      }
    });

    return () => {
      isCurrent = false;
    };
  }, [engineeringScopeKey, loadResources, onMessage]);

  const resetDraft = useCallback(() => {
    setDraft(createEngineeringDraft(selectedProcess ?? undefined, effectiveApplicationId));
  }, [effectiveApplicationId, selectedProcess]);

  const revertDraft = useCallback(async () => {
    setDraftState(state => {
      const reverted = revertEditorDraft(state);
      return {
        ...reverted,
        current: {
          ...reverted.current,
          snapshotId: state.current.snapshotId,
          processDefinitionId: state.current.processDefinitionId,
          processVersionId: state.current.processVersionId
        }
      };
    });
    onMessage('Engineering source changes discarded; the last saved Recipe and Station Profile were restored.');
  }, [onMessage]);

  const saveEngineeringSource = useCallback(async () => {
    if (!engineeringIdentity || !projectApplicationApiScope) {
      throw new Error('Open a project Application before saving engineering source.');
    }

    const submittedDraft = draft;
    const requestedScopeKey = engineeringScopeKey;
    const isCurrentScope = () => scopeKeyRef.current === requestedScopeKey;
    setBusy(true);
    try {
      let persistedWorkspace = resources.workspaces.find(
        workspace => workspace.workspaceId === engineeringIdentity.workspaceId) ?? null;
      if (!persistedWorkspace) {
        const workspace = await createWorkspace({
          workspaceId: engineeringIdentity.workspaceId,
          displayName: engineeringIdentity.workspaceName
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          throw new Error('The active Application changed while saving engineering source.');
        }
        if (!workspace.ok || !workspace.body) {
          throw new Error(`Workspace create failed: ${workspace.status} ${workspace.text}`);
        }
        persistedWorkspace = workspace.body;
      }

      let persistedProject = resources.projects.find(
        project => project.projectId === engineeringIdentity.projectId) ?? null;
      if (!persistedProject) {
        const project = await createEngineeringProject({
          projectId: engineeringIdentity.projectId,
          workspaceId: engineeringIdentity.workspaceId,
          displayName: engineeringIdentity.projectName
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          throw new Error('The active Application changed while saving engineering source.');
        }
        if (!project.ok || !project.body) {
          throw new Error(`Configuration project create failed: ${project.status} ${project.text}`);
        }
        persistedProject = project.body;
      }

      const existingRecipe = resources.recipes.find(
        recipe => recipe.recipeId === submittedDraft.recipeId) ?? null;
      if (existingRecipe && !recipeMatchesDraft(existingRecipe, submittedDraft)) {
        throw new Error(
          `Recipe ${submittedDraft.recipeId} already exists with different immutable source. Use a new Recipe ID.`);
      }
      let persistedRecipe = existingRecipe;
      if (!persistedRecipe) {
        const recipe = await createRecipe({
          recipeId: submittedDraft.recipeId,
          versionId: submittedDraft.recipeVersionId,
          displayName: submittedDraft.recipeName,
          parameters: [
            {
              key: 'inspection.mode',
              value: 'desktop-engineering'
            }
          ]
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          throw new Error('The active Application changed while saving engineering source.');
        }
        if (!recipe.ok || !recipe.body) {
          throw new Error(`Recipe create failed: ${recipe.status} ${recipe.text}`);
        }
        persistedRecipe = recipe.body;
      }

      const existingStation = resources.stations.find(
        station => station.stationProfileId === submittedDraft.stationProfileId) ?? null;
      if (existingStation && !stationProfileMatchesDraft(existingStation, submittedDraft)) {
        throw new Error(
          `Station Profile ${submittedDraft.stationProfileId} already exists with different immutable source. Use a new Station Profile ID.`);
      }
      let persistedStation = existingStation;
      if (!persistedStation) {
        const station = await createStationProfile({
          stationProfileId: submittedDraft.stationProfileId,
          stationSystemId: submittedDraft.stationSystemId,
          displayName: submittedDraft.stationName,
          deviceBindings: [
            {
              deviceBindingId: submittedDraft.deviceBindingId,
              ownerSystemId: submittedDraft.deviceOwnerSystemId,
              capabilityId: submittedDraft.capabilityId,
              deviceKey: submittedDraft.deviceKey
            }
          ]
        }, projectApplicationApiScope);
        if (!isCurrentScope()) {
          throw new Error('The active Application changed while saving engineering source.');
        }
        if (!station.ok || !station.body) {
          throw new Error(`Station create failed: ${station.status} ${station.text}`);
        }
        persistedStation = station.body;
      }

      setResources(current => ({
        ...current,
        workspaces: replaceByIdentity(
          current.workspaces,
          persistedWorkspace,
          workspace => workspace.workspaceId),
        projects: replaceByIdentity(
          current.projects,
          persistedProject,
          project => project.projectId),
        recipes: replaceByIdentity(
          current.recipes,
          persistedRecipe,
          recipe => recipe.recipeId),
        stations: replaceByIdentity(
          current.stations,
          persistedStation,
          station => station.stationProfileId)
      }));
      setDraftState(state => acceptSubmittedEditorDraft(state, submittedDraft));
      onMessage(`Engineering source saved ${submittedDraft.recipeId} / ${submittedDraft.stationProfileId}`);
      try {
        await loadResources();
      } catch (error) {
        onMessage(`Engineering source saved; resource refresh failed: ${String(error)}`);
      }
    } catch (error) {
      try {
        await loadResources();
      } catch (refreshError) {
        onMessage(`Engineering source save failed and refresh also failed: ${String(refreshError)}`);
      }
      throw error;
    } finally {
      if (isCurrentScope()) {
        setBusy(false);
      }
    }
  }, [
    draft,
    engineeringIdentity,
    engineeringScopeKey,
    loadResources,
    onMessage,
    projectApplicationApiScope,
    resources.projects,
    resources.recipes,
    resources.stations,
    resources.workspaces
  ]);

  const createRuntimeSnapshot = useCallback(async () => {
    const process = selectedProcess;
    if (!process) {
      throw new Error('Publish a process definition before creating an engineering snapshot.');
    }
    if (!engineeringIdentity || !projectApplicationApiScope) {
      throw new Error('Open a project Application before creating a configuration snapshot.');
    }
    if (draftDirty) {
      throw new Error('Save the mutable Recipe and Station Profile source before publishing a snapshot.');
    }
    const recipe = resources.recipes.find(candidate => candidate.recipeId === draft.recipeId) ?? null;
    const station = resources.stations.find(
      candidate => candidate.stationProfileId === draft.stationProfileId) ?? null;
    if (!recipe || !recipeMatchesDraft(recipe, draft)) {
      throw new Error(`Saved Recipe ${draft.recipeId} does not match the current source fields.`);
    }
    if (!station || !stationProfileMatchesDraft(station, draft)) {
      throw new Error(`Saved Station Profile ${draft.stationProfileId} does not match the current source fields.`);
    }
    if (!resources.projects.some(project => project.projectId === engineeringIdentity.projectId)) {
      throw new Error('Save engineering source before publishing its configuration snapshot.');
    }

    const requestedScopeKey = engineeringScopeKey;
    const isCurrentScope = () => scopeKeyRef.current === requestedScopeKey;
    setBusy(true);
    try {
      if (recipe.status !== 'Published') {
        const publishedRecipe = await publishRecipe(draft.recipeId, projectApplicationApiScope);
        if (!isCurrentScope()) {
          throw new Error('The active Application changed while publishing the configuration snapshot.');
        }
        if (!publishedRecipe.ok) {
          throw new Error(`Recipe publish failed: ${publishedRecipe.status} ${publishedRecipe.text}`);
        }
      }

      const snapshot = await publishConfigurationSnapshot(engineeringIdentity.projectId, {
        snapshotId: draft.snapshotId,
        processDefinitionId: process.processDefinitionId,
        processVersionId: process.versionId,
        recipeId: draft.recipeId,
        stationProfileId: draft.stationProfileId
      }, projectApplicationApiScope);
      if (!isCurrentScope()) {
        throw new Error('The active Application changed while publishing the configuration snapshot.');
      }
      if (!snapshot.ok || !snapshot.body) {
        throw new Error(`Snapshot publish failed: ${snapshot.status} ${snapshot.text}`);
      }

      setCreatedProject(snapshot.body);
      onMessage(`Snapshot published ${draft.snapshotId}`);
      try {
        await loadResources();
      } catch (error) {
        onMessage(`Snapshot published ${draft.snapshotId}; resource refresh failed: ${String(error)}`);
      }
    } catch (error) {
      try {
        await loadResources();
      } catch (refreshError) {
        onMessage(`Snapshot publish failed and resource refresh also failed: ${String(refreshError)}`);
      }
      throw error;
    } finally {
      if (isCurrentScope()) {
        setBusy(false);
      }
    }
  }, [
    draft,
    engineeringIdentity,
    engineeringScopeKey,
    loadResources,
    onMessage,
    projectApplicationApiScope,
    resources.projects,
    resources.recipes,
    resources.stations,
    selectedProcess,
    draftDirty
  ]);

  useEditorDocument({
    dirty: draftDirty,
    editRevision: draft,
    canSave: canSaveSource && sourceProblems.length === 0,
    save: saveEngineeringSource,
    revert: revertDraft,
    focus: targetId => {
      if (targetId) {
        document.querySelector<HTMLElement>(`[data-testid="${targetId}"]`)?.focus();
      }
    },
    problems: engineeringProblems
  });

  return (
    <section className="engineering-workbench">
      <div className="panel engineering-builder-panel">
        <div className="panel-title">
          <div>
            <Factory size={17} />
            <h2>Engineering Configuration</h2>
          </div>
          <span>{activeApplication?.displayName ?? 'No application selected'}</span>
        </div>

        <div className="engineering-toolbar">
          <button
            type="button"
            className="button ghost"
            onClick={loadResources}
            disabled={!isBackendHealthy || !engineeringIdentity || busy}
          >
            <RefreshCw size={15} />
            Refresh
          </button>
          <button
            type="button"
            className="button ghost"
            onClick={resetDraft}
            disabled={!engineeringIdentity || busy}
            data-testid="new-engineering-configuration"
          >
            <SlidersHorizontal size={15} />
            New Configuration
          </button>
          <button
            type="button"
            className="button ghost"
            onClick={() => void revertDraft()}
            disabled={!draftDirty || busy}
            data-testid="discard-engineering-draft"
          >
            <RotateCcw size={15} />
            Discard Changes
          </button>
          <button
            type="button"
            className="button"
            onClick={() => void saveEngineeringSource().catch(error => onMessage(String(error)))}
            disabled={(!draftDirty && sourcePersisted) || !canSaveSource || sourceProblems.length > 0}
            data-testid="save-engineering-source"
          >
            <Save size={15} />
            Save Source
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => void createRuntimeSnapshot().catch(error => onMessage(String(error)))}
            disabled={!canPublishSnapshot || engineeringProblems.length > 0}
            data-testid="create-engineering-bundle"
          >
            <Send size={15} />
            Publish Snapshot
          </button>
        </div>

        <div className="engineering-layout">
          <div className="engineering-form">
            <div className="engineering-application-context" data-testid="engineering-application-context">
              <div>
                <FolderTree size={16} />
                <span>Current Application</span>
                <strong>{activeApplication?.displayName ?? 'Open an Application to continue'}</strong>
              </div>
              <dl>
                <div>
                  <dt>Application</dt>
                  <dd>{effectiveApplicationId ?? '—'}</dd>
                </div>
                <div>
                  <dt>Workspace</dt>
                  <dd>{engineeringIdentity?.workspaceId ?? '—'}</dd>
                </div>
                <div>
                  <dt>Configuration</dt>
                  <dd>{engineeringIdentity?.projectId ?? '—'}</dd>
                </div>
              </dl>
              <small>Workspace and configuration IDs are managed by the Application.</small>
            </div>

            <FieldGroup title="Recipe">
              <TextField
                label="Recipe ID"
                testId="engineering-recipe-id"
                value={draft.recipeId}
                onChange={value => setDraft(current => ({
                  ...current,
                  recipeId: value,
                  recipeVersionId: `${value}@1.0.0`
                }))}
              />
              <TextField
                label="Recipe Version"
                testId="engineering-recipe-version"
                value={draft.recipeVersionId}
                onChange={value => setDraft(current => ({ ...current, recipeVersionId: value }))}
              />
              <TextField
                label="Display Name"
                testId="engineering-recipe-name"
                value={draft.recipeName}
                onChange={value => setDraft(current => ({ ...current, recipeName: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Station Profile">
              <TextField
                label="Station Profile ID"
                testId="engineering-station-profile-id"
                value={draft.stationProfileId}
                onChange={value => setDraft(current => ({ ...current, stationProfileId: value }))}
              />
              <label>
                <span>Station System</span>
                <select
                  value={draft.stationSystemId}
                  onChange={event => {
                    const stationSystemId = event.target.value;
                    const owners = resources.topology?.systems.filter(system =>
                      isSystemWithinStation(system, stationSystemId, resources.topology!)) ?? [];
                    const owner = owners[0] ?? null;
                    const capabilities = owner
                      ? [...new Set([
                        ...owner.requiredCapabilityIds,
                        ...owner.providedCapabilityIds
                      ])]
                      : [];
                    setDraft(current => ({
                      ...current,
                      stationSystemId,
                      deviceOwnerSystemId: owner?.systemId ?? '',
                      capabilityId: capabilities[0] ?? ''
                    }));
                  }}
                  data-testid="engineering-station-system"
                >
                  {stationSystems.length === 0 ? (
                    <option value="">Create and link a topology Station first</option>
                  ) : stationSystems.map(system => (
                    <option key={system.systemId} value={system.systemId}>
                      {system.displayName} ({system.systemId})
                    </option>
                  ))}
                </select>
              </label>
              <TextField
                label="Display Name"
                testId="engineering-station-name"
                value={draft.stationName}
                onChange={value => setDraft(current => ({ ...current, stationName: value }))}
              />
              <TextField
                label="Device Binding ID"
                testId="engineering-device-binding-id"
                value={draft.deviceBindingId}
                onChange={value => setDraft(current => ({ ...current, deviceBindingId: value }))}
              />
              <label>
                <span>Device Owner System</span>
                <select
                  value={draft.deviceOwnerSystemId}
                  onChange={event => {
                    const deviceOwnerSystemId = event.target.value;
                    const owner = deviceOwnerSystems.find(system =>
                      system.systemId === deviceOwnerSystemId) ?? null;
                    const capabilities = owner
                      ? [...new Set([
                        ...owner.requiredCapabilityIds,
                        ...owner.providedCapabilityIds
                      ])]
                      : [];
                    setDraft(current => ({
                      ...current,
                      deviceOwnerSystemId,
                      capabilityId: capabilities.includes(current.capabilityId)
                        ? current.capabilityId
                        : capabilities[0] ?? ''
                    }));
                  }}
                  data-testid="engineering-device-owner-system"
                >
                  {deviceOwnerOptions.map(option => (
                    <option key={`owner-option:${option.value}`} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>Capability</span>
                <select
                  value={draft.capabilityId}
                  onChange={event => setDraft(current => ({
                    ...current,
                    capabilityId: event.target.value
                  }))}
                  data-testid="engineering-device-capability"
                >
                  {ownerCapabilityIds.length === 0 ? (
                    <option value="">Owner System declares no capabilities</option>
                  ) : ownerCapabilityIds.map(capabilityId => (
                    <option key={capabilityId} value={capabilityId}>{capabilityId}</option>
                  ))}
                </select>
              </label>
              <TextField
                label="Device Key"
                testId="engineering-device-key"
                value={draft.deviceKey}
                onChange={value => setDraft(current => ({ ...current, deviceKey: value }))}
              />
            </FieldGroup>

            <FieldGroup title="Configuration Snapshot">
              <label>
                <span>Published Process</span>
                <select
                  value={selectedProcess?.processDefinitionId ?? ''}
                  onChange={event => {
                    const nextProcess = publishedProcesses.find(
                      definition => definition.processDefinitionId === event.target.value);
                    setDraft(current => ({
                      ...current,
                      processDefinitionId: nextProcess?.processDefinitionId ?? '',
                      processVersionId: nextProcess?.versionId ?? ''
                    }));
                  }}
                  data-testid="engineering-process-definition"
                >
                  {publishedProcesses.length === 0 ? (
                    <option value="">No published process</option>
                  ) : publishedProcesses.map(process => (
                    <option key={process.processDefinitionId} value={process.processDefinitionId}>
                      {process.displayName}
                    </option>
                  ))}
                </select>
              </label>
              <TextField
                label="Snapshot ID"
                testId="engineering-snapshot-id"
                value={draft.snapshotId}
                onChange={value => setDraft(current => ({ ...current, snapshotId: value }))}
              />
              <TextField
                label="Process Version"
                testId="engineering-process-version"
                value={selectedProcess?.versionId ?? draft.processVersionId}
                onChange={value => setDraft(current => ({ ...current, processVersionId: value }))}
                readOnly
              />
            </FieldGroup>
          </div>

          <div className="engineering-summary">
            <ResourceColumn
              icon={Layers3}
              title="Recipes"
              count={resources.recipes.length}
              rows={resources.recipes.map(recipe => ({
                id: recipe.recipeId,
                title: recipe.displayName,
                meta: `${recipe.status} / ${recipe.versionId}`
              }))}
            />
            <ResourceColumn
              icon={Factory}
              title="Station Profiles"
              count={resources.stations.length}
              rows={resources.stations.map(station => ({
                id: station.stationProfileId,
                title: station.displayName,
                meta: `${station.deviceBindings.length} bindings`
              }))}
            />
            <ResourceColumn
              icon={GitBranch}
              title="Configuration Snapshots"
              count={configurationSnapshots.length}
              rows={configurationSnapshots.map(snapshot => ({
                id: snapshot.snapshotId,
                title: snapshot.processDefinitionId,
                meta: `${snapshot.status} / ${formatDate(snapshot.publishedAtUtc)}`
              }))}
            />
          </div>
        </div>
      </div>

      <div className="panel engineering-snapshot-panel">
        <div className="panel-title">
          <div>
            <GitBranch size={17} />
            <h2>Active Snapshot</h2>
          </div>
          <span>{createdProject?.activeSnapshotId ?? 'waiting'}</span>
        </div>
        {createdProject ? (
          <SnapshotResult project={createdProject} />
        ) : (
          <div className="engineering-empty">
            <p>{engineeringIdentity
              ? 'Select a published process and publish a configuration snapshot.'
              : 'Open a project Application to configure engineering resources.'}</p>
          </div>
        )}
      </div>
    </section>
  );
}

function FieldGroup({
  title,
  children
}: {
  title: string;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <fieldset className="engineering-fieldset">
      <legend>{title}</legend>
      {children}
    </fieldset>
  );
}

function TextField({
  label,
  value,
  testId,
  readOnly = false,
  onChange
}: {
  label: string;
  value: string;
  testId?: string;
  readOnly?: boolean;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input
        data-testid={testId}
        value={value}
        readOnly={readOnly}
        onChange={event => onChange(event.target.value)}
      />
    </label>
  );
}

function ResourceColumn({
  icon: Icon,
  title,
  count,
  rows
}: {
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  count: number;
  rows: Array<{ id: string; title: string; meta: string }>;
}): React.ReactElement {
  return (
    <section className="engineering-resource-column">
      <div>
        <Icon size={15} />
        <strong>{title}</strong>
        <span>{count}</span>
      </div>
      {rows.length === 0 ? (
        <p>No rows yet</p>
      ) : rows.slice(0, 5).map(row => (
        <article key={row.id}>
          <strong>{row.title}</strong>
          <span>{row.id}</span>
          <small>{row.meta}</small>
        </article>
      ))}
    </section>
  );
}

function SnapshotResult({ project }: { project: EngineeringProjectResponse }): React.ReactElement {
  const snapshot = project.snapshots.find(item => item.snapshotId === project.activeSnapshotId)
    ?? project.snapshots[0]
    ?? null;

  if (!snapshot) {
    return (
      <div className="engineering-empty">
        <p>Project was created without a snapshot response.</p>
      </div>
    );
  }

  return (
    <div className="snapshot-result" data-testid="engineering-result">
      <div>
        <CheckCircle2 size={18} />
        <strong>{snapshot.status}</strong>
      </div>
      <dl>
        <dt>Snapshot</dt>
        <dd>{snapshot.snapshotId}</dd>
        <dt>Project</dt>
        <dd>{snapshot.projectId}</dd>
        <dt>Process</dt>
        <dd>{snapshot.processDefinitionId}</dd>
        <dt>Recipe</dt>
        <dd>{snapshot.recipeVersionId}</dd>
        <dt>Station</dt>
        <dd>{snapshot.stationProfileId}</dd>
      </dl>
    </div>
  );
}

function createEngineeringDraft(
  process?: ProcessDefinitionSummary,
  applicationId?: string | null
): EngineeringDraft {
  const seed = Date.now().toString(36);
  const prefix = (applicationId || 'application').replace(/[^a-zA-Z0-9_-]/g, '-');
  const recipeId = `${prefix}-recipe-${seed}`;

  return {
    recipeId,
    recipeVersionId: `${recipeId}@1.0.0`,
    recipeName: 'Application Runtime Recipe',
    stationProfileId: `${prefix}-station-${seed}`,
    stationSystemId: `${applicationId || 'application'}.station.1`,
    stationName: 'Application Runtime Station',
    deviceBindingId: 'loopback-primary',
    deviceOwnerSystemId: '',
    capabilityId: 'device.loopback',
    deviceKey: 'loopback-01',
    snapshotId: `${prefix}-snapshot-${seed}`,
    processDefinitionId: process?.processDefinitionId ?? '',
    processVersionId: process?.versionId ?? ''
  };
}

function isSystemWithinStation(
  system: AutomationSystemResponse,
  stationSystemId: string,
  topology: AutomationTopologyResponse
): boolean {
  let current: AutomationSystemResponse | undefined = system;
  for (let depth = 0; current && depth <= topology.systems.length; depth += 1) {
    if (current.systemId === stationSystemId) {
      return true;
    }
    current = current.parentSystemId
      ? topology.systems.find(candidate => candidate.systemId === current?.parentSystemId)
      : undefined;
  }
  return false;
}

function recipeMatchesDraft(recipe: RecipeResponse, draft: EngineeringDraft): boolean {
  const mode = recipe.parameters.find(parameter => parameter.key === 'inspection.mode') ?? null;
  return recipe.versionId === draft.recipeVersionId
    && recipe.displayName === draft.recipeName
    && recipe.parameters.length === 1
    && mode?.value === 'desktop-engineering';
}

function stationProfileMatchesDraft(
  station: StationProfileResponse,
  draft: EngineeringDraft
): boolean {
  const binding = station.deviceBindings.length === 1 ? station.deviceBindings[0] : null;
  return station.stationSystemId === draft.stationSystemId
    && station.displayName === draft.stationName
    && binding?.deviceBindingId === draft.deviceBindingId
    && binding.ownerSystemId === draft.deviceOwnerSystemId
    && binding.capabilityId === draft.capabilityId
    && binding.deviceKey === draft.deviceKey;
}

function replaceByIdentity<T>(
  rows: T[],
  item: T,
  identity: (value: T) => string
): T[] {
  const itemIdentity = identity(item);
  return [...rows.filter(row => identity(row) !== itemIdentity), item];
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}
