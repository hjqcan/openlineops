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

export interface RuntimeProductionUnitIdentity {
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
  operationId: string;
  operationAttempt: number;
  stationSystemId: string;
  productionUnitIdentity: RuntimeProductionUnitIdentity;
  runtimeStationId: string;
  latestSessionId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  lotId: string | null;
  carrierId: string | null;
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
  operationId: string;
  operationAttempt: number;
  stationSystemId: string;
  productionUnitIdentity: RuntimeProductionUnitIdentity;
  runtimeStationId: string;
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
  operationId: string;
  operationAttempt: number;
  stationSystemId: string;
  productionUnitIdentity: RuntimeProductionUnitIdentity;
  runtimeStationId: string;
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
  productModelId: string;
  productionUnitIdentityInputKey: string;
  productionUnitIdentityValue: string;
  lotId: string | null;
  carrierId: string | null;
  actorId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  disposition: ProductionDisposition;
  completedAtUtc: string;
  operationCount: number;
  failedOperationCount: number;
  commandCount: number;
  measurementCount: number;
  artifactCount: number;
  incidentCount: number;
  routeDecisionCount: number;
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
  productModelId: string;
  productionUnitIdentityInputKey: string;
  productionUnitIdentityValue: string;
  lotId: string | null;
  carrierId: string | null;
  actorId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  disposition: ProductionDisposition;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string;
  failureCode: string | null;
  failureReason: string | null;
  operations: TraceOperationExecutionResponse[];
  routeDecisions: TraceRouteDecisionResponse[];
  auditEntries: AuditEntryResponse[];
}

export interface TraceOperationExecutionResponse {
  operationRunId: string;
  operationId: string;
  attempt: number;
  stationSystemId: string;
  stationId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  runtimeSessionId: string | null;
  runtimeSessionStatus: string | null;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
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
  outputs: TraceOperationOutputResponse[];
  fencingTokens: TraceResourceFencingTokenResponse[];
}

export interface TraceRouteDecisionResponse {
  sourceOperationRunId: string;
  transitionId: string;
  targetOperationId: string;
  sourceJudgement: ProductionResultJudgement;
  traversal: number;
  decidedAtUtc: string;
}

export interface TraceOperationOutputResponse {
  key: string;
  valueKind: ProductionContextValueKind;
  canonicalJson: string;
}

export interface TraceResourceFencingTokenResponse {
  resourceKind: string;
  resourceId: string;
  fencingToken: number;
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
  resultJudgement: ProductionResultJudgement | null;
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
  packageFormat: string;
  exportedAtUtc: string;
  traceRecord: TraceRecordResponse;
}

export interface EngineeringTraceSearchQuery {
  productionRunId?: string;
  productModelId?: string;
  productionUnitIdentityInputKey?: string;
  productionUnitIdentityValue?: string;
  lotId?: string;
  carrierId?: string;
  actorId?: string;
  executionStatus?: string;
  judgement?: string;
  disposition?: string;
  projectId?: string;
  applicationId?: string;
  projectSnapshotId?: string;
  topologyId?: string;
  productionLineDefinitionId?: string;
  operationId?: string;
  stationSystemId?: string;
  stationId?: string;
  processDefinitionId?: string;
  processVersionId?: string;
  configurationSnapshotId?: string;
  recipeSnapshotId?: string;
  resourceKind?: string;
  resourceId?: string;
  deviceId?: string;
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
  productModelId: string;
  productionUnitIdentityInputKey: string;
  productionUnitIdentityValue: string;
  lotId: string | null;
  carrierId: string | null;
  actorId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  disposition: ProductionDisposition;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string;
  operationCount: number;
  failedOperationCount: number;
  commandCount: number;
  failedCommandCount: number;
  measurementCount: number;
  failedMeasurementCount: number;
  artifactCount: number;
  incidentCount: number;
  routeDecisionCount: number;
}

export interface EngineeringTraceSearchFacetsResponse {
  judgements: TraceFacetCountResponse[];
  executionStatuses: TraceFacetCountResponse[];
  dispositions: TraceFacetCountResponse[];
  stationSystems: TraceFacetCountResponse[];
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
  ownerSystemId: string;
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
  ownerSystemId: string;
  ownerStationSystemId: string;
}

export interface ProjectTargetReferenceResponse {
  kind: string;
  targetId: string;
}

export interface ProjectReleaseProductionRunContextResponse {
  projectId: string;
  applicationId: string;
  snapshotId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  productModelId: string;
  productModelIdentityInputKey: string;
  entryOperationId: string;
  entryStationSystemId: string;
  stationSystemIds: string[];
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
  ownerSystemId: string;
  ownerStationSystemId: string;
}

