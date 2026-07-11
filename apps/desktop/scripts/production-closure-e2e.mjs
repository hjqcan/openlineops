import { execFile, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  delay,
  ElectronCdpHarness,
  getFreePort
} from './electron-cdp-harness.mjs';

const execFileAsync = promisify(execFile);
const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');
const packagedExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'OpenLineOps.exe');
const helperOutputDirectory = path.join(
  repoRoot,
  'tools',
  'OpenLineOps.VendorTestHelper',
  'bin',
  'Release',
  'net10.0');
const helperFileNames = [
  'OpenLineOps.VendorTestHelper.exe',
  'OpenLineOps.VendorTestHelper.dll',
  'OpenLineOps.VendorTestHelper.deps.json',
  'OpenLineOps.VendorTestHelper.runtimeconfig.json'
];
const actorId = 'packaged-e2e-operator';
const runSuffix = `${new Date().toISOString().replaceAll(/[-:.TZ]/gu, '')}-${process.pid}`;
const artifactRoot = path.join(repoRoot, 'artifacts', 'production-closure-e2e', runSuffix);
const screenshotRoot = path.join(artifactRoot, 'screenshots');
const summaryPath = path.join(artifactRoot, 'summary.json');
const logs = [];
const summary = {
  schema: 'openlineops.production-closure-e2e',
  status: 'running',
  startedAtUtc: new Date().toISOString(),
  completedAtUtc: null,
  packagedExecutable,
  artifactRoot,
  projectPath: null,
  projectId: null,
  applicationId: null,
  topologyId: null,
  productionLineDefinitionId: null,
  projectSnapshotId: null,
  frozenRelease: null,
  scenarios: {},
  restart: null,
  diagnostics: null,
  failure: null,
  logs: []
};

let harness;
let userDataDirectory;
let projectPath;
let fixture;
let logicalTimestamp = Date.now();

async function main() {
  if (typeof WebSocket === 'undefined') {
    throw new Error('Node.js 22 or newer is required for the Electron CDP harness.');
  }
  await assertFile(packagedExecutable, 'Packaged OpenLineOps executable');
  await fs.mkdir(screenshotRoot, { recursive: true });
  await buildVendorHelper();

  const apiPort = await getFreePort();
  userDataDirectory = path.join(artifactRoot, 'user-data');
  projectPath = path.join(artifactRoot, 'project');
  await fs.mkdir(userDataDirectory, { recursive: true });
  harness = createHarness(apiPort);
  await harness.start();
  await ensureBackendHealthy();
  await createProjectFromStartCenter(projectPath);
  summary.projectPath = projectPath;
  await persistSummary();
  fixture = await createProductionFixture(projectPath);
  Object.assign(summary, {
    projectPath,
    projectId: fixture.projectId,
    applicationId: fixture.applicationId,
    topologyId: fixture.topologyId,
    productionLineDefinitionId: fixture.lineId,
    projectSnapshotId: fixture.snapshotId,
    frozenRelease: fixture.frozenRelease
  });
  await persistSummary();

  await reopenProjectFromStartCenter(projectPath, fixture.projectId);
  await registerLineSlots();
  await runConcurrentAndPassedScenario();
  await runFailedReworkScenario();
  await runCancellationScenario();
  await runCrashScenario();
  await runRecoveryScenario();
  await restartStudioAndVerifyProjection();

  summary.status = 'passed';
  summary.completedAtUtc = new Date().toISOString();
  summary.logs = logs.slice(-200);
  await persistSummary();
  console.log(`OpenLineOps packaged production closure E2E passed: ${summaryPath}`);
}

function createHarness(apiPort) {
  return new ElectronCdpHarness({
    executablePath: packagedExecutable,
    workingDirectory: path.dirname(packagedExecutable),
    userDataDirectory,
    apiBaseUrl: `http://127.0.0.1:${apiPort}`,
    environment: {
      OPENLINEOPS_REPO_ROOT: repoRoot,
      OPENLINEOPS_DESKTOP_LOG_PATH: path.join(artifactRoot, 'desktop-logs')
    },
    logs
  });
}

async function buildVendorHelper() {
  await runCommand(
    'dotnet',
    [
      'build',
      path.join(repoRoot, 'tools', 'OpenLineOps.VendorTestHelper', 'OpenLineOps.VendorTestHelper.csproj'),
      '--configuration',
      'Release',
      '--nologo',
      '--property:TreatWarningsAsErrors=true'
    ],
    repoRoot);
  for (const fileName of helperFileNames) {
    await assertFile(path.join(helperOutputDirectory, fileName), `Vendor helper ${fileName}`);
  }
}

async function ensureBackendHealthy() {
  const status = await harness.evaluate('window.openlineopsDesktop.getBackendStatus()');
  if (!status.isRunning || status.health !== 'Healthy') {
    await harness.evaluate('window.openlineopsDesktop.startBackend()');
  }
  await harness.waitFor(
    '(async () => (await window.openlineopsDesktop.getBackendStatus()).health === "Healthy")()',
    60_000,
    'the packaged API backend');
}

async function createProjectFromStartCenter(targetPath) {
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"start-create-project\\"]"))',
    30_000,
    'the Create Project entry point');
  await harness.click('start-create-project');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"project-path-input\\"]"))',
    15_000,
    'the project path dialog');
  await harness.setInput('project-path-input', targetPath);
  await harness.waitFor(
    'document.querySelector("[data-testid=\\"create-project-workspace\\"]")?.disabled === false',
    10_000,
    'the Create Project confirmation to become enabled');
  await harness.click('create-project-workspace');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"active-application-selector\\"]"))'
      + ' && document.body.innerText.includes("Project Explorer")',
    45_000,
    'the newly created project workspace');
  await recordScreenshot('project-created');
}

async function reopenProjectFromStartCenter(targetPath, expectedProjectId) {
  const switchButton = await harness.evaluate(
    'Boolean(document.querySelector("[data-testid=\\"switch-project-workspace\\"]"))');
  if (switchButton) await harness.click('switch-project-workspace');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"start-open-project-by-path\\"]"))',
    20_000,
    'the Open Project entry point');
  await harness.click('start-open-project-by-path');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"open-project-path-input\\"]"))',
    15_000,
    'the Open Project path dialog');
  await harness.setInput('open-project-path-input', targetPath);
  await harness.click('open-project-workspace');
  await harness.waitFor(
    `document.body.innerText.includes(${JSON.stringify(`Project opened ${expectedProjectId}`)})`
      + ' && Boolean(document.querySelector("[data-testid=\\"active-application-selector\\"]"))',
    45_000,
    'the frozen project to reopen');
}

