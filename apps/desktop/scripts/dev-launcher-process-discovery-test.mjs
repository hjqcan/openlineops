import assert from 'node:assert/strict';
import test from 'node:test';
import {
  formatLauncherDiagnostics,
  parseApiChildQueryOutput,
  parseProcessIdentityQueryOutput,
  waitForApiChildProcess,
  waitForLauncherProcessIdentity,
  waitForProcessIdentity,
  waitForProcessToDisappear
} from './dev-launcher-process-discovery.mjs';

const electronIdentity = Object.freeze({
  processId: 4100,
  creationTicks: '638999000000000001'
});
const apiIdentity = Object.freeze({
  processId: 4200,
  creationTicks: '638999000000000002'
});

test('API discovery retries a bounded WMI timeout and returns one exact child', async () => {
  const launcher = createLauncher();
  let currentTime = 1_000;
  let queryCount = 0;
  const identity = await waitForApiChildProcess({
    launcher,
    electronIdentity,
    scenario: 'controlled stop',
    timeoutMilliseconds: 2_000,
    dependencies: createFakePollingDependencies({
      now: () => currentTime,
      wait: async milliseconds => {
        currentTime += milliseconds;
      },
      query: async () => {
        queryCount += 1;
        if (queryCount === 1) {
          currentTime += 500;
          throw createQueryTimeout();
        }
        return {
          parentIdentity: electronIdentity,
          childIdentities: [apiIdentity]
        };
      }
    })
  });

  assert.deepEqual(identity, apiIdentity);
  assert.equal(queryCount, 2);
});

test('API discovery normalizes a final near-deadline WMI kill with redacted logs', async () => {
  const launcher = createLauncher(
    'backend failed standardToken=do-not-print safetyToken=also-secret');
  let currentTime = 5_000;
  await assert.rejects(
    waitForApiChildProcess({
      launcher,
      electronIdentity,
      scenario: 'owner termination',
      timeoutMilliseconds: 1_000,
      dependencies: createFakePollingDependencies({
        now: () => currentTime,
        wait: async milliseconds => {
          currentTime += milliseconds;
        },
        query: async () => {
          currentTime += 950;
          throw createQueryTimeout();
        }
      })
    }),
    error => {
      assert.match(
        error.message,
        /Timed out after 1000 ms waiting for the API child/u);
      assert.match(error.message, /"scenario":"owner termination"/u);
      assert.match(error.message, /standardToken=<redacted>/u);
      assert.match(error.message, /safetyToken=<redacted>/u);
      assert.doesNotMatch(error.message, /do-not-print|also-secret/u);
      assert(error.cause instanceof Error);
      assert.match(error.cause.message, /final Windows process query timed out/iu);
      return true;
    });
});

test('API discovery never accepts an identity returned after its hard deadline', async () => {
  const launcher = createLauncher();
  let currentTime = 1_000;
  await assert.rejects(
    waitForApiChildProcess({
      launcher,
      electronIdentity,
      scenario: 'controlled stop',
      timeoutMilliseconds: 500,
      dependencies: createFakePollingDependencies({
        now: () => currentTime,
        query: async () => {
          currentTime = 1_600;
          return {
            parentIdentity: electronIdentity,
            childIdentities: [apiIdentity]
          };
        }
      })
    }),
    /Timed out after 500 ms/u);
});

test('ordinary identity polling never accepts a result after its hard deadline', async () => {
  let currentTime = 2_000;
  await assert.rejects(
    waitForProcessIdentity(4100, 500, {
      now: () => currentTime,
      wait: async milliseconds => {
        currentTime += milliseconds;
      },
      queryProcessIdentity: async () => {
        currentTime = 2_600;
        return electronIdentity;
      },
      minimumQueryBudgetMilliseconds: 100,
      maximumQueryTimeoutMilliseconds: 500,
      pollIntervalMilliseconds: 50
    }),
    /Timed out binding process PID 4100/u);
});

test('launcher terminal state wins over an API identity returned concurrently', async () => {
  const launcher = createLauncher('Development binaries are missing.');
  await assert.rejects(
    waitForApiChildProcess({
      launcher,
      electronIdentity,
      scenario: 'controlled stop',
      timeoutMilliseconds: 2_000,
      dependencies: createFakePollingDependencies({
        query: async () => {
          launcher.exitCode = 1;
          return {
            parentIdentity: electronIdentity,
            childIdentities: [apiIdentity]
          };
        }
      })
    }),
    error => {
      assert.match(error.message, /launcher exited/iu);
      assert.match(error.message, /Development binaries are missing/u);
      return true;
    });
});

