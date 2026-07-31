import type {
  OperatorProductionRunCommand,
  ProductionRunReadModel
} from './contracts';

export function canIssueProductionRunCommand(
  command: OperatorProductionRunCommand,
  run: ProductionRunReadModel
): boolean {
  if (run.isTerminal) {
    return false;
  }
  if (run.controlState === 'RecoveryRequired') {
    switch (command) {
      case 'Reconcile': return run.operations.some(operation => operation.executionStatus === 'Running');
      case 'Retry': return run.operations.length > 0;
      case 'Abort':
      case 'Scrap': return true;
      default: return false;
    }
  }
  const isRunning = run.executionStatus === 'Running';
  switch (command) {
    case 'Pause': return isRunning && run.controlState === 'Active';
    case 'Continue': return isRunning && run.controlState === 'Paused';
    case 'Stop': return run.controlState !== 'StopRequested';
    case 'Hold': return isRunning
      && (run.controlState === 'Active' || run.controlState === 'Paused');
    case 'Release': return isRunning && run.controlState === 'Held';
    case 'Rework':
      return isRunning
        && run.controlState === 'Held'
        && run.operations.length > 0
        && !run.operations.some(operation => operation.executionStatus === 'Running');
    case 'SafeStop': return run.controlState !== 'SafeStopped';
    case 'Reconcile':
    case 'Retry':
    case 'Abort': return false;
    default: return true;
  }
}
