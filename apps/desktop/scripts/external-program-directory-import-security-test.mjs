import assert from 'node:assert/strict';
import fsSync from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { runBoundedChildProcess } from '../dist-electron/main/bounded-child-process.js';
import {
  assertExternalProgramDirectoryRelativePaths,
  assertExternalProgramDirectoryUnchanged,
  calculateBoundedFileSha256,
  inspectExternalProgramDirectory,
  maximumExternalProgramDirectoryCount,
  maximumExternalProgramDirectoryFileCount,
  maximumExternalProgramDirectoryFileBytes
} from '../dist-electron/main/external-program-directory-import-security.js';

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mainSource = await fs.readFile(path.join(desktopRoot, 'src', 'main', 'main.ts'), 'utf8');
const preloadSource = await fs.readFile(path.join(desktopRoot, 'src', 'preload', 'preload.cts'), 'utf8');
const directorySecuritySource = await fs.readFile(
  path.join(desktopRoot, 'src', 'main', 'external-program-directory-import-security.ts'),
  'utf8');
const selectionFunctionSource = mainSource.slice(
  mainSource.indexOf('async function selectExternalProgramDirectory('),
  mainSource.indexOf('async function importExternalProgramDirectory<'));

test('directory inspection preserves nested paths and same basenames in distinct directories', async () => {
  await withTempDirectory(async root => {
    await writeFixture(root);

    const identity = await inspectExternalProgramDirectory(root);

    assert.deepEqual(identity.files.map(file => file.relativePath), [
      'bin/vendor-helper.exe',
      'config/shared.settings.json',
      'lib/shared.settings.json'
    ]);
    assert.deepEqual(identity.files.map(file => file.resourceRelativePath), [
      'files/bin/vendor-helper.exe',
      'files/config/shared.settings.json',
      'files/lib/shared.settings.json'
    ]);
    assert.equal(identity.totalBytes, identity.files.reduce((total, file) => total + file.sizeBytes, 0));
    assert.ok(identity.files.every(file => /^[a-f0-9]{64}$/u.test(file.sha256)));
    await assertExternalProgramDirectoryUnchanged(identity);
  });
});

test('directory inspection detects mutation and physical-root replacement before upload', async () => {
  await withTempDirectory(async parent => {
    const root = path.join(parent, 'program');
    await fs.mkdir(root);
    await writeFixture(root);
    const identity = await inspectExternalProgramDirectory(root);

    await fs.appendFile(path.join(root, 'config', 'shared.settings.json'), 'changed');
    await assert.rejects(
      assertExternalProgramDirectoryUnchanged(identity),
      /changed while it was being prepared/u);

    await fs.rm(root, { recursive: true, force: true });
    await fs.mkdir(root);
    await writeFixture(root);
    await assert.rejects(
      assertExternalProgramDirectoryUnchanged(identity),
      /changed while it was being prepared/u);
  });
});

