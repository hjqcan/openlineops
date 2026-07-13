import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { Worker } from 'node:worker_threads';
import ts from 'typescript';

const sourceUrl = new URL('../src/renderer/production-route-validation.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'production-route-validation.ts'
}).outputText;
const moduleUrl = `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`;
const workerSource = `
  import { parentPort, workerData } from 'node:worker_threads';
  const validation = await import(workerData.moduleUrl);
  const operation = (id, station) => ({
    operationId: id,
    displayName: id,
    stationSystemId: station,
    flowDefinitionId: \`flow.\${id}\`,
    configurationSnapshotId: \`configuration.\${id}\`,
    resources: [{
      bindingId: \`resource.\${id}\`,
      kind: 'Station',
      topologyTargetId: station,
      resolution: 'Fixed'
    }, {
      bindingId: \`resource.device.\${id}\`,
      kind: 'Device',
      topologyTargetId: \`binding.\${id}\`,
      resolution: 'Fixed'
    }]
  });
  const transition = (id, source, target, kind, judgement, maxTraversals) => ({
    transitionId: id,
    sourceOperationId: source,
    targetOperationId: target,
    terminalDisposition: null,
    kind,
    requiredJudgement: judgement,
    maxTraversals,
    parallelGroupId: null,
    outputKey: null,
    expectedOutputKind: null,
    expectedOutputValue: null
  });
  const terminal = (id, source, disposition, kind = 'Sequence', judgement = null) => ({
    transitionId: id,
    sourceOperationId: source,
    targetOperationId: null,
    terminalDisposition: disposition,
    kind,
    requiredJudgement: judgement,
    maxTraversals: null,
    parallelGroupId: null,
    outputKey: null,
    expectedOutputKind: null,
    expectedOutputValue: null
  });
  const problems = validation.validateProductionLine({
    lineDefinitionId: 'line.main',
    displayName: 'Main Line',
    topologyId: 'topology.main',
    productModel: {
      productModelId: 'product.mainboard',
      modelCode: 'MAINBOARD-A',
      identityInputKey: 'serialNumber'
    },
    entryOperationId: 'operation.preparation',
    operations: [
      operation('operation.preparation', 'station.preparation'),
      operation('operation.vendor-test', 'station.vendor-test')
    ],
    transitions: [
      transition(
        'route.preparation-to-vendor-test',
        'operation.preparation',
        'operation.vendor-test',
        'Sequence',
        null,
        null),
      transition(
        'route.vendor-failed-rework',
        'operation.vendor-test',
        'operation.preparation',
        'Rework',
        'Failed',
        1),
      terminal(
        'route.vendor-failed-terminal',
        'operation.vendor-test',
        'Nonconforming',
        'Judgement',
        'Failed'),
      terminal(
        'route.vendor-default-terminal',
        'operation.vendor-test',
        'Completed')
    ],
    lineControllerAuthorizations: []
  }, {
    topology: {
      topologyId: 'topology.main',
      systems: [
        { systemId: 'station.preparation', parentSystemId: null, kind: 'Station' },
        { systemId: 'station.vendor-test', parentSystemId: null, kind: 'Station' }
      ],
      driverBindings: [
        {
          bindingId: 'binding.operation.preparation',
          ownerSystemId: 'station.preparation',
          capabilityId: 'capability.preparation',
          providerKind: 'ExternalSystem',
          providerKey: 'program.preparation'
        },
        {
          bindingId: 'binding.operation.vendor-test',
          ownerSystemId: 'station.vendor-test',
          capabilityId: 'capability.vendor-test',
          providerKind: 'ExternalSystem',
          providerKey: 'program.vendor-test'
        }
      ],
      capabilities: [],
      slotGroups: [],
      slots: []
    },
    publishedFlows: [],
    configurationSnapshots: [],
    stationProfiles: []
  });
  parentPort.postMessage(problems);
`;

const worker = new Worker(workerSource, {
  eval: true,
  type: 'module',
  workerData: { moduleUrl }
});
const result = await Promise.race([
  new Promise((resolve, reject) => {
    worker.once('message', resolve);
    worker.once('error', reject);
    worker.once('exit', code => {
      if (code !== 0) reject(new Error(`Route validation worker exited with code ${code}.`));
    });
  }),
  new Promise((_, reject) => setTimeout(
    () => reject(new Error('Bounded Rework route validation did not terminate.')),
    2_000))
]);
await worker.terminate();

assert(Array.isArray(result));
assert.equal(
  result.some(problem => problem.message.includes('forward route contains a cycle')),
  false);
assert.equal(
  result.some(problem => problem.message.includes('Rework must return to an earlier Operation')),
  false);
assert.equal(
  result.some(problem => problem.message.includes('does not resolve to an enabled target')),
  false);
process.stdout.write('production route validation tests passed\n');