async function createProductionFixture(targetPath) {
  const projects = await expectApi('/api/automation-projects', {}, 200, 'list automation projects');
  const projectSummary = projects.body.find(project => project.projectPath === targetPath);
  assert(projectSummary, `Project was not found by exact path ${targetPath}.`);
  const project = (await expectApi(
    `/api/automation-projects/${encodeURIComponent(projectSummary.projectId)}`,
    {},
    200,
    'read created project')).body;
  const application = project.applications[0];
  assert(application, 'The created project has no default Application.');
  const ids = createFixtureIds(project.projectId, application.applicationId);
  Object.assign(summary, {
    projectPath: targetPath,
    projectId: project.projectId,
    applicationId: application.applicationId,
    topologyId: ids.topologyId,
    productionLineDefinitionId: ids.lineId
  });
  await persistSummary();

  let topology = (await expectApi(
    ids.topologyCollectionPath,
    { method: 'POST', body: { topologyId: ids.topologyId, displayName: 'Packaged E2E Line Topology' } },
    201,
    'create topology')).body;
  await expectApi(
    `/api/automation-projects/${encodeURIComponent(project.projectId)}`
      + `/applications/${encodeURIComponent(application.applicationId)}/topology`,
    { method: 'PUT', body: { topologyId: ids.topologyId } },
    200,
    'link topology to Application');

  topology = await mutateTopology(ids, topology, 'capabilities', {
    capabilityId: ids.vendorCapabilityId,
    commandName: 'RunVendorTest',
    version: '1.0.0',
    inputSchema: '{}',
    outputSchema: '{}',
    timeoutSeconds: 60,
    safetyClass: 'Normal'
  });
  topology = await mutateTopology(ids, topology, 'capabilities', {
    capabilityId: ids.prepCapabilityId,
    commandName: 'PrepareBoard',
    version: '1.0.0',
    inputSchema: '{}',
    outputSchema: '{}',
    timeoutSeconds: 10,
    safetyClass: 'Normal'
  });
  topology = await mutateTopology(ids, topology, 'systems', {
    systemId: ids.station1,
    parentSystemId: null,
    kind: 'Station',
    systemType: 'automation.preparation-station',
    displayName: 'Preparation Station',
    requiredCapabilityIds: [ids.prepCapabilityId],
    providedCapabilityIds: [ids.prepCapabilityId],
    metadata: { stationNumber: '1' }
  });
  topology = await mutateTopology(ids, topology, 'systems', {
    systemId: ids.station2,
    parentSystemId: null,
    kind: 'Station',
    systemType: 'automation.vendor-test-station',
    displayName: 'Vendor Test Station',
    requiredCapabilityIds: [ids.vendorCapabilityId],
    providedCapabilityIds: [ids.vendorCapabilityId],
    metadata: { stationNumber: '2' }
  });
  topology = await mutateTopology(ids, topology, 'driver-bindings', {
    bindingId: ids.prepBindingId,
    ownerSystemId: ids.station1,
    capabilityId: ids.prepCapabilityId,
    providerKind: 'ExternalSystem',
    providerKey: ids.prepExternalProgramResourceId
  });
  topology = await mutateTopology(ids, topology, 'driver-bindings', {
    bindingId: ids.vendorBindingId,
    ownerSystemId: ids.station2,
    capabilityId: ids.vendorCapabilityId,
    providerKind: 'ExternalSystem',
    providerKey: ids.externalProgramResourceId
  });
  topology = await mutateTopology(ids, topology, 'slot-groups', {
    slotGroupId: ids.slotGroup1,
    parentSystemId: ids.station1,
    displayName: 'Preparation Fixture',
    kind: 'FixtureNest',
    capacity: 1
  });
  topology = await mutateTopology(ids, topology, 'slots', {
    slotGroupId: ids.slotGroup1,
    slotId: ids.slot1,
    parentSystemId: ids.station1,
    address: 'PREP-01',
    displayName: 'Preparation Slot',
    materialKind: 'ProductionUnit',
    isEnabled: true
  });
  topology = await mutateTopology(ids, topology, 'slot-groups', {
    slotGroupId: ids.slotGroup2,
    parentSystemId: ids.station2,
    displayName: 'Vendor Test Fixture',
    kind: 'FixtureNest',
    capacity: 1
  });
  topology = await mutateTopology(ids, topology, 'slots', {
    slotGroupId: ids.slotGroup2,
    slotId: ids.slot2,
    parentSystemId: ids.station2,
    address: 'TEST-01',
    displayName: 'Vendor Test Slot',
    materialKind: 'ProductionUnit',
    isEnabled: true
  });
  await createLayout(ids);
  const externalPrograms = await importVendorPrograms(ids);
  const prepFlow = await createAndPublishFlow(ids, createPrepFlow(ids));
  const vendorFlow = await createAndPublishFlow(ids, createVendorFlow(ids));
  await linkFlow(ids, prepFlow.processDefinitionId);
  await linkFlow(ids, vendorFlow.processDefinitionId);
  const prepConfiguration = await createEngineeringSnapshot(
    ids,
    prepFlow,
    ids.station1,
    [{
      deviceBindingId: 'device-binding.preparation',
      ownerSystemId: ids.station1,
      capabilityId: ids.prepCapabilityId,
      deviceKey: 'simulator-preparation-axis'
    }]);
  const vendorConfiguration = await createEngineeringSnapshot(
    ids,
    vendorFlow,
    ids.station2,
    [{
      deviceBindingId: 'device-binding.vendor',
      ownerSystemId: ids.station2,
      capabilityId: ids.vendorCapabilityId,
      deviceKey: 'vendor-test-helper'
    }]);
  const line = await createProductionLine(ids, prepFlow, vendorFlow, prepConfiguration, vendorConfiguration);
  const snapshotId = `snapshot.production-closure.${runSuffix}`;
  const publishedProject = (await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}/snapshots`,
    {
      method: 'POST',
      body: {
        snapshotId,
        applicationId: ids.applicationId,
        productionLineDefinitionId: ids.lineId
      }
    },
    201,
    'publish signed project snapshot')).body;
  const snapshot = publishedProject.snapshots.find(candidate => candidate.snapshotId === snapshotId);
  assert(snapshot, 'Published Project snapshot is absent from the project response.');
  const frozenRelease = await verifyFrozenRelease(ids, snapshot, externalPrograms);
  return {
    ...ids,
    snapshotId,
    productModelId: line.productModel.productModelId,
    identityKey: line.productModel.identityInputKey,
    frozenRelease
  };
}

function createFixtureIds(projectId, applicationId) {
  const topologyId = `${applicationId}.topology.production-closure`;
  return {
    projectId,
    applicationId,
    topologyId,
    topologyCollectionPath: `/api/automation-projects/${encodeURIComponent(projectId)}`
      + `/applications/${encodeURIComponent(applicationId)}/topologies`,
    layoutId: `${applicationId}.layout.production-closure`,
    station1: `${applicationId}.station.preparation`,
    station2: `${applicationId}.station.vendor-test`,
    slotGroup1: `${applicationId}.station.preparation.group.fixture`,
    slotGroup2: `${applicationId}.station.vendor-test.group.fixture`,
    slot1: `${applicationId}.station.preparation.group.fixture.slot.1`,
    slot2: `${applicationId}.station.vendor-test.group.fixture.slot.1`,
    vendorCapabilityId: `${applicationId}.vendor.test`,
    vendorBindingId: `${applicationId}.binding.vendor-test-helper`,
    prepCapabilityId: `${applicationId}.preparation.motion`,
    prepBindingId: `${applicationId}.binding.preparation-motion`,
    externalProgramResourceId: 'program.vendor-test-helper',
    prepExternalProgramResourceId: 'program.preparation-delay',
    prepFlowId: `${applicationId}.flow.preparation`,
    vendorFlowId: `${applicationId}.flow.vendor-test`,
    lineId: `${applicationId}.line.production-closure`,
    operationPrep: 'operation.preparation',
    operationTest: 'operation.vendor-test'
  };
}

async function mutateTopology(ids, current, segment, body) {
  return (await expectApi(
    `${ids.topologyCollectionPath}/${encodeURIComponent(ids.topologyId)}/${segment}`,
    {
      method: 'POST',
      headers: ifMatch(current.revision),
      body
    },
    200,
    `mutate topology ${segment}`)).body;
}

async function createLayout(ids) {
  const base = `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
    + `/applications/${encodeURIComponent(ids.applicationId)}/layouts`;
  let layout = (await expectApi(base, {
    method: 'POST',
    body: {
      layoutId: ids.layoutId,
      topologyId: ids.topologyId,
      displayName: 'Production Closure Overview',
      canvasWidth: 1200,
      canvasHeight: 700,
      units: 'px'
    }
  }, 201, 'create topology layout')).body;
  const elements = [
    layoutElement(`${ids.station1}.shape`, 'SystemShape', 'System', ids.station1, null, 60, 80, 480, 500, 10, { appearance: 'station' }),
    layoutElement(`${ids.slotGroup1}.region`, 'GroupRegion', 'SlotGroup', ids.slotGroup1, `${ids.station1}.shape`, 80, 260, 300, 160, 20, { appearance: 'fixture-group' }),
    layoutElement(`${ids.slot1}.shape`, 'SlotShape', 'Slot', ids.slot1, `${ids.slotGroup1}.region`, 80, 65, 130, 55, 30, { appearance: 'production-unit-slot' }),
    layoutElement(`${ids.station2}.shape`, 'SystemShape', 'System', ids.station2, null, 660, 80, 480, 500, 10, { appearance: 'station' }),
    layoutElement(`${ids.slotGroup2}.region`, 'GroupRegion', 'SlotGroup', ids.slotGroup2, `${ids.station2}.shape`, 80, 260, 300, 160, 20, { appearance: 'fixture-group' }),
    layoutElement(`${ids.slot2}.shape`, 'SlotShape', 'Slot', ids.slot2, `${ids.slotGroup2}.region`, 80, 65, 130, 55, 30, { appearance: 'production-unit-slot' })
  ];
  for (const element of elements) {
    layout = (await expectApi(
      `${base}/${encodeURIComponent(ids.layoutId)}/elements`,
      { method: 'POST', headers: ifMatch(layout.revision), body: element },
      200,
      `add layout element ${element.elementId}`)).body;
  }
}

