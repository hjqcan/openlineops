import type {
  AutomationTopologyResponse,
  ConfigurationSnapshotResponse,
  LineControllerAuthorization,
  ProcessDefinitionSummary,
  ProductionOperationRequest,
  RouteTransitionRequest,
  SaveProductionLineRequest,
  StationProfileResponse
} from './contracts';

export type ProductionDesignerProblemSeverity = 'Error' | 'Warning';
export type ProductionDesignerProblemScope = 'Line' | 'Product' | 'Operation' | 'Transition';

export interface ProductionDesignerProblem {
  id: string;
  severity: ProductionDesignerProblemSeverity;
  scope: ProductionDesignerProblemScope;
  entityId: string;
  message: string;
}

interface ValidationContext {
  topology: AutomationTopologyResponse | null;
  publishedFlows: ProcessDefinitionSummary[];
  configurationSnapshots: ConfigurationSnapshotResponse[];
  stationProfiles: StationProfileResponse[];
}

export function validateProductionLine(
  line: SaveProductionLineRequest,
  context: ValidationContext
): ProductionDesignerProblem[] {
  const problems: ProductionDesignerProblem[] = [];
  const add = (
    scope: ProductionDesignerProblemScope,
    entityId: string,
    message: string,
    severity: ProductionDesignerProblemSeverity = 'Error'
  ): void => {
    problems.push({
      id: `${scope.toLowerCase()}-${problems.length + 1}`,
      severity,
      scope,
      entityId,
      message
    });
  };

  validatePortableId(line.lineDefinitionId, 'Line ID', 'Line', line.lineDefinitionId || 'line', add);
  validateCanonicalText(line.displayName, 'Line display name', 'Line', line.lineDefinitionId || 'line', add);
  validateCanonicalText(line.topologyId, 'Topology reference', 'Line', line.lineDefinitionId || 'line', add);
  validatePortableId(
    line.productModel.productModelId,
    'Product Model ID',
    'Product',
    line.productModel.productModelId || 'product',
    add);
  validateCanonicalText(
    line.productModel.modelCode,
    'Product model code',
    'Product',
    line.productModel.productModelId || 'product',
    add);
  validateCanonicalText(
    line.productModel.identityInputKey,
    'Product identity input key',
    'Product',
    line.productModel.productModelId || 'product',
    add);

  if (context.topology && context.topology.topologyId !== line.topologyId) {
    add('Line', line.lineDefinitionId, 'The line topology reference does not match this Application topology.');
  }

  if (line.operations.length === 0) {
    add('Line', line.lineDefinitionId, 'A production line requires at least one Operation.');
    return problems;
  }

  validateUniqueIds(
    line.operations.map(operation => operation.operationId),
    'Operation',
    'Operation IDs',
    add);
  validateUniqueIds(
    line.transitions.map(transition => transition.transitionId),
    'Transition',
    'Route Transition IDs',
    add);
  validateUniqueIds(
    line.lineControllerAuthorizations.map(authorization => authorization.authorizationId),
    'Operation',
    'Line Controller authorization IDs',
    add);

  const operationById = new Map(line.operations.map(operation => [operation.operationId, operation]));
  const stationById = new Map(
    (context.topology?.systems ?? [])
      .filter(system => system.kind === 'Station')
      .map(station => [station.systemId, station]));
  const flowById = new Map(context.publishedFlows.map(flow => [flow.processDefinitionId, flow]));
  const configurationById = new Map(
    context.configurationSnapshots.map(snapshot => [snapshot.snapshotId, snapshot]));
  const profileById = new Map(context.stationProfiles.map(profile => [profile.stationProfileId, profile]));

  for (const operation of line.operations) {
    validateOperation(
      operation,
      stationById,
      flowById,
      configurationById,
      profileById,
      context.topology,
      add);
  }

  const authorizedActions = new Set<string>();
  for (const authorization of line.lineControllerAuthorizations) {
    const actionKey = `${authorization.operationId}\u001f${authorization.actionId}`;
    if (authorizedActions.has(actionKey)) {
      add(
        'Operation',
        authorization.operationId || authorization.authorizationId,
        `Flow action '${authorization.actionId}' has more than one Line Controller authorization.`);
    }
    authorizedActions.add(actionKey);
    validateLineControllerAuthorization(
      authorization,
      operationById,
      context.topology,
      add);
  }

  validatePortableId(
    line.entryOperationId,
    'Entry Operation reference',
    'Line',
    line.lineDefinitionId,
    add);
  if (!operationById.has(line.entryOperationId)) {
    add('Line', line.lineDefinitionId, `Entry Operation '${line.entryOperationId}' does not exist.`);
  }

  for (const transition of line.transitions) {
    validateTransition(transition, operationById, add);
  }

  validateDuplicateSemanticTransitions(line.transitions, add);
  validateOutgoingShapes(line.operations, line.transitions, add);
  validateRouteGraph(line, operationById, add);
  validateParallelGroups(line.transitions, add);
  return problems;
}