export interface ProjectTargetReferenceRequest {
  kind: string;
  targetId: string;
}

export interface SubmitProductionRunRequest {
  projectId: string;
  projectSnapshotId: string;
  productionRunId: string;
  productionUnitId: string;
  actorId: string;
}

export interface RegisterProductionUnitRequest {
  productionUnitId: string;
  productModelId: string;
  identityKey: string;
  identityValue: string;
  lotId: string | null;
  actorId: string;
  occurredAtUtc: string;
}

export interface MaterialArrivalRequest {
  lineId: string;
  stationSystemId: string;
  actorId: string;
  occurredAtUtc: string;
}

export interface ProductionUnitResponse {
  productionUnitId: string;
  productModelId: string;
  identityKey: string;
  identityValue: string;
  lotId: string | null;
  registeredBy: string;
  registeredAtUtc: string;
  lastTransitionAtUtc: string;
  lastLocationTransitionAtUtc: string;
  lastDispositionTransitionAtUtc: string;
  disposition: ProductionDisposition;
  dispositionBeforeHold: ProductionDisposition | null;
  activeProductionRunId: string | null;
  lastProductionRunId: string | null;
  lastProductionRunRevision: number;
  dispositionReason: string | null;
  location: MaterialLocationResponse | null;
}

export type ProductionExecutionStatus =
  | 'Pending'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'TimedOut'
  | 'Canceled'
  | 'Rejected';

export type ProductionResultJudgement =
  | 'Passed'
  | 'Failed'
  | 'Aborted'
  | 'Unknown'
  | 'NotApplicable';

export type ProductionDisposition =
  | 'InProcess'
  | 'Completed'
  | 'Nonconforming'
  | 'Held'
  | 'Scrapped';

export type ProductionRunControlState =
  | 'Active'
  | 'Paused'
  | 'Held'
  | 'RecoveryRequired'
  | 'StopRequested'
  | 'SafeStopped';

export type OperatorProductionRunCommand =
  | 'Pause'
  | 'Continue'
  | 'Stop'
  | 'Cancel'
  | 'Hold'
  | 'Release'
  | 'Rework'
  | 'Scrap'
  | 'SafeStop'
  | 'Reconcile'
  | 'Retry'
  | 'Abort';

export interface RuntimeProductionUnitIdentityResponse {
  modelId: string;
  inputKey: string;
  value: string;
}

export interface ProductionRunReadModel {
  productionRunId: string;
  productionUnitId: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  topologyId: string;
  productionLineDefinitionId: string;
  productionUnitIdentity: RuntimeProductionUnitIdentityResponse;
  lotId: string | null;
  carrierId: string | null;
  actorId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  disposition: ProductionDisposition;
  controlState: ProductionRunControlState;
  isTerminal: boolean;
  createdAtUtc: string;
  lastTransitionAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  failureCode: string | null;
  failureReason: string | null;
  entryOperationId: string;
  completedOperationCount: number;
  completedStepCount: number;
  commandCount: number;
  incidentCount: number;
  operations: ProductionOperationRunReadModel[];
  routeDecisions: ProductionRouteDecisionReadModel[];
  recoveryDecisions: ProductionRecoveryDecisionReadModel[];
}

export interface ProductionOperationRunReadModel {
  operationRunId: string;
  operationId: string;
  attempt: number;
  stationSystemId: string;
  runtimeStationId: string;
  processDefinitionId: string;
  processVersionId: string;
  configurationSnapshotId: string;
  recipeSnapshotId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  isTerminal: boolean;
  runtimeSessionId: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  failureCode: string | null;
  failureReason: string | null;
  completedStepCount: number;
  commandCount: number;
  incidentCount: number;
  resources: ProductionRunResourceReadModel[];
  outputs: ProductionRunOutputReadModel[];
}

export interface ProductionRunResourceReadModel {
  kind: string;
  resourceId: string;
  fencingToken: number | null;
}

export interface ProductionRunOutputReadModel {
  key: string;
  kind: string;
  canonicalValue: string;
}

export interface ProductionRouteDecisionReadModel {
  sourceOperationRunId: string;
  transitionId: string;
  targetOperationId: string;
  sourceJudgement: ProductionResultJudgement;
  traversal: number;
  decidedAtUtc: string;
}

export interface ProductionRecoveryDecisionReadModel {
  decisionId: string;
  kind: 'Reconcile' | 'Retry' | 'Abort' | 'Scrap';
  actorId: string;
  reason: string;
  evidenceReference: string;
  decidedAtUtc: string;
  operationRunId: string | null;
  operationId: string | null;
  observedJudgement: ProductionResultJudgement | null;
  observedOutputs: ProductionRunOutputReadModel[];
}

