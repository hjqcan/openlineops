import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import {
  prepareDevelopmentHosts
} from './development-hosts.mjs';
import {
  createLauncherDiagnosticError,
  formatLauncherDiagnostics,
  waitForApiChildProcess,
  waitForLauncherProcessIdentity,
  waitForProcessToDisappear
} from './dev-launcher-process-discovery.mjs';
import {
  resolveDotnetExecutablePath,
  spawnOwnedProcessTree
} from './owned-process-tree.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');
const launcherPath = path.join(desktopRoot, 'scripts', 'dev-launcher.mjs');
const processTreeHostPath = path.join(
  repoRoot,
  'tools',
  'OpenLineOps.ProcessTreeHost',
  'bin',
  'Release',
  'net10.0',
  'OpenLineOps.ProcessTreeHost.exe');
const physicalTempRoot = await fs.realpath(os.tmpdir());

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  await main();
}

async function main() {
  await buildDevelopmentHosts();
  await runScenario('controlled stop', async launcher => {
    launcher.stdin.end('stop\n');
    const exit = await waitForChildExit(launcher, 20_000);
    const diagnostics = formatLauncherDiagnostics(
      launcher,
      launcher.openlineopsScenario);
    assert.equal(exit.code, 0, diagnostics);
    assert.equal(exit.signal, null, diagnostics);
  });
  await runScenario('owner termination', async launcher => {
    const requested = launcher.kill('SIGKILL');
    assert(requested, 'Dev launcher root did not accept strong termination.');
    const exit = await waitForChildExit(launcher, 20_000);
    assert(
      exit.signal !== null || exit.code !== 0,
      `Strongly terminated dev launcher reported a clean exit.\n${
        formatLauncherDiagnostics(
          launcher,
          launcher.openlineopsScenario)}`);
  });

  console.log(
    'Dev launcher shutdown smoke passed for controlled stop and owner death; '
    + 'Vite, Electron, and API trees were fully terminated.');
}

async function runScenario(description, stopLauncher) {
  const userDataDirectory = await fs.mkdtemp(
    path.join(
      physicalTempRoot,
      `openlineops-dev-launcher-${description.replaceAll(' ', '-')}-`));
  const launcher = startLauncher(userDataDirectory, description);
  try {
    const state = await waitForLauncherState(launcher, 20_000);
    assert(Number.isSafeInteger(state.electronPid) && state.electronPid > 0);
    assert(Number.isSafeInteger(state.vitePid) && state.vitePid > 0);
    const [electronProcessIdentity, viteProcessIdentity] = await Promise.all([
      waitForLauncherProcessIdentity({
        launcher,
        processId: state.electronPid,
        scenario: description,
        timeoutMilliseconds: 10_000
      }),
      waitForLauncherProcessIdentity({
        launcher,
        processId: state.vitePid,
        scenario: description,
        timeoutMilliseconds: 10_000
      })
    ]);
    const apiProcessIdentity = await waitForApiChildProcess({
      launcher,
      electronIdentity: electronProcessIdentity,
      scenario: description,
      timeoutMilliseconds: 30_000
    });

    await stopLauncher(launcher);
    await waitForProcessToDisappear(apiProcessIdentity, 15_000);
    await waitForProcessToDisappear(electronProcessIdentity, 15_000);
    await waitForProcessToDisappear(viteProcessIdentity, 15_000);
  } finally {
    if (launcher.pid
        && launcher.exitCode === null
        && launcher.signalCode === null) {
      const accepted = launcher.kill('SIGKILL');
      if (!accepted
          && launcher.exitCode === null
          && launcher.signalCode === null) {
        throw new Error('Dev launcher rejected exact-handle cleanup termination.');
      }
      await waitForChildExit(launcher, 10_000);
    }
    await fs.rm(userDataDirectory, {
      force: true,
      recursive: true,
      maxRetries: 10,
      retryDelay: 200
    });
  }
}

function startLauncher(userDataDirectory, scenario) {
  const output = [];
  const launcher = spawn(process.execPath, [launcherPath], {
    cwd: desktopRoot,
    env: {
      ...process.env,
      OPENLINEOPS_DEV_LAUNCHER_SMOKE: '1',
      OPENLINEOPS_DEV_USER_DATA_DIRECTORY: userDataDirectory
    },
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true
  });
  launcher.output = output;
  launcher.openlineopsScenario = scenario;
  launcher.openlineopsLaunchError = null;
  launcher.stdout.on('data', chunk => output.push(chunk.toString()));
  launcher.stderr.on('data', chunk => output.push(chunk.toString()));
  launcher.once('error', error => {
    launcher.openlineopsLaunchError = error;
    output.push(`Dev launcher failed to start: ${error.message}`);
  });
  return launcher;
}

async function buildDevelopmentHosts() {
  const dotnetExecutable = await resolveDotnetExecutablePath(process.env);
  const hosts = await prepareDevelopmentHosts({
    repoRoot,
    build: (host, configuration) => runDevelopmentHostBuild(
      dotnetExecutable,
      host,
      configuration)
  });
  console.log(
    `Development hosts ready (Debug): ${
      hosts.map(host => host.projectName).join(', ')}`);
}

