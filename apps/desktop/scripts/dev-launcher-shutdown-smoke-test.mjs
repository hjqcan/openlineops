import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import test from 'node:test';
import {
  waitForChildExit,
  waitForLauncherState
} from './dev-launcher-shutdown-smoke.mjs';

test('launcher state timeout uses bounded redacted diagnostics', async () => {
  const child = createChild(
    `${'x'.repeat(20_000)} standardToken=state-secret`);
  await assert.rejects(
    waitForLauncherState(child, 1),
    error => assertRedactedDiagnostic(error, 'state-secret'));
});

test('launcher terminal state wins over a previously published identity record', async () => {
  const child = createChild(
    'OPENLINEOPS_DEV_LAUNCHER_STATE {"electronPid":4100,"vitePid":4200}\n'
      + 'standardToken=terminal-secret');
  child.exitCode = 1;
  await assert.rejects(
    waitForLauncherState(child, 1_000),
    error => {
      assertRedactedDiagnostic(error, 'terminal-secret');
      assert.match(error.message, /launcher exited/iu);
      return true;
    });
});

test('launcher shutdown timeout uses bounded redacted diagnostics', async () => {
  const child = createChild('safetyToken=shutdown-secret');
  await assert.rejects(
    waitForChildExit(child, 1),
    error => assertRedactedDiagnostic(error, 'shutdown-secret'));
});

function createChild(output) {
  const child = new EventEmitter();
  child.pid = 4000;
  child.exitCode = null;
  child.signalCode = null;
  child.openlineopsLaunchError = null;
  child.openlineopsScenario = 'diagnostic test';
  child.output = [output];
  return child;
}

function assertRedactedDiagnostic(error, secret) {
  assert(error instanceof Error);
  assert(error.message.length < 17_000);
  assert.match(error.message, /<redacted>/u);
  assert.doesNotMatch(error.message, new RegExp(secret, 'u'));
  return true;
}
