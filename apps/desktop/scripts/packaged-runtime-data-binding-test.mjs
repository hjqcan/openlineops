import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import fileSystem, {
  existsSync,
  linkSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  symlinkSync,
  unlinkSync,
  writeFileSync
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  computeRuntimeContentSha256,
  ensureCanonicalDesktopUserDataDirectory,
  ensurePackagedRuntimeDataBinding,
  validatePackagedContentUserDataSeparation
} from '../dist-electron/main/packaged-runtime-data-binding.js';

const databaseNames = [
  'openlineops-runtime.sqlite',
  'openlineops-production-coordination.sqlite',
  'openlineops-traceability.sqlite',
  'openlineops-devices.sqlite',
  'openlineops-operations.sqlite',
  'openlineops-plugin-events.sqlite'
];

test('desktop package defines one canonical product identity for default user data', () => {
  const desktopPackage = JSON.parse(
    readFileSync(new URL('../package.json', import.meta.url), 'utf8'));

  assert.equal(desktopPackage.productName, 'OpenLineOps');
  assert.equal(path.basename(desktopPackage.productName), desktopPackage.productName);
  assert.doesNotMatch(desktopPackage.productName, /[<>:"/\\|?*\u0000-\u001f]/u);
});

function createFixture() {
  const physicalTempRoot = fileSystem.realpathSync.native(os.tmpdir());
  const root = mkdtempSync(path.join(physicalTempRoot, 'openlineops-runtime-binding-'));
  const packagedContent = path.join(root, 'package');
  const userData = path.join(root, 'user-data');
  const data = path.join(userData, 'data');
  const state = path.join(data, 'runtime-state');
  mkdirSync(packagedContent);
  mkdirSync(data, { recursive: true });
  writeFixtureFile(packagedContent, 'Z-ordinal-runtime-order.bin', 'uppercase-first');
  writeFixtureFile(packagedContent, 'a-ordinal-runtime-order.bin', 'lowercase-second');
  writeFixtureFile(packagedContent, 'OpenLineOps.exe', 'desktop-executable');
  writeFixtureFile(
    packagedContent,
    'resources/app/dist/index.html',
    '<main>OpenLineOps</main>');
  writeFixtureFile(
    packagedContent,
    'resources/app/dist-electron/main/main.js',
    'desktop-main');
  writeFixtureFile(
    packagedContent,
    'resources/app/dist-electron/preload/preload.cjs',
    'desktop-preload');
  writeFixtureFile(
    packagedContent,
    'resources/app/package.json',
    '{"name":"@openlineops/desktop","productName":"OpenLineOps"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/OpenLineOps.Api.exe',
    'api-executable');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/OpenLineOps.Api.dll',
    'api-runtime');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/OpenLineOps.Api.deps.json',
    '{"api":"deps"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/OpenLineOps.Api.runtimeconfig.json',
    '{"api":"runtimeconfig"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/appsettings.json',
    '{"runtime":"formal"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/api/ThirdParty.Runtime.dll',
    'third-party-runtime');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe',
    'script-worker');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.dll',
    'script-worker-dll');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.deps.json',
    '{"worker":"deps"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.runtimeconfig.json',
    '{"worker":"runtimeconfig"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe',
    'plugin-host');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.dll',
    'plugin-host-dll');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.deps.json',
    '{"plugin":"deps"}');
  writeFixtureFile(
    packagedContent,
    'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.runtimeconfig.json',
    '{"plugin":"runtimeconfig"}');
  writeFixtureContentManifest(packagedContent);
  return { root, packagedContent, userData, data, state };
}

function writeFixtureFile(root, relativePath, content) {
  const filePath = path.join(root, ...relativePath.split('/'));
  mkdirSync(path.dirname(filePath), { recursive: true });
  writeFileSync(filePath, content);
}

function writeFixtureContentManifest(packageRoot) {
  const files = [];
  collectFixtureFiles(packageRoot, packageRoot, files);
  files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  writeFileSync(
    path.join(packageRoot, 'openlineops-package-content.json'),
    `${JSON.stringify({
      schema: 'openlineops.desktop-package-content',
      files
    }, null, 2)}\n`);
}