function validateLineControllerAuthorization(
  authorization: LineControllerAuthorization,
  operationById: Map<string, ProductionOperationRequest>,
  topology: AutomationTopologyResponse | null,
  add: AddProblem
): void {
  const entityId = authorization.operationId || authorization.authorizationId || 'line-controller';
  validatePortableId(authorization.authorizationId, 'Line Controller authorization ID', 'Operation', entityId, add);
  validateCanonicalText(authorization.actionId, 'Flow action ID', 'Operation', entityId, add);
  const operation = operationById.get(authorization.operationId);
  if (!operation) {
    add('Operation', entityId, `Line Controller authorization Operation '${authorization.operationId}' does not exist.`);
    return;
  }

  if (!topology) {
    return;
  }

  const controllerBinding = topology.driverBindings.find(binding => (
    binding.bindingId === authorization.controllerBindingId
    && binding.ownerSystemId === authorization.controllerSystemId
    && binding.capabilityId === authorization.controllerCapabilityId));
  const targetBinding = topology.driverBindings.find(binding => (
    binding.bindingId === authorization.targetBindingId
    && binding.ownerSystemId === authorization.targetSystemId
    && binding.capabilityId === authorization.targetCapabilityId));
  const controllerCapability = topology.capabilities.find(capability => (
    capability.capabilityId === authorization.controllerCapabilityId
    && capability.commandName === authorization.controllerAction));
  const targetCapability = topology.capabilities.find(capability => (
    capability.capabilityId === authorization.targetCapabilityId
    && capability.commandName === authorization.targetAction));
  if (!controllerBinding
      || !isSystemWithinStation(
        authorization.controllerSystemId,
        operation.stationSystemId,
        topology)) {
    add('Operation', entityId, 'Controller owner System/Binding/Capability must resolve inside the Operation Station subtree.');
  }
  if (!controllerCapability) {
    add('Operation', entityId, 'Controller Capability/Action must match one topology capability contract.');
  }
  if (!operation.resources.some(resource => resource.kind === 'Device'
      && resource.resolution === 'Fixed'
      && resource.topologyTargetId === authorization.controllerBindingId)) {
    add('Operation', entityId, 'Controller Binding must be an exact Fixed Device resource of the Operation.');
  }
  const targetStation = topology.systems.find(system => (
    system.systemId === authorization.targetStationSystemId && system.kind === 'Station'));
  if (!targetStation
      || targetStation.systemId === operation.stationSystemId
      || !targetBinding
      || !isSystemWithinStation(
        authorization.targetSystemId,
        authorization.targetStationSystemId,
        topology)) {
    add('Operation', entityId, 'Remote Station/System/Binding/Capability must resolve inside a different declared Station subtree.');
  }
  if (!targetCapability) {
    add('Operation', entityId, 'Remote Capability/Action must match one topology capability contract.');
  }
}

function isSystemWithinStation(
  systemId: string,
  stationSystemId: string,
  topology: AutomationTopologyResponse
): boolean {
  let currentId: string | null = systemId;
  for (let depth = 0; depth <= topology.systems.length && currentId; depth += 1) {
    if (currentId === stationSystemId) {
      return true;
    }
    currentId = topology.systems.find(system => system.systemId === currentId)?.parentSystemId ?? null;
  }
  return false;
}

