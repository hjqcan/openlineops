import * as signalR from '@microsoft/signalr';
import type { ApiResponse, EditorDocumentWriteOptions } from '../shared/desktop-api';
import { desktop } from './desktop-bridge';
import {
  requireApiItemsResponse,
  requireApiResponseBody
} from './runtime-monitoring-refresh-model';
import type {
  ActiveProductionRunsResponse,
  AddAutomationSystemRequest,
  AddCapabilityContractRequest,
  AddDriverBindingRequest,
  UpdateDriverBindingRequest,
  AddSiteLayoutElementRequest,
  AddProjectApplicationRequest,
  ImportProjectApplicationRequest,
  AddSlotDefinitionRequest,
  AddSlotGroupRequest,
  AutomationProjectSummaryResponse,
  AutomationProjectResponse,
  AutomationTopologyResponse,
  AutomationTopologySummaryResponse,
  AutomationProjectWorkspaceResponse,
  ApplicationExtensionPackageResponse,
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
  ExternalProgramResourceResponse,
  ExternalProgramTrialRequest,
  ExternalProgramTrialResponse,
  HealthResponse,
  PlatformResponse,
  LinkProjectTopologyRequest,
  OpenAutomationProjectWorkspaceRequest,
  ProcessDefinitionResponse,
  ProcessDefinitionSummary,
  ProcessGraphValidationReport,
  ProcessBlocklyBlockDefinition,
  OperatorProductionRunCommand,
  ProductionLineResponse,
  ProductionLineRuntimeStateResponse,
  ProductionLineSummaryResponse,
  ProductionOperationsFilters,
  ProductionRunCommandRequest,
  ProductionRunReadModel,
  ProductionUnitMaterialLifecycleResponse,
  ProductionUnitResponse,
  ProjectReleaseProductionRunContextResponse,
  PublishConfigurationSnapshotRequest,
  PublishProjectSnapshotRequest,
  RecipeResponse,
  RegisterDeviceInstanceRequest,
  RegisterProductionUnitRequest,
  RequestStationEmergencyStopRequest,
  SiteLayoutResponse,
  RuntimeAlarm,
  RuntimeAlarmsResponse,
  RuntimeMonitoringScope,
  RuntimeStationStatus,
  RuntimeStationStatusesResponse,
  RuntimeTargetStatus,
  RuntimeTargetStatusesResponse,
  RuntimeTimelineEntry,
  RuntimeTimelineResponse,
  SaveProductionLineRequest,
  SaveExternalProgramResourceRequest,
  RegisterProcessBlocklyBlockDefinitionRequest,
  SubmitProductionRunRequest,
  MaterialArrivalRequest,
  StationProfileResponse,
  StationEmergencyStopResponse,
  StationSafetyEventsResponse,
  StationSafetyTraceSearchResponse,
  TraceRecordExportPackageResponse,
  TraceRecordQueryResponse,
  TraceRecordResponse,
  TopologyTargetDeletionResponse,
  UpdateAutomationSystemRequest,
  UpdateSiteLayoutElementGeometryRequest,
  UpdateSlotDefinitionRequest,
  UpdateSlotGroupRequest,
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
  return requireListResponse(response, 'List automation Projects');
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

export async function importProjectApplication(
  projectId: string,
  request: ImportProjectApplicationRequest
): Promise<ApiResponse<AutomationProjectWorkspaceResponse>> {
  return desktop.apiRequest<AutomationProjectWorkspaceResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/applications/import`,
    {
      method: 'POST',
      body: request
    }
  );
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

export async function getProjectSnapshotProductionRunContext(
  projectId: string,
  snapshotId: string
): Promise<ApiResponse<ProjectReleaseProductionRunContextResponse>> {
  return desktop.apiRequest<ProjectReleaseProductionRunContextResponse>(
    `/api/automation-projects/${encodeURIComponent(projectId)}/snapshots/${encodeURIComponent(snapshotId)}/production-run-context`);
}

export async function submitProductionRun(
  request: SubmitProductionRunRequest
): Promise<ApiResponse<ProductionRunReadModel>> {
  return desktop.apiRequest<ProductionRunReadModel>('/api/production-runs', {
    method: 'POST',
    body: request
  });
}

export async function getProductionUnit(
  productionUnitId: string
): Promise<ApiResponse<ProductionUnitResponse>> {
  return desktop.apiRequest<ProductionUnitResponse>(
    `/api/production-units/${encodeURIComponent(productionUnitId)}`);
}

export async function registerProductionUnit(
  request: RegisterProductionUnitRequest
): Promise<ApiResponse<ProductionUnitResponse>> {
  return desktop.apiRequest<ProductionUnitResponse>('/api/production-units', {
    method: 'POST',
    body: request
  });
}

export async function arriveProductionUnit(
  productionUnitId: string,
  request: MaterialArrivalRequest
): Promise<ApiResponse<ProductionUnitResponse>> {
  return desktop.apiRequest<ProductionUnitResponse>(
    `/api/production-units/${encodeURIComponent(productionUnitId)}/arrivals`,
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
  scope: ProjectApplicationApiScope
): Promise<AutomationTopologySummaryResponse[]> {
  const response = await desktop.apiRequest<AutomationTopologySummaryResponse[]>(
    topologyCollectionPath(scope));
  return requireListResponse(response, 'List automation topologies');
}

export async function getAutomationTopology(
  topologyId: string,
  scope: ProjectApplicationApiScope,
  snapshotId?: string
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}${toQueryString({ snapshotId })}`);
}

