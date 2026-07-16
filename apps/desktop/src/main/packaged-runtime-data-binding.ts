import {
  closeSync,
  fsyncSync,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  writeFileSync
} from 'node:fs';
import type { Stats } from 'node:fs';
import * as nodeFileSystem from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import path from 'node:path';
import process from 'node:process';

const packageFileSystem: typeof nodeFileSystem = process.versions.electron
  ? createRequire(import.meta.url)('original-fs') as typeof nodeFileSystem
  : nodeFileSystem;

const stateDirectoryName = 'runtime-state';
const markerFileName = 'runtime-content-binding.json';
const activationCommitFileName = 'runtime-activation-committed.json';
const previousStateDirectoryName = '.runtime-state.previous';
const discardedStatePrefix = '.runtime-state.discarded-';
const stagedStatePrefix = '.runtime-state.new-';
const packageContentManifestFileName = 'openlineops-package-content.json';
const sha256Pattern = /^[a-f0-9]{64}$/;
const requiredPackageContentPaths = [
  'OpenLineOps.exe',
  'resources/app/dist/index.html',
  'resources/app/dist-electron/main/main.js',
  'resources/app/dist-electron/preload/preload.cjs',
  'resources/app/package.json',
  'resources/app/runtime/api/OpenLineOps.Api.exe',
  'resources/app/runtime/api/OpenLineOps.Api.dll',
  'resources/app/runtime/api/OpenLineOps.Api.deps.json',
  'resources/app/runtime/api/OpenLineOps.Api.runtimeconfig.json',
  'resources/app/runtime/api/appsettings.json',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.dll',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.deps.json',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.runtimeconfig.json',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.dll',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.deps.json',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.runtimeconfig.json'
] as const;

interface RuntimeContentBinding {
  schema: 'openlineops.desktop-runtime-content-binding';
  runtimeSha256: string;
  activationId: string;
}

interface RuntimeActivationCommit {
  schema: 'openlineops.desktop-runtime-activation';
  runtimeSha256: string;
  activationId: string;
}

interface PackageContentManifestEntry {
  path: string;
  sha256: string;
  size: number;
}

export interface PackagedRuntimeDataBindingResult {
  reset: boolean;
  runtimeSha256: string;
  runtimeStateDirectory: string;
  verify(): void;
  commit(): void;
  rollback(): void;
}

export function validatePackagedContentUserDataSeparation(
  packagedContentDirectory: string,
  userDataDirectory: string
): void {
  const canonicalPackagedContentDirectory = requireCanonicalDirectory(
    packagedContentDirectory,
    'Packaged content');
  const normalizedUserDataDirectory = normalizeCanonicalAbsolutePath(
    userDataDirectory,
    'Desktop user data');
  if (pathsOverlap(canonicalPackagedContentDirectory, normalizedUserDataDirectory)) {
    throw new Error(
      'Packaged content and Desktop user data directories must not contain one another.');
  }
}

export function ensureCanonicalDesktopUserDataDirectory(userDataDirectory: string): string {
  if (!path.isAbsolute(userDataDirectory)
      || path.resolve(userDataDirectory) !== path.normalize(userDataDirectory)) {
    throw new Error('Desktop user data path must be canonical and absolute.');
  }
  const missingDirectories: string[] = [];
  let existingAncestor = path.resolve(userDataDirectory);
  while (readPathMetadata(existingAncestor) === null) {
    missingDirectories.push(existingAncestor);
    const parent = path.dirname(existingAncestor);
    if (parent === existingAncestor) {
      throw new Error('Desktop user data has no existing canonical ancestor.');
    }
    existingAncestor = parent;
  }
  requireCanonicalDirectory(existingAncestor, 'Desktop user data ancestor');
  for (const missingDirectory of missingDirectories.reverse()) {
    mkdirSync(missingDirectory, { recursive: false, mode: 0o700 });
    requireCanonicalDirectory(missingDirectory, 'Desktop user data');
  }
  return requireCanonicalDirectory(userDataDirectory, 'Desktop user data');
}