function layoutElement(elementId, kind, targetKind, targetId, parentElementId, x, y, width, height, zIndex, style) {
  return {
    elementId,
    kind,
    target: { kind: targetKind, targetId },
    parentElementId,
    x,
    y,
    width,
    height,
    rotationDegrees: 0,
    zIndex,
    style
  };
}

async function importVendorPrograms(ids) {
  const vendor = await importVendorProgram(ids, {
    resourceId: ids.externalProgramResourceId,
    displayName: 'Signed Vendor Test Helper',
    capabilityId: ids.vendorCapabilityId,
    commandName: 'RunVendorTest',
    argumentTemplates: ['--mode', '{{input.vendorMode}}', '--delay-milliseconds', '30000'],
    timeoutMilliseconds: 60000
  });
  const preparation = await importVendorProgram(ids, {
    resourceId: ids.prepExternalProgramResourceId,
    displayName: 'Signed Preparation Delay Helper',
    capabilityId: ids.prepCapabilityId,
    commandName: 'PrepareBoard',
    argumentTemplates: ['--mode', 'Delay', '--delay-milliseconds', '4000'],
    timeoutMilliseconds: 10000
  });
  return [vendor, preparation];
}

async function importVendorProgram(ids, definitionOptions) {
  const definition = {
    resourceId: definitionOptions.resourceId,
    displayName: definitionOptions.displayName,
    capabilityId: definitionOptions.capabilityId,
    commandName: definitionOptions.commandName,
    launchKind: 'ApplicationExecutable',
    entryPoint: 'files/OpenLineOps.VendorTestHelper.exe',
    providerKind: null,
    providerKey: null,
    argumentTemplates: definitionOptions.argumentTemplates,
    inputMappings: [
      { source: '$product.identity', target: 'vendorMode' },
      { source: '$product.model', target: 'productModel' }
    ],
    resultMappings: [
      { sourcePath: '$.metrics.voltage', targetKey: 'vendor.voltage', valueKind: 'FixedPoint' },
      { sourcePath: '$.metrics.attempt', targetKey: 'vendor.attempt', valueKind: 'WholeNumber' }
    ],
    outcomeMapping: {
      sourcePath: '$.outcome',
      passedToken: 'Passed',
      failedToken: 'Failed',
      abortedToken: 'Aborted'
    },
    permissionProfile: {
      profileName: 'Restricted',
      networkAccessAllowed: false,
      allowedEnvironmentVariables: []
    },
    executionLimits: {
      timeoutMilliseconds: definitionOptions.timeoutMilliseconds,
      maximumProcessCount: 4,
      maximumWorkingSetBytes: 536870912,
      maximumCpuTimeMilliseconds: definitionOptions.timeoutMilliseconds,
      maximumStandardOutputBytes: 4194304,
      maximumStandardErrorBytes: 4194304,
      maximumArtifactCount: 64,
      maximumArtifactBytes: 67108864,
      maximumTotalArtifactBytes: 268435456
    }
  };
  const files = helperFileNames.map(fileName => ({
    sourcePath: path.join(helperOutputDirectory, fileName),
    resourceRelativePath: `files/${fileName}`
  }));
  const response = await harness.uploadExternalProgram(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
      + `/applications/${encodeURIComponent(ids.applicationId)}/external-programs/import`,
    definition,
    files);
  assertStatus(response, 201, `import ${definitionOptions.resourceId} with companion files`);
  assert(response.body.files.length === helperFileNames.length, 'External program inventory is incomplete.');
  for (const file of response.body.files) {
    assertSha256(file.sha256, `Imported file ${file.relativePath}`);
  }
  return response.body;
}

function createPrepFlow(ids) {
  const workspace = JSON.stringify({
    blocks: {
      languageVersion: 0,
      blocks: [{
        type: 'openlineops_run_external_program',
        id: 'preparation-delay',
        fields: {
          TARGET_KIND: 'System',
          TARGET_ID: ids.station1,
          CAPABILITY: ids.prepCapabilityId,
          COMMAND: 'PrepareBoard',
          RESOURCE_ID: ids.prepExternalProgramResourceId,
          TIMEOUT_MS: 10000
        }
      }]
    }
  });
  return flowRequest(ids.prepFlowId, 'Preparation Operation', workspace, 10);
}

function createVendorFlow(ids) {
  const workspace = JSON.stringify({
    blocks: {
      languageVersion: 0,
      blocks: [{
        type: 'openlineops_run_external_program',
        id: 'vendor-test-helper-action',
        fields: {
          TARGET_KIND: 'System',
          TARGET_ID: ids.station2,
          CAPABILITY: ids.vendorCapabilityId,
          COMMAND: 'RunVendorTest',
          RESOURCE_ID: ids.externalProgramResourceId,
          TIMEOUT_MS: 60000
        }
      }]
    }
  });
  return flowRequest(ids.vendorFlowId, 'Vendor Test Operation', workspace, 60);
}

function flowRequest(id, displayName, blocklyWorkspaceJson, timeoutSeconds) {
  return {
    processDefinitionId: id,
    versionId: `${id}@1`,
    displayName,
    nodes: [
      node('start', 'Start', 'Start'),
      {
        nodeId: 'flow',
        kind: 'Blockly',
        displayName,
        requiredCapability: null,
        commandName: null,
        targetKind: null,
        targetId: null,
        timeoutSeconds,
        inputPayload: null,
        blocklyWorkspaceJson,
        scriptSourceCode: null,
        scriptVersion: null
      },
      node('end', 'End', 'End')
    ],
    transitions: [
      transition('start-flow', 'start', 'flow'),
      transition('flow-end', 'flow', 'end')
    ]
  };
}

function node(nodeId, kind, displayName) {
  return {
    nodeId,
    kind,
    displayName,
    requiredCapability: null,
    commandName: null,
    targetKind: null,
    targetId: null,
    timeoutSeconds: null,
    inputPayload: null,
    blocklyWorkspaceJson: null,
    scriptSourceCode: null,
    scriptVersion: null
  };
}

function transition(transitionId, fromNodeId, toNodeId) {
  return { transitionId, fromNodeId, toNodeId, label: null, loopPolicy: 'None', maxTraversals: null };
}

