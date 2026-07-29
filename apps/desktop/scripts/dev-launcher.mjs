import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import electronPath from 'electron';
import {
  getCurrentWindowsProcessStartedAtUnixMilliseconds
} from './windows-process-identity.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');
const launcherStartedAtUnixMilliseconds =
  await getCurrentWindowsProcessStartedAtUnixMilliseconds();
const viteCliPath = path.join(
  desktopRoot,
  'node_modules',
  'vite',
  'bin',
  'vite.js');
const processTreeHostExecutable = path.join(
  repoRoot,
  'tools',
  'OpenLineOps.ProcessTreeHost',
  'bin',
  'Release',
  'net10.0',
  'OpenLineOps.ProcessTreeHost.exe');
const rendererPort = await allocateLoopbackPort();
const rendererNonce = randomBytes(32).toString('base64url');
const rendererUrl = `http://127.0.0.1:${rendererPort}`;
const configuredUserDataDirectory =
  process.env.OPENLINEOPS_DEV_USER_DATA_DIRECTORY;
if (configuredUserDataDirectory !== undefined
    && !path.isAbsolute(configuredUserDataDirectory)) {
  throw new Error('OPENLINEOPS_DEV_USER_DATA_DIRECTORY must be absolute.');
}
const processTreeHostStat = await fs.stat(processTreeHostExecutable)
  .catch(() => null);
if (!processTreeHostStat?.isFile()) {
  throw new Error(
    'The OpenLineOps Process Tree Host is not built. '
    + 'Run the desktop build:process-tree-host script first.');
}

const environment = {
  ...process.env,
  OPENLINEOPS_RENDERER_NONCE: rendererNonce,
  OPENLINEOPS_RENDERER_PORT: String(rendererPort),
  VITE_DEV_SERVER_URL: rendererUrl,
  OpenLineOps__Desktop__AllowedOrigins__0: rendererUrl,
  OpenLineOps__Desktop__AllowedOrigins__1:
    rendererUrl.replace('127.0.0.1', 'localhost')
};

const vite = spawnContained(
  process.execPath,
  [
    viteCliPath,
    '--host',
    '127.0.0.1',
    '--port',
    String(rendererPort),
    '--strictPort'
  ],
  environment,
  'Vite');
const electron = spawnContained(
  electronPath,
  [
    ...(configuredUserDataDirectory === undefined
      ? []
      : [`--user-data-dir=${path.normalize(configuredUserDataDirectory)}`]),
    desktopRoot
  ],
  environment,
  'Electron');

let stopping = false;
let stopPromise = null;
let startupFailure = null;
let signalStopRequested;
const stopRequested = new Promise(resolve => {
  signalStopRequested = resolve;
});
const childClose = [
  observeChildClose(vite),
  observeChildClose(electron)
];
const allChildrenClosed = Promise.all(childClose);
const requestStop = () => {
  if (stopPromise !== null) {
    return stopPromise;
  }
  stopping = true;
  stopPromise = stopContainedChildren();
  signalStopRequested();
  return stopPromise;
};
const onSignal = signal => {
  process.exitCode = signal === 'SIGINT' ? 130 : 143;
  void requestStop().catch(reportFatalFailure);
};
const onStandardInput = chunk => {
  if (chunk.toString().split(/\r?\n/u)
    .some(line => line.trim() === 'stop')) {
    void requestStop().catch(reportFatalFailure);
  }
};
process.once('SIGINT', onSignal);
process.once('SIGTERM', onSignal);
process.stdin.on('data', onStandardInput);

for (const [child, label, cleanExitAllowed] of [
  [vite, 'Vite', false],
  [electron, 'Electron', true]
]) {
  child.once('error', error => {
    startupFailure = startupFailure ?? error;
    if (!stopping) {
      process.exitCode = 1;
      void requestStop().catch(reportFatalFailure);
    }
  });
  child.once('exit', (code, signal) => {
    if (stopping) {
      return;
    }
    if (signal !== null || code === null) {
      process.stderr.write(
        `${label} Process Tree Host terminated unexpectedly (${
          signal ?? 'unknown status'}).\n`);
      process.exitCode = 1;
    } else if (cleanExitAllowed && code === 0) {
      process.exitCode = 0;
    } else {
      process.stderr.write(
        `${label} Process Tree Host exited unexpectedly with code ${code}.\n`);
      process.exitCode = code === 0 ? 1 : code;
    }
    void requestStop().catch(reportFatalFailure);
  });
}

