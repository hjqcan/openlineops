import assert from 'node:assert/strict';
import test from 'node:test';
import {
  DesktopCloseRequestCoordinator
} from '../dist-electron/main/desktop-close-request-coordinator.js';

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

test('close request coordinator allows one request and accepts only its matching acknowledgement and response', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(5_000, timer.clock);
  const expired = [];

  const firstRequestId = coordinator.request(requestId => expired.push(requestId));
  assert.equal(firstRequestId, 1);
  assert.equal(coordinator.pendingId, firstRequestId);
  assert.equal(coordinator.phase, 'WaitingForAcknowledgement');
  assert.equal(coordinator.request(requestId => expired.push(requestId)), null);
  assert.equal(coordinator.complete(firstRequestId), false);
  assert.equal(coordinator.acknowledge(firstRequestId + 1), false);
  assert.equal(coordinator.acknowledge(firstRequestId), true);
  assert.equal(coordinator.phase, 'AwaitingDecision');
  assert.equal(coordinator.acknowledge(firstRequestId), false);
  assert.equal(coordinator.complete(firstRequestId + 1), false);
  assert.equal(coordinator.pendingId, firstRequestId);
  assert.equal(coordinator.complete(firstRequestId), true);
  assert.equal(coordinator.pendingId, null);
  assert.deepEqual(expired, []);
});

test('unacknowledged close request expires and its late response cannot close a newer request', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const expired = [];

  const firstRequestId = coordinator.request(requestId => expired.push(requestId));
  assert.equal(timer.entries[0].timeoutMilliseconds, 10);
  timer.fire(timer.entries[0]);

  assert.deepEqual(expired, [firstRequestId]);
  assert.equal(coordinator.pendingId, null);
  const secondRequestId = coordinator.request(requestId => expired.push(requestId));
  assert.equal(secondRequestId, firstRequestId + 1);
  assert.equal(coordinator.complete(firstRequestId), false);
  assert.equal(coordinator.pendingId, secondRequestId);
  coordinator.reset();
  assert.equal(coordinator.pendingId, null);
});

test('acknowledged close request does not expire while the user decides how to handle drafts', () => {
  const timer = createClock();
  const coordinator = new DesktopCloseRequestCoordinator(10, timer.clock);
  const expired = [];
  const requestId = coordinator.request(expiredRequestId => expired.push(expiredRequestId));

  assert.equal(coordinator.acknowledge(requestId), true);
  timer.fire(timer.entries[0]);

  assert.deepEqual(expired, []);
  assert.equal(coordinator.pendingId, requestId);
  assert.equal(coordinator.complete(requestId), true);
});

test('close request coordinator rejects invalid acknowledgement watchdog durations', () => {
  for (const timeout of [0, -1, Number.NaN, Number.POSITIVE_INFINITY, 1.5]) {
    assert.throws(
      () => new DesktopCloseRequestCoordinator(timeout),
      /acknowledgement timeout must be a positive safe integer/u);
  }
});