function validateOperation(
  operation: ProductionOperationRequest,
  stationById: Map<string, unknown>,
  flowById: Map<string, ProcessDefinitionSummary>,
  configurationById: Map<string, ConfigurationSnapshotResponse>,
  profileById: Map<string, StationProfileResponse>,
  topology: AutomationTopologyResponse | null,
  add: AddProblem
): void {
  const entityId = operation.operationId || 'operation';
  validatePortableId(operation.operationId, 'Operation ID', 'Operation', entityId, add);
  validateCanonicalText(operation.displayName, 'Operation display name', 'Operation', entityId, add);
  validateCanonicalText(operation.stationSystemId, 'Station System reference', 'Operation', entityId, add);
  validateCanonicalText(operation.flowDefinitionId, 'Flow reference', 'Operation', entityId, add);
  validateCanonicalText(
    operation.configurationSnapshotId,
    'Configuration Snapshot reference',
    'Operation',
    entityId,
    add);

  if (operation.stationSystemId && !stationById.has(operation.stationSystemId)) {
    add('Operation', entityId, `Station System '${operation.stationSystemId}' is not a Station in this Application.`);
  }


  validateOperationResources(operation, topology, add);

  const flow = flowById.get(operation.flowDefinitionId);
  if (operation.flowDefinitionId && !flow) {
    add('Operation', entityId, `Flow '${operation.flowDefinitionId}' is not a published Application Flow.`);
  }

  const configuration = configurationById.get(operation.configurationSnapshotId);
  if (operation.configurationSnapshotId && !configuration) {
    add(
      'Operation',
      entityId,
      `Configuration Snapshot '${operation.configurationSnapshotId}' is not a unique published snapshot.`);
    return;
  }

  if (!configuration) {
    return;
  }

  const profile = profileById.get(configuration.stationProfileId);
  if (!profile || profile.stationSystemId !== operation.stationSystemId) {
    add(
      'Operation',
      entityId,
      `Configuration Snapshot '${configuration.snapshotId}' is not bound to Station '${operation.stationSystemId}'.`);
  }
  if (!flow
      || configuration.processDefinitionId !== flow.processDefinitionId
      || configuration.processVersionId !== flow.versionId) {
    add(
      'Operation',
      entityId,
      `Configuration Snapshot '${configuration.snapshotId}' does not freeze the selected Flow version.`);
  }
}

function validateOperationResources(
  operation: ProductionOperationRequest,
  topology: AutomationTopologyResponse | null,
  add: AddProblem
): void {
  const entityId = operation.operationId || 'operation';
  validateUniqueIds(
    operation.resources.map(resource => resource.bindingId),
    'Operation',
    'Operation Resource Binding IDs',
    add);
  const stationResources = operation.resources.filter(resource => resource.kind === 'Station');
  if (stationResources.length !== 1
      || stationResources[0]?.resolution !== 'Fixed'
      || stationResources[0]?.topologyTargetId !== operation.stationSystemId) {
    add('Operation', entityId, 'Exactly one Fixed Station resource must equal Station System.');
  }
  if (operation.resources.filter(resource => (
    resource.kind === 'Slot' && resource.resolution !== 'Fixed')).length > 1) {
    add('Operation', entityId, 'An Operation can declare at most one dynamic material Slot resource.');
  }

  for (const resource of operation.resources) {
    validatePortableId(resource.bindingId, 'Resource Binding ID', 'Operation', entityId, add);
    validateCanonicalText(resource.topologyTargetId, 'Resource topology target', 'Operation', entityId, add);
    if (resource.kind !== 'Slot' && resource.resolution !== 'Fixed') {
      add('Operation', entityId, `${resource.kind} resources only support Fixed resolution.`);
    }
    if (!topology || resource.kind === 'Station') {
      continue;
    }
    const slot = topology.slots.find(candidate => candidate.slotId === resource.topologyTargetId);
    const group = topology.slotGroups.find(candidate => candidate.slotGroupId === resource.topologyTargetId);
    const system = topology.systems.find(candidate => candidate.systemId === resource.topologyTargetId);
    const driver = topology.driverBindings.find(candidate => candidate.bindingId === resource.topologyTargetId);
    const valid = resource.kind === 'Fixture'
      ? group?.parentSystemId === operation.stationSystemId && group.kind === 'FixtureNest'
      : resource.kind === 'Device'
        ? system?.parentSystemId === operation.stationSystemId
          || (driver !== undefined
            && (driver.providerKind === 'DeviceInstance' || driver.providerKind === 'PluginCommand'))
        : resource.kind === 'SlotGroup'
          ? group?.parentSystemId === operation.stationSystemId
          : resource.resolution === 'CurrentMaterialSlot'
            ? resource.topologyTargetId === operation.stationSystemId
            : resource.resolution === 'AvailableSlotInGroup'
              ? group?.parentSystemId === operation.stationSystemId
              : slot?.parentSystemId === operation.stationSystemId && slot.isEnabled;
    if (!valid) {
      add(
        'Operation',
        entityId,
        `Resource '${resource.bindingId}' does not resolve to an enabled target in Station '${operation.stationSystemId}'.`);
    }
  }
}

