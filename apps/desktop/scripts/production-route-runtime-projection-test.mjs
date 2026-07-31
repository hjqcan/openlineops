import assert from 'node:assert/strict';
import path from 'node:path';
import process from 'node:process';
import { build } from 'esbuild';

const projectionModule = path.join(
  process.cwd(),
  'src',
  'renderer',
  'production-route-runtime-projection.ts');
const projectionModuleSpecifier = `./${path.relative(process.cwd(), projectionModule).replaceAll('\\', '/')}`;
const testSource = `
  import { buildProductionRouteRuntimeProjection } from ${JSON.stringify(projectionModuleSpecifier)};

  const operation = (operationRunId, operationId, attempt, stationSystemId, executionStatus, judgement) => ({
    operationRunId,
    operationId,
    attempt,
    stationSystemId,
    executionStatus,
    judgement,
    isTerminal: ['Completed', 'Failed', 'TimedOut', 'Canceled', 'Rejected'].includes(executionStatus),
    startedAtUtc: executionStatus === 'Pending' ? null : '2026-07-16T08:00:00.000Z',
    completedAtUtc: executionStatus === 'Completed' ? '2026-07-16T08:00:02.000Z' : null
  });
  const decision = (sourceOperationRunId, transitionId, targetOperationId, terminalDisposition, sourceJudgement, traversal, decidedAtUtc) => ({
    sourceOperationRunId,
    transitionId,
    targetOperationId,
    terminalDisposition,
    sourceJudgement,
    traversal,
    decidedAtUtc
  });
  const run = (productionRunId, disposition, isTerminal, operations, routeDecisions) => ({
    productionRunId,
    productionUnitId: 'unit-' + productionRunId,
    productionUnitIdentity: { value: 'SN-' + productionRunId },
    entryOperationId: operations[0].operationId,
    disposition,
    isTerminal,
    operations,
    routeDecisions
  });

  const passed = buildProductionRouteRuntimeProjection(run(
    'passed',
    'InProcess',
    false,
    [
      operation('prepare-1', 'operation.prepare', 1, 'station.prepare', 'Completed', 'Passed'),
      operation('test-1', 'operation.test', 1, 'station.test', 'Running', 'Unknown')
    ],
    [decision('prepare-1', 'transition.prepare-test', 'operation.test', null, 'Passed', 1, '2026-07-16T08:00:03.000Z')]
  ));

  const failed = buildProductionRouteRuntimeProjection(run(
    'failed',
    'Nonconforming',
    false,
    [
      operation('test-1', 'operation.test', 1, 'station.test', 'Completed', 'Failed'),
      operation('isolate-1', 'operation.isolate', 1, 'station.quality', 'Pending', 'Unknown')
    ],
    [decision('test-1', 'transition.failed-isolate', 'operation.isolate', null, 'Failed', 1, '2026-07-16T08:01:00.000Z')]
  ));

  const rework = buildProductionRouteRuntimeProjection(run(
    'rework',
    'InProcess',
    false,
    [
      operation('test-1', 'operation.test', 1, 'station.test', 'Completed', 'Failed'),
      operation('test-2', 'operation.test', 2, 'station.test', 'Running', 'Unknown')
    ],
    [decision('test-1', 'transition.test-rework', 'operation.test', null, 'Failed', 1, '2026-07-16T08:02:00.000Z')]
  ));

  const terminal = buildProductionRouteRuntimeProjection(run(
    'terminal',
    'Nonconforming',
    true,
    [operation('quality-1', 'operation.quality', 1, 'station.quality', 'Completed', 'Failed')],
    [decision('quality-1', 'transition.reject', null, 'Nonconforming', 'Failed', 1, '2026-07-16T08:03:00.000Z')]
  ));

  globalThis.__productionRouteRuntimeProjectionResult = { passed, failed, rework, terminal };
`;

const bundle = await build({
  stdin: {
    contents: testSource,
    loader: 'ts',
    resolveDir: process.cwd(),
    sourcefile: 'production-route-runtime-projection-test.ts'
  },
  bundle: true,
  format: 'esm',
  platform: 'node',
  target: 'node22',
  write: false,
  logLevel: 'silent'
});
const executable = bundle.outputFiles[0]?.text;
assert.ok(executable, 'Runtime route projection test bundle was not emitted.');
await import(`data:text/javascript;base64,${Buffer.from(executable).toString('base64')}`);

const { passed, failed, rework, terminal } = globalThis.__productionRouteRuntimeProjectionResult;

assert.equal(passed.decisionTrail.length, 1);
assert.equal(passed.latestDecision.transitionId, 'transition.prepare-test');
assert.equal(passed.latestDecision.source.operationId, 'operation.prepare');
assert.equal(passed.latestDecision.target.operationId, 'operation.test');
assert.equal(passed.latestDecision.sourceJudgement, 'Passed');
assert.equal(passed.currentMovements[0].tone, 'passed');

assert.equal(failed.latestDecision.transitionId, 'transition.failed-isolate');
assert.equal(failed.latestDecision.sourceJudgement, 'Failed');
assert.equal(failed.latestDecision.target.stationSystemId, 'station.quality');
assert.equal(failed.currentMovements[0].tone, 'failed');

assert.equal(rework.latestDecision.kind, 'Rework');
assert.equal(rework.latestDecision.target.operationRunId, 'test-2');
assert.equal(rework.latestDecision.target.attempt, 2);
assert.equal(rework.currentMovements[0].tone, 'rework');

assert.equal(terminal.latestDecision.kind, 'Terminal');
assert.equal(terminal.latestDecision.terminalDisposition, 'Nonconforming');
assert.equal(terminal.latestDecision.target, null);
assert.equal(terminal.currentMovements[0].movementId, terminal.latestDecision.movementId);

console.log('Production route runtime projection tests passed.');
