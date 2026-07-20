import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { externalProgramDirectoryImportLimits } from '../shared/external-program-directory-import-contract.js';
import { runBoundedChildProcess } from './bounded-child-process.js';
import { createWindowsPowerShellHost } from './windows-system-tools.js';

export const maximumExternalProgramDirectoryFileCount =
  externalProgramDirectoryImportLimits.maximumFileCount;
export const maximumExternalProgramDirectoryFileBytes =
  externalProgramDirectoryImportLimits.maximumFileBytes;
export const maximumExternalProgramDirectoryTotalBytes =
  externalProgramDirectoryImportLimits.maximumTotalBytes;
export const maximumExternalProgramDirectoryCount =
  externalProgramDirectoryImportLimits.maximumDirectoryCount;

export interface ExternalProgramDirectoryFileIdentity {
  path: string;
  relativePath: string;
  resourceRelativePath: string;
  sizeBytes: number;
  sha256: string;
  device: bigint;
  inode: bigint;
  modifiedAtNanoseconds: bigint;
  changedAtNanoseconds: bigint;
}

export interface ExternalProgramDirectoryIdentity {
  path: string;
  directoryName: string;
  totalBytes: number;
  device: bigint;
  inode: bigint;
  modifiedAtNanoseconds: bigint;
  changedAtNanoseconds: bigint;
  files: ExternalProgramDirectoryFileIdentity[];
}

interface ScannedExternalProgramFile {
  path: string;
  relativePath: string;
  resourceRelativePath: string;
  sizeBytes: number;
  device: bigint;
  inode: bigint;
  modifiedAtNanoseconds: bigint;
  changedAtNanoseconds: bigint;
}

interface ScannedExternalProgramDirectory {
  path: string;
  directoryName: string;
  totalBytes: number;
  device: bigint;
  inode: bigint;
  modifiedAtNanoseconds: bigint;
  changedAtNanoseconds: bigint;
  files: ScannedExternalProgramFile[];
  allPaths: string[];
}

export async function inspectExternalProgramDirectory(
  candidatePath: string
): Promise<ExternalProgramDirectoryIdentity> {
  const scanned = scanExternalProgramDirectory(candidatePath);
  await assertNoAlternateDataStreams(scanned.allPaths);
  const files: ExternalProgramDirectoryFileIdentity[] = [];
  for (const file of scanned.files) {
    const sha256 = await calculateBoundedFileSha256(file.path, file.sizeBytes);
    assertFileIdentityUnchanged(file);
    files.push({ ...file, sha256 });
  }

  assertDirectoryIdentityUnchanged(scanned);
  return {
    path: scanned.path,
    directoryName: scanned.directoryName,
    totalBytes: scanned.totalBytes,
    device: scanned.device,
    inode: scanned.inode,
    modifiedAtNanoseconds: scanned.modifiedAtNanoseconds,
    changedAtNanoseconds: scanned.changedAtNanoseconds,
    files
  };
}

export async function assertExternalProgramDirectoryUnchanged(
  expected: ExternalProgramDirectoryIdentity
): Promise<void> {
  const current = await inspectExternalProgramDirectory(expected.path);
  if (!sameDirectoryIdentity(expected, current)
      || expected.totalBytes !== current.totalBytes
      || expected.files.length !== current.files.length) {
    throw new Error('The selected external program directory changed while it was being prepared.');
  }

  for (let index = 0; index < expected.files.length; index++) {
    const left = expected.files[index];
    const right = current.files[index];
    if (left.path !== right.path
        || left.relativePath !== right.relativePath
        || left.resourceRelativePath !== right.resourceRelativePath
        || left.sizeBytes !== right.sizeBytes
        || left.sha256 !== right.sha256
        || !sameFileIdentity(left, right)) {
      throw new Error('The selected external program directory changed while it was being prepared.');
    }
  }
}

