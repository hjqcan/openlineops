import { execFile, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  delay,
  ElectronCdpHarness
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
const packagedRuntimeApiExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'resources',
  'app',
  'runtime',
  'api',
  'OpenLineOps.Api.exe');
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
const productionClosureEvidenceRoot = path.resolve(
  repoRoot,
  'artifacts',
  'production-closure-e2e');
const runSuffix = `${new Date().toISOString().replaceAll(/[-:.TZ]/gu, '')}-${process.pid}`;
const artifactRoot = path.join(productionClosureEvidenceRoot, runSuffix);
const privateExecutionBaseRoot = path.resolve(
  os.tmpdir(),
  'openlineops-production-closure-e2e');
const privateHandoffBaseRoot = path.resolve(
  os.tmpdir(),
  'openlineops-production-closure-handoffs');
const privateExecutionRoot = path.join(privateExecutionBaseRoot, runSuffix);
const screenshotRoot = path.join(artifactRoot, 'screenshots');
const traceArtifactSaveRoot = path.join(artifactRoot, 'verified-trace-artifact-saves');
const summaryPath = path.join(artifactRoot, 'summary.json');
const evidenceManifestPath = path.join(artifactRoot, 'evidence-manifest.json');
const privateHandoffPath = resolvePrivateHandoffPath(
  process.env.OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH);
const privateProjectEvidenceLabel = 'private-runtime/project';
const privateSourceProjectEvidenceLabel = 'private-runtime/project-source';
const logs = [];
const summary = {
  schema: 'openlineops.production-closure-e2e',
  status: 'running',
  startedAtUtc: new Date().toISOString(),
  completedAtUtc: null,
  packagedExecutable: 'packaged-desktop/OpenLineOps.exe',
  packagedBinaries: {
    before: null,
    after: null,
    unchangedDuringRun: null
  },
  artifactRoot: '.',
  projectPath: null,
  projectId: null,
  applicationId: null,
  topologyId: null,
  productionLineDefinitionId: null,
  projectSnapshotId: null,
  applicationPortability: null,
  frozenRelease: null,
  externalProgramTrial: null,
  studioAuthoring: null,
  scenarios: {},
  restart: null,
  diagnostics: null,
  failure: null
};

let harness;
let userDataDirectory;
let projectPath;
let sourceProjectPath;
let portableApplication;
let fixture;
let logicalTimestamp = Date.now();
let evidenceManifestWritten = false;
let preservePrivateExecutionRoot = false;

async function main() {
  if (typeof WebSocket === 'undefined') {
    throw new Error('Node.js 22 or newer is required for the Electron CDP harness.');
  }
  await resetProductionClosureEvidence();
  await assertFile(packagedExecutable, 'Packaged OpenLineOps executable');
  await assertFile(packagedRuntimeApiExecutable, 'Packaged OpenLineOps runtime API executable');
  await Promise.all([
    fs.mkdir(screenshotRoot, { recursive: true }),
    fs.mkdir(traceArtifactSaveRoot, { recursive: true })
  ]);
  summary.packagedBinaries.before = await capturePackagedBinaryIdentity();
  await persistSummary();
  await buildVendorHelper();

  userDataDirectory = path.join(privateExecutionRoot, 'user-data');
  projectPath = path.join(privateExecutionRoot, 'project');
  sourceProjectPath = path.join(privateExecutionRoot, 'project-source');
  await fs.mkdir(userDataDirectory, { recursive: true });
  harness = createHarness();
  await harness.start();
  await ensureBackendHealthy();
  await createProjectFromStartCenter(sourceProjectPath);
  summary.projectPath = privateSourceProjectEvidenceLabel;
  await persistSummary();
  const authoredFixture = await authorProductionFixture(sourceProjectPath);
  const importedFixture = await copyApplicationIntoTargetProject(authoredFixture);
  fixture = await publishProductionFixture(
    importedFixture.ids,
    authoredFixture.line,
    authoredFixture.externalPrograms);
  await recordPortableApplicationPhase('afterPublishTreeSha256', portableApplication.targetRoot);
  Object.assign(summary, {
    projectPath: privateProjectEvidenceLabel,
    projectId: fixture.projectId,
    applicationId: fixture.applicationId,
    topologyId: fixture.topologyId,
    productionLineDefinitionId: fixture.lineId,
    projectSnapshotId: fixture.snapshotId,
    frozenRelease: fixture.frozenRelease
  });
  await persistSummary();

  await reopenProjectFromStartCenter(projectPath, fixture.projectId);
  await verifySavedRouteAuthoring();
  await registerLineSlots();
  await runConcurrentAndPassedScenario();
  await runFailedReworkScenario();
  await runCancellationScenario();
  await runCrashScenario();
  await runRecoveryScenario();
  await restartStudioAndVerifyProjection();
  await recordPortableApplicationPhase('afterExecutionTreeSha256', portableApplication.targetRoot);
  await recordPortableApplicationPhase('sourceAfterExecutionTreeSha256', portableApplication.sourceRoot);
  summary.applicationPortability.status = 'passed';
  summary.applicationPortability.unchanged = true;
  await persistSummary();

  summary.packagedBinaries.after = await capturePackagedBinaryIdentity();
  assertPackagedBinaryIdentityUnchanged(
    summary.packagedBinaries.before,
    summary.packagedBinaries.after);
  summary.packagedBinaries.unchangedDuringRun = true;
  summary.status = 'passed';
  summary.completedAtUtc = new Date().toISOString();
  await persistSummary();
  await writeEvidenceManifest();
  evidenceManifestWritten = true;
  console.log(`OpenLineOps packaged production closure E2E passed: ${summaryPath}`);
}

function resolvePrivateHandoffPath(configuredPath) {
  if (configuredPath === undefined) return null;
  if (configuredPath.length === 0
      || configuredPath.trim() !== configuredPath
      || !path.isAbsolute(configuredPath)) {
    throw new Error(
      'OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH must be one canonical absolute file path.');
  }

  const resolvedPath = path.resolve(configuredPath);
  if (resolvedPath !== configuredPath) {
    throw new Error(
      'OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH must already be canonical.');
  }
  const relativeHandoffPath = path.relative(privateHandoffBaseRoot, resolvedPath);
  const handoffSegments = relativeHandoffPath.split(path.sep);
  if (relativeHandoffPath.startsWith(`..${path.sep}`)
      || path.isAbsolute(relativeHandoffPath)
      || handoffSegments.length !== 2
      || !/^[0-9a-f]{32}$/u.test(handoffSegments[0])
      || handoffSegments[1] !== 'production-closure-handoff.json') {
    throw new Error(
      'OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH must be <system-temp>/openlineops-production-closure-handoffs/<32-lowercase-hex>/production-closure-handoff.json.');
  }
  for (const forbiddenRoot of [privateExecutionRoot, artifactRoot]) {
    if (resolvedPath.startsWith(`${path.resolve(forbiddenRoot)}${path.sep}`)) {
      throw new Error(
        'OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH must remain outside both private execution and public evidence roots.');
    }
  }

  return resolvedPath;
}

async function resetProductionClosureEvidence() {
  const artifactsRoot = path.resolve(repoRoot, 'artifacts');
  if (path.dirname(productionClosureEvidenceRoot) !== artifactsRoot
      || path.basename(productionClosureEvidenceRoot) !== 'production-closure-e2e') {
    throw new Error(
      `Refusing to clean a non-canonical production closure evidence root: ${productionClosureEvidenceRoot}`);
  }

  await assertNoReparsePointsForRecursiveDelete(
    productionClosureEvidenceRoot,
    'public production closure evidence root');
  await fs.rm(productionClosureEvidenceRoot, {
    recursive: true,
    force: true,
    maxRetries: 10,
    retryDelay: 250
  });
  const resolvedTempRoot = path.resolve(os.tmpdir());
  const resolvedPrivateBase = path.resolve(privateExecutionBaseRoot);
  const resolvedPrivateRoot = path.resolve(privateExecutionRoot);
  if (!resolvedPrivateBase.startsWith(`${resolvedTempRoot}${path.sep}`)
      || !resolvedPrivateRoot.startsWith(`${resolvedPrivateBase}${path.sep}`)
      || path.basename(resolvedPrivateRoot) !== runSuffix) {
    throw new Error(`Refusing to clean a non-canonical private E2E root: ${resolvedPrivateRoot}`);
  }
  await assertNoReparsePointsForRecursiveDelete(
    resolvedPrivateRoot,
    'private production closure execution root');
  await fs.rm(resolvedPrivateRoot, {
    recursive: true,
    force: true,
    maxRetries: 10,
    retryDelay: 250
  });
  await fs.mkdir(resolvedPrivateRoot, { recursive: true });
}

async function assertNoReparsePointsForRecursiveDelete(root, label) {
  try {
    const rootStat = await fs.lstat(root);
    if (rootStat.isSymbolicLink()) {
      throw new Error(`${label} cannot be a symbolic link or junction.`);
    }
  } catch (error) {
    if (error?.code === 'ENOENT') return;
    throw error;
  }

  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    const entries = await fs.readdir(current, { withFileTypes: true });
    for (const entry of entries) {
      const candidate = path.join(current, entry.name);
      const stat = await fs.lstat(candidate);
      if (stat.isSymbolicLink()) {
        throw new Error(`${label} contains a symbolic link or junction.`);
      }
      if (stat.isDirectory()) pending.push(candidate);
    }
  }
}