async function createAndPublishFlow(ids, request) {
  const collection = `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
    + `/applications/${encodeURIComponent(ids.applicationId)}/processes`;
  const created = (await expectApi(
    collection,
    { method: 'POST', body: request },
    201,
    `create Flow ${request.processDefinitionId}`)).body;
  const published = (await expectApi(
    `${collection}/${encodeURIComponent(request.processDefinitionId)}/publish`,
    { method: 'POST', headers: ifMatch(created.revision) },
    200,
    `publish Flow ${request.processDefinitionId}`)).body;
  assert(published.status === 'Published', `Flow ${request.processDefinitionId} was not published.`);
  return published;
}

async function linkFlow(ids, processDefinitionId) {
  await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
      + `/applications/${encodeURIComponent(ids.applicationId)}`
      + `/process-definitions/${encodeURIComponent(processDefinitionId)}`,
    { method: 'PUT' },
    200,
    `link Flow ${processDefinitionId}`);
}

async function createEngineeringSnapshot(ids, flow, stationSystemId, deviceBindings) {
  const suffix = stationSystemId === ids.station1 ? 'preparation' : 'vendor-test';
  const base = `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
    + `/applications/${encodeURIComponent(ids.applicationId)}/engineering`;
  const recipeId = `recipe.${suffix}`;
  const stationProfileId = `station-profile.${suffix}`;
  const workspaceId = `workspace.${suffix}`;
  const engineeringProjectId = `engineering-project.${suffix}`;
  const snapshotId = `configuration.${suffix}`;
  await expectApi(`${base}/recipes`, {
    method: 'POST',
    body: { recipeId, versionId: `${recipeId}@1`, displayName: `${suffix} recipe`, parameters: [] }
  }, 201, `create ${suffix} recipe`);
  await expectApi(`${base}/recipes/${encodeURIComponent(recipeId)}/publish`, { method: 'POST' }, 200, `publish ${suffix} recipe`);
  await expectApi(`${base}/station-profiles`, {
    method: 'POST',
    body: { stationProfileId, stationSystemId, displayName: `${suffix} Station Profile`, deviceBindings }
  }, 201, `create ${suffix} Station Profile`);
  await expectApi(`${base}/workspaces`, {
    method: 'POST',
    body: { workspaceId, displayName: `${suffix} Engineering Workspace` }
  }, 201, `create ${suffix} engineering workspace`);
  await expectApi(`${base}/projects`, {
    method: 'POST',
    body: { projectId: engineeringProjectId, workspaceId, displayName: `${suffix} Engineering Project` }
  }, 201, `create ${suffix} engineering project`);
  await expectApi(`${base}/projects/${encodeURIComponent(engineeringProjectId)}/configuration-snapshots`, {
    method: 'POST',
    body: {
      snapshotId,
      processDefinitionId: flow.processDefinitionId,
      processVersionId: flow.versionId,
      recipeId,
      stationProfileId
    }
  }, 201, `publish ${suffix} configuration snapshot`);
  return snapshotId;
}

async function createProductionLine(ids, prepFlow, vendorFlow, prepConfiguration, vendorConfiguration) {
  const response = await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
      + `/applications/${encodeURIComponent(ids.applicationId)}/production-lines`,
    {
      method: 'POST',
      body: {
        lineDefinitionId: ids.lineId,
        displayName: 'Packaged Production Closure Line',
        topologyId: ids.topologyId,
        productModel: {
          productModelId: 'product-model.vendor-helper-board',
          modelCode: 'VENDOR-HELPER-BOARD',
          identityInputKey: 'vendorMode'
        },
        entryOperationId: ids.operationPrep,
        operations: [
          {
            operationId: ids.operationPrep,
            displayName: 'Prepare Board',
            stationSystemId: ids.station1,
            flowDefinitionId: prepFlow.processDefinitionId,
            configurationSnapshotId: prepConfiguration,
            resources: [
              resource('resource.station.preparation', 'Station', ids.station1),
              resource('resource.slot.preparation', 'Slot', ids.slot1),
              resource('resource.device.preparation', 'Device', ids.prepBindingId)
            ]
          },
          {
            operationId: ids.operationTest,
            displayName: 'Run Vendor Test',
            stationSystemId: ids.station2,
            flowDefinitionId: vendorFlow.processDefinitionId,
            configurationSnapshotId: vendorConfiguration,
            resources: [
              resource('resource.station.vendor-test', 'Station', ids.station2),
              resource('resource.slot.vendor-test', 'Slot', ids.slot2),
              resource('resource.device.vendor-test', 'Device', ids.vendorBindingId)
            ]
          }
        ],
        transitions: [
          {
            transitionId: 'route.preparation-to-vendor-test',
            sourceOperationId: ids.operationPrep,
            targetOperationId: ids.operationTest,
            kind: 'Sequence',
            requiredJudgement: null,
            maxTraversals: null,
            parallelGroupId: null,
            outputKey: null,
            expectedOutputKind: null,
            expectedOutputValue: null
          },
          {
            transitionId: 'route.vendor-failed-rework',
            sourceOperationId: ids.operationTest,
            targetOperationId: ids.operationPrep,
            kind: 'Rework',
            requiredJudgement: 'Failed',
            maxTraversals: 1,
            parallelGroupId: null,
            outputKey: null,
            expectedOutputKind: null,
            expectedOutputValue: null
          }
        ],
        lineControllerAuthorizations: []
      }
    },
    201,
    'create two-Station Production Line');
  return response.body;
}

function resource(bindingId, kind, topologyTargetId) {
  return { bindingId, kind, topologyTargetId, resolution: 'Fixed' };
}

async function verifyFrozenRelease(ids, snapshot, externalPrograms) {
  assertSha256(snapshot.releaseContentSha256, 'Published release content');
  const releaseManifestPath = path.resolve(projectPath, ...snapshot.releaseManifestPath.split('/'));
  await assertFile(releaseManifestPath, 'Published release manifest');
  const manifestText = await fs.readFile(releaseManifestPath, 'utf8');
  const manifest = JSON.parse(manifestText);
  const manifestTextLower = manifestText.toLowerCase();
  assert(
    manifestTextLower.includes(ids.externalProgramResourceId.toLowerCase()),
    'Frozen release manifest does not include the external program resource.');
  for (const externalProgram of externalPrograms) {
    assert(
      manifestTextLower.includes(externalProgram.resourceId.toLowerCase()),
      `Frozen release manifest is missing ${externalProgram.resourceId}.`);
    for (const file of externalProgram.files) {
      assert(
        manifestTextLower.includes(file.sha256),
        `Frozen release manifest is missing ${externalProgram.resourceId}/${file.relativePath} SHA-256.`);
    }
  }
  const packageRoot = path.join(userDataDirectory, 'data', 'station-packages', 'distribution');
  const packages = await listFiles(packageRoot, '.olopkg');
  assert(packages.length >= 2, `Expected at least two signed Station packages under ${packageRoot}.`);
  const packageEvidence = [];
  for (const packagePath of packages) {
    packageEvidence.push({
      path: packagePath,
      sha256: await sha256File(packagePath),
      sizeBytes: (await fs.stat(packagePath)).size
    });
  }
  return {
    releaseManifestPath,
    projectRelativeReleaseManifestPath: snapshot.releaseManifestPath,
    releaseContentSha256: snapshot.releaseContentSha256,
    manifestSchema: manifest.schema ?? null,
    externalPrograms: externalPrograms.map(program => ({
      resourceId: program.resourceId,
      contentSha256: program.contentSha256,
      files: program.files
    })),
    stationPackages: packageEvidence
  };
}

