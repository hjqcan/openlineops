import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  createWindowsPowerShellHost,
  windowsSystemExecutablePath
} from './windows-powershell-host.mjs';

const execFileAsync = promisify(execFile);
const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const launcherPath = path.join(desktopRoot, 'scripts', 'dev-launcher.mjs');
const userDataDirectory = await fs.mkdtemp(
  path.join(os.tmpdir(), 'openlineops-dev-launcher-smoke-'));
const output = [];
let launcher;

try {
  launcher = spawn(process.execPath, [launcherPath], {
    cwd: desktopRoot,
    env: {
      ...process.env,
      OPENLINEOPS_DEV_LAUNCHER_SMOKE: '1',
      OPENLINEOPS_DEV_USER_DATA_DIRECTORY: userDataDirectory
    },
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true
  });
  launcher.stdout.on('data', chunk => output.push(chunk.toString()));
  launcher.stderr.on('data', chunk => output.push(chunk.toString()));

  const state = await waitForLauncherState(launcher, output, 15000);
  assert(Number.isSafeInteger(state.electronPid) && state.electronPid > 0);
  assert(Number.isSafeInteger(state.vitePid) && state.vitePid > 0);
  const apiPid = await waitForApiChildProcess(state.electronPid, 30000);

  launcher.stdin.write('stop\n');
  const exit = await waitForChildExit(launcher, 20000);
  assert.equal(exit.code, 0, output.join(''));
  await waitForProcessToDisappear(apiPid, 10000);
  await waitForProcessToDisappear(state.electronPid, 10000);
  await waitForProcessToDisappear(state.vitePid, 10000);

  console.log(`Dev launcher shutdown smoke passed; API PID ${apiPid} was terminated with its Electron tree.`);
} finally {
  if (launcher?.pid && launcher.exitCode === null) {
    await terminateKnownProcessTree(launcher.pid);
  }
  await fs.rm(userDataDirectory, {
    force: true,
    recursive: true,
    maxRetries: 10,
    retryDelay: 200
  });
}

async function waitForLauncherState(child, chunks, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const match = chunks.join('').match(/OPENLINEOPS_DEV_LAUNCHER_STATE (\{[^\r\n]+\})/u);
    if (match) {
      return JSON.parse(match[1]);
    }
    if (child.exitCode !== null) {
      throw new Error(`Dev launcher exited before publishing child identities.\n${chunks.join('')}`);
    }
    await delay(100);
  }
  throw new Error(`Timed out waiting for dev launcher child identities.\n${chunks.join('')}`);
}

async function waitForApiChildProcess(electronPid, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const script = [
      `$candidate = Get-CimInstance Win32_Process -Filter "ParentProcessId = ${electronPid}"`,
      "| Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*OpenLineOps.Api.dll*' }",
      '| Select-Object -First 1 -ExpandProperty ProcessId;',
      'if ($candidate) { Write-Output $candidate }'
    ].join(' ');
    const powerShellHost = createWindowsPowerShellHost();
    const { stdout } = await execFileAsync(
      powerShellHost.executablePath,
      ['-NoProfile', '-NonInteractive', '-Command', script],
      {
        encoding: 'utf8',
        env: powerShellHost.environment,
        windowsHide: true
      });
    const value = stdout.trim();
    if (/^[1-9][0-9]*$/u.test(value)) {
      return Number(value);
    }
    await delay(100);
  }
  throw new Error(`Timed out waiting for the API child of Electron PID ${electronPid}.`);
}

async function waitForChildExit(child, timeoutMs) {
  if (child.exitCode !== null) {
    return { code: child.exitCode, signal: child.signalCode };
  }
  return Promise.race([
    new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal }))),
    delay(timeoutMs).then(() => {
      throw new Error(`Timed out waiting for dev launcher shutdown.\n${output.join('')}`);
    })
  ]);
}

async function waitForProcessToDisappear(pid, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!await processExists(pid)) {
      return;
    }
    await delay(100);
  }
  throw new Error(`Process PID ${pid} survived controlled dev launcher shutdown.`);
}

async function processExists(pid) {
  const script = `$process = Get-CimInstance Win32_Process -Filter "ProcessId = ${pid}"; if ($process) { Write-Output present }`;
  const powerShellHost = createWindowsPowerShellHost();
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    ['-NoProfile', '-NonInteractive', '-Command', script],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true
    });
  return stdout.trim() === 'present';
}

async function terminateKnownProcessTree(pid) {
  if (process.platform !== 'win32') {
    process.kill(pid, 'SIGTERM');
    return;
  }
  await execFileAsync(
    windowsSystemExecutablePath('taskkill.exe'),
    ['/pid', String(pid), '/t', '/f'],
    { encoding: 'utf8', windowsHide: true }).catch(() => undefined);
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