function createHarness() {
  return new ElectronCdpHarness({
    executablePath: packagedExecutable,
    workingDirectory: path.dirname(packagedExecutable),
    userDataDirectory,
    environment: {
      OPENLINEOPS_REPO_ROOT: repoRoot,
      OPENLINEOPS_DESKTOP_LOG_PATH: path.join(privateExecutionRoot, 'desktop-logs'),
      OPENLINEOPS_E2E_TRACE_ARTIFACT_SAVE_ROOT: traceArtifactSaveRoot
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
    'document.querySelector("[data-testid=\\"start-open-project-by-path\\"]")?.disabled === false',
    20_000,
    'the enabled Open Project entry point');
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

async function authorProductionFixture(targetPath) {
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
    projectPath: privateSourceProjectEvidenceLabel,
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
  const protocolTrial = (await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
      + `/applications/${encodeURIComponent(ids.applicationId)}`
      + `/external-programs/${encodeURIComponent(ids.prepExternalProgramResourceId)}/trial`,
    {
      method: 'POST',
      body: {
        inputs: {
          vendorMode: { kind: 'Text', canonicalValue: 'Passed' },
          productModel: { kind: 'Text', canonicalValue: 'PROTOCOL-TRIAL-BOARD' }
        }
      }
    },
    200,
    'run the imported external program protocol trial')).body;
  assert(protocolTrial.executionStatus === 'Completed', 'Protocol trial did not complete.');
  assert(protocolTrial.judgement === 'Passed', 'Protocol trial did not return Passed.');
  assert(protocolTrial.artifacts.length > 0, 'Protocol trial produced no hashed artifacts.');
  summary.externalProgramTrial = {
    status: 'passed',
    executionStatus: protocolTrial.executionStatus,
    judgement: protocolTrial.judgement,
    artifactCount: protocolTrial.artifacts.length
  };
  await persistSummary();
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
  await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}/manifest`,
    { method: 'PUT' },
    200,
    'save the complete Application project before portable copy');
  return {
    ...ids,
    line,
    externalPrograms
  };
}

async function publishProductionFixture(ids, line, externalPrograms) {
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
  const runContext = (await expectApi(
    `/api/automation-projects/${encodeURIComponent(ids.projectId)}`
      + `/snapshots/${encodeURIComponent(snapshotId)}/production-run-context`,
    { method: 'GET' },
    200,
    'resolve immutable Production Run context')).body;
  assert(
    runContext.entryStationSystemId === ids.station1,
    'Production Run context entry Station differs from the published route.');
  assert(
    typeof runContext.entryStationId === 'string' && runContext.entryStationId.length > 0,
    'Production Run context has no physical entry Station identity.');
  assert(
    /^[0-9a-f]{64}$/u.test(runContext.entryStationPackageContentSha256),
    'Production Run context has no canonical entry Station package content SHA-256.');
  return {
    ...ids,
    snapshotId,
    productModelId: line.productModel.productModelId,
    identityKey: line.productModel.identityInputKey,
    entryStationId: runContext.entryStationId,
    entryStationPackageContentSha256: runContext.entryStationPackageContentSha256,
    frozenRelease: {
      ...frozenRelease,
      entryStationDeployment: {
        stationSystemId: runContext.entryStationSystemId,
        stationId: runContext.entryStationId,
        packageContentSha256: runContext.entryStationPackageContentSha256
      }
    }
  };
}

async function copyApplicationIntoTargetProject(authoredFixture) {
  const targetProjectId = `project.production-closure.imported.${runSuffix}`;
  await expectApi(
    '/api/automation-project-workspaces',
    {
      method: 'POST',
      body: {
        projectId: targetProjectId,
        displayName: 'Imported Production Closure Project',
        projectPath,
        defaultApplicationId: null,
        defaultApplicationName: null
      }
    },
    201,
    'create target Project without a default Application');

  const sourceApplicationFiles = await listFiles(
    path.join(sourceProjectPath, 'applications'),
    '.oloapp');
  assert(
    sourceApplicationFiles.length === 1,
    'The source Project must contain exactly one complete Application project file.');
  const sourceApplicationFile = sourceApplicationFiles[0];
  const sourceRoot = path.dirname(sourceApplicationFile);
  const targetRoot = path.join(projectPath, 'applications', path.basename(sourceRoot));
  const sourceBeforeCopy = await captureApplicationTreeIdentity(sourceRoot);
  assert(sourceBeforeCopy.fileCount > 0, 'The source Application cannot be empty.');

  const targetBeforeCopy = await fs.lstat(targetRoot).catch(error => {
    if (error?.code === 'ENOENT') return null;
    throw error;
  });
  assert(targetBeforeCopy === null, 'The target Application directory must not exist before copy.');
  await fs.mkdir(path.dirname(targetRoot), { recursive: true });
  await fs.cp(sourceRoot, targetRoot, {
    recursive: true,
    force: false,
    errorOnExist: true,
    preserveTimestamps: true
  });
  const copied = await captureApplicationTreeIdentity(targetRoot);
  assertSameApplicationTree(sourceBeforeCopy, copied, 'copied Application');

  const targetApplicationFile = path.join(
    targetRoot,
    path.relative(sourceRoot, sourceApplicationFile));
  const importResponse = await expectApi(
    `/api/automation-projects/${encodeURIComponent(targetProjectId)}/applications/import`,
    {
      method: 'POST',
      body: { projectFilePath: targetApplicationFile }
    },
    200,
    'import the byte-identical Application into the target Project');
  assert(
    importResponse.body.project?.applications?.some(application => (
      application.applicationId === authoredFixture.applicationId)),
    'The target Project did not register the copied Application identity.');
  const afterImport = await captureApplicationTreeIdentity(targetRoot);
  assertSameApplicationTree(sourceBeforeCopy, afterImport, 'imported Application');

  portableApplication = {
    sourceRoot,
    targetRoot,
    baseline: sourceBeforeCopy
  };
  summary.applicationPortability = {
    status: 'imported',
    sourceProjectId: authoredFixture.projectId,
    targetProjectId,
    applicationId: authoredFixture.applicationId,
    fileCount: sourceBeforeCopy.fileCount,
    totalSizeBytes: sourceBeforeCopy.totalSizeBytes,
    sourceBeforeCopyTreeSha256: sourceBeforeCopy.treeSha256,
    copiedTreeSha256: copied.treeSha256,
    afterImportTreeSha256: afterImport.treeSha256,
    afterPublishTreeSha256: null,
    afterExecutionTreeSha256: null,
    sourceAfterExecutionTreeSha256: null,
    unchanged: false
  };
  await persistSummary();

  return {
    ids: createFixtureIds(targetProjectId, authoredFixture.applicationId)
  };
}

async function recordPortableApplicationPhase(propertyName, applicationRoot) {
  assert(portableApplication && summary.applicationPortability,
    'Portable Application evidence has not been initialized.');
  const identity = await captureApplicationTreeIdentity(applicationRoot);
  assertSameApplicationTree(portableApplication.baseline, identity, propertyName);
  summary.applicationPortability[propertyName] = identity.treeSha256;
  await persistSummary();
}

async function verifySavedRouteAuthoring() {
  const lineTestId = `production-line-${fixture.lineId}`;
  const lineSelector = `[data-testid=${JSON.stringify(lineTestId)}]`;
  await harness.click('nav-production');
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"production-workbench\\"]"))'
      + ` && Boolean(document.querySelector(${JSON.stringify(lineSelector)}))`,
    30_000,
    'the saved production route to appear in Line Designer');
  let evidence;
  try {
    await harness.waitFor(
      `(() => {
        const line = document.querySelector(${JSON.stringify(lineSelector)});
        return line instanceof HTMLButtonElement && line.disabled === false;
      })()`,
      30_000,
      'the saved production route selector to become enabled');
    await harness.click(lineTestId);
    await harness.waitFor(
      `(() => {
        const lineId = document.querySelector('[data-testid="production-line-id"]');
        const dirtyState = document.querySelector('[data-testid="production-dirty-state"]');
        const guard = document.querySelector('[data-testid="production-draft-transition-dialog"]');
        return lineId?.value === ${JSON.stringify(fixture.lineId)}
          && dirtyState?.textContent?.trim() === 'Saved'
          && guard === null;
      })()`,
      30_000,
      'the exact saved production route to finish opening without an unsaved-change guard');
    evidence = await harness.waitFor(
      `(() => {
        const model = document.querySelector('[data-testid="production-product-model-id"]');
        const nodes = document.querySelectorAll('[data-testid^="production-operation-node-"]');
        const terminals = document.querySelectorAll('[data-testid^="production-terminal-node-"]');
        const edges = document.querySelectorAll('[data-testid^="production-transition-edge-"]');
        const completed = document.querySelector('[data-testid="production-terminal-node-Completed"]');
        const nonconforming = document.querySelector('[data-testid="production-terminal-node-Nonconforming"]');
        const vendorOperation = document.querySelector('[data-testid="production-operation-node-operation.vendor-test"]');
        const publish = document.querySelector('[data-testid="publish-production-line-snapshot"]');
        const ready = model?.value === ${JSON.stringify(fixture.productModelId)}
          && nodes.length === 2
          && terminals.length === 2
          && edges.length === 4
          && completed?.textContent?.includes('Completed') === true
          && nonconforming?.textContent?.includes('Nonconforming') === true
          && vendorOperation?.textContent?.includes('2 CONDITIONAL TERMINALS') === true
          && publish?.disabled === false;
        return ready ? {
          productModelId: model.value,
          operationCount: nodes.length,
          terminalCount: terminals.length,
          terminalDispositions: [completed.textContent, nonconforming.textContent],
          transitionCount: edges.length,
          publishEnabled: true
        } : false;
      })()`,
      30_000,
      'the saved Sequence, bounded Rework, and explicit terminal routes to remain responsive');
  } catch (error) {
    const domObservation = await harness.evaluate(`(async () => {
        const line = document.querySelector(${JSON.stringify(lineSelector)});
        const lineId = document.querySelector('[data-testid="production-line-id"]');
        const dirtyState = document.querySelector('[data-testid="production-dirty-state"]');
        const model = document.querySelector('[data-testid="production-product-model-id"]');
        const nodes = document.querySelectorAll('[data-testid^="production-operation-node-"]');
        const terminals = document.querySelectorAll('[data-testid^="production-terminal-node-"]');
        const edges = document.querySelectorAll('[data-testid^="production-transition-edge-"]');
        const completed = document.querySelector('[data-testid="production-terminal-node-Completed"]');
        const nonconforming = document.querySelector('[data-testid="production-terminal-node-Nonconforming"]');
        const vendorOperation = document.querySelector('[data-testid="production-operation-node-operation.vendor-test"]');
        const publish = document.querySelector('[data-testid="publish-production-line-snapshot"]');
        const dirtyLabel = dirtyState?.textContent?.trim();
        let backendHealthy = false;
        let backendStatusAvailable = false;
        try {
          const backendStatus = await window.openlineopsDesktop?.getBackendStatus();
          backendStatusAvailable = backendStatus !== undefined && backendStatus !== null;
          backendHealthy = backendStatus?.health === 'Healthy';
        } catch {
          backendStatusAvailable = false;
        }
        return {
          backendStatusAvailable,
          backendHealthy,
          workbenchPresent: document.querySelector('[data-testid="production-workbench"]') !== null,
          lineSelectorPresent: line !== null,
          lineSelectorIsButton: line instanceof HTMLButtonElement,
          lineSelectorDisabled: line instanceof HTMLButtonElement ? line.disabled : null,
          lineIdPresent: lineId !== null,
          lineIdMatchesExpected: lineId?.value === ${JSON.stringify(fixture.lineId)},
          dirtyState: dirtyLabel === 'Saved' || dirtyLabel === 'Unsaved' || dirtyLabel === 'New'
            ? dirtyLabel
            : dirtyLabel === undefined ? 'Missing' : 'Other',
          guardDialogPresent: document.querySelector('[data-testid="production-draft-transition-dialog"]') !== null,
          productModelPresent: model !== null,
          productModelMatchesExpected: model?.value === ${JSON.stringify(fixture.productModelId)},
          operationCount: nodes.length,
          terminalCount: terminals.length,
          transitionCount: edges.length,
          completedTerminalPresent: completed !== null,
          completedTerminalLabelMatches: completed?.textContent?.includes('Completed') === true,
          nonconformingTerminalPresent: nonconforming !== null,
          nonconformingTerminalLabelMatches: nonconforming?.textContent?.includes('Nonconforming') === true,
          vendorOperationPresent: vendorOperation !== null,
          vendorConditionalTerminalLabelMatches:
            vendorOperation?.textContent?.includes('2 CONDITIONAL TERMINALS') === true,
          publishPresent: publish !== null,
          publishDisabled: publish instanceof HTMLButtonElement ? publish.disabled : null,
          problemCount: document.querySelectorAll('.production-problems-list > button').length
        };
      })()`).catch(() => ({ observationAvailable: false }));
    const rendererStack = await harness.captureJavaScriptStack().catch(stackError => ([{
      captureError: stackError instanceof Error ? stackError.message : String(stackError)
    }]));
    const privateDiagnosticBytes = Buffer.from(
      `${JSON.stringify({ rendererStack, domObservation }, null, 2)}\n`,
      'utf8');
    const privateDiagnosticDirectory = path.join(privateExecutionRoot, 'diagnostics');
    await fs.mkdir(privateDiagnosticDirectory, { recursive: true });
    await fs.writeFile(
      path.join(privateDiagnosticDirectory, 'route-authoring-diagnostic.json'),
      privateDiagnosticBytes,
      { flag: 'wx', mode: 0o600 });
    summary.diagnostics = {
      code: 'RouteAuthoringUnresponsive',
      detailSha256: createHash('sha256').update(privateDiagnosticBytes).digest('hex')
    };
    await persistSummary();
    throw new Error('Saved production route authoring did not become responsive.',
      { cause: error });
  }
  await harness.click('production-operation-node-operation.vendor-test');
  await harness.waitFor(
    'document.querySelector("[data-testid=\\"production-operation-inspector\\"]")'
      + '?.textContent?.includes("Run Vendor Test") === true'
      + ' && document.querySelector("[data-testid=\\"production-operation-inspector\\"]")'
      + '?.textContent?.includes("Completed / Nonconforming (conditional terminals)") === true',
    15_000,
    'the saved Vendor Test Operation inspector to respond');
  const screenshot = await recordScreenshot('studio-saved-route-authoring');
  summary.studioAuthoring = { status: 'passed', ...evidence, screenshot };
  await persistSummary();
}

function createFixtureIds(projectId, applicationId) {
  const topologyId = `${applicationId}.topology.production-closure`;
  return {
    projectId,
    applicationId,
    topologyId,
    topologyCollectionPath: `/api/automation-projects/${encodeURIComponent(projectId)}`
      + `/applications/${encodeURIComponent(applicationId)}/topologies`,
    layoutId: `${applicationId}.layout.main`,
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
              resource('resource.slot.preparation', 'Slot', ids.slot1)
            ],
            inputMappings: []
          },
          {
            operationId: ids.operationTest,
            displayName: 'Run Vendor Test',
            stationSystemId: ids.station2,
            flowDefinitionId: vendorFlow.processDefinitionId,
            configurationSnapshotId: vendorConfiguration,
            resources: [
              resource('resource.station.vendor-test', 'Station', ids.station2),
              resource('resource.slot.vendor-test', 'Slot', ids.slot2)
            ],
            inputMappings: []
          }
        ],
        transitions: [
          {
            transitionId: 'route.preparation-to-vendor-test',
            sourceOperationId: ids.operationPrep,
            targetOperationId: ids.operationTest,
            terminalDisposition: null,
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
            terminalDisposition: null,
            kind: 'Rework',
            requiredJudgement: 'Failed',
            maxTraversals: 1,
            parallelGroupId: null,
            outputKey: null,
            expectedOutputKind: null,
            expectedOutputValue: null
          },
          {
            transitionId: 'route.vendor-failed-terminal',
            sourceOperationId: ids.operationTest,
            targetOperationId: null,
            terminalDisposition: 'Nonconforming',
            kind: 'Judgement',
            requiredJudgement: 'Failed',
            maxTraversals: null,
            parallelGroupId: null,
            outputKey: null,
            expectedOutputKind: null,
            expectedOutputValue: null
          },
          {
            transitionId: 'route.vendor-default-terminal',
            sourceOperationId: ids.operationTest,
            targetOperationId: null,
            terminalDisposition: 'Completed',
            kind: 'Sequence',
            requiredJudgement: null,
            maxTraversals: null,
            parallelGroupId: null,
            outputKey: null,
            expectedOutputKind: null,
            expectedOutputValue: null
          }
        ],
        lineControllerAuthorizations: [],
        routeLayout: {
          operationPositions: [
            { operationId: ids.operationPrep, x: 160, y: 80 },
            { operationId: ids.operationTest, x: 450, y: 264 }
          ]
        }
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
  assert(
    snapshot.layoutIds?.includes(ids.layoutId),
    `Published snapshot does not include the Application line layout ${ids.layoutId}.`);
  const releaseManifestPath = path.resolve(projectPath, ...snapshot.releaseManifestPath.split('/'));
  await assertFile(releaseManifestPath, 'Published release manifest');
  const manifestText = await fs.readFile(releaseManifestPath, 'utf8');
  const manifest = JSON.parse(manifestText);
  const manifestTextLower = manifestText.toLowerCase();
  assert(
    manifest.metadata?.layoutIds?.includes(ids.layoutId),
    `Frozen release manifest does not include the Application line layout ${ids.layoutId}.`);
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
  assert(packages.length === 2, `Expected exactly two signed Station packages under ${packageRoot}.`);
  const catalogRoot = path.join(userDataDirectory, 'data', 'station-packages', 'deployment-catalog');
  const catalogs = await listFiles(catalogRoot, '.json');
  assert(catalogs.length === 2, `Expected exactly two Station deployment catalogs under ${catalogRoot}.`);
  const deploymentCatalogs = [];
  for (const catalogPath of catalogs) {
    const catalog = JSON.parse(await fs.readFile(catalogPath, 'utf8'));
    assert(catalog.schema === 'openlineops.station-package-deployment',
      `Station deployment catalog has an unexpected schema: ${catalogPath}`);
    assert(catalog.projectId === ids.projectId && catalog.applicationId === ids.applicationId,
      `Station deployment catalog belongs to another Project/Application: ${catalogPath}`);
    assert(catalog.projectSnapshotId === snapshot.snapshotId,
      `Station deployment catalog belongs to another Project Snapshot: ${catalogPath}`);
    assert(catalog.productionLineDefinitionId === ids.lineId,
      `Station deployment catalog belongs to another Production Line: ${catalogPath}`);
    assert([ids.station1, ids.station2].includes(catalog.stationSystemId),
      `Station deployment catalog contains an unknown Station System: ${catalogPath}`);
    assertSha256(catalog.packageContentSha256, `${catalog.stationSystemId} package content`);
    const relativePath = `public-release/deployment-catalog/${path.basename(catalogPath)}`;
    deploymentCatalogs.push({
      stationSystemId: catalog.stationSystemId,
      packageContentSha256: catalog.packageContentSha256,
      ...(await exportPublicEvidenceFile(catalogPath, relativePath))
    });
  }
  assert(new Set(deploymentCatalogs.map(item => item.stationSystemId)).size === 2,
    'Station deployment catalogs do not describe two distinct Station Systems.');

  const packageEvidence = [];
  for (const packagePath of packages) {
    const packageContentSha256 = path.basename(packagePath, '.olopkg');
    assertSha256(packageContentSha256, `Station package ${packagePath} content identity`);
    const deployment = deploymentCatalogs.find(
      candidate => candidate.packageContentSha256 === packageContentSha256);
    assert(deployment, `Station package ${packageContentSha256} has no matching deployment catalog.`);
    const relativePath = `public-release/station-packages/${path.basename(packagePath)}`;
    packageEvidence.push({
      stationSystemId: deployment.stationSystemId,
      packageContentSha256,
      ...(await exportPublicEvidenceFile(packagePath, relativePath))
    });
  }
  assert(new Set(packageEvidence.map(item => item.stationSystemId)).size === 2,
    'Signed Station packages do not describe two distinct Station Systems.');

  const publicManifest = await exportPublicEvidenceFile(
    releaseManifestPath,
    'public-release/frozen-manifest.json');
  const signingPublicKey = await exportPublicEvidenceFile(
    path.join(userDataDirectory, 'data', 'station-packages', 'keys', 'release-signing-public.pem'),
    'public-release/release-signing-public.pem');
  return {
    releaseManifest: publicManifest,
    projectRelativeReleaseManifestPath: snapshot.releaseManifestPath,
    releaseContentSha256: snapshot.releaseContentSha256,
    manifestSchema: manifest.schema ?? null,
    externalPrograms: externalPrograms.map(program => ({
      resourceId: program.resourceId,
      contentSha256: program.contentSha256,
      files: program.files
    })),
    stationPackages: packageEvidence,
    deploymentCatalogs,
    signingPublicKey
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
  const frozenTraceBeforeUnloadResponse = await waitForTraceResponse(unitB.runId);
  const frozenTraceBeforeUnload = frozenTraceBeforeUnloadResponse.body;
  const frozenTraceBeforeUnloadBytes = Buffer.from(
    frozenTraceBeforeUnloadResponse.text,
    'utf8');
  const unloadAtUtc = await completeSlotAndUnload(
    unitB,
    fixture.station2,
    fixture.slot2,
    fixture.station2);
  assert(typeof unloadAtUtc === 'string',
    'The final Slot unload did not return its exact lifecycle timestamp.');
  const passedTraceResponse = await waitForTraceResponse(unitB.runId);
  const passedTrace = passedTraceResponse.body;
  const passedTraceBytes = Buffer.from(passedTraceResponse.text, 'utf8');
  assert(passedTraceBytes.equals(frozenTraceBeforeUnloadBytes),
    'Immutable Run Trace changed after the final Slot unload.');
  const immutableRunTrace = {
    before: byteIdentity(frozenTraceBeforeUnloadBytes),
    after: byteIdentity(passedTraceBytes),
    unchanged: true,
    terminalCompletedAtUtc: passedTrace.completedAtUtc,
    unloadAtUtc
  };
  assert(immutableRunTrace.before.sha256 === immutableRunTrace.after.sha256
    && immutableRunTrace.before.sizeBytes === immutableRunTrace.after.sizeBytes,
  'Immutable Run Trace byte identity changed after the final Slot unload.');
  assert(Date.parse(immutableRunTrace.unloadAtUtc)
    > Date.parse(immutableRunTrace.terminalCompletedAtUtc),
  'The final Slot unload timestamp must be after terminal completion.');
  const passedMaterialLifecycle = await waitForMaterialLifecycle(unitB.unitId);
  assert(passedMaterialLifecycle.productionUnitId === unitB.unitId,
    'Product material lifecycle returned another Production Unit.');
  assert(passedMaterialLifecycle.currentLocation?.kind === 'StationQueue'
    && passedMaterialLifecycle.currentLocation?.stationSystemId === fixture.station2,
  'Product material lifecycle did not rebuild the final Station queue location.');
  assert(passedMaterialLifecycle.materialLocationTransitions.some(transition => (
    transition.productionRunId === unitB.runId
      && transition.destination?.kind === 'StationQueue'
      && Date.parse(transition.occurredAtUtc) > Date.parse(passedTrace.completedAtUtc))),
  'Product material lifecycle is missing the post-run final unload movement.');
  assert(passedMaterialLifecycle.slotOccupancyTransitions.some(transition => (
    transition.productionRunId === unitB.runId
      && transition.stationSystemId === fixture.station2
      && transition.slotId === fixture.slot2
      && transition.currentStatus === 'Available'
      && Date.parse(transition.occurredAtUtc) > Date.parse(passedTrace.completedAtUtc))),
  'Product material lifecycle is missing the post-run Slot release.');
  assert(passedRun.disposition === 'Completed', 'Passed run did not select the Completed disposition.');
  assert(passedTrace.disposition === 'Completed', 'Passed trace did not freeze the Completed disposition.');
  assert(passedRun.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-default-terminal'
      && decision.targetOperationId === null
      && decision.terminalDisposition === 'Completed')),
  'Passed run did not select the explicit Completed terminal route.');
  assert(passedTrace.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-default-terminal'
      && decision.targetOperationId === null
      && decision.terminalDisposition === 'Completed')),
  'Passed trace did not freeze the explicit Completed terminal route decision.');
  const artifacts = assertVendorArtifacts(passedTrace);
  const artifactDownloads = await downloadAndVerifyArtifacts(artifacts);
  assert(passedTrace.operations.every(operation => operation.incidentCount === 0), 'Passed run contains an Incident.');
  const passedScreenshot = await openTraceAndScreenshot(unitB.runId, 'scenario-vendor-passed-trace');
  const verifiedSaveActionCount = await harness.evaluate(
    'document.querySelectorAll(`[data-testid^="trace-artifact-save-"]`).length');
  assert(verifiedSaveActionCount >= 5,
    'Trace workbench did not expose verified evidence save actions.');
  const verifiedArtifactSave = await saveArtifactThroughDesktopIpc(
    artifacts.find(artifact => artifact.name === 'measurements.csv'));

  summary.scenarios.concurrentPipeline = {
    status: 'passed',
    unitA: publicUnit(unitA),
    unitB: publicUnit(unitB),
    observedAtUtc: new Date().toISOString(),
    assertion: 'A executed at Station 2 while B executed at Station 1 with both Slots Running and independent fenced leases.',
    lineState: publicLineState(lineState),
    screenshots: [overlapScreenshot, topology2dScreenshot, topology3dScreenshot]
  };
  summary.scenarios.vendorPassed = {
    status: 'passed',
    run: compactRun(passedRun),
    trace: compactTrace(passedTrace),
    immutableRunTrace,
    materialLifecycle: compactMaterialLifecycle(passedMaterialLifecycle),
    artifacts: artifacts.map(publicArtifactMetadata),
    artifactDownloads: artifactDownloads.map(publicArtifactMetadata),
    verifiedSaveActionCount,
    verifiedArtifactSave,
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
  assert(terminal.disposition === 'Nonconforming', 'Bounded Failed run did not select the Nonconforming disposition.');
  assert(terminal.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-failed-terminal'
      && decision.targetOperationId === null
      && decision.terminalDisposition === 'Nonconforming')),
  'Bounded Failed run did not select the explicit Nonconforming terminal route.');
  await completeSlotAndUnload(unit, fixture.station2, fixture.slot2, fixture.station2);
  const trace = await waitForTrace(unit.runId);
  assert(trace.disposition === 'Nonconforming', 'Bounded Failed trace did not freeze the Nonconforming disposition.');
  assert(trace.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-failed-rework'
      && decision.sourceJudgement === 'Failed')),
  'Trace did not freeze the Failed Rework route decision.');
  assert(trace.routeDecisions.some(decision => (
    decision.transitionId === 'route.vendor-failed-terminal'
      && decision.targetOperationId === null
      && decision.terminalDisposition === 'Nonconforming')),
  'Trace did not freeze the explicit Nonconforming terminal route decision.');
  assert(trace.operations.reduce((count, operation) => count + operation.incidentCount, 0) === 0, 'Failed trace contains a system Incident.');
  const screenshot = await openTraceAndScreenshot(unit.runId, 'scenario-vendor-failed-rework-trace');
  summary.scenarios.vendorFailedRework = {
    status: 'passed',
    unit: publicUnit(unit),
    run: compactRun(terminal),
    trace: compactTrace(trace),
    assertion: 'Vendor Failed remained Completed + Failed, traversed bounded Rework, selected Nonconforming, and created no system Incident.',
    screenshots: [screenshot]
  };
  await persistSummary();
}

async function runCancellationScenario() {
  const unit = await prepareRunAtStation1('SpawnChildDelay');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  await waitForOperation(unit.runId, fixture.operationTest, 1, 'Running');
  const vendorProcesses = await waitForVendorProcesses(
    processes => hasSpawnChildProcessTree(processes, 'SpawnChildDelay'),
    'vendor parent and child processes');
  await openOperationsDashboard(unit.runId);
  const beforeScreenshot = await recordScreenshot('scenario-cancel-spawn-child-running');
  await harness.click(`active-run-${unit.runId}`);
  await harness.click('production-command-Cancel');
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
    unit: publicUnit(unit),
    run: compactRun(terminal),
    vendorProcessesBeforeCancel: vendorProcesses.map(publicProcessEvidence),
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
    unit: publicUnit(unit),
    run: compactRun(terminal),
    trace: compactTrace(trace),
    incidents: incidents.map(publicIncident),
    screenshots: [screenshot]
  };
  await persistSummary();
}

async function runRecoveryScenario() {
  const unit = await prepareRunAtStation1('SpawnChildDelayRecovery');
  await waitForOperation(unit.runId, fixture.operationPrep, 1, 'Completed');
  await moveUnitBetweenSlots(unit, fixture.station1, fixture.slot1, fixture.station2, fixture.slot2);
  const running = await waitForOperation(unit.runId, fixture.operationTest, 1, 'Running');
  const operationRunId = running.operations.find(operation => (
    operation.operationId === fixture.operationTest && operation.attempt === 1)).operationRunId;
  const vendorProcesses = await waitForVendorProcesses(
    processes => hasSpawnChildProcessTree(processes, 'SpawnChildDelayRecovery'),
    'recovery vendor parent and child');
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
    unit: publicUnit(unit),
    interruptedOperationRunId: operationRunId,
    backendPidTerminated: backend.pid,
    vendorProcessesBeforeCrash: vendorProcesses.map(publicProcessEvidence),
    recoveryRequired: compactRun(recoveryRequired),
    terminal: compactRun(terminal),
    noAutomaticReplay: true,
    recoveryDecisions: terminal.recoveryDecisions.map(publicRecoveryDecision),
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
  harness = createHarness();
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
    rebuiltProjection: publicLineState(lineState),
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
      occurredAtUtc: nextTimestamp()
    }
  }, 201, `register Production Unit ${identityValue}`);
  await expectApi(`/api/production-units/${unit.unitId}/arrivals`, {
    method: 'POST',
    body: {
      projectId: fixture.projectId,
      applicationId: fixture.applicationId,
      projectSnapshotId: fixture.snapshotId,
      packageContentSha256: fixture.entryStationPackageContentSha256,
      stationId: fixture.entryStationId,
      lineId: fixture.lineId,
      stationSystemId: fixture.station1,
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
      productionUnitId: unit.unitId
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
  if (current.status !== 200 || current.body.materialId !== unit.unitId) return null;
  if (current.body.status === 'Running') await slotCommand(unit, stationSystemId, slotId, 'Complete');
  return slotCommand(unit, stationSystemId, slotId, 'Unload', {
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
  const occurredAtUtc = nextTimestamp();
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
        occurredAtUtc
      }
    },
    200,
    `${command} ${unit.identityValue} in ${slotId}`);
  return occurredAtUtc;
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

async function waitForTraceResponse(runId, timeoutMilliseconds = 45_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest;
  while (Date.now() < deadline) {
    latest = await harness.api(`/api/traceability/records/${runId}`);
    if (latest.status === 200) {
      assert(typeof latest.text === 'string',
        `Trace ${runId} did not expose its exact HTTP response bytes.`);
      return latest;
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for Trace ${runId}: ${latest?.text ?? 'no response'}`);
}