function scanExternalProgramDirectory(candidatePath: string): ScannedExternalProgramDirectory {
  const selectedPath = requireCanonicalLocalAbsolutePath(candidatePath, 'selected external program directory');
  const selectedMetadata = fs.lstatSync(selectedPath, { bigint: true });
  assertPlainDirectory(selectedMetadata, 'The selected external program path');

  const physicalPath = requireCanonicalLocalAbsolutePath(
    fs.realpathSync.native(selectedPath),
    'physical external program directory');
  const physicalMetadata = fs.lstatSync(physicalPath, { bigint: true });
  assertPlainDirectory(physicalMetadata, 'The physical external program path');
  if (!sameFileSystemObject(selectedMetadata, physicalMetadata)) {
    throw new Error('The selected external program directory resolved to a different physical directory.');
  }

  const files: ScannedExternalProgramFile[] = [];
  const allPaths = [physicalPath];
  const pendingDirectories = [physicalPath];
  let directoryCount = 1;
  let totalBytes = 0;

  while (pendingDirectories.length > 0) {
    const directory = pendingDirectories.pop()!;
    const entries = fs.readdirSync(directory, { withFileTypes: true })
      .sort((left, right) => left.name.localeCompare(right.name, 'en-US'));
    for (const entry of entries) {
      const entryPath = path.join(directory, entry.name);
      const relativePath = canonicalRelativePath(physicalPath, entryPath);
      const metadata = fs.lstatSync(entryPath, { bigint: true });
      if (metadata.isSymbolicLink()) {
        throw new Error(`External program directory entry '${relativePath}' cannot be a symbolic link or reparse point.`);
      }

      if (metadata.isDirectory()) {
        directoryCount++;
        if (directoryCount > maximumExternalProgramDirectoryCount) {
          throw new Error(
            `External program directory exceeds the ${maximumExternalProgramDirectoryCount}-directory import limit.`);
        }
        assertResolvedInsideRoot(physicalPath, entryPath, metadata, relativePath);
        allPaths.push(entryPath);
        pendingDirectories.push(entryPath);
        continue;
      }

      if (!metadata.isFile() || metadata.nlink !== 1n) {
        throw new Error(
          `External program directory entry '${relativePath}' must be one regular file with exactly one hard link.`);
      }

      assertResolvedInsideRoot(physicalPath, entryPath, metadata, relativePath);
      if (metadata.size > BigInt(maximumExternalProgramDirectoryFileBytes)) {
        throw new Error(
          `External program file '${relativePath}' exceeds the 512 MiB per-file import limit.`);
      }

      if (files.length >= maximumExternalProgramDirectoryFileCount) {
        throw new Error(
          `External program directory exceeds the ${maximumExternalProgramDirectoryFileCount}-file import limit.`);
      }

      const sizeBytes = Number(metadata.size);
      if (totalBytes > maximumExternalProgramDirectoryTotalBytes - sizeBytes) {
        throw new Error('External program directory exceeds the 2 GiB total import limit.');
      }

      const resourceRelativePath = `files/${relativePath}`;
      totalBytes += sizeBytes;
      allPaths.push(entryPath);
      files.push({
        path: entryPath,
        relativePath,
        resourceRelativePath,
        sizeBytes,
        device: metadata.dev,
        inode: metadata.ino,
        modifiedAtNanoseconds: metadata.mtimeNs,
        changedAtNanoseconds: metadata.ctimeNs
      });
    }
  }

  if (files.length === 0) {
    throw new Error('The selected external program directory contains no files.');
  }

  assertExternalProgramDirectoryRelativePaths(files.map(file => file.relativePath));
  files.sort((left, right) => left.relativePath.localeCompare(right.relativePath, 'en-US'));
  return {
    path: physicalPath,
    directoryName: path.basename(physicalPath),
    totalBytes,
    device: physicalMetadata.dev,
    inode: physicalMetadata.ino,
    modifiedAtNanoseconds: physicalMetadata.mtimeNs,
    changedAtNanoseconds: physicalMetadata.ctimeNs,
    files,
    allPaths
  };
}

function canonicalRelativePath(root: string, candidatePath: string): string {
  const relative = path.relative(root, candidatePath);
  if (relative.length === 0
      || path.isAbsolute(relative)
      || relative === '..'
      || relative.startsWith(`..${path.sep}`)) {
    throw new Error('An external program directory entry escaped its selected physical root.');
  }

  const portable = relative.split(path.sep).join('/');
  assertCanonicalPortableRelativePath(portable);
  return portable;
}

export function assertExternalProgramDirectoryRelativePaths(relativePaths: readonly string[]): void {
  const portablePaths = new Set<string>();
  for (const relativePath of relativePaths) {
    assertCanonicalPortableRelativePath(relativePath);
    const resourceRelativePath = `files/${relativePath}`;
    const portableIdentity = resourceRelativePath.normalize('NFC').toLocaleLowerCase('en-US');
    if (portablePaths.has(portableIdentity)) {
      throw new Error(
        `External program directory contains a case-insensitive path collision at '${resourceRelativePath}'.`);
    }
    portablePaths.add(portableIdentity);
  }
}

