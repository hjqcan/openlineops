import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

import {
  canonicalizeLocalBackendBaseUrl,
  fetchAuthenticatedBackend,
  resolveCanonicalBackendApiUrl
} from '../dist-electron/main/backend-api-security.js';
import {
  classifyBackendProcessError
} from '../dist-electron/main/backend-process-error-policy.js';
import {
  backendProcessHandshakeChallengeHeader,
  backendProcessHandshakeProofHeader,
  computeBackendProcessHandshakeProof,
  verifyBackendProcessHandshakeServer
} from '../dist-electron/main/backend-process-handshake.js';
import {
  canonicalizeTrustedDevRendererUrl,
  isTrustedRendererDocumentUrl,
  isTrustedRendererIpcContext,
  rendererNonceProof,
  verifyTrustedDevRendererServer
} from '../dist-electron/main/renderer-navigation-security.js';
import {
  createSafeCdpEvent,
  formatDiagnosticError,
  redactDiagnosticText,
  sanitizeDiagnosticValue
} from './smoke-diagnostics.mjs';
import {
  CdpClient,
  CdpCommandTimeoutError,
  waitForCdpValue
} from './smoke-cdp-client.mjs';
import { waitForHttp } from './smoke-http-wait.mjs';

const mainSource = await readFile(
  new URL('../src/main/main.ts', import.meta.url),
  'utf8');
const electronSmokeSource = await readFile(
  new URL('./electron-smoke.mjs', import.meta.url),
  'utf8');

test('live backend process errors retain the exact identity and credentials', () => {
  const child = new EventEmitter();
  child.exitCode = null;
  child.signalCode = null;
  let released = false;
  child.on('error', () => {
    released = classifyBackendProcessError(child, true) !== 'RetainLiveProcess';
  });

  child.emit('error', Object.assign(
    new Error('access denied while terminating the exact handle'),
    { code: 'EPERM' }));

  assert.equal(released, false);
  assert.equal(
    classifyBackendProcessError(
      { exitCode: null, signalCode: null },
      false),
    'ReleaseFailedSpawn');
  assert.equal(
    classifyBackendProcessError(
      { exitCode: 1, signalCode: null },
      true),
    'ReleaseExitedProcess');
  assert.match(
    mainSource,
    /classifyBackendProcessError\(child, spawnConfirmed\)[\s\S]*?RetainLiveProcess[\s\S]*?retaining its identity and credentials/u);
});

test('backend API paths are canonical relative paths on one loopback origin', () => {
  const base = canonicalizeLocalBackendBaseUrl('http://127.0.0.1:5135');
  assert.equal(base, 'http://127.0.0.1:5135');
  assert.equal(
    resolveCanonicalBackendApiUrl(base, '/api/platform?probe=true').href,
    'http://127.0.0.1:5135/api/platform?probe=true');

  for (const untrustedPath of [
    'https://attacker.invalid/api/platform',
    'http://user:password@attacker.invalid/api/platform',
    '//attacker.invalid/api/platform',
    '/\\attacker.invalid/api/platform',
    '/api/platform\\escape',
    '/api/platform#fragment',
    '/api/platform\u0000suffix',
    '/api/platform/%2fescape',
    '/api/a/../platform'
  ]) {
    assert.throws(
      () => resolveCanonicalBackendApiUrl(base, untrustedPath),
      /canonical|relative|escaped|percent/,
      untrustedPath);
  }

  for (const untrustedBase of [
    'https://attacker.invalid:5135',
    'http://127.0.0.1:5135/api',
    'http://user@127.0.0.1:5135',
    'http://127.0.0.1:5135#fragment'
  ]) {
    assert.throws(
      () => canonicalizeLocalBackendBaseUrl(untrustedBase),
      /canonical|loopback/,
      untrustedBase);
  }
});

