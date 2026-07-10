export interface PlatformResponse {
  productName: string;
  serviceName: string;
  version: string;
  runtime: string;
  environment: string;
}

export interface HealthResponse {
  status: string;
  service: string;
}

export type RuntimeSessionStatus =
  | 'Created'
  | 'Queued'
  | 'Running'
  | 'Pausing'
  | 'Paused'
  | 'Stopping'
  | 'Stopped'
  | 'Completed'
  | 'Failed'
  | 'Canceled';

export type RuntimeCommandStatus =
  | 'Pending'
  | 'Accepted'
  | 'InProgress'
  | 'Completed'
  | 'Failed'
  | 'TimedOut'
  | 'Canceled'
  | 'Rejected';

export interface RuntimeMonitoringScope {
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionRunId: string;
}

export interface RuntimeDutIdentity {
  modelId: string;
  inputKey: string;
  value: string;
}

export interface RuntimeStationStatus {
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionRunId: string;
  productionLineDefinitionId: string;
  stageId: string;
  stageSequence: number;
  workstationId: string;
  dutIdentity: RuntimeDutIdentity;
  stationSystemId: string;
  latestSessionId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  sessionStatus: RuntimeSessionStatus;
  stepCount: number;
  completedStepCount: number;
  runningStepCount: number;
  commandCount: number;
  incidentCount: number;
  lastTransitionAtUtc: string;
  isTerminal: boolean;
}

export interface RuntimeStationStatusesResponse {
  items: RuntimeStationStatus[];
}

export interface RuntimeTargetStatus {
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionRunId: string;
  productionLineDefinitionId: string;
  stageId: string;
  stageSequence: number;
  workstationId: string;
  dutIdentity: RuntimeDutIdentity;
  stationSystemId: string;
  sessionId: string;
  actionId: string;
  targetKind: string;
  targetId: string;
  commandStatus: RuntimeCommandStatus;
  lastTransitionAtUtc: string;
  isTerminal: boolean;
  failureReason: string | null;
}

export interface RuntimeTargetStatusesResponse {
  items: RuntimeTargetStatus[];
}

export interface RuntimeTimelineEntry {
  sequence: number;
  eventId: string;
  occurredAtUtc: string;
  eventName: string;
  sessionId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionRunId: string;
  productionLineDefinitionId: string;
  stageId: string;
  stageSequence: number;
  workstationId: string;
  dutIdentity: RuntimeDutIdentity;
  stationSystemId: string;
  entityKind: string;
  entityId: string | null;
  fromStatus: string | null;
  toStatus: string | null;
  reason: string | null;
  severity: string | null;
  code: string | null;
  sessionStatus: RuntimeSessionStatus;
}

export interface RuntimeTimelineResponse {
  items: RuntimeTimelineEntry[];
}

export interface RuntimeAlarm {
  alarmId: string;
  sessionId: string;
  stationSystemId: string;
  severity: string;
  code: string;
  message: string;
  occurredAtUtc: string;
  isAcknowledged: boolean;
  acknowledgedBy: string | null;
  acknowledgedAtUtc: string | null;
}

export interface RuntimeAlarmsResponse {
  items: RuntimeAlarm[];
}

export interface TraceRecordSummary {
  traceRecordId: string;
  productionRunId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  dutModelId: string;
  dutIdentityInputKey: string;
  dutIdentityValue: string;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  actorId: string;
  runStatus: string;
  judgement: string;
  completedAtUtc: string;
  stageCount: number;
  failedStageCount: number;
  commandCount: number;
  measurementCount: number;
  artifactCount: number;
  incidentCount: number;
}

