import * as signalR from '@microsoft/signalr';
import type { ApiResponse } from '../shared/desktop-api';
import { desktop } from './desktop-bridge';
import type {
  AddAutomationModuleRequest,
  AddCapabilityContractRequest,
  AddDriverBindingRequest,
  AddEquipmentNodeRequest,
  AddSiteLayoutElementRequest,
  AddProjectApplicationRequest,
  AddSlotDefinitionRequest,
  AddSlotGroupRequest,
  AutomationProjectSummaryResponse,
  AutomationProjectResponse,
  AutomationTopologyResponse,
  AutomationTopologySummaryResponse,
  AutomationProjectWorkspaceResponse,
  CreateAutomationTopologyRequest,
  CreateAutomationProjectWorkspaceRequest,
  CreateDeviceDefinitionRequest,
  CreateEngineeringProjectRequest,
  CreateProcessDefinitionRequest,
  CreateRecipeRequest,
  CreateSiteLayoutRequest,
  CreateStationProfileRequest,
  CreateWorkspaceRequest,
  DeviceDefinitionResponse,
  DeviceInstanceResponse,
  DeviceStatusChangeRequest,
  EngineeringTraceSearchQuery,
  EngineeringTraceSearchResponse,
  EngineeringProjectResponse,
  HealthResponse,
  PlatformResponse,
  ExternalPluginProcessEventResponse,
  LinkProjectTopologyRequest,
  OpenAutomationProjectWorkspaceRequest,
  ProcessDefinitionResponse,
  ProcessDefinitionSummary,
  ProcessGraphValidationReport,
  ProcessBlocklyBlockDefinition,
  PluginLifecycleRecordResponse,
  PluginManagementOverviewResponse,
  PublishConfigurationSnapshotRequest,
  PublishProjectSnapshotRequest,
  RecipeResponse,
  RegisterDeviceInstanceRequest,
  SiteLayoutResponse,
  RuntimeAlarm,
  RuntimeAlarmsResponse,
  RuntimeSessionRunResponse,
  RuntimeStationStatus,
  RuntimeStationStatusesResponse,
  RuntimeTimelineEntry,
  RuntimeTimelineResponse,
  RegisterProcessBlocklyBlockDefinitionRequest,
  StartedProjectSnapshotRuntimeSessionResponse,
  StartedProcessRuntimeSessionResponse,
  StartProjectSnapshotRuntimeSessionRequest,
  StartProcessRuntimeSessionRequest,
  StartRuntimeSessionRequest,
  StationProfileResponse,
  TraceRecordExportPackageResponse,
  TraceRecordQueryResponse,
  TraceRecordResponse,
  UpdateSiteLayoutElementGeometryRequest,
  WorkspaceResponse
} from './contracts';

export async function getPlatform(): Promise<ApiResponse<PlatformResponse>> {
  return desktop.apiRequest<PlatformResponse>('/api/platform');
}

export async function getHealth(): Promise<ApiResponse<HealthResponse>> {
  return desktop.apiRequest<HealthResponse>('/health/live');
}

export async function listAutomationProjects(): Promise<AutomationProjectSummaryResponse[]> {
  const response = await desktop.apiRequest<AutomationProjectSummaryResponse[]>('/api/automation-projects');
  return response.body ?? [];
}

export async function createAutomationProjectWorkspace(
  request: CreateAutomationProjectWorkspaceRequest
): Promise<ApiResponse<AutomationProjectWorkspaceResponse>> {
  return desktop.apiRequest<AutomationProjectWorkspaceResponse>(
    '/api/automation-project-workspaces',
    {
      method: 'POST',
      body: request
    });
}

export async function openAutomationProjectWorkspace(
  request: OpenAutomationProjectWorkspaceRequest
): Promise<ApiResponse<AutomationProjectWorkspaceResponse>> {
  return desktop.apiRequest<AutomationProjectWorkspaceResponse>(
    '/api/automation-project-workspaces/open',
    {
      method: 'POST',
      body: request
    });
}

export async function saveAutomationProjectManifest(
  projectId: string
): Promise<ApiResponse<AutomationProjectWorkspaceResponse>> {
  return desktop.apiRequest<AutomationProjectWorkspaceResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/manifest`,
    {
      method: 'PUT'
    });
}

export async function addProjectApplication(
  projectId: string,
  request: AddProjectApplicationRequest
): Promise<ApiResponse<AutomationProjectResponse>> {
  return desktop.apiRequest<AutomationProjectResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/applications`,
    {
      method: 'POST',
      body: request
    });
}

