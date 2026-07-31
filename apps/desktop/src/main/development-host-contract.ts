import { readFileSync } from 'node:fs';
import path from 'node:path';

interface DevelopmentHostContractDocument {
  configuration: string;
  targetFramework: string;
  projects: string[];
}

export interface DevelopmentHostDefinition {
  projectName: string;
  assemblyPath: string;
}

export function resolveDevelopmentHostDefinitions(
  desktopRoot: string,
  repoRoot: string
): DevelopmentHostDefinition[] {
  requireCanonicalAbsolutePath(desktopRoot, 'desktop root');
  requireCanonicalAbsolutePath(repoRoot, 'repository root');
  const contractPath = path.join(desktopRoot, 'development-hosts.json');
  const contract = parseDevelopmentHostContract(
    JSON.parse(readFileSync(contractPath, 'utf8')) as unknown);
  return contract.projects.map(projectName => ({
    projectName,
    assemblyPath: path.join(
      repoRoot,
      'src',
      projectName,
      'bin',
      contract.configuration,
      contract.targetFramework,
      `${projectName}.dll`)
  }));
}

export function requireDevelopmentHostAssembly(
  definitions: DevelopmentHostDefinition[],
  projectName: string
): string {
  const matches = definitions.filter(
    definition => definition.projectName === projectName);
  if (matches.length !== 1) {
    throw new Error(
      `The development host contract must declare ${projectName} exactly once.`);
  }
  return matches[0].assemblyPath;
}

function parseDevelopmentHostContract(
  value: unknown
): DevelopmentHostContractDocument {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('The development host contract must be one JSON object.');
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  if (JSON.stringify(keys)
      !== JSON.stringify(['configuration', 'projects', 'targetFramework'])) {
    throw new Error(
      'The development host contract contains unknown or missing properties.');
  }
  const configuration = requireCanonicalToken(
    record.configuration,
    'configuration');
  const targetFramework = requireCanonicalToken(
    record.targetFramework,
    'target framework');
  if (!Array.isArray(record.projects) || record.projects.length === 0) {
    throw new Error(
      'The development host contract must declare at least one project.');
  }
  const projects = record.projects.map(projectName =>
    requireCanonicalProjectName(projectName));
  if (new Set(projects).size !== projects.length) {
    throw new Error(
      'The development host contract cannot contain duplicate projects.');
  }
  return { configuration, targetFramework, projects };
}

function requireCanonicalToken(value: unknown, description: string): string {
  if (typeof value !== 'string'
      || !/^[A-Za-z0-9.]+$/u.test(value)
      || value.trim() !== value) {
    throw new Error(
      `The development host ${description} is not canonical.`);
  }
  return value;
}

function requireCanonicalProjectName(value: unknown): string {
  if (typeof value !== 'string'
      || !/^OpenLineOps\.[A-Za-z][A-Za-z0-9]*$/u.test(value)) {
    throw new Error(
      'The development host contract contains a non-canonical project name.');
  }
  return value;
}

function requireCanonicalAbsolutePath(
  value: string,
  description: string
): void {
  if (!path.isAbsolute(value) || path.resolve(value) !== value) {
    throw new Error(
      `The development host ${description} must be canonical and absolute.`);
  }
}
