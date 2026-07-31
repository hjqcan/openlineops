import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import ts from 'typescript';
import { fileURLToPath } from 'node:url';

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const modelPath = path.join(
  desktopRoot,
  'src',
  'renderer',
  'runtime-monitoring-refresh-model.ts');
const modelSource = fs.readFileSync(modelPath, 'utf8');
const modelJavaScript = ts.transpileModule(modelSource, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: modelPath
}).outputText;
const model = await import(
  `data:text/javascript;base64,${Buffer.from(modelJavaScript).toString('base64')}`);
const apiSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'api.ts'),
  'utf8');
const mainSource = fs.readFileSync(
  path.join(desktopRoot, 'src', 'renderer', 'main.tsx'),
  'utf8');

test('runtime response envelopes reject HTTP failures and missing bodies', () => {
  const body = { items: [{ id: 'station-a' }] };
  assert.equal(model.requireApiResponseBody(response({ body }), 'Load projection'), body);
  assert.equal(model.requireApiItemsResponse(response({ body }), 'Load projection'), body.items);
  assert.throws(
    () => model.requireApiResponseBody(
      response({ ok: false, status: 503, text: 'unavailable', body }),
      'Load projection'),
    /503 unavailable/);
  assert.throws(
    () => model.requireApiResponseBody(response({ body: null }), 'Load projection'),
    /failed/);
  assert.throws(
    () => model.requireApiResponseBody(response({ body: undefined }), 'Load projection'),
    /failed/);
  assert.throws(
    () => model.requireApiItemsResponse(
      response({ body: { items: null } }),
      'Load projection'),
    /items array/);
});

test('a failed projection refresh cannot replace the last known projection', async () => {
  const previous = Object.freeze({
    stations: Object.freeze([{ id: 'station-old' }]),
    targets: Object.freeze([{ id: 'target-old' }]),
    alarms: Object.freeze([{ id: 'alarm-old' }]),
    traces: Object.freeze([{ id: 'trace-old' }]),
    timeline: Object.freeze([{ id: 'timeline-old' }])
  });
  let visibleProjection = previous;
  let timelineCalled = false;

  await assert.rejects(async () => {
    visibleProjection = await model.loadRuntimeMonitoringProjection({
      loadStations: async () => [{ id: 'station-new' }],
      loadTargets: async () => { throw new Error('target endpoint unavailable'); },
      loadAlarms: async () => [{ id: 'alarm-new' }],
      loadTraces: async () => [{ id: 'trace-new' }],
      loadTimeline: async () => {
        timelineCalled = true;
        return [{ id: 'timeline-new' }];
      }
    });
  }, /target endpoint unavailable/);

  assert.equal(timelineCalled, true);
  assert.equal(visibleProjection, previous);
  assert.deepEqual(visibleProjection.stations, [{ id: 'station-old' }]);
});

test('a complete projection refresh commits all five views together', async () => {
  const projection = await model.loadRuntimeMonitoringProjection({
    loadStations: async () => [{ id: 'station-new' }],
    loadTargets: async stations => [{ id: `target-for-${stations[0].id}` }],
    loadAlarms: async () => [{ id: 'alarm-new' }],
    loadTraces: async () => [{ id: 'trace-new' }],
    loadTimeline: async stations => [{ id: `timeline-for-${stations[0].id}` }]
  });

  assert.deepEqual(projection, {
    stations: [{ id: 'station-new' }],
    targets: [{ id: 'target-for-station-new' }],
    alarms: [{ id: 'alarm-new' }],
    traces: [{ id: 'trace-new' }],
    timeline: [{ id: 'timeline-for-station-new' }]
  });
});

test('Studio wires every runtime read through strict envelopes and atomic commit', () => {
  assert.doesNotMatch(apiSource, /response\.body\?\.items \?\? \[\]/u);
  assert.match(apiSource, /requireApiItemsResponse\(response, 'Load runtime Station projection'\)/u);
  assert.match(apiSource, /requireApiItemsResponse\(response, 'Load runtime Alarm projection'\)/u);
  assert.match(apiSource, /requireApiItemsResponse\(response, 'Load runtime timeline projection'\)/u);
  assert.match(apiSource, /requireApiResponseBody\(response, 'Load Trace projection'\)/u);
  assert.match(mainSource, /const projection = await readRuntimeMonitoringProjection/u);
  assert.match(mainSource, /monitoring refresh failed:/u);

  const refreshStart = mainSource.indexOf('const refresh = useCallback');
  const refreshEnd = mainSource.indexOf('const refreshRef = useRef', refreshStart);
  const refreshSource = mainSource.slice(refreshStart, refreshEnd);
  const projectionLoad = refreshSource.indexOf('await readRuntimeMonitoringProjection');
  assert.ok(projectionLoad >= 0);
  for (const setter of [
    'setStations(projection.stations)',
    'setTargetStatuses(projection.targets)',
    'setAlarms(projection.alarms)',
    'setTraceRows(projection.traces)',
    'setTimeline(projection.timeline)'
  ]) {
    assert.ok(refreshSource.indexOf(setter) > projectionLoad, `${setter} must follow the complete load.`);
  }
  assert.doesNotMatch(
    refreshSource,
    /else\s*\{\s*setStations\(\[\]\)/u,
    'A backend outage must retain the last known projection rather than display an empty line.');
});

function response(overrides = {}) {
  return {
    ok: true,
    status: 200,
    text: '',
    body: { items: [] },
    ...overrides
  };
}
