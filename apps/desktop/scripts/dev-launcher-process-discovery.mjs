import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import {
  formatDiagnosticError,
  redactDiagnosticText
} from './smoke-diagnostics.mjs';
import { createWindowsPowerShellHost } from './windows-powershell-host.mjs';

const execFileAsync = promisify(execFile);
const maximumQueryTimeoutMilliseconds = 5_000;
const minimumQueryBudgetMilliseconds = 250;
const pollIntervalMilliseconds = 100;
const launcherLogCharacterLimit = 16 * 1024;

export async function waitForApiChildProcess({
  launcher,
  electronIdentity,
  scenario,
  timeoutMilliseconds,
  dependencies = {}
}) {
  const validatedIdentity = validateProcessIdentity(electronIdentity);
  const validatedScenario = requireNonEmptyString(scenario, 'scenario');
  const polling = createPollingDependencies(dependencies);
  const deadline = createDeadline(polling.now(), timeoutMilliseconds);
  let lastQueryTimeout = null;

  while (true) {
    assertLauncherRunning(launcher, validatedScenario);
    const queryTimeout = getNextQueryTimeout(deadline, polling);
    if (queryTimeout === null) {
      break;
    }

    let queryResult;
    try {
      queryResult = await polling.queryApiChildProcesses(
        validatedIdentity,
        queryTimeout);
    } catch (error) {
      assertLauncherRunning(launcher, validatedScenario);
      if (!isRetriableQueryTimeout(error)) {
        throw createLauncherDiagnosticError(
          `Failed to inspect the API child for ${validatedScenario}.`,
          launcher,
          validatedScenario,
          validatedIdentity,
          error);
      }
      lastQueryTimeout = error;
      if (!await waitForNextQuery(deadline, polling)) {
        break;
      }
      continue;
    }

    // The launcher may terminate while the asynchronous WMI query is in
    // flight. Its terminal state wins over a stale process snapshot.
    assertLauncherRunning(launcher, validatedScenario);
    if (polling.now() >= deadline) {
      break;
    }
    const parentIdentity = queryResult?.parentIdentity ?? null;
    if (parentIdentity === null) {
      throw createLauncherDiagnosticError(
        `Electron exited while waiting for its API child in ${validatedScenario}.`,
        launcher,
        validatedScenario,
        validatedIdentity);
    }
    if (!processIdentitiesEqual(parentIdentity, validatedIdentity)) {
      throw createLauncherDiagnosticError(
        `Electron process identity changed while waiting for its API child in ${
          validatedScenario}.`,
        launcher,
        validatedScenario,
        validatedIdentity);
    }

    const childIdentities = Array.isArray(queryResult?.childIdentities)
      ? queryResult.childIdentities.map(validateProcessIdentity)
      : [];
    if (childIdentities.length > 1) {
      throw createLauncherDiagnosticError(
        `Found multiple API children for Electron in ${validatedScenario}; `
          + 'the development runtime must have exactly one API owner.',
        launcher,
        validatedScenario,
        validatedIdentity);
    }
    if (childIdentities.length === 1) {
      return childIdentities[0];
    }
    if (!await waitForNextQuery(deadline, polling)) {
      break;
    }
  }

  assertLauncherRunning(launcher, validatedScenario);
  const timeoutCause = lastQueryTimeout === null
    ? null
    : new Error(
        `The final Windows process query timed out: ${
          formatDiagnosticError(lastQueryTimeout)}`,
        { cause: lastQueryTimeout });
  throw createLauncherDiagnosticError(
    `Timed out after ${timeoutMilliseconds} ms waiting for the API child of `
      + `Electron PID ${validatedIdentity.processId} in ${validatedScenario}.`,
    launcher,
    validatedScenario,
    validatedIdentity,
    timeoutCause);
}

export async function waitForProcessIdentity(
  processId,
  timeoutMilliseconds,
  dependencies = {}
) {
  if (!Number.isSafeInteger(processId) || processId <= 0) {
    throw new Error('A positive process ID is required.');
  }
  return pollProcessIdentity({
    timeoutMilliseconds,
    dependencies,
    query: queryTimeout => (
      dependencies.queryProcessIdentity ?? queryProcessIdentity)(
        processId,
        queryTimeout),
    accept: identity => identity === null
      ? null
      : validateProcessIdentity(identity),
    accepted: value => value !== null,
    timeoutMessage:
      `Timed out binding process PID ${processId} to its creation identity.`
  });
}

