import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import {
  getCurrentWindowsProcessStartedAtUnixMilliseconds
} from './windows-process-identity.mjs';

export async function spawnOwnedProcessTree({
  processTreeHostPath,
  command,
  args = [],
  workingDirectory,
  environment = process.env,
  stdio = ['ignore', 'pipe', 'pipe'],
  onStdoutData = null,
  onStderrData = null,
  startupTimeoutMilliseconds = 10_000
}) {
  assertPositiveTimeout(startupTimeoutMilliseconds);
  validateDataObserver(stdio, onStdoutData, 1, 'stdout');
  validateDataObserver(stdio, onStderrData, 2, 'stderr');
  const startupDeadline = Date.now() + startupTimeoutMilliseconds;
  const hostPath = await beforeDeadline(
    requireCanonicalLocalFile(processTreeHostPath, 'Process Tree Host'),
    startupDeadline,
    'Process Tree Host validation');
  const executablePath = await beforeDeadline(
    resolveExecutablePath(command),
    startupDeadline,
    'owned executable validation');
  const canonicalWorkingDirectory = requireCanonicalLocalPath(
    workingDirectory,
    'Owned process working directory');
  const workingDirectoryStat = await beforeDeadline(
    fs.stat(canonicalWorkingDirectory).catch(() => null),
    startupDeadline,
    'owned working-directory validation');
  if (!workingDirectoryStat?.isDirectory()) {
    throw new Error(
      `Owned process working directory does not exist: ${canonicalWorkingDirectory}`);
  }
  const ownerStartedAtUnixMilliseconds =
    await getCurrentWindowsProcessStartedAtUnixMilliseconds(
      remainingMilliseconds(startupDeadline, 'owner identity inspection'));
  const launchTimeoutMilliseconds = remainingMilliseconds(
    startupDeadline,
    'Process Tree Host launch');
  let child;
  try {
    child = spawn(
      hostPath,
      [
        String(process.pid),
        String(ownerStartedAtUnixMilliseconds),
        executablePath,
        canonicalWorkingDirectory,
        ...args
      ],
      {
        cwd: path.dirname(hostPath),
        env: environment,
        windowsHide: true,
        stdio
      });
  } catch (error) {
    throw new Error(
      `Process Tree Host failed to start: ${
        error instanceof Error ? error.message : String(error)}`,
      { cause: error });
  }
  child.openlineopsOwnsKillOnCloseJob = true;
  child.openlineopsLastProcessError = null;
  child.on('error', error => {
    child.openlineopsLastProcessError = error;
  });
  child.openlineopsClosePromise = observeProcessClose(child);
  attachDataObserver(child.stdout, onStdoutData, 'stdout');
  attachDataObserver(child.stderr, onStderrData, 'stderr');
  let launchTimedOut = false;
  let launchTimeout;
  const launchPromise = new Promise((resolve, reject) => {
    const onSpawn = () => {
      child.off('error', onError);
      resolve();
    };
    const onError = error => {
      child.off('spawn', onSpawn);
      reject(new Error(
        `Process Tree Host failed to start: ${error.message}`,
        { cause: error }));
    };
    child.once('spawn', onSpawn);
    child.once('error', onError);
  });
  try {
    await Promise.race([
      launchPromise,
      new Promise((_, reject) => {
        launchTimeout = setTimeout(() => {
          launchTimedOut = true;
          reject(new Error(
            'Process Tree Host did not start before its hard deadline.'));
        }, launchTimeoutMilliseconds);
      })
    ]);
  } catch (error) {
    if (!launchTimedOut) {
      throw error;
    }
    const cleanupFailures = [];
    child.removeAllListeners('spawn');
    try {
      if (child.exitCode === null && child.signalCode === null
          && !child.kill('SIGKILL')) {
        cleanupFailures.push(new Error(
          'Process Tree Host rejected exact-handle termination after startup timeout.'));
      }
    } catch (terminationError) {
      cleanupFailures.push(terminationError);
    }
    try {
      await beforeDeadline(
        child.openlineopsClosePromise,
        Date.now() + 15_000,
        'timed-out Process Tree Host close');
    } catch (closeError) {
      cleanupFailures.push(closeError);
    }
    if (cleanupFailures.length > 0) {
      const aggregate = new AggregateError(
        [error, ...cleanupFailures],
        `Process Tree Host startup timed out and exact-handle cleanup was not confirmed for PID ${
          child.pid ?? 'unknown'}.`);
      aggregate.openlineopsOwnedProcess = child;
      throw aggregate;
    }
    throw error;
  } finally {
    clearTimeout(launchTimeout);
  }
  return child;
}