async function runDevelopmentHostBuild(
  dotnetExecutable,
  host,
  configuration
) {
  const timeoutMilliseconds = 300_000;
  const deadline = Date.now() + timeoutMilliseconds;
  const child = await spawnOwnedProcessTree({
    processTreeHostPath,
    command: dotnetExecutable,
    args: [
      'build',
      host.projectPath,
      '--configuration',
      configuration,
      '--disable-build-servers',
      '--nologo',
      '--verbosity',
      'minimal',
      '--property:TreatWarningsAsErrors=true'
    ],
    workingDirectory: repoRoot,
    environment: process.env,
    stdio: ['ignore', 'pipe', 'pipe'],
    onStdoutData: chunk => process.stdout.write(chunk),
    onStderrData: chunk => process.stderr.write(chunk),
    startupTimeoutMilliseconds: remainingMilliseconds(
      deadline,
      `${host.projectName} build startup`)
  });

  let completionTimeout;
  try {
    const result = await Promise.race([
      child.openlineopsClosePromise,
      new Promise((_, reject) => {
        completionTimeout = setTimeout(
          () => reject(new Error(
            `${host.projectName} development build exceeded its hard deadline.`)),
          remainingMilliseconds(
            deadline,
            `${host.projectName} build completion`));
      })
    ]);
    if (result.exitCode !== 0 || result.signalCode !== null) {
      throw new Error(
        `${host.projectName} development build closed with code ${
          result.exitCode ?? 'unknown'} and signal ${
          result.signalCode ?? 'none'}.`);
    }
  } catch (error) {
    const cleanupFailures = [];
    if (child.exitCode === null && child.signalCode === null) {
      try {
        const accepted = child.kill('SIGKILL');
        if (!accepted
            && child.exitCode === null
            && child.signalCode === null) {
          cleanupFailures.push(new Error(
            `${host.projectName} Process Tree Host rejected termination.`));
        }
      } catch (terminationError) {
        cleanupFailures.push(terminationError);
      }
      try {
        await waitForPromise(
          child.openlineopsClosePromise,
          15_000,
          `${host.projectName} Process Tree Host cleanup`);
      } catch (closeError) {
        cleanupFailures.push(closeError);
      }
    }
    if (cleanupFailures.length > 0) {
      throw new AggregateError(
        [error, ...cleanupFailures],
        `${host.projectName} development build failed and its process tree `
          + 'cleanup could not be confirmed.');
    }
    throw error;
  } finally {
    clearTimeout(completionTimeout);
  }
}

export async function waitForLauncherState(child, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (child.openlineopsLaunchError instanceof Error) {
      throw createLauncherDiagnosticError(
        'Dev launcher failed before publishing child identities.',
        child,
        child.openlineopsScenario,
        null,
        child.openlineopsLaunchError);
    }
    if (child.exitCode !== null || child.signalCode !== null) {
      throw createLauncherDiagnosticError(
        'Dev launcher exited before publishing child identities.',
        child,
        child.openlineopsScenario);
    }
    const match = child.output.join('')
      .match(/OPENLINEOPS_DEV_LAUNCHER_STATE (\{[^\r\n]+\})/u);
    if (match) {
      return JSON.parse(match[1]);
    }
    await delay(Math.max(1, Math.min(100, deadline - Date.now())));
  }
  throw createLauncherDiagnosticError(
    'Timed out waiting for dev launcher child identities.',
    child,
    child.openlineopsScenario);
}

export function waitForChildExit(child, timeoutMilliseconds) {
  if (child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve({
      code: child.exitCode,
      signal: child.signalCode
    });
  }
  if (child.openlineopsLaunchError instanceof Error) {
    return Promise.reject(child.openlineopsLaunchError);
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
    const onExit = (code, signal) =>
      complete(() => resolve({ code, signal }));
    const onError = error => complete(() => reject(error));
    const timeout = setTimeout(
      () => complete(() => reject(createLauncherDiagnosticError(
        'Timed out waiting for dev launcher shutdown.',
        child,
        child.openlineopsScenario))),
      timeoutMilliseconds);
    child.once('exit', onExit);
    child.once('error', onError);
    if (child.exitCode !== null || child.signalCode !== null) {
      complete(() => resolve({
        code: child.exitCode,
        signal: child.signalCode
      }));
    } else if (child.openlineopsLaunchError instanceof Error) {
      complete(() => reject(child.openlineopsLaunchError));
    }
  });
}

function remainingMilliseconds(deadline, description) {
  const remaining = deadline - Date.now();
  if (!Number.isSafeInteger(deadline) || remaining <= 0) {
    throw new Error(`${description} exceeded its hard deadline.`);
  }
  return remaining;
}

async function waitForPromise(promise, timeoutMilliseconds, description) {
  let timeout;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timeout = setTimeout(
          () => reject(new Error(
            `${description} did not complete within ${
              timeoutMilliseconds} ms.`)),
          timeoutMilliseconds);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
