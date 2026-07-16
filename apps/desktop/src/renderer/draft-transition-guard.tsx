import React, { useCallback, useRef, useState } from 'react';
import {
  beginDraftTransition,
  cancelDraftTransition,
  completeDraftTransition,
  emptyDraftTransitionGuardState,
  failDraftTransition,
  requestDraftTransition,
  runDraftTransition,
  type DraftTransitionChoice,
  type DraftTransitionGuardState,
  type DraftTransitionRequest
} from './draft-transition-guard-model';

export interface DraftTransitionGuardController {
  state: DraftTransitionGuardState;
  busy: boolean;
  canSave: boolean;
  request(request: DraftTransitionRequest): void;
  cancel(): void;
  resolve(choice: DraftTransitionChoice): Promise<void>;
}

export function useDraftTransitionGuard({
  dirty,
  canSave,
  save,
  onError
}: {
  dirty: boolean;
  canSave: boolean;
  save(): Promise<void>;
  onError(message: string): void;
}): DraftTransitionGuardController {
  const [state, setState] = useState<DraftTransitionGuardState>(emptyDraftTransitionGuardState);
  const stateRef = useRef(state);
  stateRef.current = state;

  const commit = useCallback((next: DraftTransitionGuardState) => {
    stateRef.current = next;
    setState(next);
  }, []);

  const request = useCallback((transition: DraftTransitionRequest) => {
    const result = requestDraftTransition(stateRef.current, transition, dirty);
    commit(result.state);
    if (result.immediate) {
      void runDraftTransition(result.immediate, 'Discard', save).then(outcome => {
        if (!outcome.succeeded) {
          onError(`${transition.targetLabel} failed: ${outcome.error}`);
        }
      });
    }
  }, [commit, dirty, onError, save]);

  const cancel = useCallback(() => {
    commit(cancelDraftTransition(stateRef.current));
  }, [commit]);

  const resolve = useCallback(async (choice: DraftTransitionChoice) => {
    const current = stateRef.current;
    const transition = current.pending;
    if (!transition || current.phase !== 'AwaitingChoice') {
      return;
    }
    if (choice === 'Save' && !canSave) {
      const message = 'Resolve this editor\'s Problems before saving, or choose Discard Changes.';
      commit(failDraftTransition(current, message));
      onError(message);
      return;
    }

    commit(beginDraftTransition(current, choice));
    const outcome = await runDraftTransition(transition, choice, save);
    if (outcome.succeeded) {
      commit(completeDraftTransition(stateRef.current));
      return;
    }

    const message = outcome.stage === 'Save'
      ? `Save failed: ${outcome.error}`
      : `${transition.targetLabel} failed: ${outcome.error}`;
    commit(failDraftTransition(stateRef.current, message));
    onError(message);
  }, [canSave, commit, onError, save]);

  return {
    state,
    busy: state.phase !== 'AwaitingChoice',
    canSave,
    request,
    cancel,
    resolve
  };
}

export function DraftTransitionDialog({
  controller,
  testIdPrefix
}: {
  controller: DraftTransitionGuardController;
  testIdPrefix: string;
}): React.ReactElement | null {
  const transition = controller.state.pending;
  if (!transition) {
    return null;
  }

  return (
    <dialog
      open
      className="unsaved-guard-dialog"
      aria-labelledby={`${testIdPrefix}-title`}
      data-testid={`${testIdPrefix}-dialog`}
    >
      <header>
        <div>
          <span>UNSAVED EDITOR</span>
          <h2 id={`${testIdPrefix}-title`}>{transition.title}</h2>
        </div>
      </header>
      <div className="unsaved-guard-body">
        <p>{transition.detail}</p>
        <ul>
          <li>
            <span>{transition.currentDocumentLabel}</span>
            <small>{controller.state.error ?? `Continue to ${transition.targetLabel}`}</small>
          </li>
        </ul>
      </div>
      <footer>
        <button
          type="button"
          className="button ghost"
          onClick={controller.cancel}
          disabled={controller.busy}
          data-testid={`${testIdPrefix}-cancel`}
        >
          Cancel
        </button>
        <button
          type="button"
          className="button danger"
          onClick={() => void controller.resolve('Discard')}
          disabled={controller.busy}
          data-testid={`${testIdPrefix}-discard`}
        >
          {controller.state.phase === 'Discarding' ? 'Discarding…' : 'Discard Changes'}
        </button>
        <button
          type="button"
          className="button primary"
          onClick={() => void controller.resolve('Save')}
          disabled={controller.busy || !controller.canSave}
          data-testid={`${testIdPrefix}-save`}
        >
          {controller.state.phase === 'Saving' ? 'Saving…' : 'Save & Continue'}
        </button>
      </footer>
    </dialog>
  );
}