test('launcher terminal state wins while binding Electron or Vite identity', async () => {
  const launcher = createLauncher('standardToken=identity-secret');
  let queryCount = 0;
  await assert.rejects(
    waitForLauncherProcessIdentity({
      launcher,
      processId: electronIdentity.processId,
      scenario: 'controlled stop',
      timeoutMilliseconds: 2_000,
      dependencies: {
        now: () => 1_000,
        wait: async () => {},
        queryProcessIdentity: async () => {
          queryCount += 1;
          launcher.exitCode = 1;
          return null;
        },
        minimumQueryBudgetMilliseconds: 100,
        maximumQueryTimeoutMilliseconds: 500,
        pollIntervalMilliseconds: 50
      }
    }),
    error => {
      assert.match(error.message, /launcher exited/iu);
      assert.match(error.message, /standardToken=<redacted>/u);
      assert.doesNotMatch(error.message, /identity-secret/u);
      return true;
    });
  assert.equal(queryCount, 1);
});

test('API discovery rejects Electron PID reuse', async () => {
  const launcher = createLauncher();
  await assert.rejects(
    waitForApiChildProcess({
      launcher,
      electronIdentity,
      scenario: 'controlled stop',
      timeoutMilliseconds: 2_000,
      dependencies: createFakePollingDependencies({
        query: async () => ({
          parentIdentity: {
            ...electronIdentity,
            creationTicks: '638999000000000099'
          },
          childIdentities: [apiIdentity]
        })
      })
    }),
    /Electron process identity changed/u);
});

test('API discovery rejects multiple matching children', async () => {
  const launcher = createLauncher();
  await assert.rejects(
    waitForApiChildProcess({
      launcher,
      electronIdentity,
      scenario: 'controlled stop',
      timeoutMilliseconds: 2_000,
      dependencies: createFakePollingDependencies({
        query: async () => ({
          parentIdentity: electronIdentity,
          childIdentities: [
            apiIdentity,
            {
              processId: 4300,
              creationTicks: '638999000000000003'
            }
          ]
        })
      })
    }),
    /multiple API children/u);
});

test('API process query parser preserves parent and every matching child', () => {
  assert.deepEqual(
    parseApiChildQueryOutput([
      'PARENT|4100|638999000000000001',
      'CHILD|4200|638999000000000002',
      'CHILD|4300|638999000000000003'
    ].join('\r\n')),
    {
      parentIdentity: electronIdentity,
      childIdentities: [
        apiIdentity,
        {
          processId: 4300,
          creationTicks: '638999000000000003'
        }
      ]
    });
  assert.throws(
    () => parseApiChildQueryOutput('unexpected warning'),
    /invalid API process discovery record/u);
  assert.deepEqual(
    parseApiChildQueryOutput('PARENT_NOT_FOUND'),
    { parentIdentity: null, childIdentities: [] });
  assert.throws(
    () => parseApiChildQueryOutput(''),
    /incomplete API process discovery result/u);
});

test('process identity output requires an explicit absence sentinel', async () => {
  assert.equal(parseProcessIdentityQueryOutput('NOT_FOUND\r\n'), null);
  assert.deepEqual(
    parseProcessIdentityQueryOutput('4100|638999000000000001'),
    electronIdentity);
  assert.throws(
    () => parseProcessIdentityQueryOutput(''),
    /invalid process identity result/u);
  assert.throws(
    () => parseProcessIdentityQueryOutput('non-terminating WMI warning'),
    /invalid process identity result/u);

  await assert.rejects(
    waitForProcessToDisappear(electronIdentity, 500, {
      now: () => 1_000,
      wait: async () => {},
      queryProcessIdentity: async () => {
        throw new Error('malformed process query output');
      },
      minimumQueryBudgetMilliseconds: 100,
      maximumQueryTimeoutMilliseconds: 500,
      pollIntervalMilliseconds: 50
    }),
    /Windows process identity query failed/u);
});

test('launcher diagnostics are bounded and redact every captured credential', () => {
  const diagnostics = formatLauncherDiagnostics(
    createLauncher(
      `${'x'.repeat(20_000)} standardToken=secret safetyToken=other-secret`),
    'controlled stop',
    electronIdentity);
  assert(diagnostics.length < 17_000);
  assert.match(diagnostics, /standardToken=<redacted>/u);
  assert.match(diagnostics, /safetyToken=<redacted>/u);
  assert.doesNotMatch(diagnostics, /secret|other-secret/u);
});

function createLauncher(output = '') {
  return {
    pid: 4000,
    exitCode: null,
    signalCode: null,
    openlineopsLaunchError: null,
    output: [output]
  };
}

function createFakePollingDependencies({
  now = () => 1_000,
  wait = async () => {},
  query
}) {
  return {
    now,
    wait,
    queryApiChildProcesses: query,
    minimumQueryBudgetMilliseconds: 100,
    maximumQueryTimeoutMilliseconds: 500,
    pollIntervalMilliseconds: 50
  };
}

function createQueryTimeout() {
  const error = new Error('PowerShell query exceeded its timeout.');
  error.code = null;
  error.killed = true;
  error.signal = 'SIGKILL';
  return error;
}