export function ensurePackagedRuntimeDataBinding(
  packagedContentDirectory: string,
  userDataDirectory: string
): PackagedRuntimeDataBindingResult {
  validatePackagedContentUserDataSeparation(
    packagedContentDirectory,
    userDataDirectory);
  const canonicalPackagedContentDirectory = requireCanonicalDirectory(
    packagedContentDirectory,
    'Packaged content');
  const canonicalUserDataDirectory = ensureCanonicalDesktopUserDataDirectory(
    userDataDirectory);
  const dataDirectory = path.join(canonicalUserDataDirectory, 'data');
  if (readPathMetadata(dataDirectory) === null) {
    mkdirSync(dataDirectory, { recursive: false, mode: 0o700 });
  }
  const canonicalDataDirectory = requireCanonicalDirectory(
    dataDirectory,
    'Desktop data');
  const runtimeSha256 = computeRuntimeContentSha256(canonicalPackagedContentDirectory);
  recoverInterruptedActivation(canonicalDataDirectory, runtimeSha256);
  const runtimeStateDirectory = resolveDataChild(
    canonicalDataDirectory,
    stateDirectoryName);
  const previousStateDirectory = resolveDataChild(
    canonicalDataDirectory,
    previousStateDirectoryName);

  const existingState = readPathMetadata(runtimeStateDirectory);
  if (existingState !== null) {
    requirePlainDirectory(runtimeStateDirectory, 'Desktop runtime state');
    const markerPath = path.join(runtimeStateDirectory, markerFileName);
    const existingBinding = readBinding(markerPath);
    if (existingBinding?.runtimeSha256 === runtimeSha256
        && isCommittedRuntimeState(runtimeStateDirectory, existingBinding)) {
      cleanupInactiveStates(canonicalDataDirectory);
      assertPackagedContentUnchanged(
        canonicalPackagedContentDirectory,
        runtimeSha256);
      return createCommittedBindingResult(
        canonicalPackagedContentDirectory,
        canonicalDataDirectory,
        runtimeStateDirectory,
        existingBinding);
    }
    requireRemovableTree(runtimeStateDirectory);
  }

  const binding: RuntimeContentBinding = {
    schema: 'openlineops.desktop-runtime-content-binding',
    runtimeSha256,
    activationId: randomUUID()
  };
  const stagedStateDirectory = createStagedRuntimeState(
    canonicalDataDirectory,
    binding);
  try {
    assertPackagedContentUnchanged(
      canonicalPackagedContentDirectory,
      runtimeSha256);
  } catch (error) {
    removeBoundPath(stagedStateDirectory);
    throw error;
  }
  if (existingState !== null) {
    moveActiveStateToPrevious(
      canonicalDataDirectory,
      runtimeStateDirectory,
      previousStateDirectory);
  }
  requireCanonicalDirectory(canonicalDataDirectory, 'Desktop data');
  if (readPathMetadata(runtimeStateDirectory) !== null) {
    throw new Error('Desktop runtime state changed concurrently before activation.');
  }
  renameSync(stagedStateDirectory, runtimeStateDirectory);
  requirePlainDirectory(runtimeStateDirectory, 'Desktop runtime state');
  if (readBinding(path.join(runtimeStateDirectory, markerFileName))?.runtimeSha256
      !== runtimeSha256) {
    throw new Error('Activated desktop runtime state did not preserve its content binding.');
  }
  requireCanonicalDirectory(canonicalDataDirectory, 'Desktop data');
  const result = createPendingBindingResult(
    canonicalPackagedContentDirectory,
    canonicalDataDirectory,
    runtimeStateDirectory,
    previousStateDirectory,
    binding);
  try {
    result.verify();
    return result;
  } catch (error) {
    try {
      result.rollback();
    } catch (rollbackError) {
      throw new AggregateError(
        [error, rollbackError],
        'Packaged content changed and the pending runtime state could not be rolled back.');
    }
    throw error;
  }
}

function assertPackagedContentUnchanged(
  packagedContentDirectory: string,
  expectedSha256: string
): void {
  let observedSha256: string;
  try {
    observedSha256 = computeRuntimeContentSha256(packagedContentDirectory);
  } catch (error) {
    throw new Error(
      'Packaged content changed while Desktop runtime state was being bound.',
      { cause: error });
  }
  if (observedSha256 !== expectedSha256) {
    throw new Error(
      'Packaged content changed while Desktop runtime state was being bound.');
  }
}

