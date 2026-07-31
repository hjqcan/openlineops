import { execFile } from 'node:child_process';
import process from 'node:process';
import { promisify } from 'node:util';
import { createWindowsPowerShellHost } from './windows-powershell-host.mjs';

const execFileAsync = promisify(execFile);
const helperTimeoutMilliseconds = 10_000;
const helperOutputLimit = 64 * 1024;

export async function getCurrentWindowsProcessStartedAtUnixMilliseconds(
  timeoutMilliseconds = helperTimeoutMilliseconds
) {
  if (process.platform !== 'win32') {
    throw new Error('Exact Windows process creation identity requires Windows.');
  }
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error('Windows process identity timeout must be a positive safe integer.');
  }

  const powerShellHost = createWindowsPowerShellHost();
  const command = [
    `$owner = Get-Process -Id ${process.pid} -ErrorAction Stop`,
    '$startedAt = [DateTimeOffset]::new($owner.StartTime.ToUniversalTime())',
    'Write-Output $startedAt.ToUnixTimeMilliseconds()'
  ].join('; ');
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    ['-NoProfile', '-NonInteractive', '-Command', command],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true,
      timeout: timeoutMilliseconds,
      killSignal: 'SIGKILL',
      maxBuffer: helperOutputLimit
    });
  const value = stdout.trim();
  if (!/^[1-9][0-9]*$/u.test(value)) {
    throw new Error(
      `Windows returned a non-canonical process creation identity: ${JSON.stringify(value)}.`);
  }
  const startedAtUnixMilliseconds = Number(value);
  if (!Number.isSafeInteger(startedAtUnixMilliseconds)) {
    throw new Error('Windows process creation identity exceeds the JavaScript safe integer range.');
  }
  return startedAtUnixMilliseconds;
}

export async function isWindowsProcessIdentityRunning(identity) {
  const validated = validateProcessIdentity(identity);
  const result = await runIdentityPowerShell([
    `$target = Get-Process -Id ${validated.processId} -ErrorAction SilentlyContinue`,
    "if ($null -eq $target) { [Console]::Out.Write('not-running'); exit 0 }",
    'try {',
    '  $actual = [DateTimeOffset]::new($target.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()',
    `  if ($actual -ne ${validated.startedAtUnixMilliseconds}) { [Console]::Out.Write('identity-mismatch'); exit 0 }`,
    "  if ($target.HasExited) { [Console]::Out.Write('not-running') } else { [Console]::Out.Write('running') }",
    '} finally { $target.Dispose() }'
  ]);
  if (!['running', 'not-running', 'identity-mismatch'].includes(result)) {
    throw new Error(
      `Windows process identity inspection returned an invalid result: ${JSON.stringify(result)}.`);
  }
  return result === 'running';
}

export async function terminateWindowsProcessIdentity(
  identity,
  timeoutMilliseconds = 5_000
) {
  const validated = validateProcessIdentity(identity);
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error('Windows process termination timeout must be a positive safe integer.');
  }
  await runIdentityPowerShell([
    `$target = Get-Process -Id ${validated.processId} -ErrorAction Stop`,
    'try {',
    '  $actual = [DateTimeOffset]::new($target.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()',
    `  if ($actual -ne ${validated.startedAtUnixMilliseconds}) { throw 'Process creation identity changed; refusing termination.' }`,
    '  $target.Kill()',
    `  if (-not $target.WaitForExit(${timeoutMilliseconds})) { throw 'Exact process handle did not exit before the deadline.' }`,
    '} finally { $target.Dispose() }'
  ], timeoutMilliseconds + 5_000);
}

export async function requestWindowsMainWindowCloseForIdentity(
  identity,
  timeoutMilliseconds = 5_000
) {
  const validated = validateProcessIdentity(identity);
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error('Windows native-close timeout must be a positive safe integer.');
  }
  const output = await runIdentityPowerShell([
    `$target = Get-Process -Id ${validated.processId} -ErrorAction Stop`,
    'try {',
    '  $actual = [DateTimeOffset]::new($target.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()',
    `  if ($actual -ne ${validated.startedAtUnixMilliseconds}) { throw 'Process creation identity changed; refusing native close.' }`,
    '  $target.Refresh()',
    '  $windowHandle = [Int64]$target.MainWindowHandle',
    '  $windowTitle = $target.MainWindowTitle',
    '  $accepted = $target.CloseMainWindow()',
    '  [Console]::Out.Write((ConvertTo-Json @{ accepted = $accepted; windowHandle = $windowHandle; windowTitle = $windowTitle } -Compress))',
    "  if (-not $accepted) { throw 'Exact process handle did not accept a native close request.' }",
    '} finally { $target.Dispose() }'
  ], timeoutMilliseconds);
  const result = JSON.parse(output);
  if (result.accepted !== true
      || !Number.isSafeInteger(result.windowHandle)
      || result.windowHandle <= 0
      || typeof result.windowTitle !== 'string') {
    throw new Error(
      `Native close returned an invalid window identity: ${output}`);
  }
  return result;
}

function validateProcessIdentity(identity) {
  const processId = identity?.processId;
  const startedAtUnixMilliseconds = identity?.startedAtUnixMilliseconds;
  if (!Number.isSafeInteger(processId) || processId <= 0
      || !Number.isSafeInteger(startedAtUnixMilliseconds)
      || startedAtUnixMilliseconds <= 0) {
    throw new Error(
      `Windows process identity must contain a positive PID and creation time: ${
        JSON.stringify(identity)}.`);
  }
  return { processId, startedAtUnixMilliseconds };
}

async function runIdentityPowerShell(commandParts, timeout = helperTimeoutMilliseconds) {
  if (process.platform !== 'win32') {
    throw new Error('Exact Windows process identity operations require Windows.');
  }
  const powerShellHost = createWindowsPowerShellHost();
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    [
      '-NoLogo',
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      commandParts.join('; ')
    ],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true,
      timeout,
      killSignal: 'SIGKILL',
      maxBuffer: helperOutputLimit
    });
  return stdout.trim();
}
