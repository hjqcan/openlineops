import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

import {
  canonicalizeLocalBackendBaseUrl,
  fetchAuthenticatedBackend,
  resolveCanonicalBackendApiUrl
} from '../dist-electron/main/backend-api-security.js';
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

const mainSource = await readFile(
  new URL('../src/main/main.ts', import.meta.url),
  'utf8');

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
  assert.match(mainSource, /webContents\.on\('will-navigate', preventUntrustedNavigation\)/);
  assert.match(mainSource, /webContents\.on\('will-redirect', preventUntrustedNavigation\)/);
  assert.match(mainSource, /webContents\.setWindowOpenHandler\(\(\) => \(\{ action: 'deny' \}\)\)/);
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
    'desktop:close-response',
    'desktop:get-config',
    'desktop:release-external-program-directory-selection',
    'desktop:select-application-project-file',
    'desktop:select-directory',
    'desktop:select-external-program-directory',
    'desktop:select-project-file',
    'trace:save-artifact'
  ];
  const registrations = [
    ...mainSource.matchAll(/ipcMain\.(?:handle|on)\('([^']+)'/gu)
  ];
  assert.deepEqual(
    registrations.map(registration => registration[1]).sort(),
    expectedChannels);
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
      < mainSource.indexOf('mainWindow = new BrowserWindow'),
    'development renderer proof must succeed before privileged preload creation');
});

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
