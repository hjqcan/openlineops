import type {
  ExecutionStatus,
  ProductionDisposition,
  ProductionTerminalDisposition,
  ResultJudgement
} from './contracts';

export interface ProductionRouteRuntimeOperationSource {
  operationRunId: string;
  operationId: string;
  attempt: number;
  stationSystemId: string;
  executionStatus: ExecutionStatus;
  judgement: ResultJudgement;
  isTerminal: boolean;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
}

export interface ProductionRouteRuntimeDecisionSource {
  sourceOperationRunId: string;
  transitionId: string;
  targetOperationId: string | null;
  terminalDisposition: ProductionTerminalDisposition | null;
  sourceJudgement: ResultJudgement;
  traversal: number;
  decidedAtUtc: string;
}

export interface ProductionRouteRuntimeRunSource {
  productionRunId: string;
  productionUnitId: string;
  productionUnitIdentity: {
    value: string;
  };
  entryOperationId: string;
  disposition: ProductionDisposition;
  isTerminal: boolean;
  operations: readonly ProductionRouteRuntimeOperationSource[];
  routeDecisions: readonly ProductionRouteRuntimeDecisionSource[];
}

export type ProductionRouteMovementKind = 'Entry' | 'Transition' | 'Rework' | 'Terminal';
export type ProductionRouteMovementEvidence = 'RouteDecision' | 'OperationState';
export type ProductionRouteMovementTone = 'active' | 'passed' | 'failed' | 'rework' | 'terminal' | 'neutral';

export interface ProductionRouteRuntimeOperation {
  operationRunId: string;
  operationId: string;
  attempt: number;
  stationSystemId: string;
  executionStatus: ExecutionStatus;
  judgement: ResultJudgement;
}

export interface ProductionRouteRuntimeMovement {
  movementId: string;
  kind: ProductionRouteMovementKind;
  evidence: ProductionRouteMovementEvidence;
  transitionId: string | null;
  traversal: number | null;
  source: ProductionRouteRuntimeOperation | null;
  target: ProductionRouteRuntimeOperation | null;
  targetOperationId: string | null;
  terminalDisposition: ProductionTerminalDisposition | null;
  sourceJudgement: ResultJudgement | null;
  decidedAtUtc: string | null;
  tone: ProductionRouteMovementTone;
}

export interface ProductionRouteRuntimeProjection {
  productionRunId: string;
  productionUnitId: string;
  productionUnitLabel: string;
  currentOperations: ProductionRouteRuntimeOperation[];
  currentMovements: ProductionRouteRuntimeMovement[];
  decisionTrail: ProductionRouteRuntimeMovement[];
  latestDecision: ProductionRouteRuntimeMovement | null;
}

interface IndexedOperation {
  operation: ProductionRouteRuntimeOperationSource;
  index: number;
}

interface IndexedDecision {
  decision: ProductionRouteRuntimeDecisionSource;
  index: number;
}

export function buildProductionRouteRuntimeProjection(
  run: ProductionRouteRuntimeRunSource
): ProductionRouteRuntimeProjection {
  const indexedOperations = run.operations.map((operation, index) => ({ operation, index }));
  const operationByRunId = new Map(indexedOperations.map(item => [item.operation.operationRunId, item]));
  const sortedDecisions = run.routeDecisions
    .map((decision, index) => ({ decision, index }))
    .sort(compareDecisionOrder);
  const decisionTrail = sortedDecisions.map(({ decision }) => {
    const sourceItem = operationByRunId.get(decision.sourceOperationRunId) ?? null;
    const targetItem = decision.targetOperationId === null
      ? null
      : findDecisionTarget(indexedOperations, sourceItem?.index ?? -1, decision.targetOperationId);
    return toDecisionMovement(decision, sourceItem?.operation ?? null, targetItem?.operation ?? null);
  });
  const currentOperationItems = indexedOperations
    .filter(({ operation }) => operation.executionStatus === 'Running' || operation.executionStatus === 'Pending')
    .sort(compareCurrentOperationOrder);
  const currentOperations = currentOperationItems.map(({ operation }) => toRuntimeOperation(operation));
  const currentMovements = currentOperationItems.map(currentItem => {
    const matchingDecision = findLatestTargetDecision(decisionTrail, currentItem.operation.operationRunId);
    if (matchingDecision) {
      return matchingDecision;
    }

    const isRework = currentItem.operation.attempt > 1;
    const sourceItem = isRework
      ? findPreviousCompletedOperation(indexedOperations, currentItem.index)
      : null;
    return toOperationStateMovement(
      run.productionRunId,
      currentItem.operation,
      sourceItem?.operation ?? null,
      isRework);
  });
  const latestDecision = decisionTrail[decisionTrail.length - 1] ?? null;

  if (currentMovements.length === 0 && run.isTerminal && latestDecision?.kind === 'Terminal') {
    currentMovements.push(latestDecision);
  }

  return {
    productionRunId: run.productionRunId,
    productionUnitId: run.productionUnitId,
    productionUnitLabel: run.productionUnitIdentity.value,
    currentOperations,
    currentMovements,
    decisionTrail,
    latestDecision
  };
}

