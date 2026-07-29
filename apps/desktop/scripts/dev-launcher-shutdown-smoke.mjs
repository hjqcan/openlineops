import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  createWindowsPowerShellHost
} from './windows-powershell-host.mjs';

const execFileAsync = promisify(execFile);
const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const launcherPath = path.join(desktopRoot, 'scripts', 'dev-launcher.mjs');
const physicalTempRoot = await fs.realpath(os.tmpdir());

await runScenario('controlled stop', async launcher => {
  launcher.stdin.end('stop\n');
  const exit = await waitForChildExit(launcher, 20_000);
  assert.equal(exit.code, 0, launcher.output.join(''));
  assert.equal(exit.signal, null, launcher.output.join(''));
});
await runScenario('owner termination', async launcher => {
  const requested = launcher.kill('SIGKILL');
  assert(requested, 'Dev launcher root did not accept strong termination.');
  const exit = await waitForChildExit(launcher, 20_000);
  assert(
    exit.signal !== null || exit.code !== 0,
    `Strongly terminated dev launcher reported a clean exit.\n${
      launcher.output.join('')}`);
});

console.log(
  'Dev launcher shutdown smoke passed for controlled stop and owner death; '
  + 'Vite, Electron, and API trees were fully terminated.');

async function runScenario(description, stopLauncher) {
  const userDataDirectory = await fs.mkdtemp(
    path.join(
      physicalTempRoot,
      `openlineops-dev-launcher-${description.replaceAll(' ', '-')}-`));
  const launcher = startLauncher(userDataDirectory);
  try {
    const state = await waitForLauncherState(launcher, 20_000);
    assert(Number.isSafeInteger(state.electronPid) && state.electronPid > 0);
    assert(Number.isSafeInteger(state.vitePid) && state.vitePid > 0);
    const [electronProcessIdentity, viteProcessIdentity] = await Promise.all([
      waitForProcessIdentity(state.electronPid, 10_000),
      waitForProcessIdentity(state.vitePid, 10_000)
    ]);
    const apiProcessIdentity = await waitForApiChildProcess(
      state.electronPid,
      30_000);

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

function startLauncher(userDataDirectory) {
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
  launcher.openlineopsLaunchError = null;
  launcher.stdout.on('data', chunk => output.push(chunk.toString()));
  launcher.stderr.on('data', chunk => output.push(chunk.toString()));
  launcher.once('error', error => {
    launcher.openlineopsLaunchError = error;
    output.push(`Dev launcher failed to start: ${error.message}`);
  });
  return launcher;
}

async function waitForLauncherState(child, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    const match = child.output.join('')
      .match(/OPENLINEOPS_DEV_LAUNCHER_STATE (\{[^\r\n]+\})/u);
    if (match) {
      return JSON.parse(match[1]);
    }
    if (child.exitCode !== null || child.signalCode !== null) {
      throw new Error(
        `Dev launcher exited before publishing child identities.\n${
          child.output.join('')}`);
    }
    if (child.openlineopsLaunchError instanceof Error) {
      throw new Error(
        `Dev launcher failed before publishing child identities.\n${
          child.output.join('')}`,
        { cause: child.openlineopsLaunchError });
    }
    await delay(Math.max(1, Math.min(100, deadline - Date.now())));
  }
  throw new Error(
    `Timed out waiting for dev launcher child identities.\n${
      child.output.join('')}`);
}

async function waitForApiChildProcess(electronPid, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    const script = [
      `$candidate = Get-CimInstance Win32_Process -Filter "ParentProcessId = ${electronPid}"`,
      "| Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*OpenLineOps.Api.dll*' }",
       '| Select-Object -First 1;',
       'if ($candidate) {',
       '  $ticks = $candidate.CreationDate.ToUniversalTime().Ticks;',
       '  Write-Output "$($candidate.ProcessId)|$ticks"',
       '}'
    ].join(' ');
    const powerShellHost = createWindowsPowerShellHost();
    const { stdout } = await execFileAsync(
      powerShellHost.executablePath,
      ['-NoProfile', '-NonInteractive', '-Command', script],
      {
        encoding: 'utf8',
        env: powerShellHost.environment,
        windowsHide: true,
        timeout: Math.max(
          1,
          Math.min(5_000, deadline - Date.now())),
        killSignal: 'SIGKILL',
        maxBuffer: 64 * 1024
      });
    const value = stdout.trim();
    const identity = parseProcessIdentity(value);
    if (identity !== null) {
      return identity;
    }
    await delay(Math.max(1, Math.min(100, deadline - Date.now())));
  }
  throw new Error(
    `Timed out waiting for the API child of Electron PID ${electronPid}.`);
}

function waitForChildExit(child, timeoutMilliseconds) {
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
      () => complete(() => reject(new Error(
        `Timed out waiting for dev launcher shutdown.\n${
          child.output.join('')}`))),
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

async function waitForProcessIdentity(processId, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    const identity = await queryProcessIdentity(processId, deadline);
    if (identity !== null) {
      return identity;
    }
    await delay(Math.max(1, Math.min(100, deadline - Date.now())));
  }
  throw new Error(
    `Timed out binding process PID ${processId} to its creation identity.`);
}

async function waitForProcessToDisappear(processIdentity, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    const current = await queryProcessIdentity(processIdentity.processId, deadline);
    if (current === null
        || current.creationTicks !== processIdentity.creationTicks) {
      return;
    }
    await delay(Math.max(1, Math.min(100, deadline - Date.now())));
  }
  throw new Error(
    `Process PID ${processIdentity.processId} with creation identity ${
      processIdentity.creationTicks} survived controlled dev launcher shutdown.`);
}

async function queryProcessIdentity(processId, deadline) {
  const script =
    `$process = Get-CimInstance Win32_Process -Filter "ProcessId = ${processId}"; `
    + 'if ($process) { '
    + '$ticks = $process.CreationDate.ToUniversalTime().Ticks; '
    + 'Write-Output "$($process.ProcessId)|$ticks" }';
  const powerShellHost = createWindowsPowerShellHost();
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    ['-NoProfile', '-NonInteractive', '-Command', script],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true,
      timeout: Math.max(1, Math.min(5_000, deadline - Date.now())),
      killSignal: 'SIGKILL',
      maxBuffer: 64 * 1024
    });
  return parseProcessIdentity(stdout.trim());
}

function parseProcessIdentity(value) {
  const match = value.match(/^([1-9][0-9]*)\|([1-9][0-9]*)$/u);
  if (!match) {
    return null;
  }
  const processId = Number(match[1]);
  if (!Number.isSafeInteger(processId) || processId <= 0) {
    throw new Error(`Invalid process identity PID: ${match[1]}.`);
  }
  return {
    processId,
    creationTicks: match[2]
  };
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
