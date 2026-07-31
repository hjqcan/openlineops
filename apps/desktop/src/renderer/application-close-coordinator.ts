export const applicationCloseEditorWaitTimeoutMilliseconds = 30_000;

export type ApplicationCloseState =
  | { phase: 'Idle' }
  | {
      phase: 'WaitingForEditors';
      requestId: number;
      waitingSinceMilliseconds: number;
    }
  | {
      phase: 'AwaitingUnsavedDecision';
      requestId: number;
    };

export type ApplicationCloseEvaluation =
  | { state: ApplicationCloseState; action: 'None' }
  | {
      state: ApplicationCloseState;
      action: 'WaitForEditors';
      requestId: number;
      remainingMilliseconds: number;
    }
  | {
      state: ApplicationCloseState;
      action: 'PromptForUnsavedChanges';
      requestId: number;
    }
  | {
      state: ApplicationCloseState;
      action: 'Approve';
      requestId: number;
    }
  | {
      state: ApplicationCloseState;
      action: 'DenyEditorWaitTimedOut';
      requestId: number;
    }
  | {
      state: ApplicationCloseState;
      action: 'DenyCanceled';
      requestId: number;
    };

export const idleApplicationCloseState: ApplicationCloseState = { phase: 'Idle' };

export function beginApplicationCloseRequest(
  state: ApplicationCloseState,
  requestId: number,
  nowMilliseconds: number
): ApplicationCloseState {
  assertRequestId(requestId);
  assertTimestamp(nowMilliseconds);
  if (state.phase !== 'Idle' && state.requestId === requestId) {
    return state;
  }
  return {
    phase: 'WaitingForEditors',
    requestId,
    waitingSinceMilliseconds: nowMilliseconds
  };
}

export function evaluateApplicationClose(
  state: ApplicationCloseState,
  snapshot: { busy: boolean; dirty: boolean },
  nowMilliseconds: number,
  editorWaitTimeoutMilliseconds = applicationCloseEditorWaitTimeoutMilliseconds
): ApplicationCloseEvaluation {
  assertTimestamp(nowMilliseconds);
  assertTimeout(editorWaitTimeoutMilliseconds);
  if (state.phase !== 'WaitingForEditors') {
    return { state, action: 'None' };
  }

  if (snapshot.busy) {
    const elapsedMilliseconds = Math.max(
      0,
      nowMilliseconds - state.waitingSinceMilliseconds);
    const remainingMilliseconds = editorWaitTimeoutMilliseconds - elapsedMilliseconds;
    if (remainingMilliseconds <= 0) {
      return {
        state: idleApplicationCloseState,
        action: 'DenyEditorWaitTimedOut',
        requestId: state.requestId
      };
    }
    return {
      state,
      action: 'WaitForEditors',
      requestId: state.requestId,
      remainingMilliseconds
    };
  }

  if (snapshot.dirty) {
    return {
      state: {
        phase: 'AwaitingUnsavedDecision',
        requestId: state.requestId
      },
      action: 'PromptForUnsavedChanges',
      requestId: state.requestId
    };
  }

  return {
    state: idleApplicationCloseState,
    action: 'Approve',
    requestId: state.requestId
  };
}

export function resumeApplicationCloseAfterDraftHandling(
  state: ApplicationCloseState,
  requestId: number,
  nowMilliseconds: number
): ApplicationCloseState {
  assertRequestId(requestId);
  assertTimestamp(nowMilliseconds);
  if (state.phase !== 'AwaitingUnsavedDecision' || state.requestId !== requestId) {
    return state;
  }
  return {
    phase: 'WaitingForEditors',
    requestId,
    waitingSinceMilliseconds: nowMilliseconds
  };
}

export function cancelApplicationClose(
  state: ApplicationCloseState,
  requestId: number
): ApplicationCloseEvaluation {
  assertRequestId(requestId);
  if (state.phase !== 'AwaitingUnsavedDecision' || state.requestId !== requestId) {
    return { state, action: 'None' };
  }
  return {
    state: idleApplicationCloseState,
    action: 'DenyCanceled',
    requestId
  };
}

export function expireApplicationCloseRequest(
  state: ApplicationCloseState,
  requestId: number
): ApplicationCloseState {
  assertRequestId(requestId);
  return state.phase !== 'Idle' && state.requestId === requestId
    ? idleApplicationCloseState
    : state;
}

function assertRequestId(requestId: number): void {
  if (!Number.isSafeInteger(requestId) || requestId <= 0) {
    throw new Error('Application close request ID must be a positive safe integer.');
  }
}

function assertTimestamp(nowMilliseconds: number): void {
  if (!Number.isSafeInteger(nowMilliseconds) || nowMilliseconds < 0) {
    throw new Error('Application close timestamp must be a non-negative safe integer.');
  }
}

function assertTimeout(timeoutMilliseconds: number): void {
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error('Application close editor wait timeout must be a positive safe integer.');
  }
}
