import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/external-program-directory-selection.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const contractSource = await readFile(
  new URL('../src/shared/external-program-directory-import-contract.ts', import.meta.url),
  'utf8');
const compiledContract = ts.transpileModule(contractSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'external-program-directory-import-contract.ts'
}).outputText;
const contractUrl = `data:text/javascript;base64,${Buffer.from(compiledContract).toString('base64')}`;
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'external-program-directory-selection.ts'
}).outputText.replace(
  "'../shared/external-program-directory-import-contract'",
  `'${contractUrl}'`);
const model = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
const loaderSource = await readFile(
  new URL('../src/renderer/external-program-resource-loader.ts', import.meta.url),
  'utf8');
const compiledLoader = ts.transpileModule(loaderSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'external-program-resource-loader.ts'
}).outputText;
const loader = await import(`data:text/javascript;base64,${Buffer.from(compiledLoader).toString('base64')}`);
const latestRequestLeaseSource = await readFile(
  new URL('../src/renderer/latest-request-lease.ts', import.meta.url),
  'utf8');
const compiledLatestRequestLease = ts.transpileModule(latestRequestLeaseSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'latest-request-lease.ts'
}).outputText;
const latestRequestLease = await import(
  `data:text/javascript;base64,${Buffer.from(compiledLatestRequestLease).toString('base64')}`);
const workbenchSource = await readFile(
  new URL('../src/renderer/external-program-workbench.tsx', import.meta.url),
  'utf8');
const rendererMainSource = await readFile(
  new URL('../src/renderer/main.tsx', import.meta.url),
  'utf8');
const editorWorkspaceSource = await readFile(
  new URL('../src/renderer/editor-workspace.tsx', import.meta.url),
  'utf8');
const toRequestSource = workbenchSource.slice(
  workbenchSource.indexOf('function toRequest('),
  workbenchSource.indexOf('function setCapability('));
const refreshSource = workbenchSource.slice(
  workbenchSource.indexOf('const refresh = useCallback('),
  workbenchSource.indexOf('const newResource = useCallback('));
const loadResourceSource = workbenchSource.slice(
  workbenchSource.indexOf('const loadResource = useCallback('),
  workbenchSource.indexOf('const refresh = useCallback('));
const newResourceSource = workbenchSource.slice(
  workbenchSource.indexOf('const newResource = useCallback('),
  workbenchSource.indexOf('const reloadResource = useCallback('));
const reloadResourceSource = workbenchSource.slice(
  workbenchSource.indexOf('const reloadResource = useCallback('),
  workbenchSource.indexOf('const save = useCallback('));
const saveSource = workbenchSource.slice(
  workbenchSource.indexOf('const save = useCallback('),
  workbenchSource.indexOf('latestSaveRef.current = save'));

test('validated inventory keeps canonical nested paths and identical basenames in different directories', () => {
  const selection = model.requireExternalProgramDirectorySelection(result([
    file('bin/vendor-helper.exe', 6),
    file('config/shared.settings.json', 7),
    file('lib/shared.settings.json', 8)
  ]));

  assert.deepEqual(selection.files.map(item => item.resourceRelativePath), [
    'files/bin/vendor-helper.exe',
    'files/config/shared.settings.json',
    'files/lib/shared.settings.json'
  ]);
  assert.equal(selection.totalBytes, 21);
});

test('entry point is explicit, retained only when still present, and restricted to a Windows executable', () => {
  const selection = model.requireExternalProgramDirectorySelection(result([
    file('bin/vendor-helper.exe', 6),
    file('config/settings.json', 7),
    file('scripts/run.ps1', 8)
  ]));

  assert.equal(model.chooseExternalProgramEntryPoint(selection, null), null);
  assert.equal(
    model.chooseExternalProgramEntryPoint(selection, 'files/bin/vendor-helper.exe'),
    'files/bin/vendor-helper.exe');
  assert.equal(model.chooseExternalProgramEntryPoint(selection, 'files/scripts/run.ps1'), null);
  assert.equal(model.chooseExternalProgramEntryPoint(selection, 'files/bin/missing.exe'), null);
  assert.equal(model.isSupportedExternalProgramEntryPoint('files/bin/vendor-helper.EXE'), true);
  assert.equal(model.isSupportedExternalProgramEntryPoint('files/bin/vendor-helper.dll'), false);
});

