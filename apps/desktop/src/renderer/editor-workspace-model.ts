export interface EditorTabModel {
  id: string;
  kind: string;
  label: string;
  projectId: string;
  applicationId: string;
}

export interface EditorTabState {
  tabs: EditorTabModel[];
  activeId: string | null;
}

export interface EditorProblem {
  id: string;
  severity: 'Error' | 'Warning' | 'Info';
  message: string;
  targetId: string | null;
}

export interface EditorDocumentConflict {
  loadedRevision: string;
  currentRevision: string;
  reload(): Promise<void>;
  overwrite(): Promise<void>;
}

export interface EditorDocumentRegistration {
  title: string;
  dirty: boolean;
  canSave: boolean;
  save(): Promise<void>;
  revert(): Promise<void>;
  focus(targetId: string | null): void;
  problems: EditorProblem[];
  conflict: EditorDocumentConflict | null;
  saving: boolean;
  saveError: string | null;
}

type DocumentListener = () => void;

export function openEditorTab(state: EditorTabState, tab: EditorTabModel): EditorTabState {
  if (state.tabs.some(candidate => candidate.id === tab.id)) {
    return { tabs: state.tabs, activeId: tab.id };
  }

  return { tabs: [...state.tabs, tab], activeId: tab.id };
}

export function activateEditorTab(state: EditorTabState, id: string): EditorTabState {
  return state.tabs.some(tab => tab.id === id) ? { ...state, activeId: id } : state;
}

export function closeEditorTab(state: EditorTabState, id: string): EditorTabState {
  const index = state.tabs.findIndex(tab => tab.id === id);
  if (index < 0) {
    return state;
  }

  const tabs = state.tabs.filter(tab => tab.id !== id);
  if (state.activeId !== id) {
    return { tabs, activeId: state.activeId };
  }

  return {
    tabs,
    activeId: tabs[Math.min(index, tabs.length - 1)]?.id ?? null
  };
}

export class DirtyDocumentRegistry {
  private readonly documents = new Map<string, EditorDocumentRegistration>();
  private readonly listeners = new Set<DocumentListener>();

  subscribe(listener: DocumentListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  register(id: string, title: string): () => void {
    const current = this.documents.get(id);
    if (!current) {
      this.documents.set(id, createRegistration(title));
      this.emit();
    } else if (current.title !== title) {
      this.documents.set(id, { ...current, title });
      this.emit();
    }

    return () => {
      if (this.documents.delete(id)) {
        this.emit();
      }
    };
  }

  update(id: string, update: Partial<EditorDocumentRegistration>): void {
    const current = this.documents.get(id) ?? createRegistration(id);

    this.documents.set(id, { ...current, ...update });
    this.emit();
  }

  markDirty(id: string): void {
    const current = this.documents.get(id);
    if (current && !current.dirty) {
      this.documents.set(id, { ...current, dirty: true, saveError: null });
      this.emit();
    }
  }

  markSaved(id: string): void {
    const current = this.documents.get(id);
    if (current) {
      this.documents.set(id, { ...current, dirty: false, saving: false, saveError: null });
      this.emit();
    }
  }

  setConflict(id: string, conflict: EditorDocumentConflict | null): void {
    this.update(id, { conflict });
  }

  get(id: string): EditorDocumentRegistration | null {
    return this.documents.get(id) ?? null;
  }

  entries(): Array<[string, EditorDocumentRegistration]> {
    return Array.from(this.documents.entries());
  }

  dirtyEntries(ids?: ReadonlySet<string>): Array<[string, EditorDocumentRegistration]> {
    return this.entries().filter(([id, document]) => document.dirty && (!ids || ids.has(id)));
  }

  problems(): Array<{ documentId: string; documentTitle: string; problem: EditorProblem }> {
    return this.entries().flatMap(([documentId, document]) => document.problems.map(problem => ({
      documentId,
      documentTitle: document.title,
      problem
    })));
  }

  async save(id: string): Promise<boolean> {
    const document = this.documents.get(id);
    if (!document || !document.dirty) {
      return true;
    }
    if (!document.canSave) {
      this.documents.set(id, { ...document, saveError: 'Document is not currently saveable.' });
      this.emit();
      return false;
    }

    this.documents.set(id, { ...document, saving: true, saveError: null });
    this.emit();
    try {
      await document.save();
      const latest = this.documents.get(id);
      if (latest) {
        this.documents.set(id, { ...latest, dirty: false, saving: false, saveError: null });
        this.emit();
      }
      return true;
    } catch (error) {
      const latest = this.documents.get(id);
      if (latest) {
        this.documents.set(id, {
          ...latest,
          dirty: true,
          saving: false,
          saveError: error instanceof Error ? error.message : String(error)
        });
        this.emit();
      }
      return false;
    }
  }

  async saveAll(ids?: ReadonlySet<string>): Promise<boolean> {
    const dirtyIds = this.dirtyEntries(ids).map(([id]) => id);
    for (const id of dirtyIds) {
      if (!await this.save(id)) {
        return false;
      }
    }
    return true;
  }

  async revert(id: string): Promise<boolean> {
    const document = this.documents.get(id);
    if (!document) {
      return true;
    }

    try {
      await document.revert();
      const latest = this.documents.get(id);
      if (latest) {
        this.documents.set(id, {
          ...latest,
          dirty: false,
          saving: false,
          saveError: null,
          conflict: null
        });
        this.emit();
      }
      return true;
    } catch (error) {
      const latest = this.documents.get(id);
      if (latest) {
        this.documents.set(id, {
          ...latest,
          saveError: error instanceof Error ? error.message : String(error)
        });
        this.emit();
      }
      return false;
    }
  }

  discardAll(ids?: ReadonlySet<string>): void {
    for (const [id, document] of this.dirtyEntries(ids)) {
      this.documents.set(id, { ...document, dirty: false, saveError: null, conflict: null });
    }
    this.emit();
  }

  private emit(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

export function readEditorConflictRevision(responseText: string): string | null {
  try {
    const parsed = JSON.parse(responseText) as { currentRevision?: unknown };
    return typeof parsed.currentRevision === 'string' ? parsed.currentRevision : null;
  } catch {
    return null;
  }
}

function createRegistration(title: string): EditorDocumentRegistration {
  return {
    title,
    dirty: false,
    canSave: false,
    save: async () => undefined,
    revert: async () => undefined,
    focus: () => undefined,
    problems: [],
    conflict: null,
    saving: false,
    saveError: null
  };
}
