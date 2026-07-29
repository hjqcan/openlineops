import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { CdpClient } from './smoke-cdp-client.mjs';
import { observeProcessClose } from './owned-process-tree.mjs';
import {
  getCurrentWindowsProcessStartedAtUnixMilliseconds
} from './windows-process-identity.mjs';

export async function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        reject(new Error('Unable to allocate a loopback port.'));
        return;
      }
      server.close(() => resolve(address.port));
    });
  });
}

export function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

export function spawnCaptured(
  command,
  args,
  options,
  label,
  lines,
  onError = () => {}
) {
  const child = spawn(command, args, {
    ...options,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });
  const append = chunk => {
    for (const rawLine of chunk.toString().split(/\r?\n/u)) {
      const line = rawLine.trim();
      if (line) lines.push(`[${label}] ${line}`);
    }
    if (lines.length > 500) lines.splice(0, lines.length - 500);
  };
  child.stdout.on('data', append);
  child.stderr.on('data', append);
  child.openlineopsClosePromise = observeProcessClose(child);
  child.once('error', error => {
    append(`failed to start: ${error.message}`);
    onError(error);
  });
  child.on('exit', code => append(`exited with code ${code ?? 'unknown'}`));
  return child;
}

export async function stopProcess(child, timeoutMilliseconds = 8_000) {
  if (!child) return;
  if (hasChildExited(child)) {
    if (child.openlineopsOwnsKillOnCloseJob === true) {
      return;
    }
    throw new Error(
      `Process tree root ${child.pid ?? 'unknown'} exited before descendant cleanup could be confirmed.`);
  }
  assertPositiveTimeout(timeoutMilliseconds, 'process termination timeout');

  if (process.platform === 'win32'
      && child.openlineopsOwnsKillOnCloseJob !== true) {
    throw new Error(
      'Windows process cleanup requires an owning kill-on-close Job Host.');
  }

  const accepted = child.kill('SIGKILL');
  if (!accepted && !hasChildExited(child)) {
    throw new Error('Owning process handle rejected termination.');
  }
  if (!await waitForChildExit(child, timeoutMilliseconds)) {
    throw new Error(
      `Owning process ${child.pid ?? 'unknown'} remained alive after exact-handle termination.`);
  }
}

export class ElectronCdpHarness {
  constructor({
    executablePath,
    workingDirectory,
    userDataDirectory,
    environment,
    logs,
    processTreeHostPath,
    processControl = {}
  }) {
    this.executablePath = executablePath;
    this.workingDirectory = workingDirectory;
    this.userDataDirectory = userDataDirectory;
    this.environment = environment;
    this.logs = logs;
    if (typeof processTreeHostPath !== 'string'
        || !path.isAbsolute(processTreeHostPath)
        || path.resolve(processTreeHostPath) !== processTreeHostPath) {
      throw new Error(
        'Electron harness Process Tree Host path must be one canonical absolute path.');
    }
    this.processTreeHostPath = processTreeHostPath;
    this.process = null;
    this.cdp = null;
    this.cdpPort = null;
    this.closePromise = null;
    this.closed = false;
    this.processLaunchError = null;
    this.stopOwnedProcess = processControl.stopProcess ?? stopProcess;
    if (typeof this.stopOwnedProcess !== 'function') {
      throw new TypeError(
        'Electron harness process control must provide callable Host termination.');
    }
  }

  async start() {
    this.cdpPort = await getFreePort();
    const ownerStartedAtUnixMilliseconds =
      await getCurrentWindowsProcessStartedAtUnixMilliseconds();
    const launchArguments = [
      `--remote-debugging-port=${this.cdpPort}`,
      '--remote-debugging-address=127.0.0.1',
      '--disable-gpu'
    ];
    if (this.userDataDirectory !== null && this.userDataDirectory !== undefined) {
      launchArguments.push(`--user-data-dir=${this.userDataDirectory}`);
    }
    this.process = spawnCaptured(
      this.processTreeHostPath,
      [
        String(process.pid),
        String(ownerStartedAtUnixMilliseconds),
        this.executablePath,
        this.workingDirectory,
        ...launchArguments
      ],
      {
        cwd: path.dirname(this.processTreeHostPath),
        env: {
          ...process.env,
          ...this.environment
        }
      },
      'OpenLineOps',
      this.logs,
      error => {
        this.processLaunchError = error;
      });
    this.process.openlineopsOwnsKillOnCloseJob = true;

    let target;
    try {
      target = await waitForTarget(
        this.cdpPort,
        90_000,
        () => ({
          exitCode: this.process?.exitCode ?? null,
          signalCode: this.process?.signalCode ?? null,
          launchError: this.processLaunchError?.message ?? null,
          recentProcessLogs: this.logs.slice(-40)
        }));
    } catch (error) {
      if (this.processLaunchError !== null && this.process?.pid === undefined) {
        this.process = null;
      }
      throw error;
    }
    this.cdp = await CdpClient.connect(target.webSocketDebuggerUrl);
    await this.cdp.send('Runtime.enable');
    await this.cdp.send('Page.enable');
    await this.waitFor(
      'Boolean(document.querySelector("[data-testid=\\"automation-ide-shell\\"]")) && Boolean(window.openlineopsDesktop)',
      45_000,
      'the packaged Studio shell');
  }

