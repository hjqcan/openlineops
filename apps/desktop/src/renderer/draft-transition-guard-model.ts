export type DraftTransitionChoice = 'Save' | 'Discard';
export type DraftTransitionPhase = 'AwaitingChoice' | 'Saving' | 'Discarding';

export interface DraftTransitionRequest {
  id: string;
  title: string;
  detail: string;
  currentDocumentLabel: string;
  targetLabel: string;
  proceed(choice: DraftTransitionChoice): Promise<void> | void;
}

export interface DraftTransitionGuardState {
  pending: DraftTransitionRequest | null;
  phase: DraftTransitionPhase;
  error: string | null;
}

export type DraftTransitionRunResult =
  | { succeeded: true }
  | { succeeded: false; stage: 'Save' | 'Transition'; error: string };

export const emptyDraftTransitionGuardState: DraftTransitionGuardState = {
  pending: null,
  phase: 'AwaitingChoice',
  error: null
};

export function requestDraftTransition(
  state: DraftTransitionGuardState,
  request: DraftTransitionRequest,
  dirty: boolean
): { state: DraftTransitionGuardState; immediate: DraftTransitionRequest | null } {
  if (state.pending) {
    return { state, immediate: null };
  }
  if (!dirty) {
    return { state, immediate: request };
  }
  return {
    state: {
      pending: request,
      phase: 'AwaitingChoice',
      error: null
    },
    immediate: null
  };
}

export function beginDraftTransition(
  state: DraftTransitionGuardState,
  choice: DraftTransitionChoice
): DraftTransitionGuardState {
  if (!state.pending || state.phase !== 'AwaitingChoice') {
    return state;
  }
  return {
    ...state,
    phase: choice === 'Save' ? 'Saving' : 'Discarding',
    error: null
  };
}

export function failDraftTransition(
  state: DraftTransitionGuardState,
  error: string
): DraftTransitionGuardState {
  if (!state.pending) {
    return state;
  }
  return {
    ...state,
    phase: 'AwaitingChoice',
    error
  };
}

export function completeDraftTransition(
  state: DraftTransitionGuardState
): DraftTransitionGuardState {
  return state.pending ? emptyDraftTransitionGuardState : state;
}

export function cancelDraftTransition(
  state: DraftTransitionGuardState
): DraftTransitionGuardState {
  return state.pending && state.phase === 'AwaitingChoice'
    ? emptyDraftTransitionGuardState
    : state;
}

export async function runDraftTransition(
  request: DraftTransitionRequest,
  choice: DraftTransitionChoice,
  save: () => Promise<void>
): Promise<DraftTransitionRunResult> {
  if (choice === 'Save') {
    try {
      await save();
    } catch (error) {
      return {
        succeeded: false,
        stage: 'Save',
        error: errorMessage(error)
      };
    }
  }

  try {
    await request.proceed(choice);
    return { succeeded: true };
  } catch (error) {
    return {
      succeeded: false,
      stage: 'Transition',
      error: errorMessage(error)
    };
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
