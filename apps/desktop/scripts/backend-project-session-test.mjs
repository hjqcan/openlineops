import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rename, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  bindActiveProjectStartupWorkspace,
  resolveActiveProjectFile,
  startupProjectFileEnvironmentKey
} from '../dist-electron/main/backend-project-session.js';

test('active Project session accepts only a canonical ordinary .oloproj file', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'openlineops-project-session-'));
  try {
    const projectFile = path.join(root, 'line.oloproj');
    const wrongExtension = path.join(root, 'line.json');
    const uppercaseExtension = path.join(root, 'line.OLOPROJ');
    await writeFile(projectFile, '{}\n', 'utf8');
    await writeFile(wrongExtension, '{}\n', 'utf8');
    await writeFile(uppercaseExtension, '{}\n', 'utf8');

    assert.equal(resolveActiveProjectFile(projectFile), projectFile);
    assert.equal(resolveActiveProjectFile(null), null);
    // path.relative() returns an absolute path across Windows volumes, so use a
    // fixture that is relative regardless of the runner's workspace and temp drives.
    assert.throws(
      () => resolveActiveProjectFile(path.join('relative-project', 'line.oloproj')),
      /canonical absolute \.oloproj/u);
    assert.throws(
      () => resolveActiveProjectFile(`${projectFile} `),
      /canonical absolute \.oloproj/u);
    assert.throws(
      () => resolveActiveProjectFile(wrongExtension),
      /canonical absolute \.oloproj/u);
    assert.throws(
      () => resolveActiveProjectFile(uppercaseExtension),
      /canonical absolute \.oloproj/u);
    assert.throws(
      () => resolveActiveProjectFile(path.join(root, 'missing.oloproj')),
      /ENOENT/u);
    assert.throws(
      () => resolveActiveProjectFile(root),
      /canonical absolute \.oloproj/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('active Project session rejects a reparse-point Project parent', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'openlineops-project-session-link-'));
  try {
    const target = path.join(root, 'target');
    const linkedParent = path.join(root, 'linked-parent');
    await mkdir(target);
    const projectFile = path.join(target, 'line.oloproj');
    await writeFile(projectFile, '{}\n', 'utf8');
    await symlink(target, linkedParent, process.platform === 'win32' ? 'junction' : 'dir');

    assert.throws(
      () => resolveActiveProjectFile(path.join(linkedParent, 'line.oloproj')),
      /ordinary non-symbolic directory/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('backend restart revalidates a cached Project binding after path replacement', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'openlineops-project-session-restart-'));
  try {
    const activeParent = path.join(root, 'active');
    const originalParent = path.join(root, 'original');
    const replacementParent = path.join(root, 'replacement');
    await mkdir(activeParent);
    await mkdir(replacementParent);
    const projectFile = path.join(activeParent, 'line.oloproj');
    await writeFile(projectFile, '{}\n', 'utf8');
    const cachedProjectFile = resolveActiveProjectFile(projectFile);

    await writeFile(path.join(replacementParent, 'line.oloproj'), '{}\n', 'utf8');
    await rename(activeParent, originalParent);
    await symlink(
      replacementParent,
      activeParent,
      process.platform === 'win32' ? 'junction' : 'dir');

    assert.throws(
      () => resolveActiveProjectFile(cachedProjectFile),
      /ordinary non-symbolic directory/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('backend launch owns one exact startup Project workspace binding', () => {
  const projectFile = path.resolve('line.oloproj');
  const environment = bindActiveProjectStartupWorkspace({
    KEEP_ME: 'present',
    openlineops__projects__startupworkspaces__projectfiles__0: 'stale-0.oloproj',
    OpenLineOps__Projects__StartupWorkspaces__ProjectFiles__3: 'stale-3.oloproj'
  }, projectFile);

  assert.deepEqual(environment, {
    KEEP_ME: 'present',
    [startupProjectFileEnvironmentKey]: projectFile
  });
  assert.deepEqual(
    bindActiveProjectStartupWorkspace(environment, null),
    { KEEP_ME: 'present' });
});

test('workspace switch commits the backend Project binding only after guard approval', async () => {
  const [mainProcessSource, rendererMainSource, projectsWorkbenchSource] = await Promise.all([
    readFile(new URL('../src/main/main.ts', import.meta.url), 'utf8'),
    readFile(new URL('../src/renderer/main.tsx', import.meta.url), 'utf8'),
    readFile(new URL('../src/renderer/projects-workbench.tsx', import.meta.url), 'utf8')
  ]);
  const launchConfigStart = mainProcessSource.indexOf('function createBackendLaunchConfig');
  const launchConfigEnd = mainProcessSource.indexOf('function provisionLocalApiCredentials');
  assert.ok(launchConfigStart >= 0 && launchConfigEnd > launchConfigStart);
  assert.match(
    mainProcessSource.slice(launchConfigStart, launchConfigEnd),
    /const launchProjectFilePath = resolveActiveProjectFile\(projectFilePath\)/u,
    'Every backend launch must revalidate the cached Project binding.');
  assert.doesNotMatch(
    projectsWorkbenchSource,
    /desktop\.setActiveProjectFile/u,
    'Project open/create must not bind the backend before the unsaved-changes guard accepts selection.');

  const applyStart = rendererMainSource.indexOf('const applyWorkspaceSelection');
  const selectStart = rendererMainSource.indexOf('const selectWorkspace', applyStart);
  const closeStart = rendererMainSource.indexOf('const applyWorkspaceClose', selectStart);
  assert.ok(applyStart >= 0 && selectStart > applyStart && closeStart > selectStart);
  const applySource = rendererMainSource.slice(applyStart, selectStart);
  assert.ok(
    applySource.indexOf('await desktop.setActiveProjectFile(workspace.manifestPath)')
      < applySource.indexOf('setActiveWorkspace(workspace)'),
    'The main-process Project binding must succeed before renderer state changes.');
  const selectSource = rendererMainSource.slice(selectStart, closeStart);
  assert.match(selectSource, /runWithUnsavedGuard\([\s\S]*?applyWorkspaceSelection\(workspace\)/u);
  assert.match(selectSource, /\(\) => resolve\(false\)/u);
});