test('Safety credential is used only for the exact emergency-stop route', async () => {
  const requests = [];
  await withHttpServer((request, response) => {
    requests.push({ url: request.url, authorization: request.headers.authorization });
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end('{}');
  }, async baseUrl => {
    const paths = [
      '/api/platform',
      '/api/operations/stations/station-01/emergency-stop',
      '/api/operations/stations/station-01/emergency-stop?preview=true',
      '/api/operations/stations/station-01/emergency-stop/'
    ];
    for (const requestPath of paths) {
      const response = await fetchAuthenticatedBackend({
        apiBaseUrl: baseUrl,
        requestPath,
        standardToken: 'standard-secret',
        safetyToken: 'safety-secret',
        credentialMode: 'route',
        assertSessionActive: () => {}
      });
      assert.equal(response.status, 200);
      await response.arrayBuffer();
    }
  });

  assert.deepEqual(
    requests.map(request => request.authorization),
    [
      'Bearer standard-secret',
      'Bearer safety-secret',
      'Bearer standard-secret',
      'Bearer standard-secret'
    ]);
});

test('manual redirect handling never forwards the Safety credential', async () => {
  let attackerRequests = 0;
  let backendAuthorization = null;
  await withHttpServer((_request, response) => {
    attackerRequests += 1;
    response.writeHead(200);
    response.end('unexpected');
  }, async attackerBaseUrl => {
    await withHttpServer((request, response) => {
      backendAuthorization = request.headers.authorization ?? null;
      response.writeHead(302, { location: `${attackerBaseUrl}/stolen` });
      response.end();
    }, async backendBaseUrl => {
      await assert.rejects(
        fetchAuthenticatedBackend({
          apiBaseUrl: backendBaseUrl,
          requestPath: '/api/operations/stations/station-01/emergency-stop',
          standardToken: 'standard-secret',
          safetyToken: 'safety-secret',
          credentialMode: 'route',
          assertSessionActive: () => {}
        }),
        /redirects are forbidden \(HTTP 302\)/);
    });
    await new Promise(resolve => setTimeout(resolve, 50));
  });

  assert.equal(backendAuthorization, 'Bearer safety-secret');
  assert.equal(attackerRequests, 0);
});

test('expired backend session is rejected before an API credential leaves the process', async () => {
  let requestCount = 0;
  await withHttpServer((_request, response) => {
    requestCount += 1;
    response.writeHead(500);
    response.end();
  }, async baseUrl => {
    await assert.rejects(
      fetchAuthenticatedBackend({
        apiBaseUrl: baseUrl,
        requestPath: '/api/platform',
        standardToken: 'standard-secret',
        safetyToken: 'safety-secret',
        credentialMode: 'route',
        assertSessionActive: () => {
          throw new Error('backend process exited');
        }
      }),
      /backend process exited/u);
  });
  assert.equal(requestCount, 0);
});

test('spawned API must prove the per-launch nonce and never receives authorization', async () => {
  const launchNonce = randomBytes(32).toString('base64url');
  const wrongNonce = randomBytes(32).toString('base64url');
  let proofNonce = wrongNonce;
  let requestCount = 0;
  await withHttpServer((request, response) => {
    requestCount += 1;
    assert.equal(request.method, 'GET');
    assert.equal(request.headers.authorization, undefined);
    const challenge = request.headers[backendProcessHandshakeChallengeHeader.toLowerCase()];
    assert.equal(typeof challenge, 'string');
    assert.match(challenge, /^[A-Za-z0-9_-]{43}$/u);
    response.writeHead(204, {
      [backendProcessHandshakeProofHeader]: computeBackendProcessHandshakeProof(
        proofNonce,
        challenge)
    });
    response.end();
  }, async baseUrl => {
    await assert.rejects(
      verifyBackendProcessHandshakeServer(baseUrl, launchNonce, () => {}),
      /proof did not match/u);
    proofNonce = launchNonce;
    await verifyBackendProcessHandshakeServer(baseUrl, launchNonce, () => {});
  });
  assert.equal(requestCount, 2);
});