export function computeRuntimeContentSha256(packagedContentDirectory: string): string {
  const canonicalPackagedContentDirectory = requireCanonicalDirectory(
    packagedContentDirectory,
    'Packaged content');
  const manifestPath = path.join(
    canonicalPackagedContentDirectory,
    packageContentManifestFileName);
  requirePackagePlainFile(manifestPath, 'Packaged content manifest');
  const manifestEntries = readPackageContentManifest(manifestPath);
  const runtimeFiles = collectRuntimeFiles(canonicalPackagedContentDirectory);
  const firstMismatchIndex = runtimeFiles.findIndex(
    (runtimeFile, index) => runtimeFile !== manifestEntries[index]?.path);
  if (runtimeFiles.length !== manifestEntries.length || firstMismatchIndex >= 0) {
    const mismatchIndex = firstMismatchIndex >= 0
      ? firstMismatchIndex
      : Math.min(runtimeFiles.length, manifestEntries.length);
    const expectedPath = manifestEntries[mismatchIndex]?.path ?? '<end-of-manifest>';
    const observedPath = runtimeFiles[mismatchIndex] ?? '<end-of-package>';
    throw new Error(
      'Packaged content inventory does not exactly match its manifest. '
      + `Manifest files: ${manifestEntries.length}; observed files: ${runtimeFiles.length}; `
      + `first mismatch at ${mismatchIndex}: expected '${expectedPath}', observed '${observedPath}'.`);
  }
  if (requiredPackageContentPaths.some(
    requiredPath => !runtimeFiles.includes(requiredPath))) {
    throw new Error('Packaged content manifest is missing a formal runtime entry point.');
  }
  const content = createHash('sha256');
  for (const entry of manifestEntries) {
    const relativePath = entry.path;
    const filePath = path.join(
      canonicalPackagedContentDirectory,
      ...relativePath.split('/'));
    requirePackagePlainFile(filePath, `Packaged runtime file ${relativePath}`);
    const metadata = packageFileSystem.lstatSync(filePath);
    if (metadata.size !== entry.size) {
      throw new Error(`Packaged runtime file size did not match its manifest: ${relativePath}`);
    }
    const fileSha256 = createHash('sha256')
      .update(packageFileSystem.readFileSync(filePath))
      .digest('hex');
    if (fileSha256 !== entry.sha256) {
      throw new Error(`Packaged runtime file hash did not match its manifest: ${relativePath}`);
    }
    content.update(relativePath, 'utf8');
    content.update('\0', 'utf8');
    content.update(fileSha256, 'ascii');
    content.update('\0', 'utf8');
    content.update(String(entry.size), 'ascii');
    content.update('\n', 'utf8');
  }

  return content.digest('hex');
}

function readPackageContentManifest(manifestPath: string): PackageContentManifestEntry[] {
  let parsedValue: unknown;
  try {
    parsedValue = JSON.parse(packageFileSystem.readFileSync(manifestPath, 'utf8')) as unknown;
  } catch (error) {
    throw new Error('Packaged content manifest must be strict JSON.', { cause: error });
  }
  if (typeof parsedValue !== 'object'
      || parsedValue === null
      || Array.isArray(parsedValue)) {
    throw new Error('Packaged content manifest must be one object.');
  }
  const manifest = parsedValue as Record<string, unknown>;
  if (Object.keys(manifest).sort().join(',') !== 'files,schema'
      || manifest.schema !== 'openlineops.desktop-package-content'
      || !Array.isArray(manifest.files)) {
    throw new Error('Packaged content manifest has an unexpected schema.');
  }

  const entries = manifest.files.map((value, index) => {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
      throw new Error(`Packaged content manifest entry ${index} must be one object.`);
    }
    const entry = value as Record<string, unknown>;
    if (Object.keys(entry).sort().join(',') !== 'path,sha256,size'
        || typeof entry.path !== 'string'
        || !isCanonicalPackageRelativePath(entry.path)
        || typeof entry.sha256 !== 'string'
        || !sha256Pattern.test(entry.sha256)
        || typeof entry.size !== 'number'
        || !Number.isSafeInteger(entry.size)
        || entry.size < 0) {
      throw new Error(`Packaged content manifest entry ${index} is invalid.`);
    }
    return {
      path: entry.path,
      sha256: entry.sha256,
      size: entry.size
    };
  });
  const sortedPaths = entries.map(entry => entry.path)
    .sort(compareOrdinal);
  if (entries.some((entry, index) => entry.path !== sortedPaths[index])
      || new Set(sortedPaths).size !== sortedPaths.length) {
    throw new Error('Packaged content manifest entries must be unique and canonically sorted.');
  }
  return entries;
}

