import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import ts from 'typescript';
import { fileURLToPath } from 'node:url';

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const topologySource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'topology-designer.tsx'),
  'utf8');
const mainSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'main.tsx'),
  'utf8');
const processSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'process-workbench.tsx'),
  'utf8');
const stylesSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'styles.css'),
  'utf8');
const draftModelPath = path.join(
  desktopRoot,
  'src',
  'renderer',
  'topology-draft-workspace-model.ts');
const draftModelSource = fs.readFileSync(draftModelPath, 'utf8');
const editorModelPath = path.join(desktopRoot, 'src', 'renderer', 'editor-workspace-model.ts');
const editorModelJavaScript = ts.transpileModule(fs.readFileSync(editorModelPath, 'utf8'), {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: editorModelPath
}).outputText;
const editorModelUrl = `data:text/javascript;base64,${Buffer.from(editorModelJavaScript).toString('base64')}`;
const draftModelJavaScript = ts.transpileModule(draftModelSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: draftModelPath
}).outputText.replace("'./editor-workspace-model'", JSON.stringify(editorModelUrl));
const draftModel = await import(
  `data:text/javascript;base64,${Buffer.from(draftModelJavaScript).toString('base64')}`);

function savePendingEditsSource() {
  const start = topologySource.indexOf('const savePendingEdits = useCallback');
  const end = topologySource.indexOf('const revertPendingEdits = useCallback', start);
  assert.ok(start >= 0 && end > start, 'Topology Save All implementation was not found.');
  return topologySource.slice(start, end);
}

test('Topology Save All preserves Driver-to-Capability referential integrity', () => {
  const implementation = savePendingEditsSource();
  const order = [...draftModel.topologyDraftSaveOrder];
  assert.deepEqual(order, ['capability', 'driver', 'semantic', 'geometry']);
  assert.match(implementation, /topologyDraftSaveOrder\.filter/u);

  const state = {
    declaredCapabilities: new Set(['capability.a', 'capability.b']),
    driverCapability: 'capability.a'
  };
  for (const kind of order.filter(candidate => candidate === 'driver' || candidate === 'semantic')) {
    if (kind === 'driver') {
      assert.ok(state.declaredCapabilities.has('capability.b'));
      state.driverCapability = 'capability.b';
    } else {
      assert.notEqual(
        state.driverCapability,
        'capability.a',
        'Removing an in-use Capability before migrating its Driver must fail closed.');
      state.declaredCapabilities.delete('capability.a');
    }
  }
  assert.equal(state.driverCapability, 'capability.b');
  assert.deepEqual([...state.declaredCapabilities], ['capability.b']);
});