export interface ActiveProductionRunsResponse {
  runs: ProductionRunReadModel[];
}

export interface ProductionLineRuntimeStateResponse {
  productionLineDefinitionId: string;
  generatedAtUtc: string;
  activeRunCount: number;
  activeRuns: ProductionRunReadModel[];
  productionUnits: ProductionLineProductionUnitStateResponse[];
  stations: ProductionLineStationStateResponse[];
  slots: ProductionLineSlotStateResponse[];
  carriers: ProductionLineCarrierStateResponse[];
}

export type MaterialLocationKind = 'StationQueue' | 'Slot' | 'CarrierPosition';

export interface MaterialLocationResponse {
  kind: MaterialLocationKind;
  lineId: string | null;
  stationSystemId: string | null;
  slotId: string | null;
  carrierId: string | null;
  carrierPositionId: string | null;
}

export interface ProductionLineProductionUnitStateResponse {
  productionUnitId: string;
  productModelId: string;
  identityKey: string;
  identityValue: string;
  disposition: ProductionDisposition;
  judgement: ProductionResultJudgement;
  productionRunId: string | null;
  location: MaterialLocationResponse | null;
  lastTransitionAtUtc: string;
  activeOperationRunIds: string[];
}

export type ProductionLineStationRuntimeStatus =
  | 'Idle'
  | 'Queued'
  | 'WaitingForResources'
  | 'Running'
  | 'Blocked'
  | 'Offline';

export type ProductionMaterialKind = 'ProductionUnit' | 'Carrier';

export interface ProductionLineStationStateResponse {
  stationSystemId: string;
  status: ProductionLineStationRuntimeStatus;
  queue: ProductionLineQueuedMaterialResponse[];
  activeOperations: ProductionLineStationOperationStateResponse[];
}

export interface ProductionLineQueuedMaterialResponse {
  materialKind: ProductionMaterialKind;
  materialId: string;
  queuedAtUtc: string;
}

export interface ProductionLineStationOperationStateResponse {
  productionRunId: string;
  productionUnitId: string | null;
  productionUnitIdentity: RuntimeProductionUnitIdentityResponse;
  operationRunId: string;
  operationId: string;
  executionStatus: ProductionExecutionStatus;
  judgement: ProductionResultJudgement;
  startedAtUtc: string | null;
  resources: ProductionLineResourceStateResponse[];
}

export type ProductionResourceKind = 'Station' | 'Slot' | 'Fixture' | 'Device' | 'SlotGroup';

export type ProductionLineResourceRuntimeStatus =
  | 'Waiting'
  | 'Leased'
  | 'RecoveryHeld'
  | 'Expired'
  | 'Missing';

export interface ProductionLineResourceStateResponse {
  kind: ProductionResourceKind;
  resourceId: string;
  status: ProductionLineResourceRuntimeStatus;
  fencingToken: number | null;
  acquiredAtUtc: string | null;
  expiresAtUtc: string | null;
}

export type ProductionSlotOccupancyStatus =
  | 'Available'
  | 'Reserved'
  | 'Occupied'
  | 'Running'
  | 'Blocked'
  | 'Offline';

export interface ProductionLineSlotStateResponse {
  stationSystemId: string;
  slotId: string;
  status: ProductionSlotOccupancyStatus;
  materialKind: ProductionMaterialKind | null;
  materialId: string | null;
  lastTransitionAtUtc: string;
}

export interface ProductionLineCarrierStateResponse {
  carrierId: string;
  carrierTypeId: string;
  capacity: number;
  location: MaterialLocationResponse | null;
  lastTransitionAtUtc: string;
  productionUnits: ProductionLineCarrierPositionStateResponse[];
}

export interface ProductionLineCarrierPositionStateResponse {
  carrierPositionId: string;
  productionUnitId: string;
  disposition: ProductionDisposition;
  judgement: ProductionResultJudgement;
}

export interface ProductionOperationsFilters {
  productionLineDefinitionId: string;
  stationSystemId: string;
  slotId: string;
}

export interface ProductionRunCommandRequest {
  actorId: string;
  reason: string | null;
  operationId: string | null;
  recoveryDecision: ProductionRecoveryDecisionRequest | null;
}

export interface ProductionContextValueRequest {
  kind: 'Text' | 'Boolean' | 'WholeNumber' | 'FixedPoint' | 'DateTimeUtc';
  canonicalValue: string;
}