function isCanonicalPackageRelativePath(value: string): boolean {
  return value.length > 0
    && value.length <= 1024
    && !value.includes('\\')
    && !path.posix.isAbsolute(value)
    && path.posix.normalize(value) === value
    && value !== packageContentManifestFileName
    && !value.startsWith('../')
    && ![...value].some(character => character.charCodeAt(0) < 32);
}

function collectRuntimeFiles(runtimeDirectory: string): string[] {
  const files: string[] = [];
  collectRuntimeFilesUnder(runtimeDirectory, runtimeDirectory, files);
  return files.sort(compareOrdinal);
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function collectRuntimeFilesUnder(
  runtimeDirectory: string,
  currentDirectory: string,
  files: string[]
): void {
  for (const entry of packageFileSystem.readdirSync(currentDirectory, { withFileTypes: true })) {
    const entryPath = path.join(currentDirectory, entry.name);
    const metadata = packageFileSystem.lstatSync(entryPath);
    if (metadata.isSymbolicLink()) {
      throw new Error(`Packaged runtime cannot contain a symbolic link: ${entryPath}`);
    }
    if (metadata.isDirectory()) {
      collectRuntimeFilesUnder(runtimeDirectory, entryPath, files);
      continue;
    }
    if (!metadata.isFile() || metadata.nlink !== 1) {
      throw new Error(`Packaged runtime must contain only plain files and directories: ${entryPath}`);
    }
    const relativePath = path.relative(runtimeDirectory, entryPath).replaceAll('\\', '/');
    if (relativePath !== packageContentManifestFileName) {
      files.push(relativePath);
    }
  }
}

function requirePackagePlainFile(filePath: string, name: string): void {
  let metadata: Stats;
  try {
    metadata = packageFileSystem.lstatSync(filePath);
  } catch (error) {
    throw new Error(`${name} is missing: ${filePath}`, { cause: error });
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.nlink !== 1) {
    throw new Error(`${name} must be one plain regular file: ${filePath}`);
  }
}

function recoverInterruptedActivation(
  dataDirectory: string,
  currentRuntimeSha256: string
): void {
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
  const runtimeStateDirectory = resolveDataChild(dataDirectory, stateDirectoryName);
  const previousStateDirectory = resolveDataChild(dataDirectory, previousStateDirectoryName);
  const previousMetadata = readPathMetadata(previousStateDirectory);
  const activeMetadata = readPathMetadata(runtimeStateDirectory);
  if (previousMetadata !== null) {
    requireRemovableTree(previousStateDirectory);
    if (activeMetadata !== null) {
      requirePlainDirectory(runtimeStateDirectory, 'Desktop runtime state');
      const binding = readBinding(path.join(runtimeStateDirectory, markerFileName));
      if (binding
          && binding.runtimeSha256 === currentRuntimeSha256
          && isCommittedRuntimeState(runtimeStateDirectory, binding)) {
        removeBoundPath(previousStateDirectory);
      } else {
        requireRemovableTree(runtimeStateDirectory);
        removeBoundPath(runtimeStateDirectory);
        renameSync(previousStateDirectory, runtimeStateDirectory);
      }
    } else {
      renameSync(previousStateDirectory, runtimeStateDirectory);
    }
  } else if (activeMetadata !== null) {
    requirePlainDirectory(runtimeStateDirectory, 'Desktop runtime state');
    const binding = readBinding(path.join(runtimeStateDirectory, markerFileName));
    if (binding && !isCommittedRuntimeState(runtimeStateDirectory, binding)) {
      requireRemovableTree(runtimeStateDirectory);
      removeBoundPath(runtimeStateDirectory);
    }
  }
  cleanupInactiveStates(dataDirectory);
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
}

function moveActiveStateToPrevious(
  dataDirectory: string,
  runtimeStateDirectory: string,
  previousStateDirectory: string
): void {
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
  requirePlainDirectory(runtimeStateDirectory, 'Desktop runtime state');
  if (readPathMetadata(previousStateDirectory) !== null) {
    throw new Error('A previous Desktop runtime state already exists before activation.');
  }
  renameSync(runtimeStateDirectory, previousStateDirectory);
  requirePlainDirectory(previousStateDirectory, 'Previous desktop runtime state');
  if (readPathMetadata(runtimeStateDirectory) !== null) {
    throw new Error('Desktop runtime state changed concurrently while it was being preserved.');
  }
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
}

function createCommittedBindingResult(
  packagedContentDirectory: string,
  dataDirectory: string,
  runtimeStateDirectory: string,
  binding: RuntimeContentBinding
): PackagedRuntimeDataBindingResult {
  const verify = (): void => {
    assertPackagedContentUnchanged(packagedContentDirectory, binding.runtimeSha256);
    const activeBinding = readBinding(path.join(runtimeStateDirectory, markerFileName));
    if (!activeBinding
        || !bindingsEqual(activeBinding, binding)
        || !isCommittedRuntimeState(runtimeStateDirectory, activeBinding)) {
      throw new Error('Committed Desktop runtime state changed after it was validated.');
    }
  };
  return {
    reset: false,
    runtimeSha256: binding.runtimeSha256,
    runtimeStateDirectory,
    verify,
    commit: () => {
      verify();
      cleanupInactiveStates(dataDirectory);
    },
    rollback: () => {}
  };
}

function createPendingBindingResult(
  packagedContentDirectory: string,
  dataDirectory: string,
  runtimeStateDirectory: string,
  previousStateDirectory: string,
  binding: RuntimeContentBinding
): PackagedRuntimeDataBindingResult {
  let state: 'pending' | 'committed' | 'rolled-back' = 'pending';
  const commitPath = path.join(runtimeStateDirectory, activationCommitFileName);
  const verifyActiveBinding = (): void => {
    if (state === 'rolled-back') {
      throw new Error('Desktop runtime activation has already been rolled back.');
    }
    assertPackagedContentUnchanged(packagedContentDirectory, binding.runtimeSha256);
    const activeBinding = readBinding(path.join(runtimeStateDirectory, markerFileName));
    if (!activeBinding || !bindingsEqual(activeBinding, binding)) {
      throw new Error('Pending Desktop runtime state changed after activation.');
    }
    if (state === 'committed') {
      if (!isCommittedRuntimeState(runtimeStateDirectory, binding)) {
        throw new Error('Committed Desktop runtime activation proof is missing.');
      }
    } else if (readPathMetadata(commitPath) !== null) {
      throw new Error('Pending Desktop runtime state contains an unexpected commit proof.');
    }
  };
  const rollback = (): void => {
    if (state === 'committed' || state === 'rolled-back') {
      return;
    }
    const activeBinding = readBinding(path.join(runtimeStateDirectory, markerFileName));
    if (!activeBinding || !bindingsEqual(activeBinding, binding)) {
      throw new Error('Refusing to roll back a different Desktop runtime activation.');
    }
    requireRemovableTree(runtimeStateDirectory);
    removeBoundPath(runtimeStateDirectory);
    if (readPathMetadata(previousStateDirectory) !== null) {
      requirePlainDirectory(previousStateDirectory, 'Previous desktop runtime state');
      renameSync(previousStateDirectory, runtimeStateDirectory);
    }
    cleanupInactiveStates(dataDirectory);
    state = 'rolled-back';
  };
  const commit = (): void => {
    if (state === 'committed') {
      verifyActiveBinding();
      cleanupInactiveStates(dataDirectory);
      return;
    }
    verifyActiveBinding();
    if (readPathMetadata(previousStateDirectory) !== null) {
      requireRemovableTree(previousStateDirectory);
    }
    writeActivationCommit(commitPath, binding);
    try {
      assertPackagedContentUnchanged(packagedContentDirectory, binding.runtimeSha256);
      if (!isCommittedRuntimeState(runtimeStateDirectory, binding)) {
        throw new Error('Desktop runtime activation commit proof could not be verified.');
      }
    } catch (error) {
      removeBoundPath(commitPath);
      rollback();
      throw error;
    }
    state = 'committed';
    removeBoundPath(previousStateDirectory);
    cleanupInactiveStates(dataDirectory);
  };
  return {
    reset: true,
    runtimeSha256: binding.runtimeSha256,
    runtimeStateDirectory,
    verify: verifyActiveBinding,
    commit,
    rollback
  };
}

function createStagedRuntimeState(
  dataDirectory: string,
  binding: RuntimeContentBinding
): string {
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
  const stagedStateDirectory = resolveDataChild(
    dataDirectory,
    `${stagedStatePrefix}${process.pid}-${randomUUID()}`);
  mkdirSync(stagedStateDirectory, { recursive: false, mode: 0o700 });
  requirePlainDirectory(stagedStateDirectory, 'Staged desktop runtime state');
  const markerPath = path.join(stagedStateDirectory, markerFileName);
  let fileDescriptor: number | null = null;
  try {
    fileDescriptor = openSync(markerPath, 'wx', 0o600);
    writeFileSync(fileDescriptor, `${JSON.stringify(binding, null, 2)}\n`, {
      encoding: 'utf8'
    });
    fsyncSync(fileDescriptor);
  } finally {
    if (fileDescriptor !== null) {
      closeSync(fileDescriptor);
    }
  }
  requirePlainFile(markerPath, 'Desktop runtime content binding');
  const stagedBinding = readBinding(markerPath);
  if (!stagedBinding || !bindingsEqual(stagedBinding, binding)) {
    throw new Error('Staged desktop runtime state did not preserve its content binding.');
  }
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
  return stagedStateDirectory;
}

function cleanupInactiveStates(dataDirectory: string): void {
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
  const inactivePaths = readdirSync(dataDirectory, { withFileTypes: true })
    .filter(entry => entry.name.startsWith(discardedStatePrefix)
      || entry.name.startsWith(stagedStatePrefix))
    .map(entry => resolveDataChild(dataDirectory, entry.name));
  for (const inactivePath of inactivePaths) {
    removeBoundPath(inactivePath);
  }
  requireCanonicalDirectory(dataDirectory, 'Desktop data');
}

function isCommittedRuntimeState(
  runtimeStateDirectory: string,
  binding: RuntimeContentBinding
): boolean {
  const commit = readActivationCommit(path.join(
    runtimeStateDirectory,
    activationCommitFileName));
  return commit !== null
    && commit.runtimeSha256 === binding.runtimeSha256
    && commit.activationId === binding.activationId;
}

function writeActivationCommit(
  commitPath: string,
  binding: RuntimeContentBinding
): void {
  const commit: RuntimeActivationCommit = {
    schema: 'openlineops.desktop-runtime-activation',
    runtimeSha256: binding.runtimeSha256,
    activationId: binding.activationId
  };
  writeExclusiveJsonFile(commitPath, commit);
}

function readActivationCommit(commitPath: string): RuntimeActivationCommit | null {
  const parsed = readStrictJsonFile(commitPath, 'Desktop runtime activation commit');
  if (parsed === null) {
    return null;
  }
  const keys = Object.keys(parsed).sort();
  if (keys.join(',') !== 'activationId,runtimeSha256,schema'
      || parsed.schema !== 'openlineops.desktop-runtime-activation'
      || typeof parsed.runtimeSha256 !== 'string'
      || !sha256Pattern.test(parsed.runtimeSha256)
      || typeof parsed.activationId !== 'string'
      || !isCanonicalActivationId(parsed.activationId)) {
    return null;
  }
  return parsed as unknown as RuntimeActivationCommit;
}

function readBinding(markerPath: string): RuntimeContentBinding | null {
  const parsed = readStrictJsonFile(markerPath, 'Desktop runtime content binding');
  if (parsed === null) {
    return null;
  }
  const keys = Object.keys(parsed).sort();
  if (keys.join(',') !== 'activationId,runtimeSha256,schema'
      || parsed.schema !== 'openlineops.desktop-runtime-content-binding'
      || typeof parsed.runtimeSha256 !== 'string'
      || !sha256Pattern.test(parsed.runtimeSha256)
      || typeof parsed.activationId !== 'string'
      || !isCanonicalActivationId(parsed.activationId)) {
    return null;
  }

  return parsed as unknown as RuntimeContentBinding;
}

function readStrictJsonFile(
  filePath: string,
  name: string
): Record<string, unknown> | null {
  if (readPathMetadata(filePath) === null) {
    return null;
  }
  requirePlainFile(filePath, name);
  let parsedValue: unknown;
  try {
    parsedValue = JSON.parse(readFileSync(filePath, 'utf8')) as unknown;
  } catch {
    return null;
  }
  if (typeof parsedValue !== 'object'
      || parsedValue === null
      || Array.isArray(parsedValue)) {
    return null;
  }
  return parsedValue as Record<string, unknown>;
}

function writeExclusiveJsonFile(filePath: string, value: object): void {
  let fileDescriptor: number | null = null;
  try {
    fileDescriptor = openSync(filePath, 'wx', 0o600);
    writeFileSync(fileDescriptor, `${JSON.stringify(value, null, 2)}\n`, {
      encoding: 'utf8'
    });
    fsyncSync(fileDescriptor);
  } finally {
    if (fileDescriptor !== null) {
      closeSync(fileDescriptor);
    }
  }
  requirePlainFile(filePath, 'Desktop runtime activation document');
}

function bindingsEqual(
  left: RuntimeContentBinding,
  right: RuntimeContentBinding
): boolean {
  return left.schema === right.schema
    && left.runtimeSha256 === right.runtimeSha256
    && left.activationId === right.activationId;
}

function isCanonicalActivationId(value: string): boolean {
  return /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/u
    .test(value);
}

function removeBoundPath(targetPath: string): void {
  const metadata = readPathMetadata(targetPath);
  if (metadata === null) {
    return;
  }
  if (metadata.isSymbolicLink()) {
    throw new Error(`Refusing to remove a symbolic-link runtime path: ${targetPath}`);
  }
  if (!metadata.isFile() && !metadata.isDirectory()) {
    throw new Error(`Refusing to remove a non-regular runtime path: ${targetPath}`);
  }
  requireRemovableTree(targetPath);
  rmSync(targetPath, { recursive: metadata.isDirectory(), force: false });
}

function requireRemovableTree(targetPath: string): void {
  const metadata = lstatSync(targetPath);
  if (metadata.isSymbolicLink()) {
    throw new Error(`Runtime state cannot contain a symbolic link or junction: ${targetPath}`);
  }
  if (metadata.isFile()) {
    if (metadata.nlink !== 1) {
      throw new Error(`Runtime state file must have exactly one hard link: ${targetPath}`);
    }
    return;
  }
  if (!metadata.isDirectory()) {
    throw new Error(`Runtime state must contain only plain files and directories: ${targetPath}`);
  }
  for (const entry of readdirSync(targetPath, { withFileTypes: true })) {
    requireRemovableTree(path.join(targetPath, entry.name));
  }
}

function readPathMetadata(targetPath: string): Stats | null {
  try {
    return lstatSync(targetPath);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
      return null;
    }
    throw error;
  }
}

