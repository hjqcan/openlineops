import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import {
  DesktopCloseRequestCoordinator,
  desktopCloseBindingsEqual
} from '../dist-electron/main/desktop-close-request-coordinator.js';
import {
  DesktopApplicationQuitCoordinator
} from '../dist-electron/main/desktop-application-quit-coordinator.js';
import {
  hasDesktopBackendProcessExited,
  stopDesktopBackendForApplicationShutdown
} from '../dist-electron/main/desktop-backend-shutdown.js';
import {
  DesktopRendererRecoveryCoordinator
} from '../dist-electron/main/desktop-renderer-recovery-coordinator.js';
import {
  DesktopRendererCloseReadiness
} from '../dist-electron/main/desktop-renderer-close-readiness.js';

const mainProcessSource = await readFile(
  new URL('../src/main/main.ts', import.meta.url),
  'utf8');
const preloadSource = await readFile(
  new URL('../src/preload/preload.cts', import.meta.url),
  'utf8');
const rendererSource = await readFile(
  new URL('../src/renderer/main.tsx', import.meta.url),
  'utf8');

function createClock() {
  const entries = [];
  return {
    entries,
    clock: {
      schedule(callback, timeoutMilliseconds) {
        const entry = { callback, timeoutMilliseconds, canceled: false };
        entries.push(entry);
        return entry;
      },
      cancel(entry) {
        entry.canceled = true;
      }
    },
    fire(entry) {
      if (!entry.canceled) entry.callback();
    }
  };
}

function binding(
  windowId = 1,
  webContentsId = 10,
  rendererGeneration = 1
) {
  return { windowId, webContentsId, rendererGeneration };
}

test('close request coordinator allows one request and accepts only its matching acknowledgement and response', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(5_000, timer.clock);
  const expired = [];
  const owner = binding();
  const otherWindow = binding(2, 20);
  const staleRenderer = binding(1, 10, 2);

  const firstRequestId = coordinator.request(owner, requestId => expired.push(requestId));
  assert.equal(firstRequestId, 1);
  assert.equal(coordinator.pendingId, firstRequestId);
  assert.equal(coordinator.phase, 'WaitingForAcknowledgement');
  assert.deepEqual(coordinator.binding, owner);
  assert.equal(coordinator.request(otherWindow, requestId => expired.push(requestId)), null);
  assert.equal(coordinator.complete(owner, firstRequestId), false);
  assert.equal(coordinator.acknowledge(owner, firstRequestId + 1), false);
  assert.equal(coordinator.acknowledge(otherWindow, firstRequestId), false);
  assert.equal(coordinator.acknowledge(staleRenderer, firstRequestId), false);
  assert.equal(coordinator.acknowledge(owner, firstRequestId), true);
  assert.equal(coordinator.phase, 'AwaitingDecision');
  assert.equal(coordinator.acknowledge(owner, firstRequestId), false);
  assert.equal(coordinator.complete(otherWindow, firstRequestId), false);
  assert.equal(coordinator.complete(staleRenderer, firstRequestId), false);
  assert.equal(coordinator.complete(owner, firstRequestId + 1), false);
  assert.equal(coordinator.pendingId, firstRequestId);
  assert.equal(coordinator.complete(owner, firstRequestId), true);
  assert.equal(coordinator.pendingId, null);
  assert.equal(coordinator.binding, null);
  assert.deepEqual(expired, []);
});

test('unacknowledged close request expires and its late response cannot close a newer request', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const expired = [];
  const owner = binding();

  const firstRequestId = coordinator.request(owner, requestId => expired.push(requestId));
  assert.equal(timer.entries[0].timeoutMilliseconds, 10);
  timer.fire(timer.entries[0]);

  assert.deepEqual(expired, [firstRequestId]);
  assert.equal(coordinator.pendingId, null);
  const secondRequestId = coordinator.request(owner, requestId => expired.push(requestId));
  assert.equal(secondRequestId, firstRequestId + 1);
  assert.equal(coordinator.acknowledge(owner, firstRequestId), false);
  assert.equal(coordinator.complete(owner, firstRequestId), false);
  assert.equal(coordinator.pendingId, secondRequestId);
  coordinator.reset();
  assert.equal(coordinator.pendingId, null);
});