async function registerLineSlots() {
  for (const [stationSystemId, slotId] of [
    [fixture.station1, fixture.slot1],
    [fixture.station2, fixture.slot2]
  ]) {
    await expectApi('/api/slot-occupancies', {
      method: 'POST',
      body: {
        lineId: fixture.lineId,
        stationSystemId,
        slotId,
        actorId,
        occurredAtUtc: nextTimestamp()
      }
    }, 201, `register Slot ${slotId}`);
  }
}

async function runConcurrentAndPassedScenario() {
  const unitA = await prepareRunAtStation1('Delay');
  const unitB = await prepareRunAtStation1('Passed', { waitForSlot: true });

  await waitForOperation(unitA.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unitA, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  await waitForOperation(unitA.runId, fixture.operationTest, 1, 'Running');
  await completeSlotAndUnload(unitB, fixture.station1, fixture.slot1, fixture.station1);
  await reserveLoadStart(unitB, fixture.station1, fixture.slot1);
  await submitPreparedRun(unitB);
  const runBAtPrep = await waitForOperation(unitB.runId, fixture.operationPrep, 1, 'Running');
  const lineState = await waitForLineState(state => {
    const station1 = state.stations.find(station => station.stationSystemId === fixture.station1);
    const station2 = state.stations.find(station => station.stationSystemId === fixture.station2);
    return station1?.activeOperations.some(operation => operation.productionRunId === unitB.runId)
      && station2?.activeOperations.some(operation => operation.productionRunId === unitA.runId)
      && state.slots.some(slot => slot.slotId === fixture.slot1 && slot.status === 'Running')
      && state.slots.some(slot => slot.slotId === fixture.slot2 && slot.status === 'Running')
      ? state : null;
  }, 'A at Station 2 while B executes at Station 1');
  const station1Operation = lineState.stations
    .find(station => station.stationSystemId === fixture.station1).activeOperations[0];
  const station2Operation = lineState.stations
    .find(station => station.stationSystemId === fixture.station2).activeOperations[0];
  assertLeaseEvidence(station1Operation);
  assertLeaseEvidence(station2Operation);
  assert(
    new Set([
      ...station1Operation.resources.map(resource => resource.fencingToken),
      ...station2Operation.resources.map(resource => resource.fencingToken)
    ].filter(value => value !== null)).size >= 2,
    'Concurrent operations did not expose independent fencing tokens.');

  await openOperationsDashboard(unitB.runId);
  await harness.waitFor(
    `document.querySelector('[data-testid=${JSON.stringify(`line-station-active-${fixture.station1}`)}]')?.textContent?.includes('Passed')`
      + ` && document.querySelector('[data-testid=${JSON.stringify(`line-station-active-${fixture.station2}`)}]')?.textContent?.includes('Delay')`,
    20_000,
    'the concurrent products in the Operations dashboard');
  const overlapScreenshot = await recordScreenshot('scenario-concurrent-two-stations');
  await harness.click('nav-topology');
  await harness.waitFor(
    `(() => {
      const live = document.querySelector('[data-testid="topology-live-projection"]');
      const firstSlot = document.querySelector('[data-testid=${JSON.stringify(`topology-slot-${fixture.station1}-${fixture.slot1}`)}]');
      const secondSlot = document.querySelector('[data-testid=${JSON.stringify(`topology-slot-${fixture.station2}-${fixture.slot2}`)}]');
      return live?.textContent?.includes('Delay')
        && live?.textContent?.includes('Passed')
        && live?.textContent?.includes('Preparation Station / ${fixture.operationPrep}')
        && live?.textContent?.includes('Vendor Test Station / ${fixture.operationTest}')
        && firstSlot?.getAttribute('data-slot-status') === 'Running'
        && secondSlot?.getAttribute('data-slot-status') === 'Running';
    })()`,
    30_000,
    'the shared 2D topology projection with both products and Running Slots');
  const topology2dScreenshot = await recordScreenshot('scenario-concurrent-topology-2d');
  await harness.click('topology-dimension-3d');
  await harness.waitFor(
    `Boolean(document.querySelector('[data-testid="topology-3d-viewport"]'))`
      + ` && document.querySelector('[data-testid="topology-live-projection"]')?.textContent?.includes('Delay')`
      + ` && document.querySelector('[data-testid="topology-live-projection"]')?.textContent?.includes('Passed')`
      + ` && document.querySelector('[data-testid=${JSON.stringify(`topology-slot-${fixture.station1}-${fixture.slot1}`)}]')?.getAttribute('data-slot-status') === 'Running'`
      + ` && document.querySelector('[data-testid=${JSON.stringify(`topology-slot-${fixture.station2}-${fixture.slot2}`)}]')?.getAttribute('data-slot-status') === 'Running'`,
    20_000,
    'the same concurrent runtime projection in 3D');
  const topology3dScreenshot = await recordScreenshot('scenario-concurrent-topology-3d');

  await waitForOperation(unitB.runId, fixture.operationPrep, 1, 'Completed');
  await completeSlotAndUnload(unitB, fixture.station1, fixture.slot1, fixture.station2);
  await waitForRun(unitA.runId, run => run.isTerminal && run.executionStatus === 'Completed', 'Delay run completion');
  await completeSlotAndUnload(unitA, fixture.station2, fixture.slot2, fixture.station2);
  await reserveLoadStart(unitB, fixture.station2, fixture.slot2);
  const passedRun = await waitForRun(
    unitB.runId,
    run => run.isTerminal && run.executionStatus === 'Completed' && run.judgement === 'Passed',
    'Passed vendor run');
  await completeSlotAndUnload(unitB, fixture.station2, fixture.slot2, fixture.station2);
  const passedTrace = await waitForTrace(unitB.runId);
  const artifacts = assertVendorArtifacts(passedTrace);
  assert(passedTrace.operations.every(operation => operation.incidentCount === 0), 'Passed run contains an Incident.');
  const passedScreenshot = await openTraceAndScreenshot(unitB.runId, 'scenario-vendor-passed-trace');

  summary.scenarios.concurrentPipeline = {
    status: 'passed',
    unitA,
    unitB,
    observedAtUtc: new Date().toISOString(),
    assertion: 'A executed at Station 2 while B executed at Station 1 with both Slots Running and independent fenced leases.',
    lineState,
    screenshots: [overlapScreenshot, topology2dScreenshot, topology3dScreenshot]
  };
  summary.scenarios.vendorPassed = {
    status: 'passed',
    run: compactRun(passedRun),
    trace: compactTrace(passedTrace),
    artifacts,
    screenshots: [passedScreenshot]
  };
  await persistSummary();
}

async function runFailedReworkScenario() {
  const unit = await prepareRunAtStation1('Failed');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  const firstFailure = await waitForRun(
    unit.runId,
    run => run.routeDecisions.some(decision => decision.transitionId === 'route.vendor-failed-rework'),
    'Failed judgement rework decision');
  assert(firstFailure.incidentCount === 0, 'Vendor product failure produced a system Incident.');
  assert(firstFailure.operations.some(operation => (
    operation.operationId === fixture.operationTest
      && operation.attempt === 1
      && operation.executionStatus === 'Completed'
      && operation.judgement === 'Failed')),
  'First vendor failure was not Completed + Failed.');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station1);
  await reserveLoadStart(unit, fixture.station1, fixture.slot1);
  await waitForOperation(unit.runId, fixture.operationPrep, 2, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  const terminal = await waitForRun(
    unit.runId,
    run => run.isTerminal && run.executionStatus === 'Completed' && run.judgement === 'Failed',
    'bounded Failed rework terminal');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station2);
  const trace = await waitForTrace(unit.runId);
  assert(trace.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-failed-rework'
      && decision.sourceJudgement === 'Failed')),
  'Trace did not freeze the Failed Rework route decision.');
  assert(trace.operations.reduce((count, operation) => count + operation.incidentCount, 0) === 0, 'Failed trace contains a system Incident.');
  const screenshot = await openTraceAndScreenshot(unit.runId, 'scenario-vendor-failed-rework-trace');
  summary.scenarios.vendorFailedRework = {
    status: 'passed',
    unit,
    run: compactRun(terminal),
    trace: compactTrace(trace),
    assertion: 'Vendor Failed remained Completed + Failed, traversed the bounded Rework edge, and created no system Incident.',
    screenshots: [screenshot]
  };
  await persistSummary();
}

