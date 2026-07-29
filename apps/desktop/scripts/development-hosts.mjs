import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const contractPath = fileURLToPath(
  new URL('../development-hosts.json', import.meta.url));
const developmentHostContract = parseDevelopmentHostContract(
  JSON.parse(await fs.readFile(contractPath, 'utf8')));

export const developmentHostConfiguration =
  developmentHostContract.configuration;

export function createDevelopmentHostDefinitions(repoRoot) {
  if (typeof repoRoot !== 'string' || !path.isAbsolute(repoRoot)) {
    throw new Error('The development host repository root must be absolute.');
  }

  return developmentHostContract.projects.map(projectName => ({
    projectName,
    projectPath: path.join(
      repoRoot,
      'src',
      projectName,
      `${projectName}.csproj`),
    assemblyPath: path.join(
      repoRoot,
      'src',
      projectName,
      'bin',
      developmentHostConfiguration,
      developmentHostContract.targetFramework,
      `${projectName}.dll`)
  }));
}

export async function prepareDevelopmentHosts({
  repoRoot,
  build,
  inspectFile = filePath => fs.stat(filePath)
}) {
  if (typeof build !== 'function') {
    throw new Error('A development host build function is required.');
  }
  if (typeof inspectFile !== 'function') {
    throw new Error('A development host file inspector is required.');
  }

  const hosts = createDevelopmentHostDefinitions(repoRoot);
  for (const host of hosts) {
    await build(host, developmentHostConfiguration);
    const assembly = await inspectFile(host.assemblyPath).catch(() => null);
    if (!assembly?.isFile()) {
      throw new Error(
        `Development host build did not produce ${host.projectName}: ${
          host.assemblyPath}`);
    }
  }
  return hosts;
}

function parseDevelopmentHostContract(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('The development host contract must be one JSON object.');
  }
  const keys = Object.keys(value).sort();
  if (JSON.stringify(keys)
      !== JSON.stringify(['configuration', 'projects', 'targetFramework'])) {
    throw new Error(
      'The development host contract contains unknown or missing properties.');
  }
  const configuration = requireCanonicalToken(
    value.configuration,
    'configuration');
  const targetFramework = requireCanonicalToken(
    value.targetFramework,
    'target framework');
  if (!Array.isArray(value.projects) || value.projects.length === 0) {
    throw new Error(
      'The development host contract must declare at least one project.');
  }
  const projects = value.projects.map(projectName =>
    requireCanonicalProjectName(projectName));
  if (new Set(projects).size !== projects.length) {
    throw new Error(
      'The development host contract cannot contain duplicate projects.');
  }
  return Object.freeze({
    configuration,
    targetFramework,
    projects: Object.freeze(projects)
  });
}

function requireCanonicalToken(value, description) {
  if (typeof value !== 'string'
      || !/^[A-Za-z0-9.]+$/u.test(value)
      || value.trim() !== value) {
    throw new Error(
      `The development host ${description} is not canonical.`);
  }
  return value;
}

function requireCanonicalProjectName(value) {
  if (typeof value !== 'string'
      || !/^OpenLineOps\.[A-Za-z][A-Za-z0-9]*$/u.test(value)) {
    throw new Error(
      'The development host contract contains a non-canonical project name.');
  }
  return value;
}