export interface ProductionRecoveryDecisionRequest {
  decisionId: string;
  evidenceReference: string;
  decidedAtUtc: string;
  operationRunId: string | null;
  operationId: string | null;
  observedJudgement: 'Passed' | 'Failed' | 'NotApplicable' | null;
  observedOutputs: Record<string, ProductionContextValueRequest>;
}

export type StationEmergencyStopStatus = 'Pending' | 'Acknowledged' | 'Rejected';

export interface RequestStationEmergencyStopRequest {
  messageId: string;
  idempotencyKey: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  actorId: string;
  reason: string;
  requestedAtUtc: string;
}

export interface StationSafetyEvidenceResponse {
  sequence: number;
  kind: 'EmergencyStopRequested'
    | 'EmergencyStopDispatchFailed'
    | 'EmergencyStopAcknowledged'
    | 'EmergencyStopRejected';
  messageId: string;
  occurredAtUtc: string;
  failureCode: string | null;
  failureReason: string | null;
}

export interface StationEmergencyStopResponse {
  messageId: string;
  idempotencyKey: string;
  projectId: string;
  applicationId: string;
  projectSnapshotId: string;
  stationSystemId: string;
  agentId: string;
  stationId: string;
  relatedProductionRunIds: string[];
  actorId: string;
  reason: string;
  requestedAtUtc: string;
  status: StationEmergencyStopStatus;
  acknowledgementMessageId: string | null;
  acknowledgedAtUtc: string | null;
  failureCode: string | null;
  failureReason: string | null;
  dispatchAttemptCount: number;
  lastDispatchFailure: string | null;
  lastUpdatedAtUtc: string;
  replayed: boolean;
  evidence: StationSafetyEvidenceResponse[];
}

export interface StationSafetyEventsResponse {
  events: StationEmergencyStopResponse[];
}

export interface StationSafetyTraceSearchResponse {
  items: StationEmergencyStopResponse[];
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
  ownerSystemId: string;
  capabilityId: string;
  providerKind: string;
  providerKey: string;
}

export interface UpdateDriverBindingRequest {
  ownerSystemId: string;
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
  revision: string;
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
  ownerSystemId: string;
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
  revision: string;
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
  productModelCode: string;
  operationCount: number;
  updatedAtUtc: string;
}

export interface ProductionLineResponse {
  lineDefinitionId: string;
  displayName: string;
  topologyId: string;
  productModel: ProductModelResponse;
  entryOperationId: string;
  operations: ProductionOperationResponse[];
  transitions: RouteTransitionResponse[];
  lineControllerAuthorizations: LineControllerAuthorization[];
  createdAtUtc: string;
  updatedAtUtc: string;
  revision: string;
}

export interface ProductModelResponse {
  productModelId: string;
  modelCode: string;
  identityInputKey: string;
}

export interface ProductionOperationResponse {
  operationId: string;
  displayName: string;
  stationSystemId: string;
  flowDefinitionId: string;
  configurationSnapshotId: string;
  resources: OperationResourceBinding[];
}

export type OperationResourceKind = 'Station' | 'Fixture' | 'Device' | 'SlotGroup' | 'Slot';

export type OperationResourceResolution = 'Fixed' | 'CurrentMaterialSlot' | 'AvailableSlotInGroup';

export interface OperationResourceBinding {
  bindingId: string;
  kind: OperationResourceKind;
  topologyTargetId: string;
  resolution: OperationResourceResolution;
}

export interface LineControllerAuthorization {
  authorizationId: string;
  operationId: string;
  actionId: string;
  controllerSystemId: string;
  controllerBindingId: string;
  controllerCapabilityId: string;
  controllerAction: string;
  targetStationSystemId: string;
  targetSystemId: string;
  targetBindingId: string;
  targetCapabilityId: string;
  targetAction: string;
}

export type RouteTransitionKind =
  | 'Sequence'
  | 'Judgement'
  | 'Condition'
  | 'Rework'
  | 'ParallelFork'
  | 'ParallelJoin';

export type ProductionContextValueKind =
  | 'Text'
  | 'Boolean'
  | 'WholeNumber'
  | 'FixedPoint'
  | 'DateTimeUtc';

export type RouteJudgement =
  | 'Passed'
  | 'Failed'
  | 'Aborted'
  | 'Unknown'
  | 'NotApplicable';

export interface RouteTransitionResponse {
  transitionId: string;
  sourceOperationId: string;
  targetOperationId: string;
  kind: RouteTransitionKind;
  requiredJudgement: RouteJudgement | null;
  maxTraversals: number | null;
  parallelGroupId: string | null;
  outputKey: string | null;
  expectedOutputKind: ProductionContextValueKind | null;
  expectedOutputValue: string | null;
}

