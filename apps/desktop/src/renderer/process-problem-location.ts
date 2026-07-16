import type {
  ProcessGraphValidationIssue,
  ProcessGraphValidationTargetKind
} from './contracts';
import type { EditorProblem } from './editor-workspace-model';

export interface ProcessProblemLocation {
  targetKind: ProcessGraphValidationTargetKind;
  targetId: string;
}

const targetKinds: ProcessGraphValidationTargetKind[] = ['Graph', 'Node', 'Transition'];

export function createProcessEditorProblems(
  issues: ProcessGraphValidationIssue[]
): EditorProblem[] {
  return issues.map((issue, index) => ({
    id: `${issue.code}-${index + 1}`,
    severity: issue.severity === 'Error' ? 'Error' : 'Warning',
    message: issue.message,
    targetId: serializeProcessProblemLocation(issue)
  }));
}

export function serializeProcessProblemLocation(location: ProcessProblemLocation): string {
  assertProcessProblemLocation(location);
  return JSON.stringify([location.targetKind, location.targetId]);
}

export function parseProcessProblemLocation(value: string | null): ProcessProblemLocation | null {
  if (value === null) {
    return null;
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (!Array.isArray(parsed)
        || parsed.length !== 2
        || !isTargetKind(parsed[0])
        || typeof parsed[1] !== 'string') {
      return null;
    }

    const location = { targetKind: parsed[0], targetId: parsed[1] };
    assertProcessProblemLocation(location);
    return location;
  } catch {
    return null;
  }
}

function assertProcessProblemLocation(location: ProcessProblemLocation): void {
  if (!isTargetKind(location.targetKind)) {
    throw new Error(`Unsupported process validation target kind: ${String(location.targetKind)}`);
  }

  if (!location.targetId || location.targetId.trim() !== location.targetId) {
    throw new Error('Process validation target id must be a non-empty canonical string.');
  }
}

function isTargetKind(value: unknown): value is ProcessGraphValidationTargetKind {
  return typeof value === 'string'
    && targetKinds.some(candidate => candidate === value);
}
