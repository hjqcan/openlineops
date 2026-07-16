import { app, dialog } from 'electron';
import { createHash } from 'node:crypto';
import { lstatSync } from 'node:fs';
import path from 'node:path';
import type {
  TraceArtifactSaveOptions,
  TraceArtifactSaveResult
} from '../shared/desktop-api.js';
import {
  saveTraceArtifactToPath,
  validateTraceArtifactSaveOptions
} from './trace-artifact-save-core.js';

export async function saveTraceArtifact(
  apiBaseUrl: string,
  apiAccessToken: string,
  options: TraceArtifactSaveOptions,
  assertSessionActive: () => void
): Promise<TraceArtifactSaveResult> {
  validateTraceArtifactSaveOptions(options);
  const automatedDestination = resolveAutomationDestination(options);
  if (automatedDestination !== null) {
    return saveTraceArtifactToPath(
      apiBaseUrl,
      apiAccessToken,
      options,
      automatedDestination,
      assertSessionActive);
  }

  const result = await dialog.showSaveDialog({
    title: 'Save verified production evidence',
    defaultPath: path.join(app.getPath('downloads'), options.fileName),
    buttonLabel: 'Save evidence',
    properties: ['showOverwriteConfirmation', 'createDirectory']
  });
  if (result.canceled || !result.filePath) {
    return {
      canceled: true,
      path: null,
      sizeBytes: null,
      sha256: null
    };
  }

  return saveTraceArtifactToPath(
    apiBaseUrl,
    apiAccessToken,
    options,
    result.filePath,
    assertSessionActive);
}

function resolveAutomationDestination(options: TraceArtifactSaveOptions): string | null {
  const configuredRoot = process.env.OPENLINEOPS_E2E_TRACE_ARTIFACT_SAVE_ROOT;
  if (configuredRoot === undefined) {
    return null;
  }
  if (!path.isAbsolute(configuredRoot)) {
    throw new Error('OPENLINEOPS_E2E_TRACE_ARTIFACT_SAVE_ROOT must be absolute.');
  }

  const root = path.normalize(configuredRoot);
  const metadata = lstatSync(root);
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    throw new Error('The automated Trace artifact save root must be a real directory.');
  }

  const storageIdentity = createHash('sha256')
    .update(options.storageKey, 'utf8')
    .digest('hex')
    .slice(0, 16);
  return path.join(root, `${storageIdentity}-${options.fileName}`);
}