function requireCanonicalDirectory(value: string, name: string): string {
  const canonical = normalizeCanonicalAbsolutePath(value, name);

  requirePlainDirectory(canonical, name);
  const physical = realpathSync.native(canonical);
  const physicalMatchesCanonical = process.platform === 'win32'
    ? physical.toLowerCase() === canonical.toLowerCase()
    : physical === canonical;
  if (!physicalMatchesCanonical) {
    throw new Error(`${name} path must not traverse a symbolic link or junction.`);
  }
  return physical;
}

function normalizeCanonicalAbsolutePath(value: string, name: string): string {
  if (!path.isAbsolute(value)) {
    throw new Error(`${name} path must be canonical and absolute.`);
  }
  const canonical = path.resolve(value);
  if (canonical !== path.normalize(value)) {
    throw new Error(`${name} path must be canonical and absolute.`);
  }
  return canonical;
}

function pathsOverlap(leftPath: string, rightPath: string): boolean {
  const left = comparablePath(leftPath);
  const right = comparablePath(rightPath);
  return left === right
    || left.startsWith(`${right}${path.sep}`)
    || right.startsWith(`${left}${path.sep}`);
}

function comparablePath(value: string): string {
  const normalized = path.resolve(value).replace(/[\\/]+$/u, '');
  return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

function requirePlainDirectory(directoryPath: string, name: string): void {
  const metadata = lstatSync(directoryPath);
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    throw new Error(`${name} path must be a plain directory: ${directoryPath}`);
  }
}

function requirePlainFile(filePath: string, name: string): void {
  const metadata = lstatSync(filePath);
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.nlink !== 1) {
    throw new Error(`${name} path must be one plain regular file: ${filePath}`);
  }
}

function resolveDataChild(dataDirectory: string, childName: string): string {
  const canonicalRoot = path.resolve(dataDirectory);
  const resolved = path.resolve(canonicalRoot, childName);
  if (path.dirname(resolved) !== canonicalRoot) {
    throw new Error(`Desktop runtime state path escaped its data directory: ${resolved}`);
  }
  return resolved;
}