export async function waitForLauncherProcessIdentity({
  launcher,
  processId,
  scenario,
  timeoutMilliseconds,
  dependencies = {}
}) {
  const validatedScenario = requireNonEmptyString(scenario, 'scenario');
  try {
    return await waitForProcessIdentity(
      processId,
      timeoutMilliseconds,
      {
        ...dependencies,
        observe: () => assertLauncherRunning(launcher, validatedScenario)
      });
  } catch (error) {
    if (error?.openlineopsLauncherDiagnostic === true) {
      throw error;
    }
    throw createLauncherDiagnosticError(
      `Failed to bind process PID ${processId} during ${validatedScenario}.`,
      launcher,
      validatedScenario,
      null,
      error);
  }
}

export async function waitForProcessToDisappear(
  processIdentity,
  timeoutMilliseconds,
  dependencies = {}
) {
  const validatedIdentity = validateProcessIdentity(processIdentity);
  await pollProcessIdentity({
    timeoutMilliseconds,
    dependencies,
    query: queryTimeout => (
      dependencies.queryProcessIdentity ?? queryProcessIdentity)(
        validatedIdentity.processId,
        queryTimeout),
    accept: identity => identity === null
      || !processIdentitiesEqual(
        validateProcessIdentity(identity),
        validatedIdentity),
    accepted: value => value === true,
    timeoutMessage:
      `Process PID ${validatedIdentity.processId} with creation identity ${
        validatedIdentity.creationTicks} survived controlled dev launcher shutdown.`
  });
}

