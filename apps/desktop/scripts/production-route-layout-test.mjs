import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/production-route-layout.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'production-route-layout.ts'
}).outputText;
const moduleUrl = `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`;
const layout = await import(moduleUrl);

const operations = [
  { operationId: 'operation.load' },
  { operationId: 'operation.inspect' }
];
const created = layout.createProductionRouteLayout(operations, {
  'operation.load': { x: 120.4, y: 79.7 },
  'operation.inspect': { x: 100_004, y: -4 }
});
assert.deepEqual(created, {
  operationPositions: [
    { operationId: 'operation.load', x: 120, y: 80 },
    { operationId: 'operation.inspect', x: 100_000, y: 0 }
  ]
});
assert.deepEqual(layout.routeLayoutToGraphPoints(created), {
  'operation.load': { x: 120, y: 80 },
  'operation.inspect': { x: 100_000, y: 0 }
});

const moved = layout.moveOperationInRouteLayout(
  created,
  'operation.load',
  { x: 456.6, y: 200.2 });
assert.deepEqual(moved.operationPositions[0], {
  operationId: 'operation.load',
  x: 457,
  y: 200
});
assert.notEqual(moved, created);
assert.deepEqual(created.operationPositions[0], {
  operationId: 'operation.load',
  x: 120,
  y: 80
});

const renamed = layout.renameOperationInRouteLayout(
  moved,
  'operation.inspect',
  'operation.test');
assert.equal(renamed.operationPositions[1].operationId, 'operation.test');
const removed = layout.removeOperationFromRouteLayout(renamed, 'operation.load');
assert.deepEqual(removed.operationPositions, [
  { operationId: 'operation.test', x: 100_000, y: 0 }
]);

assert.throws(
  () => layout.createProductionRouteLayout(operations, {
    'operation.load': { x: 1, y: 2 }
  }),
  /missing Operation operation\.inspect/);
assert.throws(
  () => layout.moveOperationInRouteLayout(created, 'operation.missing', { x: 1, y: 2 }),
  /does not contain Operation operation\.missing/);
assert.throws(
  () => layout.moveOperationInRouteLayout(created, 'operation.load', { x: Number.NaN, y: 2 }),
  /finite numbers/);

process.stdout.write('production route layout tests passed\n');
