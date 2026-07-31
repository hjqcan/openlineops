import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  ElectronCdpHarness,
  stopProcess,
  waitForOwnedProcessClose,
  waitForTarget
} from './electron-cdp-harness.mjs';
import {
  observeProcessClose,
  spawnOwnedProcessTree
} from './owned-process-tree.mjs';

const productionClosureSource = await fs.readFile(
  new URL('./production-closure-e2e.mjs', import.meta.url),
  'utf8');
const missingProcessTreeHostPath =
  `${process.cwd()}\\missing-process-tree-host.exe`;

test('target discovery aborts every stalled request and respects its overall deadline', async () => {
  let abortedRequestCount = 0;
  const startedAt = Date.now();
  const keepEventLoopAlive = setInterval(() => {}, 1_000);
  try {
    await assert.rejects(
      waitForTarget(
        65534,
        80,
        () => ({ state: 'starting' }),
        {
          requestTimeoutMilliseconds: 20,
          fetchImplementation: (_url, options) => new Promise((resolve, reject) => {
            options.signal.addEventListener('abort', () => {
              abortedRequestCount += 1;
              reject(options.signal.reason);
            }, { once: true });
          })
        }),
      /Timed out waiting for packaged Electron CDP/u);
  } finally {
    clearInterval(keepEventLoopAlive);
  }
  const elapsedMilliseconds = Date.now() - startedAt;
  assert.ok(abortedRequestCount >= 1);
  assert.ok(
    elapsedMilliseconds < 1_000,
    `target discovery exceeded its hard test deadline: ${elapsedMilliseconds} ms`);
});

test('target discovery returns the packaged page from a bounded response', async () => {
  const target = await waitForTarget(
    9222,
    1_000,
    () => null,
    {
      requestTimeoutMilliseconds: 50,
      fetchImplementation: async (_url, options) => {
        assert.ok(options.signal instanceof AbortSignal);
        return {
          ok: true,
          json: async () => [
            {
              type: 'service_worker',
              url: 'file:///worker.js',
              webSocketDebuggerUrl: 'ws://worker'
            },
            {
              type: 'page',
              url: 'file:///studio/index.html',
              webSocketDebuggerUrl: 'ws://studio'
            }
          ]
        };
      }
    });
  assert.equal(target.webSocketDebuggerUrl, 'ws://studio');
});

test('process shutdown waits for the exact owning process handle', {
  skip: process.platform !== 'win32'
}, async () => {
  const root = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
    windowsHide: true,
    stdio: 'ignore'
  });
  root.openlineopsOwnsKillOnCloseJob = true;

  try {
    await stopProcess(root, 10_000);
    assert.ok(root.exitCode !== null || root.signalCode !== null);
  } finally {
    await stopProcess(root, 5_000).catch(() => undefined);
  }
});

test('owned command process uses a kill-on-close Job boundary', {
  skip: process.platform !== 'win32'
}, async () => {
  const processTreeHostPath = path.resolve(
    process.cwd(),
    '..',
    '..',
    'tools',
    'OpenLineOps.ProcessTreeHost',
    'bin',
    'Release',
    'net10.0',
    'OpenLineOps.ProcessTreeHost.exe');
  const child = await spawnOwnedProcessTree({
    processTreeHostPath,
    command: process.execPath,
    args: ['-e', 'setInterval(() => {}, 1000)'],
    workingDirectory: process.cwd(),
    environment: process.env
  });
  try {
    assert.equal(child.openlineopsOwnsKillOnCloseJob, true);
    await new Promise(resolve => setTimeout(resolve, 100));
    await stopProcess(child, 5_000);
    assert.notEqual(child.exitCode ?? child.signalCode, null);
  } finally {
    if (child.exitCode === null && child.signalCode === null) {
      await stopProcess(child, 5_000);
    }
  }
});