export function parseProcessIdentity(value) {
  const match = String(value).match(/^([1-9][0-9]*)\|([1-9][0-9]*)$/u);
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

export function parseApiChildQueryOutput(value) {
  const parentIdentities = [];
  const childIdentities = [];
  const lines = String(value)
    .split(/\r?\n/u)
    .map(line => line.trim())
    .filter(Boolean);
  if (lines.length === 1 && lines[0] === 'PARENT_NOT_FOUND') {
    return {
      parentIdentity: null,
      childIdentities: []
    };
  }
  if (lines.length === 0 || lines.includes('PARENT_NOT_FOUND')) {
    throw new Error(
      'Windows returned an incomplete API process discovery result.');
  }
  for (const line of lines) {
    const match = line.match(/^(PARENT|CHILD)\|(.+)$/u);
    const identity = match === null
      ? null
      : parseProcessIdentity(match[2]);
    if (match === null || identity === null) {
      throw new Error(
        `Windows returned an invalid API process discovery record: ${
          JSON.stringify(line)}.`);
    }
    (match[1] === 'PARENT' ? parentIdentities : childIdentities)
      .push(identity);
  }
  if (parentIdentities.length > 1) {
    throw new Error('Windows returned multiple identities for the Electron parent.');
  }
  return {
    parentIdentity: parentIdentities[0] ?? null,
    childIdentities
  };
}

export function parseProcessIdentityQueryOutput(value) {
  const normalized = String(value).trim();
  if (normalized === 'NOT_FOUND') {
    return null;
  }
  const identity = parseProcessIdentity(normalized);
  if (identity === null) {
    throw new Error(
      `Windows returned an invalid process identity result: ${
        JSON.stringify(normalized)}.`);
  }
  return identity;
}

export function createLauncherDiagnosticError(
  message,
  launcher,
  scenario,
  electronIdentity = null,
  cause = null
) {
  const completeMessage = `${message}\n${
    formatLauncherDiagnostics(launcher, scenario, electronIdentity)}`;
  const error = cause instanceof Error
    ? new Error(completeMessage, { cause })
    : new Error(completeMessage);
  error.openlineopsLauncherDiagnostic = true;
  return error;
}

export function formatLauncherDiagnostics(
  launcher,
  scenario,
  electronIdentity = null
) {
  const validatedScenario = requireNonEmptyString(scenario, 'scenario');
  const logText = Array.isArray(launcher?.output)
    ? launcher.output.join('')
    : '';
  const logTail = redactDiagnosticText(logText)
    .slice(-launcherLogCharacterLimit);
  const diagnostics = {
    scenario: validatedScenario,
    launcherPid: launcher?.pid ?? null,
    launcherExitCode: launcher?.exitCode ?? null,
    launcherSignalCode: launcher?.signalCode ?? null,
    electronIdentity
  };
  const diagnosticText = redactDiagnosticText(JSON.stringify(diagnostics));
  return `Launcher state: ${diagnosticText}`
    + `\nLauncher output tail:\n${logTail || '<empty>'}`;
}

async function pollProcessIdentity({
  timeoutMilliseconds,
  dependencies,
  query,
  accept,
  accepted,
  timeoutMessage
}) {
  const polling = createPollingDependencies(dependencies);
  const deadline = createDeadline(polling.now(), timeoutMilliseconds);
  let lastQueryTimeout = null;
  while (true) {
    polling.observe();
    const queryTimeout = getNextQueryTimeout(deadline, polling);
    if (queryTimeout === null) {
      break;
    }
    try {
      const queryResult = await query(queryTimeout);
      polling.observe();
      if (polling.now() >= deadline) {
        break;
      }
      const value = accept(queryResult);
      if (accepted(value)) {
        return value;
      }
    } catch (error) {
      polling.observe();
      if (!isRetriableQueryTimeout(error)) {
        throw new Error(
          `Windows process identity query failed: ${formatDiagnosticError(error)}`,
          { cause: error });
      }
      lastQueryTimeout = error;
    }
    if (!await waitForNextQuery(deadline, polling)) {
      break;
    }
  }
  polling.observe();
  throw new Error(
    lastQueryTimeout === null
      ? timeoutMessage
      : `${timeoutMessage} Final query: ${
        formatDiagnosticError(lastQueryTimeout)}`,
    lastQueryTimeout === null ? undefined : { cause: lastQueryTimeout });
}

function createPollingDependencies(dependencies) {
  const now = dependencies.now ?? Date.now;
  const wait = dependencies.wait ?? delay;
  const observe = dependencies.observe ?? (() => {});
  const queryApiChildProcessesDependency =
    dependencies.queryApiChildProcesses ?? queryApiChildProcesses;
  const minimumQueryBudget = dependencies.minimumQueryBudgetMilliseconds
    ?? minimumQueryBudgetMilliseconds;
  const maximumQueryTimeout = dependencies.maximumQueryTimeoutMilliseconds
    ?? maximumQueryTimeoutMilliseconds;
  const pollInterval = dependencies.pollIntervalMilliseconds
    ?? pollIntervalMilliseconds;
  for (const [name, value] of [
    ['minimum query budget', minimumQueryBudget],
    ['maximum query timeout', maximumQueryTimeout],
    ['poll interval', pollInterval]
  ]) {
    if (!Number.isSafeInteger(value) || value <= 0) {
      throw new Error(`The ${name} must be a positive safe integer.`);
    }
  }
  if (minimumQueryBudget > maximumQueryTimeout) {
    throw new Error(
      'The minimum query budget cannot exceed the maximum query timeout.');
  }
  if (typeof now !== 'function'
      || typeof wait !== 'function'
      || typeof observe !== 'function'
      || typeof queryApiChildProcessesDependency !== 'function') {
    throw new Error('Process discovery dependencies must be callable.');
  }
  return {
    now,
    wait,
    observe,
    queryApiChildProcesses: queryApiChildProcessesDependency,
    minimumQueryBudgetMilliseconds: minimumQueryBudget,
    maximumQueryTimeoutMilliseconds: maximumQueryTimeout,
    pollIntervalMilliseconds: pollInterval
  };
}

function createDeadline(startedAt, timeoutMilliseconds) {
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error('The process discovery timeout must be a positive safe integer.');
  }
  const deadline = startedAt + timeoutMilliseconds;
  if (!Number.isSafeInteger(startedAt) || !Number.isSafeInteger(deadline)) {
    throw new Error('The process discovery deadline is outside the safe integer range.');
  }
  return deadline;
}

function getNextQueryTimeout(deadline, polling) {
  const remaining = deadline - polling.now();
  if (remaining < polling.minimumQueryBudgetMilliseconds) {
    return null;
  }
  return Math.min(polling.maximumQueryTimeoutMilliseconds, remaining);
}