export interface TraceRecordQueryResponse {
  items: TraceRecordSummary[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface TraceRecordResponse {
  traceRecordId: string;
  productionRunId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  dutModelId: string;
  dutIdentityInputKey: string;
  dutIdentityValue: string;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  actorId: string;
  runStatus: string;
  judgement: string;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string;
  failureCode: string | null;
  failureReason: string | null;
  stages: TraceStageExecutionResponse[];
  auditEntries: AuditEntryResponse[];
}

export interface TraceStageExecutionResponse {
  stageId: string;
  sequence: number;
  workstationId: string;
  stationId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  runtimeSessionId: string | null;
  runtimeSessionStatus: string | null;
  status: string;
  startedAtUtc: string | null;
  completedAtUtc: string;
  failureCode: string | null;
  failureReason: string | null;
  completedStepCount: number;
  commandCount: number;
  incidentCount: number;
  commands: TraceCommandResponse[];
  measurements: MeasurementRecordResponse[];
  artifacts: ArtifactRecordResponse[];
  incidents: TraceIncidentResponse[];
}

export interface TraceCommandResponse {
  runtimeCommandId: string;
  runtimeStepId: string;
  actionId: string;
  targetKind: string;
  targetId: string;
  targetCapabilityId: string;
  commandName: string;
  status: string;
  semanticOutcome: string | null;
  createdAtUtc: string;
  deadlineAtUtc: string;
  acceptedAtUtc: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  resultPayload: string | null;
  failureReason: string | null;
}

export interface MeasurementRecordResponse {
  measurementRecordId: string;
  name: string;
  numericValue: number | null;
  textValue: string | null;
  unit: string | null;
  deviceId: string | null;
  runtimeCommandId: string | null;
  actionId: string;
  targetKind: string;
  targetId: string;
  commandStatus: string;
  passed: boolean | null;
  measuredAtUtc: string;
}

export interface ArtifactRecordResponse {
  artifactRecordId: string;
  name: string;
  kind: string;
  storageKey: string;
  mediaType: string | null;
  sizeBytes: number;
  sha256: string | null;
  deviceId: string | null;
  capturedAtUtc: string;
}

export interface TraceIncidentResponse {
  runtimeIncidentId: string;
  severity: string;
  code: string;
  message: string;
  occurredAtUtc: string;
}

export interface AuditEntryResponse {
  auditEntryId: string;
  actorId: string;
  action: string;
  detail: string | null;
  occurredAtUtc: string;
}

export interface TraceRecordExportPackageResponse {
  packageFormatVersion: string;
  exportedAtUtc: string;
  traceRecord: TraceRecordResponse;
}

export interface EngineeringTraceSearchQuery {
  productionRunId?: string;
  dutModelId?: string;
  dutIdentityInputKey?: string;
  dutIdentityValue?: string;
  batchId?: string;
  fixtureId?: string;
  deviceId?: string;
  actorId?: string;
  runStatus?: string;
  judgement?: string;
  projectId?: string;
  applicationId?: string;
  projectSnapshotId?: string;
  topologyId?: string;
  productionLineDefinitionId?: string;
  stageId?: string;
  workstationId?: string;
  stationId?: string;
  processDefinitionId?: string;
  processVersionId?: string;
  configurationSnapshotId?: string;
  recipeSnapshotId?: string;
  completedFromUtc?: string;
  completedToUtc?: string;
  pageNumber?: number;
  pageSize?: number;
}

export interface EngineeringTraceSearchResponse {
  results: PagedEngineeringTraceSearchRowsResponse;
  facets: EngineeringTraceSearchFacetsResponse;
  areFacetsTruncated: boolean;
}

export interface PagedEngineeringTraceSearchRowsResponse {
  items: EngineeringTraceSearchRowResponse[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface EngineeringTraceSearchRowResponse {
  traceRecordId: string;
  productionRunId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  dutModelId: string;
  dutIdentityInputKey: string;
  dutIdentityValue: string;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  actorId: string;
  runStatus: string;
  judgement: string;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string;
  stageCount: number;
  failedStageCount: number;
  commandCount: number;
  failedCommandCount: number;
  measurementCount: number;
  failedMeasurementCount: number;
  artifactCount: number;
  incidentCount: number;
}

export interface EngineeringTraceSearchFacetsResponse {
  judgements: TraceFacetCountResponse[];
  runStatuses: TraceFacetCountResponse[];
  stations: TraceFacetCountResponse[];
  devices: TraceFacetCountResponse[];
  productionLines: TraceFacetCountResponse[];
  processVersions: TraceFacetCountResponse[];
  projectSnapshots: TraceFacetCountResponse[];
}

export interface TraceFacetCountResponse {
  value: string;
  count: number;
}

export interface WorkspaceResponse {
  workspaceId: string;
  displayName: string;
  createdAtUtc: string;
}

export interface EngineeringProjectResponse {
  projectId: string;
  workspaceId: string;
  displayName: string;
  createdAtUtc: string;
  activeSnapshotId: string | null;
  snapshots: ConfigurationSnapshotResponse[];
}

export interface RecipeResponse {
  recipeId: string;
  versionId: string;
  displayName: string;
  status: string;
  createdAtUtc: string;
  publishedAtUtc: string | null;
  parameters: RecipeParameterResponse[];
}

export interface RecipeParameterResponse {
  key: string;
  value: string;
}

export interface StationProfileResponse {
  stationProfileId: string;
  stationSystemId: string;
  displayName: string;
  deviceBindings: DeviceBindingResponse[];
}

export interface DeviceBindingResponse {
  deviceBindingId: string;
  capabilityId: string;
  deviceKey: string;
}

export interface ConfigurationSnapshotResponse {
  snapshotId: string;
  projectId: string;
  processDefinitionId: string;
  processVersionId: string;
  recipeId: string;
  recipeVersionId: string;
  stationProfileId: string;
  status: string;
  publishedAtUtc: string;
  deviceBindings: DeviceBindingResponse[];
}

export interface AutomationProjectSummaryResponse {
  projectId: string;
  displayName: string;
  projectPath: string;
  activeSnapshotId: string | null;
}

export interface AutomationProjectResponse {
  projectId: string;
  displayName: string;
  projectPath: string;
  createdAtUtc: string;
  activeSnapshotId: string | null;
  applications: ProjectApplicationResponse[];
  snapshots: PublishedProjectSnapshotResponse[];
}

export interface ProjectApplicationResponse {
  applicationId: string;
  displayName: string;
  topologyId: string | null;
  processDefinitionIds: string[];
  projectFilePath: string | null;
}

export interface AddProjectApplicationRequest {
  applicationId: string;
  displayName: string;
}

export interface ImportProjectApplicationRequest {
  projectFilePath: string;
}

export interface PublishedProjectSnapshotResponse {
  snapshotId: string;
  projectId: string;
  applicationId: string;
  topologyId: string;
  layoutIds: string[];
  productionLineDefinitionId: string;
  publishedAtUtc: string;
  capabilityBindings: SnapshotCapabilityBindingResponse[];
  targetReferences: ProjectTargetReferenceResponse[];
  blockVersionIds: string[];
  releaseManifestPath: string;
  releaseContentSha256: string;
}

export interface SnapshotCapabilityBindingResponse {
  capabilityId: string;
  bindingId: string;
  providerKind: string;
  providerKey: string;
}

export interface ProjectTargetReferenceResponse {
  kind: string;
  targetId: string;
}

export interface PublishProjectSnapshotRequest {
  snapshotId: string;
  applicationId: string;
  productionLineDefinitionId: string;
}

export interface SnapshotCapabilityBindingRequest {
  capabilityId: string;
  bindingId: string;
  providerKind: string;
  providerKey: string;
}

export interface ProjectTargetReferenceRequest {
  kind: string;
  targetId: string;
}

export type ProductionRunStatus = 'Created' | 'Running' | 'Completed' | 'Failed' | 'Canceled';

export type ProductionStageRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled' | 'Skipped';

export interface StartProjectSnapshotProductionRunRequest {
  productionRunId: string;
  dutIdentityValue: string;
  actorId: string;
  batchId?: string | null;
  fixtureId?: string | null;
  deviceId?: string | null;
}

export interface StartedProjectSnapshotProductionRunResponse {
  snapshotId: string;
  projectId: string;
  applicationId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  productionRunId: string;
  dutModelId: string;
  dutIdentityInputKey: string;
  dutIdentityValue: string;
  actorId: string;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  status: ProductionRunStatus;
  isTerminal: boolean;
  createdAtUtc: string;
  lastTransitionAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  failureCode: string | null;
  failureReason: string | null;
  completedStageCount: number;
  completedStepCount: number;
  commandCount: number;
  incidentCount: number;
  stages: ProductionStageRunResponse[];
}

export interface ProductionStageRunResponse {
  stageId: string;
  sequence: number;
  workstationId: string;
  stationSystemId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  status: ProductionStageRunStatus;
  runtimeSessionId: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  failureCode: string | null;
  failureReason: string | null;
  completedStepCount: number;
  commandCount: number;
  incidentCount: number;
}

export interface AutomationProjectWorkspaceResponse {
  project: AutomationProjectResponse;
  manifestPath: string;
  manifest: AutomationProjectManifestResponse;
}

export interface AutomationProjectManifestResponse {
  formatVersion: number;
  product: string;
  projectId: string;
  displayName: string;
  projectPath: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  activeSnapshotId: string | null;
  applications: ProjectApplicationManifestResponse[];
  snapshots: PublishedProjectSnapshotManifestResponse[];
}

export interface ProjectApplicationManifestResponse {
  applicationId: string;
  displayName: string;
  topologyId: string | null;
  processDefinitionIds: string[];
  projectFilePath: string | null;
}

export interface PublishedProjectSnapshotManifestResponse {
  snapshotId: string;
  projectId: string;
  applicationId: string;
  topologyId: string;
  layoutIds: string[];
  productionLineDefinitionId: string;
  publishedAtUtc: string;
  capabilityBindings: SnapshotCapabilityBindingResponse[];
  targetReferences: ProjectTargetReferenceResponse[];
  blockVersionIds: string[];
  releaseManifestPath: string;
  releaseContentSha256: string;
}

export interface CreateAutomationProjectWorkspaceRequest {
  projectId: string;
  displayName: string;
  projectPath: string;
  defaultApplicationId: string | null;
  defaultApplicationName: string | null;
}

export interface OpenAutomationProjectWorkspaceRequest {
  projectPath: string;
}

export interface CreateAutomationTopologyRequest {
  topologyId: string;
  displayName: string;
}

export interface AddAutomationSystemRequest {
  systemId: string;
  parentSystemId: string | null;
  kind: 'System' | 'Station';
  systemType: string;
  displayName: string;
  requiredCapabilityIds: string[];
  providedCapabilityIds: string[];
  metadata: Record<string, string>;
}

export interface UpdateAutomationSystemRequest {
  systemType?: string;
  displayName?: string;
  metadata?: Record<string, string>;
}

export interface AddCapabilityContractRequest {
  capabilityId: string;
  commandName: string;
  version: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutSeconds: number;
  safetyClass: string;
}

export interface AddDriverBindingRequest {
  bindingId: string;
  capabilityId: string;
  providerKind: string;
  providerKey: string;
}

export interface AddSlotGroupRequest {
  slotGroupId: string;
  parentSystemId: string;
  displayName: string;
  kind: string;
  capacity: number;
}

export interface UpdateSlotGroupRequest {
  displayName?: string;
  kind?: string;
  capacity?: number;
}

export interface AddSlotDefinitionRequest {
  slotGroupId: string;
  slotId: string;
  parentSystemId: string;
  address: string;
  displayName: string;
  materialKind: string;
  isEnabled: boolean;
}

export interface UpdateSlotDefinitionRequest {
  address?: string;
  displayName?: string;
  materialKind?: string;
  isEnabled?: boolean;
}

export interface CreateSiteLayoutRequest {
  layoutId: string;
  topologyId: string;
  displayName: string;
  canvasWidth: number;
  canvasHeight: number;
  units: string;
}

export interface AddSiteLayoutElementRequest {
  elementId: string;
  kind: 'SystemShape' | 'GroupRegion' | 'SlotShape';
  target: SiteLayoutTargetReference;
  parentElementId: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDegrees: number;
  zIndex: number;
  style: Record<string, string>;
}

export interface UpdateSiteLayoutElementGeometryRequest {
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDegrees: number;
}

export interface LinkProjectTopologyRequest {
  topologyId: string;
}

export interface AutomationTopologySummaryResponse {
  topologyId: string;
  displayName: string;
  systemCount: number;
  stationCount: number;
  slotCount: number;
}

export interface AutomationTopologyResponse {
  topologyId: string;
  displayName: string;
  createdAtUtc: string;
  systems: AutomationSystemResponse[];
  capabilities: CapabilityContractResponse[];
  driverBindings: DriverBindingRouteResponse[];
  slotGroups: SlotGroupResponse[];
  slots: SlotDefinitionResponse[];
}

export interface TopologyTargetDeletionResponse {
  topology: AutomationTopologyResponse;
  updatedLayoutCount: number;
  removedLayoutElementCount: number;
  publicationImpact: string;
}

export interface AutomationSystemResponse {
  systemId: string;
  parentSystemId: string | null;
  kind: 'System' | 'Station';
  systemType: string;
  displayName: string;
  requiredCapabilityIds: string[];
  providedCapabilityIds: string[];
  metadata: Record<string, string>;
}

export interface CapabilityContractResponse {
  capabilityId: string;
  commandName: string;
  version: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutSeconds: number;
  safetyClass: string;
}

export interface DriverBindingRouteResponse {
  bindingId: string;
  capabilityId: string;
  providerKind: string;
  providerKey: string;
}

export interface SlotGroupResponse {
  slotGroupId: string;
  parentSystemId: string;
  displayName: string;
  kind: string;
  capacity: number;
  slotIds: string[];
}

export interface SlotDefinitionResponse {
  slotId: string;
  slotGroupId: string;
  parentSystemId: string;
  address: string;
  displayName: string;
  materialKind: string;
  isEnabled: boolean;
}

export interface SiteLayoutResponse {
  layoutId: string;
  topologyId: string;
  displayName: string;
  canvasWidth: number;
  canvasHeight: number;
  units: string;
  elements: SiteLayoutElementResponse[];
}

export interface SiteLayoutElementResponse {
  elementId: string;
  kind: 'SystemShape' | 'GroupRegion' | 'SlotShape';
  target: SiteLayoutTargetReference;
  parentElementId: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDegrees: number;
  zIndex: number;
  style: Record<string, string>;
}

export interface SiteLayoutTargetReference {
  kind: 'System' | 'SlotGroup' | 'Slot';
  targetId: string;
}

export interface ProductionLineSummaryResponse {
  lineDefinitionId: string;
  displayName: string;
  topologyId: string;
  dutModelCode: string;
  stageCount: number;
  updatedAtUtc: string;
}

export interface ProductionLineResponse {
  lineDefinitionId: string;
  displayName: string;
  topologyId: string;
  dutModel: DutModelResponse;
  workstations: ProductionWorkstationResponse[];
  stages: ProductionStageResponse[];
  externalTestProgramAdapters: ExternalTestProgramAdapterResponse[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface DutModelResponse {
  dutModelId: string;
  modelCode: string;
  identityInputKey: string;
}

export interface ProductionWorkstationResponse {
  workstationId: string;
  displayName: string;
  stationSystemId: string;
}

export interface ProductionStageResponse {
  stageId: string;
  sequence: number;
  displayName: string;
  workstationId: string;
  flowDefinitionId: string;
  configurationSnapshotId: string;
  externalTestProgramAdapterId: string | null;
  nextStageId: string | null;
}

export interface ExternalTestProgramAdapterResponse {
  adapterId: string;
  displayName: string;
  capabilityId: string;
  commandName: string;
  launchKind: string;
  executable: string | null;
  providerKey: string | null;
  argumentTemplates: string[];
  inputMappings: ExternalTestProgramInputMappingResponse[];
  resultMappings: ExternalTestProgramResultMappingResponse[];
  outcomeMapping: ExternalTestProgramOutcomeMappingResponse;
  timeoutMilliseconds: number;
}

export interface ExternalTestProgramInputMappingResponse {
  source: string;
  target: string;
}

export interface ExternalTestProgramResultMappingResponse {
  sourcePath: string;
  targetKey: string;
}

export interface ExternalTestProgramOutcomeMappingResponse {
  sourcePath: string;
  passedToken: string;
  failedToken: string;
  abortedToken: string;
}

export interface SaveProductionLineRequest {
  lineDefinitionId: string;
  displayName: string;
  topologyId: string;
  dutModel: DutModelRequest;
  workstations: ProductionWorkstationRequest[];
  stages: ProductionStageRequest[];
  externalTestProgramAdapters: ExternalTestProgramAdapterRequest[];
}

export interface DutModelRequest {
  dutModelId: string;
  modelCode: string;
  identityInputKey: string;
}

export interface ProductionWorkstationRequest {
  workstationId: string;
  displayName: string;
  stationSystemId: string;
}

export interface ProductionStageRequest {
  stageId: string;
  sequence: number;
  displayName: string;
  workstationId: string;
  flowDefinitionId: string;
  configurationSnapshotId: string;
  externalTestProgramAdapterId: string | null;
}

export interface ExternalTestProgramAdapterRequest {
  adapterId: string;
  displayName: string;
  capabilityId: string;
  commandName: string;
  executable: string | null;
  providerKey: string | null;
  argumentTemplates: string[];
  inputMappings: ExternalTestProgramInputMappingRequest[];
  resultMappings: ExternalTestProgramResultMappingRequest[];
  outcomeMapping: ExternalTestProgramOutcomeMappingRequest;
  timeoutMilliseconds: number;
}

export interface ExternalTestProgramInputMappingRequest {
  source: string;
  target: string;
}

export interface ExternalTestProgramResultMappingRequest {
  sourcePath: string;
  targetKey: string;
}

export interface ExternalTestProgramOutcomeMappingRequest {
  sourcePath: string;
  passedToken: string;
  failedToken: string;
  abortedToken: string;
}

export interface CreateWorkspaceRequest {
  workspaceId: string;
  displayName: string;
}

export interface CreateEngineeringProjectRequest {
  projectId: string;
  workspaceId: string;
  displayName: string;
}

export interface CreateRecipeRequest {
  recipeId: string;
  versionId: string;
  displayName: string;
  parameters: RecipeParameterRequest[];
}

export interface RecipeParameterRequest {
  key: string;
  value: string;
}

export interface CreateStationProfileRequest {
  stationProfileId: string;
  stationSystemId: string;
  displayName: string;
  deviceBindings: CreateDeviceBindingRequest[];
}

export interface CreateDeviceBindingRequest {
  deviceBindingId: string;
  capabilityId: string;
  deviceKey: string;
}

export interface PublishConfigurationSnapshotRequest {
  snapshotId: string;
  processDefinitionId: string;
  processVersionId: string;
  recipeId: string;
  stationProfileId: string;
}

export interface DeviceDefinitionResponse {
  deviceDefinitionId: string;
  displayName: string;
  pluginId: string;
  createdAtUtc: string;
  capabilities: DeviceCapabilityResponse[];
  commands: DeviceCommandDefinitionResponse[];
}

export interface DeviceCapabilityResponse {
  capabilityId: string;
  displayName: string;
}

export interface DeviceCommandDefinitionResponse {
  commandDefinitionId: string;
  capabilityId: string;
  commandName: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutSeconds: number;
  maxRetries: number;
}

export interface DeviceInstanceResponse {
  deviceInstanceId: string;
  deviceDefinitionId: string;
  stationId: string;
  displayName: string;
  protocol: string;
  address: string;
  registeredAtUtc: string;
  status: string;
  connectedAtUtc: string | null;
  lastDisconnectedAtUtc: string | null;
  faultReason: string | null;
}

export interface CreateDeviceDefinitionRequest {
  deviceDefinitionId: string;
  displayName: string;
  pluginId: string;
  capabilities: CreateDeviceCapabilityRequest[];
  commands: CreateDeviceCommandDefinitionRequest[];
}

export interface CreateDeviceCapabilityRequest {
  capabilityId: string;
  displayName: string;
}

export interface CreateDeviceCommandDefinitionRequest {
  commandDefinitionId: string;
  capabilityId: string;
  commandName: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutSeconds: number;
  maxRetries: number;
}

export interface RegisterDeviceInstanceRequest {
  deviceInstanceId: string;
  deviceDefinitionId: string;
  stationId: string;
  displayName: string;
  protocol: string;
  address: string;
}

export interface DeviceStatusChangeRequest {
  reason: string | null;
}

export interface ProcessDefinitionSummary {
  processDefinitionId: string;
  versionId: string;
  displayName: string;
  status: string;
  createdAtUtc: string;
  publishedAtUtc: string | null;
}

export interface ProcessDefinitionResponse extends ProcessDefinitionSummary {
  nodes: ProcessNodeResponse[];
  transitions: ProcessTransitionResponse[];
}

export interface ProcessNodeResponse {
  nodeId: string;
  kind: string;
  displayName: string;
  requiredCapability: string | null;
  commandName: string | null;
  targetKind: string | null;
  targetId: string | null;
  timeoutSeconds: number | null;
  inputPayload: string | null;
  scriptLanguage: string | null;
  blocklyWorkspaceJson: string | null;
  scriptSourceCode: string | null;
  scriptSourceHash: string | null;
  scriptVersion: string | null;
}

export interface ProcessTransitionResponse {
  transitionId: string;
  fromNodeId: string;
  toNodeId: string;
  label: string | null;
  loopPolicy: string;
  maxTraversals: number | null;
}

export interface ProcessGraphValidationReport {
  isValid: boolean;
  issues: ProcessGraphValidationIssue[];
}

export interface ProcessGraphValidationIssue {
  severity: string;
  code: string;
  message: string;
}

export interface CreateProcessDefinitionRequest {
  processDefinitionId: string;
  versionId: string;
  displayName: string;
  nodes: CreateProcessNodeRequest[];
  transitions: CreateProcessTransitionRequest[];
}

export interface CreateProcessNodeRequest {
  nodeId: string;
  kind: string;
  displayName: string;
  requiredCapability: string | null;
  commandName: string | null;
  targetKind: string | null;
  targetId: string | null;
  timeoutSeconds: number | null;
  inputPayload: string | null;
  blocklyWorkspaceJson?: string | null;
  scriptSourceCode?: string | null;
  scriptVersion?: string | null;
}

export interface CreateProcessTransitionRequest {
  transitionId: string;
  fromNodeId: string;
  toNodeId: string;
  label: string | null;
  loopPolicy?: string | null;
  maxTraversals?: number | null;
}

export interface ProcessBlocklyBlockDefinition {
  blockType: string;
  category: string;
  displayName: string;
  blocklyJson: Record<string, unknown>;
  isBuiltIn: boolean;
  version: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  executionMode: string;
  runtimeActionContractSchemaVersion: string;
  runtimeActionContract: Record<string, unknown>;
  runtimeActionContractSha256: string;
}

export interface RegisterProcessBlocklyBlockDefinitionRequest {
  blockType: string;
  category: string;
  displayName: string;
  blocklyJson: Record<string, unknown>;
  runtimeActionContractSchemaVersion: string;
  runtimeActionContract: Record<string, unknown>;
}

export interface PluginManagementOverviewResponse {
  packages: PluginPackageResponse[];
  capabilities: PluginCapabilityResponse[];
  deviceCommands: PluginCommandResponse[];
  processCommands: PluginCommandResponse[];
  recentEvents: ExternalPluginProcessEventResponse[];
}

export interface PluginPackageResponse {
  manifest: PluginManifestResponse;
  packagePath: string;
  manifestPath: string;
  isValid: boolean;
  validationIssues: PluginValidationIssueResponse[];
}

export interface PluginManifestResponse {
  id: string;
  name: string;
  version: string;
  kind: string;
  entryAssembly: string;
  entryType: string;
  contractVersion: string;
  minimumPlatformVersion: string;
  capabilities: string[];
  deviceCommands: PluginCommandDefinitionResponse[];
  processCommands: PluginCommandDefinitionResponse[];
}

export interface PluginCommandDefinitionResponse {
  id: string;
  capability: string;
  commandName: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutMilliseconds: number;
  maxRetries: number;
}

export interface PluginValidationIssueResponse {
  code: string;
  message: string;
}

export interface PluginCapabilityResponse {
  pluginId: string;
  pluginName: string;
  pluginKind: string;
  capability: string;
}

export interface PluginCommandResponse {
  pluginId: string;
  pluginName: string;
  pluginKind: string;
  commandDefinitionId: string;
  capability: string;
  commandName: string;
  inputSchema: string | null;
  outputSchema: string | null;
  timeoutMilliseconds: number;
  maxRetries: number;
}

export interface PluginLifecycleRecordResponse {
  manifest: PluginManifestResponse;
  state: string;
  initializationStatus: string;
  validationIssues: PluginValidationIssueResponse[];
  failureReason: string | null;
}

export interface ExternalPluginProcessEventResponse {
  kind: string;
  pluginId: string;
  message: string;
  occurredAtUtc: string;
  detail: string | null;
}