function collectFixtureFiles(packageRoot, currentDirectory, files) {
  for (const entry of readdirSync(currentDirectory, { withFileTypes: true })) {
    const entryPath = path.join(currentDirectory, entry.name);
    if (entry.isDirectory()) {
      collectFixtureFiles(packageRoot, entryPath, files);
      continue;
    }
    const relativePath = path.relative(packageRoot, entryPath).replaceAll('\\', '/');
    if (relativePath === 'openlineops-package-content.json') {
      continue;
    }
    const content = readFileSync(entryPath);
    files.push({
      path: relativePath,
      sha256: createHash('sha256').update(content).digest('hex'),
      size: statSync(entryPath).size
    });
  }
}

function createIncompatibleRuntimeState(fixture, marker = '{"old":true}') {
  mkdirSync(fixture.state, { recursive: true });
  writeFileSync(path.join(fixture.state, 'runtime-content-binding.json'), marker);
  createRuntimeState(fixture.state);
}

function createRuntimeState(state) {
  for (const name of databaseNames) {
    writeFileSync(path.join(state, name), name);
    writeFileSync(path.join(state, `${name}-journal`), 'journal');
    writeFileSync(path.join(state, `${name}-wal`), 'wal');
    writeFileSync(path.join(state, `${name}-shm`), 'shm');
  }
  mkdirSync(path.join(state, 'trace-artifacts'), { recursive: true });
  writeFileSync(path.join(state, 'trace-artifacts', 'evidence.bin'), 'evidence');
  mkdirSync(path.join(state, 'external-program-workspaces'), { recursive: true });
  writeFileSync(path.join(state, 'external-program-workspaces', 'work.bin'), 'work');
  mkdirSync(path.join(state, 'external-program-evidence'), { recursive: true });
  writeFileSync(path.join(state, 'external-program-evidence', 'old.bin'), 'old-evidence');
}

function assertRuntimeStateRemoved(state) {
  for (const name of databaseNames) {
    assert.equal(readOptional(path.join(state, name)), null);
    assert.equal(readOptional(path.join(state, `${name}-journal`)), null);
    assert.equal(readOptional(path.join(state, `${name}-wal`)), null);
    assert.equal(readOptional(path.join(state, `${name}-shm`)), null);
  }
  assert.equal(readOptional(path.join(state, 'trace-artifacts', 'evidence.bin')), null);
  assert.equal(readOptional(path.join(state, 'external-program-workspaces', 'work.bin')), null);
  assert.equal(readOptional(path.join(state, 'external-program-evidence', 'old.bin')), null);
}

