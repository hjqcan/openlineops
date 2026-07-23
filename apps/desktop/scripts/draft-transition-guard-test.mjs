import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/draft-transition-guard-model.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'draft-transition-guard-model.ts'
}).outputText;
const model = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('clean documents proceed immediately without opening the guard', () => {
  const transition = request('open-b');
  const result = model.requestDraftTransition(
    model.emptyDraftTransitionGuardState,
    transition,
    false);

  assert.equal(result.immediate, transition);
  assert.equal(result.state.pending, null);
});

test('dirty documents keep exactly one pending transition until the user resolves it', () => {
  const first = request('open-b');
  const second = request('open-c');
  const pending = model.requestDraftTransition(
    model.emptyDraftTransitionGuardState,
    first,
    true).state;
  const ignored = model.requestDraftTransition(pending, second, true);

  assert.equal(pending.pending, first);
  assert.equal(pending.phase, 'AwaitingChoice');
  assert.equal(ignored.state.pending, first);
  assert.equal(ignored.immediate, null);
  assert.equal(model.cancelDraftTransition(pending).pending, null);
});

test('save failure never runs the requested resource transition', async () => {
  let transitions = 0;
  const transition = request('open-b', () => { transitions += 1; });
  const result = await model.runDraftTransition(transition, 'Save', async () => {
    throw new Error('write denied');
  });

  assert.deepEqual(result, {
    succeeded: false,
    stage: 'Save',
    error: 'write denied'
  });
  assert.equal(transitions, 0);
});

test('failed reload leaves the guard pending and reports the transition stage', async () => {
  let saves = 0;
  const transition = request('open-b', () => {
    throw new Error('resource disappeared');
  });
  const pending = model.requestDraftTransition(
    model.emptyDraftTransitionGuardState,
    transition,
    true).state;
  const running = model.beginDraftTransition(pending, 'Save');
  const result = await model.runDraftTransition(transition, 'Save', async () => {
    saves += 1;
  });
  const failed = model.failDraftTransition(running, result.error);

  assert.equal(saves, 1);
  assert.deepEqual(result, {
    succeeded: false,
    stage: 'Transition',
    error: 'resource disappeared'
  });
  assert.equal(failed.pending, transition);
  assert.equal(failed.phase, 'AwaitingChoice');
  assert.equal(failed.error, 'resource disappeared');
});

test('discard skips saving and successful transition clears the guard', async () => {
  let saves = 0;
  let transitions = 0;
  const transition = request('new-resource', () => { transitions += 1; });
  const pending = model.requestDraftTransition(
    model.emptyDraftTransitionGuardState,
    transition,
    true).state;
  const running = model.beginDraftTransition(pending, 'Discard');
  const result = await model.runDraftTransition(transition, 'Discard', async () => {
    saves += 1;
  });

  assert.deepEqual(result, { succeeded: true });
  assert.equal(saves, 0);
  assert.equal(transitions, 1);
  assert.equal(model.completeDraftTransition(running).pending, null);
});

function request(id, proceed = () => undefined) {
  return {
    id,
    title: 'Unsaved resource',
    detail: 'Choose how to continue.',
    currentDocumentLabel: 'Resource A',
    targetLabel: 'Resource B',
    proceed
  };
}