export function observeProcessClose(child) {
  if (child === null
      || typeof child !== 'object'
      || typeof child.once !== 'function') {
    throw new TypeError('Owned process close observation requires an event-emitting child.');
  }
  return new Promise(resolve => {
    child.once('close', (exitCode, signalCode) => {
      resolve({ exitCode, signalCode });
    });
  });
}

export async function resolveExecutablePath(command) {
  if (typeof command !== 'string' || command.trim() !== command
      || command.length === 0) {
    throw new Error('Owned process executable must be one non-empty canonical token.');
  }
  return requireCanonicalLocalFile(command, 'Owned process executable');
}

export async function resolveDotnetExecutablePath(environment = process.env) {
  const configuredHost = environment?.DOTNET_HOST_PATH?.trim();
  if (configuredHost) {
    return requireCanonicalLocalFile(
      path.resolve(configuredHost),
      'DOTNET_HOST_PATH executable');
  }
  const configuredRoot = environment?.DOTNET_ROOT?.trim();
  if (configuredRoot) {
    return requireCanonicalLocalFile(
      path.resolve(configuredRoot, 'dotnet.exe'),
      'DOTNET_ROOT executable');
  }
  const programFiles = environment?.ProgramFiles?.trim();
  if (!programFiles) {
    throw new Error(
      'A local DOTNET_HOST_PATH, DOTNET_ROOT, or ProgramFiles directory is required.');
  }
  return requireCanonicalLocalFile(
    path.resolve(programFiles, 'dotnet', 'dotnet.exe'),
    'Program Files dotnet executable');
}

async function requireCanonicalLocalFile(filePath, description) {
  const canonicalPath = requireCanonicalLocalPath(filePath, description);
  const stat = await fs.stat(filePath).catch(() => null);
  if (!stat?.isFile()) {
    throw new Error(`${description} does not exist: ${canonicalPath}`);
  }
  return canonicalPath;
}

function attachDataObserver(stream, observer, streamName) {
  if (observer === null) {
    return;
  }
  if (typeof observer !== 'function') {
    throw new TypeError(`Owned process ${streamName} observer must be callable.`);
  }
  if (stream === null || typeof stream.on !== 'function') {
    throw new Error(
      `Owned process ${streamName} observer requires a piped ${streamName} stream.`);
  }
  stream.on('data', observer);
}

function validateDataObserver(stdio, observer, streamIndex, streamName) {
  if (observer === null) {
    return;
  }
  if (typeof observer !== 'function') {
    throw new TypeError(`Owned process ${streamName} observer must be callable.`);
  }
  if (!Array.isArray(stdio) || stdio[streamIndex] !== 'pipe') {
    throw new Error(
      `Owned process ${streamName} observer requires piped ${streamName} stdio.`);
  }
}

function requireCanonicalLocalPath(filePath, description) {
  if (typeof filePath !== 'string'
      || !path.isAbsolute(filePath)
      || path.resolve(filePath) !== filePath
      || !/^[A-Za-z]:\\/u.test(filePath)) {
    throw new Error(
      `${description} path must be canonical, absolute, and local to one Windows drive.`);
  }
  return filePath;
}

function assertPositiveTimeout(timeoutMilliseconds) {
  if (!Number.isSafeInteger(timeoutMilliseconds)
      || timeoutMilliseconds <= 0) {
    throw new Error(
      'Owned process startup timeout must be a positive safe integer.');
  }
}

function remainingMilliseconds(deadline, description) {
  const remaining = deadline - Date.now();
  if (!Number.isSafeInteger(deadline) || remaining <= 0) {
    throw new Error(`${description} exceeded the owned process hard deadline.`);
  }
  return remaining;
}

async function beforeDeadline(operation, deadline, description) {
  const timeoutMilliseconds = remainingMilliseconds(deadline, description);
  let timeout;
  try {
    return await Promise.race([
      operation,
      new Promise((_, reject) => {
        timeout = setTimeout(
          () => reject(new Error(
            `${description} did not complete before the owned process hard deadline.`)),
          timeoutMilliseconds);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}
