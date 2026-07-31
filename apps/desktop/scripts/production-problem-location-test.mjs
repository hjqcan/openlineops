import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const compilerOptions = {
  module: ts.ModuleKind.ES2022,
  target: ts.ScriptTarget.ES2022
};
const layoutSource = await readFile(
  new URL('../src/renderer/production-route-layout.ts', import.meta.url),
  'utf8');
const layoutModuleUrl = compileModule(layoutSource, 'production-route-layout.ts');
const validationSource = await readFile(
  new URL('../src/renderer/production-route-validation.ts', import.meta.url),
  'utf8');
const validationModuleUrl = compileModule(
  validationSource,
  'production-route-validation.ts',
  [['./production-route-layout', layoutModuleUrl]]);
const locationSource = await readFile(
  new URL('../src/renderer/production-problem-location.ts', import.meta.url),
  'utf8');
const workbenchSource = await readFile(
  new URL('../src/renderer/production-workbench.tsx', import.meta.url),
  'utf8');
const locationModuleUrl = compileModule(
  locationSource,
  'production-problem-location.ts',
  [['./production-route-validation', validationModuleUrl]]);

const validation = await import(validationModuleUrl);
const location = await import(locationModuleUrl);

for (const fieldName of Object.keys(validation.productionProblemFields)) {
  assert(
    workbenchSource.includes(
      `data-production-problem-field={productionProblemFields.${fieldName}}`),
    `Production input is missing the ${fieldName} problem locator.`);
}
assert(workbenchSource.includes('targetId: serializeProductionProblemLocation(problem)'));
assert.equal(
  workbenchSource.includes('problems.find(candidate => candidate.entityId === targetId)'),
  false);

const invalidLine = {
  lineDefinitionId: ' invalid-line ',
  displayName: ' Invalid Line ',
  topologyId: ' invalid-topology ',
  productModel: {
    productModelId: ' invalid-product ',
    modelCode: ' INVALID-MODEL ',
    identityInputKey: ' serialNumber '
  },
  entryOperationId: 'operation.entry',
  operations: [],
  transitions: [],
  lineControllerAuthorizations: [],
  routeLayout: { operationPositions: [] }
};
const problems = validation.validateProductionLine(invalidLine, {
  topology: {
    topologyId: 'topology.application',
    systems: [],
    driverBindings: [],
    capabilities: [],
    slotGroups: [],
    slots: []
  },
  publishedFlows: [],
  configurationSnapshots: [],
  stationProfiles: []
});

assertProblemField(problems, 'Line ID must be', validation.productionProblemFields.lineDefinitionId);
assertProblemField(problems, 'Line display name must be', validation.productionProblemFields.lineDisplayName);
assertProblemField(problems, 'Topology reference must be', validation.productionProblemFields.lineTopologyId);
assertProblemField(
  problems,
  'does not match this Application topology',
  validation.productionProblemFields.lineTopologyId);
assertProblemField(problems, 'Product Model ID must be', validation.productionProblemFields.productModelId);
assertProblemField(problems, 'Product model code must be', validation.productionProblemFields.productModelCode);
assertProblemField(
  problems,
  'Product identity input key must be',
  validation.productionProblemFields.productIdentityInputKey);
assert.equal(
  problems.find(problem => problem.message.includes('requires at least one Operation'))?.fieldLocator,
  null);

const invalidEntryProblems = validation.validateProductionLine({
  lineDefinitionId: 'line.main',
  displayName: 'Main Line',
  topologyId: 'topology.main',
  productModel: {
    productModelId: 'product.main',
    modelCode: 'MAIN-A',
    identityInputKey: 'serialNumber'
  },
  entryOperationId: ' invalid-entry ',
  operations: [{
    operationId: 'operation.main',
    displayName: 'Main Operation',
    stationSystemId: 'station.main',
    flowDefinitionId: 'flow.main',
    configurationSnapshotId: 'configuration.main',
    resources: [{
      bindingId: 'resource.station.main',
      kind: 'Station',
      topologyTargetId: 'station.main',
      resolution: 'Fixed'
    }],
    inputMappings: []
  }],
  transitions: [{
    transitionId: 'route.main-completed',
    sourceOperationId: 'operation.main',
    targetOperationId: null,
    terminalDisposition: 'Completed',
    kind: 'Sequence',
    requiredJudgement: null,
    maxTraversals: null,
    parallelGroupId: null,
    outputKey: null,
    expectedOutputKind: null,
    expectedOutputValue: null
  }],
  lineControllerAuthorizations: [],
  routeLayout: {
    operationPositions: [{ operationId: 'operation.main', x: 100, y: 100 }]
  }
}, {
  topology: {
    topologyId: 'topology.main',
    systems: [{ systemId: 'station.main', parentSystemId: null, kind: 'Station' }],
    driverBindings: [],
    capabilities: [],
    slotGroups: [],
    slots: []
  },
  publishedFlows: [{
    processDefinitionId: 'flow.main',
    versionId: 'flow-version.main',
    status: 'Published'
  }],
  configurationSnapshots: [{
    snapshotId: 'configuration.main',
    stationProfileId: 'profile.main',
    processDefinitionId: 'flow.main',
    processVersionId: 'flow-version.main'
  }],
  stationProfiles: [{
    stationProfileId: 'profile.main',
    stationSystemId: 'station.main'
  }]
});
assertProblemField(
  invalidEntryProblems,
  'Entry Operation reference must be',
  validation.productionProblemFields.lineEntryOperationId);