async function waitForTrace(runId, timeoutMilliseconds = 45_000) {
  return (await waitForTraceResponse(runId, timeoutMilliseconds)).body;
}

async function waitForMaterialLifecycle(productionUnitId, timeoutMilliseconds = 45_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let latest;
  while (Date.now() < deadline) {
    latest = await harness.api(
      `/api/traceability/production-units/${encodeURIComponent(productionUnitId)}/material-lifecycle`);
    if (latest.status === 200) return latest.body;
    await delay(250);
  }
  throw new Error(
    `Timed out waiting for Product material lifecycle ${productionUnitId}: ${latest?.text ?? 'no response'}`);
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
    '!document.querySelector("[data-testid=\\"trace-search-run\\"]")?.disabled',
    30_000,
    'the Trace search action');
  await harness.click('trace-search-run');
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
  await harness.waitFor(
    'document.querySelector("[data-testid=\\"trace-run-material-evidence\\"]")?.textContent?.includes("Immutable run material evidence")',
    30_000,
    `immutable material evidence for ${runId}`);
  await harness.waitFor(
    'document.querySelector("[data-testid=\\"trace-product-material-lifecycle\\"]")?.textContent?.includes("Observed through")',
    30_000,
    `latest Product material lifecycle for ${runId}`);
  await harness.waitFor(
    'Boolean(document.querySelector("[data-testid=\\"trace-product-post-run-location-transition\\"]")'
      + ' && document.querySelector("[data-testid=\\"trace-product-post-run-slot-transition\\"]"))',
    30_000,
    `post-run final unload material evidence for ${runId}`);
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

