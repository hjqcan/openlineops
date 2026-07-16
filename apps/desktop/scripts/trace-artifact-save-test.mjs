import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { saveTraceArtifactToPath } from '../dist-electron/main/trace-artifact-save-core.js';

const token = 'trace-artifact-save-test-token';
const storageKey = 'runs/run-1/operation-1/stdout.log';
const assertSessionActive = () => {};

test('verified Trace artifact save uses a sibling temporary file and atomic replacement', async () => {
  await withWorkspace(async workspace => {
    const bytes = Buffer.from('immutable production evidence\n', 'utf8');
    const server = await startArtifactServer((request, response) => {
      assert.equal(request.url, `/api/traceability/artifacts/${storageKey}`);
      assert.equal(request.headers.authorization, `Bearer ${token}`);
      response.writeHead(200, {
        'content-type': 'text/plain',
        'content-length': bytes.byteLength
      });
      response.end(bytes);
    });
    try {
      const destination = path.join(workspace, 'stdout.log');
      await fs.writeFile(destination, 'old evidence must be replaced atomically', 'utf8');
      const options = artifactOptions(bytes);
      const result = await saveTraceArtifactToPath(
        server.baseUrl,
        token,
        options,
        destination,
        assertSessionActive);

      assert.deepEqual(await fs.readFile(destination), bytes);
      assert.deepEqual(result, {
        canceled: false,
        path: destination,
        sizeBytes: bytes.byteLength,
        sha256: options.expectedSha256
      });
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('exact chunked response is streamed successfully without Content-Length', async () => {
  await withWorkspace(async workspace => {
    const bytes = Buffer.from('chunked immutable production evidence', 'utf8');
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-type': 'application/octet-stream' });
      response.write(bytes.subarray(0, 9));
      response.end(bytes.subarray(9));
    });
    try {
      const destination = path.join(workspace, 'chunked-success.bin');
      const result = await saveTraceArtifactToPath(
        server.baseUrl,
        token,
        artifactOptions(bytes),
        destination,
        assertSessionActive);
      assert.deepEqual(await fs.readFile(destination), bytes);
      assert.equal(result.sizeBytes, bytes.byteLength);
      assert.equal(result.sha256, createHash('sha256').update(bytes).digest('hex'));
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('hash mismatch is rejected without creating a destination or temporary file', async () => {
  await withWorkspace(async workspace => {
    const bytes = Buffer.from('tampered evidence', 'utf8');
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-length': bytes.byteLength });
      response.end(bytes);
    });
    try {
      const destination = path.join(workspace, 'hash-mismatch.log');
      const options = artifactOptions(bytes);
      options.expectedSha256 = '0'.repeat(64);
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          options,
          destination,
          assertSessionActive),
        /SHA-256 does not match immutable metadata/u);
      await assert.rejects(fs.stat(destination), error => error?.code === 'ENOENT');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('size mismatch is rejected without creating a destination or temporary file', async () => {
  await withWorkspace(async workspace => {
    const bytes = Buffer.from('wrong byte count', 'utf8');
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-length': bytes.byteLength });
      response.end(bytes);
    });
    try {
      const destination = path.join(workspace, 'size-mismatch.log');
      const options = artifactOptions(bytes);
      options.expectedSizeBytes += 1;
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          options,
          destination,
          assertSessionActive),
        /Content-Length does not match immutable metadata/u);
      await assert.rejects(fs.stat(destination), error => error?.code === 'ENOENT');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('chunked oversize response is canceled without buffering or leaving files', async () => {
  await withWorkspace(async workspace => {
    const expectedBytes = Buffer.from('bounded evidence', 'utf8');
    const oversizedChunk = Buffer.alloc(64 * 1024, 0x41);
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-type': 'application/octet-stream' });
      response.write(oversizedChunk);
      response.end();
    });
    try {
      const destination = path.join(workspace, 'chunked-oversize.bin');
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(expectedBytes),
          destination,
          assertSessionActive),
        /byte count exceeds immutable metadata/u);
      await assert.rejects(fs.stat(destination), error => error?.code === 'ENOENT');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('chunked underflow preserves the prior destination and removes the partial temporary file', async () => {
  await withWorkspace(async workspace => {
    const expectedBytes = Buffer.from('complete immutable evidence', 'utf8');
    const partialBytes = expectedBytes.subarray(0, 8);
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-type': 'application/octet-stream' });
      response.end(partialBytes);
    });
    try {
      const destination = path.join(workspace, 'chunked-underflow.bin');
      await fs.writeFile(destination, 'prior verified evidence', 'utf8');
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(expectedBytes),
          destination,
          assertSessionActive),
        /byte count does not match immutable metadata/u);
      assert.equal(await fs.readFile(destination, 'utf8'), 'prior verified evidence');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('aborted response stream preserves the prior destination and removes partial bytes', async () => {
  await withWorkspace(async workspace => {
    const expectedBytes = Buffer.alloc(128 * 1024, 0x42);
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-type': 'application/octet-stream' });
      response.write(expectedBytes.subarray(0, 32 * 1024));
      setTimeout(() => response.destroy(new Error('intentional stream abort')), 25);
    });
    try {
      const destination = path.join(workspace, 'aborted-stream.bin');
      await fs.writeFile(destination, 'prior verified evidence', 'utf8');
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(expectedBytes),
          destination,
          assertSessionActive));
      assert.equal(await fs.readFile(destination, 'utf8'), 'prior verified evidence');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('HTTP redirect is rejected without following it or leaving files', async () => {
  await withWorkspace(async workspace => {
    let redirectedRequestCount = 0;
    const bytes = Buffer.from('redirected evidence', 'utf8');
    const server = await startArtifactServer((request, response) => {
      if (request.url === '/redirected') {
        redirectedRequestCount += 1;
        response.writeHead(200, { 'content-length': bytes.byteLength });
        response.end(bytes);
        return;
      }
      response.writeHead(302, { location: '/redirected' });
      response.end();
    });
    try {
      const destination = path.join(workspace, 'redirect.log');
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(bytes),
          destination,
          assertSessionActive),
        /fetch failed/u);
      assert.equal(redirectedRequestCount, 0);
      await assert.rejects(fs.stat(destination), error => error?.code === 'ENOENT');
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

test('hard-linked, directory, and linked-parent destinations are rejected safely', async () => {
  await withWorkspace(async workspace => {
    const bytes = Buffer.from('evidence', 'utf8');
    const server = await startArtifactServer((_request, response) => {
      response.writeHead(200, { 'content-length': bytes.byteLength });
      response.end(bytes);
    });
    try {
      const original = path.join(workspace, 'original.log');
      const hardLink = path.join(workspace, 'hard-link.log');
      await fs.writeFile(original, 'protected original', 'utf8');
      await fs.link(original, hardLink);
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(bytes),
          hardLink,
          assertSessionActive),
        /destination must be one regular file/u);
      assert.equal(await fs.readFile(original, 'utf8'), 'protected original');
      assert.equal(await fs.readFile(hardLink, 'utf8'), 'protected original');
      await assertNoTemporaryFiles(workspace);

      const directoryDestination = path.join(workspace, 'directory-target');
      await fs.mkdir(directoryDestination);
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(bytes),
          directoryDestination,
          assertSessionActive),
        /destination must be one regular file/u);
      assert.equal((await fs.stat(directoryDestination)).isDirectory(), true);
      await assertNoTemporaryFiles(workspace);

      const realParent = path.join(workspace, 'real-parent');
      const linkedParent = path.join(workspace, 'linked-parent');
      await fs.mkdir(realParent);
      await fs.symlink(realParent, linkedParent, process.platform === 'win32' ? 'junction' : 'dir');
      const linkedDestination = path.join(linkedParent, 'linked.log');
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(bytes),
          linkedDestination,
          assertSessionActive),
        /destination parent must be a real directory/u);
      await assert.rejects(fs.stat(path.join(realParent, 'linked.log')), error => error?.code === 'ENOENT');
      await assertNoTemporaryFiles(realParent);
    } finally {
      await server.close();
    }
  });
});

test('expired backend session is rejected before an artifact credential leaves the process', async () => {
  await withWorkspace(async workspace => {
    let requestCount = 0;
    const bytes = Buffer.from('must not leave the process', 'utf8');
    const server = await startArtifactServer((_request, response) => {
      requestCount += 1;
      response.writeHead(500);
      response.end();
    });
    try {
      await assert.rejects(
        saveTraceArtifactToPath(
          server.baseUrl,
          token,
          artifactOptions(bytes),
          path.join(workspace, 'expired.log'),
          () => {
            throw new Error('backend session exited');
          }),
        /backend session exited/u);
      assert.equal(requestCount, 0);
      await assertNoTemporaryFiles(workspace);
    } finally {
      await server.close();
    }
  });
});

function artifactOptions(bytes) {
  return {
    storageKey,
    fileName: 'stdout.log',
    expectedSizeBytes: bytes.byteLength,
    expectedSha256: createHash('sha256').update(bytes).digest('hex')
  };
}

async function withWorkspace(action) {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), 'openlineops-trace-save-'));
  try {
    await action(workspace);
  } finally {
    await fs.rm(workspace, { force: true, recursive: true });
  }
}

async function assertNoTemporaryFiles(directory) {
  const entries = await fs.readdir(directory);
  assert.deepEqual(
    entries.filter(entry => entry.startsWith('.openlineops-trace-') && entry.endsWith('.tmp')),
    []);
}

async function startArtifactServer(handler) {
  const server = http.createServer(handler);
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  assert(address && typeof address !== 'string');
  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
    })
  };
}
