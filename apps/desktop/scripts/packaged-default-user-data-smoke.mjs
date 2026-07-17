import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { ElectronCdpHarness } from './electron-cdp-harness.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const packagedExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'OpenLineOps.exe');
const logs = [];

if (process.platform !== 'win32') {
  throw new Error('The packaged default user-data smoke requires Windows.');
}
if (process.env.OPENLINEOPS_ALLOW_DEFAULT_USER_DATA_SMOKE !== '1') {
  throw new Error(
    'Set OPENLINEOPS_ALLOW_DEFAULT_USER_DATA_SMOKE=1 to run against the current disposable Windows profile.');
}
const configuredAppDataRoot = process.env.APPDATA;
if (!configuredAppDataRoot || !path.isAbsolute(configuredAppDataRoot)) {
  throw new Error('APPDATA must identify the current absolute Windows roaming profile path.');
}

const physicalTempRoot = await fs.realpath(os.tmpdir());
const executionRoot = await fs.mkdtemp(
  path.join(physicalTempRoot, 'openlineops-default-user-data-'));
const roamingAppDataRoot = await fs.realpath(configuredAppDataRoot);
const expectedUserDataDirectory = path.join(roamingAppDataRoot, 'OpenLineOps');
const scopedNameDirectory = path.join(roamingAppDataRoot, '@openlineops');
const scopedNameIdentityBefore = await captureTreeIdentity(scopedNameDirectory);

const harness = new ElectronCdpHarness({
  executablePath: packagedExecutable,
  workingDirectory: path.dirname(packagedExecutable),
  userDataDirectory: null,
  environment: {
    OPENLINEOPS_DESKTOP_LOG_PATH: path.join(executionRoot, 'logs')
  },
  logs
});

let succeeded = false;
try {
  await harness.start();
  const initialBackend = await harness.evaluate(
    'window.openlineopsDesktop.getBackendStatus()');
  if (!initialBackend.isRunning || initialBackend.health !== 'Healthy') {
    await harness.evaluate('window.openlineopsDesktop.startBackend()');
  }
  await harness.waitFor(
    '(async () => (await window.openlineopsDesktop.getBackendStatus()).health === "Healthy")()',
    90_000,
    'the packaged backend started with Electron default user data');

  const expectedUserData = await fs.stat(expectedUserDataDirectory).catch(() => null);
  if (!expectedUserData?.isDirectory()) {
    const derivedEntries = await fs.readdir(roamingAppDataRoot).catch(() => []);
    throw new Error(
      `Electron did not derive the canonical OpenLineOps user-data directory. Found: ${JSON.stringify(derivedEntries)}`);
  }
  const runtimeState = await fs.stat(
    path.join(expectedUserDataDirectory, 'data', 'runtime-state')).catch(() => null);
  if (!runtimeState?.isDirectory()) {
    throw new Error(
      'The packaged backend did not bind runtime state below the default OpenLineOps user-data directory.');
  }
  const scopedNameIdentityAfter = await captureTreeIdentity(scopedNameDirectory);
  if (scopedNameIdentityAfter !== scopedNameIdentityBefore) {
    throw new Error(
      'Electron changed the scoped npm-name directory instead of using productName user data.');
  }

  succeeded = true;
  console.log(
    `Packaged Electron default user-data smoke passed: ${expectedUserDataDirectory}`);
} catch (error) {
  const diagnostics = logs.slice(-80).join('\n');
  throw new Error(
    `${error instanceof Error ? error.message : String(error)}\n${diagnostics}`,
    { cause: error });
} finally {
  await harness.close();
  if (succeeded) {
    await fs.rm(executionRoot, { recursive: true, force: true });
  }
}

async function captureTreeIdentity(root) {
  const metadata = await fs.lstat(root).catch(error => {
    if (error?.code === 'ENOENT') return null;
    throw error;
  });
  if (metadata === null) return 'missing';
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    return `unsafe:${metadata.mode}:${metadata.size}:${metadata.mtimeMs}`;
  }

  const entries = [];
  const pending = [{ absolute: root, relative: '.' }];
  while (pending.length > 0) {
    const current = pending.pop();
    const children = await fs.readdir(current.absolute, { withFileTypes: true });
    children.sort((left, right) => left.name.localeCompare(right.name, 'en'));
    for (const child of children) {
      const absolute = path.join(current.absolute, child.name);
      const relative = path.join(current.relative, child.name);
      const childMetadata = await fs.lstat(absolute);
      entries.push([
        relative,
        childMetadata.mode,
        childMetadata.size,
        childMetadata.mtimeMs,
        childMetadata.isDirectory(),
        childMetadata.isSymbolicLink()
      ]);
      if (childMetadata.isDirectory() && !childMetadata.isSymbolicLink()) {
        pending.push({ absolute, relative });
      }
    }
  }
  return JSON.stringify(entries);
}
