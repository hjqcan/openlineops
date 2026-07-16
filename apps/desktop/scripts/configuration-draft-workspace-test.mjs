import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import ts from 'typescript';
import { fileURLToPath } from 'node:url';

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const modelPath = path.join(desktopRoot, 'src', 'renderer', 'editor-draft-baseline-model.ts');
const modelSource = fs.readFileSync(modelPath, 'utf8');
const modelJavaScript = ts.transpileModule(modelSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: modelPath
}).outputText;
const model = await import(
  `data:text/javascript;base64,${Buffer.from(modelJavaScript).toString('base64')}`);
const engineeringModelPath = path.join(
  desktopRoot,
  'src',
  'renderer',
  'engineering-draft-model.ts');
const engineeringModelJavaScript = ts.transpileModule(
  fs.readFileSync(engineeringModelPath, 'utf8'),
  {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022
    },
    fileName: engineeringModelPath
  }).outputText;
const engineeringModel = await import(
  `data:text/javascript;base64,${Buffer.from(engineeringModelJavaScript).toString('base64')}`);
const draftEquals = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const workbenchSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'engineering-workbench.tsx'),
  'utf8');
const devicesWorkbenchSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'devices-workbench.tsx'),
  'utf8');
const apiSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'api.ts'),
  'utf8');

function draft(overrides = {}) {
  return {
    recipeId: 'recipe-a',
    recipeVersionId: 'recipe-a@1.0.0',
    recipeName: 'Recipe A',
    stationProfileId: 'station-profile-a',
    stationSystemId: 'station-a',
    stationName: 'Station A',
    deviceBindingId: 'binding-a',
    deviceOwnerSystemId: 'system-a',
    capabilityId: 'capability.a',
    deviceKey: 'device-a',
    snapshotId: 'snapshot-a',
    processDefinitionId: 'process-a',
    processVersionId: 'process-a@1.0.0',
    ...overrides
  };
}

test('engineering draft baseline protects edits across refresh, save, and discard', () => {
  const initial = draft();
  let state = model.createEditorDraftBaseline(initial);
  assert.equal(model.isEditorDraftDirty(state, draftEquals), false);

  const edited = draft({ recipeName: 'Operator draft' });
  state = model.replaceEditorDraft(state, edited);
  assert.equal(model.isEditorDraftDirty(state, draftEquals), true);

  const normalizedRefresh = draft({ stationSystemId: 'server-station' });
  assert.equal(
    model.synchronizeCleanEditorDraft(state, normalizedRefresh, draftEquals),
    state,
    'A server refresh must not overwrite an unsaved operator draft.');

  state = model.acceptSubmittedEditorDraft(state, edited);
  assert.equal(model.isEditorDraftDirty(state, draftEquals), false);

  const postSubmitEdit = draft({ recipeName: 'Edit while publish was in flight' });
  state = model.replaceEditorDraft(state, postSubmitEdit);
  state = model.acceptSubmittedEditorDraft(state, edited);
  assert.equal(
    model.isEditorDraftDirty(state, draftEquals),
    true,
    'Edits made after submission must not be marked as published.');
  assert.deepEqual(model.revertEditorDraft(state).current, edited);
});

test('engineering dirty state covers mutable source but not immutable publication inputs', () => {
  const initial = draft();
  assert.equal(
    engineeringModel.engineeringSourceDraftsEqual(
      initial,
      draft({
        snapshotId: 'another-snapshot',
        processDefinitionId: 'another-process',
        processVersionId: 'another-process@2.0.0'
      })),
    true,
    'Snapshot publication inputs are not mutable Application source.');
  assert.equal(
    engineeringModel.engineeringSourceDraftsEqual(
      initial,
      draft({ recipeName: 'Changed source' })),
    false);
});

test('engineering workbench participates in the unified document lifecycle', () => {
  assert.match(workbenchSource, /useEditorDocument\(\{/u);
  assert.match(workbenchSource, /dirty: draftDirty/u);
  assert.match(workbenchSource, /save: saveEngineeringSource/u);
  assert.match(workbenchSource, /revert: revertDraft/u);
  assert.match(workbenchSource, /setDraftState\(createEditorDraftBaseline\(/u);
  assert.match(workbenchSource, /data-testid="discard-engineering-draft"/u);
  assert.match(workbenchSource, /data-testid="new-engineering-configuration"/u);
  assert.match(workbenchSource, /data-testid="save-engineering-source"/u);
  assert.match(workbenchSource, /throw new Error\(`Snapshot publish failed:/u);
  assert.doesNotMatch(
    workbenchSource,
    /Snapshot publish failed:[^\n]+\n\s*return;/u,
    'A failed immutable publication must reject instead of masquerading as success.');

  const saveStart = workbenchSource.indexOf('const saveEngineeringSource = useCallback');
  const publishStart = workbenchSource.indexOf('const createRuntimeSnapshot = useCallback', saveStart);
  const registryStart = workbenchSource.indexOf('useEditorDocument({', publishStart);
  assert.ok(saveStart >= 0 && publishStart > saveStart && registryStart > publishStart);
  assert.doesNotMatch(
    workbenchSource.slice(saveStart, publishStart),
    /publishRecipe|publishConfigurationSnapshot/u,
    'Save must persist mutable source without publishing immutable content.');
  assert.match(workbenchSource.slice(publishStart, registryStart), /publishRecipe/u);
  assert.match(workbenchSource.slice(publishStart, registryStart), /publishConfigurationSnapshot/u);
  assert.match(
    workbenchSource,
    /const ownerCapabilityIds = useMemo\([\s\S]*?\[selectedDeviceOwner\]\);/u,
    'Problem dependencies must remain referentially stable when registry updates rerender the Studio.');
});

test('device registration drafts participate in the unified document lifecycle', () => {
  assert.match(devicesWorkbenchSource, /useEditorDocument\(\{/u);
  assert.match(devicesWorkbenchSource, /dirty: draftDirty/u);
  assert.match(devicesWorkbenchSource, /save: createBundle/u);
  assert.match(devicesWorkbenchSource, /revert: revertDraft/u);
  assert.match(devicesWorkbenchSource, /data-testid="discard-device-draft"/u);
  assert.match(devicesWorkbenchSource, /acceptSubmittedEditorDraft/u);
  assert.match(devicesWorkbenchSource, /deviceDefinitionMatchesDraft/u);
  assert.match(devicesWorkbenchSource, /deviceInstanceMatchesDraft/u);
  assert.match(devicesWorkbenchSource, /throw new Error\(`Device definition failed:/u);
  assert.match(devicesWorkbenchSource, /throw new Error\(`Device instance failed:/u);
});

test('editor list reloads fail closed on HTTP errors and missing response bodies', () => {
  assert.match(apiSource, /function requireListResponse/u);
  assert.match(apiSource, /if \(!response\.ok \|\| response\.body === null\)/u);
  assert.doesNotMatch(
    apiSource,
    /return response\.body \?\? \[\];/u,
    'A failed list request must never masquerade as a valid empty persisted source.');
});