test('hidden editor tabs remain saveable and topology drafts have an inline discard path', () => {
  const implementation = savePendingEditsSource();
  assert.doesNotMatch(
    implementation,
    /offsetParent|getBoundingClientRect|getClientRects/u,
    'Save All must not require the editor tab to be visible.');
  assert.match(implementation, /\.find\(button => !button\.disabled\)/u);
  assert.match(topologySource, /data-testid="discard-topology-drafts"/u);
  assert.match(topologySource, /const revertPendingEdits = useCallback/u);
  assert.match(topologySource, /await reloadPersistedDrafts\(\)/u);
  const revertStart = topologySource.indexOf('const revertPendingEdits = useCallback');
  const revertEnd = topologySource.indexOf('const topologyProblems', revertStart);
  assert.doesNotMatch(topologySource.slice(revertStart, revertEnd), /await refresh\(/u);
});

test('topology draft reload fails closed before dirty state can be cleared', () => {
  const topology = { topologyId: 'topology-a' };
  const layout = { layoutId: 'layout-a' };
  assert.deepEqual(
    draftModel.requireTopologyDraftReload(
      { ok: true, status: 200, text: '', body: topology },
      { ok: true, status: 200, text: '', body: layout }),
    { topology, layout });

  for (const [name, topologyResponse, layoutResponse] of [
    [
      'topology HTTP failure',
      { ok: false, status: 503, text: 'offline', body: null },
      { ok: true, status: 200, text: '', body: layout }
    ],
    [
      'missing topology body',
      { ok: true, status: 200, text: '', body: null },
      { ok: true, status: 200, text: '', body: layout }
    ],
    [
      'layout HTTP failure',
      { ok: true, status: 200, text: '', body: topology },
      { ok: false, status: 500, text: 'failed', body: null }
    ],
    [
      'missing layout body',
      { ok: true, status: 200, text: '', body: topology },
      { ok: true, status: 200, text: '', body: null }
    ]
  ]) {
    assert.throws(
      () => draftModel.requireTopologyDraftReload(topologyResponse, layoutResponse),
      /Reload persisted (?:topology|layout) failed/u,
      name);
  }
});

test('topology and layout stale revisions become strict editor conflicts', () => {
  const topologyConflict = draftModel.readTopologyDraftConflict(
    {
      status: 412,
      text: JSON.stringify({ currentRevision: 'b'.repeat(64) })
    },
    'Topology',
    'a'.repeat(64));
  assert.deepEqual(topologyConflict, {
    documentKind: 'Topology',
    loadedRevision: 'a'.repeat(64),
    currentRevision: 'b'.repeat(64)
  });
  const layoutConflict = draftModel.readTopologyDraftConflict(
    {
      status: 412,
      text: JSON.stringify({ currentRevision: 'd'.repeat(64) })
    },
    'Layout',
    'c'.repeat(64));
  assert.deepEqual(layoutConflict, {
    documentKind: 'Layout',
    loadedRevision: 'c'.repeat(64),
    currentRevision: 'd'.repeat(64)
  });
  assert.equal(
    draftModel.readTopologyDraftConflict({ status: 409, text: '{}' }, 'Topology', 'a'),
    null);
  assert.throws(
    () => draftModel.readTopologyDraftConflict({ status: 412, text: '{}' }, 'Layout', 'a'),
    /Layout conflict response did not contain a current revision/u);
});

test('every saveable topology draft preserves stale editor state and registers conflict actions', () => {
  assert.match(topologySource, /const \[conflict, setConflict\] = useState<EditorDocumentConflict \| null>/u);
  assert.match(topologySource, /const registerDraftConflict = useCallback/u);
  assert.match(topologySource, /reload:\s*async \(\) => \{\s*await reloadPersistedDrafts\(\);\s*clearDirtyDrafts\(\);/u);
  assert.match(topologySource, /overwrite:\s*async \(\) => \{\s*if \(!await overwriteDraft\(\)\)/u);
  assert.match(topologySource, /problems: topologyProblems,\s*conflict/u);

  for (const saveAction of [
    'commitGeometry',
    'saveSemanticTarget',
    'createCapability',
    'saveDriverBinding'
  ]) {
    const start = topologySource.indexOf(`const ${saveAction} = useCallback`);
    const end = topologySource.indexOf('\n  const ', start + 10);
    assert.ok(start >= 0 && end > start, `${saveAction} implementation was not found.`);
    const implementation = topologySource.slice(start, end);
    assert.match(implementation, /registerDraftConflict\(/u, `${saveAction} does not register stale revisions.`);
    assert.match(implementation, /force = false/u, `${saveAction} has no explicit overwrite path.`);
    assert.match(implementation, /force \}/u, `${saveAction} does not send explicit overwrite intent.`);
    assert.match(implementation, /setConflict\(null\)/u, `${saveAction} does not clear conflict after success.`);
  }

  const geometryStart = topologySource.indexOf('const commitGeometry = useCallback');
  const geometryEnd = topologySource.indexOf('const saveSemanticTarget = useCallback', geometryStart);
  const geometrySave = topologySource.slice(geometryStart, geometryEnd);
  const staleBranchStart = geometrySave.indexOf('if (!response.ok || !response.body)');
  const staleBranchEnd = geometrySave.indexOf("setSaveState('error')", staleBranchStart);
  assert.doesNotMatch(
    geometrySave.slice(staleBranchStart, staleBranchEnd),
    /updateLayoutGeometry\(current, elementId, previousGeometry\)\);\s*registerDraftConflict/u,
    'A stale layout revision must not roll the editor geometry back before conflict resolution.');
});

test('discarding a Capability draft rebuilds it from persisted topology state', () => {
  const unsaved = { capabilityId: 'capability.unsaved', commandName: 'MustNotPersist' };
  const persisted = { capabilityId: 'capability.next', commandName: 'Execute' };
  assert.equal(
    draftModel.resolvePersistedCapabilityDraft(true, unsaved, persisted),
    unsaved,
    'A dirty Capability draft must survive unrelated topology refreshes.');
  assert.equal(
    draftModel.resolvePersistedCapabilityDraft(false, unsaved, persisted),
    persisted,
    'Discard must replace an unpersisted Capability identity with a fresh persisted draft.');
  assert.match(
    topologySource,
    /setDraft\(current => resolvePersistedCapabilityDraft\(\s*dirty,\s*current,\s*newCapabilityContractDraft\(topology\)\)\)/u);
});

test('each topology sub-editor owns dirty state and server refreshes preserve other drafts', () => {
  for (const kind of ['capability', 'semantic', 'driver', 'geometry']) {
    assert.match(
      topologySource,
      new RegExp(`setDraftDirty\\('${kind}', false\\)`, 'u'),
      `${kind} save does not clear only its own dirty state.`);
  }
  assert.match(topologySource, /onDirtyChange\(true\)/u);
  assert.match(topologySource, /onDraftDirty\('semantic', true\)/u);
  assert.match(topologySource, /onDraftDirty\('geometry', true\)/u);
  assert.match(topologySource, /if \(dirty\) \{\s*return;\s*\}/u);
  assert.equal(
    [...topologySource.matchAll(/onPreviewGeometry\(element\.elementId, drag\.previousGeometry, false\)/gu)].length,
    2,
    'Both 2D and 3D canceled drags must clear geometry dirty state.');
});

test('topology save progress does not masquerade as a concurrent user edit', () => {
  const dirtySetterStart = topologySource.indexOf('const setDraftDirty = useCallback');
  const dirtySetterEnd = topologySource.indexOf('const clearDirtyDrafts = useCallback', dirtySetterStart);
  assert.ok(dirtySetterStart >= 0 && dirtySetterEnd > dirtySetterStart);
  const dirtySetter = topologySource.slice(dirtySetterStart, dirtySetterEnd);
  assert.match(dirtySetter, /if \(dirty\) \{\s*setDraftEditRevision\(current => current \+ 1\);\s*\}/u);
  assert.match(topologySource, /editRevision: draftEditRevision/u);
  assert.doesNotMatch(topologySource, /editRevision: dirtyDrafts/u);
});

test('Project Targets keeps every legal Application target available', () => {
  assert.match(processSource, /\{targets\.map\(target => \(/u);
  assert.doesNotMatch(processSource, /targets\.slice\(/u);
  assert.match(
    stylesSource,
    /\.project-target-list\s*\{[^}]*max-height:\s*238px;[^}]*overflow:\s*auto;/su,
    'The unbounded target collection must remain usable through a scrolling viewport.');
});

test('entering runtime is guarded by the unified unsaved-document registry', () => {
  for (const title of [
    'Enter Run mode?',
    'Run the published Project?',
    'Open runtime operations?'
  ]) {
    assert.match(mainSource, new RegExp(title.replace(/[?]/gu, '\\?'), 'u'));
  }
  assert.match(mainSource, /runWithUnsavedGuard\(/u);
});
