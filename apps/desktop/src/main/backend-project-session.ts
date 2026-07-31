import { lstatSync } from 'node:fs';
import path from 'node:path';

export const startupProjectFilesEnvironmentPrefix =
  'OpenLineOps__Projects__StartupWorkspaces__ProjectFiles__';
export const startupProjectFileEnvironmentKey =
  `${startupProjectFilesEnvironmentPrefix}0`;

export function resolveActiveProjectFile(projectFilePath: string | null): string | null {
  if (projectFilePath === null) {
    return null;
  }
  if (typeof projectFilePath !== 'string'
      || projectFilePath.length === 0
      || projectFilePath.trim() !== projectFilePath
      || projectFilePath.includes('\u0000')
      || !path.isAbsolute(projectFilePath)) {
    throw new Error('The active Project file must be one canonical absolute .oloproj path.');
  }

  const resolvedPath = path.resolve(projectFilePath);
  if (resolvedPath !== projectFilePath
      || path.extname(resolvedPath) !== '.oloproj') {
    throw new Error('The active Project file must be one canonical absolute .oloproj path.');
  }

  let parentPath = path.dirname(resolvedPath);
  while (true) {
    const parentMetadata = lstatSync(parentPath);
    if (!parentMetadata.isDirectory() || parentMetadata.isSymbolicLink()) {
      throw new Error(
        'Every active Project parent must be an ordinary non-symbolic directory.');
    }
    const ancestorPath = path.dirname(parentPath);
    if (ancestorPath === parentPath) {
      break;
    }
    parentPath = ancestorPath;
  }
  const metadata = lstatSync(resolvedPath);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error('The active Project file must be one ordinary non-symbolic file.');
  }

  return resolvedPath;
}

export function bindActiveProjectStartupWorkspace(
  environment: NodeJS.ProcessEnv,
  projectFilePath: string | null
): NodeJS.ProcessEnv {
  const boundEnvironment: NodeJS.ProcessEnv = { ...environment };
  for (const key of Object.keys(boundEnvironment)) {
    if (key.toLowerCase().startsWith(startupProjectFilesEnvironmentPrefix.toLowerCase())) {
      delete boundEnvironment[key];
    }
  }
  if (projectFilePath !== null) {
    boundEnvironment[startupProjectFileEnvironmentKey] = projectFilePath;
  }
  return boundEnvironment;
}