async function waitForNextQuery(deadline, polling) {
  const remaining = deadline - polling.now();
  if (remaining <= polling.minimumQueryBudgetMilliseconds) {
    return false;
  }
  const delayMilliseconds = Math.min(
    polling.pollIntervalMilliseconds,
    remaining - polling.minimumQueryBudgetMilliseconds);
  if (delayMilliseconds <= 0) {
    return false;
  }
  await polling.wait(delayMilliseconds);
  return true;
}

function isRetriableQueryTimeout(error) {
  return error instanceof Error
    && (error.code === 'ETIMEDOUT'
      || (error.killed === true && error.signal === 'SIGKILL'));
}

function assertLauncherRunning(launcher, scenario) {
  if (launcher?.openlineopsLaunchError instanceof Error) {
    throw createLauncherDiagnosticError(
      `The development launcher failed during ${scenario}.`,
      launcher,
      scenario,
      null,
      launcher.openlineopsLaunchError);
  }
  if (launcher?.exitCode !== null || launcher?.signalCode !== null) {
    throw createLauncherDiagnosticError(
      `The development launcher exited while discovering the API in ${scenario}.`,
      launcher,
      scenario);
  }
}

function validateProcessIdentity(identity) {
  if (!Number.isSafeInteger(identity?.processId) || identity.processId <= 0
      || typeof identity?.creationTicks !== 'string'
      || !/^[1-9][0-9]*$/u.test(identity.creationTicks)) {
    throw new Error(
      `A canonical process identity is required: ${JSON.stringify(identity)}.`);
  }
  return {
    processId: identity.processId,
    creationTicks: identity.creationTicks
  };
}

function processIdentitiesEqual(left, right) {
  return left.processId === right.processId
    && left.creationTicks === right.creationTicks;
}

function requireNonEmptyString(value, description) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`A non-empty ${description} is required.`);
  }
  return value.trim();
}

async function queryApiChildProcesses(electronIdentity, timeoutMilliseconds) {
  const script = [
    "$ErrorActionPreference = 'Stop'",
    `$parent = Get-CimInstance Win32_Process -Filter "ProcessId = ${
      electronIdentity.processId}" -ErrorAction Stop`,
    'if ($null -eq $parent) {',
    "  Write-Output 'PARENT_NOT_FOUND'",
    '  exit 0',
    '}',
    '$ticks = $parent.CreationDate.ToUniversalTime().Ticks',
    'Write-Output "PARENT|$($parent.ProcessId)|$ticks"',
    `$candidates = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = ${
      electronIdentity.processId}" -ErrorAction Stop `
      + "| Where-Object { $_.Name -eq 'dotnet.exe' "
      + "-and $_.CommandLine -like '*OpenLineOps.Api.dll*' })",
    'foreach ($candidate in $candidates) {',
    '  $ticks = $candidate.CreationDate.ToUniversalTime().Ticks',
    '  Write-Output "CHILD|$($candidate.ProcessId)|$ticks"',
    '}'
  ].join('\r\n');
  const powerShellHost = createWindowsPowerShellHost();
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    ['-NoProfile', '-NonInteractive', '-Command', script],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true,
      timeout: timeoutMilliseconds,
      killSignal: 'SIGKILL',
      maxBuffer: 64 * 1024
    });
  return parseApiChildQueryOutput(stdout);
}

async function queryProcessIdentity(processId, timeoutMilliseconds) {
  const script = [
    "$ErrorActionPreference = 'Stop'",
    `$process = Get-CimInstance Win32_Process -Filter "ProcessId = ${
      processId}" -ErrorAction Stop`,
    'if ($null -eq $process) {',
    "  Write-Output 'NOT_FOUND'",
    '  exit 0',
    '}',
    '$ticks = $process.CreationDate.ToUniversalTime().Ticks',
    'Write-Output "$($process.ProcessId)|$ticks"'
  ].join('\r\n');
  const powerShellHost = createWindowsPowerShellHost();
  const { stdout } = await execFileAsync(
    powerShellHost.executablePath,
    ['-NoProfile', '-NonInteractive', '-Command', script],
    {
      encoding: 'utf8',
      env: powerShellHost.environment,
      windowsHide: true,
      timeout: timeoutMilliseconds,
      killSignal: 'SIGKILL',
      maxBuffer: 64 * 1024
    });
  return parseProcessIdentityQueryOutput(stdout);
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
