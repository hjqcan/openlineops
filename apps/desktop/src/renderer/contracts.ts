export interface PlatformResponse {
  service: string;
  version: string;
  environment: string;
}

export interface HealthResponse {
  status: string;
  service: string;
}

export interface RuntimeSessionRunResponse {
  sessionId: string;
  configurationSnapshotId: string;
  status: string;
  completedSteps: number;
  commandCount: number;
  incidentCount: number;
}

export interface RuntimeStationStatus {
  stationId: string;
  latestSessionId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  serialNumber: string | null;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  sessionStatus: string;
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

export interface RuntimeTimelineEntry {
  sequence: number;
  eventId: string;
  occurredAtUtc: string;
  eventName: string;
  sessionId: string;
  stationId: string;
  entityKind: string;
  entityId: string | null;
  fromStatus: string | null;
  toStatus: string | null;
  reason: string | null;
  severity: string | null;
  code: string | null;
  sessionStatus: string;
}

export interface RuntimeTimelineResponse {
  items: RuntimeTimelineEntry[];
}

export interface RuntimeAlarm {
  alarmId: string;
  sessionId: string;
  stationId: string;
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

export interface StartRuntimeSessionRequest {
  stationId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  processDefinitionId: string;
  processVersionId: string;
  serialNumber: string;
  batchId: string;
  fixtureId: string;
  deviceId: string;
  actorId: string;
  nodes: StartRuntimeNodeRequest[];
}

export interface StartRuntimeNodeRequest {
  nodeId: string;
  displayName: string;
  targetCapability: string;
  commandName: string;
  timeoutSeconds: number;
  inputPayload: string;
}

export interface TraceRecordSummary {
  traceRecordId: string;
  runtimeSessionId: string;
  serialNumber: string;
  batchId: string | null;
  stationId: string;
  fixtureId: string | null;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  deviceId: string;
  judgement: string;
  completedAtUtc: string;
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
  runtimeSessionId: string;
  serialNumber: string;
  batchId: string | null;
  stationId: string;
  fixtureId: string | null;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  deviceId: string;
  judgement: string;
  startedAtUtc: string;
  completedAtUtc: string;
  recordedBy: string;
  measurements: MeasurementRecordResponse[];
  artifacts: ArtifactRecordResponse[];
  auditEntries: AuditEntryResponse[];
}

export interface MeasurementRecordResponse {
  measurementRecordId: string;
  name: string;
  numericValue: number | null;
  textValue: string | null;
  unit: string | null;
  deviceId: string;
  runtimeCommandId: string | null;
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
  deviceId: string;
  capturedAtUtc: string;
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
  serialNumber?: string;
  batchId?: string;
  stationId?: string;
  fixtureId?: string;
  processDefinitionId?: string;
  processVersionId?: string;
  configurationSnapshotId?: string;
  recipeSnapshotId?: string;
  deviceId?: string;
  judgement?: string;
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
  runtimeSessionId: string;
  serialNumber: string;
  batchId: string | null;
  stationId: string;
  fixtureId: string | null;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  deviceId: string;
  judgement: string;
  startedAtUtc: string;
  completedAtUtc: string;
  measurementCount: number;
  failedMeasurementCount: number;
  artifactCount: number;
}

export interface EngineeringTraceSearchFacetsResponse {
  judgements: TraceFacetCountResponse[];
  stations: TraceFacetCountResponse[];
  devices: TraceFacetCountResponse[];
  processVersions: TraceFacetCountResponse[];
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
}

export interface PublishedProjectSnapshotResponse {
  snapshotId: string;
  projectId: string;
  applicationId: string;
  topologyId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  publishedAtUtc: string;
  capabilityBindings: SnapshotCapabilityBindingResponse[];
  targetReferences: ProjectTargetReferenceResponse[];
  blockVersionIds: string[];
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
  topologyId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  capabilityBindings: SnapshotCapabilityBindingRequest[];
  targetReferences: ProjectTargetReferenceRequest[];
  blockVersionIds: string[];
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

export interface StartProjectSnapshotRuntimeSessionRequest {
  serialNumber?: string | null;
  batchId?: string | null;
  fixtureId?: string | null;
  deviceId?: string | null;
  actorId?: string | null;
}

export interface StartedProjectSnapshotRuntimeSessionResponse {
  snapshotId: string;
  sessionId: string;
  configurationSnapshotId: string;
  status: string;
  completedSteps: number;
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
}

export interface PublishedProjectSnapshotManifestResponse {
  snapshotId: string;
  projectId: string;
  applicationId: string;
  topologyId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  publishedAtUtc: string;
  capabilityBindings: SnapshotCapabilityBindingResponse[];
  targetReferences: ProjectTargetReferenceResponse[];
  blockVersionIds: string[];
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

export interface AddEquipmentNodeRequest {
  nodeId: string;
  parentNodeId: string | null;
  kind: string;
  displayName: string;
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

export interface AddAutomationModuleRequest {
  moduleId: string;
  nodeId: string;
  moduleKind: string;
  displayName: string;
  requiredCapabilityIds: string[];
  providedCapabilityIds: string[];
}

export interface AddDriverBindingRequest {
  bindingId: string;
  capabilityId: string;
  providerKind: string;
  providerKey: string;
}

export interface AddSlotGroupRequest {
  slotGroupId: string;
  parentNodeId: string;
  displayName: string;
  kind: string;
  capacity: number;
}

export interface AddSlotDefinitionRequest {
  slotGroupId: string;
  slotId: string;
  parentNodeId: string;
  address: string;
  displayName: string;
  materialKind: string;
  isEnabled: boolean;
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
  kind: string;
  targetKind: string;
  targetId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDegrees: number;
  layerId: string;
  label: string;
}

export interface LinkProjectTopologyRequest {
  topologyId: string;
}

export interface AutomationTopologySummaryResponse {
  topologyId: string;
  displayName: string;
  nodeCount: number;
  moduleCount: number;
  slotCount: number;
}

export interface AutomationTopologyResponse {
  topologyId: string;
  displayName: string;
  createdAtUtc: string;
  nodes: EquipmentNodeResponse[];
  modules: AutomationModuleResponse[];
  capabilities: CapabilityContractResponse[];
  driverBindings: DriverBindingRouteResponse[];
  slotGroups: SlotGroupResponse[];
  slots: SlotDefinitionResponse[];
}

export interface EquipmentNodeResponse {
  nodeId: string;
  parentNodeId: string | null;
  kind: string;
  displayName: string;
}

export interface AutomationModuleResponse {
  moduleId: string;
  nodeId: string;
  moduleKind: string;
  displayName: string;
  requiredCapabilityIds: string[];
  providedCapabilityIds: string[];
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
  parentNodeId: string;
  displayName: string;
  kind: string;
  capacity: number;
  slotIds: string[];
}

export interface SlotDefinitionResponse {
  slotId: string;
  parentNodeId: string;
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
  kind: string;
  targetKind: string;
  targetId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDegrees: number;
  layerId: string;
  label: string;
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
  timeoutSeconds: number | null;
  inputPayload: string | null;
  scriptLanguage: string | null;
  scriptEditorMode: string | null;
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

export interface StartProcessRuntimeSessionRequest {
  configurationSnapshotId: string;
  serialNumber: string | null;
  batchId: string | null;
  fixtureId: string | null;
  deviceId: string | null;
  actorId: string | null;
}

export interface StartedProcessRuntimeSessionResponse {
  sessionId: string;
  snapshotId?: string;
  configurationSnapshotId: string;
  status: string;
  completedSteps: number;
  commandCount: number;
  incidentCount: number;
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
  timeoutSeconds: number | null;
  inputPayload: string | null;
  scriptEditorMode?: string | null;
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
  pythonCodeTemplate: string;
  isBuiltIn: boolean;
  version: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface RegisterProcessBlocklyBlockDefinitionRequest {
  blockType: string;
  category: string;
  displayName: string;
  blocklyJson: Record<string, unknown>;
  pythonCodeTemplate: string;
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