function compareDecisionOrder(left: IndexedDecision, right: IndexedDecision): number {
  return left.decision.decidedAtUtc.localeCompare(right.decision.decidedAtUtc)
    || left.index - right.index;
}

function compareCurrentOperationOrder(left: IndexedOperation, right: IndexedOperation): number {
  const leftRank = left.operation.executionStatus === 'Running' ? 0 : 1;
  const rightRank = right.operation.executionStatus === 'Running' ? 0 : 1;
  return leftRank - rightRank || left.index - right.index;
}

function findDecisionTarget(
  operations: readonly IndexedOperation[],
  sourceIndex: number,
  targetOperationId: string
): IndexedOperation | null {
  const following = operations.find(({ operation, index }) => (
    index > sourceIndex && operation.operationId === targetOperationId));
  if (following) {
    return following;
  }

  for (let index = operations.length - 1; index >= 0; index -= 1) {
    const candidate = operations[index];
    if (candidate?.operation.operationId === targetOperationId) {
      return candidate;
    }
  }
  return null;
}

function findLatestTargetDecision(
  decisions: readonly ProductionRouteRuntimeMovement[],
  operationRunId: string
): ProductionRouteRuntimeMovement | null {
  for (let index = decisions.length - 1; index >= 0; index -= 1) {
    const decision = decisions[index];
    if (decision?.target?.operationRunId === operationRunId) {
      return decision;
    }
  }
  return null;
}

function findPreviousCompletedOperation(
  operations: readonly IndexedOperation[],
  currentIndex: number
): IndexedOperation | null {
  for (let index = currentIndex - 1; index >= 0; index -= 1) {
    const candidate = operations[index];
    if (candidate?.operation.executionStatus === 'Completed') {
      return candidate;
    }
  }
  return null;
}

function toDecisionMovement(
  decision: ProductionRouteRuntimeDecisionSource,
  source: ProductionRouteRuntimeOperationSource | null,
  target: ProductionRouteRuntimeOperationSource | null
): ProductionRouteRuntimeMovement {
  const kind: ProductionRouteMovementKind = decision.terminalDisposition !== null
    ? 'Terminal'
    : target?.attempt && target.attempt > 1
      ? 'Rework'
      : 'Transition';
  return {
    movementId: `decision:${decision.sourceOperationRunId}:${decision.transitionId}:${decision.traversal}`,
    kind,
    evidence: 'RouteDecision',
    transitionId: decision.transitionId,
    traversal: decision.traversal,
    source: source ? toRuntimeOperation(source) : null,
    target: target ? toRuntimeOperation(target) : null,
    targetOperationId: decision.targetOperationId,
    terminalDisposition: decision.terminalDisposition,
    sourceJudgement: decision.sourceJudgement,
    decidedAtUtc: decision.decidedAtUtc,
    tone: movementTone(kind, decision.sourceJudgement, decision.terminalDisposition, target?.executionStatus ?? null)
  };
}

function toOperationStateMovement(
  productionRunId: string,
  target: ProductionRouteRuntimeOperationSource,
  source: ProductionRouteRuntimeOperationSource | null,
  rework: boolean
): ProductionRouteRuntimeMovement {
  const kind: ProductionRouteMovementKind = rework ? 'Rework' : 'Entry';
  return {
    movementId: `operation:${productionRunId}:${target.operationRunId}`,
    kind,
    evidence: 'OperationState',
    transitionId: null,
    traversal: null,
    source: source ? toRuntimeOperation(source) : null,
    target: toRuntimeOperation(target),
    targetOperationId: target.operationId,
    terminalDisposition: null,
    sourceJudgement: source?.judgement ?? null,
    decidedAtUtc: null,
    tone: movementTone(kind, source?.judgement ?? null, null, target.executionStatus)
  };
}

function toRuntimeOperation(
  operation: ProductionRouteRuntimeOperationSource
): ProductionRouteRuntimeOperation {
  return {
    operationRunId: operation.operationRunId,
    operationId: operation.operationId,
    attempt: operation.attempt,
    stationSystemId: operation.stationSystemId,
    executionStatus: operation.executionStatus,
    judgement: operation.judgement
  };
}

function movementTone(
  kind: ProductionRouteMovementKind,
  judgement: ResultJudgement | null,
  terminalDisposition: ProductionTerminalDisposition | null,
  targetStatus: ExecutionStatus | null
): ProductionRouteMovementTone {
  if (kind === 'Rework') {
    return 'rework';
  }
  if (judgement === 'Failed') {
    return 'failed';
  }
  if (kind === 'Terminal') {
    return terminalDisposition === 'Completed' && judgement === 'Passed'
      ? 'passed'
      : 'terminal';
  }
  if (judgement === 'Passed') {
    return 'passed';
  }
  if (targetStatus === 'Running' || targetStatus === 'Pending') {
    return 'active';
  }
  return 'neutral';
}