async function runCancellationScenario() {
  const unit = await prepareRunAtStation1('SpawnChildDelay');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  await waitForOperation(unit.runId, fixture.operationTest, 1, 'Running');
  const vendorProcesses = await waitForVendorProcesses(processes => processes.length >= 2, 'vendor parent and child processes');
  await openOperationsDashboard(unit.runId);
  const beforeScreenshot = await recordScreenshot('scenario-cancel-spawn-child-running');
  await harness.click(`active-run-${unit.runId}`);
  await harness.click('production-command-Cancel');
  await harness.setInput('production-command-actor', actorId);
  await harness.setInput('production-command-reason', 'Packaged E2E operator cancellation');
  await harness.click('confirm-production-command');
  const terminal = await waitForRun(
    unit.runId,
    run => run.isTerminal && run.executionStatus === 'Canceled' && run.judgement === 'Aborted',
    'UI Cancel terminal state');
  await waitForVendorProcesses(
    processes => vendorProcesses.every(previous => !processes.some(current => current.processId === previous.processId)),
    'the canceled vendor process tree to disappear');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station2);
  const trace = await waitForTrace(unit.runId);
  const afterScreenshot = await openTraceAndScreenshot(unit.runId, 'scenario-cancel-spawn-child-trace');
  summary.scenarios.operatorCancel = {
    status: 'passed',
    unit,
    run: compactRun(terminal),
    vendorProcessesBeforeCancel: vendorProcesses,
    processTreeTerminated: true,
    trace: compactTrace(trace),
    screenshots: [beforeScreenshot, afterScreenshot]
  };
  await persistSummary();
}

async function runCrashScenario() {
  const unit = await prepareRunAtStation1('Crash');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  const terminal = await waitForRun(
    unit.runId,
    run => run.isTerminal && run.executionStatus === 'Failed' && run.judgement === 'Unknown',
    'vendor Crash failure');
  assert(terminal.incidentCount > 0, 'Vendor Crash did not create a system Incident.');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station2);
  const trace = await waitForTrace(unit.runId);
  const incidents = trace.operations.flatMap(operation => operation.incidents);
  assert(incidents.length > 0, 'Vendor Crash Incident is absent from Trace.');
  const screenshot = await openTraceAndScreenshot(unit.runId, 'scenario-vendor-crash-incident-trace');
  summary.scenarios.vendorCrash = {
    status: 'passed',
    unit,
    run: compactRun(terminal),
    trace: compactTrace(trace),
    incidents,
    screenshots: [screenshot]
  };
  await persistSummary();
}

async function runRecoveryScenario() {
  const unit = await prepareRunAtStation1('SpawnChildDelay');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  const running = await waitForOperation(unit.runId, fixture.operationTest, 1, 'Running');
  const operationRunId = running.operations.find(operation => (
    operation.operationId === fixture.operationTest && operation.attempt === 1)).operationRunId;
  const vendorProcesses = await waitForVendorProcesses(processes => processes.length >= 2, 'recovery vendor parent and child');
  const backend = await harness.evaluate('window.openlineopsDesktop.getBackendStatus()');
  assert(Number.isInteger(backend.pid) && backend.pid > 0, 'Packaged backend PID is unavailable.');
  await execFileAsync('taskkill.exe', ['/PID', String(backend.pid), '/F'], { windowsHide: true });
  await harness.waitFor(
    '(async () => (await window.openlineopsDesktop.getBackendStatus()).health === "Unreachable")()',
    20_000,
    'the forced Coordinator crash');
  await waitForVendorProcesses(
    processes => vendorProcesses.every(previous => !processes.some(current => current.processId === previous.processId)),
    'Job Object to kill the interrupted vendor process tree');
  await harness.evaluate('window.openlineopsDesktop.startBackend()');
  await harness.waitFor(
    '(async () => (await window.openlineopsDesktop.getBackendStatus()).health === "Healthy")()',
    60_000,
    'Coordinator restart');
  const recoveryRequired = await waitForRun(
    unit.runId,
    run => run.controlState === 'RecoveryRequired' && !run.isTerminal,
    'RecoveryRequired after Coordinator restart');
  assert(
    recoveryRequired.operations.filter(operation => operation.operationId === fixture.operationTest).length === 1,
    'Interrupted vendor Operation was replayed automatically.');
  assert((await listVendorProcesses()).length === 0, 'A vendor process was replayed after restart.');
  await openOperationsDashboard(unit.runId);
  const requiredScreenshot = await recordScreenshot('scenario-recovery-required-no-replay');
  await harness.click(`active-run-${unit.runId}`);
  await harness.click('production-command-Reconcile');
  await harness.setInput('production-command-actor', actorId);
  await harness.setInput('production-command-reason', 'Operator observed the interrupted vendor action completed safely');
  await harness.setInput('recovery-evidence-reference', `urn:openlineops:e2e:observation:${unit.runId}`);
  await harness.setSelect('recovery-operation-run', operationRunId);
  await harness.setSelect('recovery-observed-judgement', 'Passed');
  await harness.setInput('recovery-observed-outputs', JSON.stringify({
    'operator.observation': { kind: 'Text', canonicalValue: 'completed-before-crash' }
  }));
  await harness.click('confirm-production-command');
  const terminal = await waitForRun(
    unit.runId,
    run => run.isTerminal && run.executionStatus === 'Completed' && run.judgement === 'Passed'
      && run.recoveryDecisions.some(decision => decision.kind === 'Reconcile'),
    'UI Reconcile terminal state');
  assert(
    terminal.operations.filter(operation => operation.operationId === fixture.operationTest).length === 1,
    'UI Reconcile replayed the interrupted vendor Operation.');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station2);
  const trace = await waitForTrace(unit.runId);
  assert(trace.auditEntries.some(entry => entry.action === 'ProductionRun.Recovery.Reconcile'), 'Trace lacks the Reconcile audit entry.');
  const decisionScreenshot = await openTraceAndScreenshot(unit.runId, 'scenario-recovery-reconciled-trace');
  summary.scenarios.recovery = {
    status: 'passed',
    unit,
    interruptedOperationRunId: operationRunId,
    backendPidTerminated: backend.pid,
    vendorProcessesBeforeCrash: vendorProcesses,
    recoveryRequired: compactRun(recoveryRequired),
    terminal: compactRun(terminal),
    noAutomaticReplay: true,
    recoveryDecisions: terminal.recoveryDecisions,
    trace: compactTrace(trace),
    screenshots: [requiredScreenshot, decisionScreenshot]
  };
  await persistSummary();
}

