import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/application-close-coordinator.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'application-close-coordinator.ts'
}).outputText;
const model = await import(
  `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

const {
  beginApplicationCloseRequest,
  cancelApplicationClose,
  evaluateApplicationClose,
  expireApplicationCloseRequest,
  idleApplicationCloseState,
  resumeApplicationCloseAfterDraftHandling
} = model;

test('clean idle application close approves exactly once', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 1, 100);
  const approved = evaluateApplicationClose(waiting, { busy: false, dirty: false }, 100);

  assert.equal(approved.action, 'Approve');
  assert.deepEqual(approved.state, idleApplicationCloseState);
  assert.equal(
    evaluateApplicationClose(approved.state, { busy: false, dirty: false }, 100).action,
    'None');
});

test('busy editor settles to clean and approves the same close request', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 2, 100);
  const busy = evaluateApplicationClose(waiting, { busy: true, dirty: false }, 110, 1_000);
  assert.equal(busy.action, 'WaitForEditors');
  assert.equal(busy.requestId, 2);
  assert.equal(busy.remainingMilliseconds, 990);

  const approved = evaluateApplicationClose(busy.state, { busy: false, dirty: false }, 120, 1_000);
  assert.equal(approved.action, 'Approve');
  assert.equal(approved.requestId, 2);
});

test('busy editor becoming dirty prompts once and duplicate evaluations do nothing', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 3, 100);
  const prompt = evaluateApplicationClose(waiting, { busy: false, dirty: true }, 120);
  assert.equal(prompt.action, 'PromptForUnsavedChanges');
  assert.equal(prompt.requestId, 3);
  assert.equal(
    evaluateApplicationClose(prompt.state, { busy: false, dirty: true }, 120).action,
    'None');
});

test('draft handling always returns to a complete busy and dirty recheck', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 4, 100);
  const prompt = evaluateApplicationClose(waiting, { busy: false, dirty: true }, 100);
  const resumed = resumeApplicationCloseAfterDraftHandling(prompt.state, 4, 200);

  assert.equal(
    evaluateApplicationClose(resumed, { busy: true, dirty: false }, 210).action,
    'WaitForEditors');
  const secondPrompt = evaluateApplicationClose(resumed, { busy: false, dirty: true }, 220);
  assert.equal(secondPrompt.action, 'PromptForUnsavedChanges');
  assert.equal(secondPrompt.requestId, 4);
});

test('cancel denies only the exact close request', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 5, 100);
  const prompt = evaluateApplicationClose(waiting, { busy: false, dirty: true }, 100);

  assert.equal(cancelApplicationClose(prompt.state, 6).action, 'None');
  const canceled = cancelApplicationClose(prompt.state, 5);
  assert.equal(canceled.action, 'DenyCanceled');
  assert.deepEqual(canceled.state, idleApplicationCloseState);
});

test('expired request and late callbacks cannot mutate a newer close request', () => {
  const first = beginApplicationCloseRequest(idleApplicationCloseState, 6, 100);
  const expired = expireApplicationCloseRequest(first, 6);
  const second = beginApplicationCloseRequest(expired, 7, 200);

  assert.equal(resumeApplicationCloseAfterDraftHandling(second, 6, 300), second);
  assert.equal(expireApplicationCloseRequest(second, 6), second);
  assert.equal(cancelApplicationClose(second, 6).action, 'None');
});

test('editor wait timeout denies without approving or discarding drafts', () => {
  const waiting = beginApplicationCloseRequest(idleApplicationCloseState, 8, 100);
  const timedOut = evaluateApplicationClose(
    waiting,
    { busy: true, dirty: true },
    1_100,
    1_000);

  assert.equal(timedOut.action, 'DenyEditorWaitTimedOut');
  assert.equal(timedOut.requestId, 8);
  assert.deepEqual(timedOut.state, idleApplicationCloseState);
});

test('application close model rejects malformed identities and timing values', () => {
  assert.throws(
    () => beginApplicationCloseRequest(idleApplicationCloseState, 0, 1),
    /positive safe integer/u);
  assert.throws(
    () => beginApplicationCloseRequest(idleApplicationCloseState, 1, -1),
    /non-negative safe integer/u);
  assert.throws(
    () => evaluateApplicationClose(idleApplicationCloseState, { busy: false, dirty: false }, 1, 0),
    /positive safe integer/u);
});