test('acknowledged close request does not expire while the user decides how to handle drafts', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const expired = [];
  const owner = binding();
  const requestId = coordinator.request(
    owner,
    expiredRequestId => expired.push(expiredRequestId));

  assert.equal(coordinator.acknowledge(owner, requestId), true);
  timer.fire(timer.entries[0]);

  assert.deepEqual(expired, []);
  assert.equal(coordinator.pendingId, requestId);
  assert.equal(coordinator.complete(owner, requestId), true);
});

test('renderer lifecycle cleanup rejects stale IPC and the next close receives a higher request ID', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const firstRenderer = binding(1, 10, 1);
  const nextRenderer = binding(1, 10, 2);
  const expired = [];

  const firstRequestId = coordinator.request(
    firstRenderer,
    requestId => expired.push(requestId));
  assert.equal(coordinator.acknowledge(firstRenderer, firstRequestId), true);
  assert.equal(coordinator.release(nextRenderer), false);
  assert.equal(coordinator.release(firstRenderer), true);
  assert.equal(coordinator.acknowledge(firstRenderer, firstRequestId), false);
  assert.equal(coordinator.complete(firstRenderer, firstRequestId), false);

  const secondRequestId = coordinator.request(
    nextRenderer,
    requestId => expired.push(requestId));
  assert.equal(secondRequestId, firstRequestId + 1);
  timer.fire(timer.entries[0]);
  assert.equal(coordinator.pendingId, secondRequestId);
  assert.deepEqual(expired, []);
});

test('renderer cleanup before acknowledgement cancels the exact watchdog without affecting a newer request', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const firstRenderer = binding(1, 10, 1);
  const nextRenderer = binding(1, 10, 2);
  const expired = [];

  const firstRequestId = coordinator.request(
    firstRenderer,
    requestId => expired.push(requestId));
  assert.equal(coordinator.release(firstRenderer), true);
  assert.equal(timer.entries[0].canceled, true);

  const secondRequestId = coordinator.request(
    nextRenderer,
    requestId => expired.push(requestId));
  timer.fire(timer.entries[0]);
  assert.equal(coordinator.pendingId, secondRequestId);
  assert.equal(coordinator.acknowledge(firstRenderer, firstRequestId), false);
  assert.deepEqual(expired, []);
});

test('a denied close decision is completed exactly once before a higher request ID is issued', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const owner = binding();

  const canceledRequestId = coordinator.request(owner, () => assert.fail('must not expire'));
  assert.equal(coordinator.acknowledge(owner, canceledRequestId), true);
  assert.equal(coordinator.complete(owner, canceledRequestId), true);
  assert.equal(coordinator.complete(owner, canceledRequestId), false);

  const retryRequestId = coordinator.request(owner, () => assert.fail('must not expire'));
  assert.equal(retryRequestId, canceledRequestId + 1);
});

test('close request coordinator validates every binding identity', () => {
  const coordinator = new DesktopCloseRequestCoordinator();
  const malformedBindings = [
    null,
    {},
    binding(0),
    binding(1, 0),
    binding(1, 10, 0),
    binding(1.5),
    binding(1, Number.POSITIVE_INFINITY),
    binding(1, 10, Number.NaN)
  ];

  for (const malformedBinding of malformedBindings) {
    assert.throws(
      () => coordinator.request(malformedBinding, () => undefined),
      /binding must contain positive safe integer identities/u);
  }
});

test('close coordinator binding equality rejects stale windows, senders, and generations', () => {
  const current = binding(1, 10, 3);
  assert.equal(desktopCloseBindingsEqual({ ...current }, current), true);
  assert.equal(desktopCloseBindingsEqual(binding(2, 10, 3), current), false);
  assert.equal(desktopCloseBindingsEqual(binding(1, 11, 3), current), false);
  assert.equal(desktopCloseBindingsEqual(binding(1, 10, 2), current), false);
  assert.equal(desktopCloseBindingsEqual(null, current), false);
});