  async close() {
    if (this.closed) return;
    if (this.closePromise) {
      await this.closePromise;
      return;
    }

    const closeAttempt = this.closeCore();
    this.closePromise = closeAttempt;
    try {
      await closeAttempt;
      this.closed = true;
    } finally {
      this.closePromise = null;
    }
  }

  async closeCore() {
    if (this.cdp) {
      try {
        await this.cdp.send('Browser.close', {}, 30_000);
      } catch {
        // The renderer can close before the acknowledgement is delivered.
      }
      this.cdp.close();
      this.cdp = null;
    }
    const ownedProcess = this.process;
    await this.stopOwnedProcess(ownedProcess, 15_000);
    await waitForOwnedProcessClose(ownedProcess, 15_000);
    this.process = null;
  }

  async evaluate(expression, timeoutMilliseconds = 30_000) {
    if (!this.cdp) throw new Error('Electron CDP is not connected.');
    const response = await this.cdp.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true
    }, timeoutMilliseconds);
    if (response.exceptionDetails) {
      throw new Error(
        response.exceptionDetails.exception?.description
        ?? response.exceptionDetails.text
        ?? 'Renderer evaluation failed.');
    }
    return response.result.value;
  }

  async waitFor(expression, timeoutMilliseconds, description) {
    const deadline = Date.now() + timeoutMilliseconds;
    let lastValue;
    while (Date.now() < deadline) {
      try {
        lastValue = await this.evaluate(
          expression,
          Math.max(250, Math.min(5_000, deadline - Date.now())));
        if (lastValue) return lastValue;
      } catch (error) {
        lastValue = error instanceof Error ? error.message : String(error);
      }
      await delay(250);
    }
    throw new Error(`Timed out waiting for ${description}. Last value: ${JSON.stringify(lastValue)}`);
  }

  async captureJavaScriptStack(timeoutMilliseconds = 10_000) {
    if (!this.cdp) throw new Error('Electron CDP is not connected.');
    await this.cdp.send('Debugger.enable', {}, timeoutMilliseconds);
    const paused = this.cdp.waitForEvent('Debugger.paused', timeoutMilliseconds);
    try {
      const [event] = await Promise.all([
        paused,
        this.cdp.send('Debugger.pause', {}, timeoutMilliseconds)
      ]);
      return (event.callFrames ?? []).map(frame => ({
        functionName: frame.functionName || '(anonymous)',
        url: frame.url,
        lineNumber: (frame.location?.lineNumber ?? -1) + 1,
        columnNumber: (frame.location?.columnNumber ?? -1) + 1
      }));
    } finally {
      await this.cdp.send('Debugger.resume', {}, timeoutMilliseconds)
        .catch(() => undefined);
    }
  }

  async click(testId) {
    return this.evaluate(`(() => {
      const element = document.querySelector('[data-testid=${JSON.stringify(testId)}]');
      if (!(element instanceof HTMLElement || element instanceof SVGElement)) {
        throw new Error('Missing element ${escapeForJavaScript(testId)}');
      }
      if (element instanceof HTMLButtonElement && element.disabled) {
        throw new Error('Disabled button ${escapeForJavaScript(testId)}');
      }
      if (element instanceof HTMLElement) element.click();
      else element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
      return true;
    })()`);
  }

  async setInput(testId, value) {
    return this.evaluate(`(() => {
      const element = document.querySelector('[data-testid=${JSON.stringify(testId)}]');
      if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)) {
        throw new Error('Missing input ${escapeForJavaScript(testId)}');
      }
      const prototype = element instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(element, ${JSON.stringify(value)});
      element.dispatchEvent(new Event('input', { bubbles: true }));
      element.dispatchEvent(new Event('change', { bubbles: true }));
      return true;
    })()`);
  }

  async setSelect(testId, value) {
    return this.evaluate(`(() => {
      const element = document.querySelector('[data-testid=${JSON.stringify(testId)}]');
      if (!(element instanceof HTMLSelectElement)) throw new Error('Missing select ${escapeForJavaScript(testId)}');
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set?.call(element, ${JSON.stringify(value)});
      element.dispatchEvent(new Event('input', { bubbles: true }));
      element.dispatchEvent(new Event('change', { bubbles: true }));
      return true;
    })()`);
  }

  async api(pathname, options = {}) {
    return this.evaluate(
      `window.openlineopsDesktop.apiRequest(${JSON.stringify(pathname)}, ${JSON.stringify(options)})`);
  }

  async screenshot(filePath) {
    if (!this.cdp) throw new Error('Electron CDP is not connected.');
    const result = await this.cdp.send('Page.captureScreenshot', {
      format: 'png',
      fromSurface: true,
      captureBeyondViewport: false
    });
    await fs.mkdir(path.dirname(filePath), { recursive: true });
    await fs.writeFile(filePath, Buffer.from(result.data, 'base64'));
    return filePath;
  }
}

