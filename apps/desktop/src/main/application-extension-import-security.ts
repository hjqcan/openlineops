import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

export const maximumApplicationExtensionArchiveBytes = 256 * 1024 * 1024;

export interface ApplicationExtensionArchiveIdentity {
  path: string;
  fileName: string;
  sizeBytes: number;
  device: bigint;
  inode: bigint;
  modifiedAtNanoseconds: bigint;
  changedAtNanoseconds: bigint;
}

export function inspectApplicationExtensionArchive(
  candidatePath: string
): ApplicationExtensionArchiveIdentity {
  if (typeof candidatePath !== 'string'
      || candidatePath.length === 0
      || candidatePath !== candidatePath.trim()
      || candidatePath.length > 32_767
      || candidatePath.includes('\0')
      || !path.isAbsolute(candidatePath)) {
    throw new Error('The selected extension package path is not one canonical absolute local path.');
  }

  const normalizedPath = path.normalize(candidatePath);
  if (!samePath(candidatePath, normalizedPath)
      || process.platform === 'win32' && isWindowsRemoteOrDevicePath(normalizedPath)) {
    throw new Error('The selected extension package path is not one canonical local path.');
  }

  const fileName = path.basename(normalizedPath);
  assertApplicationExtensionArchiveFileName(fileName);

  // Reject a linked leaf, then anchor subsequent path-based opens outside any stable parent alias
  // by returning the resolved physical path bound to the selected file's filesystem identity.
  const selectedMetadata = fs.lstatSync(normalizedPath, { bigint: true });
  assertApplicationExtensionArchiveMetadata(selectedMetadata);

  const resolvedPath = fs.realpathSync.native(normalizedPath);
  assertCanonicalResolvedApplicationExtensionPath(resolvedPath);
  assertApplicationExtensionArchiveFileName(path.basename(resolvedPath));

  const metadata = fs.lstatSync(resolvedPath, { bigint: true });
  assertApplicationExtensionArchiveMetadata(metadata);
  if (!sameArchiveIdentity(selectedMetadata, metadata)) {
    throw new Error('The selected extension package resolved to a different physical file.');
  }

  return {
    path: resolvedPath,
    fileName,
    sizeBytes: Number(metadata.size),
    device: metadata.dev,
    inode: metadata.ino,
    modifiedAtNanoseconds: metadata.mtimeNs,
    changedAtNanoseconds: metadata.ctimeNs
  };
}

export function assertApplicationExtensionArchiveUnchanged(
  expected: ApplicationExtensionArchiveIdentity
): void {
  const current = inspectApplicationExtensionArchive(expected.path);
  if (current.sizeBytes !== expected.sizeBytes
      || current.device !== expected.device
      || current.inode !== expected.inode
      || current.modifiedAtNanoseconds !== expected.modifiedAtNanoseconds
      || current.changedAtNanoseconds !== expected.changedAtNanoseconds) {
    throw new Error('The selected extension package changed while it was being prepared.');
  }
}

export function deriveApplicationExtensionPortableId(
  archiveFileName: string,
  contentSha256: string
): string {
  if (!/^[a-f0-9]{64}$/u.test(contentSha256)) {
    throw new Error('Extension package content identity must be one lowercase SHA-256 value.');
  }

  const fileStem = path.basename(archiveFileName, path.extname(archiveFileName));
  const normalized = fileStem
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/gu, '')
    .replace(/[^A-Za-z0-9._-]+/gu, '-')
    .replace(/^[._-]+|[._-]+$/gu, '')
    .toLowerCase();
  const portableId = normalized.length === 0
    ? `extension-${contentSha256.slice(0, 16)}`
    : normalized.slice(0, 96).replace(/[._-]+$/gu, '');
  if (portableId.length === 0 || portableId === '.' || portableId === '..') {
    throw new Error('Unable to derive a portable extension identity from the selected ZIP archive.');
  }

  return portableId;
}

function samePath(left: string, right: string): boolean {
  const normalize = (value: string): string => process.platform === 'win32'
    ? value.toLocaleLowerCase('en-US')
    : value;
  return normalize(left) === normalize(right);
}

function assertCanonicalResolvedApplicationExtensionPath(resolvedPath: string): void {
  if (resolvedPath.length === 0
      || resolvedPath !== resolvedPath.trim()
      || resolvedPath.length > 32_767
      || resolvedPath.includes('\0')
      || !path.isAbsolute(resolvedPath)
      || !samePath(resolvedPath, path.normalize(resolvedPath))
      || process.platform === 'win32' && isWindowsRemoteOrDevicePath(resolvedPath)) {
    throw new Error('The selected extension package did not resolve to one canonical local path.');
  }
}

function assertApplicationExtensionArchiveFileName(fileName: string): void {
  if (fileName !== fileName.trim()
      || fileName.length > 255
      || [...fileName].some(character => character.charCodeAt(0) < 32)
      || path.extname(fileName).toLowerCase() !== '.zip') {
    throw new Error('Application extensions must be imported from one local .zip archive.');
  }
}

function assertApplicationExtensionArchiveMetadata(metadata: fs.BigIntStats): void {
  if (!metadata.isFile()
      || metadata.isSymbolicLink()
      || metadata.nlink !== 1n
      || metadata.size <= 0n
      || metadata.size > BigInt(maximumApplicationExtensionArchiveBytes)) {
    throw new Error(
      'The selected extension package must be one non-linked regular ZIP file no larger than 256 MiB.');
  }
}

function sameArchiveIdentity(left: fs.BigIntStats, right: fs.BigIntStats): boolean {
  return left.dev === right.dev
    && left.ino === right.ino
    && left.size === right.size
    && left.mtimeNs === right.mtimeNs
    && left.ctimeNs === right.ctimeNs;
}

function isWindowsRemoteOrDevicePath(value: string): boolean {
  return value.startsWith('\\\\') || value.startsWith('\\\\?\\') || value.startsWith('\\\\.\\');
}
