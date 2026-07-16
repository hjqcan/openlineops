import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useSyncExternalStore
} from 'react';
import { AlertTriangle, CircleAlert, Info, Save, X } from 'lucide-react';
import type {
  DirtyDocumentRegistry,
  EditorDocumentConflict,
  EditorDocumentRegistration,
  EditorProblem,
  EditorTabModel
} from './editor-workspace-model';

interface EditorDocumentContextValue {
  documentId: string;
  registry: DirtyDocumentRegistry;
}

const EditorDocumentContext = createContext<EditorDocumentContextValue | null>(null);

export interface EditorDocumentState {
  dirty: boolean;
  editRevision: unknown;
  canSave: boolean;
  save(): Promise<void>;
  revert(): Promise<void>;
  focus?(targetId: string | null): void;
  problems?: EditorProblem[];
  conflict?: EditorDocumentConflict | null;
}

export function EditorDocumentHost({
  documentId,
  title,
  registry,
  active,
  children
}: {
  documentId: string;
  title: string;
  registry: DirtyDocumentRegistry;
  active: boolean;
  children: React.ReactNode;
}): React.ReactElement {
  useEffect(() => registry.register(documentId, title), [documentId, registry, title]);
  const value = useMemo(() => ({ documentId, registry }), [documentId, registry]);
  return (
    <EditorDocumentContext.Provider value={value}>
      <section
        className={active ? 'ide-editor-document active' : 'ide-editor-document'}
        aria-hidden={!active}
        data-document-id={documentId}
        data-testid={`editor-document-${safeTestId(documentId)}`}
      >
        {children}
      </section>
    </EditorDocumentContext.Provider>
  );
}

export function useEditorDocument(state: EditorDocumentState): {
  markDirty(): void;
  markSaved(): void;
  setConflict(conflict: EditorDocumentConflict | null): void;
} {
  const context = useContext(EditorDocumentContext);
  const latestRef = useRef(state);
  latestRef.current = state;

  useEffect(() => {
    if (!context) {
      return;
    }
    context.registry.update(context.documentId, {
      dirty: state.dirty,
      editRevision: state.editRevision,
      canSave: state.canSave,
      save: () => latestRef.current.save(),
      revert: () => latestRef.current.revert(),
      focus: targetId => latestRef.current.focus?.(targetId),
      problems: state.problems ?? [],
      conflict: state.conflict ?? null
    });
  }, [context, state.canSave, state.conflict, state.dirty, state.editRevision, state.problems]);

  return useMemo(() => ({
    markDirty: () => {
      if (context) context.registry.markDirty(context.documentId);
    },
    markSaved: () => {
      if (context) context.registry.markSaved(context.documentId);
    },
    setConflict: (conflict: EditorDocumentConflict | null) => {
      if (context) context.registry.setConflict(context.documentId, conflict);
    }
  }), [context]);
}

export function useDocumentRegistrySnapshot(registry: DirtyDocumentRegistry): number {
  const revisionRef = useRef(0);
  const subscribe = useCallback((listener: () => void) => registry.subscribe(() => {
    revisionRef.current += 1;
    listener();
  }), [registry]);
  return useSyncExternalStore(subscribe, () => revisionRef.current, () => revisionRef.current);
}

export function EditorTabStrip({
  tabs,
  activeId,
  registry,
  iconFor,
  onActivate,
  onClose
}: {
  tabs: EditorTabModel[];
  activeId: string | null;
  registry: DirtyDocumentRegistry;
  iconFor(kind: string): React.ReactNode;
  onActivate(tab: EditorTabModel): void;
  onClose(tab: EditorTabModel): void;
}): React.ReactElement {
  useDocumentRegistrySnapshot(registry);
  return (
    <div className="ide-editor-tabs" role="tablist" aria-label="Open editors" data-testid="editor-tab-strip">
      {tabs.map(tab => {
        const document = registry.get(tab.id);
        return (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={activeId === tab.id}
            className={activeId === tab.id ? 'ide-editor-tab active' : 'ide-editor-tab'}
            onClick={() => onActivate(tab)}
            data-testid={`editor-tab-${safeTestId(tab.kind)}`}
          >
            {iconFor(tab.kind)}
            <span>{tab.label}</span>
            {document?.dirty ? <i className="editor-dirty-badge" aria-label="Unsaved changes" /> : null}
            {document?.saving ? <small>Saving</small> : <small>{tab.applicationId}</small>}
            <span
              role="button"
              tabIndex={0}
              className="editor-tab-close"
              aria-label={`Close ${tab.label}`}
              data-testid={`close-editor-tab-${safeTestId(tab.kind)}`}
              onClick={event => {
                event.stopPropagation();
                onClose(tab);
              }}
              onKeyDown={event => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  event.stopPropagation();
                  onClose(tab);
                }
              }}
            >
              <X size={12} />
            </span>
          </button>
        );
      })}
    </div>
  );
}

export function EditorProblemsPanel({
  registry,
  onActivateDocument
}: {
  registry: DirtyDocumentRegistry;
  onActivateDocument(documentId: string): void;
}): React.ReactElement {
  useDocumentRegistrySnapshot(registry);
  const problems = registry.problems();
  return (
    <div className="ide-problems" data-testid="workspace-problems">
      {problems.length === 0 ? <p>No problems in open editors.</p> : problems.map(item => (
        <button
          type="button"
          key={`${item.documentId}:${item.problem.id}`}
          className={`ide-problem ${item.problem.severity.toLowerCase()}`}
          onClick={() => {
            onActivateDocument(item.documentId);
            registry.get(item.documentId)?.focus(item.problem.targetId);
          }}
          data-testid={`workspace-problem-${safeTestId(item.problem.id)}`}
        >
          {item.problem.severity === 'Error'
            ? <CircleAlert size={13} />
            : item.problem.severity === 'Warning'
              ? <AlertTriangle size={13} />
              : <Info size={13} />}
          <span><strong>{item.documentTitle}</strong><small>{item.problem.message}</small></span>
        </button>
      ))}
    </div>
  );
}

export function EditorConflictNotice({ document }: { document: EditorDocumentRegistration | null }): React.ReactElement | null {
  const conflict = document?.conflict;
  if (!conflict) {
    return null;
  }

  return (
    <div className="editor-conflict-notice" role="alert" data-testid="editor-external-conflict">
      <AlertTriangle size={17} />
      <span>
        <strong>This file changed outside this editor.</strong>
        <small>Loaded {shortRevision(conflict.loadedRevision)} · Current {shortRevision(conflict.currentRevision)}</small>
      </span>
      <button type="button" className="button" onClick={() => void conflict.reload()} data-testid="conflict-reload">
        Reload
      </button>
      <button type="button" className="button danger" onClick={() => void conflict.overwrite()} data-testid="conflict-keep-editor">
        Keep Editor &amp; Overwrite
      </button>
    </div>
  );
}

export function SaveAllButton({
  registry,
  onResult
}: {
  registry: DirtyDocumentRegistry;
  onResult(success: boolean): void;
}): React.ReactElement {
  useDocumentRegistrySnapshot(registry);
  const dirtyCount = registry.dirtyEntries().length;
  return (
    <button
      type="button"
      className="ide-save-all"
      disabled={dirtyCount === 0}
      onClick={() => void registry.saveAll().then(onResult)}
      data-testid="save-all-editors"
      title="Save all open editors"
    >
      <Save size={14} /> Save All{dirtyCount > 0 ? ` (${dirtyCount})` : ''}
    </button>
  );
}

function safeTestId(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
}

function shortRevision(value: string): string {
  return value ? value.slice(0, 12) : 'new document';
}