function assertCanonicalPortableRelativePath(value: string): void {
  if (typeof value !== 'string'
      || value.length === 0
      || value.length > 1_024
      || value.includes('\\')
      || value.includes(':')) {
    throw new Error(`External program directory entry '${value}' is not a canonical portable relative path.`);
  }

  const segments = value.split('/');
  if (segments.some(segment => segment.length === 0
      || segment.length > 255
      || segment === '.'
      || segment === '..'
      || segment !== segment.trim()
      || segment.endsWith('.')
      || segment.includes('/')
      || segment.includes('\\')
      || segment.includes(':')
      || /[<>"|?*]/u.test(segment)
      || segment !== segment.normalize('NFC')
      || isWindowsReservedPathSegment(segment)
      || [...segment].some(character => character.charCodeAt(0) < 32))) {
    throw new Error(
      `External program directory entry '${value}' is not a canonical portable relative path.`);
  }
}

function isWindowsReservedPathSegment(segment: string): boolean {
  const stem = segment.split('.')[0].toLocaleUpperCase('en-US');
  return /^(?:CON|PRN|AUX|NUL|CLOCK\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])$/u.test(stem);
}

function assertResolvedInsideRoot(
  root: string,
  entryPath: string,
  selectedMetadata: fs.BigIntStats,
  relativePath: string
): void {
  const physicalEntryPath = requireCanonicalLocalAbsolutePath(
    fs.realpathSync.native(entryPath),
    `physical external program entry '${relativePath}'`);
  const relativePhysicalPath = path.relative(root, physicalEntryPath);
  if (relativePhysicalPath === '..'
      || relativePhysicalPath.startsWith(`..${path.sep}`)
      || path.isAbsolute(relativePhysicalPath)) {
    throw new Error(`External program directory entry '${relativePath}' escaped its selected physical root.`);
  }

  const physicalMetadata = fs.lstatSync(physicalEntryPath, { bigint: true });
  if (!sameFileSystemObject(selectedMetadata, physicalMetadata)) {
    throw new Error(`External program directory entry '${relativePath}' resolved to a different physical object.`);
  }
}

function assertPlainDirectory(metadata: fs.BigIntStats, label: string): void {
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    throw new Error(`${label} must be one plain local directory and cannot be a reparse point.`);
  }
}

function assertFileIdentityUnchanged(expected: ScannedExternalProgramFile): void {
  const metadata = fs.lstatSync(expected.path, { bigint: true });
  if (!metadata.isFile()
      || metadata.isSymbolicLink()
      || metadata.nlink !== 1n
      || metadata.size !== BigInt(expected.sizeBytes)
      || metadata.dev !== expected.device
      || metadata.ino !== expected.inode
      || metadata.mtimeNs !== expected.modifiedAtNanoseconds
      || metadata.ctimeNs !== expected.changedAtNanoseconds) {
    throw new Error(`External program file '${expected.relativePath}' changed while it was being inspected.`);
  }
}

function assertDirectoryIdentityUnchanged(expected: ScannedExternalProgramDirectory): void {
  const metadata = fs.lstatSync(expected.path, { bigint: true });
  if (!metadata.isDirectory()
      || metadata.isSymbolicLink()
      || metadata.dev !== expected.device
      || metadata.ino !== expected.inode
      || metadata.mtimeNs !== expected.modifiedAtNanoseconds
      || metadata.ctimeNs !== expected.changedAtNanoseconds) {
    throw new Error('The selected external program directory changed while it was being inspected.');
  }
}

function sameDirectoryIdentity(
  left: ExternalProgramDirectoryIdentity,
  right: ExternalProgramDirectoryIdentity
): boolean {
  return left.path === right.path
    && left.directoryName === right.directoryName
    && left.device === right.device
    && left.inode === right.inode
    && left.modifiedAtNanoseconds === right.modifiedAtNanoseconds
    && left.changedAtNanoseconds === right.changedAtNanoseconds;
}

