import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

import {
  BackendSessionLifecycle
} from '../dist-electron/main/backend-session-lifecycle.js';

const mainSource = await readFile(
  new URL('../src/main/main.ts', import.meta.url),
  'utf8');

function pendingSession(process) {
  return {
    process,
    standardToken: 'standard-session-token',
    safetyToken: 'safety-session-token',
    nonce: 'backend-session-nonce',
    handshakePath: 'backend-session-handshake.json'
  };
}

test('authentication atomically promotes the exact pending session', () => {
  const lifecycle = new BackendSessionLifecycle();
  const process = { name: 'backend' };
  const pending = lifecycle.beginPending(pendingSession(process));

  assert.equal(lifecycle.pending, pending);
  assert.equal(lifecycle.authenticated, null);
  assert.equal(lifecycle.process, process);

  const authenticated = lifecycle.authenticate(
    process,
    'http://127.0.0.1:5135');

  assert.equal(lifecycle.pending, null);
  assert.equal(lifecycle.authenticated, authenticated);
  assert.equal(authenticated.process, process);
  assert.equal(authenticated.standardToken, pending.standardToken);
  assert.equal(authenticated.safetyToken, pending.safetyToken);
  assert.equal(authenticated.nonce, pending.nonce);
  assert.equal(authenticated.handshakePath, pending.handshakePath);
  assert.equal(authenticated.apiBaseUrl, 'http://127.0.0.1:5135');
});

test('authentication failure and rejected termination retain identity and credentials', () => {
  const lifecycle = new BackendSessionLifecycle();
  const process = { name: 'backend' };
  const pending = lifecycle.beginPending(pendingSession(process));

  assert.throws(
    () => lifecycle.releaseConfirmedExit(process, false),
    /cannot be released before confirmed exit/u);
  assert.equal(lifecycle.pending, pending);
  assert.equal(lifecycle.process, process);
  assert.equal(lifecycle.pending?.standardToken, 'standard-session-token');
  assert.equal(lifecycle.pending?.safetyToken, 'safety-session-token');
  assert.equal(lifecycle.pending?.nonce, 'backend-session-nonce');

  assert.equal(lifecycle.releaseConfirmedExit(process, true), pending);
  assert.equal(lifecycle.pending, null);
  assert.equal(lifecycle.authenticated, null);
  assert.equal(lifecycle.process, null);
});

test('a stale release cannot clear a replacement session or its handshake', () => {
  const lifecycle = new BackendSessionLifecycle();
  const retainedProcess = { name: 'retained-backend' };
  const retained = lifecycle.beginPending(pendingSession(retainedProcess));

  assert.equal(
    lifecycle.releaseConfirmedExit(retainedProcess, true),
    retained);

  const replacementProcess = { name: 'replacement-backend' };
  const replacement = lifecycle.beginPending({
    process: replacementProcess,
    standardToken: 'replacement-standard-token',
    safetyToken: 'replacement-safety-token',
    nonce: 'replacement-nonce',
    handshakePath: 'replacement-handshake.json'
  });

  assert.equal(
    lifecycle.releaseConfirmedExit(retainedProcess, true),
    null);
  assert.equal(lifecycle.process, replacementProcess);
  assert.equal(lifecycle.pending, replacement);
  assert.equal(
    lifecycle.pending?.handshakePath,
    'replacement-handshake.json');
});

test('a confirmed spawn failure releases pending credentials without an exit', () => {
  const lifecycle = new BackendSessionLifecycle();
  const process = { name: 'failed-spawn' };
  lifecycle.beginPending(pendingSession(process));

  assert.equal(
    lifecycle.releaseFailedSpawn(process)?.handshakePath,
    'backend-session-handshake.json');
  assert.equal(lifecycle.process, null);
  assert.equal(lifecycle.releaseFailedSpawn(process), null);
});

test('desktop launch retains the pending session before observing authentication', () => {
  const beginIndex = mainSource.indexOf('backendSessionLifecycle.beginPending({');
  const processErrorIndex = mainSource.indexOf("child.on('error'", beginIndex);
  const authenticationIndex = mainSource.indexOf(
    'backendSessionLifecycle.authenticate(child, apiBaseUrl)',
    processErrorIndex);
  const terminationFailureIndex = mainSource.indexOf(
    'catch (terminationError)',
    authenticationIndex);
  const confirmedReleaseIndex = mainSource.indexOf(
    'releaseBackendSessionAfterConfirmedExit(child)',
    terminationFailureIndex);

  assert.ok(beginIndex >= 0);
  assert.ok(processErrorIndex > beginIndex);
  assert.ok(authenticationIndex > processErrorIndex);
  assert.ok(terminationFailureIndex > authenticationIndex);
  assert.ok(confirmedReleaseIndex > terminationFailureIndex);
  assert.match(
    mainSource.slice(terminationFailureIndex, confirmedReleaseIndex),
    /process tree could not be confirmed stopped/u);
  assert.doesNotMatch(
    mainSource.slice(terminationFailureIndex, confirmedReleaseIndex),
    /releaseBackendSessionAfter(?:ConfirmedExit|FailedSpawn)/u);
  assert.match(
    mainSource,
    /child\.on\('exit',[\s\S]*?releaseBackendSessionAfterConfirmedExit\(child\)/u);
  assert.match(
    mainSource,
    /const releasedSession = backendSessionLifecycle\.releaseConfirmedExit\([\s\S]*?if \(releasedSession === null\) \{\s*return;\s*\}[\s\S]*?cleanupBackendHandshakeForSession\(releasedSession\.handshakePath\)/u);
  assert.doesNotMatch(
    mainSource,
    /pendingHandshakePath/u);
});