test('a malicious loopback renderer without this launch nonce fails closed', async () => {
  const launchNonce = randomBytes(32).toString('base64url');
  let proof = rendererNonceProof(randomBytes(32).toString('base64url'));
  await withHttpServer((request, response) => {
    assert.equal(request.headers.authorization, undefined);
    response.writeHead(200, {
      'x-openlineops-renderer-proof': proof,
      'cache-control': 'no-store'
    });
    response.end('<!doctype html>');
  }, async baseUrl => {
    await assert.rejects(
      verifyTrustedDevRendererServer(baseUrl, launchNonce),
      /proof did not match/u);
    proof = rendererNonceProof(launchNonce);
    await verifyTrustedDevRendererServer(baseUrl, launchNonce);
  });
});

test('renderer document trust is exact and rejects remote or opaque senders', () => {
  const trustedDevUrl = canonicalizeTrustedDevRendererUrl('http://127.0.0.1:5173');
  assert.equal(trustedDevUrl, 'http://127.0.0.1:5173/');
  assert.equal(isTrustedRendererDocumentUrl(trustedDevUrl, trustedDevUrl), true);
  for (const senderUrl of [
    'https://attacker.invalid/',
    'about:blank',
    'data:text/html,untrusted',
    'http://127.0.0.1:5173/another-document',
    'http://localhost:5173/'
  ]) {
    assert.equal(isTrustedRendererDocumentUrl(senderUrl, trustedDevUrl), false, senderUrl);
  }

  const packagedUrl = pathToFileURL(path.resolve('dist', 'index.html')).href;
  assert.equal(isTrustedRendererDocumentUrl(packagedUrl, packagedUrl), true);
  assert.equal(isTrustedRendererDocumentUrl(`${packagedUrl}#remote`, packagedUrl), false);
  assert.equal(isTrustedRendererDocumentUrl('file:///C:/Windows/System32/index.html', packagedUrl), false);
  assert.equal(isTrustedRendererIpcContext(trustedDevUrl, trustedDevUrl, trustedDevUrl), true);
  assert.equal(isTrustedRendererIpcContext('https://attacker.invalid/', trustedDevUrl, trustedDevUrl), false);
  assert.equal(isTrustedRendererIpcContext('about:blank', trustedDevUrl, trustedDevUrl), false);
  assert.equal(isTrustedRendererIpcContext(trustedDevUrl, 'data:text/html,opaque', trustedDevUrl), false);
  assert.throws(
    () => canonicalizeTrustedDevRendererUrl('https://example.com/'),
    /loopback/);
});