async function downloadAndVerifyArtifacts(artifacts) {
  const config = await harness.evaluate('window.openlineopsDesktop.getConfig()');
  assert(config?.apiBaseUrl, 'Desktop API base URL is unavailable for artifact verification.');
  assert(config?.apiAccessToken, 'Desktop API credential is unavailable for artifact verification.');
  const downloads = [];
  for (const artifact of artifacts) {
    assert(Number.isSafeInteger(artifact.sizeBytes) && artifact.sizeBytes >= 0,
      `Artifact ${artifact.name} has no canonical byte count.`);
    assertSha256(artifact.sha256, artifact.name);
    const encodedKey = artifact.storageKey.split('/').map(encodeURIComponent).join('/');
    const response = await fetch(
      new URL(`/api/traceability/artifacts/${encodedKey}`, config.apiBaseUrl),
      {
        method: 'GET',
        headers: { authorization: `Bearer ${config.apiAccessToken}` },
        redirect: 'error'
      });
    assert(response.status === 200,
      `Trace artifact ${artifact.name} download returned HTTP ${response.status}.`);
    const bytes = Buffer.from(await response.arrayBuffer());
    assert(bytes.byteLength === artifact.sizeBytes,
      `Trace artifact ${artifact.name} byte count changed during download.`);
    const sha256 = createHash('sha256').update(bytes).digest('hex');
    assert(sha256 === artifact.sha256,
      `Trace artifact ${artifact.name} SHA-256 changed during download.`);
    downloads.push({
      name: artifact.name,
      storageKey: artifact.storageKey,
      mediaType: response.headers.get('content-type'),
      sizeBytes: bytes.byteLength,
      sha256
    });
  }

  for (const required of ['measurements.csv', 'inspection.png', 'report.pdf', 'stdout.log', 'stderr.log']) {
    assert(downloads.some(artifact => artifact.name === required),
      `Trace API download verification did not include ${required}.`);
  }
  return downloads;
}