function sameFileIdentity(
  left: ExternalProgramDirectoryFileIdentity,
  right: ExternalProgramDirectoryFileIdentity
): boolean {
  return left.device === right.device
    && left.inode === right.inode
    && left.modifiedAtNanoseconds === right.modifiedAtNanoseconds
    && left.changedAtNanoseconds === right.changedAtNanoseconds;
}

function sameFileSystemObject(left: fs.BigIntStats, right: fs.BigIntStats): boolean {
  return left.dev === right.dev && left.ino === right.ino;
}

function requireCanonicalLocalAbsolutePath(value: string, label: string): string {
  if (typeof value !== 'string'
      || value.length === 0
      || value !== value.trim()
      || value.length > 32_767
      || value.includes('\0')
      || !path.isAbsolute(value)) {
    throw new Error(`The ${label} is not one canonical absolute local path.`);
  }

  const normalized = path.normalize(value);
  if (!samePath(value, normalized)
      || process.platform === 'win32' && isWindowsRemoteOrDevicePath(normalized)) {
    throw new Error(`The ${label} is not one canonical local path.`);
  }

  return normalized;
}

function samePath(left: string, right: string): boolean {
  return process.platform === 'win32'
    ? left.toLocaleLowerCase('en-US') === right.toLocaleLowerCase('en-US')
    : left === right;
}

function isWindowsRemoteOrDevicePath(value: string): boolean {
  return value.startsWith('\\\\') || value.startsWith('\\\\?\\') || value.startsWith('\\\\.\\');
}

export async function calculateBoundedFileSha256(
  filePath: string,
  expectedSizeBytes: number
): Promise<string> {
  if (!Number.isSafeInteger(expectedSizeBytes)
      || expectedSizeBytes < 0
      || expectedSizeBytes > maximumExternalProgramDirectoryFileBytes) {
    throw new Error('External program file expected size is outside the bounded hashing limit.');
  }
  const hash = createHash('sha256');
  const handle = await fs.promises.open(filePath, 'r');
  try {
    const buffer = Buffer.allocUnsafe(64 * 1024);
    let readBytes = 0;
    while (readBytes < expectedSizeBytes) {
      const requested = Math.min(buffer.byteLength, expectedSizeBytes - readBytes);
      const result = await handle.read(buffer, 0, requested, null);
      if (result.bytesRead === 0) {
        throw new Error(`External program file '${filePath}' became shorter while it was hashed.`);
      }
      hash.update(buffer.subarray(0, result.bytesRead));
      readBytes += result.bytesRead;
    }

    const extra = await handle.read(buffer, 0, 1, null);
    const finalMetadata = await handle.stat({ bigint: true });
    if (extra.bytesRead !== 0 || finalMetadata.size !== BigInt(expectedSizeBytes)) {
      throw new Error(`External program file '${filePath}' changed while it was hashed.`);
    }
    return hash.digest('hex');
  } finally {
    await handle.close();
  }
}

async function assertNoAlternateDataStreams(paths: string[]): Promise<void> {
  if (process.platform !== 'win32') {
    return;
  }

  const host = createWindowsPowerShellHost();
  const script = String.raw`
$ErrorActionPreference = 'Stop'
$paths = [Console]::In.ReadToEnd() | ConvertFrom-Json
foreach ($candidate in $paths) {
  $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
  if (([int]$item.Attributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
    [Console]::Error.WriteLine("Reparse point '$candidate' is not allowed.")
    exit 16
  }
  $streams = @(Get-Item -LiteralPath $candidate -Force -Stream * -ErrorAction Stop)
  foreach ($stream in $streams) {
    if ($stream.Stream -ne ':$DATA') {
      [Console]::Error.WriteLine("Alternate data stream '$($stream.Stream)' exists on '$candidate'.")
      exit 17
    }
  }
}
exit 0
`;
  try {
    await runBoundedChildProcess({
      executablePath: host.executablePath,
      arguments: [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-Command',
        script
      ],
      cwd: host.systemRoot,
      environment: host.environment,
      input: JSON.stringify(paths),
      maximumInputBytes: 8 * 1024 * 1024,
      maximumStdoutBytes: 64 * 1024,
      maximumStderrBytes: 64 * 1024,
      maximumTotalOutputBytes: 64 * 1024,
      timeoutMs: 30_000
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message.trim() : '';
    throw new Error(detail.length > 0
      ? `External program directory alternate-data-stream validation failed: ${detail}`
      : 'External program directory alternate-data-stream validation failed closed.');
  }
}
