export interface EditorDraftBaseline<TDraft> {
  current: TDraft;
  baseline: TDraft;
}

export function createEditorDraftBaseline<TDraft>(
  draft: TDraft
): EditorDraftBaseline<TDraft> {
  return { current: draft, baseline: draft };
}

export function replaceEditorDraft<TDraft>(
  state: EditorDraftBaseline<TDraft>,
  draft: TDraft
): EditorDraftBaseline<TDraft> {
  return { ...state, current: draft };
}

export function synchronizeCleanEditorDraft<TDraft>(
  state: EditorDraftBaseline<TDraft>,
  normalized: TDraft,
  equals: (left: TDraft, right: TDraft) => boolean
): EditorDraftBaseline<TDraft> {
  return isEditorDraftDirty(state, equals)
    ? state
    : { current: normalized, baseline: normalized };
}

export function acceptSubmittedEditorDraft<TDraft>(
  state: EditorDraftBaseline<TDraft>,
  submitted: TDraft
): EditorDraftBaseline<TDraft> {
  return { current: state.current, baseline: submitted };
}

export function revertEditorDraft<TDraft>(
  state: EditorDraftBaseline<TDraft>
): EditorDraftBaseline<TDraft> {
  return { current: state.baseline, baseline: state.baseline };
}

export function isEditorDraftDirty<TDraft>(
  state: EditorDraftBaseline<TDraft>,
  equals: (left: TDraft, right: TDraft) => boolean
): boolean {
  return !equals(state.current, state.baseline);
}