async function saveArtifactThroughDesktopIpc(artifact) {
  assert(artifact, 'No real Trace artifact was selected for the desktop save boundary.');
  assert(Number.isSafeInteger(artifact.sizeBytes) && artifact.sizeBytes >= 0,
    `Artifact ${artifact.name} has no canonical byte count.`);
  assertSha256(artifact.sha256, artifact.name);
  const result = await harness.evaluate(
    `window.openlineopsDesktop.saveTraceArtifact(${JSON.stringify({
      storageKey: artifact.storageKey,
      fileName: artifact.name,
      expectedSizeBytes: artifact.sizeBytes,
      expectedSha256: artifact.sha256
    })})`);
  assert(result?.canceled === false, 'Desktop Trace artifact save was canceled.');
  assert(typeof result.path === 'string' && path.isAbsolute(result.path),
    'Desktop Trace artifact save returned no absolute destination.');
  assert(path.dirname(path.normalize(result.path)) === path.normalize(traceArtifactSaveRoot),
    'Desktop Trace artifact save escaped the host-controlled E2E destination.');

  const bytes = await fs.readFile(result.path);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  assert(bytes.byteLength === artifact.sizeBytes,
    'Desktop Trace artifact save changed the immutable byte count.');
  assert(sha256 === artifact.sha256,
    'Desktop Trace artifact save changed the immutable SHA-256.');
  assert(result.sizeBytes === bytes.byteLength && result.sha256 === sha256,
    'Desktop Trace artifact IPC result did not describe the saved evidence exactly.');
  const remainingTemporaryFiles = (await fs.readdir(traceArtifactSaveRoot))
    .filter(name => name.startsWith('.openlineops-trace-') && name.endsWith('.tmp'));
  assert(remainingTemporaryFiles.length === 0,
    'Desktop Trace artifact save left a sibling temporary file behind.');
  return {
    name: artifact.name,
    storageKeySha256: createHash('sha256')
      .update(artifact.storageKey, 'utf8')
      .digest('hex'),
    path: toEvidenceRelativePath(result.path),
    sizeBytes: bytes.byteLength,
    sha256,
    invokedThroughPreloadIpc: true,
    atomicTemporaryFileRemoved: true
  };
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

function hasSpawnChildProcessTree(processes, mode) {
  const parent = processes.find(process => (
    process.processName?.toLowerCase() === 'openlineops.vendortesthelper.exe'
      && process.commandLine?.includes(`--mode ${mode}`)));
  return Boolean(parent && processes.some(process => process.parentProcessId === parent.processId));
}

async function listVendorProcesses() {
  const command = "$items = @(Get-CimInstance Win32_Process -Filter \"Name = 'OpenLineOps.VendorTestHelper.exe' OR Name = 'dotnet.exe'\" | "
    + 'Select-Object @{Name=\'processId\';Expression={$_.ProcessId}},'
    + "@{Name='parentProcessId';Expression={$_.ParentProcessId}},"
    + "@{Name='processName';Expression={$_.Name}},"
    + "@{Name='commandLine';Expression={$_.CommandLine}}); "
    + 'if ($items.Count -eq 0) { Write-Output \'[]\' } else { $items | ConvertTo-Json -Compress -Depth 3 }';
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', command],
    { windowsHide: true, maxBuffer: 1024 * 1024 });
  const parsed = JSON.parse(stdout.trim() || '[]');
  const projectNeedle = projectPath.toLowerCase();
  return (Array.isArray(parsed) ? parsed : [parsed])
    .map(item => ({
      processId: Number(item.processId),
      parentProcessId: Number(item.parentProcessId),
      processName: item.processName ?? null,
      commandLine: item.commandLine ?? null
    }))
    .filter(item => item.commandLine?.toLowerCase().includes(projectNeedle));
}

