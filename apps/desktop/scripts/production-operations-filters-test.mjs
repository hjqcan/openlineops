import assert from 'node:assert/strict';
import path from 'node:path';
import process from 'node:process';
import { build } from 'esbuild';

const filterModule = path.join(
  process.cwd(),
  'src',
  'renderer',
  'production-operations-filters.ts');
const filterModuleSpecifier = `./${path.relative(process.cwd(), filterModule).replaceAll('\\', '/')}`;
const testSource = `
  import {
    buildActiveProductionRunsQuery,
    buildProductionOperationsSlotOptions,
    buildSlotResourceId,
    isCanonicalSlotResourceId,
    slotResourceIdMatchesScope,
    stationMatchesProductionOperationsFilters
  } from ${JSON.stringify(filterModuleSpecifier)};

  const lineState = {
    productionLineDefinitionId: 'line.main',
    slots: [
      { stationSystemId: 'station.a', slotId: 'slot.shared' },
      { stationSystemId: 'station.b', slotId: 'slot.shared' },
      { stationSystemId: 'station.b', slotId: 'slot.unique' }
    ]
  };
  const station = (stationSystemId, slotIds) => ({
    stationSystemId,
    slots: slotIds.map(slotId => ({ slotId }))
  });

  globalThis.__productionOperationsFiltersResult = {
    address: buildSlotResourceId('line.main', 'station.a', 'slot.shared'),
    allOptions: buildProductionOperationsSlotOptions(lineState, ''),
    stationOptions: buildProductionOperationsSlotOptions(lineState, 'station.b'),
    stationAMatches: stationMatchesProductionOperationsFilters(
      'line.main',
      station('station.a', ['slot.shared']),
      {
        productionLineDefinitionId: '',
        stationSystemId: '',
        slotResourceId: 'line.main/station.a/slot.shared'
      }),
    stationBMatches: stationMatchesProductionOperationsFilters(
      'line.main',
      station('station.b', ['slot.shared']),
      {
        productionLineDefinitionId: '',
        stationSystemId: '',
        slotResourceId: 'line.main/station.a/slot.shared'
      }),
    query: buildActiveProductionRunsQuery({
      productionLineDefinitionId: 'line.main',
      stationSystemId: 'station.a',
      slotResourceId: 'line.main/station.a/slot.shared'
    }),
    canonical: isCanonicalSlotResourceId('line.main/station.a/slot.shared'),
    malformed: isCanonicalSlotResourceId('slot.shared'),
    scoped: slotResourceIdMatchesScope(
      'line.main/station.a/slot.shared',
      'line.main',
      'station.a'),
    mismatchedScope: slotResourceIdMatchesScope(
      'line.main/station.a/slot.shared',
      'line.main',
      'station.b')
  };

  let invalidSegmentRejected = false;
  try {
    buildSlotResourceId('line/main', 'station.a', 'slot.shared');
  } catch {
    invalidSegmentRejected = true;
  }
  globalThis.__productionOperationsFiltersResult.invalidSegmentRejected = invalidSegmentRejected;
`;

const bundle = await build({
  stdin: {
    contents: testSource,
    loader: 'ts',
    resolveDir: process.cwd(),
    sourcefile: 'production-operations-filters-test.ts'
  },
  bundle: true,
  format: 'esm',
  platform: 'node',
  target: 'node22',
  write: false,
  logLevel: 'silent'
});
const executable = bundle.outputFiles[0]?.text;
assert.ok(executable, 'Production Operations filter test bundle was not emitted.');
await import(`data:text/javascript;base64,${Buffer.from(executable).toString('base64')}`);

const result = globalThis.__productionOperationsFiltersResult;
assert.equal(result.address, 'line.main/station.a/slot.shared');
assert.equal(result.allOptions.length, 3);
assert.deepEqual(
  result.allOptions.map(option => option.resourceId),
  [
    'line.main/station.a/slot.shared',
    'line.main/station.b/slot.shared',
    'line.main/station.b/slot.unique'
  ]);
assert.deepEqual(
  result.allOptions.map(option => option.label),
  [
    'station.a / slot.shared',
    'station.b / slot.shared',
    'station.b / slot.unique'
  ]);
assert.deepEqual(
  result.stationOptions.map(option => [option.resourceId, option.label]),
  [
    ['line.main/station.b/slot.shared', 'slot.shared'],
    ['line.main/station.b/slot.unique', 'slot.unique']
  ]);
assert.equal(result.stationAMatches, true);
assert.equal(result.stationBMatches, false);
assert.equal(result.canonical, true);
assert.equal(result.malformed, false);
assert.equal(result.scoped, true);
assert.equal(result.mismatchedScope, false);
assert.equal(result.invalidSegmentRejected, true);

const query = new URLSearchParams(result.query);
assert.equal(query.get('productionLineDefinitionId'), 'line.main');
assert.equal(query.get('stationSystemId'), 'station.a');
assert.equal(query.get('slotResourceId'), 'line.main/station.a/slot.shared');
assert.equal(query.has('slotId'), false);
assert.equal(result.query.includes('%2F'), false);
assert.match(result.query, /slotResourceId=line\.main\/station\.a\/slot\.shared/u);

process.stdout.write('production operations filter tests passed\n');
