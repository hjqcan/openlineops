import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  createDevelopmentHostDefinitions,
  developmentHostConfiguration,
  prepareDevelopmentHosts
} from './development-hosts.mjs';

test('development host definitions are complete and use one strict configuration', () => {
  const repoRoot = path.resolve('openlineops-development-host-contract');
  const hosts = createDevelopmentHostDefinitions(repoRoot);

  assert.equal(developmentHostConfiguration, 'Debug');
  assert.deepEqual(
    hosts.map(host => host.projectName),
    [
      'OpenLineOps.Api',
      'OpenLineOps.ScriptWorker',
      'OpenLineOps.PluginHost'
    ]);
  for (const host of hosts) {
    assert.equal(
      host.projectPath,
      path.join(
        repoRoot,
        'src',
        host.projectName,
        `${host.projectName}.csproj`));
    assert.equal(
      host.assemblyPath,
      path.join(
        repoRoot,
        'src',
        host.projectName,
        'bin',
        'Debug',
        'net10.0',
        `${host.projectName}.dll`));
  }
});

test('development host preparation builds and verifies every required host', async () => {
  const repoRoot = await fs.mkdtemp(
    path.join(await fs.realpath(os.tmpdir()), 'openlineops-dev-host-test-'));
  const builtProjects = [];
  try {
    const hosts = await prepareDevelopmentHosts({
      repoRoot,
      build: async (host, configuration) => {
        builtProjects.push([host.projectName, configuration]);
        await fs.mkdir(path.dirname(host.assemblyPath), { recursive: true });
        await fs.writeFile(host.assemblyPath, host.projectName, 'utf8');
      }
    });

    assert.deepEqual(
      builtProjects,
      hosts.map(host => [host.projectName, 'Debug']));
  } finally {
    await fs.rm(repoRoot, { force: true, recursive: true });
  }
});

test('development host preparation rejects a build with a missing output', async () => {
  const repoRoot = path.resolve('openlineops-missing-development-host');
  await assert.rejects(
    prepareDevelopmentHosts({
      repoRoot,
      build: async () => {},
      inspectFile: async () => {
        throw new Error('not found');
      }
    }),
    /Development host build did not produce OpenLineOps\.Api/u);
});