function readOptional(filePath) {
  try {
    return readFileSync(filePath, 'utf8');
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
}

function inactiveStateNames(data) {
  return readdirSync(data).filter(name => name.startsWith('.runtime-state.'));
}

test('first formal binding atomically replaces incompatible runtime state but preserves credentials and packages', () => {
  const fixture = createFixture();
  try {
    createIncompatibleRuntimeState(fixture);
    mkdirSync(path.join(fixture.data, 'security'));
    writeFileSync(path.join(fixture.data, 'security', 'studio-standard.token'), 'private');
    mkdirSync(path.join(fixture.data, 'station-packages', 'keys'), { recursive: true });
    writeFileSync(path.join(fixture.data, 'station-packages', 'keys', 'release.pem'), 'private-key');

    const result = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    result.commit();
    assert.equal(result.reset, true);
    assert.match(result.runtimeSha256, /^[a-f0-9]{64}$/);
    assert.equal(result.runtimeStateDirectory, fixture.state);
    assertRuntimeStateRemoved(fixture.state);
    assert.equal(
      readFileSync(path.join(fixture.data, 'security', 'studio-standard.token'), 'utf8'),
      'private');
    assert.equal(
      readFileSync(path.join(fixture.data, 'station-packages', 'keys', 'release.pem'), 'utf8'),
      'private-key');
    assert.equal(readOptional(path.join(fixture.data, 'runtime-content-binding.json')), null);
    assert.deepEqual(inactiveStateNames(fixture.data), []);

    const marker = JSON.parse(readFileSync(
      path.join(fixture.state, 'runtime-content-binding.json'),
      'utf8'));
    assert.deepEqual(
      Object.keys(marker).sort(),
      ['activationId', 'runtimeSha256', 'schema']);
    assert.equal(marker.schema, 'openlineops.desktop-runtime-content-binding');
    assert.equal(marker.runtimeSha256, result.runtimeSha256);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('same packaged content preserves state while changed content replaces the whole state root exactly once', () => {
  const fixture = createFixture();
  try {
    const first = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    first.commit();
    writeFileSync(path.join(fixture.state, 'openlineops-traceability.sqlite'), 'current-data');

    const same = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    same.commit();
    assert.equal(same.reset, false);
    assert.equal(same.runtimeSha256, first.runtimeSha256);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-traceability.sqlite'), 'utf8'),
      'current-data');

    writeFixtureFile(
      fixture.packagedContent,
      'resources/app/runtime/api/ThirdParty.Runtime.dll',
      'changed-third-party-runtime');
    writeFixtureContentManifest(fixture.packagedContent);
    const changed = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    changed.commit();
    assert.equal(changed.reset, true);
    assert.notEqual(changed.runtimeSha256, first.runtimeSha256);
    assert.equal(readOptional(path.join(fixture.state, 'openlineops-traceability.sqlite')), null);
    assert.deepEqual(inactiveStateNames(fixture.data), []);

    writeFileSync(path.join(fixture.state, 'openlineops-traceability.sqlite'), 'new-data');
    const preserved = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    preserved.commit();
    assert.equal(preserved.reset, false);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-traceability.sqlite'), 'utf8'),
      'new-data');
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('malformed active binding is quarantined and replaced rather than repaired in place', () => {
  const fixture = createFixture();
  try {
    createIncompatibleRuntimeState(fixture, JSON.stringify({
      schema: 'openlineops.desktop-runtime-content-binding',
      runtimeSha256: computeRuntimeContentSha256(fixture.packagedContent),
      unexpectedProperty: true
    }));

    const result = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    result.commit();
    assert.equal(result.reset, true);
    assertRuntimeStateRemoved(fixture.state);
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('unsafe active marker fails before runtime state is moved or deleted', () => {
  const fixture = createFixture();
  try {
    mkdirSync(fixture.state, { recursive: true });
    createRuntimeState(fixture.state);
    const externalMarker = path.join(fixture.root, 'shared-marker.json');
    writeFileSync(externalMarker, '{}');
    linkSync(externalMarker, path.join(fixture.state, 'runtime-content-binding.json'));

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(fixture.packagedContent, fixture.userData),
      /must be one plain regular file/u);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-traceability.sqlite'), 'utf8'),
      'openlineops-traceability.sqlite');
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('unsafe nested runtime junction fails before atomic replacement', () => {
  const fixture = createFixture();
  try {
    createIncompatibleRuntimeState(fixture);
    rmSync(path.join(fixture.state, 'external-program-workspaces'), {
      recursive: true,
      force: true
    });
    const externalDirectory = path.join(fixture.root, 'external-workspace');
    mkdirSync(externalDirectory);
    writeFileSync(path.join(externalDirectory, 'must-survive.bin'), 'outside');
    symlinkSync(
      externalDirectory,
      path.join(fixture.state, 'external-program-workspaces'),
      process.platform === 'win32' ? 'junction' : 'dir');

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(fixture.packagedContent, fixture.userData),
      /cannot contain a symbolic link or junction/u);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'utf8'),
      'openlineops-runtime.sqlite');
    assert.equal(
      readFileSync(path.join(externalDirectory, 'must-survive.bin'), 'utf8'),
      'outside');
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('an uncommitted activation is rolled back after a crash before the upgrade is retried', () => {
  const fixture = createFixture();
  try {
    ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData).commit();
    writeFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'old-state');
    writeFixtureFile(
      fixture.packagedContent,
      'resources/app/runtime/api/ThirdParty.Runtime.dll',
      'upgraded-runtime');
    writeFixtureContentManifest(fixture.packagedContent);

    const abandoned = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    assert.equal(abandoned.reset, true);
    assert.equal(
      readFileSync(path.join(
        fixture.data,
        '.runtime-state.previous',
        'openlineops-runtime.sqlite'), 'utf8'),
      'old-state');
    assert.equal(
      existsSync(path.join(fixture.state, 'runtime-activation-committed.json')),
      false);

    const recovered = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    assert.equal(
      readFileSync(path.join(
        fixture.data,
        '.runtime-state.previous',
        'openlineops-runtime.sqlite'), 'utf8'),
      'old-state');
    recovered.commit();
    assert.equal(recovered.reset, true);
    assert.equal(readOptional(path.join(fixture.state, 'openlineops-runtime.sqlite')), null);
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('an uncommitted first activation is discarded after a crash', () => {
  const fixture = createFixture();
  try {
    const abandoned = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    writeFileSync(path.join(fixture.state, 'startup-partial.bin'), 'partial');
    assert.equal(abandoned.reset, true);

    const recovered = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData);
    assert.equal(readOptional(path.join(fixture.state, 'startup-partial.bin')), null);
    recovered.commit();
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('complete packaged content digest changes for every executable content class', () => {
  const fixture = createFixture();
  try {
    const mutationPaths = [
      'OpenLineOps.exe',
      'resources/app/dist-electron/main/main.js',
      'resources/app/runtime/api/appsettings.json',
      'resources/app/runtime/api/ThirdParty.Runtime.dll',
      'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe',
      'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe'
    ];
    let previous = computeRuntimeContentSha256(fixture.packagedContent);
    for (const [index, relativePath] of mutationPaths.entries()) {
      writeFixtureFile(fixture.packagedContent, relativePath, `mutated-${index}`);
      writeFixtureContentManifest(fixture.packagedContent);
      const next = computeRuntimeContentSha256(fixture.packagedContent);
      assert.notEqual(next, previous, `${relativePath} must participate in the digest.`);
      previous = next;
    }
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('unmanifested packaged content fails before the committed runtime state is touched', () => {
  const fixture = createFixture();
  try {
    ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData).commit();
    writeFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'must-survive');
    writeFixtureFile(
      fixture.packagedContent,
      'resources/app/runtime/api/unmanifested.dll',
      'not-in-the-formal-inventory');

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        fixture.userData),
      /inventory does not exactly match its manifest/u);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'utf8'),
      'must-survive');
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('a manifest cannot legitimize a package missing a formal runtime entry point', () => {
  const fixture = createFixture();
  try {
    ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData).commit();
    writeFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'must-survive');
    rmSync(path.join(
      fixture.packagedContent,
      'resources/app/runtime/api/OpenLineOps.Api.exe'));
    writeFixtureContentManifest(fixture.packagedContent);

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        fixture.userData),
      /missing a formal runtime entry point/u);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'utf8'),
      'must-survive');
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('packaged content and user data roots cannot contain one another', () => {
  const fixture = createFixture();
  try {
    const nestedUserData = path.join(fixture.packagedContent, 'nested-user-data');
    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        nestedUserData),
      /must not contain one another/u);
    assert.equal(existsSync(nestedUserData), false);

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        fixture.root),
      /must not contain one another/u);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('concurrent packaged content change aborts before the old active state is quarantined', async () => {
  const fixture = createFixture();
  let mutator;
  try {
    ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      fixture.userData).commit();
    const markerPath = path.join(fixture.state, 'runtime-content-binding.json');
    const originalMarker = readFileSync(markerPath, 'utf8');
    writeFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'must-survive');

    const mutationTarget = path.join(fixture.packagedContent, '000-target.bin');
    writeFileSync(mutationTarget, 'before');
    writeFileSync(
      path.join(fixture.packagedContent, 'zzz-padding.bin'),
      Buffer.alloc(128 * 1024 * 1024, 0x5a));
    writeFixtureContentManifest(fixture.packagedContent);
    mutator = spawn(
      process.execPath,
      [
        '-e',
        `setTimeout(() => require('node:fs').writeFileSync(${JSON.stringify(mutationTarget)}, 'after'), 50);`
      ],
      { stdio: 'ignore', windowsHide: true });

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        fixture.userData),
      /changed while Desktop runtime state was being bound/u);
    await waitForChild(mutator);
    assert.equal(
      readFileSync(path.join(fixture.state, 'openlineops-runtime.sqlite'), 'utf8'),
      'must-survive');
    assert.equal(readFileSync(markerPath, 'utf8'), originalMarker);
    assert.deepEqual(inactiveStateNames(fixture.data), []);
  } finally {
    if (mutator?.exitCode === null) {
      mutator.kill();
      await waitForChild(mutator).catch(() => {});
    }
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

function waitForChild(child) {
  if (child.exitCode !== null) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', code => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Mutation helper exited with ${code}.`));
      }
    });
  });
}

test('destructive binding rejects a relative user data root', () => {
  const fixture = createFixture();
  try {
    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        'relative-user-data'),
      /must be canonical and absolute/u);
    const nonCanonicalUserData = `${fixture.root}${path.sep}missing${path.sep}..${path.sep}user-data`;
    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        nonCanonicalUserData),
      /must be canonical and absolute/u);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('first launch creates each missing canonical user data directory before binding', () => {
  const fixture = createFixture();
  try {
    const freshUserData = path.join(fixture.root, 'fresh', 'nested', 'user-data');
    const result = ensurePackagedRuntimeDataBinding(
      fixture.packagedContent,
      freshUserData);
    result.commit();
    assert.equal(result.reset, true);
    assert.equal(
      result.runtimeStateDirectory,
      path.join(freshUserData, 'data', 'runtime-state'));
    assert.match(
      readFileSync(
        path.join(result.runtimeStateDirectory, 'runtime-content-binding.json'),
        'utf8'),
      /openlineops\.desktop-runtime-content-binding/u);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('destructive binding rejects a user data path whose parent traverses a junction', () => {
  const fixture = createFixture();
  try {
    const physicalParent = path.join(fixture.root, 'physical-parent');
    const physicalUserData = path.join(physicalParent, 'user-data');
    const physicalData = path.join(physicalUserData, 'data');
    mkdirSync(physicalData, { recursive: true });
    writeFileSync(path.join(physicalData, 'must-survive.bin'), 'must-survive');

    const aliasedParent = path.join(fixture.root, 'aliased-parent');
    symlinkSync(
      physicalParent,
      aliasedParent,
      process.platform === 'win32' ? 'junction' : 'dir');

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        fixture.packagedContent,
        path.join(aliasedParent, 'user-data')),
      /must not traverse a symbolic link or junction/u);
    assert.equal(
      readFileSync(path.join(physicalData, 'must-survive.bin'), 'utf8'),
      'must-survive');
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('destructive binding rejects a packaged content path whose parent traverses a junction', () => {
  const fixture = createFixture();
  try {
    const aliasedRoot = path.join(path.dirname(fixture.root), `${path.basename(fixture.root)}-alias`);
    symlinkSync(
      fixture.root,
      aliasedRoot,
      process.platform === 'win32' ? 'junction' : 'dir');
    const aliasedPackagedContent = path.join(aliasedRoot, 'package');

    assert.throws(
      () => ensurePackagedRuntimeDataBinding(
        aliasedPackagedContent,
        fixture.userData),
      /must not traverse a symbolic link or junction/u);
    assert.equal(existsSync(fixture.state), false);
  } finally {
    const aliasedRoot = `${fixture.root}-alias`;
    if (existsSync(aliasedRoot)) {
      unlinkSync(aliasedRoot);
    }
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('native directory spelling is accepted only when its physical identity remains exact', context => {
  const fixture = createFixture();
  try {
    const selectedMetadata = fileSystem.lstatSync(fixture.packagedContent, { bigint: true });
    const differentMetadata = fileSystem.lstatSync(fixture.userData, { bigint: true });
    const physicalParent = path.join(path.parse(fixture.packagedContent).root, 'openlineops-physical');
    const physicalPath = path.join(physicalParent, 'package');
    const originalRealpath = fileSystem.realpathSync.native;
    const originalLstat = fileSystem.lstatSync;
    const realpathMock = context.mock.method(fileSystem.realpathSync, 'native', value => (
      value === fixture.packagedContent ? physicalPath : originalRealpath(value)));
    const lstatMock = context.mock.method(fileSystem, 'lstatSync', (value, options) => {
      if (value === physicalParent || value === physicalPath) {
        return selectedMetadata;
      }
      return originalLstat(value, options);
    });

    assert.doesNotThrow(() => validatePackagedContentUserDataSeparation(
      fixture.packagedContent,
      fixture.userData));

    lstatMock.mock.mockImplementation((value, options) => {
      if (value === physicalParent) {
        return selectedMetadata;
      }
      if (value === physicalPath) {
        return differentMetadata;
      }
      return originalLstat(value, options);
    });
    assert.throws(
      () => validatePackagedContentUserDataSeparation(
        fixture.packagedContent,
        fixture.userData),
      /physical identity was being verified/u);

    if (process.platform === 'win32') {
      realpathMock.mock.mockImplementation(value => (
        value === fixture.packagedContent
          ? '\\\\server\\share\\package'
          : originalRealpath(value)));
      assert.throws(
        () => validatePackagedContentUserDataSeparation(
          fixture.packagedContent,
          fixture.userData),
        /physical path must be one canonical local path/u);
    }
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('separation compares a missing user data root through its physical existing ancestor', context => {
  const fixture = createFixture();
  try {
    const selectedAliasRoot = path.join(fixture.root, 'selected-alias-root');
    mkdirSync(selectedAliasRoot);
    const prospectiveUserData = path.join(selectedAliasRoot, 'prospective-user-data');
    const physicalAliasRoot = path.join(fixture.packagedContent, 'physical-alias-root');
    const aliasMetadata = fileSystem.lstatSync(selectedAliasRoot, { bigint: true });
    const originalRealpath = fileSystem.realpathSync.native;
    const originalLstat = fileSystem.lstatSync;
    context.mock.method(fileSystem.realpathSync, 'native', value => (
      value === selectedAliasRoot ? physicalAliasRoot : originalRealpath(value)));
    context.mock.method(fileSystem, 'lstatSync', (value, options) => {
      if (value === physicalAliasRoot) {
        return aliasMetadata;
      }
      return originalLstat(value, options);
    });

    assert.throws(
      () => validatePackagedContentUserDataSeparation(
        fixture.packagedContent,
        prospectiveUserData),
      /must not contain one another/u);
    assert.equal(existsSync(prospectiveUserData), false);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});

test('missing user data directories are created only below the verified physical ancestor', context => {
  const fixture = createFixture();
  try {
    const selectedAncestor = path.join(fixture.root, 'selected-user-root');
    mkdirSync(selectedAncestor);
    const requestedUserData = path.join(selectedAncestor, 'nested', 'user-data');
    const physicalAncestor = path.join(
      path.parse(selectedAncestor).root,
      'openlineops-physical-user-root');
    const selectedMetadata = fileSystem.lstatSync(selectedAncestor, { bigint: true });
    const originalRealpath = fileSystem.realpathSync.native;
    const originalLstat = fileSystem.lstatSync;
    const originalMkdir = fileSystem.mkdirSync;
    const createdPaths = [];
    context.mock.method(fileSystem.realpathSync, 'native', value => (
      value === selectedAncestor || value.startsWith(`${physicalAncestor}${path.sep}`)
        ? value === selectedAncestor ? physicalAncestor : value
        : originalRealpath(value)));
    context.mock.method(fileSystem, 'lstatSync', (value, options) => (
      value === physicalAncestor || value.startsWith(`${physicalAncestor}${path.sep}`)
        ? selectedMetadata
        : originalLstat(value, options)));
    context.mock.method(fileSystem, 'mkdirSync', (value, options) => {
      if (value === physicalAncestor || value.startsWith(`${physicalAncestor}${path.sep}`)) {
        createdPaths.push(value);
        return undefined;
      }
      return originalMkdir(value, options);
    });

    const result = ensureCanonicalDesktopUserDataDirectory(requestedUserData);
    assert.equal(result, path.join(physicalAncestor, 'nested', 'user-data'));
    assert.deepEqual(createdPaths, [
      path.join(physicalAncestor, 'nested'),
      path.join(physicalAncestor, 'nested', 'user-data')
    ]);
    assert.equal(existsSync(path.join(selectedAncestor, 'nested')), false);
  } finally {
    rmSync(fixture.root, { recursive: true, force: true });
  }
});
