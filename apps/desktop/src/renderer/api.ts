import * as signalR from '@microsoft/signalr';
import type { ApiResponse } from '../shared/desktop-api';
import { desktop } from './desktop-bridge';
import type {
  CreateDeviceDefinitionRequest,
  CreateEngineeringProjectRequest,
  CreateProcessDefinitionRequest,
  CreateRecipeRequest,
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
  ProcessDefinitionResponse,
  ProcessDefinitionSummary,
  ProcessGraphValidationReport,
  ProcessBlocklyBlockDefinition,
  PluginLifecycleRecordResponse,
  PluginManagementOverviewResponse,
  PublishConfigurationSnapshotRequest,
  RecipeResponse,
  RegisterDeviceInstanceRequest,
  RuntimeAlarm,
  RuntimeAlarmsResponse,
  RuntimeSessionRunResponse,
  RuntimeStationStatus,
  RuntimeStationStatusesResponse,
  RuntimeTimelineEntry,
  RuntimeTimelineResponse,
  RegisterProcessBlocklyBlockDefinitionRequest,
  StartedProcessRuntimeSessionResponse,
  StartProcessRuntimeSessionRequest,
  StartRuntimeSessionRequest,
  StationProfileResponse,
  TraceRecordExportPackageResponse,
  TraceRecordQueryResponse,
  TraceRecordResponse,
  WorkspaceResponse
} from './contracts';

export async function getPlatform(): Promise<ApiResponse<PlatformResponse>> {
  return desktop.apiRequest<PlatformResponse>('/api/platform');
}

export async function getHealth(): Promise<ApiResponse<HealthResponse>> {
  return desktop.apiRequest<HealthResponse>('/health/live');
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

export async function listWorkspaces(): Promise<WorkspaceResponse[]> {
  const response = await desktop.apiRequest<WorkspaceResponse[]>('/api/engineering/workspaces');
  return response.body ?? [];
}

export async function createWorkspace(
  request: CreateWorkspaceRequest
): Promise<ApiResponse<WorkspaceResponse>> {
  return desktop.apiRequest<WorkspaceResponse>(
    '/api/engineering/workspaces',
    {
      method: 'POST',
      body: request
    });
}

export async function listEngineeringProjects(): Promise<EngineeringProjectResponse[]> {
  const response = await desktop.apiRequest<EngineeringProjectResponse[]>('/api/engineering/projects');
  return response.body ?? [];
}

export async function createEngineeringProject(
  request: CreateEngineeringProjectRequest
): Promise<ApiResponse<EngineeringProjectResponse>> {
  return desktop.apiRequest<EngineeringProjectResponse>(
    '/api/engineering/projects',
    {
      method: 'POST',
      body: request
    });
}

export async function listRecipes(): Promise<RecipeResponse[]> {
  const response = await desktop.apiRequest<RecipeResponse[]>('/api/engineering/recipes');
  return response.body ?? [];
}

export async function createRecipe(
  request: CreateRecipeRequest
): Promise<ApiResponse<RecipeResponse>> {
  return desktop.apiRequest<RecipeResponse>(
    '/api/engineering/recipes',
    {
      method: 'POST',
      body: request
    });
}

export async function publishRecipe(recipeId: string): Promise<ApiResponse<RecipeResponse>> {
  return desktop.apiRequest<RecipeResponse>(
    `/api/engineering/recipes/${encodeURIComponent(recipeId)}/publish`,
    {
      method: 'POST'
    });
}

export async function listStationProfiles(): Promise<StationProfileResponse[]> {
  const response = await desktop.apiRequest<StationProfileResponse[]>('/api/engineering/station-profiles');
  return response.body ?? [];
}

export async function createStationProfile(
  request: CreateStationProfileRequest
): Promise<ApiResponse<StationProfileResponse>> {
  return desktop.apiRequest<StationProfileResponse>(
    '/api/engineering/station-profiles',
    {
      method: 'POST',
      body: request
    });
}

export async function publishConfigurationSnapshot(
  projectId: string,
  request: PublishConfigurationSnapshotRequest
): Promise<ApiResponse<EngineeringProjectResponse>> {
  return desktop.apiRequest<EngineeringProjectResponse>(
    `/api/engineering/projects/${encodeURIComponent(projectId)}/configuration-snapshots`,
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

export async function listProcessDefinitions(): Promise<ProcessDefinitionSummary[]> {
  const response = await desktop.apiRequest<ProcessDefinitionSummary[]>('/api/process-definitions');
  return response.body ?? [];
}

export async function listProcessBlocklyBlocks(): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>('/api/process-blocks');
  return response.body ?? [];
}

export async function listProcessBlocklyBlockVersions(
  blockType: string
): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>(
    `/api/process-blocks/${encodeURIComponent(blockType)}/versions`);
  return response.body ?? [];
}

export async function registerProcessBlocklyBlock(
  request: RegisterProcessBlocklyBlockDefinitionRequest
): Promise<ApiResponse<ProcessBlocklyBlockDefinition>> {
  return desktop.apiRequest<ProcessBlocklyBlockDefinition>(
    '/api/process-blocks',
    {
      method: 'POST',
      body: request
    });
}

export async function createProcessDefinition(
  request: CreateProcessDefinitionRequest
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    '/api/process-definitions',
    {
      method: 'POST',
      body: request
    });
}

export async function getProcessDefinition(
  processDefinitionId: string
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `/api/process-definitions/${encodeURIComponent(processDefinitionId)}`);
}

export async function publishProcessDefinition(
  processDefinitionId: string
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `/api/process-definitions/${encodeURIComponent(processDefinitionId)}/publish`,
    {
      method: 'POST'
    });
}

export async function validateProcessDefinition(
  processDefinitionId: string
): Promise<ProcessGraphValidationReport | null> {
  const response = await desktop.apiRequest<ProcessGraphValidationReport>(
    `/api/process-definitions/${encodeURIComponent(processDefinitionId)}/validation`);
  return response.body;
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