export async function linkProjectTopology(
  projectId: string,
  applicationId: string,
  request: LinkProjectTopologyRequest
): Promise<ApiResponse<AutomationProjectResponse>> {
  return desktop.apiRequest<AutomationProjectResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/applications/${encodeURIComponent(applicationId)}/topology`,
    {
      method: 'PUT',
      body: request
    });
}

export async function linkProjectProcessDefinition(
  projectId: string,
  applicationId: string,
  processDefinitionId: string
): Promise<ApiResponse<AutomationProjectResponse>> {
  return desktop.apiRequest<AutomationProjectResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/applications/${encodeURIComponent(applicationId)}/process-definitions/${encodeURIComponent(processDefinitionId)}`,
    {
      method: 'PUT'
    });
}

export async function publishProjectSnapshot(
  projectId: string,
  request: PublishProjectSnapshotRequest
): Promise<ApiResponse<AutomationProjectResponse>> {
  return desktop.apiRequest<AutomationProjectResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/snapshots`,
    {
      method: 'POST',
      body: request
    });
}

export async function startProjectSnapshotRuntimeSession(
  projectId: string,
  snapshotId: string,
  request: StartProjectSnapshotRuntimeSessionRequest
): Promise<ApiResponse<StartedProjectSnapshotRuntimeSessionResponse>> {
  return desktop.apiRequest<StartedProjectSnapshotRuntimeSessionResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/snapshots/${encodeURIComponent(snapshotId)}/runtime-sessions`,
    {
      method: 'POST',
      body: request
    });
}

export interface ProjectApplicationApiScope {
  projectId: string;
  applicationId: string;
}

export async function listAutomationTopologies(
  scope?: ProjectApplicationApiScope
): Promise<AutomationTopologySummaryResponse[]> {
  const response = await desktop.apiRequest<AutomationTopologySummaryResponse[]>(
    topologyCollectionPath(scope));
  return response.body ?? [];
}

export async function getAutomationTopology(
  topologyId: string,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}`);
}

export async function createAutomationTopology(
  request: CreateAutomationTopologyRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    topologyCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function addEquipmentNode(
  topologyId: string,
  request: AddEquipmentNodeRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/nodes`,
    {
      method: 'POST',
      body: request
    });
}

export async function addCapabilityContract(
  topologyId: string,
  request: AddCapabilityContractRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/capabilities`,
    {
      method: 'POST',
      body: request
    });
}

export async function addAutomationModule(
  topologyId: string,
  request: AddAutomationModuleRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/modules`,
    {
      method: 'POST',
      body: request
    });
}

export async function addDriverBinding(
  topologyId: string,
  request: AddDriverBindingRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/driver-bindings`,
    {
      method: 'POST',
      body: request
    });
}

export async function addSlotGroup(
  topologyId: string,
  request: AddSlotGroupRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slot-groups`,
    {
      method: 'POST',
      body: request
    });
}

export async function addSlotDefinition(
  topologyId: string,
  request: AddSlotDefinitionRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slots`,
    {
      method: 'POST',
      body: request
    });
}

export async function getSiteLayout(
  layoutId: string,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}`);
}

export async function createSiteLayout(
  request: CreateSiteLayoutRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    siteLayoutCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function addSiteLayoutElement(
  layoutId: string,
  request: AddSiteLayoutElementRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}/elements`,
    {
      method: 'POST',
      body: request
    });
}

export async function updateSiteLayoutElementGeometry(
  layoutId: string,
  elementId: string,
  request: UpdateSiteLayoutElementGeometryRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}/elements/${encodeURIComponent(elementId)}/geometry`,
    {
      method: 'PUT',
      body: request
    });
}

function topologyCollectionPath(scope?: ProjectApplicationApiScope): string {
  return scope
    ? `${projectApplicationPath(scope)}/topologies`
    : '/api/automation-topologies';
}

function siteLayoutCollectionPath(scope?: ProjectApplicationApiScope): string {
  return scope
    ? `${projectApplicationPath(scope)}/layouts`
    : '/api/site-layouts';
}

function projectApplicationPath(scope: ProjectApplicationApiScope): string {
  return `/api/automation-projects/${encodeURIComponent(scope.projectId)}/applications/${encodeURIComponent(scope.applicationId)}`;
}

function engineeringPath(scope?: ProjectApplicationApiScope): string {
  return scope
    ? `${projectApplicationPath(scope)}/engineering`
    : '/api/engineering';
}

