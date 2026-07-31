import { readEditorConflictRevision } from './editor-workspace-model';

export type TopologyDraftKind = 'capability' | 'semantic' | 'driver' | 'geometry';
export type TopologyDocumentKind = 'Topology' | 'Layout';

export const topologyDraftSaveOrder: readonly TopologyDraftKind[] = [
  'capability',
  'driver',
  'semantic',
  'geometry'
];

export interface TopologyDraftReloadResponse<T> {
  ok: boolean;
  status: number;
  text: string;
  body: T | null;
}

export interface TopologyDraftConflict {
  documentKind: TopologyDocumentKind;
  loadedRevision: string;
  currentRevision: string;
}

export function readTopologyDraftConflict(
  response: Pick<TopologyDraftReloadResponse<unknown>, 'status' | 'text'>,
  documentKind: TopologyDocumentKind,
  loadedRevision: string
): TopologyDraftConflict | null {
  if (response.status !== 412) {
    return null;
  }

  const currentRevision = readEditorConflictRevision(response.text);
  if (!currentRevision) {
    throw new Error(`${documentKind} conflict response did not contain a current revision.`);
  }

  return {
    documentKind,
    loadedRevision,
    currentRevision
  };
}

export function resolvePersistedCapabilityDraft<T>(
  dirty: boolean,
  currentDraft: T,
  persistedDraft: T
): T {
  return dirty ? currentDraft : persistedDraft;
}

export function requireTopologyDraftReload<TTopology, TLayout>(
  topology: TopologyDraftReloadResponse<TTopology>,
  layout: TopologyDraftReloadResponse<TLayout>
): { topology: TTopology; layout: TLayout } {
  if (!topology.ok || topology.body === null) {
    throw new Error(
      `Reload persisted topology failed: ${topology.status} ${topology.text}`.trimEnd());
  }
  if (!layout.ok || layout.body === null) {
    throw new Error(
      `Reload persisted layout failed: ${layout.status} ${layout.text}`.trimEnd());
  }
  return { topology: topology.body, layout: layout.body };
}