test('renderer rejects forged totals, limits, path aliases, and case collisions', () => {
  assert.throws(
    () => model.requireExternalProgramDirectorySelection(result([
      file('lib/Shared.dll', 1),
      file('lib/shared.dll', 1)
    ])),
    /conflicts by case/u);
  assert.throws(
    () => model.requireExternalProgramDirectorySelection({ ...result([file('bin/helper.exe', 1)]), totalBytes: 2 }),
    /total size/u);
  assert.throws(
    () => model.requireExternalProgramDirectorySelection(result([file('CON.txt', 1)])),
    /invalid inventory metadata/u);
  assert.throws(
    () => model.requireExternalProgramDirectorySelection(result([file('lib/e\u0301.dll', 1)])),
    /invalid inventory metadata/u);
  assert.throws(
    () => model.requireExternalProgramDirectorySelection(result([file('lib/invalid?.dll', 1)])),
    /invalid inventory metadata/u);
  assert.throws(
    () => model.requireExternalProgramDirectorySelection(result([file('oversized.exe', 512 * 1024 * 1024 + 1)])),
    /invalid inventory metadata/u);
});

test('workbench exposes atomic directory replacement, explicit executable selection, limits, and scope-race checks', () => {
  assert.match(workbenchSource, /Select Program Directory/u);
  assert.match(workbenchSource, /Save atomically replaces the complete frozen file set/u);
  assert.match(workbenchSource, /externalProgramDirectoryImportLimits\.maximumFileCount/u);
  assert.match(workbenchSource, /externalProgramDirectoryImportLimits\.maximumTotalBytes/u);
  assert.match(workbenchSource, /<option value="">Choose entry point<\/option>/u);
  assert.match(workbenchSource, /currentScopeKey\.current !== startedScopeKey/u);
  assert.match(workbenchSource, /currentEditorIdentity\.current !== startedEditorIdentity/u);
  assert.match(workbenchSource, /releaseExternalProgramDirectorySelection\(selection\.selectionId\)[\s\S]*?active Application or Program Resource changed/u);
  assert.match(workbenchSource, /releaseExternalProgramDirectorySelection\(selectionId\)/u);
  assert.match(workbenchSource, /releaseExternalProgramDirectorySelection\(selectionId\)\.catch\(\(\) => undefined\)/u);
  assert.match(workbenchSource, /isCurrentEditorOperation\(startedScopeKey, startedEditorIdentity\)/u);
  assert.match(workbenchSource, /const updateDraft = useCallback[\s\S]*?return \{ \.\.\.next, dirty: true \}/u);
  assert.match(workbenchSource, /<MappingRows[^>]+onChange=\{updateDraft\}/u);
  assert.match(workbenchSource, /setTrialKindsByTarget[\s\S]*?setTrialValues/u);
  assert.doesNotMatch(workbenchSource, /onInputCapture/u);
  assert.match(workbenchSource, /const startedEditorGeneration = editorGeneration\.current[\s\S]*?editorGeneration\.current !== startedEditorGeneration/u);
  assert.match(workbenchSource, /const requestedRefreshEpoch = \+\+refreshRequestEpoch\.current[\s\S]*?runLatestExternalProgramRequest[\s\S]*?refreshRequestEpoch\.current === requestedRefreshEpoch/u);
  assert.match(workbenchSource, /overwrite: async \(\) => \{[\s\S]*?latestSaveRef\.current\(true\)/u);
  assert.match(workbenchSource, /latestSaveRef\.current = save/u);
  assert.doesNotMatch(workbenchSource, /overwrite: async \(\) => \{[\s\S]*?await save\(true\)/u);
  assert.match(workbenchSource, /if \(draft\.dirty \|\| pendingDirectory\)[\s\S]*?Save the Program Resource before running a trial/u);
  assert.match(workbenchSource, /disabled=\{busy \|\| !draft\.persisted \|\| draft\.dirty \|\| pendingDirectory !== null\}/u);
  assert.equal((workbenchSource.match(/disabled=\{busy\} onChange=\{event => \{\s*setTrialResult\(null\)/gu) ?? []).length, 2);
  assert.match(workbenchSource, /draft\.dirty \? 'Pending changes — save to update hash'/u);
  assert.match(toRequestSource, /resourceId: draft\.resourceId[\s\S]*?executionLimits: \{ \.\.\.draft\.executionLimits \}/u);
  assert.doesNotMatch(toRequestSource, /updatedAtUtc|\.\.\.request/u);
  assert.match(refreshSource, /editorGeneration\.current !== startedEditorGeneration[\s\S]*?return;[\s\S]*?setConflict\(null\)[\s\S]*?setDraft/u);
  assert.match(newResourceSource, /setPendingDirectory\(null\)[\s\S]*?setConflict\(null\)[\s\S]*?setDraft\(createDraft/u);
  assert.match(rendererMainSource, /<ExternalProgramWorkbench[\s\S]*?key=\{`\$\{activeWorkspace\?\.project\.projectId[\s\S]*?activeApplication\?\.applicationId/u);
  assert.doesNotMatch(workbenchSource, /Import support file|selectExternalProgramFiles|portableFileName/u);
});

test('workbench locks the complete editor and rejects reentrant saves before a directory token can be reused', () => {
  assert.match(workbenchSource, /const operationInFlight = useRef\(false\)/u);
  assert.match(workbenchSource, /if \(operationInFlight\.current \|\| resourceLoadLease\.current\.busy\)[\s\S]*?operationInFlight\.current = true[\s\S]*?setBusy\(true\)/u);
  assert.match(workbenchSource, /operationInFlight\.current = false[\s\S]*?setBusy\(false\)/u);
  assert.match(saveSource, /if \(!beginOperation\('Save'\)\)[\s\S]*?importExternalProgramDirectory/u);
  assert.match(workbenchSource, /canSave: isBackendHealthy && !busy && !resourceLoading && editorProblems\.length === 0/u);
  assert.match(workbenchSource, /<fieldset className="external-program-editor" disabled=\{busy \|\| resourceLoading\}[\s\S]*?<\/fieldset>/u);
  assert.match(workbenchSource, /disabled=\{busy \|\| resourceLoading \|\| draftTransitionGuard\.busy\}[\s\S]*?data-testid="new-external-program-resource"/u);
  assert.match(workbenchSource, /data-testid=\{`external-program-resource-\$\{resource\.resourceId\}`\}/u);
  assert.match(workbenchSource, /disabled=\{busy \|\| draftTransitionGuard\.busy\}[\s\S]*?data-testid=\{`external-program-resource-/u);
  assert.match(workbenchSource, /disabled=\{draft\.persisted \|\| pendingDirectory !== null \|\| busy\}[\s\S]*?data-testid="external-program-resource-id"/u);
});

test('conflict reload and overwrite stay disabled and guarded while either action is running', () => {
  assert.match(reloadResourceSource, /if \(!beginOperation\('Reload'\)\)[\s\S]*?try \{[\s\S]*?loadExternalProgramResourceCore[\s\S]*?finally \{\s*endOperation\(\)/u);
  assert.doesNotMatch(reloadResourceSource, /await loadResource\(/u);
  assert.match(workbenchSource, /useEditorDocument\(\{\s*dirty: draft\.dirty,\s*editRevision: draft,\s*busy: busy \|\| resourceLoading,/u);
  assert.match(editorWorkspaceSource, /const actionsDisabled = document\.saving \|\| document\.busy/u);
  assert.equal((editorWorkspaceSource.match(/disabled=\{actionsDisabled\}/gu) ?? []).length, 2);
  assert.equal((editorWorkspaceSource.match(/\.catch\(\(\) => undefined\)/gu) ?? []).length >= 2, true);
});

test('resource loading is last-click-wins under reversed latency and blocks editor teardown while active', async () => {
  assert.match(loadResourceSource, /const requestedResourceLoadEpoch = resourceLoadLease\.current\.start\(\)/u);
  assert.match(loadResourceSource, /loadExternalProgramResourceCore[\s\S]*?!resourceLoadLease\.current\.isCurrent\(requestedResourceLoadEpoch\)[\s\S]*?return false;[\s\S]*?selectResource\(loaded\.resource\)/u);
  assert.match(loadResourceSource, /setResourceLoading\(true\)[\s\S]*?finally[\s\S]*?resourceLoadLease\.current\.finish\(requestedResourceLoadEpoch\)[\s\S]*?setResourceLoading\(false\)/u);
  assert.match(rendererMainSource, /const inFlightDocuments = documentRegistry\.entries\(\)[\s\S]*?document\.busy \|\| document\.saving[\s\S]*?cancel\?\.\(\);\s*return;/u);
  assert.match(workbenchSource, /draft\.persisted && draft\.resourceId === resource\.resourceId[\s\S]*?resourceLoadLease\.current\.cancel\(\)[\s\S]*?setResourceLoading\(false\)[\s\S]*?return;/u);

  let epoch = 0;
  let selected = null;
  const first = deferred();
  const second = deferred();
  const load = async (resourceId, completion) => {
    const requestedEpoch = ++epoch;
    const loaded = await loader.loadExternalProgramResourceCore(
      resourceId,
      async () => {
        await completion;
        return [{ resourceId: 'resource-a' }, { resourceId: 'resource-b' }];
      },
      () => epoch === requestedEpoch);
    if (loaded) selected = loaded.resource.resourceId;
  };
  const firstLoad = load('resource-a', first.promise);
  const secondLoad = load('resource-b', second.promise);
  second.resolve();
  await secondLoad;
  first.resolve();
  await firstLoad;
  assert.equal(selected, 'resource-b');

  selected = 'resource-a';
  const slowSecond = deferred();
  const slowSecondLoad = load('resource-b', slowSecond.promise);
  epoch++;
  slowSecond.resolve();
  await slowSecondLoad;
  assert.equal(selected, 'resource-a');
});

test('only the latest resource request owns the busy lease', async () => {
  const lease = new latestRequestLease.LatestRequestLease();
  const staleCompletion = deferred();
  const latestCompletion = deferred();
  let resourceLoading = false;

  const load = async completion => {
    const requestEpoch = lease.start();
    resourceLoading = true;
    try {
      await completion;
    } finally {
      if (lease.finish(requestEpoch)) {
        resourceLoading = false;
      }
    }
  };

  const staleLoad = load(staleCompletion.promise);
  const latestLoad = load(latestCompletion.promise);
  latestCompletion.resolve();
  await latestLoad;
  assert.equal(resourceLoading, false);
  assert.equal(lease.busy, false);

  staleCompletion.resolve();
  await staleLoad;
  assert.equal(resourceLoading, false);
  assert.equal(lease.busy, false);

  const canceledCompletion = deferred();
  const canceledLoad = load(canceledCompletion.promise);
  assert.equal(resourceLoading, true);
  assert.equal(lease.cancel(), true);
  resourceLoading = false;
  assert.equal(resourceLoading, false);
  assert.equal(lease.busy, false);
  canceledCompletion.resolve();
  await canceledLoad;
  assert.equal(resourceLoading, false);
  assert.equal(lease.busy, false);
});

test('persisted resource reload succeeds while its caller owns the operation lease', async () => {
  const operationInFlight = true;
  const loaded = await loader.loadExternalProgramResourceCore(
    'resource-persisted',
    async () => [{ resourceId: 'resource-persisted', revision: 'revision-2' }],
    () => {
      assert.equal(operationInFlight, true);
      return true;
    });
  assert.equal(loaded.resource.resourceId, 'resource-persisted');
  assert.equal(loaded.resource.revision, 'revision-2');
});

test('a superseded resource load ignores its late failure after the latest load succeeds', async () => {
  let epoch = 1;
  let selected = 'resource-a';
  const staleCompletion = deferred();
  const staleLoad = loader.loadExternalProgramResourceCore(
    'resource-b',
    async () => {
      await staleCompletion.promise;
      throw new Error('stale request failed');
    },
    () => epoch === 1);

  epoch = 2;
  const latest = await loader.loadExternalProgramResourceCore(
    'resource-c',
    async () => [{ resourceId: 'resource-c' }],
    () => epoch === 2);
  selected = latest.resource.resourceId;
  staleCompletion.resolve();
  assert.equal(await staleLoad, null);
  assert.equal(selected, 'resource-c');

  await assert.rejects(
    loader.loadExternalProgramResourceCore(
      'resource-c',
      async () => { throw new Error('latest request failed'); },
      () => true),
    /latest request failed/u);
});

test('a superseded refresh ignores its late failure after the latest refresh succeeds', async () => {
  let refreshEpoch = 1;
  const staleCompletion = deferred();
  const staleRefresh = loader.runLatestExternalProgramRequest(
    async () => {
      await staleCompletion.promise;
      throw new Error('stale refresh failed');
    },
    () => refreshEpoch === 1);

  refreshEpoch = 2;
  const latestRefresh = await loader.runLatestExternalProgramRequest(
    async () => ({ resourceIds: ['resource-c'] }),
    () => refreshEpoch === 2);
  staleCompletion.resolve();

  assert.deepEqual(latestRefresh, { resourceIds: ['resource-c'] });
  assert.equal(await staleRefresh, null);
});

function file(relativePath, sizeBytes) {
  return {
    relativePath,
    resourceRelativePath: `files/${relativePath}`,
    sizeBytes,
    sha256: 'a'.repeat(64)
  };
}

function result(files) {
  return {
    canceled: false,
    selectionId: 'b'.repeat(64),
    directoryName: 'vendor-program',
    totalBytes: files.reduce((total, item) => total + item.sizeBytes, 0),
    files
  };
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