try {
  const [viteProcessId, electronProcessId] = await Promise.all([
    waitForHostedProcessId(vite, 'Vite', 15_000),
    waitForHostedProcessId(electron, 'Electron', 15_000)
  ]);
  if (process.env.OPENLINEOPS_DEV_LAUNCHER_SMOKE === '1') {
    process.stdout.write(
      `OPENLINEOPS_DEV_LAUNCHER_STATE ${JSON.stringify({
        electronPid: electronProcessId,
        vitePid: viteProcessId
      })}\n`);
  }
  await Promise.race([allChildrenClosed, stopRequested]);
  if (stopPromise === null) {
    await requestStop();
  } else {
    await stopPromise;
  }
  const closeResults = await waitForObservedChildClose(
    allChildrenClosed,
    5_000);
  const closeFailures = closeResults
    .filter(result => result.status === 'rejected')
    .map(result => result.reason);
  if (closeFailures.length > 0) {
    throw new AggregateError(
      closeFailures,
      'A contained development process could not be observed through close.');
  }
  if (startupFailure !== null) {
    throw startupFailure;
  }
} catch (error) {
  process.exitCode = 1;
  let cleanupFailure = null;
  try {
    await requestStop();
  } catch (stopError) {
    cleanupFailure = stopError;
  }
  reportFatalFailure(cleanupFailure === null
    ? error
    : new AggregateError(
        [error, cleanupFailure],
        'Development launcher failed and could not confirm process-tree cleanup.'));
  if (cleanupFailure !== null) {
    // Owner death is the final kernel-enforced cleanup path for any Host that
    // rejected or outlived exact-handle termination.
    setTimeout(() => process.exit(1), 100);
  }
} finally {
  process.removeListener('SIGINT', onSignal);
  process.removeListener('SIGTERM', onSignal);
  process.stdin.off('data', onStandardInput);
  process.stdin.pause();
}

function spawnContained(executablePath, argumentsList, childEnvironment, label) {
  const child = spawn(
    processTreeHostExecutable,
    [
      String(process.pid),
      String(launcherStartedAtUnixMilliseconds),
      path.resolve(executablePath),
      desktopRoot,
      ...argumentsList
    ],
    {
      cwd: path.dirname(processTreeHostExecutable),
      env: childEnvironment,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });
  child.openlineopsOwnsKillOnCloseJob = true;
  child.openlineopsHostedProcessId = null;
  child.openlineopsHostedProcessStartedAtUnixMilliseconds = null;
  child.openlineopsIdentityBuffer = '';
  child.openlineopsLaunchError = null;
  child.stdout.pipe(process.stdout, { end: false });
  child.stderr.pipe(process.stderr, { end: false });
  child.stderr.on('data', chunk => {
    captureHostedProcessId(child, chunk);
  });
  child.once('error', error => {
    child.openlineopsLaunchError = error;
  });
  return child;
}

function captureHostedProcessId(child, chunk) {
  if (child.openlineopsHostedProcessId !== null) {
    return;
  }
  const combined = `${child.openlineopsIdentityBuffer}${chunk.toString()}`;
  const match = combined.match(
    /(?:^|\r?\n)OPENLINEOPS_PROCESS_TREE_ROOT ([1-9][0-9]*) ([1-9][0-9]*)(?:\r?\n|$)/u);
  if (match) {
    const processId = Number(match[1]);
    const startedAtUnixMilliseconds = Number(match[2]);
    if (Number.isSafeInteger(processId) && processId > 0
        && Number.isSafeInteger(startedAtUnixMilliseconds)
        && startedAtUnixMilliseconds > 0) {
      child.openlineopsHostedProcessId = processId;
      child.openlineopsHostedProcessStartedAtUnixMilliseconds =
        startedAtUnixMilliseconds;
    }
  }
  const finalNewline = Math.max(
    combined.lastIndexOf('\n'),
    combined.lastIndexOf('\r'));
  child.openlineopsIdentityBuffer = (
    finalNewline >= 0
      ? combined.slice(finalNewline + 1)
      : combined
  ).slice(-256);
}