function validateTransition(
  transition: RouteTransitionRequest,
  operationById: Map<string, ProductionOperationRequest>,
  add: AddProblem
): void {
  const entityId = transition.transitionId || 'transition';
  validatePortableId(transition.transitionId, 'Route Transition ID', 'Transition', entityId, add);
  if (!operationById.has(transition.sourceOperationId)) {
    add('Transition', entityId, `Source Operation '${transition.sourceOperationId}' does not exist.`);
  }
  if (!operationById.has(transition.targetOperationId)) {
    add('Transition', entityId, `Target Operation '${transition.targetOperationId}' does not exist.`);
  }
  if (transition.sourceOperationId === transition.targetOperationId) {
    add('Transition', entityId, 'A Route Transition cannot target its source Operation.');
  }

  const conditional = transition.kind === 'Judgement' || transition.kind === 'Rework';
  if (conditional !== (transition.requiredJudgement !== null)) {
    add(
      'Transition',
      entityId,
      conditional
        ? `${transition.kind} requires exactly one Result Judgement.`
        : `${transition.kind} cannot define a Result Judgement.`);
  }

  if (transition.kind === 'Condition') {
    validatePortableId(
      transition.outputKey ?? '',
      'Production Context Output Key',
      'Transition',
      entityId,
      add);
    if (transition.expectedOutputKind === null || transition.expectedOutputValue === null) {
      add('Transition', entityId, 'Condition requires one typed expected Production Context value.');
    } else if (!isCanonicalContextValue(
      transition.expectedOutputKind,
      transition.expectedOutputValue)) {
      add(
        'Transition',
        entityId,
        `Expected value '${transition.expectedOutputValue}' is not canonical for ${transition.expectedOutputKind}.`);
    }
  } else if (transition.outputKey !== null
      || transition.expectedOutputKind !== null
      || transition.expectedOutputValue !== null) {
    add('Transition', entityId, `${transition.kind} cannot define a typed output condition.`);
  }

  if (transition.kind === 'Rework') {
    if (!Number.isInteger(transition.maxTraversals) || (transition.maxTraversals ?? 0) <= 0) {
      add('Transition', entityId, 'Rework requires a positive whole-number traversal limit.');
    }
  } else if (transition.maxTraversals !== null) {
    add('Transition', entityId, `${transition.kind} cannot define a traversal limit.`);
  }

  const parallel = transition.kind === 'ParallelFork' || transition.kind === 'ParallelJoin';
  if (parallel) {
    validatePortableId(
      transition.parallelGroupId ?? '',
      'Parallel Group ID',
      'Transition',
      entityId,
      add);
  } else if (transition.parallelGroupId !== null) {
    add('Transition', entityId, `${transition.kind} cannot define a Parallel Group ID.`);
  }
}

function validateDuplicateSemanticTransitions(
  transitions: RouteTransitionRequest[],
  add: AddProblem
): void {
  const semanticKeys = new Map<string, string>();
  for (const transition of transitions) {
    const key = `${transition.sourceOperationId}\u0000${transition.targetOperationId}\u0000${transition.kind}`;
    const existing = semanticKeys.get(key);
    if (existing) {
      add(
        'Transition',
        transition.transitionId,
        `Duplicates the semantic route defined by '${existing}'.`);
    } else {
      semanticKeys.set(key, transition.transitionId);
    }
  }
}

function validateOutgoingShapes(
  operations: ProductionOperationRequest[],
  transitions: RouteTransitionRequest[],
  add: AddProblem
): void {
  const outgoingByOperation = groupBy(transitions, transition => transition.sourceOperationId);
  for (const operation of operations) {
    const outgoing = outgoingByOperation.get(operation.operationId) ?? [];
    if (outgoing.length === 0) {
      continue;
    }

    const singleForward = outgoing.length === 1
      && (outgoing[0].kind === 'Sequence' || outgoing[0].kind === 'ParallelJoin');
    const conditional = outgoing.every(transition => (
      transition.kind === 'Judgement' || transition.kind === 'Rework'));
    const outputConditions = outgoing.every(transition => transition.kind === 'Condition');
    const fork = outgoing.length >= 2
      && outgoing.every(transition => transition.kind === 'ParallelFork')
      && new Set(outgoing.map(transition => transition.parallelGroupId)).size === 1;
    if (!singleForward && !conditional && !outputConditions && !fork) {
      add(
        'Operation',
        operation.operationId,
        'Outgoing routes must be one Sequence/Parallel Join, unique Judgement/Rework branches, one typed Condition branch set, or one Parallel Fork group.');
      continue;
    }

    if (conditional) {
      const judgements = outgoing.map(transition => transition.requiredJudgement);
      if (new Set(judgements).size !== judgements.length) {
        add('Operation', operation.operationId, 'Outgoing Result Judgements must be unique.');
      }
    }
    if (outputConditions) {
      const outputKeys = new Set(outgoing.map(transition => transition.outputKey));
      const typedValues = outgoing.map(transition => (
        `${transition.expectedOutputKind ?? ''}\u0000${transition.expectedOutputValue ?? ''}`));
      if (outputKeys.size !== 1) {
        add('Operation', operation.operationId, 'Condition branches from one Operation must use one Output Key.');
      }
      if (new Set(typedValues).size !== typedValues.length) {
        add('Operation', operation.operationId, 'Condition branches must use unique typed expected values.');
      }
    }
  }
}

