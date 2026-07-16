import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  assertApplicationExtensionArchiveUnchanged,
  deriveApplicationExtensionPortableId,
  inspectApplicationExtensionArchive,
  maximumApplicationExtensionArchiveBytes
} from '../dist-electron/main/application-extension-import-security.js';

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mainSource = await fs.readFile(path.join(desktopRoot, 'src', 'main', 'main.ts'), 'utf8');
const preloadSource = await fs.readFile(path.join(desktopRoot, 'src', 'preload', 'preload.cts'), 'utf8');
const workbenchSource = await fs.readFile(
  path.join(desktopRoot, 'src', 'renderer', 'plugins-workbench.tsx'),
  'utf8');

test('extension archive inspection accepts one canonical regular ZIP and derives a stable portable id', async () => {
  await withTempDirectory(async directory => {
    const archivePath = path.join(directory, 'OpenLineOps.Sample.Plugin.zip');
    await fs.writeFile(archivePath, Buffer.from('PK\u0003\u0004fixture'));
    const identity = inspectApplicationExtensionArchive(archivePath);
    assert.equal(identity.path, archivePath);
    assert.equal(identity.fileName, 'OpenLineOps.Sample.Plugin.zip');
    assert.equal(identity.sizeBytes, 11);
    assert.doesNotThrow(() => assertApplicationExtensionArchiveUnchanged(identity));
    assert.equal(
      deriveApplicationExtensionPortableId(
        identity.fileName,
        '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'),
      'openlineops.sample.plugin');
  });
});

test('extension archive inspection rejects relative paths, non-ZIP files, empty files, and oversized files', async () => {
  await withTempDirectory(async directory => {
    const textPath = path.join(directory, 'package.txt');
    await fs.writeFile(textPath, 'not a zip');
    assert.throws(() => inspectApplicationExtensionArchive('package.zip'), /canonical absolute local path/u);
    assert.throws(() => inspectApplicationExtensionArchive(textPath), /\.zip archive/u);

    const emptyPath = path.join(directory, 'empty.zip');
    await fs.writeFile(emptyPath, '');
    assert.throws(() => inspectApplicationExtensionArchive(emptyPath), /non-linked regular ZIP/u);

    const largePath = path.join(directory, 'large.zip');
    await fs.writeFile(largePath, 'PK');
    await fs.truncate(largePath, maximumApplicationExtensionArchiveBytes + 1);
    assert.throws(() => inspectApplicationExtensionArchive(largePath), /256 MiB/u);
  });
});

test('extension archive inspection rejects hard links and detects post-selection mutation', async () => {
  await withTempDirectory(async directory => {
    const archivePath = path.join(directory, 'package.zip');
    await fs.writeFile(archivePath, 'PK fixture');
    const identity = inspectApplicationExtensionArchive(archivePath);
    await fs.appendFile(archivePath, ' changed');
    assert.throws(
      () => assertApplicationExtensionArchiveUnchanged(identity),
      /changed while it was being prepared/u);

    const hardLink = path.join(directory, 'package-copy.zip');
    await fs.link(archivePath, hardLink);
    assert.throws(() => inspectApplicationExtensionArchive(archivePath), /non-linked regular ZIP/u);
    assert.throws(() => inspectApplicationExtensionArchive(hardLink), /non-linked regular ZIP/u);
  });
});

test('extension import IPC owns file selection and streams fixed multipart fields to one scoped route', () => {
  assert.match(
    mainSource,
    /ipcMain\.handle\('api:import-application-extension',[\s\S]*?projectId: string,[\s\S]*?applicationId: string[\s\S]*?assertTrustedRendererIpcSender\(event\)[\s\S]*?selectAndImportApplicationExtension\(projectId, applicationId\)/u);
  assert.match(mainSource, /properties: \['openFile'\][\s\S]*?extensions: \['zip'\]/u);
  assert.match(mainSource, /inspectApplicationExtensionArchive\(selectedPath\)/u);
  assert.match(mainSource, /calculateFileSha256\(archive\.path\)/u);
  assert.match(mainSource, /form\.set\('portableId', portableId\)/u);
  assert.match(mainSource, /form\.set\('package', await openAsBlob\(archive\.path\), archive\.fileName\)/u);
  assert.match(
    mainSource,
    /automation-projects\/\$\{encodeURIComponent\(projectId\)\}[\s\S]*?applications\/\$\{encodeURIComponent\(applicationId\)\}\/extensions\/import/u);
  assert.match(
    mainSource,
    /assertSameActiveBackendSession\(initialSession\);[\s\S]*?await response\.text\(\);[\s\S]*?assertSameActiveBackendSession\(initialSession\)/u);
  assert.match(
    preloadSource,
    /importApplicationExtension: <T = unknown,>\(projectId: string, applicationId: string\)[\s\S]*?'api:import-application-extension',[\s\S]*?projectId,[\s\S]*?applicationId/u);
  assert.doesNotMatch(
    preloadSource,
    /importApplicationExtension:[^\n]*sourcePath/u,
    'the renderer bridge must never accept a local extension archive path');
});

test('extension hash preview uses an intact UTF-8 ellipsis and contains no mojibake marker', () => {
  assert.match(
    workbenchSource,
    /`\$\{value\.slice\(0, 10\)\}…\$\{value\.slice\(-6\)\}`/u);
  assert.doesNotMatch(workbenchSource, /鈥/u);
});

async function withTempDirectory(action) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'openlineops-extension-import-test-'));
  try {
    await action(directory);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
}