async function restartStudioAndVerifyProjection() {
  const before = await expectApi(
    `/api/traceability/records?projectId=${encodeURIComponent(fixture.projectId)}&pageSize=50`,
    {},
    200,
    'trace count before Studio restart');
  const previousCdpPort = harness.cdpPort;
  await harness.close();
  const restartedApiPort = await getFreePort();
  harness = createHarness(restartedApiPort);
  await harness.start();
  await ensureBackendHealthy();
  await reopenProjectFromStartCenter(projectPath, fixture.projectId);
  const after = await expectApi(
    `/api/traceability/records?projectId=${encodeURIComponent(fixture.projectId)}&pageSize=50`,
    {},
    200,
    'trace count after Studio restart');
  assert(after.body.totalCount === before.body.totalCount, 'Trace count changed across packaged Studio restart.');
  assert(after.body.totalCount >= 6, 'Expected all production closure traces after restart.');
  const lineState = await waitForLineState(state => state.activeRunCount === 0 ? state : null, 'idle rebuilt line projection');
  await openOperationsDashboard();
  const projectionScreenshot = await recordScreenshot('restart-persisted-line-projection');
  await harness.click('nav-trace');
  await harness.waitFor(
    `document.querySelector('[data-testid="trace-search-workbench"]')?.textContent?.includes(${JSON.stringify(`${after.body.totalCount} runs`)})`,
    30_000,
    'persisted Trace search after restart');
  const traceScreenshot = await recordScreenshot('restart-persisted-trace');
  summary.restart = {
    status: 'passed',
    previousCdpPort,
    traceCountBefore: before.body.totalCount,
    traceCountAfter: after.body.totalCount,
    activeRunCount: lineState.activeRunCount,
    rebuiltProjection: lineState,
    screenshots: [projectionScreenshot, traceScreenshot]
  };
  await persistSummary();
}

async function prepareRunAtStation1(identityValue, options = {}) {
  const unit = {
    unitId: randomUUID(),
    runId: randomUUID(),
    identityValue,
    runSubmitted: false
  };
  await expectApi('/api/production-units', {
    method: 'POST',
    body: {
      productionUnitId: unit.unitId,
      productModelId: fixture.productModelId,
      identityKey: fixture.identityKey,
      identityValue,
      lotId: null,
      actorId,
      occurredAtUtc: nextTimestamp()
    }
  }, 201, `register Production Unit ${identityValue}`);
  await expectApi(`/api/production-units/${unit.unitId}/arrivals`, {
    method: 'POST',
    body: {
      lineId: fixture.lineId,
      stationSystemId: fixture.station1,
      actorId,
      occurredAtUtc: nextTimestamp()
    }
  }, 200, `arrive ${identityValue} at Station 1`);
  if (!options.waitForSlot) {
    await reserveLoadStart(unit, fixture.station1, fixture.slot1);
    await submitPreparedRun(unit);
  }
  return unit;
}

async function submitPreparedRun(unit) {
  const response = await expectApi('/api/production-runs', {
    method: 'POST',
    body: {
      projectId: fixture.projectId,
      projectSnapshotId: fixture.snapshotId,
      productionRunId: unit.runId,
      productionUnitId: unit.unitId,
      actorId
    }
  }, 202, `submit Production Run ${unit.identityValue}`);
  assert(response.body.productionRunId === unit.runId, 'Run submission returned another Run ID.');
  unit.runSubmitted = true;
}

async function reserveLoadStart(unit, stationSystemId, slotId) {
  await slotCommand(unit, stationSystemId, slotId, 'Reserve');
  await slotCommand(unit, stationSystemId, slotId, 'Load');
  await slotCommand(unit, stationSystemId, slotId, 'Start');
}

async function completeSlotAndUnload(unit, stationSystemId, slotId, destinationStationSystemId) {
  const current = await harness.api(
    `/api/slot-occupancies/${encodeURIComponent(fixture.lineId)}`
      + `/${encodeURIComponent(stationSystemId)}/${encodeURIComponent(slotId)}`);
  if (current.status !== 200 || current.body.materialId !== unit.unitId) return;
  if (current.body.status === 'Running') await slotCommand(unit, stationSystemId, slotId, 'Complete');
  await slotCommand(unit, stationSystemId, slotId, 'Unload', {
    kind: 'StationQueue',
    lineId: fixture.lineId,
    stationSystemId: destinationStationSystemId,
    slotId: null,
    carrierId: null,
    carrierPositionId: null
  });
}

async function moveUnitBetweenSlots(unit, sourceStation, sourceSlot, destinationStation, destinationSlot) {
  await completeSlotAndUnload(unit, sourceStation, sourceSlot, destinationStation);
  await reserveLoadStart(unit, destinationStation, destinationSlot);
}

async function slotCommand(unit, stationSystemId, slotId, command, destination = null) {
  await expectApi(
    `/api/slot-occupancies/${encodeURIComponent(fixture.lineId)}`
      + `/${encodeURIComponent(stationSystemId)}/${encodeURIComponent(slotId)}/commands/${command}`,
    {
      method: 'POST',
      body: {
        materialKind: 'ProductionUnit',
        materialId: unit.unitId,
        destination,
        reason: null,
        actorId,
        occurredAtUtc: nextTimestamp()
      }
    },
    200,
    `${command} ${unit.identityValue} in ${slotId}`);
}

async function waitForOperation(runId, operationId, attempt, executionStatus) {
  return waitForRun(
    runId,
    run => run.operations.some(operation => (
      operation.operationId === operationId
        && operation.attempt === attempt
        && operation.executionStatus === executionStatus)),
    `${operationId} attempt ${attempt} ${executionStatus}`);
}

async function waitForRun(runId, predicate, description, timeoutMilliseconds = 90_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest;
  while (Date.now() < deadline) {
    const response = await harness.api(`/api/production-runs/${runId}`);
    if (response.status === 200) {
      latest = response.body;
      if (predicate(latest)) return latest;
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${description}: ${JSON.stringify(latest)}`);
}

async function waitForLineState(predicate, description, timeoutMilliseconds = 45_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest;
  while (Date.now() < deadline) {
    const response = await harness.api(
      `/api/operations/lines/${encodeURIComponent(fixture.lineId)}/state`);
    if (response.status === 200) {
      latest = response.body;
      const result = predicate(latest);
      if (result) return result;
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${description}: ${JSON.stringify(latest)}`);
}

async function waitForTrace(runId, timeoutMilliseconds = 45_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest;
  while (Date.now() < deadline) {
    latest = await harness.api(`/api/traceability/records/${runId}`);
    if (latest.status === 200) return latest.body;
    await delay(250);
  }
  throw new Error(`Timed out waiting for Trace ${runId}: ${latest?.text ?? 'no response'}`);
}

async function openOperationsDashboard(runId = null) {
  await harness.click('nav-dashboard');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"operations-workbench\\"]"))',
    30_000,
    'the Operations dashboard');
  const lineOption = await harness.evaluate(
    `Array.from(document.querySelectorAll('[data-testid="operations-filter-line"] option'))`
      + `.some(option => option.value === ${JSON.stringify(fixture.lineId)})`);
  if (lineOption) await harness.setSelect('operations-filter-line', fixture.lineId);
  if (runId) {
    await harness.waitFor(
      `Boolean(document.querySelector('[data-testid=${JSON.stringify(`active-run-${runId}`)}]'))`,
      30_000,
      `active run ${runId} in Operations`);
    await harness.click(`active-run-${runId}`);
  }
}

async function openTraceAndScreenshot(runId, name) {
  await harness.click('nav-trace');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"trace-search-workbench\\"]"))',
    30_000,
    'the Trace workbench');
  await harness.waitFor(
    `Array.from(document.querySelectorAll('.trace-result-row')).some(row => row.textContent?.includes(${JSON.stringify(runId)}))`,
    30_000,
    `Trace row ${runId}`);
  await harness.evaluate(`(() => {
    const row = Array.from(document.querySelectorAll('.trace-result-row'))
      .find(candidate => candidate.textContent?.includes(${JSON.stringify(runId)}));
    row?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    return Boolean(row);
  })()`);
  await harness.waitFor(
    `document.querySelector('[data-testid="trace-detail-panel"]')?.textContent?.includes(${JSON.stringify(runId)})`,
    30_000,
    `Trace detail ${runId}`);
  return recordScreenshot(name);
}