export async function waitForOwnedProcessClose(
  child,
  timeoutMilliseconds = 8_000
) {
  if (!child) return;
  assertPositiveTimeout(timeoutMilliseconds, 'process close timeout');
  const closePromise = child.openlineopsClosePromise;
  if (!(closePromise instanceof Promise)) {
    throw new Error(
      'Owned process close must be observed from the instant it is spawned.');
  }
  let timeout;
  try {
    await Promise.race([
      closePromise,
      new Promise((_, reject) => {
        timeout = setTimeout(
          () => reject(new Error(
            `Owning process ${child.pid ?? 'unknown'} streams did not close within ${
              timeoutMilliseconds} ms.`)),
          timeoutMilliseconds);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function escapeForJavaScript(value) {
  return value.replaceAll('\\', '\\\\').replaceAll("'", "\\'");
}

export async function waitForTarget(
  port,
  timeoutMilliseconds,
  diagnostics = () => null,
  options = {}
) {
  assertPositiveTimeout(timeoutMilliseconds, 'CDP target timeout');
  const requestTimeoutMilliseconds = options.requestTimeoutMilliseconds ?? 2_000;
  assertPositiveTimeout(requestTimeoutMilliseconds, 'CDP target request timeout');
  const fetchImplementation = options.fetchImplementation ?? fetch;
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    try {
      const remaining = Math.max(1, deadline - Date.now());
      const response = await fetchImplementation(
        `http://127.0.0.1:${port}/json/list`,
        {
          signal: AbortSignal.timeout(
            Math.max(1, Math.min(requestTimeoutMilliseconds, remaining)))
        });
      if (response.ok) {
        const targets = await response.json();
        const target = targets.find(candidate => candidate.type === 'page' && candidate.url.startsWith('file:'));
        if (target?.webSocketDebuggerUrl) return target;
      }
    } catch {
      // Electron is still starting.
    }
    const state = diagnostics();
    if (typeof state?.launchError === 'string'
        && state.launchError.length > 0) {
      throw new Error(
        `Packaged Electron failed to start before exposing CDP on port ${port}. Diagnostics: ${JSON.stringify(state)}`);
    }
    if ((state?.exitCode !== null && state?.exitCode !== undefined)
        || (state?.signalCode !== null && state?.signalCode !== undefined)) {
      throw new Error(
        `Packaged Electron exited before exposing CDP on port ${port}. Diagnostics: ${JSON.stringify(state)}`);
    }
    await delay(Math.max(1, Math.min(250, deadline - Date.now())));
  }
  throw new Error(
    `Timed out waiting for packaged Electron CDP on port ${port}. Diagnostics: ${JSON.stringify(diagnostics())}`);
}

async function waitForChildExit(child, timeoutMilliseconds) {
  if (!child || hasChildExited(child)) return true;
  return new Promise(resolve => {
    let settled = false;
    const complete = value => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      child.removeListener('exit', onExit);
      resolve(value);
    };
    const onExit = () => complete(true);
    const timeout = setTimeout(
      () => complete(false),
      timeoutMilliseconds);
    child.once('exit', onExit);
    if (hasChildExited(child)) complete(true);
  });
}

function hasChildExited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

function assertPositiveTimeout(timeoutMilliseconds, description) {
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error(`${description} must be a positive safe integer.`);
  }
}