test('main process applies navigation, window-open, redirect, and every IPC sender gate', () => {
  assert.match(mainSource, /ownedWebContents\.on\('will-navigate', preventUntrustedNavigation\)/);
  assert.match(mainSource, /ownedWebContents\.on\('will-redirect', preventUntrustedNavigation\)/);
  assert.match(mainSource, /ownedWebContents\.setWindowOpenHandler\(\(\) => \(\{ action: 'deny' \}\)\)/);
  assert.match(mainSource, /preload: path\.join\([^\n]+preload\.cjs/);
  assert.match(mainSource, /contextIsolation: true,[\s\S]*?nodeIntegration: false,[\s\S]*?sandbox: true/u);
  assert.doesNotMatch(mainSource, /sandbox: false/u);
  const expectedChannels = [
    'api:import-application-extension',
    'api:import-external-program-directory',
    'api:request',
    'backend:get-status',
    'backend:start',
    'backend:stop',
    'desktop:close-coordinator-ready',
    'desktop:close-request-acknowledged',
    'desktop:close-response',
    'desktop:get-close-coordinator-binding',
    'desktop:get-config',
    'desktop:release-external-program-directory-selection',
    'desktop:select-application-project-file',
    'desktop:select-directory',
    'desktop:select-external-program-directory',
    'desktop:select-project-file',
    'desktop:set-active-project-file',
    'trace:save-artifact'
  ];
  const registrations = [
    ...mainSource.matchAll(/ipcMain\.(?:handle|on)\('([^']+)'/gu)
  ];
  assert.deepEqual(
    registrations.map(registration => registration[1]).sort(),
    expectedChannels);
  assert.match(
    mainSource,
    /event\.senderFrame !== event\.sender\.mainFrame/u,
    'privileged IPC must reject a same-origin subframe');
  for (const [index, registration] of registrations.entries()) {
    const next = registrations[index + 1];
    const handlerSource = mainSource.slice(registration.index, next?.index ?? mainSource.length);
    assert.match(
      handlerSource,
      /assertTrustedRendererIpcSender\(event\)/u,
      `${registration[1]} must reject an untrusted sender before privileged work`);
  }
  assert.match(
    mainSource,
    /event\.senderFrame\?\.url[\s\S]*?event\.sender\.getURL\(\)[\s\S]*?isTrustedRendererIpcContext/);
  assert.ok(
    mainSource.indexOf('await waitForTrustedDevRenderer')
      < mainSource.indexOf('const ownedWindow = new BrowserWindow'),
    'development renderer proof must succeed before privileged preload creation');
  assert.ok(
    mainSource.indexOf("child.on('error'")
      < mainSource.indexOf('if (child.pid === undefined)'),
    'spawn errors must be handled before the backend process identity is inspected');
  assert.match(
    mainSource,
    /desktopProcessCreationTime = process\.getCreationTime\(\)[\s\S]*?desktopProcessStartedAtUnixMilliseconds = Math\.trunc\(\s*desktopProcessCreationTime\)/u);
  assert.doesNotMatch(
    mainSource,
    /Date\.now\(\) - \(process\.uptime\(\) \* 1000\)/u,
    'desktop parent binding must never use an approximate creation time');
  assert.equal(
    (
      mainSource.match(
        /OPENLINEOPS_DESKTOP_PARENT_PROCESS_STARTED_AT_UNIX_MS:/gu)
      ?? []
    ).length,
    2,
    'development and packaged API launches must carry the parent creation identity');
  assert.match(
    mainSource,
    /const terminationAccepted = child\.kill\(\)[\s\S]*?await waitForBackendProcessExit/u);
  assert.doesNotMatch(
    mainSource,
    /assertBackendProcessAlive[\s\S]*?child\.killed[\s\S]*?\}/u,
    'a sent termination signal must not erase the exact live-process session before confirmed exit');
  assert.doesNotMatch(
    mainSource,
    /taskkill\.exe/u,
    'desktop backend termination must use the exact spawned-process handle, never a reusable PID');
});

