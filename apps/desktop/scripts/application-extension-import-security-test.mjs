import assert from 'node:assert/strict';
import fsSync from 'node:fs';
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
    const physicalPath = fsSync.realpathSync.native(archivePath);
    const physicalMetadata = fsSync.lstatSync(physicalPath, { bigint: true });
    assert.equal(identity.path, physicalPath);
    assert.equal(identity.fileName, 'OpenLineOps.Sample.Plugin.zip');
    assert.equal(identity.sizeBytes, 11);
    assert.equal(identity.device, physicalMetadata.dev);
    assert.equal(identity.inode, physicalMetadata.ino);
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

test('extension archive inspection trusts physical identity instead of native path spelling', async context => {
  await withTempDirectory(async directory => {
    const archivePath = path.join(directory, 'package.zip');
    const otherPath = path.join(directory, 'other.zip');
    await fs.writeFile(archivePath, 'PK fixture');
    await fs.writeFile(otherPath, 'PK other fixture');

    const selectedMetadata = fsSync.lstatSync(archivePath, { bigint: true });
    const otherMetadata = fsSync.lstatSync(otherPath, { bigint: true });
    const physicalPath = process.platform === 'win32'
      ? 'Z:\\openlineops-physical\\package.zip'
      : '/openlineops-physical/package.zip';
    const originalRealpath = fsSync.realpathSync.native;
    const originalLstat = fsSync.lstatSync;
    context.mock.method(fsSync.realpathSync, 'native', value => (
      value === archivePath ? physicalPath : originalRealpath(value)));
    const lstatMock = context.mock.method(fsSync, 'lstatSync', (value, options) => {
      if (value === physicalPath) {
        return selectedMetadata;
      }

      return originalLstat(value, options);
    });

    const identity = inspectApplicationExtensionArchive(archivePath);
    assert.equal(identity.path, physicalPath);
    assert.equal(identity.inode, selectedMetadata.ino);

    lstatMock.mock.mockImplementation((value, options) => {
      if (value === physicalPath) {
        return otherMetadata;
      }

      return originalLstat(value, options);
    });
    assert.throws(
      () => inspectApplicationExtensionArchive(archivePath),
      /different physical file/u);
  });
});

test('extension archive inspection rejects a physical path in a remote or device namespace', async context => {
  if (process.platform !== 'win32') {
    context.skip('Windows namespace validation applies only on Windows.');
    return;
  }

  await withTempDirectory(async directory => {
    const archivePath = path.join(directory, 'package.zip');
    await fs.writeFile(archivePath, 'PK fixture');
    const realpathMock = context.mock.method(
      fsSync.realpathSync,
      'native',
      () => '\\\\server\\share\\package.zip');
    assert.throws(
      () => inspectApplicationExtensionArchive(archivePath),
      /canonical local path/u);

    realpathMock.mock.mockImplementation(() => '\\\\?\\C:\\package.zip');
    assert.throws(
      () => inspectApplicationExtensionArchive(archivePath),
      /canonical local path/u);
  });
});

test('extension archive inspection rejects a linked leaf and anchors a parent alias', async context => {
  await withTempDirectory(async directory => {
    const physicalDirectory = path.join(directory, 'physical');
    const redirectedDirectory = path.join(directory, 'redirected');
    const linkedLeaf = path.join(directory, 'linked.zip');
    const linkedFile = path.join(directory, 'linked-file.zip');
    await fs.mkdir(physicalDirectory);
    const archivePath = path.join(physicalDirectory, 'package.zip');
    await fs.writeFile(archivePath, 'PK fixture');
    await fs.symlink(
      physicalDirectory,
      redirectedDirectory,
      process.platform === 'win32' ? 'junction' : 'dir');
    await fs.symlink(
      physicalDirectory,
      linkedLeaf,
      process.platform === 'win32' ? 'junction' : 'dir');

    const identity = inspectApplicationExtensionArchive(path.join(redirectedDirectory, 'package.zip'));
    assert.equal(identity.path, fsSync.realpathSync.native(archivePath));
    assert.doesNotThrow(() => assertApplicationExtensionArchiveUnchanged(identity));
    assert.throws(() => inspectApplicationExtensionArchive(linkedLeaf), /non-linked regular ZIP/u);

    try {
      await fs.symlink(archivePath, linkedFile, 'file');
    } catch (error) {
      if (process.platform === 'win32' && error?.code === 'EPERM') {
        context.diagnostic('Windows file symlink privilege is unavailable; junction coverage remains active.');
        return;
      }
      throw error;
    }
    assert.throws(() => inspectApplicationExtensionArchive(linkedFile), /non-linked regular ZIP/u);
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
