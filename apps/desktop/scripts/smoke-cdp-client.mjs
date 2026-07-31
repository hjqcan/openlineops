import WebSocket from 'ws';

const webSocketConnectingState = 0;
const webSocketOpenState = 1;
const defaultConnectionTimeoutMilliseconds = 5000;
const defaultCommandTimeoutMilliseconds = 60000;

export class CdpCommandTimeoutError extends Error {
  constructor(method, timeoutMilliseconds) {
    super(`CDP command ${method} did not complete within ${timeoutMilliseconds} ms.`);
    this.name = 'CdpCommandTimeoutError';
    this.method = method;
    this.timeoutMilliseconds = timeoutMilliseconds;
  }
}

export async function waitForCdpValue({
  probe,
  timeoutMilliseconds,
  description,
  commandTimeoutMilliseconds = 5000,
  retryDelayMilliseconds = 500
}) {
  if (typeof probe !== 'function') {
    throw new Error('CDP wait probe must be a function.');
  }
  assertPositiveTimeout(timeoutMilliseconds, 'CDP wait timeout');
  assertPositiveTimeout(commandTimeoutMilliseconds, 'CDP wait command timeout');
  assertPositiveTimeout(retryDelayMilliseconds, 'CDP wait retry delay');
  if (typeof description !== 'string' || description.trim().length === 0) {
    throw new Error('CDP wait description is required.');
  }

  const deadline = Date.now() + timeoutMilliseconds;
  let lastValue;
  while (Date.now() < deadline) {
    const remainingMilliseconds = Math.max(1, deadline - Date.now());
    try {
      lastValue = await probe(
        Math.min(commandTimeoutMilliseconds, remainingMilliseconds));
    } catch (error) {
      if (!(error instanceof CdpCommandTimeoutError)) {
        throw new Error(
          `Failed while waiting for ${description}: ${
            error instanceof Error ? error.message : String(error)}`,
          { cause: error });
      }
      lastValue = {
        commandTimeout: error.message
      };
      const delayMilliseconds = Math.min(
        retryDelayMilliseconds,
        Math.max(0, deadline - Date.now()));
      if (delayMilliseconds > 0) {
        await delay(delayMilliseconds);
      }
      continue;
    }

    if (lastValue) {
      return lastValue;
    }
    const delayMilliseconds = Math.min(
      retryDelayMilliseconds,
      Math.max(0, deadline - Date.now()));
    if (delayMilliseconds > 0) {
      await delay(delayMilliseconds);
    }
  }

  throw new Error(
    `Timed out waiting for ${description}. Last value: ${JSON.stringify(lastValue)}`);
}

export class CdpClient {
  constructor(socket, onEvent, commandTimeoutMilliseconds) {
    this.socket = socket;
    this.onEvent = onEvent;
    this.commandTimeoutMilliseconds = commandTimeoutMilliseconds;
    this.nextId = 1;
    this.pending = new Map();
    this.eventWaiters = new Map();
  }

  static connect(webSocketUrl, options = {}) {
    const connectionTimeoutMilliseconds = options.connectionTimeoutMilliseconds
      ?? defaultConnectionTimeoutMilliseconds;
    const commandTimeoutMilliseconds = options.commandTimeoutMilliseconds
      ?? defaultCommandTimeoutMilliseconds;
    const onEvent = options.onEvent ?? (() => {});
    const socketFactory = options.socketFactory ?? (url => new WebSocket(url));
    assertPositiveTimeout(
      connectionTimeoutMilliseconds,
      'CDP connection timeout');
    assertPositiveTimeout(
      commandTimeoutMilliseconds,
      'CDP command timeout');
    if (typeof webSocketUrl !== 'string' || webSocketUrl.length === 0) {
      return Promise.reject(new Error('CDP WebSocket URL is required.'));
    }
    if (typeof onEvent !== 'function' || typeof socketFactory !== 'function') {
      return Promise.reject(
        new Error('CDP event handler and WebSocket factory must be functions.'));
    }

    return new Promise((resolve, reject) => {
      let socket;
      try {
        socket = socketFactory(webSocketUrl);
      } catch (error) {
        reject(error);
        return;
      }
      const client = new CdpClient(
        socket,
        onEvent,
        commandTimeoutMilliseconds);
      let settled = false;
      const removeHandshakeListeners = () => {
        socket.removeEventListener('open', onOpen);
        socket.removeEventListener('error', onHandshakeError);
        socket.removeEventListener('close', onHandshakeClose);
      };
      const settle = action => {
        if (settled) {
          return false;
        }
        settled = true;
        clearTimeout(timeout);
        removeHandshakeListeners();
        action();
        return true;
      };
      const onOpen = () => settle(() => resolve(client));
      const onHandshakeError = event => {
        const error = event?.error instanceof Error
          ? event.error
          : new Error('CDP WebSocket failed during its opening handshake.');
        if (settle(() => reject(error))) {
          client.closeSocket();
        }
      };
      const onHandshakeClose = event => {
        const code = Number.isSafeInteger(event?.code) ? event.code : 'unknown';
        const reason = typeof event?.reason === 'string' && event.reason.length > 0
          ? `: ${event.reason}`
          : '.';
        settle(() => reject(
          new Error(`CDP WebSocket closed during its opening handshake with code ${code}${reason}`)));
      };
      const timeout = setTimeout(() => {
        const error = new Error(
          `CDP WebSocket did not open within ${connectionTimeoutMilliseconds} ms.`);
        if (settle(() => reject(error))) {
          client.closeSocket();
        }
      }, connectionTimeoutMilliseconds);

      socket.addEventListener('open', onOpen);
      socket.addEventListener('error', onHandshakeError);
      socket.addEventListener('close', onHandshakeClose);
      socket.addEventListener('error', event => {
        client.failAndClose(event?.error instanceof Error
          ? event.error
          : new Error('CDP WebSocket error.'));
      });
      socket.addEventListener('close', event => {
        const code = Number.isSafeInteger(event?.code) ? event.code : 'unknown';
        const reason = typeof event?.reason === 'string' && event.reason.length > 0
          ? `: ${event.reason}`
          : '.';
        client.rejectAll(
          new Error(`CDP WebSocket closed with code ${code}${reason}`));
      });
      socket.addEventListener('message', event => client.handleMessage(event.data));
    });
  }