function validateRouteGraph(
  line: SaveProductionLineRequest,
  operationById: Map<string, ProductionOperationRequest>,
  add: AddProblem
): void {
  const forward = line.transitions.filter(transition => transition.kind !== 'Rework');
  if (forward.some(transition => transition.targetOperationId === line.entryOperationId)) {
    add('Line', line.lineDefinitionId, 'The Entry Operation cannot have an incoming forward Route Transition.');
  }

  if (operationById.has(line.entryOperationId)) {
    const reachable = traverse(line.entryOperationId, line.transitions);
    for (const operation of line.operations) {
      if (!reachable.has(operation.operationId)) {
        add(
          'Operation',
          operation.operationId,
          `Operation is not reachable from Entry Operation '${line.entryOperationId}'.`);
      }
    }
  }

  const indegrees = new Map(line.operations.map(operation => [operation.operationId, 0]));
  for (const transition of forward) {
    if (indegrees.has(transition.targetOperationId)) {
      indegrees.set(transition.targetOperationId, (indegrees.get(transition.targetOperationId) ?? 0) + 1);
    }
  }
  const queue = [...indegrees.entries()].filter(([, degree]) => degree === 0).map(([id]) => id);
  let visited = 0;
  while (queue.length > 0) {
    const operationId = queue.shift();
    if (!operationId) {
      continue;
    }
    visited += 1;
    for (const transition of forward.filter(candidate => candidate.sourceOperationId === operationId)) {
      const nextDegree = (indegrees.get(transition.targetOperationId) ?? 0) - 1;
      indegrees.set(transition.targetOperationId, nextDegree);
      if (nextDegree === 0) {
        queue.push(transition.targetOperationId);
      }
    }
  }
  if (visited !== line.operations.length) {
    add(
      'Line',
      line.lineDefinitionId,
      'The forward route contains a cycle. Every loop must be an explicitly bounded Rework transition.');
  }

  const forwardSources = new Set(forward.map(transition => transition.sourceOperationId));
  const terminals = line.operations
    .map(operation => operation.operationId)
    .filter(operationId => !forwardSources.has(operationId));
  if (terminals.length === 0) {
    add('Line', line.lineDefinitionId, 'The route requires at least one terminal Operation.');
  } else {
    const reverse = forward.map(transition => ({
      ...transition,
      sourceOperationId: transition.targetOperationId,
      targetOperationId: transition.sourceOperationId
    }));
    const completable = new Set<string>();
    for (const terminal of terminals) {
      for (const operationId of traverse(terminal, reverse)) {
        completable.add(operationId);
      }
    }
    for (const operation of line.operations) {
      if (!completable.has(operation.operationId)) {
        add('Operation', operation.operationId, 'Operation has no forward path to a terminal disposition.');
      }
    }
  }

  for (const rework of line.transitions.filter(transition => transition.kind === 'Rework')) {
    if (!traverse(rework.targetOperationId, forward).has(rework.sourceOperationId)) {
      add(
        'Transition',
        rework.transitionId,
        'Rework must return to an earlier Operation on its forward route.');
    }
  }
}