function compactRun(run) {
  return {
    productionRunId: run.productionRunId,
    productionUnitId: run.productionUnitId,
    identity: publicProductionIdentity(run.productionUnitIdentity),
    executionStatus: run.executionStatus,
    judgement: run.judgement,
    disposition: run.disposition,
    controlState: run.controlState,
    failureCode: run.failureCode,
    operationCount: run.operations.length,
    operations: run.operations.map(publicOperation),
    routeDecisions: run.routeDecisions.map(publicRouteDecision),
    recoveryDecisions: run.recoveryDecisions.map(publicRecoveryDecision),
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
    operations: (trace.operations ?? []).map(publicTraceOperation),
    routeDecisions: (trace.routeDecisions ?? []).map(publicRouteDecision),
    genealogyCount: Array.isArray(trace.genealogy) ? trace.genealogy.length : 0,
    materialLocationTransitions: (trace.materialLocationTransitions ?? []).map(publicLocationTransition),
    slotOccupancyTransitions: (trace.slotOccupancyTransitions ?? []).map(publicSlotTransition),
    dispositionTransitions: (trace.dispositionTransitions ?? []).map(publicDispositionTransition),
    auditEntries: (trace.auditEntries ?? []).map(publicAuditEntry)
  };
}

function compactMaterialLifecycle(lifecycle) {
  return {
    productionUnitId: lifecycle.productionUnitId,
    currentDisposition: lifecycle.currentDisposition,
    currentLocation: publicMaterialLocation(lifecycle.currentLocation),
    currentCarrierLocation: publicMaterialLocation(lifecycle.currentCarrierLocation),
    registeredAtUtc: lifecycle.registeredAtUtc,
    observedThroughUtc: lifecycle.observedThroughUtc,
    genealogyCount: Array.isArray(lifecycle.genealogy) ? lifecycle.genealogy.length : 0,
    materialLocationTransitions: (lifecycle.materialLocationTransitions ?? []).map(publicLocationTransition),
    slotOccupancyTransitions: (lifecycle.slotOccupancyTransitions ?? []).map(publicSlotTransition),
    dispositionTransitions: (lifecycle.dispositionTransitions ?? []).map(publicDispositionTransition)
  };
}

function publicUnit(unit) {
  return {
    unitId: unit.unitId,
    runId: unit.runId,
    identityValue: canonicalE2eMode(unit.identityValue),
    runSubmitted: unit.runSubmitted === true
  };
}

function publicProductionIdentity(identity) {
  return identity ? {
    modelId: identity.modelId,
    inputKey: identity.inputKey,
    value: canonicalE2eMode(identity.value)
  } : null;
}

function canonicalE2eMode(value) {
  const allowed = new Set([
    'Delay',
    'Passed',
    'Failed',
    'Crash',
    'SpawnChildDelay',
    'SpawnChildDelayRecovery'
  ]);
  assert(allowed.has(value), 'A public Production Unit identity is not an allowlisted E2E mode.');
  return value;
}

function publicOperation(operation) {
  return {
    operationRunId: operation.operationRunId,
    operationId: operation.operationId,
    attempt: operation.attempt,
    stationSystemId: operation.stationSystemId,
    executionStatus: operation.executionStatus,
    judgement: operation.judgement,
    isTerminal: operation.isTerminal ?? null,
    startedAtUtc: operation.startedAtUtc ?? null,
    completedAtUtc: operation.completedAtUtc ?? null,
    failureCode: operation.failureCode ?? null,
    completedStepCount: operation.completedStepCount ?? 0,
    commandCount: operation.commandCount ?? 0,
    incidentCount: operation.incidentCount ?? 0,
    resources: (operation.resources ?? []).map(publicResource),
    outputs: (operation.outputs ?? []).map(publicTypedOutput)
  };
}

function publicTraceOperation(operation) {
  return {
    ...publicOperation(operation),
    runtimeSessionStatus: operation.runtimeSessionStatus ?? null,
    artifactCount: (operation.artifacts ?? []).length,
    artifacts: (operation.artifacts ?? []).map(publicArtifactMetadata),
    incidents: (operation.incidents ?? []).map(publicIncident),
    commandStatuses: (operation.commands ?? []).map(command => ({
      runtimeCommandId: command.runtimeCommandId,
      actionId: command.actionId,
      commandName: command.commandName,
      executionStatus: command.executionStatus,
      resultJudgement: command.resultJudgement,
      completedAtUtc: command.completedAtUtc ?? null
    }))
  };
}

function publicResource(resource) {
  return {
    kind: resource.kind,
    resourceId: resource.resourceId,
    status: resource.status ?? null,
    fencingToken: resource.fencingToken ?? null
  };
}

function publicTypedOutput(output) {
  assert(output && typeof output === 'object', 'A typed output is not an object.');
  assert(
    typeof output.key === 'string' && output.key.length > 0,
    'A typed output key is missing.');

  const isRuntimeOutput = typeof output.kind === 'string'
    && typeof output.canonicalValue === 'string'
    && output.valueKind === undefined
    && output.canonicalJson === undefined;
  const isTraceOutput = typeof output.valueKind === 'string'
    && typeof output.canonicalJson === 'string'
    && output.kind === undefined
    && output.canonicalValue === undefined;
  assert(
    isRuntimeOutput !== isTraceOutput,
    `Typed output '${output.key}' must match exactly one supported API representation.`);

  const kind = isRuntimeOutput ? output.kind : output.valueKind;
  assert(
    new Set(['Text', 'Boolean', 'WholeNumber', 'FixedPoint', 'DateTimeUtc']).has(kind),
    `Typed output '${output.key}' has an unsupported value kind.`);
  const value = isRuntimeOutput ? output.canonicalValue : output.canonicalJson;

  return {
    key: output.key,
    kind,
    valueSha256: createHash('sha256').update(String(value), 'utf8').digest('hex')
  };
}

function publicRouteDecision(decision) {
  return {
    sourceOperationRunId: decision.sourceOperationRunId,
    transitionId: decision.transitionId,
    targetOperationId: decision.targetOperationId ?? null,
    terminalDisposition: decision.terminalDisposition ?? null,
    sourceJudgement: decision.sourceJudgement,
    traversal: decision.traversal,
    decidedAtUtc: decision.decidedAtUtc
  };
}

function publicRecoveryDecision(decision) {
  return {
    decisionId: decision.decisionId ?? null,
    kind: decision.kind,
    operationRunId: decision.operationRunId ?? null,
    operationId: decision.operationId ?? null,
    observedJudgement: decision.observedJudgement ?? null,
    observedOutputCount: decision.observedOutputs
      ? Object.keys(decision.observedOutputs).length
      : 0,
    decidedAtUtc: decision.decidedAtUtc
  };
}

function publicIncident(incident) {
  return {
    runtimeIncidentId: incident.runtimeIncidentId,
    severity: incident.severity,
    code: incident.code,
    occurredAtUtc: incident.occurredAtUtc
  };
}

function publicAuditEntry(entry) {
  return {
    auditEntryId: entry.auditEntryId,
    action: entry.action,
    occurredAtUtc: entry.occurredAtUtc
  };
}

function publicArtifactMetadata(artifact) {
  return {
    name: artifact.name,
    kind: artifact.kind ?? null,
    storageKeySha256: artifact.storageKey
      ? createHash('sha256').update(artifact.storageKey, 'utf8').digest('hex')
      : null,
    mediaType: artifact.mediaType ?? null,
    sizeBytes: artifact.sizeBytes,
    sha256: artifact.sha256
  };
}

function publicMaterialLocation(location) {
  return location ? {
    kind: location.kind,
    lineId: location.lineId ?? null,
    stationSystemId: location.stationSystemId ?? null,
    slotId: location.slotId ?? null,
    carrierId: location.carrierId ?? null,
    carrierPositionId: location.carrierPositionId ?? null
  } : null;
}

function publicLocationTransition(transition) {
  return {
    evidenceId: transition.evidenceId,
    productionRunId: transition.productionRunId,
    materialKind: transition.materialKind,
    materialId: transition.materialId,
    source: publicMaterialLocation(transition.source),
    destination: publicMaterialLocation(transition.destination),
    occurredAtUtc: transition.occurredAtUtc
  };
}

function publicSlotTransition(transition) {
  return {
    evidenceId: transition.evidenceId,
    productionRunId: transition.productionRunId,
    lineId: transition.lineId,
    stationSystemId: transition.stationSystemId,
    slotId: transition.slotId,
    materialKind: transition.materialKind,
    materialId: transition.materialId,
    previousStatus: transition.previousStatus,
    currentStatus: transition.currentStatus,
    occurredAtUtc: transition.occurredAtUtc
  };
}