test('window teardown uses the captured WebContents identity after BrowserWindow destruction', () => {
  assert.match(
    mainProcessSource,
    /const ownedWebContents = ownedWindow\.webContents;[\s\S]*?const ownedWebContentsId = ownedWebContents\.id;/u);
  assert.match(
    mainProcessSource,
    /ownedWebContents\.once\('destroyed',[\s\S]*?desktopCloseSessions\.delete\(ownedWebContentsId\);/u);
  assert.match(
    mainProcessSource,
    /ownedWindow\.on\('closed',[\s\S]*?desktopCloseSessions\.delete\(ownedWebContentsId\);/u);
  assert.doesNotMatch(
    mainProcessSource,
    /desktopCloseSessions\.delete\(ownedWindow\.webContents\.id\)/u);
});

test('application quit waits for one coordinated window decision and the backend shutdown barrier', () => {
  const coordinator = new DesktopApplicationQuitCoordinator();

  assert.equal(coordinator.handleBeforeQuit(true), 'BeginCoordinatedWindowClose');
  assert.equal(coordinator.phase, 'WaitingForWindowClose');
  assert.equal(coordinator.handleBeforeQuit(true), 'WaitForCoordinatedWindowClose');
  assert.equal(coordinator.cancelCoordinatedWindowClose(), true);
  assert.equal(coordinator.phase, 'Idle');

  assert.equal(coordinator.handleBeforeQuit(true), 'BeginCoordinatedWindowClose');
  assert.equal(coordinator.handleBeforeQuit(false), 'BeginFinalShutdown');
  assert.equal(coordinator.phase, 'StoppingBackend');
  assert.equal(coordinator.cancelCoordinatedWindowClose(), false);
  assert.equal(coordinator.handleBeforeQuit(false), 'WaitForFinalShutdown');
  assert.equal(coordinator.completeFinalShutdown(), true);
  assert.equal(coordinator.phase, 'Finalizing');
  assert.equal(coordinator.handleBeforeQuit(false), 'AllowFinalShutdown');
  assert.equal(coordinator.completeFinalShutdown(), false);
});

test('clean renderer exit releases close ownership and cannot strand application quit', () => {
  const coordinator = new DesktopApplicationQuitCoordinator();
  assert.equal(coordinator.handleBeforeQuit(true), 'BeginCoordinatedWindowClose');
  assert.equal(coordinator.phase, 'WaitingForWindowClose');

  const closeRequestReleased = true;
  if (closeRequestReleased) {
    coordinator.cancelCoordinatedWindowClose();
  }

  assert.equal(coordinator.phase, 'Idle');
  assert.match(
    mainProcessSource,
    /render-process-gone[\s\S]*?const closeRequestReleased = releaseOwnedCloseRequest\([\s\S]*?if \(closeRequestReleased\) \{[\s\S]*?cancelCoordinatedApplicationQuit\([\s\S]*?if \(details\.reason === 'clean-exit'\) \{[\s\S]*?setImmediate\([\s\S]*?rendererGeneration === closeSession\.readiness\.generation[\s\S]*?requestRendererRecovery\('RendererGone'\)/u);
});

test('failed backend termination remains retryable and can return to a usable Studio', () => {
  const retryCoordinator = new DesktopApplicationQuitCoordinator();
  assert.equal(retryCoordinator.handleBeforeQuit(false), 'BeginFinalShutdown');
  assert.equal(retryCoordinator.failFinalShutdown(), true);
  assert.equal(retryCoordinator.phase, 'ShutdownFailed');
  assert.equal(retryCoordinator.completeFinalShutdown(), false);
  assert.equal(retryCoordinator.handleBeforeQuit(false), 'BeginFinalShutdown');
  assert.equal(retryCoordinator.phase, 'StoppingBackend');

  const reopenCoordinator = new DesktopApplicationQuitCoordinator();
  assert.equal(reopenCoordinator.handleBeforeQuit(false), 'BeginFinalShutdown');
  assert.equal(reopenCoordinator.failFinalShutdown(), true);
  assert.equal(reopenCoordinator.resumeAfterFailedShutdown(), true);
  assert.equal(reopenCoordinator.phase, 'Idle');
  assert.equal(reopenCoordinator.resumeAfterFailedShutdown(), false);
  assert.equal(reopenCoordinator.handleBeforeQuit(true), 'BeginCoordinatedWindowClose');
});

test('backend shutdown failure preserves process state and never reaches finalization', async () => {
  const processIdentity = { pid: 42 };
  const events = [];

  await assert.rejects(
    stopDesktopBackendForApplicationShutdown(
      processIdentity,
      async process => {
        assert.equal(process, processIdentity);
        events.push('terminate');
        throw new Error('process tree is still alive');
      },
      () => {
        events.push('release');
      }),
    /process tree is still alive/u);

  assert.deepEqual(events, ['terminate']);
});

test('backend state is released only after confirmed process-tree termination', async () => {
  const processIdentity = { pid: 43 };
  const events = [];

  await stopDesktopBackendForApplicationShutdown(
    processIdentity,
    async process => {
      assert.equal(process, processIdentity);
      events.push('terminate');
    },
    process => {
      assert.equal(process, processIdentity);
      events.push('release');
    });

  assert.deepEqual(events, ['terminate', 'release']);
});

test('backend exit detection accepts both normal and signal termination', () => {
  assert.equal(
    hasDesktopBackendProcessExited({ exitCode: null, signalCode: null }),
    false);
  assert.equal(
    hasDesktopBackendProcessExited({ exitCode: 0, signalCode: null }),
    true);
  assert.equal(
    hasDesktopBackendProcessExited({ exitCode: null, signalCode: 'SIGTERM' }),
    true);
});

test('renderer recovery escalates an unresponsive prompt to renderer-gone for the same generation', () => {
  const coordinator = new DesktopRendererRecoveryCoordinator();
  const unresponsive = coordinator.request('Unresponsive', 1);
  assert.equal(unresponsive.action, 'Start');

  const rendererGone = coordinator.request('RendererGone', 1);
  assert.equal(rendererGone.action, 'Queue');
  assert.deepEqual(coordinator.queued, rendererGone.request);
  assert.deepEqual(
    coordinator.complete(unresponsive.request, 1),
    rendererGone.request);
  assert.equal(coordinator.complete(rendererGone.request, 1), null);
});

test('newer renderer recovery supersedes an older generation regardless of reason priority', () => {
  const coordinator = new DesktopRendererRecoveryCoordinator();
  const oldRendererGone = coordinator.request('RendererGone', 1);
  const newUnresponsive = coordinator.request('Unresponsive', 2);

  assert.equal(oldRendererGone.action, 'Start');
  assert.equal(newUnresponsive.action, 'Queue');
  assert.deepEqual(
    coordinator.complete(oldRendererGone.request, 2),
    newUnresponsive.request);
  assert.equal(coordinator.request('RendererGone', 1).action, 'Ignore');
});

test('renderer close readiness accepts both registration and load event orders', () => {
  const registrationFirst = new DesktopRendererCloseReadiness();
  assert.equal(registrationFirst.setCoordinatorRegistered(1, true), true);
  assert.equal(registrationFirst.isReady, false);
  assert.equal(registrationFirst.completeLoad(1), true);
  assert.equal(registrationFirst.isReady, true);

  const loadFirst = new DesktopRendererCloseReadiness();
  assert.equal(loadFirst.completeLoad(1), true);
  assert.equal(loadFirst.isReady, false);
  assert.equal(loadFirst.setCoordinatorRegistered(1, true), true);
  assert.equal(loadFirst.isReady, true);
});

test('renderer close readiness rejects stale generations and requires responsiveness', () => {
  const readiness = new DesktopRendererCloseReadiness();
  assert.equal(readiness.completeLoad(1), true);
  assert.equal(readiness.setCoordinatorRegistered(1, true), true);
  assert.equal(readiness.isReady, true);

  assert.equal(readiness.startNavigation(), 2);
  assert.equal(readiness.isReady, false);
  assert.equal(readiness.completeLoad(1), false);
  assert.equal(readiness.setCoordinatorRegistered(1, true), false);
  assert.equal(readiness.completeLoad(2), true);
  assert.equal(readiness.setCoordinatorRegistered(2, true), true);
  assert.equal(readiness.isReady, true);
  assert.equal(readiness.setResponsive(2, false), true);
  assert.equal(readiness.isReady, false);
  assert.equal(readiness.setResponsive(1, true), false);
  assert.equal(readiness.setResponsive(2, true), true);
  assert.equal(readiness.isReady, true);
});

test('close coordinator readiness is explicit, load-gated, current-frame-only, and reversible', () => {
  assert.match(
    preloadSource,
    /getCloseCoordinatorBinding:[\s\S]*?desktop:get-close-coordinator-binding[\s\S]*?setCloseCoordinatorReady:[\s\S]*?ipcRenderer\.send\('desktop:close-coordinator-ready', binding, ready\)/u);
  assert.match(
    rendererSource,
    /onCloseRequested[\s\S]*?onCloseRequestExpired[\s\S]*?getCloseCoordinatorBinding\(\)[\s\S]*?setCloseCoordinatorReady\(binding, true\)[\s\S]*?return \(\) => \{[\s\S]*?setCloseCoordinatorReady\(closeCoordinatorBinding, false\)/u);
  assert.match(
    mainProcessSource,
    /desktopCloseBindingsEqual\(binding, closeSession\.binding\)[\s\S]*?setCoordinatorRegistered\([\s\S]*?closeSession\.readiness\.generation,[\s\S]*?true\)/u);
  assert.match(
    mainProcessSource,
    /event\.senderFrame !== event\.sender\.mainFrame/u);
  assert.match(
    mainProcessSource,
    /on\('did-finish-load',[\s\S]{0,160}completeLoad\(closeSession\.readiness\.generation\)/u);
});

test('application before-quit wiring cannot tear down the backend before coordinated close approval', () => {
  assert.match(
    mainProcessSource,
    /app\.on\('before-quit', event => \{[\s\S]*?event\.preventDefault\(\);[\s\S]*?BeginCoordinatedWindowClose[\s\S]*?windowToCoordinate\?\.close\(\)/u);
  assert.match(
    mainProcessSource,
    /if \(!allowClose\) \{[\s\S]*?cancelCoordinatedApplicationQuit/u);
  assert.match(
    mainProcessSource,
    /beginFinalApplicationShutdown[\s\S]*?await stopBackendAndReleaseState\(child\)[\s\S]*?completeFinalShutdown\(\)[\s\S]*?app\.quit\(\)/u);
  assert.match(
    mainProcessSource,
    /Backend termination failed during Studio shutdown[\s\S]*?failFinalShutdown\(\)[\s\S]*?presentApplicationShutdownFailure/u);
  assert.match(
    mainProcessSource,
    /app\.on\('second-instance',[\s\S]*?phase === 'ShutdownFailed'[\s\S]*?mainWindow\.destroy\(\)[\s\S]*?app\.quit\(\)/u);
  assert.match(
    mainProcessSource,
    /Studio startup failed closed[\s\S]*?await stopBackendAndReleaseState\(child\)[\s\S]*?Backend termination failed during Studio startup shutdown[\s\S]*?mainWindow\.destroy\(\)[\s\S]*?app\.quit\(\);[\s\S]*?return;[\s\S]*?app\.exit\(1\)/u);
  assert.match(
    mainProcessSource,
    /const terminationAccepted = child\.kill\(\)[\s\S]*?await waitForBackendProcessExit/u);
  assert.match(
    mainProcessSource,
    /hasDesktopBackendProcessExited\(child\)/u);
  assert.doesNotMatch(
    mainProcessSource,
    /taskkill\.exe/u);
});

test('close request coordinator rejects invalid acknowledgement watchdog durations', () => {
  for (const timeout of [0, -1, Number.NaN, Number.POSITIVE_INFINITY, 1.5]) {
    assert.throws(
      () => new DesktopCloseRequestCoordinator(timeout),
      /acknowledgement timeout must be a positive safe integer/u);
  }
});