export async function getStationStatuses(): Promise<RuntimeStationStatus[]> {
  const response = await desktop.apiRequest<RuntimeStationStatusesResponse>(
    '/api/runtime/monitoring/stations');
  return response.body?.items ?? [];
}

export async function getAlarms(includeAcknowledged = false): Promise<RuntimeAlarm[]> {
  const response = await desktop.apiRequest<RuntimeAlarmsResponse>(
    `/api/runtime/monitoring/alarms?includeAcknowledged=${includeAcknowledged}`);
  return response.body?.items ?? [];
}

export async function getTimeline(sessionId: string): Promise<RuntimeTimelineEntry[]> {
  const response = await desktop.apiRequest<RuntimeTimelineResponse>(
    `/api/runtime/monitoring/sessions/${sessionId}/timeline`);
  return response.body?.items ?? [];
}

export async function getTraceRecords(serialNumber?: string): Promise<TraceRecordQueryResponse | null> {
  const query = serialNumber ? `?serialNumber=${encodeURIComponent(serialNumber)}` : '';
  const response = await desktop.apiRequest<TraceRecordQueryResponse>(
    `/api/traceability/records${query}`);
  return response.body;
}

export async function searchEngineeringTrace(
  query: EngineeringTraceSearchQuery
): Promise<EngineeringTraceSearchResponse | null> {
  const response = await desktop.apiRequest<EngineeringTraceSearchResponse>(
    `/api/traceability/read-models/engineering-search${toQueryString(query)}`);
  return response.body;
}

export async function getTraceRecord(traceRecordId: string): Promise<TraceRecordResponse | null> {
  const response = await desktop.apiRequest<TraceRecordResponse>(
    `/api/traceability/records/${encodeURIComponent(traceRecordId)}`);
  return response.body;
}

export async function exportTraceRecord(
  traceRecordId: string
): Promise<TraceRecordExportPackageResponse | null> {
  const response = await desktop.apiRequest<TraceRecordExportPackageResponse>(
    `/api/traceability/records/${encodeURIComponent(traceRecordId)}/export`);
  return response.body;
}

export async function acknowledgeAlarm(alarmId: string, acknowledgedBy: string): Promise<RuntimeAlarm | null> {
  const response = await desktop.apiRequest<RuntimeAlarm>(
    `/api/runtime/monitoring/alarms/${alarmId}/acknowledgements`,
    {
      method: 'POST',
      body: { acknowledgedBy }
    });
  return response.body;
}

export async function listWorkspaces(
  scope?: ProjectApplicationApiScope
): Promise<WorkspaceResponse[]> {
  const response = await desktop.apiRequest<WorkspaceResponse[]>(
    `${engineeringPath(scope)}/workspaces`);
  return response.body ?? [];
}

