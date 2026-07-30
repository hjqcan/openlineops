import assert from 'node:assert/strict';
import test from 'node:test';

import {
  captureBackendSessionIdentity,
  waitForBoundBackendHealth
} from './smoke-backend-health-wait.mjs';

const baseStatus = Object.freeze({
  isRunning: true,
  pid: 4242,
  health: 'Unreachable',
  apiBaseUrl: 'http://127.0.0.1:5135',
  startedAtUtc: '2026-07-30T08:00:00.000Z',
  startedAtUnixMilliseconds: Date.parse('2026-07-30T08:00:00.000Z'),
  lastExitCode: null,
  recentLogs: []
});

test('bound backend health accepts only the same session becoming Healthy', async () => {
  let currentTime = 10_000;
  const probeTimeouts = [];
  const statuses = [
    baseStatus,
    { ...baseStatus, health: 'Healthy' }
  ];
  const sessionIdentity = captureBackendSessionIdentity(
    baseStatus,
    'test backend session');

  const healthy = await waitForBoundBackendHealth({
    sessionIdentity,
    getStatus: async timeoutMilliseconds => {
      probeTimeouts.push(timeoutMilliseconds);
      return statuses.shift();
    },
    timeoutMilliseconds: 1_000,
    pollIntervalMilliseconds: 100,
    description: 'test backend session',
    now: () => currentTime,
    wait: async milliseconds => {
      currentTime += milliseconds;
    }
  });

  assert.equal(healthy.health, 'Healthy');
  assert.deepEqual(probeTimeouts, [1_000, 900]);
  assert.equal(currentTime, 10_100);
});

test('bound backend health rejects process or authenticated session replacement', async () => {
  const sessionIdentity = captureBackendSessionIdentity(
    baseStatus,
    'test backend session');
  const replacements = [
    { ...baseStatus, pid: baseStatus.pid + 1 },
    {
      ...baseStatus,
      startedAtUnixMilliseconds: baseStatus.startedAtUnixMilliseconds + 1
    },
    {
      ...baseStatus,
      startedAtUtc: '2026-07-30T08:00:01.000Z',
      startedAtUnixMilliseconds: Date.parse('2026-07-30T08:00:01.000Z')
    },
    { ...baseStatus, apiBaseUrl: 'http://127.0.0.1:5136' },
    { ...baseStatus, isRunning: false, pid: null },
    { ...baseStatus, lastExitCode: 1 }
  ];

  for (const replacement of replacements) {
    await assert.rejects(
      waitForBoundBackendHealth({
        sessionIdentity,
        getStatus: async () => replacement,
        timeoutMilliseconds: 1_000,
        description: 'test backend session'
      }),
      /changed or exited its bound backend session/u);
  }
});

test('bound backend health timeout is hard and cannot accept a late Healthy probe', async () => {
  let currentTime = 20_000;
  const sessionIdentity = captureBackendSessionIdentity(
    baseStatus,
    'test backend session');
  let probeCount = 0;

  await assert.rejects(
    waitForBoundBackendHealth({
      sessionIdentity,
      getStatus: async () => {
        probeCount += 1;
        return baseStatus;
      },
      timeoutMilliseconds: 250,
      pollIntervalMilliseconds: 100,
      description: 'test backend session',
      now: () => currentTime,
      wait: async milliseconds => {
        currentTime += milliseconds;
      }
    }),
    /Timed out after 250 ms/u);
  assert.equal(currentTime, 20_250);
  assert.equal(probeCount, 3);

  currentTime = 30_000;
  await assert.rejects(
    waitForBoundBackendHealth({
      sessionIdentity,
      getStatus: async timeoutMilliseconds => {
        currentTime += timeoutMilliseconds;
        return { ...baseStatus, health: 'Healthy' };
      },
      timeoutMilliseconds: 250,
      description: 'test backend session',
      now: () => currentTime,
      wait: async () => {}
    }),
    /Timed out after 250 ms/u);
});