test('owned command rejects a corrupt Process Tree Host without an unhandled error', {
  skip: process.platform !== 'win32'
}, async () => {
  const temporaryDirectory = await fs.mkdtemp(
    path.join(os.tmpdir(), 'openlineops-corrupt-process-tree-host-'));
  const corruptHostPath = path.join(temporaryDirectory, 'ProcessTreeHost.exe');
  try {
    await fs.writeFile(
      corruptHostPath,
      'This file deliberately is not a Windows executable.',
      'utf8');
    await assert.rejects(
      spawnOwnedProcessTree({
        processTreeHostPath: corruptHostPath,
        command: process.execPath,
        args: ['-e', 'process.exit(0)'],
        workingDirectory: process.cwd(),
        environment: process.env
      }),
      /Process Tree Host failed to start/u);
  } finally {
    await fs.rm(temporaryDirectory, { recursive: true, force: true });
  }
});

test('owned command rejects an observer and inherited-stdio mismatch before spawning', {
  skip: process.platform !== 'win32'
}, async () => {
  const processTreeHostPath = path.resolve(
    process.cwd(),
    '..',
    '..',
    'tools',
    'OpenLineOps.ProcessTreeHost',
    'bin',
    'Release',
    'net10.0',
    'OpenLineOps.ProcessTreeHost.exe');
  await assert.rejects(
    spawnOwnedProcessTree({
      processTreeHostPath,
      command: process.execPath,
      args: ['-e', 'setInterval(() => {}, 1000)'],
      workingDirectory: process.cwd(),
      environment: process.env,
      stdio: 'inherit',
      onStdoutData: () => {}
    }),
    /observer requires piped stdout stdio/u);
});

test('owned process completion waits for close so trailing output is observable', async () => {
  const child = new EventEmitter();
  child.stdout = new EventEmitter();
  let output = '';
  let completed = false;
  child.stdout.on('data', chunk => {
    output += chunk.toString();
  });
  const completion = observeProcessClose(child).then(result => {
    completed = true;
    return result;
  });

  child.emit('exit', 0, null);
  child.stdout.emit('data', Buffer.from('trailing-output'));
  await Promise.resolve();
  assert.equal(completed, false);
  child.emit('close', 0, null);

  assert.deepEqual(await completion, { exitCode: 0, signalCode: null });
  assert.equal(output, 'trailing-output');
});

test('Electron harness close does not settle before trailing process output closes', async () => {
  const child = new EventEmitter();
  child.pid = 48151;
  child.exitCode = null;
  child.signalCode = null;
  child.openlineopsOwnsKillOnCloseJob = true;
  child.openlineopsClosePromise = observeProcessClose(child);
  const logs = [];
  const harness = new ElectronCdpHarness({
    executablePath: `${process.cwd()}\\unused-openlineops.exe`,
    processTreeHostPath: `${process.cwd()}\\unused-process-tree-host.exe`,
    workingDirectory: process.cwd(),
    userDataDirectory: null,
    environment: {},
    logs,
    processControl: {
      stopProcess: async () => {}
    }
  });
  harness.process = child;
  let settled = false;
  const closing = harness.close().then(() => {
    settled = true;
  });

  child.exitCode = 0;
  child.emit('exit', 0, null);
  await Promise.resolve();
  assert.equal(settled, false);
  child.emit('close', 0, null);
  await closing;

  assert.equal(settled, true);
  assert.equal(harness.process, null);
  assert.equal(harness.closed, true);
  await waitForOwnedProcessClose(child, 10);
});

test('signal-terminated uncontained roots fail closed without targeting a recycled PID', async () => {
  const child = spawn(
    process.execPath,
    ['-e', 'setInterval(() => {}, 1000)'],
    { windowsHide: true, stdio: 'ignore' });
  const exited = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', resolve);
  });
  child.kill('SIGTERM');
  await exited;
  const startedAt = Date.now();

  await assert.rejects(
    stopProcess(child, 100),
    /exited before descendant cleanup could be confirmed/u);
  child.openlineopsOwnsKillOnCloseJob = true;
  await stopProcess(child, 100);

  assert.ok(Date.now() - startedAt < 100);
  assert.ok(child.exitCode !== null || child.signalCode !== null);
});