function validateParallelGroups(
  transitions: RouteTransitionRequest[],
  add: AddProblem
): void {
  const groups = groupBy(
    transitions.filter(transition => transition.parallelGroupId !== null),
    transition => transition.parallelGroupId ?? '');
  for (const [groupId, group] of groups) {
    const forks = group.filter(transition => transition.kind === 'ParallelFork');
    const joins = group.filter(transition => transition.kind === 'ParallelJoin');
    const validShape = forks.length >= 2
      && joins.length >= 2
      && forks.length === joins.length
      && new Set(forks.map(transition => transition.sourceOperationId)).size === 1
      && new Set(joins.map(transition => transition.targetOperationId)).size === 1
      && new Set(forks.map(transition => transition.targetOperationId)).size === forks.length
      && new Set(joins.map(transition => transition.sourceOperationId)).size === joins.length;
    if (!validShape) {
      add(
        'Transition',
        group[0]?.transitionId ?? groupId,
        `Parallel Group '${groupId}' must have one fork and one join with the same number of distinct branches.`);
      continue;
    }
    if (forks[0].sourceOperationId === joins[0].targetOperationId) {
      add(
        'Transition',
        group[0].transitionId,
        `Parallel Group '${groupId}' must use distinct fork and join Operations.`);
    }
  }
}

function validateUniqueIds(
  ids: string[],
  scope: ProductionDesignerProblemScope,
  label: string,
  add: AddProblem
): void {
  const exact = new Set<string>();
  const insensitive = new Map<string, string>();
  for (const id of ids) {
    if (exact.has(id)) {
      add(scope, id || scope.toLowerCase(), `${label} must be unique; '${id}' is duplicated.`);
      continue;
    }
    exact.add(id);
    const folded = id.toLocaleLowerCase('en-US');
    const caseVariant = insensitive.get(folded);
    if (caseVariant && caseVariant !== id) {
      add(
        scope,
        id,
        `${label} '${caseVariant}' and '${id}' differ only by case and are not portable between filesystems.`);
    } else {
      insensitive.set(folded, id);
    }
  }
}

function validatePortableId(
  value: string,
  label: string,
  scope: ProductionDesignerProblemScope,
  entityId: string,
  add: AddProblem
): void {
  if (!isPortableId(value)) {
    add(
      scope,
      entityId,
      `${label} must be a portable segment of at most 128 letters, digits, '.', '-' or '_' characters.`);
  }
}

function validateCanonicalText(
  value: string,
  label: string,
  scope: ProductionDesignerProblemScope,
  entityId: string,
  add: AddProblem
): void {
  if (!isCanonicalText(value)) {
    add(scope, entityId, `${label} must be non-empty without leading or trailing whitespace.`);
  }
}

function isCanonicalText(value: string): boolean {
  return value.length > 0 && value.trim() === value;
}

function isPortableId(value: string): boolean {
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(value)
      || value === '.'
      || value === '..'
      || value.endsWith('.')) {
    return false;
  }
  const base = value.split('.', 1)[0].toUpperCase();
  return !['CON', 'PRN', 'AUX', 'NUL'].includes(base)
    && !/^(COM|LPT)[1-9]$/.test(base);
}

function isCanonicalContextValue(kind: string, value: string): boolean {
  if (!isCanonicalText(value)) {
    return false;
  }
  switch (kind) {
    case 'Text':
      return true;
    case 'Boolean':
      return value === 'true' || value === 'false';
    case 'WholeNumber':
      try {
        return BigInt(value).toString() === value;
      } catch {
        return false;
      }
    case 'FixedPoint':
      return /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(value);
    case 'DateTimeUtc':
      return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$/.test(value)
        && !Number.isNaN(Date.parse(`${value.slice(0, 23)}Z`));
    default:
      return false;
  }
}

function traverse(
  start: string,
  transitions: Array<Pick<RouteTransitionRequest, 'sourceOperationId' | 'targetOperationId'>>
): Set<string> {
  const outgoing = groupBy(transitions, transition => transition.sourceOperationId);
  const reachable = new Set<string>();
  const pending = [start];
  while (pending.length > 0) {
    const operationId = pending.pop();
    if (!operationId || !reachable.add(operationId)) {
      continue;
    }
    for (const transition of outgoing.get(operationId) ?? []) {
      pending.push(transition.targetOperationId);
    }
  }
  return reachable;
}

function groupBy<T>(items: T[], keySelector: (item: T) => string): Map<string, T[]> {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const key = keySelector(item);
    const group = groups.get(key);
    if (group) {
      group.push(item);
    } else {
      groups.set(key, [item]);
    }
  }
  return groups;
}

type AddProblem = (
  scope: ProductionDesignerProblemScope,
  entityId: string,
  message: string,
  severity?: ProductionDesignerProblemSeverity
) => void;