function assertVendorArtifacts(trace) {
  const vendorOperations = trace.operations.filter(operation => (
    operation.operationId === fixture.operationTest
      && operation.executionStatus === 'Completed'
      && operation.judgement === 'Passed'));
  assert(vendorOperations.length > 0, 'Trace has no Completed + Passed vendor Operation.');
  const artifacts = vendorOperations.flatMap(operation => operation.artifacts);
  for (const required of ['measurements.csv', 'inspection.png', 'report.pdf', 'stdout.log', 'stderr.log']) {
    const artifact = artifacts.find(candidate => candidate.name === required);
    assert(artifact, `Passed Trace is missing ${required}.`);
    assert(artifact.storageKey, `${required} has no storage key.`);
    assertSha256(artifact.sha256, required);
  }
  return artifacts;
}

function assertLeaseEvidence(operation) {
  assert(operation.resources.length >= 2, `Operation ${operation.operationRunId} has incomplete resources.`);
  assert(
    operation.resources.every(resource => resource.status === 'Leased' && Number.isInteger(resource.fencingToken)),
    `Operation ${operation.operationRunId} does not have active fenced leases.`);
}

async function waitForVendorProcesses(predicate, description, timeoutMilliseconds = 20_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest = [];
  while (Date.now() < deadline) {
    latest = await listVendorProcesses();
    if (predicate(latest)) return latest;
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${description}: ${JSON.stringify(latest)}`);
}

async function listVendorProcesses() {
  const command = "$items = @(Get-CimInstance Win32_Process -Filter \"Name = 'OpenLineOps.VendorTestHelper.exe'\" | "
    + 'Select-Object @{Name=\'processId\';Expression={$_.ProcessId}},'
    + "@{Name='parentProcessId';Expression={$_.ParentProcessId}},"
    + "@{Name='commandLine';Expression={$_.CommandLine}}); "
    + 'if ($items.Count -eq 0) { Write-Output \'[]\' } else { $items | ConvertTo-Json -Compress -Depth 3 }';
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', command],
    { windowsHide: true, maxBuffer: 1024 * 1024 });
  const parsed = JSON.parse(stdout.trim() || '[]');
  return (Array.isArray(parsed) ? parsed : [parsed]).map(item => ({
    processId: Number(item.processId),
    parentProcessId: Number(item.parentProcessId),
    commandLine: item.commandLine ?? null
  }));
}

function compactRun(run) {
  return {
    productionRunId: run.productionRunId,
    productionUnitId: run.productionUnitId,
    identity: run.productionUnitIdentity,
    executionStatus: run.executionStatus,
    judgement: run.judgement,
    disposition: run.disposition,
    controlState: run.controlState,
    failureCode: run.failureCode,
    failureReason: run.failureReason,
    operationCount: run.operations.length,
    operations: run.operations,
    routeDecisions: run.routeDecisions,
    recoveryDecisions: run.recoveryDecisions,
    incidentCount: run.incidentCount
  };
}

function compactTrace(trace) {
  return {
    traceRecordId: trace.traceRecordId,
    productionRunId: trace.productionRunId,
    executionStatus: trace.executionStatus,
    judgement: trace.judgement,
    disposition: trace.disposition,
    failureCode: trace.failureCode,
    failureReason: trace.failureReason,
    operations: trace.operations,
    routeDecisions: trace.routeDecisions,
    materialLocationTransitions: trace.materialLocationTransitions,
    slotOccupancyTransitions: trace.slotOccupancyTransitions,
    dispositionTransitions: trace.dispositionTransitions,
    auditEntries: trace.auditEntries
  };
}

async function recordScreenshot(name) {
  const filePath = path.join(screenshotRoot, `${name}.png`);
  await harness.screenshot(filePath);
  return {
    name,
    path: filePath,
    sha256: await sha256File(filePath),
    sizeBytes: (await fs.stat(filePath)).size
  };
}

async function expectApi(pathname, options, expectedStatus, description) {
  const response = await harness.api(pathname, options);
  assertStatus(response, expectedStatus, description);
  return response;
}

function assertStatus(response, expectedStatus, description) {
  if (response.status !== expectedStatus) {
    throw new Error(`${description}: expected ${expectedStatus}, got ${response.status}. ${response.text}`);
  }
}

function ifMatch(revision) {
  assert(revision, 'A current editor document revision is required.');
  return { 'If-Match': `"${revision}"` };
}

function nextTimestamp() {
  logicalTimestamp = Math.max(logicalTimestamp + 1, Date.now());
  return new Date(logicalTimestamp).toISOString();
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function assertSha256(value, label) {
  assert(typeof value === 'string' && /^[0-9a-f]{64}$/u.test(value), `${label} does not have a canonical SHA-256.`);
}

async function assertFile(filePath, label) {
  const stat = await fs.stat(filePath).catch(() => null);
  if (!stat?.isFile()) throw new Error(`${label} was not found: ${filePath}`);
}

async function sha256File(filePath) {
  const hash = createHash('sha256');
  hash.update(await fs.readFile(filePath));
  return hash.digest('hex');
}

async function listFiles(root, extension) {
  const stat = await fs.stat(root).catch(() => null);
  if (!stat?.isDirectory()) return [];
  const result = [];
  const visit = async directory => {
    const entries = await fs.readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (entry.isFile() && entry.name.endsWith(extension)) result.push(fullPath);
    }
  };
  await visit(root);
  return result.sort();
}

async function runCommand(command, args, cwd) {
  await new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: 'inherit' });
    child.once('error', reject);
    child.once('exit', code => code === 0
      ? resolve()
      : reject(new Error(`${command} exited with code ${code ?? 'unknown'}.`)));
  });
}

async function persistSummary() {
  await fs.mkdir(artifactRoot, { recursive: true });
  const temporaryPath = `${summaryPath}.tmp`;
  await fs.writeFile(temporaryPath, `${JSON.stringify(summary, null, 2)}\n`, 'utf8');
  await fs.rename(temporaryPath, summaryPath);
}

main()
  .catch(async error => {
    summary.status = 'failed';
    summary.completedAtUtc = new Date().toISOString();
    summary.failure = {
      message: error instanceof Error ? error.message : String(error),
      stack: error instanceof Error ? error.stack : null
    };
    summary.diagnostics = await collectDiagnostics().catch(diagnosticError => ({
      collectionFailure: diagnosticError instanceof Error ? diagnosticError.message : String(diagnosticError)
    }));
    summary.logs = logs.slice(-300);
    await persistSummary().catch(() => undefined);
    console.error(error);
    console.error(`Failure evidence: ${summaryPath}`);
    process.exitCode = 1;
  })
  .finally(async () => {
    await harness?.close().catch(() => undefined);
  });

async function collectDiagnostics() {
  if (!harness?.cdp) return null;
  const [backendStatus, page, activeRuns, lineState] = await Promise.all([
    harness.evaluate('window.openlineopsDesktop.getBackendStatus()').catch(error => ({ error: String(error) })),
    harness.evaluate(`(() => ({
      title: document.title,
      text: (document.body?.innerText ?? '').slice(-12000),
      location: window.location.href
    }))()`).catch(error => ({ error: String(error) })),
    harness.api('/api/operations/active-runs').catch(error => ({ error: String(error) })),
    fixture?.lineId
      ? harness.api(`/api/operations/lines/${encodeURIComponent(fixture.lineId)}/state`)
        .catch(error => ({ error: String(error) }))
      : Promise.resolve(null)
  ]);
  const diagnosticScreenshot = await recordScreenshot('failure-diagnostic').catch(() => null);
  return { backendStatus, page, activeRuns, lineState, diagnosticScreenshot };
}