test('asynchronous process spawn failure rejects start and remains safely closable', async () => {
  const logs = [];
  const harness = new ElectronCdpHarness({
    executablePath: `${process.cwd()}\\missing-openlineops-${Date.now()}.exe`,
    processTreeHostPath: missingProcessTreeHostPath,
    workingDirectory: process.cwd(),
    userDataDirectory: null,
    environment: {},
    logs
  });
  const startedAt = Date.now();

  await assert.rejects(
    harness.start(),
    /failed to start before exposing CDP/u);
  assert.ok(
    Date.now() - startedAt < 5_000,
    'spawn failure was not surfaced within the bounded target poll');
  assert.equal(harness.process, null);
  assert.ok(logs.some(line => line.includes('failed to start')));
  await harness.close();
  assert.equal(harness.closed, true);
});

test('close acts only on the owning Process Tree Host, never a reusable backend PID', async () => {
  const stopCalls = [];
  const harness = new ElectronCdpHarness({
    executablePath: 'unused',
    processTreeHostPath: missingProcessTreeHostPath,
    workingDirectory: process.cwd(),
    userDataDirectory: null,
    environment: {},
    logs: [],
    processControl: {
      stopProcess: async processHandle => {
        stopCalls.push(processHandle);
      }
    }
  });
  const rootProcess = {
    pid: 101,
    exitCode: null,
    openlineopsClosePromise: Promise.resolve({
      exitCode: 0,
      signalCode: null
    })
  };
  harness.process = rootProcess;
  harness.cdp = {
    send: async () => ({}),
    close: () => {}
  };

  await harness.close();
  assert.equal(harness.closed, true);
  assert.equal(harness.process, null);
  assert.deepEqual(stopCalls, [rootProcess]);
});

test('debugger pause command failure observes the pending event rejection', async () => {
  const harness = new ElectronCdpHarness({
    executablePath: 'unused',
    processTreeHostPath: missingProcessTreeHostPath,
    workingDirectory: process.cwd(),
    userDataDirectory: null,
    environment: {},
    logs: []
  });
  harness.cdp = {
    send: async method => {
      if (method === 'Debugger.pause') {
        throw new Error('Synthetic debugger pause failure.');
      }
      return {};
    },
    waitForEvent: () => new Promise((resolve, reject) => {
      setTimeout(
        () => reject(new Error('Synthetic delayed event failure.')),
        20);
    })
  };

  await assert.rejects(
    harness.captureJavaScriptStack(100),
    /Synthetic debugger pause failure/u);
  await new Promise(resolve => setTimeout(resolve, 40));
});

test('production closure external operations all carry hard deadlines', () => {
  assert.match(
    productionClosureSource,
    /let harness = null;/u,
    'pre-launch failures must be treated as having no application process to stop');
  assert.match(
    productionClosureSource,
    /terminateWindowsProcessIdentity\(\{[\s\S]*?processId:\s*backend\.pid,[\s\S]*?startedAtUnixMilliseconds:\s*backend\.startedAtUnixMilliseconds/u);
  assert.doesNotMatch(
    productionClosureSource,
    /taskkill\.exe/u);
  assert.match(
    productionClosureSource,
    /fetch\([\s\S]*?authorization:[\s\S]*?signal:\s*AbortSignal\.timeout\(30_000\)/u);
  assert.match(
    productionClosureSource,
    /async function listVendorProcesses\(\)[\s\S]*?execFileAsync\([\s\S]*?timeout:\s*5_000[\s\S]*?killSignal:\s*'SIGKILL'/u);
  assert.match(
    productionClosureSource,
    /async function runCommand\([\s\S]*?setTimeout\([\s\S]*?stopProcess\(child,\s*15_000\)/u);
  const finalization = productionClosureSource.slice(
    productionClosureSource.indexOf('.finally(async () =>'));
  assert.ok(
    finalization.indexOf('await writePrivateHandoff()')
      < finalization.indexOf('await fs.rm(privateExecutionRoot'),
    'private handoff must be written before its project root can be removed');
  assert.match(
    finalization,
    /privateHandoffPath !== null[\s\S]*?preservePrivateExecutionRoot = true;[\s\S]*?await writePrivateHandoff\(\)/u);
});