function publicDispositionTransition(transition) {
  return {
    evidenceId: transition.evidenceId,
    productionUnitId: transition.productionUnitId,
    productionRunId: transition.productionRunId,
    previousDisposition: transition.previousDisposition,
    currentDisposition: transition.currentDisposition,
    occurredAtUtc: transition.occurredAtUtc
  };
}

function publicProcessEvidence(item) {
  assert(Number.isInteger(item.processId) && item.processId > 0,
    'Vendor process evidence has no positive PID.');
  assert(Number.isInteger(item.parentProcessId) && item.parentProcessId >= 0,
    'Vendor process evidence has no canonical parent PID.');
  const imageName = String(item.processName ?? '');
  assert(imageName === 'OpenLineOps.VendorTestHelper.exe' || imageName === 'dotnet.exe',
    'Vendor process evidence has a non-allowlisted image name.');
  return {
    processId: item.processId,
    parentProcessId: item.parentProcessId,
    imageName
  };
}

function publicLineState(state) {
  return {
    productionLineDefinitionId: state.productionLineDefinitionId,
    generatedAtUtc: state.generatedAtUtc,
    activeRunCount: state.activeRunCount,
    activeRuns: (state.activeRuns ?? []).map(run => ({
      productionRunId: run.productionRunId,
      productionUnitId: run.productionUnitId,
      executionStatus: run.executionStatus,
      judgement: run.judgement,
      disposition: run.disposition,
      controlState: run.controlState,
      isTerminal: run.isTerminal,
      incidentCount: run.incidentCount
    })),
    stations: (state.stations ?? []).map(station => ({
      stationSystemId: station.stationSystemId,
      status: station.status,
      stationId: station.stationId,
      presenceState: station.agentPresenceState ?? null,
      presenceHealth: station.agentPresenceHealth ?? null,
      queueCount: (station.queue ?? []).length,
      activeOperations: (station.activeOperations ?? []).map(operation => ({
        productionRunId: operation.productionRunId,
        productionUnitId: operation.productionUnitId,
        operationRunId: operation.operationRunId,
        operationId: operation.operationId,
        executionStatus: operation.executionStatus,
        judgement: operation.judgement,
        resources: (operation.resources ?? []).map(publicResource)
      }))
    })),
    slots: (state.slots ?? []).map(slot => ({
      stationSystemId: slot.stationSystemId,
      slotId: slot.slotId,
      status: slot.status,
      materialKind: slot.materialKind,
      materialId: slot.materialId,
      lastTransitionAtUtc: slot.lastTransitionAtUtc
    })),
    carrierCount: (state.carriers ?? []).length
  };
}

async function recordScreenshot(name) {
  const filePath = path.join(screenshotRoot, `${name}.png`);
  await harness.screenshot(filePath);
  return {
    name,
    path: toEvidenceRelativePath(filePath),
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

function toEvidenceRelativePath(filePath) {
  const resolvedRoot = path.resolve(artifactRoot);
  const resolvedFile = path.resolve(filePath);
  const relativePath = path.relative(resolvedRoot, resolvedFile);
  assert(relativePath.length > 0
    && !path.isAbsolute(relativePath)
    && relativePath !== '..'
    && !relativePath.startsWith(`..${path.sep}`),
  `Evidence file escaped the public evidence root: ${filePath}`);
  return relativePath.split(path.sep).join('/');
}

async function exportPublicEvidenceFile(sourcePath, relativePath) {
  await assertFile(sourcePath, `Public evidence source ${relativePath}`);
  assert(typeof relativePath === 'string'
    && relativePath.length > 0
    && relativePath === relativePath.split('\\').join('/')
    && !path.posix.isAbsolute(relativePath)
    && relativePath.split('/').every(segment => segment.length > 0 && segment !== '.' && segment !== '..'),
  `Public evidence path is not canonical: ${relativePath}`);
  const destinationPath = path.resolve(artifactRoot, ...relativePath.split('/'));
  assert(toEvidenceRelativePath(destinationPath) === relativePath,
    `Public evidence destination is not canonical: ${relativePath}`);
  await fs.mkdir(path.dirname(destinationPath), { recursive: true });
  await fs.copyFile(sourcePath, destinationPath);
  const sourceSha256 = await sha256File(sourcePath);
  const destinationSha256 = await sha256File(destinationPath);
  assert(destinationSha256 === sourceSha256,
    `Exported public evidence changed during copy: ${relativePath}`);
  const sourceSize = (await fs.stat(sourcePath)).size;
  const destinationSize = (await fs.stat(destinationPath)).size;
  assert(destinationSize === sourceSize,
    `Exported public evidence size changed during copy: ${relativePath}`);
  return { relativePath, sizeBytes: destinationSize, sha256: destinationSha256 };
}

async function writeEvidenceManifest() {
  await assertFile(summaryPath, 'Production closure summary');
  const files = await listAllEvidenceFiles(artifactRoot);
  const entries = [];
  for (const filePath of files) {
    if (path.resolve(filePath) === path.resolve(evidenceManifestPath)) continue;
    const relativePath = toEvidenceRelativePath(filePath);
    assert(isAllowedPublicEvidencePath(relativePath),
      `Production closure attempted to publish a non-allowlisted file: ${relativePath}`);
    const stat = await fs.stat(filePath);
    entries.push({
      relativePath,
      sizeBytes: stat.size,
      sha256: await sha256File(filePath)
    });
  }
  entries.sort((left, right) => left.relativePath < right.relativePath
    ? -1
    : left.relativePath > right.relativePath ? 1 : 0);
  assert(entries.some(entry => entry.relativePath === 'summary.json'),
    'Production closure evidence manifest has no summary.');
  const document = {
    schema: 'openlineops.production-closure-evidence-manifest',
    schemaVersion: 1,
    generatedAtUtc: new Date().toISOString(),
    files: entries
  };
  const temporaryPath = `${evidenceManifestPath}.tmp`;
  await fs.writeFile(temporaryPath, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
  await fs.rename(temporaryPath, evidenceManifestPath);
}

async function writePrivateHandoff() {
  if (privateHandoffPath === null) return;
  await Promise.all([
    assertFile(summaryPath, 'Production closure summary'),
    assertFile(evidenceManifestPath, 'Production closure evidence manifest'),
    fs.stat(projectPath).then(stat => {
      assert(stat.isDirectory(), `Private production project is not a directory: ${projectPath}`);
    })
  ]);
  assert(summary.status === 'passed' && summary.completedAtUtc,
    'A private production closure handoff can only be written after a passed run.');

  const handoffDirectory = path.dirname(privateHandoffPath);
  await fs.mkdir(handoffDirectory, { recursive: true });
  await assertNoReparsePointsForRecursiveDelete(
    handoffDirectory,
    'private production closure handoff directory');
  const realHandoffDirectory = await fs.realpath(handoffDirectory);
  assert(realHandoffDirectory.toLowerCase() === handoffDirectory.toLowerCase(),
    `Private handoff directory cannot traverse a symbolic link or junction: ${handoffDirectory}`);
  const existing = await fs.lstat(privateHandoffPath).catch(error => {
    if (error?.code === 'ENOENT') return null;
    throw error;
  });
  assert(existing === null,
    `Private production closure handoff already exists and will not be overwritten: ${privateHandoffPath}`);

  const [summaryStat, manifestStat] = await Promise.all([
    fs.stat(summaryPath),
    fs.stat(evidenceManifestPath)
  ]);
  const immutableRunTrace = summary.scenarios.vendorPassed?.immutableRunTrace;
  assertImmutableRunTraceEvidence(immutableRunTrace);
  const document = {
    schema: 'openlineops.production-closure-private-handoff',
    schemaVersion: 1,
    createdAtUtc: new Date().toISOString(),
    privateExecutionRoot,
    sourceProjectPath,
    projectPath,
    summaryPath,
    summarySizeBytes: summaryStat.size,
    summarySha256: await sha256File(summaryPath),
    evidenceManifestPath,
    evidenceManifestSizeBytes: manifestStat.size,
    evidenceManifestSha256: await sha256File(evidenceManifestPath),
    projectId: summary.projectId,
    applicationId: summary.applicationId,
    topologyId: summary.topologyId,
    productionLineDefinitionId: summary.productionLineDefinitionId,
    projectSnapshotId: summary.projectSnapshotId,
    immutableRunTrace
  };
  for (const identity of [
    document.projectId,
    document.applicationId,
    document.topologyId,
    document.productionLineDefinitionId,
    document.projectSnapshotId
  ]) {
    assert(typeof identity === 'string'
      && identity.length > 0
      && identity.trim() === identity,
    'Private production closure handoff identity is missing or non-canonical.');
  }

  const temporaryPath = path.join(
    handoffDirectory,
    `.production-closure-handoff.${process.pid}.${randomUUID()}.tmp`);
  try {
    const handle = await fs.open(temporaryPath, 'wx', 0o600);
    try {
      await handle.writeFile(`${JSON.stringify(document, null, 2)}\n`, 'utf8');
      await handle.sync();
    } finally {
      await handle.close();
    }
    await fs.rename(temporaryPath, privateHandoffPath);
  } finally {
    await fs.rm(temporaryPath, { force: true }).catch(() => undefined);
  }
  preservePrivateExecutionRoot = true;
  console.log(`OpenLineOps private production closure handoff ready: ${privateHandoffPath}`);
}

function isAllowedPublicEvidencePath(relativePath) {
  return relativePath === 'summary.json'
    || /^screenshots\/[A-Za-z0-9._-]+\.png$/u.test(relativePath)
    || /^verified-trace-artifact-saves\/[A-Za-z0-9._-]+$/u.test(relativePath)
    || relativePath === 'public-release/frozen-manifest.json'
    || relativePath === 'public-release/release-signing-public.pem'
    || /^public-release\/station-packages\/[0-9a-f]{64}\.olopkg$/u.test(relativePath)
    || /^public-release\/deployment-catalog\/[0-9a-f]{64}\.json$/u.test(relativePath);
}

async function listAllEvidenceFiles(root) {
  const result = [];
  const visit = async directory => {
    const entries = await fs.readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      assert(!entry.isSymbolicLink(),
        `Production closure evidence cannot contain a symbolic link: ${path.join(directory, entry.name)}`);
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (entry.isFile()) result.push(fullPath);
      else throw new Error(`Production closure evidence contains an unsupported filesystem entry: ${fullPath}`);
    }
  };
  await visit(root);
  return result.sort();
}

async function capturePackagedBinaryIdentity() {
  return {
    desktopExecutable: await captureFileIdentity(
      packagedExecutable,
      'packaged-desktop/OpenLineOps.exe'),
    runtimeApiExecutable: await captureFileIdentity(
      packagedRuntimeApiExecutable,
      'packaged-desktop/runtime/api/OpenLineOps.Api.exe')
  };
}

async function captureFileIdentity(filePath, publicPath) {
  const stat = await fs.stat(filePath);
  return {
    path: publicPath,
    sha256: await sha256File(filePath),
    sizeBytes: stat.size,
    modifiedAtUtc: stat.mtime.toISOString()
  };
}

function byteIdentity(bytes) {
  return {
    sha256: createHash('sha256').update(bytes).digest('hex'),
    sizeBytes: bytes.length
  };
}

function assertImmutableRunTraceEvidence(evidence) {
  assert(evidence && typeof evidence === 'object',
    'Immutable Run Trace evidence is required.');
  for (const phase of ['before', 'after']) {
    assertSha256(evidence[phase]?.sha256, `immutable Run Trace ${phase}`);
    assert(Number.isSafeInteger(evidence[phase]?.sizeBytes)
      && evidence[phase].sizeBytes > 0,
    `Immutable Run Trace ${phase} size must be a positive safe integer.`);
  }
  assert(evidence.unchanged === true
    && evidence.before.sha256 === evidence.after.sha256
    && evidence.before.sizeBytes === evidence.after.sizeBytes,
  'Immutable Run Trace evidence must prove identical before/after bytes.');
  assert(typeof evidence.terminalCompletedAtUtc === 'string'
    && Number.isFinite(Date.parse(evidence.terminalCompletedAtUtc)),
  'Immutable Run Trace terminal completion timestamp is invalid.');
  assert(typeof evidence.unloadAtUtc === 'string'
    && Number.isFinite(Date.parse(evidence.unloadAtUtc))
    && Date.parse(evidence.unloadAtUtc) > Date.parse(evidence.terminalCompletedAtUtc),
  'Immutable Run Trace unload timestamp must follow terminal completion.');
}

function assertPackagedBinaryIdentityUnchanged(before, after) {
  for (const key of ['desktopExecutable', 'runtimeApiExecutable']) {
    assert(before?.[key]?.path === after?.[key]?.path, `${key} path changed during packaged E2E.`);
    assert(before?.[key]?.sha256 === after?.[key]?.sha256, `${key} SHA-256 changed during packaged E2E.`);
    assert(before?.[key]?.sizeBytes === after?.[key]?.sizeBytes, `${key} size changed during packaged E2E.`);
    assert(before?.[key]?.modifiedAtUtc === after?.[key]?.modifiedAtUtc, `${key} mtime changed during packaged E2E.`);
  }
}

async function captureApplicationTreeIdentity(applicationRoot) {
  const rootStat = await fs.lstat(applicationRoot);
  assert(rootStat.isDirectory() && !rootStat.isSymbolicLink(),
    'Application inventory root must be a real directory.');
  const entries = [];
  const visit = async (directory, relativeDirectory) => {
    const children = await fs.readdir(directory, { withFileTypes: true });
    children.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
    for (const child of children) {
      const absolutePath = path.join(directory, child.name);
      const relativePath = relativeDirectory.length === 0
        ? child.name
        : `${relativeDirectory}/${child.name}`;
      const stat = await fs.lstat(absolutePath);
      assert(!stat.isSymbolicLink(),
        `Application copy cannot contain a symbolic link or junction: ${relativePath}`);
      if (stat.isDirectory()) {
        await visit(absolutePath, relativePath);
      }
      else if (stat.isFile()) {
        entries.push({
          relativePath,
          sizeBytes: stat.size,
          sha256: await sha256File(absolutePath)
        });
      }
      else {
        throw new Error(`Application copy contains an unsupported entry: ${relativePath}`);
      }
    }
  };
  await visit(applicationRoot, '');
  entries.sort((left, right) => left.relativePath < right.relativePath
    ? -1
    : left.relativePath > right.relativePath ? 1 : 0);
  const treeHash = createHash('sha256');
  for (const entry of entries) {
    treeHash.update(entry.relativePath, 'utf8');
    treeHash.update('\0', 'utf8');
    treeHash.update(String(entry.sizeBytes), 'utf8');
    treeHash.update('\0', 'utf8');
    treeHash.update(entry.sha256, 'utf8');
    treeHash.update('\n', 'utf8');
  }
  return {
    fileCount: entries.length,
    totalSizeBytes: entries.reduce((total, entry) => total + entry.sizeBytes, 0),
    treeSha256: treeHash.digest('hex'),
    entries
  };
}

function assertSameApplicationTree(expected, actual, label) {
  assert(expected.fileCount === actual.fileCount,
    `${label} file count differs from the source Application.`);
  assert(expected.totalSizeBytes === actual.totalSizeBytes,
    `${label} byte count differs from the source Application.`);
  assert(expected.treeSha256 === actual.treeSha256,
    `${label} tree SHA-256 differs from the source Application.`);
  assert(JSON.stringify(expected.entries) === JSON.stringify(actual.entries),
    `${label} file inventory differs from the source Application.`);
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
  const publicSummary = sanitizePublicEvidenceValue(summary);
  await fs.writeFile(temporaryPath, `${JSON.stringify(publicSummary, null, 2)}\n`, 'utf8');
  await fs.rename(temporaryPath, summaryPath);
}

function sanitizePublicEvidenceValue(value) {
  if (typeof value === 'string') return sanitizePublicEvidenceString(value);
  if (Array.isArray(value)) return value.map(item => sanitizePublicEvidenceValue(item));
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => (
      [key, sanitizePublicEvidenceValue(item)])));
  }
  return value;
}

