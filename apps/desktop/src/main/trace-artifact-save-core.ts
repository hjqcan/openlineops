import { createHash, randomBytes } from 'node:crypto';
import {
  existsSync,
  lstatSync
} from 'node:fs';
import { open, rename, rm, type FileHandle } from 'node:fs/promises';
import path from 'node:path';
import type {
  TraceArtifactSaveOptions,
  TraceArtifactSaveResult
} from '../shared/desktop-api.js';

const maximumArtifactBytes = 256 * 1024 * 1024;

export async function saveTraceArtifactToPath(
  apiBaseUrl: string,
  apiAccessToken: string,
  options: TraceArtifactSaveOptions,
  destinationPath: string,
  assertSessionActive: () => void
): Promise<TraceArtifactSaveResult> {
  validateTraceArtifactSaveOptions(options);
  if (!path.isAbsolute(destinationPath)) {
    throw new Error('Trace artifact destination must be an absolute path.');
  }

  const destination = path.normalize(destinationPath);
  validateDestination(destination);

  const artifactUrl = createArtifactUrl(apiBaseUrl, options.storageKey);
  assertSessionActive();
  const response = await fetch(artifactUrl, {
    method: 'GET',
    headers: { authorization: `Bearer ${apiAccessToken}` },
    redirect: 'error'
  });
  if (response.status !== 200) {
    await response.body?.cancel().catch(() => undefined);
    throw new Error(`Trace artifact download failed with HTTP ${response.status}.`);
  }

  const declaredLength = response.headers.get('content-length');
  if (declaredLength !== null
      && (!/^(0|[1-9]\d*)$/u.test(declaredLength)
        || Number(declaredLength) !== options.expectedSizeBytes)) {
    await response.body?.cancel().catch(() => undefined);
    throw new Error('Trace artifact Content-Length does not match immutable metadata.');
  }

  // Validate again immediately before creating the sibling temporary file. The
  // response is streamed directly to that file so neither a missing nor a false
  // Content-Length can force the Electron main process to buffer an artifact.
  validateDestination(destination);
  const parent = path.dirname(destination);
  const temporaryPath = path.join(
    parent,
    `.openlineops-trace-${randomBytes(12).toString('hex')}.tmp`);
  let temporaryFile: FileHandle | null = null;
  let sizeBytes = 0;
  const hash = createHash('sha256');
  try {
    temporaryFile = await open(temporaryPath, 'wx', 0o600);
    const reader = response.body?.getReader();
    if (!reader) {
      throw new Error('Trace artifact response has no readable body.');
    }

    let streamCompleted = false;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) {
          streamCompleted = true;
          break;
        }

        sizeBytes += chunk.value.byteLength;
        if (sizeBytes > options.expectedSizeBytes || sizeBytes > maximumArtifactBytes) {
          throw new Error('Trace artifact byte count exceeds immutable metadata.');
        }

        await writeAll(temporaryFile, chunk.value);
        hash.update(chunk.value);
      }
    } finally {
      if (!streamCompleted) {
        await reader.cancel('Trace artifact stream did not complete safely.').catch(() => undefined);
      }
      reader.releaseLock();
    }

    if (sizeBytes !== options.expectedSizeBytes) {
      throw new Error('Trace artifact byte count does not match immutable metadata.');
    }

    const sha256 = hash.digest('hex');
    if (sha256 !== options.expectedSha256) {
      throw new Error('Trace artifact SHA-256 does not match immutable metadata.');
    }

    await temporaryFile.sync();
    const completedFile = temporaryFile;
    temporaryFile = null;
    await completedFile.close();

    validateDestination(destination);
    await rename(temporaryPath, destination);
  } finally {
    try {
      if (temporaryFile !== null) {
        const incompleteFile = temporaryFile;
        temporaryFile = null;
        await incompleteFile.close();
      }
    } finally {
      await rm(temporaryPath, { force: true, recursive: false });
    }
  }

  return {
    canceled: false,
    path: destination,
    sizeBytes,
    sha256: options.expectedSha256
  };
}

async function writeAll(file: FileHandle, bytes: Uint8Array): Promise<void> {
  let offset = 0;
  while (offset < bytes.byteLength) {
    const result = await file.write(bytes, offset, bytes.byteLength - offset, null);
    if (result.bytesWritten <= 0) {
      throw new Error('Trace artifact temporary file write made no progress.');
    }
    offset += result.bytesWritten;
  }
}

export function validateTraceArtifactSaveOptions(options: TraceArtifactSaveOptions): void {
  if (!options || typeof options !== 'object') {
    throw new Error('Trace artifact save options are required.');
  }

  if (typeof options.storageKey !== 'string'
      || typeof options.fileName !== 'string'
      || typeof options.expectedSha256 !== 'string') {
    throw new Error('Trace artifact save metadata has invalid field types.');
  }

  const segments = options.storageKey.split('/');
  if (options.storageKey.length === 0
      || options.storageKey.length > 512
      || segments.some(segment => !/^[A-Za-z0-9._-]+$/u.test(segment)
        || segment === '.'
        || segment === '..')) {
    throw new Error('Trace artifact storage key is not canonical.');
  }

  if (options.fileName.length === 0
      || options.fileName.length > 255
      || path.basename(options.fileName) !== options.fileName
      || options.fileName === '.'
      || options.fileName === '..'
      || /[\u0000-\u001f<>:"/\\|?*]/u.test(options.fileName)
      || /[ .]$/u.test(options.fileName)
      || /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/iu.test(options.fileName)) {
    throw new Error('Trace artifact file name is not canonical.');
  }

  if (!Number.isSafeInteger(options.expectedSizeBytes)
      || options.expectedSizeBytes < 0
      || options.expectedSizeBytes > maximumArtifactBytes) {
    throw new Error('Trace artifact size is outside the supported evidence boundary.');
  }

  if (!/^[0-9a-f]{64}$/u.test(options.expectedSha256)) {
    throw new Error('Trace artifact SHA-256 is not canonical.');
  }
}

function validateDestination(destination: string): void {
  const parent = path.dirname(destination);
  const parentMetadata = lstatSync(parent);
  if (!parentMetadata.isDirectory() || parentMetadata.isSymbolicLink()) {
    throw new Error('Trace artifact destination parent must be a real directory.');
  }

  if (!existsSync(destination)) {
    return;
  }

  const destinationMetadata = lstatSync(destination);
  if (!destinationMetadata.isFile()
      || destinationMetadata.isSymbolicLink()
      || destinationMetadata.nlink !== 1) {
    throw new Error('Trace artifact destination must be one regular file.');
  }
}

function createArtifactUrl(apiBaseUrl: string, storageKey: string): URL {
  const base = new URL(`${apiBaseUrl.replace(/\/+$/u, '')}/`);
  if (base.protocol !== 'http:' && base.protocol !== 'https:') {
    throw new Error('Trace artifact API must use HTTP or HTTPS.');
  }

  const encodedKey = storageKey.split('/').map(encodeURIComponent).join('/');
  const url = new URL(`api/traceability/artifacts/${encodedKey}`, base);
  if (url.origin !== base.origin) {
    throw new Error('Trace artifact URL escaped the configured API origin.');
  }

  return url;
}
