import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/editor-workspace-model.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'editor-workspace-model.ts'
}).outputText;
const model = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

const line = tab('production', 'Line Designer');
const flow = tab('processes', 'Flow Designer');
let tabs = { tabs: [], activeId: null };
tabs = model.openEditorTab(tabs, line);
tabs = model.openEditorTab(tabs, flow);
assert.deepEqual(tabs.tabs.map(item => item.kind), ['production', 'processes']);
assert.equal(tabs.activeId, flow.id);
tabs = model.activateEditorTab(tabs, line.id);
assert.equal(tabs.activeId, line.id);
tabs = model.closeEditorTab(tabs, line.id);
assert.equal(tabs.activeId, flow.id);
assert.equal(tabs.tabs.length, 1);

const registry = new model.DirtyDocumentRegistry();
let lineSaved = 0;
let flowSaved = 0;
registry.register(line.id, line.label);
registry.register(flow.id, flow.label);
registry.update(line.id, registration({
  dirty: true,
  save: async () => { lineSaved += 1; }
}));
registry.update(flow.id, registration({
  dirty: true,
  save: async () => { flowSaved += 1; }
}));
assert.equal(await registry.saveAll(), true);
assert.equal(lineSaved, 1);
assert.equal(flowSaved, 1);
assert.equal(registry.dirtyEntries().length, 0);

registry.update(flow.id, registration({
  dirty: true,
  save: async () => { throw new Error('disk denied'); }
}));
assert.equal(await registry.saveAll(), false);
assert.equal(registry.get(flow.id).dirty, true);
assert.match(registry.get(flow.id).saveError, /disk denied/);
assert.deepEqual(registry.dirtyEntries(new Set([line.id])), []);
assert.equal(registry.dirtyEntries(new Set([flow.id])).length, 1);

const concurrentSaveRegistry = new model.DirtyDocumentRegistry();
let finishConcurrentSave;
const concurrentSave = new Promise(resolve => { finishConcurrentSave = resolve; });
concurrentSaveRegistry.register(line.id, line.label);
concurrentSaveRegistry.update(line.id, registration({
  dirty: true,
  editRevision: 'draft-1',
  save: async () => concurrentSave
}));
const concurrentSaveResult = concurrentSaveRegistry.save(line.id);
assert.equal(concurrentSaveRegistry.get(line.id).saving, true);
concurrentSaveRegistry.update(line.id, { dirty: true });
finishConcurrentSave();
assert.equal(await concurrentSaveResult, false);
assert.equal(concurrentSaveRegistry.get(line.id).dirty, true);
assert.equal(concurrentSaveRegistry.get(line.id).saving, false);
assert.match(concurrentSaveRegistry.get(line.id).saveError, /changed while it was being saved/);

let finishStatusOnlySave;
const statusOnlyBarrier = new Promise(resolve => { finishStatusOnlySave = resolve; });
concurrentSaveRegistry.update(line.id, registration({
  dirty: true,
  editRevision: 'draft-2',
  save: async () => statusOnlyBarrier
}));
const statusOnlySaveResult = concurrentSaveRegistry.save(line.id);
concurrentSaveRegistry.update(line.id, {
  dirty: true,
  editRevision: 'draft-2',
  canSave: false
});
finishStatusOnlySave();
assert.equal(await statusOnlySaveResult, true);
assert.equal(concurrentSaveRegistry.get(line.id).dirty, false);

let finishSaveAll;
const saveAllBarrier = new Promise(resolve => { finishSaveAll = resolve; });
concurrentSaveRegistry.update(line.id, registration({
  dirty: true,
  editRevision: 'draft-3',
  save: async () => saveAllBarrier
}));
const concurrentSaveAllResult = concurrentSaveRegistry.saveAll();
concurrentSaveRegistry.markDirty(line.id);
finishSaveAll();
assert.equal(await concurrentSaveAllResult, false);
assert.equal(concurrentSaveRegistry.get(line.id).dirty, true);

const revertRegistry = new model.DirtyDocumentRegistry();
let lineDraft = 'unsaved-line';
let flowDraft = 'unsaved-flow';
revertRegistry.register(line.id, line.label);
revertRegistry.register(flow.id, flow.label);
revertRegistry.update(line.id, registration({
  dirty: true,
  revert: async () => { lineDraft = 'persisted-line'; }
}));
revertRegistry.update(flow.id, registration({
  dirty: true,
  revert: async () => { flowDraft = 'persisted-flow'; }
}));
assert.equal(await revertRegistry.revertAll(), true);
assert.equal(lineDraft, 'persisted-line');
assert.equal(flowDraft, 'persisted-flow');
assert.equal(revertRegistry.dirtyEntries().length, 0);

lineDraft = 'second-unsaved-line';
flowDraft = 'second-unsaved-flow';
revertRegistry.update(line.id, registration({
  dirty: true,
  revert: async () => { lineDraft = 'second-persisted-line'; }
}));
revertRegistry.update(flow.id, registration({
  dirty: true,
  revert: async () => { throw new Error('reload denied'); }
}));
assert.equal(await revertRegistry.revertAll(), false);
assert.equal(lineDraft, 'second-persisted-line');
assert.equal(flowDraft, 'second-unsaved-flow');
assert.equal(revertRegistry.get(line.id).dirty, false);
assert.equal(revertRegistry.get(flow.id).dirty, true);
assert.match(revertRegistry.get(flow.id).saveError, /reload denied/);

let focusedTarget = null;
registry.update(line.id, registration({
  problems: [{ id: 'missing-station', severity: 'Error', message: 'Station missing', targetId: 'operation-2' }],
  focus: target => { focusedTarget = target; }
}));
const problem = registry.problems()[0];
assert.equal(problem.documentId, line.id);
registry.get(problem.documentId).focus(problem.problem.targetId);
assert.equal(focusedTarget, 'operation-2');

const conflictText = JSON.stringify({
  status: 412,
  title: 'Editor.DocumentRevisionConflict',
  currentRevision: 'b'.repeat(64)
});
assert.equal(model.readEditorConflictRevision(conflictText), 'b'.repeat(64));
assert.equal(model.readEditorConflictRevision('not-json'), null);

process.stdout.write('editor workspace model tests passed\n');

function tab(kind, label) {
  return {
    id: `project\u001fapp\u001f${kind}`,
    kind,
    label,
    projectId: 'project',
    applicationId: 'app'
  };
}

function registration(overrides) {
  return {
    title: 'editor',
    dirty: false,
    editRevision: null,
    canSave: true,
    save: async () => undefined,
    revert: async () => undefined,
    focus: () => undefined,
    problems: [],
    conflict: null,
    saving: false,
    saveError: null,
    ...overrides
  };
}