test('smoke failure diagnostics cannot serialize the live desktop API credential', () => {
  const diagnosticsStart = electronSmokeSource.indexOf('async function collectDiagnostics()');
  const diagnosticsEnd = electronSmokeSource.indexOf(
    'async function waitForCdpTarget',
    diagnosticsStart);
  assert.ok(diagnosticsStart >= 0);
  assert.ok(diagnosticsEnd > diagnosticsStart);
  const diagnosticsSource = electronSmokeSource.slice(diagnosticsStart, diagnosticsEnd);

  assert.doesNotMatch(diagnosticsSource, /apiAccessToken/u);
  assert.doesNotMatch(
    diagnosticsSource,
    /config:\s*await window\.openlineopsDesktop\?\.getConfig/u);
  assert.match(
    diagnosticsSource,
    /config:\s*desktopConfig == null[\s\S]*?apiBaseUrl:[\s\S]*?apiActorId:[\s\S]*?isPackaged:[\s\S]*?publicEvidenceMode:/u);
  assert.doesNotMatch(electronSmokeSource, /apiSquatterAuthorizationHeaders/u);
  assert.doesNotMatch(electronSmokeSource, /cdpEvents\.push\(message\)/u);
  assert.match(electronSmokeSource, /cdpEvents\.push\(createSafeCdpEvent\(message\)\)/u);
  assert.match(electronSmokeSource, /console\.error\(formatDiagnosticError\(error\)\)/u);
  assert.match(
    electronSmokeSource,
    /async function stopChild[\s\S]*?openlineopsOwnsKillOnCloseJob[\s\S]*?child\.kill\('SIGKILL'\)[\s\S]*?waitForChildExit/u);
  assert.doesNotMatch(
    electronSmokeSource,
    /taskkill\.exe|killProcessTreeByPid|killProcessByPid/u);
  assert.match(
    electronSmokeSource,
    /terminateWindowsProcessIdentity\([\s\S]*?backendProcessIdentity/u);
  assert.match(
    electronSmokeSource,
    /withTimeout\(\s*collectDiagnostics\(\),\s*5000,\s*'smoke failure diagnostics'\)/u);
  assert.match(
    electronSmokeSource,
    /closingCdp\.send\('Browser\.close', \{\}, 15_000\)/u,
    'cancelable application close must not retain the default one-minute command timer');
  assert.match(
    electronSmokeSource,
    /evaluate\('window\.openlineopsDesktop\?\.stopBackend\?\.\(\)', 4500\)[\s\S]*?5000,\s*'backend stop during cleanup'/u,
    'cleanup must expire the underlying backend command before its wrapper deadline');
  assert.match(
    electronSmokeSource,
    /cdp\.send\('Browser\.close', \{\}, 2500\)[\s\S]*?3000,\s*'Electron browser close during cleanup'/u,
    'cleanup must expire the underlying browser command before its wrapper deadline');
});

test('smoke diagnostic sanitization removes credentials and minimizes CDP payloads', () => {
  const secret = 'secret-token-value';
  const basicSecret = 'basic-secret-value';
  const uriUser = 'diagnostic-user';
  const uriPassword = 'diagnostic-password';
  const redactedText = redactDiagnosticText(
    `authorization: Bearer ${secret}; Authorization=Basic ${basicSecret}; `
    + `broker=amqps://${uriUser}:${uriPassword}@rabbitmq.local:5671/production; `
    + `apiAccessToken="${secret}"; password=${secret}`);
  assert.doesNotMatch(
    redactedText,
    new RegExp(secret, 'u'));
  assert.doesNotMatch(redactedText, new RegExp(basicSecret, 'u'));
  assert.doesNotMatch(redactedText, new RegExp(uriUser, 'u'));
  assert.doesNotMatch(redactedText, new RegExp(uriPassword, 'u'));
  assert.match(redactedText, /<redacted>/u);
  assert.doesNotMatch(
    formatDiagnosticError(new Error(`Bearer ${secret}`)),
    new RegExp(secret, 'u'));

  const safeConsoleEvent = createSafeCdpEvent({
    method: 'Runtime.consoleAPICalled',
    params: {
      type: 'log',
      timestamp: 1,
      args: [{ value: `Bearer ${secret}` }]
    }
  });
  assert.deepEqual(safeConsoleEvent, {
    method: 'Runtime.consoleAPICalled',
    type: 'log',
    timestamp: 1,
    argumentCount: 1
  });
  assert.doesNotMatch(JSON.stringify(safeConsoleEvent), new RegExp(secret, 'u'));

  const safeLogEvent = createSafeCdpEvent({
    method: 'Log.entryAdded',
    params: {
      entry: {
        source: 'javascript',
        level: 'error',
        text: `authorization=Bearer ${secret}`,
        url: 'file:///index.html',
        lineNumber: 12,
        requestHeaders: { authorization: `Bearer ${secret}` }
      }
    }
  });
  assert.doesNotMatch(JSON.stringify(safeLogEvent), new RegExp(secret, 'u'));

  const sanitizedValue = sanitizeDiagnosticValue({
    apiAccessToken: secret,
    nested: {
      recentLogs: [`authorization: Bearer ${secret}`],
      password: secret
    }
  });
  assert.deepEqual(sanitizedValue, {
    apiAccessToken: '<redacted>',
    nested: {
      recentLogs: ['authorization: <redacted>'],
      password: '<redacted>'
    }
  });
  assert.doesNotMatch(JSON.stringify(sanitizedValue), new RegExp(secret, 'u'));
});

test('smoke CDP connection and command waits are hard-bounded without poisoning the session', async () => {
  const unopenedSocket = new FakeWebSocket();
  await assert.rejects(
    CdpClient.connect('ws://127.0.0.1/unopened', {
      connectionTimeoutMilliseconds: 20,
      socketFactory: () => unopenedSocket
    }),
    /did not open within 20 ms/u);
  assert.equal(unopenedSocket.closeCalls, 1);

  const openSocket = new FakeWebSocket();
  const client = await CdpClient.connect('ws://127.0.0.1/open', {
    connectionTimeoutMilliseconds: 100,
    commandTimeoutMilliseconds: 20,
    socketFactory: () => {
      queueMicrotask(() => openSocket.open());
      return openSocket;
    }
  });
  const firstCommand = client.send('Runtime.evaluate');
  const secondCommand = client.send('Page.enable', {}, 200);
  await assert.rejects(firstCommand, error => {
    assert.ok(error instanceof CdpCommandTimeoutError);
    assert.equal(error.method, 'Runtime.evaluate');
    assert.equal(error.timeoutMilliseconds, 20);
    return true;
  });
  openSocket.completeLastCommand({ enabled: true });
  assert.deepEqual(await secondCommand, { enabled: true });
  assert.equal(openSocket.closeCalls, 0);
  assert.equal(client.isOpen(), true);

  openSocket.completeCommand(0, { result: { value: 'late' } });
  const followUpCommand = client.send('Runtime.evaluate', {}, 100);
  openSocket.completeLastCommand({ result: { value: 'recovered' } });
  assert.deepEqual(
    await followUpCommand,
    { result: { value: 'recovered' } });
  assert.equal(client.isOpen(), true);
  client.close();
  assert.equal(openSocket.closeCalls, 1);

  const delayedSocket = new FakeWebSocket();
  const delayedClient = await CdpClient.connect('ws://127.0.0.1/delayed', {
    connectionTimeoutMilliseconds: 100,
    commandTimeoutMilliseconds: 100,
    socketFactory: () => {
      queueMicrotask(() => delayedSocket.open());
      return delayedSocket;
    }
  });
  const delayedCommand = delayedClient.send('Runtime.evaluate');
  await new Promise(resolve => setTimeout(resolve, 30));
  delayedSocket.completeLastCommand({ result: { value: true } });
  assert.deepEqual(
    await delayedCommand,
    { result: { value: true } });
  const pausedEvent = delayedClient.waitForEvent('Debugger.paused', 100);
  delayedSocket.emitCdpEvent('Debugger.paused', {
    reason: 'other',
    callFrames: []
  });
  assert.deepEqual(await pausedEvent, {
    reason: 'other',
    callFrames: []
  });
  assert.equal(delayedSocket.closeCalls, 0);
  delayedClient.close();
  assert.equal(delayedClient.isOpen(), false);
  await assert.rejects(
    delayedClient.send('Runtime.evaluate'),
    /CDP socket is not open/u);
});

test('smoke CDP polling retries only command timeouts within its overall deadline', async () => {
  let attempts = 0;
  const recovered = await waitForCdpValue({
    probe: async commandTimeoutMilliseconds => {
      attempts += 1;
      assert.ok(commandTimeoutMilliseconds > 0);
      if (attempts <= 2) {
        throw new CdpCommandTimeoutError(
          'Runtime.evaluate',
          commandTimeoutMilliseconds);
      }
      return { ready: true };
    },
    timeoutMilliseconds: 100,
    commandTimeoutMilliseconds: 20,
    retryDelayMilliseconds: 1,
    description: 'the renderer readiness probe'
  });
  assert.deepEqual(recovered, { ready: true });
  assert.equal(attempts, 3);

  const timeoutStartedAt = Date.now();
  await assert.rejects(
    waitForCdpValue({
      probe: async commandTimeoutMilliseconds => {
        throw new CdpCommandTimeoutError(
          'Runtime.evaluate',
          commandTimeoutMilliseconds);
      },
      timeoutMilliseconds: 30,
      commandTimeoutMilliseconds: 5,
      retryDelayMilliseconds: 1,
      description: 'the persistently stalled renderer'
    }),
    error => {
      assert.match(
        error.message,
        /Timed out waiting for the persistently stalled renderer/u);
      assert.match(error.message, /commandTimeout/u);
      return true;
    });
  assert.ok(
    Date.now() - timeoutStartedAt < 500,
    'the CDP polling deadline was not hard-bounded');

  await assert.rejects(
    waitForCdpValue({
      probe: async () => {
        throw new Error('renderer assertion failed');
      },
      timeoutMilliseconds: 100,
      retryDelayMilliseconds: 1,
      description: 'the labeled renderer assertion'
    }),
    /Failed while waiting for the labeled renderer assertion: renderer assertion failed/u);
});

test('stalled real CDP upgrade is force-terminated with no live TCP handle', async () => {
  const sockets = new Set();
  const server = net.createServer(socket => {
    sockets.add(socket);
    socket.resume();
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address !== 'string');

  try {
    await assert.rejects(
      CdpClient.connect(`ws://127.0.0.1:${address.port}/stalled`, {
        connectionTimeoutMilliseconds: 50
      }),
      /(?:did not open within 50 ms|Opening handshake has timed out)/u);
    const deadline = Date.now() + 1_000;
    while (sockets.size > 0 && Date.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.equal(
      sockets.size,
      0,
      'timed-out CDP handshake retained an accepted TCP connection');
  } finally {
    for (const socket of sockets) {
      socket.destroy();
    }
    await new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
    });
  }
});

test('stalled real HTTP response cannot outlive the endpoint deadline', async () => {
  const sockets = new Set();
  const server = net.createServer(socket => {
    sockets.add(socket);
    socket.resume();
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address !== 'string');
  const startedAt = Date.now();

  try {
    await assert.rejects(
      waitForHttp(
        `http://127.0.0.1:${address.port}`,
        80,
        'stalled test endpoint'),
      /Timed out waiting for stalled test endpoint/u);
    assert.ok(Date.now() - startedAt < 1_000);
    const deadline = Date.now() + 1_000;
    while (sockets.size > 0 && Date.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.equal(
      sockets.size,
      0,
      'timed-out HTTP request retained an accepted TCP connection');
  } finally {
    for (const socket of sockets) {
      socket.destroy();
    }
    await new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
    });
  }
});

class FakeWebSocket extends EventTarget {
  constructor() {
    super();
    this.readyState = 0;
    this.closeCalls = 0;
    this.sentPayloads = [];
  }

  open() {
    this.readyState = 1;
    this.dispatchEvent(new Event('open'));
  }

  send(payload) {
    this.sentPayloads.push(payload);
  }

  completeLastCommand(result) {
    this.completeCommand(this.sentPayloads.length - 1, result);
  }

  completeCommand(index, result) {
    const payload = JSON.parse(this.sentPayloads[index]);
    const event = new Event('message');
    Object.defineProperty(event, 'data', {
      value: JSON.stringify({ id: payload.id, result })
    });
    this.dispatchEvent(event);
  }

  emitCdpEvent(method, params) {
    const event = new Event('message');
    Object.defineProperty(event, 'data', {
      value: JSON.stringify({ method, params })
    });
    this.dispatchEvent(event);
  }

  close() {
    if (this.readyState === 3) {
      return;
    }
    this.closeCalls += 1;
    this.readyState = 3;
    const event = new Event('close');
    Object.defineProperties(event, {
      code: { value: 1000 },
      reason: { value: '' }
    });
    this.dispatchEvent(event);
  }
}

async function withHttpServer(listener, action) {
  const server = http.createServer(listener);
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    const address = server.address();
    assert.ok(address && typeof address !== 'string');
    await action(`http://127.0.0.1:${address.port}`);
  } finally {
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
}
