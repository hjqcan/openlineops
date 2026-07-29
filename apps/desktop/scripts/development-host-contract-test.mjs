import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  requireDevelopmentHostAssembly,
  resolveDevelopmentHostDefinitions
} from '../dist-electron/main/development-host-contract.js';
import {
  createDevelopmentHostDefinitions
} from './development-hosts.mjs';

const desktopRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');

test('runtime and smoke resolve development hosts from one contract', async () => {
  const runtimeDefinitions = resolveDevelopmentHostDefinitions(
    desktopRoot,
    repoRoot);
  const smokeDefinitions = createDevelopmentHostDefinitions(repoRoot);
  assert.deepEqual(runtimeDefinitions, smokeDefinitions.map(host => ({
    projectName: host.projectName,
    assemblyPath: host.assemblyPath
  })));
  for (const definition of runtimeDefinitions) {
    assert.equal(
      requireDevelopmentHostAssembly(
        runtimeDefinitions,
        definition.projectName),
      definition.assemblyPath);
  }

  const mainSource = await fs.readFile(
    new URL('../src/main/main.ts', import.meta.url),
    'utf8');
  const start = mainSource.indexOf('function createBackendLaunchConfig');
  const end = mainSource.indexOf('function provisionLocalApiCredentials');
  assert(start >= 0 && end > start);
  const launchSource = mainSource.slice(start, end);
  assert.match(
    launchSource,
    /resolveDevelopmentHostDefinitions\(\s*appPath,\s*repoRoot\)/u);
  assert.doesNotMatch(
    launchSource,
    /['"]bin['"][\s\S]{0,80}['"]Debug['"]/u,
    'Development runtime paths must not duplicate the shared host contract.');
});

test('runtime development host contract rejects unknown and duplicate entries', async () => {
  const fixtureRoot = await fs.mkdtemp(
    path.join(await fs.realpath(os.tmpdir()), 'openlineops-host-contract-'));
  try {
    await fs.writeFile(
      path.join(fixtureRoot, 'development-hosts.json'),
      JSON.stringify({
        configuration: 'Debug',
        targetFramework: 'net10.0',
        projects: ['OpenLineOps.Api', 'OpenLineOps.Api']
      }),
      'utf8');
    assert.throws(
      () => resolveDevelopmentHostDefinitions(fixtureRoot, repoRoot),
      /duplicate projects/u);

    await fs.writeFile(
      path.join(fixtureRoot, 'development-hosts.json'),
      JSON.stringify({
        configuration: 'Debug',
        targetFramework: 'net10.0',
        projects: ['OpenLineOps.Api'],
        unexpected: true
      }),
      'utf8');
    assert.throws(
      () => resolveDevelopmentHostDefinitions(fixtureRoot, repoRoot),
      /unknown or missing properties/u);
  } finally {
    await fs.rm(fixtureRoot, { force: true, recursive: true });
  }
});