async function waitForHostedProcessId(child, label, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (Number.isSafeInteger(child.openlineopsHostedProcessId)
        && child.openlineopsHostedProcessId > 0
        && Number.isSafeInteger(
          child.openlineopsHostedProcessStartedAtUnixMilliseconds)
        && child.openlineopsHostedProcessStartedAtUnixMilliseconds > 0) {
      return child.openlineopsHostedProcessId;
    }
    if (child.openlineopsLaunchError instanceof Error) {
      throw new Error(`${label} Process Tree Host failed to start.`, {
        cause: child.openlineopsLaunchError
      });
    }
    if (hasChildExited(child)) {
      throw new Error(
        `${label} Process Tree Host exited before publishing its child identity: ${
          JSON.stringify({
            exitCode: child.exitCode,
            signalCode: child.signalCode
          })}`);
    }
    await delay(Math.max(1, Math.min(50, deadline - Date.now())));
  }
  throw new Error(
    `${label} Process Tree Host did not publish its child identity within ${
      timeoutMilliseconds} ms.`);
}

async function stopContainedChildren() {
  const results = await Promise.allSettled([
    terminateContainedTree(electron, 'Electron'),
    terminateContainedTree(vite, 'Vite')
  ]);
  const failures = results
    .filter(result => result.status === 'rejected')
    .map(result => result.reason);
  if (failures.length > 0) {
    throw new AggregateError(
      failures,
      'Development launcher could not confirm all process trees stopped.');
  }
}

async function terminateContainedTree(child, label) {
  if (child.openlineopsLaunchError instanceof Error
      && child.pid === undefined) {
    return;
  }
  if (!hasChildExited(child)) {
    const requested = child.kill('SIGKILL');
    if (!requested && !hasChildExited(child)) {
      if (!await waitForChildExit(child, 10_000)) {
        throw new Error(
          `${label} Process Tree Host rejected exact-handle termination and remained alive.`);
      }
    } else if (!await waitForChildExit(child, 10_000)) {
      throw new Error(
        `${label} Process Tree Host did not exit after exact-handle termination.`);
    }
  }
}

function waitForChildExit(child, timeoutMilliseconds) {
  if (hasChildExited(child)) {
    return Promise.resolve(true);
  }
  return new Promise((resolve, reject) => {
    let settled = false;
    const complete = action => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeout);
      child.removeListener('exit', onExit);
      child.removeListener('error', onError);
      action();
    };
    const onExit = () => complete(() => resolve(true));
    const onError = error => complete(() => reject(error));
    const timeout = setTimeout(
      () => complete(() => resolve(false)),
      timeoutMilliseconds);
    child.once('exit', onExit);
    child.once('error', onError);
    if (hasChildExited(child)) {
      complete(() => resolve(true));
    }
  });
}

function waitForChildClose(child, timeoutMilliseconds = null) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const complete = action => {
      if (settled) {
        return;
      }
      settled = true;
      if (timeout !== null) {
        clearTimeout(timeout);
      }
      child.removeListener('close', onClose);
      child.removeListener('error', onError);
      action();
    };
    const onClose = (code, signal) =>
      complete(() => resolve({ code, signal }));
    const onError = error => complete(() => reject(error));
    const timeout = timeoutMilliseconds === null
      ? null
      : setTimeout(
          () => complete(() => reject(new Error(
            `Child process PID ${child.pid ?? 'unknown'} did not close within ${
              timeoutMilliseconds} ms.`))),
          timeoutMilliseconds);
    child.once('close', onClose);
    child.once('error', onError);
  });
}

function observeChildClose(child) {
  return waitForChildClose(child).then(
    value => ({ status: 'fulfilled', value }),
    reason => ({ status: 'rejected', reason }));
}

async function waitForObservedChildClose(observedClose, timeoutMilliseconds) {
  let timeout;
  try {
    return await Promise.race([
      observedClose,
      new Promise((_, reject) => {
        timeout = setTimeout(
          () => reject(new Error(
            `Contained development process streams did not close within ${
              timeoutMilliseconds} ms after Job termination.`)),
          timeoutMilliseconds);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function hasChildExited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

function reportFatalFailure(error) {
  process.exitCode = 1;
  process.stderr.write(
    `${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
}

async function allocateLoopbackPort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        server.close();
        reject(new Error('Could not allocate a loopback renderer port.'));
        return;
      }
      server.close(error =>
        error ? reject(error) : resolve(address.port));
    });
  });
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