test('bounded hashing reads exactly the declared bytes and probes EOF', async () => {
  await withTempDirectory(async root => {
    const filePath = path.join(root, 'helper.exe');
    await fs.writeFile(filePath, 'abc');
    assert.equal(
      await calculateBoundedFileSha256(filePath, 3),
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    await assert.rejects(calculateBoundedFileSha256(filePath, 2), /changed while it was hashed/u);
    await assert.rejects(calculateBoundedFileSha256(filePath, 4), /shorter while it was hashed/u);
  });
});

test('bounded child process fails closed on timeout, output overflow, and non-zero exit', async () => {
  await assert.rejects(
    runBoundedChildProcess(childRequest(
      ['-e', 'setTimeout(() => {}, 10_000);'],
      { timeoutMs: 50 })),
    /timed out after 50 ms/u);

  await assert.rejects(
    runBoundedChildProcess(childRequest(
      ['-e', "process.stdout.write('x'.repeat(4096));"],
      { maximumStdoutBytes: 128, maximumTotalOutputBytes: 256 })),
    /exceeded its output limit/u);

  await assert.rejects(
    runBoundedChildProcess(childRequest(
      ['-e', "process.stdout.write('x'.repeat(80)); process.stderr.write('y'.repeat(80));"],
      { maximumStdoutBytes: 128, maximumStderrBytes: 128, maximumTotalOutputBytes: 100 })),
    /exceeded its output limit/u);

  await assert.rejects(
    runBoundedChildProcess(childRequest(
      ['-e', "process.stderr.write('expected rejection'); process.exit(7);"])),
    /exit code 7: expected rejection/u);
});

test('directory inspection rejects hard links, reparse points, alternate streams, and oversized files', async context => {
  await withTempDirectory(async root => {
    const regular = path.join(root, 'vendor-helper.exe');
    const hardLink = path.join(root, 'vendor-helper-copy.exe');
    await fs.writeFile(regular, 'helper');
    await fs.link(regular, hardLink);
    await assert.rejects(inspectExternalProgramDirectory(root), /hard link/u);
  });

  await withTempDirectory(async parent => {
    const root = path.join(parent, 'program');
    const outside = path.join(parent, 'outside');
    await fs.mkdir(root);
    await fs.mkdir(outside);
    await fs.writeFile(path.join(root, 'vendor-helper.exe'), 'helper');
    await fs.writeFile(path.join(outside, 'payload.dll'), 'payload');
    await fs.symlink(outside, path.join(root, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(inspectExternalProgramDirectory(root), /symbolic link or reparse point/u);
  });

  await withTempDirectory(async root => {
    const oversized = path.join(root, 'oversized.exe');
    await fs.writeFile(oversized, 'x');
    await fs.truncate(oversized, maximumExternalProgramDirectoryFileBytes + 1);
    await assert.rejects(inspectExternalProgramDirectory(root), /512 MiB/u);
  });

  if (process.platform === 'win32') {
    await withTempDirectory(async root => {
      const regular = path.join(root, 'vendor-helper.exe');
      await fs.writeFile(regular, 'helper');
      await fs.writeFile(`${regular}:OpenLineOpsMetadata`, 'hidden');
      await assert.rejects(inspectExternalProgramDirectory(root), /alternate-data-stream/u);
    });
  } else {
    context.diagnostic('Alternate data streams are a Windows-only boundary.');
  }
});

test('portable path validation rejects case collisions, reserved names, trailing aliases, invalid characters, and non-NFC text', () => {
  assert.doesNotThrow(() => assertExternalProgramDirectoryRelativePaths([
    'bin/vendor-helper.exe',
    'config/shared.settings.json',
    'lib/shared.settings.json'
  ]));
  assert.throws(
    () => assertExternalProgramDirectoryRelativePaths(['lib/Shared.dll', 'lib/shared.dll']),
    /case-insensitive path collision/u);
  for (const invalid of [
    'CON.txt',
    'config./settings.json',
    'lib/invalid?.dll',
    'lib/e\u0301/settings.json',
    'lib/trailing-space /settings.json'
  ]) {
    assert.throws(
      () => assertExternalProgramDirectoryRelativePaths([invalid]),
      /canonical portable relative path/u,
      invalid);
  }
});

test('directory inspection accepts the directory-count boundary and rejects the next directory', async () => {
  await withTempDirectory(async root => {
    await fs.writeFile(path.join(root, 'vendor-helper.exe'), 'helper');
    for (let index = 1; index < maximumExternalProgramDirectoryCount; index++) {
      await fs.mkdir(path.join(root, `empty-${index.toString().padStart(4, '0')}`));
    }

    const identity = await inspectExternalProgramDirectory(root);
    assert.equal(identity.files.length, 1);

    await fs.mkdir(path.join(root, 'one-too-many'));
    await assert.rejects(
      inspectExternalProgramDirectory(root),
      new RegExp(`${maximumExternalProgramDirectoryCount}-directory`, 'u'));
  });
});

test('directory inspection accepts the file-count boundary and rejects the next file', async () => {
  await withTempDirectory(async root => {
    for (let index = 0; index < maximumExternalProgramDirectoryFileCount; index++) {
      await fs.writeFile(path.join(root, `file-${index.toString().padStart(4, '0')}.bin`), 'x');
    }

    const identity = await inspectExternalProgramDirectory(root);
    assert.equal(identity.files.length, maximumExternalProgramDirectoryFileCount);

    await fs.writeFile(path.join(root, 'one-too-many.bin'), 'x');
    await assert.rejects(
      inspectExternalProgramDirectory(root),
      new RegExp(`${maximumExternalProgramDirectoryFileCount}-file`, 'u'));
  });
});

test('privileged IPC binds bounded one-shot selections to backend session, Application, and resource identity', () => {
  assert.match(mainSource, /maximumPendingExternalProgramDirectories = 16/u);
  assert.match(mainSource, /pendingExternalProgramDirectories\.size >= maximumPendingExternalProgramDirectories/u);
  assert.match(mainSource, /pending\.projectId !== projectId[\s\S]*?pending\.applicationId !== applicationId[\s\S]*?pending\.resourceId !== resourceId/u);
  assert.match(mainSource, /if \(pending\.inFlight\)[\s\S]*?already being imported/u);
  assert.match(mainSource, /pending\.inFlight = true/u);
  assert.match(mainSource, /if \(response\.ok\) \{[\s\S]*?pendingExternalProgramDirectories\.delete\(selectionId\)/u);
  assert.match(mainSource, /activeBackendSession = null;\s*pendingExternalProgramDirectories\.clear\(\)/u);
  assert.match(mainSource, /desktop:release-external-program-directory-selection/u);
  assert.match(mainSource, /assertExternalProgramDirectoryUnchanged\(pending\.identity\)[\s\S]*?openAsBlob[\s\S]*?assertExternalProgramDirectoryUnchanged\(pending\.identity\)/u);
  assert.doesNotMatch(selectionFunctionSource, /pendingExternalProgramDirectories\.clear\(\)/u);
  assert.doesNotMatch(mainSource, /selectExternalProgramFiles|uploadExternalProgram|api:upload-external-program/u);
  assert.doesNotMatch(preloadSource, /selectExternalProgramFiles|uploadExternalProgram|api:upload-external-program/u);
  assert.match(directorySecuritySource, /await runBoundedChildProcess\(\{/u);
  assert.doesNotMatch(directorySecuritySource, /spawnSync/u);
});

async function writeFixture(root) {
  await fs.mkdir(path.join(root, 'bin'), { recursive: true });
  await fs.mkdir(path.join(root, 'config'), { recursive: true });
  await fs.mkdir(path.join(root, 'lib'), { recursive: true });
  await fs.writeFile(path.join(root, 'bin', 'vendor-helper.exe'), 'helper');
  await fs.writeFile(path.join(root, 'config', 'shared.settings.json'), 'config');
  await fs.writeFile(path.join(root, 'lib', 'shared.settings.json'), 'library');
}

async function withTempDirectory(action) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'openlineops-program-directory-test-'));
  try {
    await action(directory);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
}

function childRequest(arguments_, overrides = {}) {
  return {
    executablePath: process.execPath,
    arguments: arguments_,
    cwd: desktopRoot,
    environment: { ...process.env },
    input: '{}',
    maximumInputBytes: 1024,
    maximumStdoutBytes: 1024,
    maximumStderrBytes: 1024,
    maximumTotalOutputBytes: 2048,
    timeoutMs: 5000,
    ...overrides
  };
}
