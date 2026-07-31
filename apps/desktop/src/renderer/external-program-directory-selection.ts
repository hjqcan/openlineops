import type {
  ExternalProgramDirectoryInventoryFile,
  ExternalProgramDirectorySelectionResult
} from '../shared/desktop-api';
import { externalProgramDirectoryImportLimits } from '../shared/external-program-directory-import-contract';

export interface ExternalProgramDirectorySelection {
  selectionId: string;
  directoryName: string;
  totalBytes: number;
  files: ExternalProgramDirectoryInventoryFile[];
}

export function requireExternalProgramDirectorySelection(
  result: ExternalProgramDirectorySelectionResult
): ExternalProgramDirectorySelection | null {
  if (result.canceled) {
    if (result.selectionId !== null
        || result.directoryName !== null
        || result.totalBytes !== null
        || result.files.length !== 0) {
      throw new Error('Canceled external program directory selection contains unexpected inventory.');
    }
    return null;
  }

  if (!result.selectionId
      || !/^[a-f0-9]{64}$/u.test(result.selectionId)
      || !result.directoryName
      || result.directoryName !== result.directoryName.trim()
      || result.totalBytes === null
      || !Number.isSafeInteger(result.totalBytes)
      || result.totalBytes < 0
      || result.files.length === 0
      || result.files.length > externalProgramDirectoryImportLimits.maximumFileCount) {
    throw new Error('External program directory selection inventory is incomplete.');
  }

  const paths = new Set<string>();
  let totalBytes = 0;
  const files = result.files.map(file => {
    const expectedResourcePath = `files/${file.relativePath}`;
    if (!isCanonicalRelativePath(file.relativePath)
        || file.resourceRelativePath !== expectedResourcePath
        || !Number.isSafeInteger(file.sizeBytes)
        || file.sizeBytes < 0
        || file.sizeBytes > externalProgramDirectoryImportLimits.maximumFileBytes
        || !/^[a-f0-9]{64}$/u.test(file.sha256)) {
      throw new Error(`External program directory file '${file.relativePath}' has invalid inventory metadata.`);
    }

    const portableIdentity = expectedResourcePath.normalize('NFC').toLocaleLowerCase('en-US');
    if (paths.has(portableIdentity)) {
      throw new Error(`External program directory file '${expectedResourcePath}' conflicts by case.`);
    }
    paths.add(portableIdentity);
    totalBytes += file.sizeBytes;
    return { ...file };
  }).sort((left, right) => left.relativePath.localeCompare(right.relativePath, 'en-US'));

  if (totalBytes !== result.totalBytes
      || totalBytes > externalProgramDirectoryImportLimits.maximumTotalBytes) {
    throw new Error('External program directory total size does not match its validated inventory.');
  }

  return {
    selectionId: result.selectionId,
    directoryName: result.directoryName,
    totalBytes,
    files
  };
}

export function chooseExternalProgramEntryPoint(
  selection: ExternalProgramDirectorySelection,
  currentEntryPoint: string | null
): string | null {
  if (currentEntryPoint
      && isSupportedExternalProgramEntryPoint(currentEntryPoint)
      && selection.files.some(file => file.resourceRelativePath === currentEntryPoint)) {
    return currentEntryPoint;
  }
  return null;
}

export function isSupportedExternalProgramEntryPoint(resourceRelativePath: string): boolean {
  return resourceRelativePath.toLocaleLowerCase('en-US').endsWith('.exe');
}

function isCanonicalRelativePath(value: string): boolean {
  if (value.length === 0
      || value.length > 1_024
      || value.includes('\\')
      || value.includes(':')) {
    return false;
  }
  return value.split('/').every(segment => segment.length > 0
    && segment.length <= 255
    && segment !== '.'
    && segment !== '..'
    && segment === segment.trim()
    && !segment.endsWith('.')
    && !/[<>"|?*]/u.test(segment)
    && segment === segment.normalize('NFC')
    && !isWindowsReservedPathSegment(segment)
    && ![...segment].some(character => character.charCodeAt(0) < 32));
}

function isWindowsReservedPathSegment(segment: string): boolean {
  const stem = segment.split('.')[0].toLocaleUpperCase('en-US');
  return /^(?:CON|PRN|AUX|NUL|CLOCK\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])$/u.test(stem);
}
