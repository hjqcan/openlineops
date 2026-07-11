import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';

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

export function spawnCaptured(command, args, options, label, lines) {
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
  child.on('exit', code => append(`exited with code ${code ?? 'unknown'}`));
  return child;
}

export async function stopProcess(child, timeoutMilliseconds = 8_000) {
  if (!child || child.exitCode !== null || child.killed) return;
  child.kill();
  const exited = await Promise.race([
    new Promise(resolve => child.once('exit', () => resolve(true))),
    delay(timeoutMilliseconds).then(() => false)
  ]);
  if (!exited && child.exitCode === null) child.kill('SIGKILL');
}

export class ElectronCdpHarness {
  constructor({ executablePath, workingDirectory, userDataDirectory, apiBaseUrl, environment, logs }) {
    this.executablePath = executablePath;
    this.workingDirectory = workingDirectory;
    this.userDataDirectory = userDataDirectory;
    this.apiBaseUrl = apiBaseUrl;
    this.environment = environment;
    this.logs = logs;
    this.process = null;
    this.cdp = null;
    this.cdpPort = null;
  }

  async start() {
    this.cdpPort = await getFreePort();
    this.process = spawnCaptured(
      this.executablePath,
      [
        `--remote-debugging-port=${this.cdpPort}`,
        '--disable-gpu',
        `--user-data-dir=${this.userDataDirectory}`
      ],
      {
        cwd: this.workingDirectory,
        env: {
          ...process.env,
          ...this.environment,
          OPENLINEOPS_API_BASE_URL: this.apiBaseUrl
        }
      },
      'OpenLineOps',
      this.logs);

    const target = await waitForTarget(this.cdpPort, 45_000);
    this.cdp = await CdpClient.connect(target.webSocketDebuggerUrl);
    await this.cdp.send('Runtime.enable');
    await this.cdp.send('Page.enable');
    await this.waitFor(
      'Boolean(document.querySelector("[data-testid=\\"automation-ide-shell\\"]")) && Boolean(window.openlineopsDesktop)',
      45_000,
      'the packaged Studio shell');
  }

  async close() {
    if (this.cdp) {
      try {
        await this.cdp.send('Browser.close');
      } catch {
        // The renderer can close before the acknowledgement is delivered.
      }
      this.cdp.close();
      this.cdp = null;
    }
    await stopProcess(this.process);
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
    await this.cdp.send('Debugger.pause', {}, timeoutMilliseconds);
    const event = await paused;
    const frames = (event.callFrames ?? []).map(frame => ({
      functionName: frame.functionName || '(anonymous)',
      url: frame.url,
      lineNumber: (frame.location?.lineNumber ?? -1) + 1,
      columnNumber: (frame.location?.columnNumber ?? -1) + 1
    }));
    await this.cdp.send('Debugger.resume', {}, timeoutMilliseconds).catch(() => undefined);
    return frames;
  }

  async click(testId) {
    return this.evaluate(`(() => {
      const element = document.querySelector('[data-testid=${JSON.stringify(testId)}]');
      if (!(element instanceof HTMLElement || element instanceof SVGElement)) {
        throw new Error('Missing element ${escapeForJavaScript(testId)}');
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

  async uploadExternalProgram(pathname, definition, files, headers = {}) {
    return this.evaluate(
      `window.openlineopsDesktop.uploadExternalProgram(`
      + `${JSON.stringify(pathname)}, ${JSON.stringify(definition)}, ${JSON.stringify(files)}, ${JSON.stringify(headers)})`);
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

function escapeForJavaScript(value) {
  return value.replaceAll('\\', '\\\\').replaceAll("'", "\\'");
}

async function waitForTarget(port, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      if (response.ok) {
        const targets = await response.json();
        const target = targets.find(candidate => candidate.type === 'page' && candidate.url.startsWith('file:'));
        if (target?.webSocketDebuggerUrl) return target;
      }
    } catch {
      // Electron is still starting.
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for packaged Electron CDP on port ${port}.`);
}

class CdpClient {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 1;
    this.pending = new Map();
    this.eventWaiters = new Map();
  }

  static connect(webSocketUrl) {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(webSocketUrl);
      const client = new CdpClient(socket);
      socket.addEventListener('open', () => resolve(client), { once: true });
      socket.addEventListener('error', event => reject(event.error ?? new Error('CDP socket error.')), { once: true });
      socket.addEventListener('close', () => client.handleClose(), { once: true });
      socket.addEventListener('message', event => client.handle(event.data));
    });
  }

  send(method, params = {}, timeoutMilliseconds = 30_000) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        if (!this.pending.delete(id)) return;
        reject(new Error(`CDP ${method} timed out after ${timeoutMilliseconds} ms.`));
      }, timeoutMilliseconds);
      this.pending.set(id, {
        resolve: value => {
          clearTimeout(timeout);
          resolve(value);
        },
        reject: error => {
          clearTimeout(timeout);
          reject(error);
        }
      });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  close() {
    this.handleClose();
    if (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING) {
      this.socket.close();
    }
  }

  handleClose() {
    for (const pending of this.pending.values()) pending.reject(new Error('CDP connection closed.'));
    this.pending.clear();
    for (const waiters of this.eventWaiters.values()) {
      for (const waiter of waiters) waiter.reject(new Error('CDP connection closed.'));
    }
    this.eventWaiters.clear();
  }

  waitForEvent(method, timeoutMilliseconds = 10_000) {
    return new Promise((resolve, reject) => {
      const waiters = this.eventWaiters.get(method) ?? [];
      const waiter = {
        resolve: value => {
          clearTimeout(timeout);
          resolve(value);
        },
        reject: error => {
          clearTimeout(timeout);
          reject(error);
        }
      };
      const timeout = setTimeout(() => {
        const current = this.eventWaiters.get(method) ?? [];
        const index = current.indexOf(waiter);
        if (index >= 0) current.splice(index, 1);
        if (current.length === 0) this.eventWaiters.delete(method);
        reject(new Error(`CDP event ${method} timed out after ${timeoutMilliseconds} ms.`));
      }, timeoutMilliseconds);
      waiters.push(waiter);
      this.eventWaiters.set(method, waiters);
    });
  }

  handle(data) {
    const message = JSON.parse(data);
    if (!message.id) {
      const waiters = this.eventWaiters.get(message.method);
      const waiter = waiters?.shift();
      if (waiters?.length === 0) this.eventWaiters.delete(message.method);
      waiter?.resolve(message.params ?? {});
      return;
    }
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    if (message.error) pending.reject(new Error(message.error.message));
    else pending.resolve(message.result);
  }
}