  send(method, params = {}, timeoutMilliseconds = this.commandTimeoutMilliseconds) {
    if (typeof method !== 'string' || method.length === 0) {
      return Promise.reject(new Error('CDP command method is required.'));
    }
    assertPositiveTimeout(timeoutMilliseconds, 'CDP command timeout');
    if (this.socket.readyState !== webSocketOpenState) {
      return Promise.reject(new Error(
        `Cannot send ${method}; the CDP socket is not open (state ${this.socket.readyState}).`));
    }

    const id = this.nextId++;
    const payload = JSON.stringify({ id, method, params });
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        const pending = this.pending.get(id);
        if (!pending) {
          return;
        }
        // CDP requests are multiplexed. A command deadline does not prove that
        // the transport is corrupt, so expire only this request. Any late
        // response is ignored because its pending entry no longer exists.
        this.pending.delete(id);
        pending.reject(new CdpCommandTimeoutError(method, timeoutMilliseconds));
      }, timeoutMilliseconds);
      this.pending.set(id, { resolve, reject, timeout });
      try {
        this.socket.send(payload);
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(id);
        reject(error);
        this.failAndClose(error instanceof Error
          ? error
          : new Error(`CDP command ${method} could not be sent.`));
      }
    });
  }

  isOpen() {
    return this.socket.readyState === webSocketOpenState;
  }

  close() {
    this.rejectAll(new Error('CDP socket closed.'));
    this.closeSocket();
  }

  failAndClose(error) {
    this.rejectAll(error);
    this.closeSocket();
  }

  closeSocket() {
    if (this.socket.readyState !== webSocketOpenState
        && this.socket.readyState !== webSocketConnectingState) {
      return;
    }
    try {
      if (typeof this.socket.terminate === 'function') {
        this.socket.terminate();
      } else {
        this.socket.close();
      }
    } catch {
      // Pending commands were already rejected before socket cleanup.
    }
  }

  rejectPending(error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.pending.clear();
  }

  rejectEventWaiters(error) {
    for (const waiters of this.eventWaiters.values()) {
      for (const waiter of waiters) {
        clearTimeout(waiter.timeout);
        waiter.reject(error);
      }
    }
    this.eventWaiters.clear();
  }

  rejectAll(error) {
    this.rejectPending(error);
    this.rejectEventWaiters(error);
  }

  waitForEvent(method, timeoutMilliseconds = this.commandTimeoutMilliseconds) {
    if (typeof method !== 'string' || method.length === 0) {
      return Promise.reject(new Error('CDP event method is required.'));
    }
    assertPositiveTimeout(timeoutMilliseconds, 'CDP event timeout');
    if (this.socket.readyState !== webSocketOpenState) {
      return Promise.reject(new Error(
        `Cannot wait for ${method}; the CDP socket is not open (state ${this.socket.readyState}).`));
    }

    return new Promise((resolve, reject) => {
      const waiters = this.eventWaiters.get(method) ?? [];
      const waiter = {
        resolve,
        reject,
        timeout: null
      };
      waiter.timeout = setTimeout(() => {
        const current = this.eventWaiters.get(method) ?? [];
        const index = current.indexOf(waiter);
        if (index >= 0) {
          current.splice(index, 1);
        }
        if (current.length === 0) {
          this.eventWaiters.delete(method);
        }
        reject(new Error(
          `CDP event ${method} did not arrive within ${timeoutMilliseconds} ms.`));
      }, timeoutMilliseconds);
      waiters.push(waiter);
      this.eventWaiters.set(method, waiters);
    });
  }

  handleMessage(data) {
    let message;
    try {
      message = JSON.parse(data);
    } catch {
      this.failAndClose(new Error('CDP WebSocket returned invalid JSON.'));
      return;
    }

    if (!message.id) {
      const waiters = this.eventWaiters.get(message.method);
      const waiter = waiters?.shift();
      if (waiters?.length === 0) {
        this.eventWaiters.delete(message.method);
      }
      if (waiter) {
        clearTimeout(waiter.timeout);
        waiter.resolve(message.params ?? {});
      }
      try {
        this.onEvent(message);
      } catch {
        this.failAndClose(new Error('CDP event handling failed.'));
      }
      return;
    }

    const pending = this.pending.get(message.id);
    if (!pending) {
      return;
    }

    clearTimeout(pending.timeout);
    this.pending.delete(message.id);
    if (message.error) {
      pending.reject(new Error(message.error.message));
      return;
    }
    pending.resolve(message.result);
  }
}

function assertPositiveTimeout(timeoutMilliseconds, description) {
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
    throw new Error(`${description} must be a positive safe integer.`);
  }
}

function delay(timeoutMilliseconds) {
  return new Promise(resolve => setTimeout(resolve, timeoutMilliseconds));
}
