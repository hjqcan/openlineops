import { lstatSync, realpathSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

export const maximumApplicationExtensionArchiveBytes = 256 * 1024 * 1024;

export interface ApplicationExtensionArchiveIdentity {
  path: string;
  fileName: string;
  sizeBytes: number;
  device: number;
  inode: number;
  modifiedAtMilliseconds: number;
  changedAtMilliseconds: number;
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
  if (fileName !== fileName.trim()
      || fileName.length > 255
      || [...fileName].some(character => character.charCodeAt(0) < 32)
      || path.extname(fileName).toLowerCase() !== '.zip') {
    throw new Error('Application extensions must be imported from one local .zip archive.');
  }

  const resolvedPath = realpathSync.native(normalizedPath);
  if (!samePath(resolvedPath, normalizedPath)) {
    throw new Error('The selected extension package cannot be a link or redirected path.');
  }

  const metadata = lstatSync(normalizedPath);
  if (!metadata.isFile()
      || metadata.isSymbolicLink()
      || metadata.nlink !== 1
      || metadata.size <= 0
      || metadata.size > maximumApplicationExtensionArchiveBytes) {
    throw new Error(
      'The selected extension package must be one non-linked regular ZIP file no larger than 256 MiB.');
  }

  return {
    path: normalizedPath,
    fileName,
    sizeBytes: metadata.size,
    device: metadata.dev,
    inode: metadata.ino,
    modifiedAtMilliseconds: metadata.mtimeMs,
    changedAtMilliseconds: metadata.ctimeMs
  };
}

export function assertApplicationExtensionArchiveUnchanged(
  expected: ApplicationExtensionArchiveIdentity
): void {
  const current = inspectApplicationExtensionArchive(expected.path);
  if (current.sizeBytes !== expected.sizeBytes
      || current.device !== expected.device
      || current.inode !== expected.inode
      || current.modifiedAtMilliseconds !== expected.modifiedAtMilliseconds
      || current.changedAtMilliseconds !== expected.changedAtMilliseconds) {
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

function isWindowsRemoteOrDevicePath(value: string): boolean {
  return value.startsWith('\\\\') || value.startsWith('\\\\?\\') || value.startsWith('\\\\.\\');
}