assertProblemField(
  invalidEntryProblems,
  'does not exist',
  validation.productionProblemFields.lineEntryOperationId);

const lineProblem = problems.find(problem => (
  problem.fieldLocator === validation.productionProblemFields.lineDisplayName));
assert(lineProblem);
const serialized = location.serializeProductionProblemLocation(lineProblem);
assert.equal(
  serialized,
  JSON.stringify(['Line', ' invalid-line ', validation.productionProblemFields.lineDisplayName]));
assert.deepEqual(location.parseProductionProblemLocation(serialized), {
  scope: 'Line',
  entityId: ' invalid-line ',
  fieldLocator: validation.productionProblemFields.lineDisplayName
});
assert.equal(location.parseProductionProblemLocation('not-json'), null);
assert.equal(
  location.parseProductionProblemLocation(JSON.stringify([
    'Operation',
    'operation.entry',
    validation.productionProblemFields.lineDisplayName
  ])),
  null);

const calls = [];
const fieldTarget = {
  scrollIntoView: options => calls.push(['scrollIntoView', options]),
  focus: options => calls.push(['focus', options])
};
let selected = { kind: 'operation', id: 'old-selection' };
const root = {
  querySelector: selector => {
    calls.push(['querySelector', selector]);
    return fieldTarget;
  }
};
assert.equal(
  location.focusProductionProblem(lineProblem, next => { selected = next; }, root),
  true);
assert.equal(selected, null);
assert.deepEqual(calls, [
  [
    'querySelector',
    `[data-production-problem-field="${validation.productionProblemFields.lineDisplayName}"]`
  ],
  ['scrollIntoView', { block: 'center', inline: 'nearest' }],
  ['focus', { preventScroll: true }]
]);

let graphSelection = null;
let graphQueryCount = 0;
const graphRoot = {
  querySelector: () => {
    graphQueryCount += 1;
    throw new Error('Operation and Transition locations must never query a field fallback.');
  }
};
assert.equal(location.focusProductionProblem({
  scope: 'Operation',
  entityId: 'operation.entry',
  fieldLocator: null
}, next => { graphSelection = next; }, graphRoot), true);
assert.deepEqual(graphSelection, { kind: 'operation', id: 'operation.entry' });
assert.equal(location.focusProductionProblem({
  scope: 'Transition',
  entityId: 'route.entry-complete',
  fieldLocator: null
}, next => { graphSelection = next; }, graphRoot), true);
assert.deepEqual(graphSelection, { kind: 'transition', id: 'route.entry-complete' });
assert.equal(graphQueryCount, 0);

let missingSelection = { kind: 'operation', id: 'old-selection' };
assert.equal(location.focusProductionProblem({
  scope: 'Product',
  entityId: 'product.main',
  fieldLocator: validation.productionProblemFields.productModelCode
}, next => { missingSelection = next; }, { querySelector: () => null }), false);
assert.equal(missingSelection, null);

process.stdout.write('production problem location tests passed\n');

function compileModule(source, fileName, replacements = []) {
  let output = ts.transpileModule(source, {
    compilerOptions,
    fileName
  }).outputText;
  for (const [specifier, moduleUrl] of replacements) {
    const escapedSpecifier = specifier.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    output = output.replace(
      new RegExp(`from ['"]${escapedSpecifier}['"];`),
      `from '${moduleUrl}';`);
  }
  return `data:text/javascript;base64,${Buffer.from(output).toString('base64')}`;
}

function assertProblemField(problems, message, fieldLocator) {
  const problem = problems.find(candidate => candidate.message.includes(message));
  assert(problem, `Expected a validation problem containing '${message}'.`);
  assert.equal(problem.fieldLocator, fieldLocator, problem.message);
}