export interface SaveProductionLineRequest {
  lineDefinitionId: string;
  displayName: string;
  topologyId: string;
  productModel: ProductModelRequest;
  entryOperationId: string;
  operations: ProductionOperationRequest[];
  transitions: RouteTransitionRequest[];
  lineControllerAuthorizations: LineControllerAuthorization[];
}

export interface ProductModelRequest {
  productModelId: string;
  modelCode: string;
  identityInputKey: string;
}

export interface ProductionOperationRequest {
  operationId: string;
  displayName: string;
  stationSystemId: string;
  flowDefinitionId: string;
  configurationSnapshotId: string;
  resources: OperationResourceBinding[];
}

export interface RouteTransitionRequest {
  transitionId: string;
  sourceOperationId: string;
  targetOperationId: string;
  kind: RouteTransitionKind;
  requiredJudgement: RouteJudgement | null;
  maxTraversals: number | null;
  parallelGroupId: string | null;
  outputKey: string | null;
  expectedOutputKind: ProductionContextValueKind | null;
  expectedOutputValue: string | null;
}

export type ExternalProgramLaunchKind = 'ApplicationExecutable' | 'Provider';

export interface ExternalProgramResourceResponse {
  resourceId: string;
  displayName: string;
  capabilityId: string;
  commandName: string;
  launchKind: ExternalProgramLaunchKind;
  entryPoint: string | null;
  providerKind: string | null;
  providerKey: string | null;
  argumentTemplates: string[];
  inputMappings: ExternalProgramInputMapping[];
  resultMappings: ExternalProgramResultMapping[];
  outcomeMapping: ExternalProgramOutcomeMapping;
  permissionProfile: ExternalProgramPermissionProfile;
  executionLimits: ExternalProgramExecutionLimits;
  files: ExternalProgramFileResponse[];
  contentSha256: string;
  updatedAtUtc: string;
  revision: string;
}

export interface SaveExternalProgramResourceRequest {
  resourceId: string;
  displayName: string;
  capabilityId: string;
  commandName: string;
  launchKind: ExternalProgramLaunchKind;
  entryPoint: string | null;
  providerKind: string | null;
  providerKey: string | null;
  argumentTemplates: string[];
  inputMappings: ExternalProgramInputMapping[];
  resultMappings: ExternalProgramResultMapping[];
  outcomeMapping: ExternalProgramOutcomeMapping;
  permissionProfile: ExternalProgramPermissionProfile;
  executionLimits: ExternalProgramExecutionLimits;
}

export interface ExternalProgramInputMapping {
  source: string;
  target: string;
}

export interface ExternalProgramResultMapping {
  sourcePath: string;
  targetKey: string;
  valueKind: ProductionContextValueKind;
}

export interface ExternalProgramOutcomeMapping {
  sourcePath: string;
  passedToken: string;
  failedToken: string;
  abortedToken: string;
}

export interface ExternalProgramPermissionProfile {
  profileName: 'Restricted';
  networkAccessAllowed: boolean;
  allowedEnvironmentVariables: string[];
}

export interface ExternalProgramExecutionLimits {
  timeoutMilliseconds: number;
  maximumProcessCount: number;
  maximumWorkingSetBytes: number;
  maximumCpuTimeMilliseconds: number;
  maximumStandardOutputBytes: number;
  maximumStandardErrorBytes: number;
  maximumArtifactCount: number;
  maximumArtifactBytes: number;
  maximumTotalArtifactBytes: number;
}

export interface ExternalProgramFileResponse {
  relativePath: string;
  sizeBytes: number;
  sha256: string;
}

export type ExternalProgramTrialInputKind =
  | 'Text'
  | 'IntegralNumber'
  | 'FractionalNumber'
  | 'Logical';

export interface ExternalProgramTrialInput {
  kind: ExternalProgramTrialInputKind;
  canonicalValue: string;
}

export interface ExternalProgramTrialRequest {
  inputs: Record<string, ExternalProgramTrialInput>;
}

export interface ExternalProgramTrialResponse {
  resourceId: string;
  launchKind: ExternalProgramLaunchKind;
  contentSha256: string;
  executionStatus: string;
  judgement: string;
  resultPayload: string | null;
  failureReason: string | null;
  artifacts: ExternalProgramTrialArtifactResponse[];
}

export interface ExternalProgramTrialArtifactResponse {
  name: string;
  kind: string;
  mediaType: string | null;
  sizeBytes: number;
  sha256: string;
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
  ownerSystemId: string;
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
  revision: string;
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