export async function createWorkspace(
  request: CreateWorkspaceRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<WorkspaceResponse>> {
  return desktop.apiRequest<WorkspaceResponse>(
    `${engineeringPath(scope)}/workspaces`,
    {
      method: 'POST',
      body: request
    });
}

export async function listEngineeringProjects(
  scope?: ProjectApplicationApiScope
): Promise<EngineeringProjectResponse[]> {
  const response = await desktop.apiRequest<EngineeringProjectResponse[]>(
    `${engineeringPath(scope)}/projects`);
  return response.body ?? [];
}

export async function createEngineeringProject(
  request: CreateEngineeringProjectRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<EngineeringProjectResponse>> {
  return desktop.apiRequest<EngineeringProjectResponse>(
    `${engineeringPath(scope)}/projects`,
    {
      method: 'POST',
      body: request
    });
}

export async function listRecipes(
  scope?: ProjectApplicationApiScope
): Promise<RecipeResponse[]> {
  const response = await desktop.apiRequest<RecipeResponse[]>(
    `${engineeringPath(scope)}/recipes`);
  return response.body ?? [];
}

export async function createRecipe(
  request: CreateRecipeRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<RecipeResponse>> {
  return desktop.apiRequest<RecipeResponse>(
    `${engineeringPath(scope)}/recipes`,
    {
      method: 'POST',
      body: request
    });
}

export async function publishRecipe(
  recipeId: string,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<RecipeResponse>> {
  return desktop.apiRequest<RecipeResponse>(
    `${engineeringPath(scope)}/recipes/${encodeURIComponent(recipeId)}/publish`,
    {
      method: 'POST'
    });
}

export async function listStationProfiles(
  scope?: ProjectApplicationApiScope
): Promise<StationProfileResponse[]> {
  const response = await desktop.apiRequest<StationProfileResponse[]>(
    `${engineeringPath(scope)}/station-profiles`);
  return response.body ?? [];
}

export async function createStationProfile(
  request: CreateStationProfileRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<StationProfileResponse>> {
  return desktop.apiRequest<StationProfileResponse>(
    `${engineeringPath(scope)}/station-profiles`,
    {
      method: 'POST',
      body: request
    });
}

export async function publishConfigurationSnapshot(
  projectId: string,
  request: PublishConfigurationSnapshotRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<EngineeringProjectResponse>> {
  return desktop.apiRequest<EngineeringProjectResponse>(
    `${engineeringPath(scope)}/projects/${encodeURIComponent(projectId)}/configuration-snapshots`,
    {
      method: 'POST',
      body: request
    });
}

export async function listDeviceDefinitions(): Promise<DeviceDefinitionResponse[]> {
  const response = await desktop.apiRequest<DeviceDefinitionResponse[]>('/api/devices/definitions');
  return response.body ?? [];
}

export async function createDeviceDefinition(
  request: CreateDeviceDefinitionRequest
): Promise<ApiResponse<DeviceDefinitionResponse>> {
  return desktop.apiRequest<DeviceDefinitionResponse>(
    '/api/devices/definitions',
    {
      method: 'POST',
      body: request
    });
}

export async function listDeviceInstances(): Promise<DeviceInstanceResponse[]> {
  const response = await desktop.apiRequest<DeviceInstanceResponse[]>('/api/devices/instances');
  return response.body ?? [];
}

export async function registerDeviceInstance(
  request: RegisterDeviceInstanceRequest
): Promise<ApiResponse<DeviceInstanceResponse>> {
  return desktop.apiRequest<DeviceInstanceResponse>(
    '/api/devices/instances',
    {
      method: 'POST',
      body: request
    });
}

export async function connectDeviceInstance(
  deviceInstanceId: string
): Promise<ApiResponse<DeviceInstanceResponse>> {
  return desktop.apiRequest<DeviceInstanceResponse>(
    `/api/devices/instances/${encodeURIComponent(deviceInstanceId)}/connect`,
    {
      method: 'POST'
    });
}

export async function disconnectDeviceInstance(
  deviceInstanceId: string,
  request: DeviceStatusChangeRequest
): Promise<ApiResponse<DeviceInstanceResponse>> {
  return desktop.apiRequest<DeviceInstanceResponse>(
    `/api/devices/instances/${encodeURIComponent(deviceInstanceId)}/disconnect`,
    {
      method: 'POST',
      body: request
    });
}

export async function faultDeviceInstance(
  deviceInstanceId: string,
  request: DeviceStatusChangeRequest
): Promise<ApiResponse<DeviceInstanceResponse>> {
  return desktop.apiRequest<DeviceInstanceResponse>(
    `/api/devices/instances/${encodeURIComponent(deviceInstanceId)}/faults`,
    {
      method: 'POST',
      body: request
    });
}

export async function resetDeviceFault(
  deviceInstanceId: string
): Promise<ApiResponse<DeviceInstanceResponse>> {
  return desktop.apiRequest<DeviceInstanceResponse>(
    `/api/devices/instances/${encodeURIComponent(deviceInstanceId)}/fault-reset`,
    {
      method: 'POST'
    });
}

export async function getPluginOverview(): Promise<PluginManagementOverviewResponse | null> {
  const response = await desktop.apiRequest<PluginManagementOverviewResponse>('/api/plugins/overview');
  return response.body;
}

export async function startPlugins(): Promise<ApiResponse<PluginLifecycleRecordResponse[]>> {
  return desktop.apiRequest<PluginLifecycleRecordResponse[]>(
    '/api/plugins/lifecycle/start',
    {
      method: 'POST'
    });
}

export async function stopPlugins(): Promise<ApiResponse<PluginLifecycleRecordResponse[]>> {
  return desktop.apiRequest<PluginLifecycleRecordResponse[]>(
    '/api/plugins/lifecycle/stop',
    {
      method: 'POST'
    });
}

export async function listPluginEvents(
  pluginId?: string,
  kind?: string,
  take = 50
): Promise<ExternalPluginProcessEventResponse[]> {
  const query = toQueryString({ pluginId, kind, take });
  const response = await desktop.apiRequest<ExternalPluginProcessEventResponse[]>(
    `/api/plugins/process-events${query}`);
  return response.body ?? [];
}

export async function listProcessDefinitions(
  scope?: ProjectApplicationApiScope
): Promise<ProcessDefinitionSummary[]> {
  const response = await desktop.apiRequest<ProcessDefinitionSummary[]>(processCollectionPath(scope));
  return response.body ?? [];
}

export async function listProcessBlocklyBlocks(
  scope?: ProjectApplicationApiScope
): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>(
    processBlockCollectionPath(scope));
  return response.body ?? [];
}

export async function listProcessBlocklyBlockVersions(
  blockType: string,
  scope?: ProjectApplicationApiScope
): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>(
    `${processBlockCollectionPath(scope)}/${encodeURIComponent(blockType)}/versions`);
  return response.body ?? [];
}

export async function registerProcessBlocklyBlock(
  request: RegisterProcessBlocklyBlockDefinitionRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessBlocklyBlockDefinition>> {
  return desktop.apiRequest<ProcessBlocklyBlockDefinition>(
    processBlockCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function createProcessDefinition(
  request: CreateProcessDefinitionRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    processCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function updateProcessDefinition(
  processDefinitionId: string,
  request: CreateProcessDefinitionRequest,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}`,
    {
      method: 'PUT',
      body: request
    });
}

export async function getProcessDefinition(
  processDefinitionId: string,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}`);
}

export async function publishProcessDefinition(
  processDefinitionId: string,
  scope?: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}/publish`,
    {
      method: 'POST'
    });
}

export async function validateProcessDefinition(
  processDefinitionId: string,
  scope?: ProjectApplicationApiScope
): Promise<ProcessGraphValidationReport | null> {
  const response = await desktop.apiRequest<ProcessGraphValidationReport>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}/validation`);
  return response.body;
}

function processCollectionPath(scope?: ProjectApplicationApiScope): string {
  return scope
    ? `${projectApplicationPath(scope)}/processes`
    : '/api/process-definitions';
}

function processBlockCollectionPath(scope?: ProjectApplicationApiScope): string {
  return scope
    ? `${projectApplicationPath(scope)}/process-blocks`
    : '/api/process-blocks';
}

export async function startProcessRuntimeSession(
  processDefinitionId: string,
  request: StartProcessRuntimeSessionRequest
): Promise<ApiResponse<StartedProcessRuntimeSessionResponse>> {
  return desktop.apiRequest<StartedProcessRuntimeSessionResponse>(
    `/api/process-definitions/${encodeURIComponent(processDefinitionId)}/runtime-sessions`,
    {
      method: 'POST',
      body: request
    });
}

export async function startDemoRuntimeSession(seed: string): Promise<RuntimeSessionRunResponse | null> {
  const request: StartRuntimeSessionRequest = {
    stationId: `station-desktop-${seed}`,
    configurationSnapshotId: `snapshot-desktop-${seed}`,
    recipeSnapshotId: `recipe-desktop-${seed}`,
    processDefinitionId: `process-desktop-${seed}`,
    processVersionId: `process-desktop-${seed}@1.0.0`,
    serialNumber: `SN-${seed.toUpperCase()}`,
    batchId: `batch-${seed}`,
    fixtureId: `fixture-${seed}`,
    deviceId: `device-${seed}`,
    actorId: 'desktop-operator',
    nodes: [
      {
        nodeId: 'node-scan',
        displayName: 'Scan barcode',
        targetCapability: 'device.scanner',
        commandName: 'Scan',
        timeoutSeconds: 30,
        inputPayload: 'scan-ok'
      },
      {
        nodeId: 'node-measure',
        displayName: 'Measure voltage',
        targetCapability: 'device.multimeter',
        commandName: 'MeasureVoltage',
        timeoutSeconds: 30,
        inputPayload: Math.random() > 0.75 ? 'fail' : 'measure-ok'
      }
    ]
  };

  const response = await desktop.apiRequest<RuntimeSessionRunResponse>(
    '/api/runtime/sessions/simulated',
    {
      method: 'POST',
      body: request
    });

  return response.body;
}

export function createRuntimeHubConnection(apiBaseUrl: string): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${apiBaseUrl}/hubs/runtime-progress`)
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

function toQueryString(query: object): string {
  const parameters = new URLSearchParams();
  for (const [key, value] of Object.entries(query) as Array<[string, string | number | undefined]>) {
    if (value === undefined || value === '') {
      continue;
    }

    parameters.set(key, String(value));
  }

  const text = parameters.toString();
  return text ? `?${text}` : '';
}