function sanitizePublicEvidenceString(value) {
  let sanitized = value;
  for (const privatePath of [privateExecutionRoot, userDataDirectory, projectPath]) {
    if (typeof privatePath !== 'string' || privatePath.length === 0) continue;
    sanitized = sanitized.replace(
      new RegExp(escapeRegularExpression(privatePath), 'giu'),
      '<private-runtime>');
  }
  sanitized = sanitized.replace(
    new RegExp(escapeRegularExpression(repoRoot), 'giu'),
    '<repository>');
  return sanitized
    .replace(/-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----/giu,
      '<private-key-redacted>')
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/giu, 'Bearer <redacted>')
    .replace(/\b((?:amqp|amqps|http|https):\/\/)[^\s/:@]+:[^\s/@]+@/giu, '$1<redacted>@')
    .replace(/("(?:apiAccessToken|artifactUploadBearerToken|bearerToken|authorization|clientSecret|password)"\s*:\s*)"[^"]*"/giu,
      '$1"<redacted>"')
    .replace(/\b(OPENLINEOPS_[A-Z0-9_]*(?:TOKEN|PASSWORD|SECRET)\s*[=:]\s*)[^\s;]+/giu,
      '$1<redacted>');
}

function escapeRegularExpression(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}

main()
  .catch(async error => {
    summary.status = 'failed';
    summary.completedAtUtc = new Date().toISOString();
    const failureText = error instanceof Error
      ? `${error.name}\n${error.message}\n${error.stack ?? ''}`
      : String(error);
    summary.failure = {
      code: 'ProductionClosureFailed',
      detailSha256: createHash('sha256').update(failureText, 'utf8').digest('hex')
    };
    summary.diagnostics = null;
    await persistSummary().catch(() => undefined);
    console.error(error);
    console.error(`Failure evidence: ${summaryPath}`);
    process.exitCode = 1;
  })
  .finally(async () => {
    await harness?.close().catch(() => undefined);
    try {
      if (!evidenceManifestWritten) {
        await writeEvidenceManifest();
        evidenceManifestWritten = true;
      }
    } catch (error) {
      console.error(`Production closure evidence manifest failed: ${error instanceof Error ? error.stack : String(error)}`);
      process.exitCode = 1;
    }
    if (summary.status === 'passed'
        && evidenceManifestWritten
        && (process.exitCode === undefined || process.exitCode === 0)) {
      try {
        await writePrivateHandoff();
      } catch (error) {
        console.error(`Private production closure handoff failed: ${error instanceof Error ? error.stack : String(error)}`);
        process.exitCode = 1;
      }
    }
    if (!preservePrivateExecutionRoot) {
      try {
        await assertNoReparsePointsForRecursiveDelete(
          privateExecutionRoot,
          'private production closure execution root');
        await fs.rm(privateExecutionRoot, {
          recursive: true,
          force: true,
          maxRetries: 10,
          retryDelay: 250
        });
      } catch (error) {
        console.error(`Private production closure cleanup failed outside the publishable root: ${String(error)}`);
        process.exitCode = 1;
      }
    }
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