export async function createAutomationTopology(
  request: CreateAutomationTopologyRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    topologyCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function addAutomationSystem(
  topologyId: string,
  request: AddAutomationSystemRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/systems`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function updateAutomationSystem(
  topologyId: string,
  systemId: string,
  request: UpdateAutomationSystemRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/systems/${encodeURIComponent(systemId)}`,
    {
      method: 'PATCH',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function deleteAutomationSystem(
  topologyId: string,
  systemId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<TopologyTargetDeletionResponse>> {
  return desktop.apiRequest<TopologyTargetDeletionResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/systems/${encodeURIComponent(systemId)}`,
    { method: 'DELETE', headers: editorDocumentHeaders(write) });
}

export async function addCapabilityContract(
  topologyId: string,
  request: AddCapabilityContractRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/capabilities`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function addDriverBinding(
  topologyId: string,
  request: AddDriverBindingRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/driver-bindings`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function updateDriverBinding(
  topologyId: string,
  bindingId: string,
  request: UpdateDriverBindingRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/driver-bindings/${encodeURIComponent(bindingId)}`,
    {
      method: 'PUT',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function deleteDriverBinding(
  topologyId: string,
  bindingId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/driver-bindings/${encodeURIComponent(bindingId)}`,
    { method: 'DELETE', headers: editorDocumentHeaders(write) });
}

export async function addSlotGroup(
  topologyId: string,
  request: AddSlotGroupRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slot-groups`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function updateSlotGroup(
  topologyId: string,
  slotGroupId: string,
  request: UpdateSlotGroupRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slot-groups/${encodeURIComponent(slotGroupId)}`,
    {
      method: 'PATCH',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function deleteSlotGroup(
  topologyId: string,
  slotGroupId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<TopologyTargetDeletionResponse>> {
  return desktop.apiRequest<TopologyTargetDeletionResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slot-groups/${encodeURIComponent(slotGroupId)}`,
    { method: 'DELETE', headers: editorDocumentHeaders(write) });
}

export async function addSlotDefinition(
  topologyId: string,
  request: AddSlotDefinitionRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slots`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function updateSlotDefinition(
  topologyId: string,
  slotId: string,
  request: UpdateSlotDefinitionRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<AutomationTopologyResponse>> {
  return desktop.apiRequest<AutomationTopologyResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slots/${encodeURIComponent(slotId)}`,
    {
      method: 'PATCH',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function deleteSlotDefinition(
  topologyId: string,
  slotId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<TopologyTargetDeletionResponse>> {
  return desktop.apiRequest<TopologyTargetDeletionResponse>(
    `${topologyCollectionPath(scope)}/${encodeURIComponent(topologyId)}/slots/${encodeURIComponent(slotId)}`,
    { method: 'DELETE', headers: editorDocumentHeaders(write) });
}

export async function getSiteLayout(
  layoutId: string,
  scope: ProjectApplicationApiScope,
  snapshotId?: string
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}${toQueryString({ snapshotId })}`);
}

export async function createSiteLayout(
  request: CreateSiteLayoutRequest,
  scope: ProjectApplicationApiScope
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
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}/elements`,
    {
      method: 'POST',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function updateSiteLayoutElementGeometry(
  layoutId: string,
  elementId: string,
  request: UpdateSiteLayoutElementGeometryRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<SiteLayoutResponse>> {
  return desktop.apiRequest<SiteLayoutResponse>(
    `${siteLayoutCollectionPath(scope)}/${encodeURIComponent(layoutId)}/elements/${encodeURIComponent(elementId)}/geometry`,
    {
      method: 'PUT',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

function topologyCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/topologies`;
}

function siteLayoutCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/layouts`;
}

function projectApplicationPath(scope: ProjectApplicationApiScope): string {
  return `/api/automation-projects/${encodeURIComponent(scope.projectId)}/applications/${encodeURIComponent(scope.applicationId)}`;
}

function engineeringPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/engineering`;
}

export async function getStationStatuses(
  scope: RuntimeMonitoringScope
): Promise<RuntimeStationStatus[]> {
  const response = await desktop.apiRequest<RuntimeStationStatusesResponse>(
    `/api/runtime/monitoring/stations?${runtimeMonitoringQuery(scope)}`);
  return requireApiItemsResponse(response, 'Load runtime Station projection');
}

export async function getTargetStatuses(
  scope: RuntimeMonitoringScope,
  stationSystemIds?: readonly string[]
): Promise<RuntimeTargetStatus[]> {
  const uniqueStationSystemIds = [...new Set(
    (stationSystemIds ?? []).filter(stationSystemId => stationSystemId.length > 0))];
  const paths = uniqueStationSystemIds.length === 0
    ? [`/api/runtime/monitoring/targets?${runtimeMonitoringQuery(scope)}`]
    : uniqueStationSystemIds.map(stationSystemId =>
      `/api/runtime/monitoring/targets?${runtimeMonitoringQuery(scope, stationSystemId)}`);
  const responses = await Promise.all(paths.map(path =>
    desktop.apiRequest<RuntimeTargetStatusesResponse>(path)));

  return responses.flatMap((response, index) => requireApiItemsResponse(
    response,
    `Load runtime target projection (${index + 1}/${responses.length})`));
}

function runtimeMonitoringQuery(
  scope: RuntimeMonitoringScope,
  stationSystemId?: string
): string {
  const query = new URLSearchParams({
    projectId: scope.projectId,
    applicationId: scope.applicationId,
    projectSnapshotId: scope.projectSnapshotId,
    topologyId: scope.topologyId,
    productionRunId: scope.productionRunId
  });
  if (stationSystemId !== undefined) {
    query.set('stationSystemId', stationSystemId);
  }

  return query.toString();
}

export async function getAlarms(includeAcknowledged = false): Promise<RuntimeAlarm[]> {
  const response = await desktop.apiRequest<RuntimeAlarmsResponse>(
    `/api/runtime/monitoring/alarms?includeAcknowledged=${includeAcknowledged}`);
  return requireApiItemsResponse(response, 'Load runtime Alarm projection');
}

export async function getTimeline(
  sessionId: string,
  scope: RuntimeMonitoringScope
): Promise<RuntimeTimelineEntry[]> {
  const response = await desktop.apiRequest<RuntimeTimelineResponse>(
    `/api/runtime/monitoring/sessions/${sessionId}/timeline?${runtimeMonitoringQuery(scope)}`);
  return requireApiItemsResponse(response, 'Load runtime timeline projection');
}

export async function getTraceRecords(productionUnitIdentityValue?: string): Promise<TraceRecordQueryResponse> {
  const query = productionUnitIdentityValue
    ? `?productionUnitIdentityValue=${encodeURIComponent(productionUnitIdentityValue)}`
    : '';
  const response = await desktop.apiRequest<TraceRecordQueryResponse>(
    `/api/traceability/records${query}`);
  return requireApiResponseBody(response, 'Load Trace projection');
}

export async function searchEngineeringTrace(
  query: EngineeringTraceSearchQuery
): Promise<EngineeringTraceSearchResponse | null> {
  const response = await desktop.apiRequest<EngineeringTraceSearchResponse>(
    `/api/traceability/read-models/engineering-search${toQueryString(query)}`);
  return response.body;
}

export async function searchStationSafetyTrace({
  projectId,
  applicationId,
  projectSnapshotId,
  stationSystemId
}: {
  projectId: string;
  applicationId: string;
  projectSnapshotId?: string;
  stationSystemId?: string;
}): Promise<StationSafetyTraceSearchResponse | null> {
  const query = new URLSearchParams({ projectId, applicationId });
  if (projectSnapshotId) {
    query.set('projectSnapshotId', projectSnapshotId);
  }
  if (stationSystemId) {
    query.set('stationSystemId', stationSystemId);
  }
  const response = await desktop.apiRequest<StationSafetyTraceSearchResponse>(
    `/api/traceability/station-safety-evidence?${query.toString()}`);
  return response.body;
}

export async function getTraceRecord(traceRecordId: string): Promise<TraceRecordResponse | null> {
  const response = await desktop.apiRequest<TraceRecordResponse>(
    `/api/traceability/records/${encodeURIComponent(traceRecordId)}`);
  return response.body;
}

export async function getProductionUnitMaterialLifecycle(
  productionUnitId: string
): Promise<ProductionUnitMaterialLifecycleResponse | null> {
  const response = await desktop.apiRequest<ProductionUnitMaterialLifecycleResponse>(
    `/api/traceability/production-units/${encodeURIComponent(productionUnitId)}/material-lifecycle`);
  return response.body;
}

export async function exportTraceRecord(
  traceRecordId: string
): Promise<TraceRecordExportPackageResponse | null> {
  const response = await desktop.apiRequest<TraceRecordExportPackageResponse>(
    `/api/traceability/records/${encodeURIComponent(traceRecordId)}/export`);
  return response.body;
}

export async function acknowledgeAlarm(alarmId: string): Promise<RuntimeAlarm | null> {
  const response = await desktop.apiRequest<RuntimeAlarm>(
    `/api/runtime/monitoring/alarms/${alarmId}/acknowledgements`,
    {
      method: 'POST',
      body: {}
    });
  return response.body;
}

export async function listWorkspaces(
  scope: ProjectApplicationApiScope
): Promise<WorkspaceResponse[]> {
  const response = await desktop.apiRequest<WorkspaceResponse[]>(
    `${engineeringPath(scope)}/workspaces`);
  return requireListResponse(response, 'List engineering workspaces');
}

export async function createWorkspace(
  request: CreateWorkspaceRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<WorkspaceResponse>> {
  return desktop.apiRequest<WorkspaceResponse>(
    `${engineeringPath(scope)}/workspaces`,
    {
      method: 'POST',
      body: request
    });
}

export async function listEngineeringProjects(
  scope: ProjectApplicationApiScope
): Promise<EngineeringProjectResponse[]> {
  const response = await desktop.apiRequest<EngineeringProjectResponse[]>(
    `${engineeringPath(scope)}/projects`);
  return requireListResponse(response, 'List engineering projects');
}

export async function createEngineeringProject(
  request: CreateEngineeringProjectRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<EngineeringProjectResponse>> {
  return desktop.apiRequest<EngineeringProjectResponse>(
    `${engineeringPath(scope)}/projects`,
    {
      method: 'POST',
      body: request
    });
}

export async function listRecipes(
  scope: ProjectApplicationApiScope
): Promise<RecipeResponse[]> {
  const response = await desktop.apiRequest<RecipeResponse[]>(
    `${engineeringPath(scope)}/recipes`);
  return requireListResponse(response, 'List engineering recipes');
}

export async function createRecipe(
  request: CreateRecipeRequest,
  scope: ProjectApplicationApiScope
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
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<RecipeResponse>> {
  return desktop.apiRequest<RecipeResponse>(
    `${engineeringPath(scope)}/recipes/${encodeURIComponent(recipeId)}/publish`,
    {
      method: 'POST'
    });
}

export async function listStationProfiles(
  scope: ProjectApplicationApiScope
): Promise<StationProfileResponse[]> {
  const response = await desktop.apiRequest<StationProfileResponse[]>(
    `${engineeringPath(scope)}/station-profiles`);
  return requireListResponse(response, 'List engineering Station Profiles');
}

export async function createStationProfile(
  request: CreateStationProfileRequest,
  scope: ProjectApplicationApiScope
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
  scope: ProjectApplicationApiScope
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
  return requireListResponse(response, 'List device definitions');
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
  return requireListResponse(response, 'List device instances');
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

export async function listApplicationExtensions(
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ApplicationExtensionPackageResponse[]>> {
  return desktop.apiRequest<ApplicationExtensionPackageResponse[]>(
    applicationExtensionCollectionPath(scope));
}

export async function importApplicationExtension(
  scope: ProjectApplicationApiScope
) {
  return desktop.importApplicationExtension<ApplicationExtensionPackageResponse>(
    scope.projectId,
    scope.applicationId);
}

export async function validateApplicationExtensions(
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ApplicationExtensionPackageResponse[]>> {
  return desktop.apiRequest<ApplicationExtensionPackageResponse[]>(
    `${applicationExtensionCollectionPath(scope)}/validate`,
    { method: 'POST' });
}

export async function removeApplicationExtension(
  pluginId: string,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<unknown>> {
  return desktop.apiRequest(
    `${applicationExtensionCollectionPath(scope)}/${encodeURIComponent(pluginId)}`,
    { method: 'DELETE' });
}

export async function listProcessDefinitions(
  scope: ProjectApplicationApiScope
): Promise<ProcessDefinitionSummary[]> {
  const response = await desktop.apiRequest<ProcessDefinitionSummary[]>(processCollectionPath(scope));
  return requireListResponse(response, 'List process definitions');
}

export async function listProductionLines(
  scope: ProjectApplicationApiScope
): Promise<ProductionLineSummaryResponse[]> {
  const response = await desktop.apiRequest<ProductionLineSummaryResponse[]>(
    productionLineCollectionPath(scope));
  return requireListResponse(response, 'List production lines');
}

export async function getProductionLine(
  lineDefinitionId: string,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ProductionLineResponse>> {
  return desktop.apiRequest<ProductionLineResponse>(
    `${productionLineCollectionPath(scope)}/${encodeURIComponent(lineDefinitionId)}`);
}

export async function createProductionLine(
  request: SaveProductionLineRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ProductionLineResponse>> {
  return desktop.apiRequest<ProductionLineResponse>(
    productionLineCollectionPath(scope),
    {
      method: 'POST',
      body: request
    });
}

export async function replaceProductionLine(
  lineDefinitionId: string,
  request: SaveProductionLineRequest,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<ProductionLineResponse>> {
  return desktop.apiRequest<ProductionLineResponse>(
    `${productionLineCollectionPath(scope)}/${encodeURIComponent(lineDefinitionId)}`,
    {
      method: 'PUT',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function listExternalProgramResources(
  scope: ProjectApplicationApiScope
): Promise<ExternalProgramResourceResponse[]> {
  const response = await desktop.apiRequest<ExternalProgramResourceResponse[]>(
    externalProgramCollectionPath(scope));
  return requireListResponse(response, 'List external program resources');
}

export async function saveExternalProgramResource(
  resourceId: string,
  request: SaveExternalProgramResourceRequest,
  scope: ProjectApplicationApiScope,
  write?: EditorDocumentWriteOptions
): Promise<ApiResponse<ExternalProgramResourceResponse>> {
  return desktop.apiRequest<ExternalProgramResourceResponse>(
    `${externalProgramCollectionPath(scope)}/${encodeURIComponent(resourceId)}`,
    {
      method: 'PUT',
      body: request,
      headers: write ? editorDocumentHeaders(write) : undefined
    });
}

export async function importExternalProgramResource(
  request: SaveExternalProgramResourceRequest,
  files: Array<{ sourcePath: string; resourceRelativePath: string }>,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ExternalProgramResourceResponse>> {
  return desktop.uploadExternalProgram<ExternalProgramResourceResponse>(
    `${externalProgramCollectionPath(scope)}/import`,
    request,
    files);
}

export async function importExternalProgramResourceFile(
  resourceId: string,
  file: { sourcePath: string; resourceRelativePath: string },
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<ExternalProgramResourceResponse>> {
  return desktop.uploadExternalProgram<ExternalProgramResourceResponse>(
    `${externalProgramCollectionPath(scope)}/${encodeURIComponent(resourceId)}/files`,
    null,
    [file],
    editorDocumentHeaders(write));
}

export async function trialExternalProgramResource(
  resourceId: string,
  request: ExternalProgramTrialRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ExternalProgramTrialResponse>> {
  return desktop.apiRequest<ExternalProgramTrialResponse>(
    `${externalProgramCollectionPath(scope)}/${encodeURIComponent(resourceId)}/trial`,
    { method: 'POST', body: request });
}

export async function trialExternalProgramDefinition(
  definition: SaveExternalProgramResourceRequest,
  request: ExternalProgramTrialRequest,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ExternalProgramTrialResponse>> {
  return desktop.apiRequest<ExternalProgramTrialResponse>(
    `${externalProgramCollectionPath(scope)}/trial`,
    { method: 'POST', body: { definition, inputs: request.inputs } });
}

export async function deleteExternalProgramResource(
  resourceId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<unknown>> {
  return desktop.apiRequest(
    `${externalProgramCollectionPath(scope)}/${encodeURIComponent(resourceId)}`,
    { method: 'DELETE', headers: editorDocumentHeaders(write) });
}

export async function getActiveProductionRuns(
  filters: ProductionOperationsFilters
): Promise<ApiResponse<ActiveProductionRunsResponse>> {
  const query = new URLSearchParams();
  if (filters.productionLineDefinitionId) {
    query.set('productionLineDefinitionId', filters.productionLineDefinitionId);
  }
  if (filters.stationSystemId) {
    query.set('stationSystemId', filters.stationSystemId);
  }
  if (filters.slotId) {
    query.set('slotId', filters.slotId);
  }
  const suffix = query.size > 0 ? `?${query.toString()}` : '';
  return desktop.apiRequest<ActiveProductionRunsResponse>(`/api/operations/active-runs${suffix}`);
}

export async function getProductionRun(
  productionRunId: string
): Promise<ApiResponse<ProductionRunReadModel>> {
  return desktop.apiRequest<ProductionRunReadModel>(
    `/api/production-runs/${encodeURIComponent(productionRunId)}`);
}

export async function getProductionLineRuntimeState(
  productionLineDefinitionId: string
): Promise<ApiResponse<ProductionLineRuntimeStateResponse>> {
  return desktop.apiRequest<ProductionLineRuntimeStateResponse>(
    `/api/operations/lines/${encodeURIComponent(productionLineDefinitionId)}/state`);
}

export async function commandProductionRun(
  productionRunId: string,
  command: OperatorProductionRunCommand,
  request: ProductionRunCommandRequest
): Promise<ApiResponse<ProductionRunReadModel>> {
  return desktop.apiRequest<ProductionRunReadModel>(
    `/api/production-runs/${encodeURIComponent(productionRunId)}/commands/${command}`,
    {
      method: 'POST',
      body: request
    });
}

export async function requestStationEmergencyStop(
  stationSystemId: string,
  request: RequestStationEmergencyStopRequest
): Promise<ApiResponse<StationEmergencyStopResponse>> {
  return desktop.apiRequest<StationEmergencyStopResponse>(
    `/api/operations/stations/${encodeURIComponent(stationSystemId)}/emergency-stop`,
    {
      method: 'POST',
      body: request
    });
}

export async function getStationSafetyEvents({
  projectId,
  applicationId,
  projectSnapshotId,
  stationSystemId
}: {
  projectId: string;
  applicationId: string;
  projectSnapshotId?: string;
  stationSystemId?: string;
}): Promise<ApiResponse<StationSafetyEventsResponse>> {
  const query = new URLSearchParams({ projectId, applicationId });
  if (projectSnapshotId) {
    query.set('projectSnapshotId', projectSnapshotId);
  }
  if (stationSystemId) {
    query.set('stationSystemId', stationSystemId);
  }
  return desktop.apiRequest<StationSafetyEventsResponse>(
    `/api/operations/safety-events?${query.toString()}`);
}

export async function listProcessBlocklyBlocks(
  scope: ProjectApplicationApiScope
): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>(
    processBlockCollectionPath(scope));
  return requireListResponse(response, 'List Process Blockly blocks');
}

export async function listProcessBlocklyBlockVersions(
  blockType: string,
  scope: ProjectApplicationApiScope
): Promise<ProcessBlocklyBlockDefinition[]> {
  const response = await desktop.apiRequest<ProcessBlocklyBlockDefinition[]>(
    `${processBlockCollectionPath(scope)}/${encodeURIComponent(blockType)}/versions`);
  return requireListResponse(response, 'List Process Blockly block versions');
}

export async function registerProcessBlocklyBlock(
  request: RegisterProcessBlocklyBlockDefinitionRequest,
  scope: ProjectApplicationApiScope
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
  scope: ProjectApplicationApiScope
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
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}`,
    {
      method: 'PUT',
      body: request,
      headers: editorDocumentHeaders(write)
    });
}

export async function getProcessDefinition(
  processDefinitionId: string,
  scope: ProjectApplicationApiScope
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}`);
}

export async function publishProcessDefinition(
  processDefinitionId: string,
  scope: ProjectApplicationApiScope,
  write: EditorDocumentWriteOptions
): Promise<ApiResponse<ProcessDefinitionResponse>> {
  return desktop.apiRequest<ProcessDefinitionResponse>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}/publish`,
    {
      method: 'POST',
      headers: editorDocumentHeaders(write)
    });
}

export async function validateProcessDefinition(
  processDefinitionId: string,
  scope: ProjectApplicationApiScope
): Promise<ProcessGraphValidationReport | null> {
  const response = await desktop.apiRequest<ProcessGraphValidationReport>(
    `${processCollectionPath(scope)}/${encodeURIComponent(processDefinitionId)}/validation`);
  return response.body;
}

function processCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/processes`;
}

function externalProgramCollectionPath(scope: ProjectApplicationApiScope): string {
  return `/api/automation-projects/${encodeURIComponent(scope.projectId)}`
    + `/applications/${encodeURIComponent(scope.applicationId)}/external-programs`;
}

function applicationExtensionCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/extensions`;
}

function requireListResponse<T>(response: ApiResponse<T[]>, action: string): T[] {
  if (!response.ok || response.body === null) {
    throw new Error(`${action} failed: ${response.status} ${response.text}`.trimEnd());
  }
  return response.body;
}

function editorDocumentHeaders(write: EditorDocumentWriteOptions): Record<string, string> {
  if (write.force) {
    return {
      'If-Match': '*',
      'X-OpenLineOps-Conflict-Resolution': 'overwrite'
    };
  }

  return { 'If-Match': `"${write.revision}"` };
}

function productionLineCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/production-lines`;
}

function processBlockCollectionPath(scope: ProjectApplicationApiScope): string {
  return `${projectApplicationPath(scope)}/process-blocks`;
}

export function createRuntimeHubConnection(
  apiBaseUrl: string,
  apiAccessToken: string
): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${apiBaseUrl}/hubs/runtime-progress`, {
      accessTokenFactory: () => apiAccessToken,
      transport: signalR.HttpTransportType.LongPolling
    })
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
