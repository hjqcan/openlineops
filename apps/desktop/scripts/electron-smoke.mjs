import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import electronPath from 'electron';
import { buildSampleExtensionArchive } from './build-sample-extension-archive.mjs';
import {
  createWindowsPowerShellHost,
  windowsSystemExecutablePath
} from './windows-powershell-host.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');
const packagedMode = process.argv.includes('--packaged');
const packagedExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'OpenLineOps.exe');
const packagedScriptWorkerExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'resources',
  'app',
  'runtime',
  'script-worker',
  'OpenLineOps.ScriptWorker.exe');
const packagedPluginHostExecutable = path.join(
  desktopRoot,
  'release',
  'desktop',
  'win-unpacked',
  'resources',
  'app',
  'runtime',
  'plugin-host',
  'OpenLineOps.PluginHost.exe');
const viteCliPath = path.join(desktopRoot, 'node_modules', 'vite', 'bin', 'vite.js');
const smokeScreenshotDirectory = process.env.OPENLINEOPS_SMOKE_SCREENSHOT_DIR?.trim() ?? '';

const childLogs = [];
const cdpEvents = [];
const maxLogLines = 160;
const maxCdpEvents = 80;
let previewProcess;
let electronProcess;
let cdp;
let activeProjectApplicationScope;
let apiSquatterServer;
let apiSquatterBaseUrl;
const apiSquatterAuthorizationHeaders = [];
const smokeProjectDirectories = [];
let smokeUserDataDirectory;
let sampleExtensionArchive;

async function main() {
  assertNodeRuntime();

  const previewPort = packagedMode ? null : await getFreePort();
  const cdpPort = await getFreePort();
  const previewUrl = previewPort === null ? null : `http://127.0.0.1:${previewPort}`;
  const rendererNonce = randomBytes(32).toString('base64url');
  let apiBaseUrl = 'unavailable';
  const physicalTempRoot = await fs.realpath(os.tmpdir());
  smokeUserDataDirectory = await fs.mkdtemp(
    path.join(physicalTempRoot, 'openlineops-desktop-smoke-'));
  if (packagedMode) {
    await seedIncompatiblePackagedRuntimeState();
  }
  sampleExtensionArchive = await buildSampleExtensionArchive(repoRoot, {
    buildDevelopmentHost: !packagedMode
  });
  ({ server: apiSquatterServer, baseUrl: apiSquatterBaseUrl } = await startApiSquatter());

  if (previewUrl) {
    previewProcess = spawnLogged(
      process.execPath,
      [viteCliPath, 'preview', '--host', '127.0.0.1', '--port', String(previewPort)],
      {
        cwd: desktopRoot,
        env: {
          ...process.env,
          OPENLINEOPS_RENDERER_NONCE: rendererNonce,
          OPENLINEOPS_RENDERER_PORT: String(previewPort)
        }
      },
      'vite');

    await waitForHttp(previewUrl, 30000, 'Vite preview');
  } else {
    const executableStat = await fs.stat(packagedExecutable).catch(() => null);
    if (!executableStat?.isFile()) {
      throw new Error(`Packaged OpenLineOps executable was not found: ${packagedExecutable}`);
    }
    const scriptWorkerStat = await fs.stat(packagedScriptWorkerExecutable).catch(() => null);
    if (!scriptWorkerStat?.isFile()) {
      throw new Error(
        `Packaged process-isolated Python worker was not found: ${packagedScriptWorkerExecutable}`);
    }
    const pluginHostStat = await fs.stat(packagedPluginHostExecutable).catch(() => null);
    if (!pluginHostStat?.isFile()) {
      throw new Error(
        `Packaged process-isolated plugin host was not found: ${packagedPluginHostExecutable}`);
    }
  }

  const electronLaunchStartedAt = Date.now();
  electronProcess = spawnLogged(
    packagedMode ? packagedExecutable : electronPath,
    packagedMode
      ? packagedElectronArguments(cdpPort)
      : [
          `--remote-debugging-port=${cdpPort}`,
          '--disable-gpu',
          `--user-data-dir=${smokeUserDataDirectory}`,
          desktopRoot
        ],
    {
      cwd: packagedMode ? path.dirname(packagedExecutable) : desktopRoot,
      env: packagedMode
        ? packagedElectronEnvironment(rendererNonce)
        : {
            ...process.env,
            ASPNETCORE_ENVIRONMENT: 'Development',
            OPENLINEOPS_API_BASE_URL: apiSquatterBaseUrl,
            OPENLINEOPS_RENDERER_NONCE: rendererNonce,
            OPENLINEOPS_E2E_ALLOW_EXTENSION_DIALOG_BYPASS: '1',
            OPENLINEOPS_E2E_EXTENSION_ARCHIVE_PATH: sampleExtensionArchive.archivePath,
            OPENLINEOPS_RENDERER_PORT: String(previewPort),
            OPENLINEOPS_REPO_ROOT: repoRoot,
            VITE_DEV_SERVER_URL: previewUrl,
            OpenLineOps__Desktop__AllowedOrigins__0: previewUrl,
            OpenLineOps__Desktop__AllowedOrigins__1: previewUrl.replace('127.0.0.1', 'localhost')
          }
    },
    'electron');

  const target = await waitForCdpTarget(
    cdpPort,
    previewUrl,
    packagedMode ? 90000 : 30000);
  if (packagedMode) {
    console.log(`Packaged Electron renderer target ready in ${Date.now() - electronLaunchStartedAt} ms.`);
  }
  cdp = await CdpClient.connect(target.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');
  await cdp.send('Page.enable');

  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"automation-ide-shell\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"project-workspace-panel\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"start-create-project\\"]"))'
    + ' && Boolean(window.openlineopsDesktop))()',
    30000,
    'automation IDE start center to render');

  await evaluate('window.__openlineopsSmokeEvents = {}');
  await ensureBackendStarted();
  await waitForHealthyBackend();
  if (packagedMode) {
    await assertPackagedRuntimeDataBinding();
  }
  apiBaseUrl = (await evaluate('window.openlineopsDesktop.getConfig()')).apiBaseUrl;
  if (apiBaseUrl === apiSquatterBaseUrl) {
    throw new Error('Studio trusted an ambient API URL instead of its spawned process handshake.');
  }
  await captureSmokeScreenshot('start-center.png');

  const smokeProjectPath = path.join(
    repoRoot,
    'artifacts',
    'desktop-smoke-projects',
    `project-${Date.now().toString(36)}`);
  smokeProjectDirectories.push(smokeProjectPath);
  await waitForExpression(
    '(() => document.body.innerText.includes("Automation Projects")'
    + ' && Boolean(document.querySelector("[data-testid=\\"project-workspace-panel\\"]")))()',
    30000,
    'projects workbench to render');
  await clickByTestId('start-create-project');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"project-path-input\\"]")))()',
    15000,
    'new project dialog to render');
  await setInputByTestId('project-path-input', smokeProjectPath);
  await clickByTestId('create-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project created project-")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Project Explorer")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Default Application")'
    + ' && document.querySelector("[data-testid=\\"save-project-manifest\\"]")?.disabled === false)()',
    30000,
    'automation project workspace to create from desktop');
  await waitForExpression(
    '(() => document.querySelector(".rail-footer .status-pill")?.textContent?.trim() === "Connected")()',
    30000,
    'SignalR connection');
  await clickByTestId('save-project-manifest');
  await waitForExpression(
    '(() => document.body.innerText.includes("Manifest saved")'
    + ' && document.body.innerText.includes(".oloproj"))()',
    30000,
    'automation project manifest to save from desktop');
  await assertProjectFileLayout(smokeProjectPath, 1);
  await clickByTestId('switch-project-workspace');
  await waitForExpression(
    '(() => {'
    + ' const button = document.querySelector("[data-testid=\\"start-open-project-by-path\\"]");'
    + ' return button instanceof HTMLButtonElement && button.disabled === false;'
    + '})()',
    15000,
    'start center to render for project reopen');
  await clickByTestId('start-open-project-by-path');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"open-project-path-input\\"]")))()',
    15000,
    'open project path dialog to render');
  await setInputByTestId('open-project-path-input', smokeProjectPath);
  await clickByTestId('open-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project opened project-")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Automation Project"))()',
    30000,
    'automation project workspace to reopen from desktop');
  let openedProject = await getAutomationProjectByPath(smokeProjectPath);
  let openedApplication = openedProject.applications?.[0];
  if (!openedApplication) {
    throw new Error(`Opened automation project has no application: ${JSON.stringify(openedProject)}`);
  }
  activeProjectApplicationScope = {
    projectId: openedProject.projectId,
    applicationId: openedApplication.applicationId
  };
  const topologyId = `${openedApplication.applicationId}.topology.main`;
  const layoutId = `${openedApplication.applicationId}.layout.main`;
  const capabilityId = `${openedApplication.applicationId}.motion.axis.move`;
  const topologyBasePath = `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/applications/${encodeURIComponent(openedApplication.applicationId)}`
    + `/topologies/${encodeURIComponent(topologyId)}`;

  await clickByTestId('nav-topology');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"topology-workbench\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"new-topology-layout-empty\\"]")))()',
    30000,
    '2D layout workbench to render');
  await clickByTestId('new-topology-layout-empty');
  await waitForExpression(
    '(() => document.body.innerText.includes("2D layout ready ")'
    + ' && Boolean(document.querySelector("[data-testid=\\"topology-canvas\\"]")))()',
    30000,
    'Application topology and 2D layout to create');
  const capabilityMutation = await expectApiStatus(
    `${topologyBasePath}/capabilities`,
    await editorMutationOptions(topologyBasePath, {
      method: 'POST',
      body: {
        capabilityId,
        commandName: 'MoveAxis',
        version: '1.0.0',
        inputSchema: '{"axis":"string","position":"number","unit":"string"}',
        outputSchema: '{"completed":"boolean"}',
        timeoutSeconds: 15,
        safetyClass: 'Motion'
      }
    }),
    200,
    'add Application motion capability');
  await clickByTestId('refresh-topology-layout');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`@ ${capabilityMutation.body.revision.slice(0, 12)}`)})`
    + ' && document.querySelector("[data-testid=\\"topology-workbench\\"]")?.textContent?.includes("1 capabilities"))()',
    30000,
    'capability to refresh into the 2D layout editor');
  await dragPaletteItemToCanvas('add-topology-station', 0.32, 0.38);
  await waitForExpression(
    '(() => document.body.innerText.includes("Station added ")'
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("Station 01"))()',
    30000,
    'Station to be dragged onto the Application canvas');
  await dragPaletteItemToCanvas('add-topology-system', 0.31, 0.31);
  await waitForExpression(
    '(() => document.body.innerText.includes("System added ")'
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("System 1"))()',
    30000,
    'child System to be dragged into the Station');
  const driverBindingMutation = await expectApiStatus(
    `${topologyBasePath}/driver-bindings`,
    await editorMutationOptions(topologyBasePath, {
      method: 'POST',
      body: {
        bindingId: `${openedApplication.applicationId}.binding.axis.simulator`,
        ownerSystemId: `${openedApplication.applicationId}.station.1`,
        capabilityId,
        providerKind: 'Simulator',
        providerKey: 'simulator.axis.x'
      }
    }),
    200,
    'bind Application motion simulator to its Station System');
  await clickByTestId('refresh-topology-layout');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`@ ${driverBindingMutation.body.revision.slice(0, 12)}`)}))()`,
    30000,
    'Driver binding mutation revision to refresh into the 2D layout editor');
  await dragPaletteItemToCanvas('add-topology-system', 0.43, 0.31);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("System 2")'
    + ' && document.querySelector("[data-testid=\\"topology-property-display-name\\"]")?.value === "System 2")()',
    30000,
    'disposable child System to be added and selected');
  await setInputByTestId('topology-property-display-name', 'Temporary Vision Node');
  await setInputByTestId('topology-property-system-type', 'vision.controller');
  await clickByTestId('save-topology-target');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("Temporary Vision Node")'
    + ' && document.body.innerText.includes("Properties saved "))()',
    30000,
    'disposable child System properties to be renamed');
  await clickByTestId('delete-topology-target');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"delete-topology-target\\"]")?.textContent?.includes("Confirm Delete"))()',
    15000,
    'destructive target deletion to enter explicit confirmation state');
  await clickByTestId('delete-topology-target');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("Temporary Vision Node")'
    + ' && document.body.innerText.includes("Deleted "))()',
    30000,
    'confirmed child System deletion to update canvas and hierarchy');
  await dragPaletteItemToCanvas('add-topology-group', 0.35, 0.48);
  await waitForExpression(
    '(() => document.body.innerText.includes("Slot Group added ")'
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("Fixture Group 1"))()',
    30000,
    'Slot Group to be dragged into the Station');
  await dragPaletteItemToCanvas('add-topology-slot', 0.35, 0.48);
  await waitForExpression(
    '(() => document.body.innerText.includes("Production Unit Slot added ")'
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("S1"))()',
    30000,
    'Production Unit Slot to be dragged into its Slot Group');
  await captureSmokeScreenshot('topology-edit.png');
  await setInputByTestId('layout-geometry-x', '18');
  await clickByTestId('save-layout-geometry');
  await waitForExpression(
    '(() => document.body.innerText.includes("Layout saved")'
    + ' && document.querySelector("[data-testid=\\"layout-geometry-x\\"]")?.value === "18")()',
    30000,
    'nested Slot local geometry to be edited and saved');
  await clickByTestId('topology-dimension-3d');
  await waitForExpression(
    '(() => {'
    + ' const viewport = document.querySelector("[data-testid=\\"topology-3d-viewport\\"]");'
    + ' return viewport?.textContent?.includes("Station 01")'
    + '   && viewport?.textContent?.includes("S1")'
    + '   && Boolean(document.querySelector("[data-testid^=\\"topology-3d-element-\\"]"));'
    + '})()',
    30000,
    'semantic 3D topology to project the persisted hierarchy');
  await clickByTestId('topology-3d-rotate-right');
  await clickByTestId('topology-3d-zoom-in');
  const slot3DTestId = `topology-3d-element-${openedApplication.applicationId}.station.1.group.1.slot.1.shape`;
  const initial3DGeometry = await evaluate(`(() => ({
    x: Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value),
    y: Number(document.querySelector('[data-testid="layout-geometry-y"]')?.value)
  }))()`);
  if (!Number.isFinite(initial3DGeometry.x) || !Number.isFinite(initial3DGeometry.y)) {
    throw new Error(`3D drag has no finite starting geometry: ${JSON.stringify(initial3DGeometry)}`);
  }
  const dragEvents = await dragElementByTestId(slot3DTestId, 24, 14);
  try {
    await waitForExpression(
      `(() => Math.abs(Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value) - ${JSON.stringify(initial3DGeometry.x)}) > 0.001`
      + ` || Math.abs(Number(document.querySelector('[data-testid="layout-geometry-y"]')?.value) - ${JSON.stringify(initial3DGeometry.y)}) > 0.001)()`,
      30000,
      '3D block drag to update the shared local geometry');
  } catch (error) {
    throw new Error(`${error instanceof Error ? error.message : String(error)}\nPointer evidence: ${JSON.stringify(dragEvents)}`);
  }
  let edited3DGeometry = { x: Number.NaN, y: Number.NaN };
  const geometryDeadline = Date.now() + 30000;
  while (Date.now() < geometryDeadline) {
    const persistedLayout = await apiRequest(
      `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
      + `/applications/${encodeURIComponent(openedApplication.applicationId)}`
      + `/layouts/${encodeURIComponent(layoutId)}`);
    const persistedSlot = persistedLayout.body?.elements?.find(
      element => element.target?.targetId === `${openedApplication.applicationId}.station.1.group.1.slot.1`);
    if (persistedLayout.status === 200
        && (Math.abs(persistedSlot?.x - initial3DGeometry.x) > 0.001
          || Math.abs(persistedSlot?.y - initial3DGeometry.y) > 0.001)) {
      edited3DGeometry = { x: persistedSlot.x, y: persistedSlot.y };
      break;
    }
    await delay(200);
  }
  if (!Number.isFinite(edited3DGeometry.x)
      || !Number.isFinite(edited3DGeometry.y)
      || (Math.abs(edited3DGeometry.x - initial3DGeometry.x) <= 0.001
        && Math.abs(edited3DGeometry.y - initial3DGeometry.y) <= 0.001)) {
    throw new Error(`3D drag did not produce persisted geometry: ${JSON.stringify(edited3DGeometry)}`);
  }
  await captureSmokeScreenshot('topology-3d-edit.png');
  await clickByTestId('nav-projects');
  await clickByTestId('switch-project-workspace');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"start-open-project-by-path\\"]")))()',
    15000,
    'start center to render after layout edit');
  await clickByTestId('start-open-project-by-path');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"open-project-path-input\\"]")))()',
    15000,
    'open project path dialog to render after layout edit');
  await setInputByTestId('open-project-path-input', smokeProjectPath);
  await clickByTestId('open-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project opened project-"))()',
    30000,
    'automation project to reopen after layout edit');
  await clickByTestId('nav-topology');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("S1")'
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("System 1"))()',
    30000,
    'nested Application layout to persist through manifest reopen');
  const layoutResponse = await apiRequest(
    `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/applications/${encodeURIComponent(openedApplication.applicationId)}`
    + `/layouts/${encodeURIComponent(layoutId)}`);
  const editedElement = layoutResponse.body?.elements?.find(
    element => element.target?.targetId === `${openedApplication.applicationId}.station.1.group.1.slot.1`);
  if (layoutResponse.status !== 200
      || Math.abs(editedElement?.x - edited3DGeometry.x) > 0.01
      || Math.abs(editedElement?.y - edited3DGeometry.y) > 0.01
      || !editedElement?.parentElementId) {
    throw new Error(
      `Edited layout geometry was not persisted. Expected ${JSON.stringify(edited3DGeometry)}, `
      + `actual ${JSON.stringify(editedElement)}: ${layoutResponse.text}`);
  }
  const topologyResponseAfterReopen = await apiRequest(topologyBasePath);
  const topologySystemIds = topologyResponseAfterReopen.body?.systems?.map(system => system.systemId) ?? [];
  if (topologyResponseAfterReopen.status !== 200
      || !topologySystemIds.includes(`${openedApplication.applicationId}.station.1`)
      || !topologySystemIds.includes(`${openedApplication.applicationId}.station.1.component.1`)
      || topologySystemIds.includes(`${openedApplication.applicationId}.station.1.component.2`)) {
    throw new Error(`Topology rename/delete was not persisted: ${topologyResponseAfterReopen.text}`);
  }

  await exerciseTopologyDraftLifecycle({
    projectPath: smokeProjectPath,
    projectId: openedProject.projectId,
    applicationId: openedApplication.applicationId,
    topologyId,
    topologyBasePath,
    layoutId,
    capabilityAId: capabilityId,
    driverBindingId: `${openedApplication.applicationId}.binding.axis.simulator`,
    stationSystemId: `${openedApplication.applicationId}.station.1`
  });

  openedProject = await getAutomationProjectByPath(smokeProjectPath);
  openedApplication = openedProject.applications?.find(
    application => application.applicationId === activeProjectApplicationScope.applicationId);
  if (!openedApplication?.topologyId) {
    throw new Error(`Application topology link was not persisted: ${JSON.stringify(openedProject)}`);
  }

  await clickByTestId('nav-projects');
  const portableTarget = await copyAndImportPortableApplication(
    smokeProjectPath,
    openedProject,
    openedApplication);
  await openProjectByPathFromWorkbench(
    portableTarget.projectPath,
    portableTarget.projectId,
    openedApplication.applicationId);
  await openProjectByPathFromWorkbench(
    smokeProjectPath,
    openedProject.projectId,
    openedApplication.applicationId);

  const secondaryApplicationId = `${openedApplication.applicationId}-secondary`;
  await setInputByTestId('new-application-id', secondaryApplicationId);
  await setInputByTestId('new-application-name', 'Secondary Application');
  await clickByTestId('add-project-application');
  await waitForExpression(
    `(() => document.querySelector('[data-testid="active-application-selector"]')?.value === ${JSON.stringify(secondaryApplicationId)}`
    + ' && document.body.innerText.includes("Application created"))()',
    30000,
    'secondary project application to be created and selected');
  await assertProjectFileLayout(smokeProjectPath, 2);
  await setSelectByTestId('active-application-selector', openedApplication.applicationId);
  await clickByTestId('nav-topology');
  await waitForExpression(
    `(() => document.querySelector('[data-testid="active-application-selector"]')?.value === ${JSON.stringify(openedApplication.applicationId)}`
    + ' && document.querySelector("[data-testid=\\"topology-canvas\\"]")?.textContent?.includes("S1"))()',
    30000,
    'primary project application topology to be restored after switching applications');

  const primaryExtensionsPath = `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/applications/${encodeURIComponent(openedApplication.applicationId)}/extensions`;
  const secondaryExtensionsPath = `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/applications/${encodeURIComponent(secondaryApplicationId)}/extensions`;
  const secondaryBeforeImport = await apiRequest(secondaryExtensionsPath);
  if (secondaryBeforeImport.status !== 200 || secondaryBeforeImport.body?.length !== 0) {
    throw new Error(
      `Secondary Application unexpectedly inherited extensions: ${secondaryBeforeImport.text}`);
  }

  await clickByTestId('nav-plugins');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"plugins-workbench\\"]"))'
    + ' && document.body.innerText.includes("No extensions in this Application")'
    + ' && document.querySelector("[data-testid=\\"import-application-extension\\"]")?.disabled === false)()',
    30000,
    'primary Application Extensions Studio empty state');
  await clickByTestId('import-application-extension');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"extension-manifest-preview\\"]")?.textContent?.includes("Loopback Device Sample")'
    + ' && document.querySelector("[data-testid=\\"extension-content-sha256\\"] code")?.textContent?.trim().length === 64'
    + ' && document.querySelector("[data-testid=\\"extension-file-preview\\"]")?.textContent?.includes("OpenLineOps.SamplePlugins.LoopbackDevice.dll")'
    + ' && document.querySelector("[data-testid=\\"validate-application-extensions\\"]")?.disabled === false)()',
    45000,
    'explicit extension ZIP import with manifest, hash, and file preview');
  await clickByTestId('validate-application-extensions');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"extension-problems\\"]")?.textContent?.includes("No extension problems")'
    + ' && document.querySelector("[data-testid=\\"extension-manifest-preview\\"]")?.textContent?.includes("Package valid")'
    + ' && document.querySelector("[data-testid=\\"run-extension-provider-trial\\"]")?.disabled === false)()',
    30000,
    'Application extension validation');
  await clickByTestId('run-extension-provider-trial');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"extension-provider-trial-result\\"]")?.textContent?.includes("Completed / Passed")'
    + ' && document.querySelector("[data-testid=\\"run-extension-provider-trial\\"]")?.disabled === false)()',
    45000,
    'isolated provider trial success');
  await setInputByTestId('extension-trial-provider-key', 'missing.provider.for.failure-proof');
  await clickByTestId('run-extension-provider-trial');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"extension-provider-trial-result\\"]")?.textContent?.includes("Rejected / Unknown")'
    + ' && document.querySelector("[data-testid=\\"run-extension-provider-trial\\"]")?.disabled === false)()',
    45000,
    'isolated provider trial failure');
  await waitForNoPluginHostProcesses();

  await setSelectByTestId('active-application-selector', secondaryApplicationId);
  await waitForExpression(
    `(() => document.querySelector('[data-testid="active-application-selector"]')?.value === ${JSON.stringify(secondaryApplicationId)}`
    + ` && document.body.innerText.includes(${JSON.stringify(`Application selected ${secondaryApplicationId}`)}))()`,
    30000,
    'secondary Application selection');
  await clickByTestId('nav-plugins');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"plugins-workbench\\"]")?.textContent?.includes("No extensions in this Application")'
    + ' && document.querySelector("[data-testid=\\"import-application-extension\\"]")?.disabled === false)()',
    30000,
    'secondary Application extension scope');
  await clickByTestId('import-application-extension');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"extension-manifest-preview\\"]")?.textContent?.includes("openlineops.samples.loopback-device")'
    + ' && document.querySelector("[data-testid=\\"extension-file-preview\\"]")?.textContent?.includes("manifest.json")'
    + ' && document.querySelector("[data-testid=\\"remove-application-extension\\"]")?.disabled === false)()',
    45000,
    'same plugin id to import independently into secondary Application');

  const [primaryWithExtension, secondaryWithExtension] = await Promise.all([
    apiRequest(primaryExtensionsPath),
    apiRequest(secondaryExtensionsPath)
  ]);
  if (primaryWithExtension.status !== 200
      || secondaryWithExtension.status !== 200
      || primaryWithExtension.body?.length !== 1
      || secondaryWithExtension.body?.length !== 1
      || primaryWithExtension.body[0]?.pluginId !== secondaryWithExtension.body[0]?.pluginId
      || primaryWithExtension.body[0]?.portableId !== secondaryWithExtension.body[0]?.portableId) {
    throw new Error(
      'Application extension isolation failed before removal: '
      + `${primaryWithExtension.text} / ${secondaryWithExtension.text}`);
  }
  const portableId = primaryWithExtension.body[0].portableId;
  const [primaryStorage, secondaryStorage] = await Promise.all([
    readApplicationExtensionStorage(smokeProjectPath, openedApplication.applicationId, portableId),
    readApplicationExtensionStorage(smokeProjectPath, secondaryApplicationId, portableId)
  ]);
  if (primaryStorage.reference?.manifestPath !== secondaryStorage.reference?.manifestPath
      || primaryStorage.manifestPath === secondaryStorage.manifestPath
      || !primaryStorage.manifestExists
      || !secondaryStorage.manifestExists) {
    throw new Error(
      `Application extension files are not independently rooted: ${JSON.stringify({ primaryStorage, secondaryStorage })}`);
  }
  await clickByTestId('remove-application-extension');
  await waitForExpression(
    'document.querySelector("[data-testid=\\"remove-application-extension\\"]")?.textContent?.includes("Confirm remove")',
    10000,
    'extension remove confirmation');
  await clickByTestId('remove-application-extension');
  await waitForExpression(
    'document.querySelector("[data-testid=\\"plugins-workbench\\"]")?.textContent?.includes("No extensions in this Application")',
    30000,
    'extension removal from secondary Application');
  const [primaryAfterSecondaryRemoval, secondaryAfterRemoval] = await Promise.all([
    apiRequest(primaryExtensionsPath),
    apiRequest(secondaryExtensionsPath)
  ]);
  if (primaryAfterSecondaryRemoval.body?.length !== 1
      || secondaryAfterRemoval.body?.length !== 0) {
    throw new Error(
      'Removing a secondary Application extension crossed its scope boundary: '
      + `${primaryAfterSecondaryRemoval.text} / ${secondaryAfterRemoval.text}`);
  }
  const [primaryStorageAfterRemoval, secondaryStorageAfterRemoval] = await Promise.all([
    readApplicationExtensionStorage(smokeProjectPath, openedApplication.applicationId, portableId),
    readApplicationExtensionStorage(smokeProjectPath, secondaryApplicationId, portableId)
  ]);
  if (!primaryStorageAfterRemoval.reference
      || !primaryStorageAfterRemoval.manifestExists
      || secondaryStorageAfterRemoval.reference
      || secondaryStorageAfterRemoval.manifestExists) {
    throw new Error(
      `Application extension removal did not preserve the independent primary copy: ${JSON.stringify({ primaryStorageAfterRemoval, secondaryStorageAfterRemoval })}`);
  }
  await setSelectByTestId('active-application-selector', openedApplication.applicationId);
  await waitForExpression(
    `(() => document.querySelector('[data-testid="active-application-selector"]')?.value === ${JSON.stringify(openedApplication.applicationId)}`
    + ` && document.body.innerText.includes(${JSON.stringify(`Application selected ${openedApplication.applicationId}`)}))()`,
    30000,
    'primary Application selection after scoped extension removal');
  await clickByTestId('nav-plugins');
  await waitForExpression(
    'document.querySelector("[data-testid=\\"extension-manifest-preview\\"]")?.textContent?.includes("Loopback Device Sample")',
    30000,
    'primary Application extension to survive secondary removal');
  await captureSmokeScreenshot('application-extensions.png');

  await clickByTestId('nav-programs');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"external-program-workbench\\"]"))'
    + ' && document.querySelector("[data-testid=\\"save-external-program-resource\\"]")?.disabled === false)()',
    30000,
    'Application Program Resources workbench to load a valid Provider draft');
  const externalProgramGuardResourceId = `external-program-guard-${Date.now().toString(36)}`;
  await setInputByFieldLabel(
    'external-program-workbench',
    'Provider Kind',
    'PluginCommand');
  await setInputByFieldLabel(
    'external-program-workbench',
    'Provider Key',
    'openlineops.samples.loopback-device');
  await setInputByTestId('external-program-resource-id', externalProgramGuardResourceId);
  await clickByTestId('new-external-program-resource');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"external-program-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'External Program resource transition guard to open for Cancel');
  await clickByTestId('external-program-draft-transition-cancel');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="external-program-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="external-program-resource-id"]')?.value === ${JSON.stringify(externalProgramGuardResourceId)})()`,
    15000,
    'External Program Cancel to preserve the dirty resource draft');
  await clickByTestId('new-external-program-resource');
  await clickByTestId('external-program-draft-transition-save');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="external-program-draft-transition-dialog"]')`
    + ` && Boolean(document.querySelector('[data-testid=${JSON.stringify(`external-program-resource-${externalProgramGuardResourceId}`)}]'))`
    + ` && document.querySelector('[data-testid="external-program-resource-id"]')?.value !== ${JSON.stringify(externalProgramGuardResourceId)})()`,
    30000,
    'External Program Save and Continue to persist the resource before creating another');
  await clickByTestId(`external-program-resource-${externalProgramGuardResourceId}`);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"external-program-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'External Program resource transition guard to protect the replacement draft');
  await clickByTestId('external-program-draft-transition-discard');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="external-program-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="external-program-resource-id"]')?.value === ${JSON.stringify(externalProgramGuardResourceId)}`
    + ' && document.querySelector(\'[data-testid="external-program-resource-id"]\')?.disabled === true)()',
    30000,
    'External Program Discard Changes to replace the dirty draft with persisted content');

  await clickByTestId('nav-processes');
  await waitForExpression(
    '(() => document.body.innerText.includes("Process Builder")'
    + ' && document.body.innerText.includes("Blockly")'
    + ' && document.body.innerText.includes("Automation Blocks")'
    + ' && document.body.innerText.includes("Transitions")'
    + ' && document.body.innerText.includes("Block Catalog")'
    + ' && document.querySelector("[data-testid=\\"project-target-panel\\"]")?.textContent?.includes("Station 01")'
    + ' && Boolean(document.querySelector("[data-testid=\\"blockly-workspace\\"]")))()',
    15000,
    'process workbench to render');
  await waitForExpression(
    '(() => ['
    + ' "apply-project-target-system-0",'
    + ' "apply-project-target-capability-0",'
    + ' "apply-project-target-driver-0",'
    + ' "apply-project-target-slot-group-0",'
    + ' "apply-project-target-slot-0",'
    + ' "apply-project-target-production-unit-0"'
    + '].every(testId => Boolean(document.querySelector(`[data-testid="${testId}"]`))))()',
    15000,
    'all six formal process target kinds to be available');
  await waitForExpression(
    'Boolean(document.querySelector("[data-testid=\\"insert-block-openlineops_move_axis\\"]"))',
    30000,
    'the built-in Move Axis block catalog to finish loading');
  await clickByTestId('insert-block-openlineops_move_axis');
  await waitForExpression(
    '(() => document.body.innerText.includes("Blockly block inserted Move Axis"))()',
    15000,
    'target-bound Move Axis block to insert into the Blockly node');
  await captureSmokeScreenshot('flow-designer.png');
  await waitForExpression(
    '(() => {'
    + ' const workbench = document.querySelector(".process-workbench");'
    + ' const host = document.querySelector("[data-testid=\\"blockly-workspace\\"]");'
    + ' const block = Array.from(host?.querySelectorAll(".blocklyDraggable") ?? [])'
    + '   .find(candidate => candidate.textContent?.toLowerCase().includes("move")'
    + '     && candidate.textContent?.toLowerCase().includes("target"));'
    + ' if (!workbench || !host || !block) return false;'
    + ' const hostRect = host.getBoundingClientRect();'
    + ' const blockRect = block.getBoundingClientRect();'
    + ' return hostRect.height >= 340'
    + '   && blockRect.width > 80 && blockRect.height > 20'
    + '   && blockRect.bottom > Math.max(0, hostRect.top)'
    + '   && blockRect.top < Math.min(hostRect.bottom, window.innerHeight)'
    + '   && workbench.scrollWidth <= workbench.clientWidth + 1;'
    + '})()',
    15000,
    'rendered Move Axis Blockly block to be visible in the first-screen editor');
  await clickByTestId('register-blockly-block');
  await waitForExpression(
    '(() => document.body.innerText.includes("Fixture Action")'
    + ' && document.body.innerText.includes("1 custom")'
    + ' && document.body.innerText.includes("Registered Blockly block user_fixture_action revision 1")'
    + ' && !document.querySelector("[data-testid=\\"register-blockly-block\\"]")?.disabled)()',
    30000,
    'custom Blockly block to register from UI');
  await clickByTestId('register-blockly-block');
  await waitForExpression(
    '(() => document.querySelectorAll("[data-testid=\\"block-version-history\\"] .block-version-row").length === 2)()',
    30000,
    'custom Blockly block version history to load');
  await clickByTestId('restore-blockly-block-revision-1');
  await waitForExpression(
    '(() => document.body.innerText.includes("Restored user_fixture_action revision 1 as revision 3")'
    + ' && document.querySelector("[data-testid=\\"block-version-history\\"]")?.textContent?.includes("revision 3"))()',
    30000,
    'custom Blockly block version to restore from UI');
  await clickByTestId('add-command-node');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"process-node-command-1\\"]")))()',
    15000,
    'process graph command node to be added');
  await clickByTestId('apply-project-target-system-0');
  await waitForExpression(
    `(() => document.body.innerText.includes("Project target applied Station 01")`
    + ' && document.querySelector("[data-testid=\\"process-node-target-kind\\"]")?.value === "System"'
    + ` && document.querySelector("[data-testid=\\"process-node-target-id\\"]")?.value === ${JSON.stringify(`${openedApplication.applicationId}.station.1`)}`
    + ` && document.querySelector("[data-testid=\\"process-node-required-capability\\"]")?.value === ${JSON.stringify(capabilityId)})()`,
    15000,
    'project topology target to apply to the command node');
  await clickByTestId('add-pythonscript-node');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"process-node-pythonscript-1\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"python-source-editor\\"]")))()',
    15000,
    'process-isolated Python node to be added');
  await setInputByTestId('python-timeout-seconds', '60');
  await clickByTestId('add-decision-node');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"process-node-decision-1\\"]")))()',
    15000,
    'process graph decision node to be added');
  await clickByTestId('add-transition');
  const retryTransitionId = await waitForGeneratedTransitionId(
    'decision-1-to-end',
    ['decision-1-to-end'],
    'decision retry transition row to be added');
  await setSelectByTestId(`transition-to-${retryTransitionId}`, 'automation');
  await setInputByTestId(`transition-label-${retryTransitionId}`, 'retry');
  await setSelectByTestId(`transition-loop-policy-${retryTransitionId}`, 'Counted');
  await setInputByTestId(`transition-max-traversals-${retryTransitionId}`, '2');
  await setInputByTestId(`transition-id-${retryTransitionId}`, 'decision-1-to-automation-retry');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"transition-loop-policy-decision-1-to-automation-retry\\"]")?.value === "Counted"'
    + ' && document.querySelector("[data-testid=\\"transition-max-traversals-decision-1-to-automation-retry\\"]")?.value === "2"'
    + ' && document.querySelector("[data-testid=\\"transition-to-decision-1-to-automation-retry\\"]")?.value === "automation")()',
    15000,
    'transition counted loop policy to be edited');
  await clickByTestId('save-process-definition');
  await waitForExpression(
    '(() => document.body.innerText.includes("Saved desktop-flow-"))()',
    30000,
    'Blockly process definition to save');
  const savedDefinition = await getLatestDesktopProcessDefinition();
  const savedBlocklyNode = savedDefinition.nodes
    ?.find(node => node.nodeId === 'automation' && node.kind === 'Blockly');
  if (savedBlocklyNode?.scriptSourceCode !== null
    || !savedBlocklyNode?.blocklyWorkspaceJson?.includes('.station.1')
    || !savedBlocklyNode?.blocklyWorkspaceJson?.includes('.motion.axis.move')) {
    throw new Error(`Direct Blockly target binding was not persisted: ${JSON.stringify(savedBlocklyNode)}`);
  }
  const savedCommandNode = savedDefinition.nodes
    ?.find(node => node.nodeId === 'command-1' && node.kind === 'Command');
  if (savedCommandNode?.targetKind !== 'System'
    || savedCommandNode?.targetId !== `${openedApplication.applicationId}.station.1`
    || savedCommandNode?.requiredCapability !== capabilityId
    || savedCommandNode?.commandName !== 'MoveAxis') {
    throw new Error(`Formal Command target binding was not persisted: ${JSON.stringify(savedCommandNode)}`);
  }
  const savedPythonNode = savedDefinition.nodes
    ?.find(node => node.nodeId === 'pythonscript-1' && node.kind === 'PythonScript');
  if (!savedPythonNode?.scriptSourceCode?.includes('manual Python action completed')) {
    throw new Error(`Python source node was not persisted: ${JSON.stringify(savedPythonNode)}`);
  }
  const countedTransition = savedDefinition.transitions
    ?.find(transition => transition.transitionId === 'decision-1-to-automation-retry');
  if (countedTransition?.loopPolicy !== 'Counted' || countedTransition?.maxTraversals !== 2) {
    throw new Error(`Counted transition policy was not persisted: ${JSON.stringify(countedTransition)}`);
  }
  const processGuardSavedName = `Guard Saved Flow ${Date.now().toString(36)}`;
  await setInputByTestId('process-display-name', processGuardSavedName);
  await clickByTestId('new-process-definition');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"process-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'Process resource transition guard to open for Cancel');
  await clickByTestId('process-draft-transition-cancel');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="process-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="process-display-name"]')?.value === ${JSON.stringify(processGuardSavedName)})()`,
    15000,
    'Process Cancel to preserve the dirty draft');
  await clickByTestId('new-process-definition');
  await clickByTestId('process-draft-transition-save');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="process-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="process-definition-id"]')?.value !== ${JSON.stringify(savedDefinition.processDefinitionId)})()`,
    30000,
    'Process Save and Continue to persist the draft before creating another Flow');
  await clickByTestId(`process-definition-${savedDefinition.processDefinitionId}`);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"process-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'Process resource transition guard to protect the replacement draft');
  await clickByTestId('process-draft-transition-discard');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="process-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="process-definition-id"]')?.value === ${JSON.stringify(savedDefinition.processDefinitionId)}`
    + ` && document.querySelector('[data-testid="process-display-name"]')?.value === ${JSON.stringify(processGuardSavedName)})()`,
    30000,
    'Process Discard Changes to replace the dirty new draft with the saved Flow');
  await clickByTestId('process-node-pythonscript-1');
  await clickByTestId('remove-process-node');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"process-node-pythonscript-1\\"]"))()',
    15000,
    'validated Python draft node to be removed before Station-scoped publication');
  const updatedProcessDisplayName = `Updated Automation Flow ${Date.now().toString(36)}`;
  await setInputByTestId('process-display-name', updatedProcessDisplayName);
  await clickByTestId('save-process-definition');
  const updatedDefinition = await waitForProcessDefinitionDisplayName(
    savedDefinition.processDefinitionId,
    updatedProcessDisplayName);
  if (updatedDefinition.createdAtUtc !== savedDefinition.createdAtUtc) {
    throw new Error(
      `Replacing a draft changed its creation time: ${savedDefinition.createdAtUtc} -> ${updatedDefinition.createdAtUtc}`);
  }
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"publish-process-definition\\"]")?.disabled === false)()',
    15000,
    'updated process draft to remain selected for publication');
  await clickByTestId('publish-process-definition');
  await waitForExpression(
    '(() => document.body.innerText.includes("Published desktop-flow-")'
    + ' && document.body.innerText.includes("Published"))()',
    30000,
    'direct Blockly process definition to publish');
  const publishedDefinition = await getPublishedDesktopProcessDefinition();
  const configurationSnapshotId = await createPublishedEngineeringSnapshot(publishedDefinition);
  await clickByTestId('nav-production');
  await waitForExpression(
    '(() => document.body.innerText.includes("Line Designer")'
    + ' && Boolean(document.querySelector("[data-testid=\\"production-workbench\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"save-production-line\\"]")))()',
    30000,
    'production line designer to render');
  await clickByTestId('new-production-line');
  await waitForExpression(
    `(() => document.querySelector('[data-testid="production-operation-configuration-0"]')?.value === ${JSON.stringify(configurationSnapshotId)})()`,
    30000,
    'production Operation to bind its published Station and Flow configuration');
  await clickByTestId('add-production-operation');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"production-operation-node-operation-2\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"production-transition-edge-route-1\\"]")))()',
    15000,
    'second Operation and its default route to appear');
  await clickByTestId('production-transition-edge-route-1');
  await setSelectByTestId('route-transition-kind', 'Condition');
  await setInputByTestId('route-condition-output-key', 'inspectionPassed');
  await setSelectByTestId('route-condition-expected-kind', 'Boolean');
  await setInputByTestId('route-condition-expected-value', 'true');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-transition-edge-route-1\\"]")?.textContent?.includes("inspectionPassed = true"))()',
    15000,
    'typed Production Context Condition to render on the route graph');
  await setSelectByTestId('route-transition-kind', 'Sequence');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-transition-edge-route-1\\"]")?.textContent?.includes("Sequence"))()',
    15000,
    'runtime Route Transition to return to an unconditional sequence');
  await clickByTestId('production-operation-node-operation-2');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid^=\\"production-transition-edge-route-terminal-\\"]"))'
    + ' && document.querySelector("[data-testid=\\"production-operation-node-operation-2\\"]")'
    + '   ?.textContent?.includes("COMPLETED"))()',
    15000,
    'explicit Completed terminal Route Transition to appear after Operation 2');
  await evaluate(`(() => {
    const editor = document.querySelector('[data-testid="operation-resource-editor"]');
    const add = Array.from(editor?.querySelectorAll('button') ?? [])
      .find(candidate => candidate.textContent?.trim().endsWith('Resource'));
    if (!(add instanceof HTMLButtonElement)) {
      throw new Error('Missing Operation Resource action for Operation 2');
    }
    add.click();
    return true;
  })()`);
  await waitForExpression(
    '(() => Array.from(document.querySelectorAll(\'[data-testid="operation-resource-editor"] label\'))'
    + '.some(label => label.querySelector(\':scope > span\')?.textContent?.trim() === "Resolution"'
    + ' && label.querySelector("select")?.value === "CurrentMaterialSlot"))()',
    15000,
    'Operation 2 Slot Resource editor to render');
  await setSelectByFieldLabel('operation-resource-editor', 'Resolution', 'Fixed');
  const productionSlotId = `${openedApplication.applicationId}.station.1.group.1.slot.1`;
  await waitForExpression(
    `(() => {
      const editor = document.querySelector('[data-testid="operation-resource-editor"]');
      const labels = Array.from(editor?.querySelectorAll('label') ?? []);
      const resolution = labels.find(label => label.querySelector(':scope > span')?.textContent?.trim() === 'Resolution')
        ?.querySelector('select');
      const target = labels.find(label => label.querySelector(':scope > span')?.textContent?.trim() === 'Topology target')
        ?.querySelector('select');
      return resolution?.value === 'Fixed'
        && Array.from(target?.options ?? []).some(option => option.value === ${JSON.stringify(productionSlotId)});
    })()`,
    15000,
    'fixed Slot resolution to expose Station-local topology targets');
  await setSelectByFieldLabel('operation-resource-editor', 'Topology target', productionSlotId);
  await waitForExpression(
    `(() => {
      const editor = document.querySelector('[data-testid="operation-resource-editor"]');
      const labels = Array.from(editor?.querySelectorAll('label') ?? []);
      const resolution = labels.find(label => label.querySelector(':scope > span')?.textContent?.trim() === 'Resolution')
        ?.querySelector('select');
      const target = labels.find(label => label.querySelector(':scope > span')?.textContent?.trim() === 'Topology target')
        ?.querySelector('select');
      return resolution?.value === 'Fixed' && target?.value === ${JSON.stringify(productionSlotId)};
    })()`,
    15000,
    'Operation 2 to require the fixed production Slot before execution');
  await clickByTestId('save-production-line');
  await waitForExpression(
    '(() => document.body.innerText.includes("Production line saved line-"))()',
    30000,
    'Product Model and Operation route graph to save');
  await captureSmokeScreenshot('line-designer.png');
  const productionLineDefinitionId = await assertProductionLinePersisted();
  const savedLayoutBeforeDrag = await getProductionLineLayout(productionLineDefinitionId);
  const savedPositionBeforeDrag = savedLayoutBeforeDrag.operationPositions.find(
    position => position.operationId === 'operation-1');
  if (!savedPositionBeforeDrag) {
    throw new Error(`Saved route layout is missing operation-1: ${JSON.stringify(savedLayoutBeforeDrag)}`);
  }
  await dragProductionOperationByTestId('production-operation-node-operation-1', 36, 22);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-dirty-state\\"]")?.textContent?.trim() === "Unsaved")()',
    15000,
    'dragging a production Operation to mark the Line Designer dirty');
  await clickByTestId('save-production-line');
  const savedLayoutAfterDrag = await waitForProductionLineLayout(
    productionLineDefinitionId,
    layout => layout.operationPositions.some(position => (
      position.operationId === 'operation-1'
      && (position.x !== savedPositionBeforeDrag.x || position.y !== savedPositionBeforeDrag.y))),
    'dragged route layout to be included in the atomic save request');
  const savedPositionAfterDrag = savedLayoutAfterDrag.operationPositions.find(
    position => position.operationId === 'operation-1');
  if (!savedPositionAfterDrag) {
    throw new Error(`Dragged route layout is missing operation-1: ${JSON.stringify(savedLayoutAfterDrag)}`);
  }
  await clickByTestId('auto-arrange-production-route');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-dirty-state\\"]")?.textContent?.trim() === "Unsaved")()',
    15000,
    'Auto arrange to mark the Line Designer dirty');
  await clickByTestId('save-production-line');
  const arrangedLayout = await waitForProductionLineLayout(
    productionLineDefinitionId,
    layout => layout.operationPositions.length === 2
      && layout.operationPositions.some(position => position.operationId === 'operation-1'
        && (position.x !== savedPositionAfterDrag.x
          || position.y !== savedPositionAfterDrag.y)),
    'auto-arranged route layout to persist');
  const arrangedPosition = arrangedLayout.operationPositions.find(
    position => position.operationId === 'operation-1');
  if (!arrangedPosition) {
    throw new Error(`Auto-arranged route layout is missing operation-1: ${JSON.stringify(arrangedLayout)}`);
  }
  await waitForExpression(
    `(() => document.querySelector('[data-testid="production-dirty-state"]')?.textContent?.trim() === 'Saved'`
      + ` && document.querySelector('[data-testid="production-operation-node-operation-1"]')?.style.transform === ${JSON.stringify(`translate(${arrangedPosition.x}px, ${arrangedPosition.y}px)`)}`
      + ' && document.querySelector(\'[data-testid="new-production-line"]\')?.disabled === false)()',
    15000,
    'auto-arranged route save and command state to settle before editing the persisted Line');
  const productionGuardSavedName = `Guard Saved Line ${Date.now().toString(36)}`;
  await setInputByTestId('production-line-name', productionGuardSavedName);
  await waitForExpression(
    `(() => document.querySelector('[data-testid="production-line-name"]')?.value === ${JSON.stringify(productionGuardSavedName)}`
      + ' && document.querySelector("[data-testid=\\"production-dirty-state\\"]")?.textContent?.trim() === "Unsaved")()',
    15000,
    'Production line name edit to commit before requesting a guarded resource transition');
  await clickByTestId('new-production-line');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'Production resource transition guard to open for Cancel');
  await clickByTestId('production-draft-transition-cancel');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="production-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="production-line-name"]')?.value === ${JSON.stringify(productionGuardSavedName)})()`,
    15000,
    'Production Cancel to preserve the dirty Line draft');
  await clickByTestId('new-production-line');
  await clickByTestId('production-draft-transition-save');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="production-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="production-line-id"]')?.value !== ${JSON.stringify(productionLineDefinitionId)})()`,
    30000,
    'Production Save and Continue to persist the Line before creating another');
  await clickByTestId(`production-line-${productionLineDefinitionId}`);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-draft-transition-dialog\\"]")?.open === true)()',
    15000,
    'Production resource transition guard to protect the replacement Line draft');
  await clickByTestId('production-draft-transition-discard');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="production-draft-transition-dialog"]')`
    + ` && document.querySelector('[data-testid="production-line-id"]')?.value === ${JSON.stringify(productionLineDefinitionId)}`
    + ` && document.querySelector('[data-testid="production-line-name"]')?.value === ${JSON.stringify(productionGuardSavedName)}`
    + ` && document.querySelector('[data-testid="production-operation-node-operation-1"]')?.style.transform === ${JSON.stringify(`translate(${arrangedPosition.x}px, ${arrangedPosition.y}px)`)})()`,
    30000,
    'Production Discard Changes to reopen the saved Line with server-owned route coordinates');
  await setInputByTestId('production-line-name', '');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-designer-problems\\"]")?.textContent?.includes("Line display name"))()',
    15000,
    'Line display name validation problem to render');
  await evaluate(`(() => {
    const button = Array.from(document.querySelectorAll('[data-testid="production-designer-problems"] button'))
      .find(candidate => candidate.textContent?.includes('Line display name'));
    if (!(button instanceof HTMLButtonElement)) {
      throw new Error('Missing Line display name Problem action');
    }
    button.click();
    return true;
  })()`);
  await waitForExpression(
    '(() => document.activeElement?.getAttribute("data-production-problem-field") === "line.display-name")()',
    15000,
    'Production Problem navigation to focus the exact Line display-name field');
  await setInputByTestId('production-line-name', productionGuardSavedName);
  await clickByTestId('save-production-line');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-dirty-state\\"]")?.textContent?.trim() === "Saved")()',
    30000,
    'Production Line to return clean after exact Problem navigation');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"publish-production-line-snapshot\\"]")?.disabled === false)()',
    30000,
    'saved production line to become publishable');
  await clickByTestId('publish-production-line-snapshot');
  const projectSnapshotId = await waitForExpression(
    '(() => {'
    + ' const result = document.querySelector("[data-testid=\\"production-snapshot-result\\"]");'
    + ' const snapshotId = result?.querySelector("strong")?.textContent?.trim() ?? "";'
    + ` return snapshotId.startsWith("project-snapshot-") && result?.textContent?.includes(${JSON.stringify(productionLineDefinitionId)})`
    + '   ? snapshotId'
    + '   : "";'
    + '})()',
    45000,
    'project snapshot to publish from the saved production line');
  const projectWithSnapshot = await getAutomationProjectByPath(smokeProjectPath);
  const publishedProjectSnapshot = projectWithSnapshot.snapshots
    ?.find(snapshot => snapshot.snapshotId === projectSnapshotId);
  if (projectWithSnapshot.activeSnapshotId !== projectSnapshotId || !publishedProjectSnapshot) {
    throw new Error(`Project snapshot was not activated: ${JSON.stringify(projectWithSnapshot)}`);
  }
  if (publishedProjectSnapshot.productionLineDefinitionId !== productionLineDefinitionId
    || 'processDefinitionId' in publishedProjectSnapshot
    || 'processVersionId' in publishedProjectSnapshot
    || 'configurationSnapshotId' in publishedProjectSnapshot
    || !publishedProjectSnapshot.targetReferences?.some(target => target.targetId.includes('.station.1'))
    || !publishedProjectSnapshot.capabilityBindings?.some(binding => binding.providerKey === 'simulator.axis.x')
    || !publishedProjectSnapshot.blockVersionIds?.some(blockVersionId => blockVersionId.startsWith('openlineops_move_axis@'))) {
    throw new Error(`Project snapshot payload was incomplete: ${JSON.stringify(publishedProjectSnapshot)}`);
  }

  const immutableContextPath = `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/snapshots/${encodeURIComponent(projectSnapshotId)}/production-run-context`;
  const immutableContextBeforeDraftEdit = await expectApiStatus(
    immutableContextPath,
    {},
    200,
    'read immutable Production Run context before editing the draft line');
  const frozenContext = immutableContextBeforeDraftEdit.body;
  const liveLinePath = `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
    + `/applications/${encodeURIComponent(openedApplication.applicationId)}`
    + `/production-lines/${encodeURIComponent(productionLineDefinitionId)}`;
  const liveLine = await expectApiStatus(
    liveLinePath,
    {},
    200,
    'read editable Production line after publishing');
  if (frozenContext?.projectId !== openedProject.projectId
      || frozenContext?.applicationId !== openedApplication.applicationId
      || frozenContext?.snapshotId !== projectSnapshotId
      || frozenContext?.topologyId !== publishedProjectSnapshot.topologyId
      || frozenContext?.productionLineDefinitionId !== productionLineDefinitionId
      || frozenContext?.productModelId !== liveLine.body?.productModel?.productModelId
      || frozenContext?.productModelIdentityInputKey !== liveLine.body?.productModel?.identityInputKey
      || frozenContext?.entryOperationId !== liveLine.body?.entryOperationId
      || !frozenContext?.entryStationSystemId) {
    throw new Error(`Immutable Production Run context was incomplete: ${JSON.stringify(frozenContext)}`);
  }

  const editedDraftProductModelId = `${frozenContext.productModelId}.draft-edited`;
  await expectApiStatus(
    liveLinePath,
    {
      method: 'PUT',
      headers: { 'If-Match': `"${liveLine.body.revision}"` },
      body: {
        lineDefinitionId: liveLine.body.lineDefinitionId,
        displayName: liveLine.body.displayName,
        topologyId: liveLine.body.topologyId,
        productModel: {
          productModelId: editedDraftProductModelId,
          modelCode: liveLine.body.productModel.modelCode,
          identityInputKey: 'draftSerial'
        },
        entryOperationId: liveLine.body.entryOperationId,
        operations: liveLine.body.operations.map(operation => ({
          operationId: operation.operationId,
          displayName: operation.displayName,
          stationSystemId: operation.stationSystemId,
          flowDefinitionId: operation.flowDefinitionId,
          configurationSnapshotId: operation.configurationSnapshotId,
          resources: operation.resources.map(resource => ({
            bindingId: resource.bindingId,
            kind: resource.kind,
            topologyTargetId: resource.topologyTargetId,
            resolution: resource.resolution
          })),
          inputMappings: operation.inputMappings.map(mapping => ({
            targetInputKey: mapping.targetInputKey,
            sourceOperationId: mapping.sourceOperationId,
            sourceOutputKey: mapping.sourceOutputKey,
            expectedValueKind: mapping.expectedValueKind
          }))
        })),
        transitions: liveLine.body.transitions.map(transition => ({
          transitionId: transition.transitionId,
          sourceOperationId: transition.sourceOperationId,
          targetOperationId: transition.targetOperationId,
          terminalDisposition: transition.terminalDisposition,
          kind: transition.kind,
          requiredJudgement: transition.requiredJudgement,
          maxTraversals: transition.maxTraversals,
          parallelGroupId: transition.parallelGroupId,
          outputKey: transition.outputKey,
          expectedOutputKind: transition.expectedOutputKind,
          expectedOutputValue: transition.expectedOutputValue
        })),
        lineControllerAuthorizations: liveLine.body.lineControllerAuthorizations.map(
          authorization => ({ ...authorization })),
        routeLayout: {
          operationPositions: liveLine.body.routeLayout.operationPositions.map(
            position => ({ ...position }))
        }
      }
    },
    200,
    'edit the draft Product Model after publishing');
  const immutableContextAfterDraftEdit = await expectApiStatus(
    immutableContextPath,
    {},
    200,
    'read immutable Production Run context after editing the draft line');
  if (immutableContextAfterDraftEdit.body?.productModelId !== frozenContext.productModelId
      || immutableContextAfterDraftEdit.body?.productModelIdentityInputKey
        !== frozenContext.productModelIdentityInputKey
      || immutableContextAfterDraftEdit.body?.productModelId === editedDraftProductModelId) {
    throw new Error(
      `Immutable Production Run context changed with the draft line: ${JSON.stringify(immutableContextAfterDraftEdit.body)}`);
  }

  await clickByTestId('nav-production');
  await setInputByTestId('production-line-name', `${liveLine.body.displayName} Editor Draft`);
  await clickByTestId('save-production-line');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"editor-external-conflict\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"conflict-reload\\"]"))'
    + ' && document.querySelector("[data-testid=\\"editor-tab-production\\"]")?.textContent?.includes("Line Designer"))()',
    30000,
    'external Production Line conflict to preserve the mounted editor draft');
  await captureSmokeScreenshot('editor-external-conflict.png');
  await clickByTestId('conflict-reload');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="editor-external-conflict"]')`
    + ` && document.querySelector('[data-testid="production-product-model-id"]')?.value === ${JSON.stringify(editedDraftProductModelId)}`
    + ' && document.querySelectorAll("[data-testid=\\"editor-tab-strip\\"] [role=\\"tab\\"]").length >= 3)()',
    30000,
    'conflicted editor to reload without unmounting the other open tabs');

  await clickByTestId('run-active-project');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"production-run-dialog\\"]")?.open === true)()',
    15000,
    'Run Project identity dialog to render');
  const productionUnitId = await waitForExpression(
    '(() => {'
    + ' const value = document.querySelector("[data-testid=\\"production-run-unit-id\\"]")?.value ?? "";'
    + ' return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value)'
    + '   ? value : "";'
    + '})()',
    15000,
    'Run Project to generate a canonical lowercase Production Unit ID');
  await setInputByTestId('production-run-unit-identity', `UNIT-${openedProject.projectId}`);
  await clickByTestId('confirm-production-run');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"topology-workbench\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"topology-dimension-2d\\"]")))()',
    30000,
    'Run Project to open the Topology Monitor');
  await clickByTestId('topology-dimension-2d');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"topology-2d-route-flow\\"]")))()',
    15000,
    'Topology Monitor to enter its explicit 2D runtime projection');
  const sharedRouteProjection = await waitForExpression(
    `(() => {
      const overlay = document.querySelector(
        '[data-testid="topology-current-material-flow"] [data-transition-id="route-1"][data-flow-kind="Transition"]');
      const graphic = document.querySelector(
        '[data-testid="topology-2d-route-flow"] [data-testid^="topology-route-flow-"][data-transition-id="route-1"]');
      if (!(overlay instanceof HTMLElement) || !(graphic instanceof SVGElement)) return null;
      const overlayTestId = overlay.getAttribute('data-testid') ?? '';
      const graphicTestId = graphic.getAttribute('data-testid') ?? '';
      const movementIdentity = overlayTestId.startsWith('topology-live-route-flow-')
        ? overlayTestId.slice('topology-live-route-flow-'.length)
        : '';
      if (!movementIdentity || graphicTestId !== 'topology-route-flow-' + movementIdentity) return null;
      return {
        movementIdentity,
        graphicTestId,
        transitionId: overlay.getAttribute('data-transition-id'),
        flowKind: overlay.getAttribute('data-flow-kind'),
        sourceStation: overlay.getAttribute('data-source-station'),
        targetStation: overlay.getAttribute('data-target-station')
      };
    })()`,
    60000,
    'real run to expose the shared route-1 movement in the 2D topology and material-flow overlay');
  if (sharedRouteProjection.transitionId !== 'route-1'
      || sharedRouteProjection.flowKind !== 'Transition'
      || !sharedRouteProjection.sourceStation
      || !sharedRouteProjection.targetStation) {
    throw new Error(`2D topology route projection was incomplete: ${JSON.stringify(sharedRouteProjection)}`);
  }
  await clickByTestId('topology-dimension-3d');
  await waitForExpression(
    `(() => {
      const movement = document.querySelector(
        '[data-testid="topology-3d-route-flow"] [data-testid=${JSON.stringify(sharedRouteProjection.graphicTestId)}]');
      return movement?.getAttribute('data-transition-id') === 'route-1'
        && movement?.getAttribute('data-source-station') === ${JSON.stringify(sharedRouteProjection.sourceStation)}
        && movement?.getAttribute('data-target-station') === ${JSON.stringify(sharedRouteProjection.targetStation)};
    })()`,
    30000,
    '3D topology to render the exact same real route movement as 2D');
  await clickByTestId('nav-dashboard');
  await waitForExpression(
    '(() => {'
    + ' const current = document.querySelector("[data-testid=\\"production-route-current-flow\\"]");'
    + ' const decision = document.querySelector("[data-testid=\\"production-route-decision-route-1\\"]");'
    + ' const trail = document.querySelector("[data-testid=\\"production-route-decision-trail\\"]");'
    + ' return current?.getAttribute("data-transition-id") === "route-1"'
    + '   && current?.getAttribute("data-flow-kind") === "Transition"'
    + '   && current?.textContent?.includes("CURRENT TRANSITION")'
    + '   && decision?.getAttribute("data-flow-kind") === "Transition"'
    + '   && decision?.textContent?.includes("route-1")'
    + '   && trail?.textContent?.includes("route-1");'
    + '})()',
    30000,
    'Operations to show the real current Route Transition and persisted decision trail');
  await captureSmokeScreenshot('route-runtime-shared-projection.png');
  const productionStationSystemId = `${openedApplication.applicationId}.station.1`;
  const slotAddressPath = `/api/slot-occupancies/${encodeURIComponent(productionLineDefinitionId)}`
    + `/${encodeURIComponent(productionStationSystemId)}`
    + `/${encodeURIComponent(productionSlotId)}`;
  const slotLifecycleStartedAt = Date.now();
  await expectApiStatus(
    '/api/slot-occupancies',
    {
      method: 'POST',
      body: {
        lineId: productionLineDefinitionId,
        stationSystemId: productionStationSystemId,
        slotId: productionSlotId,
        occurredAtUtc: new Date(slotLifecycleStartedAt).toISOString()
      }
    },
    201,
    'register the frozen production Slot before releasing Operation 2');
  for (const [index, command] of ['Reserve', 'Load', 'Start'].entries()) {
    await expectApiStatus(
      `${slotAddressPath}/commands/${command}`,
      {
        method: 'POST',
        body: {
          materialKind: 'ProductionUnit',
          materialId: productionUnitId,
          destination: null,
          reason: null,
          occurredAtUtc: new Date(slotLifecycleStartedAt + index + 1).toISOString()
        }
      },
      200,
      `${command} the Production Unit in the frozen Slot`);
  }
  await clickByTestId('nav-topology');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"topology-workbench\\"]")))()',
    15000,
    'Topology monitor to reopen after Operations route evidence');
  await clickByTestId('topology-dimension-2d');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"topology-canvas\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"topology-2d-route-flow\\"]")))()',
    15000,
    'Topology Monitor to return to 2D for final runtime-state assertions');
  let completedSlot = null;
  const completedSlotDeadline = Date.now() + 45000;
  while (Date.now() < completedSlotDeadline) {
    completedSlot = await apiRequest(slotAddressPath);
    if (completedSlot.status === 200 && completedSlot.body?.status === 'Occupied') {
      break;
    }
    await delay(100);
  }
  if (completedSlot?.status !== 200 || completedSlot.body?.status !== 'Occupied') {
    throw new Error(
      `Operation 2 did not complete its running Slot lifecycle: ${completedSlot?.text ?? 'no response'}`);
  }
  await expectApiStatus(
    `${slotAddressPath}/commands/Unload`,
    {
      method: 'POST',
      body: {
        materialKind: 'ProductionUnit',
        materialId: productionUnitId,
        destination: {
          kind: 'StationQueue',
          lineId: productionLineDefinitionId,
          stationSystemId: productionStationSystemId,
          slotId: null,
          carrierId: null,
          carrierPositionId: null
        },
        reason: null,
        occurredAtUtc: new Date().toISOString()
      }
    },
    200,
    'unload the completed Production Unit and restore the Slot to Available');
  await waitForExpression(
    `(() => {
      const events = window.__openlineopsSmokeEvents ?? {};
      const workbench = document.querySelector('[data-testid="topology-workbench"]');
      const station = workbench?.querySelector('.station-system');
      const childSystem = workbench?.querySelector('.system-system');
      const group = workbench?.querySelector('.group-region');
      const slot = workbench?.querySelector('.slot-shape');
      return document.body.innerText.includes('Production run Completed')
        && workbench?.textContent?.includes('Live line overview')
        && workbench?.textContent?.includes('Station 01')
        && workbench?.querySelector('[data-testid="topology-runtime-state-label"]')?.textContent?.includes('Line idle')
        && workbench?.querySelector('[data-testid="topology-live-projection"]')?.textContent?.includes('No active products')
        && workbench?.querySelector('[data-testid="topology-live-projection"]')?.textContent?.includes('CONNECTED')
        && station?.getAttribute('data-operational-state') === 'Idle'
        && childSystem?.getAttribute('data-operational-state') === 'Idle'
        && group?.getAttribute('data-operational-state') === 'Idle'
        && slot?.getAttribute('data-operational-state') === 'Idle'
        && (events.RuntimeEvent ?? 0) > 0
        && (events.StationStatusChanged ?? 0) > 0
        && (events.TargetStatusChanged ?? 0) > 0;
    })()`,
    45000,
    'formal project snapshot runtime to open the topology Monitor view');
  const productionUnit = await expectApiStatus(
    `/api/production-units/${encodeURIComponent(productionUnitId)}`,
    {},
    200,
    'verify Run Project registered the Unit from immutable context');
  if (productionUnit.body?.productModelId !== frozenContext.productModelId
      || productionUnit.body?.identityKey !== frozenContext.productModelIdentityInputKey
      || productionUnit.body?.productModelId === editedDraftProductModelId) {
    throw new Error(
      `Run Project used editable draft identity instead of immutable context: ${JSON.stringify(productionUnit.body)}`);
  }
  await captureSmokeScreenshot('topology-monitor.png');
  await clickByTestId('topology-dimension-3d');
  await waitForExpression(
    '(() => {'
    + ' const systems = Array.from(document.querySelectorAll("[data-testid^=\\"topology-3d-element-\\"].system-shape"));'
    + ' const station = systems.find(element => element.getAttribute("aria-label")?.startsWith("Station 01,"));'
    + ' const childSystem = systems.find(element => element.getAttribute("aria-label")?.startsWith("System 1,"));'
    + ' const viewport = document.querySelector("[data-testid=\\"topology-3d-viewport\\"]");'
    + ' return viewport?.textContent?.includes("Station 01")'
    + '   && station?.getAttribute("data-operational-state") === "Idle"'
    + '   && childSystem?.getAttribute("data-operational-state") === "Idle";'
    + '})()',
    30000,
    '3D monitor to project the exact station and target runtime states');
  await captureSmokeScreenshot('topology-3d-monitor.png');

  await clickByTestId('nav-dashboard');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"operations-workbench\\"]")?.textContent?.includes("Projection connected")'
    + ' && document.querySelector("[data-testid=\\"operations-workbench\\"]")?.textContent?.includes("Line State")'
    + ' && document.querySelector("[data-testid=\\"operations-workbench\\"]")?.textContent?.includes("No matching active runs")'
    + ' && !document.querySelector("[data-testid=\\"production-command-Cancel\\"]"))()',
    30000,
    'Operations workbench to reconnect without exposing controls for a completed Run');
  const emergencyStationSystemId = `${openedApplication.applicationId}.station.1`;
  await waitForExpression(
    `(() => Boolean(document.querySelector('[data-testid="emergency-stop-${emergencyStationSystemId}"]')))()`,
    15000,
    'Operations workbench to expose the independent Station Emergency Stop control');
  await clickByTestId(`emergency-stop-${emergencyStationSystemId}`);
  await waitForExpression(
    `(() => {
      const confirmation = document.querySelector('[data-testid="emergency-stop-confirmation"]');
      const trigger = document.querySelector('[data-testid="confirm-emergency-stop"]');
      return confirmation?.textContent?.includes('independent Station safety channel')
        && confirmation?.textContent?.includes(${JSON.stringify(emergencyStationSystemId)})
        && trigger?.disabled === true;
    })()`,
    10000,
    'Emergency Stop to require an explicit second confirmation for one Station');
  await setInputByTestId('emergency-stop-reason', 'Desktop smoke independent safety confirmation');
  await setInputByTestId('emergency-stop-confirmation-text', emergencyStationSystemId);
  await waitForExpression(
    '(() => document.querySelector(\'[data-testid="confirm-emergency-stop"]\')?.disabled === false)()',
    10000,
    'Emergency Stop confirmation to require the exact Station System ID');
  await captureSmokeScreenshot('emergency-stop-confirmation.png');
  await clickByTestId('cancel-emergency-stop');
  await captureSmokeScreenshot('line-operations.png');

  const traceRecordsAfterRuns = await expectApiStatus(
    `/api/traceability/records?projectId=${encodeURIComponent(openedProject.projectId)}`,
    {},
    200,
    'query trace records after the production run');
  if (traceRecordsAfterRuns.body?.totalCount !== 1
    || !traceRecordsAfterRuns.body?.items?.[0]?.productionRunId
    || traceRecordsAfterRuns.body?.items?.[0]?.traceRecordId
      !== traceRecordsAfterRuns.body?.items?.[0]?.productionRunId
    || traceRecordsAfterRuns.body?.items?.[0]?.operationCount < 2) {
    throw new Error(
      `Expected one production run to create exactly one Operation trace record: ${JSON.stringify(traceRecordsAfterRuns.body)}`);
  }

  await clickByTestId('nav-trace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Production Trace")'
    + ' && Boolean(document.querySelector("[data-testid=\\"trace-result-row\\"]")))()',
    30000,
    'trace workbench to render production run results');
  await clickByTestId('trace-result-row');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("UNIT-")'
    + ' && Boolean(document.querySelector("[data-testid=\\"trace-operation-card\\"]"))'
    + ' && document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("Commands")'
    + ' && document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("Measurements"))()',
    30000,
    'trace detail to load nested production Operation evidence');
  await captureSmokeScreenshot('production-run-trace.png');
  await clickByTestId('trace-export-package');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("openlineops.production-run-trace-package"))()',
    30000,
    'trace export package to load');

  await clickByTestId('nav-engineering');
  await waitForExpression(
    '(() => document.body.innerText.includes("Engineering Configuration")'
    + ' && document.body.innerText.includes("Publish Snapshot")'
    + ' && document.querySelector("[data-testid=\\"new-engineering-configuration\\"]")?.disabled === false)()',
    30000,
    'engineering workbench to render its mutable source actions');
  await clickByTestId('new-engineering-configuration');
  await setSelectByTestId(
    'engineering-device-owner-system',
    `${openedApplication.applicationId}.station.1`);
  await setSelectByTestId('engineering-device-capability', capabilityId);
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"save-engineering-source\\"]")?.disabled === false'
    + ' && document.querySelector("[data-testid=\\"create-engineering-bundle\\"]")?.disabled === true)()',
    15000,
    'new engineering source to become dirty before publication');
  await clickByTestId('save-engineering-source');
  await waitForExpression(
    '(() => document.body.innerText.includes("Engineering source saved ")'
    + ' && document.querySelector("[data-testid=\\"save-engineering-source\\"]")?.disabled === true'
    + ' && document.querySelector("[data-testid=\\"create-engineering-bundle\\"]")?.disabled === false)()',
    45000,
    'engineering source save to complete before snapshot publication');
  await clickByTestId('create-engineering-bundle');
  await waitForExpression(
    '(() => document.body.innerText.includes("Snapshot published ")'
    + ' && document.querySelector("[data-testid=\\"engineering-result\\"]")?.textContent?.includes("Published"))()',
    45000,
    'engineering configuration snapshot to publish from UI');
  await clickByTestId('nav-devices');
  await waitForExpression(
    '(() => document.body.innerText.includes("Device Integration")'
    + ' && document.querySelector("[data-testid=\\"create-device-bundle\\"]")?.disabled === false)()',
    30000,
    'devices workbench to render');
  await clickByTestId('create-device-bundle');
  await waitForExpression(
    '(() => document.body.innerText.includes("Device registered device-instance-desktop-")'
    + ' && document.querySelector("[data-testid=\\"device-status-card\\"]")?.textContent?.includes("Disconnected"))()',
    30000,
    'device bundle to register from UI');
  await clickByTestId('connect-device-instance');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"device-status-card\\"]")?.textContent?.includes("Connected"))()',
    30000,
    'device instance to connect from UI');
  await clickByTestId('nav-plugins');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"plugins-workbench\\"]"))'
    + ' && document.querySelector("[data-testid=\\"extension-manifest-preview\\"]")?.textContent?.includes("Loopback Device Sample"))()',
    30000,
    'primary Application extension to remain scoped after other IDE work');
  const removedGlobalPluginApi = await apiRequest(['', 'api', 'plugins', 'overview'].join('/'));
  if (removedGlobalPluginApi.status !== 404) {
    throw new Error(`Legacy global plugin API still exists: ${removedGlobalPluginApi.text}`);
  }

  if (packagedMode) {
    await assertPackagedRestartPersistence(
      `UNIT-${openedProject.projectId}`,
      rendererNonce);
    await assertBackendExitFailsClosed();
    await assertPackagedPrimaryTerminationStopsBackend();
  } else {
    await assertBackendExitFailsClosed();
  }
  if (apiSquatterAuthorizationHeaders.length !== 0) {
    throw new Error(
      `API squatter received credentials: ${JSON.stringify(apiSquatterAuthorizationHeaders)}`);
  }
  console.log(`${packagedMode ? 'Packaged ' : ''}Electron smoke passed against ${apiBaseUrl}.`);
}

async function seedIncompatiblePackagedRuntimeState() {
  const runtimeStateDirectory = path.join(smokeUserDataDirectory, 'data', 'runtime-state');
  await fs.mkdir(runtimeStateDirectory, { recursive: true });
  await fs.writeFile(
    path.join(runtimeStateDirectory, 'openlineops-traceability.sqlite'),
    'obsolete-runtime-state',
    'utf8');
  await fs.mkdir(path.join(runtimeStateDirectory, 'trace-artifacts'), { recursive: true });
  await fs.writeFile(
    path.join(runtimeStateDirectory, 'trace-artifacts', 'obsolete.bin'),
    'obsolete-artifact',
    'utf8');
  await fs.mkdir(path.join(runtimeStateDirectory, 'external-program-evidence'), { recursive: true });
  await fs.writeFile(
    path.join(runtimeStateDirectory, 'external-program-evidence', 'obsolete.json'),
    'obsolete-evidence',
    'utf8');
}

async function assertPackagedRuntimeDataBinding() {
  const response = await evaluate(
    'window.openlineopsDesktop.apiRequest("/api/traceability/records")');
  if (!response.ok || response.status !== 200) {
    throw new Error(
      `Packaged runtime did not replace incompatible Trace state: ${JSON.stringify(response)}`);
  }
  const runtimeStateDirectory = path.join(smokeUserDataDirectory, 'data', 'runtime-state');
  const markerPath = path.join(runtimeStateDirectory, 'runtime-content-binding.json');
  const activationCommitPath = path.join(
    runtimeStateDirectory,
    'runtime-activation-committed.json');
  const marker = JSON.parse(await fs.readFile(markerPath, 'utf8'));
  const activationCommit = JSON.parse(await fs.readFile(activationCommitPath, 'utf8'));
  const canonicalActivationId = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
  if (Object.keys(marker).sort().join(',') !== 'activationId,runtimeSha256,schema'
      || marker.schema !== 'openlineops.desktop-runtime-content-binding'
      || !/^[a-f0-9]{64}$/u.test(marker.runtimeSha256)
      || !canonicalActivationId.test(marker.activationId)) {
    throw new Error(`Packaged runtime binding marker is invalid: ${JSON.stringify(marker)}`);
  }
  if (Object.keys(activationCommit).sort().join(',') !== 'activationId,runtimeSha256,schema'
      || activationCommit.schema !== 'openlineops.desktop-runtime-activation'
      || activationCommit.runtimeSha256 !== marker.runtimeSha256
      || activationCommit.activationId !== marker.activationId) {
    throw new Error(
      `Packaged runtime activation was not committed after API health: ${JSON.stringify(activationCommit)}`);
  }
  const obsoleteArtifact = await fs.stat(
    path.join(runtimeStateDirectory, 'trace-artifacts', 'obsolete.bin')).catch(() => null);
  if (obsoleteArtifact !== null) {
    throw new Error('Packaged runtime binding preserved an incompatible Trace artifact.');
  }
  const obsoleteExternalEvidence = await fs.stat(
    path.join(runtimeStateDirectory, 'external-program-evidence', 'obsolete.json')).catch(() => null);
  if (obsoleteExternalEvidence !== null) {
    throw new Error('Packaged runtime binding preserved incompatible external-program evidence.');
  }
}

async function assertPackagedRestartPersistence(productionUnitIdentityValue, rendererNonce) {
  const runtimeStateDirectory = path.join(smokeUserDataDirectory, 'data', 'runtime-state');
  const markerPath = path.join(runtimeStateDirectory, 'runtime-content-binding.json');
  const activationCommitPath = path.join(
    runtimeStateDirectory,
    'runtime-activation-committed.json');
  const traceArtifactDirectory = path.join(runtimeStateDirectory, 'trace-artifacts');
  const sentinelPath = path.join(traceArtifactDirectory, 'packaged-restart-sentinel.txt');
  const sentinelContent = `same-package-restart-${randomBytes(16).toString('hex')}`;
  const tracePath = `/api/traceability/records?productionUnitIdentityValue=${encodeURIComponent(productionUnitIdentityValue)}`;
  const traceBeforeRestart = await expectApiStatus(
    tracePath,
    {},
    200,
    'query packaged Trace before restarting the same package');
  const traceRecordBeforeRestart = traceBeforeRestart.body?.items?.find(
    item => item.productionUnitIdentityValue === productionUnitIdentityValue);
  if (!traceRecordBeforeRestart?.traceRecordId) {
    throw new Error(
      `Packaged Trace did not contain ${productionUnitIdentityValue} before restart: ${traceBeforeRestart.text}`);
  }
  const markerBeforeRestart = await fs.readFile(markerPath, 'utf8');
  const activationCommitBeforeRestart = await fs.readFile(activationCommitPath, 'utf8');
  await fs.mkdir(traceArtifactDirectory, { recursive: true });
  await fs.writeFile(sentinelPath, sentinelContent, 'utf8');

  await closeElectronForPackagedRestart();
  const restartCdpPort = await getFreePort();
  electronProcess = spawnLogged(
    packagedExecutable,
    packagedElectronArguments(restartCdpPort),
    {
      cwd: path.dirname(packagedExecutable),
      env: packagedElectronEnvironment(rendererNonce)
    },
    'electron-restart');
  const target = await waitForCdpTarget(restartCdpPort, null, 30000);
  cdp = await CdpClient.connect(target.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');
  await cdp.send('Page.enable');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"automation-ide-shell\\"]"))'
    + ' && Boolean(window.openlineopsDesktop))()',
    30000,
    'same packaged desktop to render after restart');
  await evaluate('window.__openlineopsSmokeEvents = {}');
  await ensureBackendStarted();
  await waitForHealthyBackend();

  const markerAfterRestart = await fs.readFile(markerPath, 'utf8');
  const activationCommitAfterRestart = await fs.readFile(activationCommitPath, 'utf8');
  const sentinelAfterRestart = await fs.readFile(sentinelPath, 'utf8');
  if (markerAfterRestart !== markerBeforeRestart) {
    throw new Error(
      `Same-package restart rewrote the runtime binding marker: ${markerBeforeRestart} -> ${markerAfterRestart}`);
  }
  if (activationCommitAfterRestart !== activationCommitBeforeRestart) {
    throw new Error(
      'Same-package restart rewrote the committed runtime activation identity.');
  }
  if (sentinelAfterRestart !== sentinelContent) {
    throw new Error(
      `Same-package restart did not preserve the Trace artifact sentinel: ${sentinelAfterRestart}`);
  }
  const traceAfterRestart = await expectApiStatus(
    tracePath,
    {},
    200,
    'query packaged Trace after restarting the same package');
  const traceRecordAfterRestart = traceAfterRestart.body?.items?.find(
    item => item.productionUnitIdentityValue === productionUnitIdentityValue);
  if (traceRecordAfterRestart?.traceRecordId !== traceRecordBeforeRestart.traceRecordId) {
    throw new Error(
      `Same-package restart lost or replaced Trace evidence: ${JSON.stringify({
        before: traceRecordBeforeRestart,
        after: traceRecordAfterRestart
      })}`);
  }

  const primaryBackend = await getBackendStatus();
  if (!primaryBackend.isRunning
      || primaryBackend.health !== 'Healthy'
      || !Number.isSafeInteger(primaryBackend.pid)
      || primaryBackend.pid <= 0) {
    throw new Error(
      `Restarted packaged primary instance has no healthy backend identity: ${JSON.stringify(primaryBackend)}`);
  }
  const secondInstanceCdpPort = await getFreePort();
  const secondInstance = spawnLogged(
    packagedExecutable,
    packagedElectronArguments(secondInstanceCdpPort),
    {
      cwd: path.dirname(packagedExecutable),
      env: packagedElectronEnvironment(rendererNonce)
    },
    'electron-second-instance');
  try {
    const secondInstanceExited = await waitForChildExit(secondInstance, 10000);
    if (!secondInstanceExited) {
      throw new Error('A second packaged instance with the same userData did not exit promptly.');
    }
  } finally {
    await stopChild(secondInstance);
  }
  const backendAfterSecondInstance = await getBackendStatus();
  if (!backendAfterSecondInstance.isRunning
      || backendAfterSecondInstance.health !== 'Healthy'
      || backendAfterSecondInstance.pid !== primaryBackend.pid) {
    throw new Error(
      `Second packaged instance replaced or disrupted the primary backend: ${JSON.stringify({
        primaryBackend,
        backendAfterSecondInstance
      })}`);
  }
  if (await fs.readFile(markerPath, 'utf8') !== markerBeforeRestart
      || await fs.readFile(activationCommitPath, 'utf8') !== activationCommitBeforeRestart
      || await fs.readFile(sentinelPath, 'utf8') !== sentinelContent) {
    throw new Error('Second packaged instance changed the primary runtime-state content.');
  }
}

async function closeElectronForPackagedRestart() {
  if (!cdp || !electronProcess) {
    throw new Error('Packaged restart requires an active Electron CDP session and process.');
  }
  const closingCdp = cdp;
  const closingProcess = electronProcess;
  let closeRequestError = null;
  try {
    await withTimeout(
      closingCdp.send('Browser.close'),
      5000,
      'packaged Electron browser close before restart');
  } catch (error) {
    closeRequestError = error;
  } finally {
    closingCdp.close();
    cdp = undefined;
  }
  if (!await waitForChildExit(closingProcess, 15000)) {
    throw new Error(
      'Packaged Electron did not exit cleanly before same-package restart.'
      + (closeRequestError ? ` Browser.close: ${String(closeRequestError)}` : ''));
  }
  electronProcess = undefined;
}

async function assertPackagedPrimaryTerminationStopsBackend() {
  if (!cdp || !electronProcess) {
    throw new Error('Packaged parent-death test requires an active Electron CDP session and process.');
  }
  await ensureBackendStarted();
  await waitForHealthyBackend();
  const backendBeforeTermination = await getBackendStatus();
  if (!backendBeforeTermination.isRunning
      || backendBeforeTermination.health !== 'Healthy'
      || !Number.isSafeInteger(backendBeforeTermination.pid)
      || backendBeforeTermination.pid <= 0) {
    throw new Error(
      `Packaged parent-death test has no healthy backend PID: ${JSON.stringify(backendBeforeTermination)}`);
  }

  const terminatedElectron = electronProcess;
  const terminatedElectronPid = terminatedElectron.pid;
  if (!Number.isSafeInteger(terminatedElectronPid) || terminatedElectronPid <= 0) {
    throw new Error(`Packaged Electron has no process identity: ${terminatedElectronPid}`);
  }
  cdp.close();
  cdp = undefined;
  const terminationRequested = terminatedElectron.kill('SIGKILL');
  if (!terminationRequested && terminatedElectron.exitCode === null) {
    throw new Error(`Failed to strongly terminate packaged Electron PID ${terminatedElectronPid}.`);
  }
  if (!await waitForChildExit(terminatedElectron, 10000)) {
    throw new Error(`Strongly terminated packaged Electron PID ${terminatedElectronPid} did not exit.`);
  }
  electronProcess = undefined;

  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    if (!await isWindowsProcessRunning(backendBeforeTermination.pid)) {
      return;
    }
    await delay(100);
  }
  await killProcessTreeByPid(backendBeforeTermination.pid).catch(() => undefined);
  throw new Error(
    `Backend PID ${backendBeforeTermination.pid} survived strong termination of Electron PID ${terminatedElectronPid}.`);
}

function packagedElectronArguments(cdpPort) {
  return [
    `--remote-debugging-port=${cdpPort}`,
    '--disable-gpu',
    `--user-data-dir=${smokeUserDataDirectory}`
  ];
}

function packagedElectronEnvironment(rendererNonce) {
  return {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',
    OPENLINEOPS_API_BASE_URL: apiSquatterBaseUrl,
    OPENLINEOPS_RENDERER_NONCE: rendererNonce,
    OPENLINEOPS_E2E_ALLOW_EXTENSION_DIALOG_BYPASS: '1',
    OPENLINEOPS_E2E_EXTENSION_ARCHIVE_PATH: sampleExtensionArchive.archivePath,
    OPENLINEOPS_REPO_ROOT: repoRoot
  };
}

async function exerciseTopologyDraftLifecycle({
  projectPath,
  projectId,
  applicationId,
  topologyId,
  topologyBasePath,
  layoutId,
  capabilityAId,
  driverBindingId,
  stationSystemId
}) {
  const capabilityBId = `${applicationId}.motion.axis.home`;
  const capabilityDraftId = `${applicationId}.vision.inspect`;
  const discardedCapabilityId = `${applicationId}.discarded.capability`;
  const savedStationDisplayName = 'Station Save All Verified';
  const savedDriverProviderKey = 'simulator.axis.home';
  const savedGeometryX = 74;
  const layoutPath = `/api/automation-projects/${encodeURIComponent(projectId)}`
    + `/applications/${encodeURIComponent(applicationId)}`
    + `/layouts/${encodeURIComponent(layoutId)}`;

  const [baselineTopologyResponse, baselineLayoutResponse] = await Promise.all([
    expectApiStatus(topologyBasePath, {}, 200, 'load baseline topology before draft lifecycle'),
    expectApiStatus(layoutPath, {}, 200, 'load baseline layout before draft lifecycle')
  ]);
  const baselineStation = baselineTopologyResponse.body?.systems?.find(
    system => system.systemId === stationSystemId);
  const baselineDriver = baselineTopologyResponse.body?.driverBindings?.find(
    binding => binding.bindingId === driverBindingId);
  const baselineElement = baselineLayoutResponse.body?.elements?.find(
    element => element.target?.targetId === stationSystemId);
  if (!baselineStation || !baselineDriver || !baselineElement) {
    throw new Error('The disposable topology baseline is incomplete before draft lifecycle testing.');
  }

  await clickTopologyHierarchyTarget('Station 01');
  await waitForExpression(
    `(() => document.querySelector('[data-testid="topology-property-display-name"]')?.value === 'Station 01'`
    + ` && Boolean(document.querySelector('[data-testid="system-requires-${capabilityAId}"]')))()`,
    15000,
    'Station System properties to be selected for draft lifecycle');

  await ensureCapabilityContractForm();
  await setInputByTestId('capability-contract-id', capabilityBId);
  await setInputByTestId('capability-contract-command', 'HomeAxis');
  await setInputByTestId('capability-contract-version', '1.0.0');
  await setInputByTestId('capability-contract-timeout', '20');
  await setSelectByTestId('capability-contract-safety', 'Motion');
  await setTextAreaByTestId(
    'capability-contract-input-schema',
    '{"type":"object","properties":{"axis":{"type":"string"}}}');
  await setTextAreaByTestId(
    'capability-contract-output-schema',
    '{"type":"object","properties":{"homed":{"type":"boolean"}}}');
  await clickByTestId('create-capability-contract');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`Created Capability ${capabilityBId}`)})`
    + ` && document.querySelector('[data-testid="topology-capability-editor"]')?.textContent?.includes(${JSON.stringify(capabilityBId)})`
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Saved")()',
    30000,
    'second Capability contract to be created through the real editor');

  await setCheckboxByTestId(`system-requires-${capabilityBId}`, true);
  await setCheckboxByTestId(`system-provides-${capabilityBId}`, true);
  await clickByTestId('save-topology-target');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`Properties saved ${stationSystemId}`)})`
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Saved")()',
    30000,
    'Station to persist both Driver migration capabilities before the multi-draft edit');

  const preparedTopology = await expectApiStatus(
    topologyBasePath,
    {},
    200,
    'load topology prepared for the multi-draft edit');
  const preparedStation = preparedTopology.body?.systems?.find(
    system => system.systemId === stationSystemId);
  const preparedDriver = preparedTopology.body?.driverBindings?.find(
    binding => binding.bindingId === driverBindingId);
  if (!preparedStation?.requiredCapabilityIds?.includes(capabilityAId)
      || !preparedStation?.requiredCapabilityIds?.includes(capabilityBId)
      || !preparedStation?.providedCapabilityIds?.includes(capabilityAId)
      || !preparedStation?.providedCapabilityIds?.includes(capabilityBId)
      || preparedDriver?.capabilityId !== capabilityAId) {
    throw new Error(
      `Topology was not prepared for an atomic Driver A to B migration: ${preparedTopology.text}`);
  }

  await clickDriverBinding(driverBindingId);
  await ensureCapabilityContractForm();
  await setInputByTestId('capability-contract-id', capabilityDraftId);
  await setInputByTestId('capability-contract-command', 'InspectImage');
  await setInputByTestId('capability-contract-version', '2.1.0');
  await setInputByTestId('capability-contract-timeout', '45');
  await setSelectByTestId('capability-contract-safety', 'Normal');
  await setTextAreaByTestId(
    'capability-contract-output-schema',
    '{"type":"object","required":["passed"],"properties":{"passed":{"type":"boolean"}}}');
  await setInputByTestId('topology-property-display-name', savedStationDisplayName);
  await setCheckboxByTestId(`system-requires-${capabilityAId}`, false);
  await setCheckboxByTestId(`system-provides-${capabilityAId}`, false);
  await setSelectByTestId('driver-binding-capability', capabilityBId);
  await setInputByTestId('driver-binding-provider-key', savedDriverProviderKey);
  await setInputByTestId('layout-geometry-x', String(savedGeometryX));
  await waitForExpression(
    '(() => document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (4)"'
    + ' && document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === false'
    + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")))()',
    15000,
    'Capability, semantic System, Driver, and geometry drafts to be dirty together');

  await clickByTestId('nav-projects');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"editor-tab-projects\\"]")?.getAttribute("aria-selected") === "true"'
    + ' && document.querySelector("[data-testid=\\"topology-workbench\\"]")?.closest(".ide-editor-document")?.getAttribute("aria-hidden") === "true"'
    + ' && document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === false)()',
    15000,
    'Topology editor to remain mounted but hidden behind the Projects tab');
  await clickByTestId('save-all-editors');
  await waitForExpression(
    '(() => document.body.innerText.includes("All open editors saved.")'
    + ' && document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === true'
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge"))()',
    45000,
    'Save All to persist all four drafts from the hidden Topology tab');

  const [savedTopology, savedLayout, savedStorage] = await Promise.all([
    expectApiStatus(topologyBasePath, {}, 200, 'load topology after hidden Save All'),
    expectApiStatus(layoutPath, {}, 200, 'load layout after hidden Save All'),
    readApplicationTopologyStorage(projectPath, applicationId, topologyId, layoutId)
  ]);
  assertSavedTopologyDrafts({
    topology: savedTopology.body,
    layout: savedLayout.body,
    diskTopology: savedStorage.topologyDocument,
    diskLayout: savedStorage.layoutDocument,
    stationSystemId,
    driverBindingId,
    capabilityAId,
    capabilityBId,
    capabilityDraftId,
    savedStationDisplayName,
    savedDriverProviderKey,
    savedGeometryX
  });

  await clickByTestId('editor-tab-topology');
  await waitForExpression(
    `(() => document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)}`
    + ` && Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value) === ${savedGeometryX})()`,
    15000,
    'saved topology values to render after returning to the Topology tab');
  await clickDriverBinding(driverBindingId);
  await waitForExpression(
    `(() => document.querySelector('[data-testid="driver-binding-capability"]')?.value === ${JSON.stringify(capabilityBId)}`
    + ` && document.querySelector('[data-testid="driver-binding-provider-key"]')?.value === ${JSON.stringify(savedDriverProviderKey)})()`,
    15000,
    'migrated Driver B draft to reload from persisted topology');

  await exerciseTopologyExternalConflicts({
    topologyBasePath,
    layoutPath,
    stationSystemId,
    savedStationDisplayName,
    savedGeometryX
  });

  await ensureCapabilityContractForm();
  await setInputByTestId('capability-contract-id', discardedCapabilityId);
  await setInputByTestId('capability-contract-command', 'MustNotPersist');
  await setInputByTestId('topology-property-display-name', 'Discarded Station Name');
  await setInputByTestId('topology-property-system-type', 'discarded.station.type');
  await setInputByTestId('driver-binding-provider-key', 'discarded.provider.key');
  await setInputByTestId('layout-geometry-x', '137');
  await waitForExpression(
    'document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (4)"',
    15000,
    'four disposable topology drafts to be registered');
  await clickByTestId('discard-topology-drafts');
  await waitForExpression(
    `(() => document.querySelector('.topology-save-state')?.textContent?.trim() === 'Saved'`
    + ` && document.querySelector('[data-testid="capability-contract-id"]')?.value !== ${JSON.stringify(discardedCapabilityId)}`
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)}`
    + ' && document.querySelector("[data-testid=\\"topology-property-system-type\\"]")?.value === "automation.station"'
    + ` && document.querySelector('[data-testid="driver-binding-provider-key"]')?.value === ${JSON.stringify(savedDriverProviderKey)}`
    + ` && Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value) === ${savedGeometryX})()`,
    30000,
    'Discard Drafts to restore every persisted topology field');

  const storageAfterDiscard = await readApplicationTopologyStorage(
    projectPath,
    applicationId,
    topologyId,
    layoutId);
  if (storageAfterDiscard.topologyText !== savedStorage.topologyText
      || storageAfterDiscard.layoutText !== savedStorage.layoutText) {
    throw new Error('Discard Drafts changed persisted topology or layout files.');
  }

  await setInputByTestId('topology-property-display-name', 'Guard Unsaved Station');
  await waitForExpression(
    'document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (1)"',
    15000,
    'unsaved topology edit before opening runtime operations');
  await clickByTestId('nav-dashboard');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("Open runtime operations?")'
    + ' && document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("2D Layout")'
    + ' && document.querySelector("[data-testid=\\"editor-tab-topology\\"]")?.getAttribute("aria-selected") === "true")()',
    15000,
    'runtime operations navigation to trigger the global unsaved guard');
  await clickByTestId('unsaved-cancel');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")'
    + ' && document.querySelector("[data-testid=\\"editor-tab-topology\\"]")?.getAttribute("aria-selected") === "true"'
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (1)")()',
    15000,
    'canceling the runtime unsaved guard to preserve the topology draft');
  await clickByTestId('discard-topology-drafts');
  await waitForExpression(
    `(() => document.querySelector('.topology-save-state')?.textContent?.trim() === 'Saved'`
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)})()`,
    30000,
    'guarded topology draft to be explicitly discarded');

  const storageAfterGuard = await readApplicationTopologyStorage(
    projectPath,
    applicationId,
    topologyId,
    layoutId);
  if (storageAfterGuard.topologyText !== savedStorage.topologyText
      || storageAfterGuard.layoutText !== savedStorage.layoutText) {
    throw new Error('Canceling the runtime unsaved guard changed persisted topology files.');
  }

  await setInputByTestId('topology-property-display-name', 'Failed Discard Must Survive');
  await waitForExpression(
    'document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (1)"',
    15000,
    'topology draft before a forced reload failure');
  const unavailableTopologyPath = `${savedStorage.topologyPath}.smoke-unavailable`;
  await fs.rename(savedStorage.topologyPath, unavailableTopologyPath);
  try {
    await clickByTestId('close-editor-tab-topology');
    await waitForExpression(
      'document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("Close 2D Layout?")',
      15000,
      'dirty Topology close guard while its persisted source is unavailable');
    await clickByTestId('unsaved-discard');
    await waitForExpression(
      '(() => document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("Close 2D Layout?")'
      + ' && document.body.innerText.includes("Discard failed. The editor remains open with its draft intact.")'
      + ' && document.querySelector("[data-testid=\\"topology-property-display-name\\"]")?.value === "Failed Discard Must Survive"'
      + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge"))'
      + ' && document.querySelector("[data-testid=\\"unsaved-discard\\"]")?.disabled === false)()',
      15000,
      'failed global discard to keep the dialog, tab, and in-memory topology draft intact');
    await clickByTestId('unsaved-cancel');
  } finally {
    await fs.rename(unavailableTopologyPath, savedStorage.topologyPath)
      .catch(async error => {
        if (await fs.stat(savedStorage.topologyPath).catch(() => null)) return;
        throw error;
      });
  }
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"topology-property-display-name\\"]")?.value === "Failed Discard Must Survive"'
    + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")))()',
    15000,
    'failed-discard topology draft to survive persisted source recovery');
  await clickByTestId('discard-topology-drafts');
  await waitForExpression(
    `(() => document.querySelector('.topology-save-state')?.textContent?.trim() === 'Saved'`
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)})()`,
    30000,
    'topology draft to reload successfully after backend recovery');

  await setInputByTestId('topology-property-display-name', 'Global Discard Station');
  await waitForExpression(
    '(() => document.querySelector(".topology-save-state")?.textContent?.trim() === "Unsaved (1)"'
    + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")))()',
    15000,
    'topology draft before closing its editor tab');
  await clickByTestId('close-editor-tab-topology');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("Close 2D Layout?")'
    + ' && document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")?.textContent?.includes("Global Discard Station") === false)()',
    15000,
    'closing the dirty Topology tab to trigger its scoped unsaved guard');
  await clickByTestId('unsaved-discard');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"unsaved-changes-dialog\\"]")'
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"]")'
    + ' && document.querySelector("[data-testid=\\"editor-tab-projects\\"]")?.getAttribute("aria-selected") === "true")()',
    30000,
    'global Discard Changes to revert and close the Topology editor tab');
  const storageAfterGlobalDiscard = await readApplicationTopologyStorage(
    projectPath,
    applicationId,
    topologyId,
    layoutId);
  if (storageAfterGlobalDiscard.topologyText !== savedStorage.topologyText
      || storageAfterGlobalDiscard.layoutText !== savedStorage.layoutText) {
    throw new Error('Global Discard Changes modified persisted topology files.');
  }

  await clickByTestId('nav-topology');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"]"))'
    + ' && Boolean(document.querySelector("[data-testid=\\"topology-workbench\\"]"))'
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Saved")()',
    30000,
    'Topology editor to reopen after global discard');
  await clickTopologyHierarchyTarget(savedStationDisplayName);
  await waitForExpression(
    `(() => document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)}`
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")'
    + ' && document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === true)()',
    15000,
    'reopened Topology editor to contain only persisted values with no draft residue');

  await setInputByTestId('topology-property-display-name', baselineStation.displayName);
  await setCheckboxByTestId(`system-requires-${capabilityAId}`, true);
  await setCheckboxByTestId(`system-provides-${capabilityAId}`, true);
  await clickByTestId('save-topology-target');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`Properties saved ${stationSystemId}`)})`
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Saved")()',
    30000,
    'baseline Capability to be restored on the Station before migrating its Driver');

  await clickDriverBinding(driverBindingId);
  await setSelectByTestId('driver-binding-capability', baselineDriver.capabilityId);
  await setSelectByTestId('driver-binding-provider-kind', baselineDriver.providerKind);
  await setInputByTestId('driver-binding-provider-key', baselineDriver.providerKey);
  await clickByTestId('save-driver-binding');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`Updated Driver binding ${driverBindingId}`)})`
    + ' && document.querySelector(".topology-save-state")?.textContent?.trim() === "Saved")()',
    30000,
    'Driver binding to migrate back to the baseline Capability');

  await setCheckboxByTestId(`system-requires-${capabilityBId}`, false);
  await setCheckboxByTestId(`system-provides-${capabilityBId}`, false);
  await setInputByTestId('layout-geometry-x', String(baselineElement.x));
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === false'
    + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")))()',
    15000,
    'baseline topology restoration drafts to become dirty');
  await clickByTestId('save-all-editors');
  await waitForExpression(
    `(() => document.querySelector('.topology-save-state')?.textContent?.trim() === 'Saved'`
    + ' && document.querySelector("[data-testid=\\"save-all-editors\\"]")?.disabled === true'
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(baselineStation.displayName)})()`,
    45000,
    'draft lifecycle fixture to restore its downstream topology baseline');
  const [restoredTopologyResponse, restoredLayoutResponse] = await Promise.all([
    expectApiStatus(topologyBasePath, {}, 200, 'load restored topology baseline'),
    expectApiStatus(layoutPath, {}, 200, 'load restored layout baseline')
  ]);
  const restoredStation = restoredTopologyResponse.body?.systems?.find(
    system => system.systemId === stationSystemId);
  const restoredDriver = restoredTopologyResponse.body?.driverBindings?.find(
    binding => binding.bindingId === driverBindingId);
  const restoredElement = restoredLayoutResponse.body?.elements?.find(
    element => element.target?.targetId === stationSystemId);
  if (restoredStation?.displayName !== baselineStation.displayName
      || JSON.stringify(restoredStation.requiredCapabilityIds)
        !== JSON.stringify(baselineStation.requiredCapabilityIds)
      || JSON.stringify(restoredStation.providedCapabilityIds)
        !== JSON.stringify(baselineStation.providedCapabilityIds)
      || restoredDriver?.capabilityId !== baselineDriver.capabilityId
      || restoredDriver?.providerKind !== baselineDriver.providerKind
      || restoredDriver?.providerKey !== baselineDriver.providerKey
      || restoredElement?.x !== baselineElement.x) {
    throw new Error('The topology draft lifecycle did not restore its downstream fixture baseline.');
  }
}

async function exerciseTopologyExternalConflicts({
  topologyBasePath,
  layoutPath,
  stationSystemId,
  savedStationDisplayName,
  savedGeometryX
}) {
  const mutateSystemName = async displayName => {
    const current = await expectApiStatus(topologyBasePath, {}, 200, 'load external Topology revision');
    const station = current.body?.systems?.find(system => system.systemId === stationSystemId);
    if (!station) throw new Error(`External Topology mutation cannot find ${stationSystemId}.`);
    return expectApiStatus(
      `${topologyBasePath}/systems/${encodeURIComponent(stationSystemId)}`,
      {
        method: 'PATCH',
        headers: { 'If-Match': `"${current.body.revision}"` },
        body: {
          displayName,
          systemType: station.systemType,
          requiredCapabilityIds: station.requiredCapabilityIds,
          providedCapabilityIds: station.providedCapabilityIds,
          metadata: station.metadata
        }
      },
      200,
      'mutate Topology outside the mounted editor');
  };
  const mutateGeometryX = async x => {
    const current = await expectApiStatus(layoutPath, {}, 200, 'load external Layout revision');
    const element = current.body?.elements?.find(candidate => candidate.target?.targetId === stationSystemId);
    if (!element) throw new Error(`External Layout mutation cannot find ${stationSystemId}.`);
    return expectApiStatus(
      `${layoutPath}/elements/${encodeURIComponent(element.elementId)}/geometry`,
      {
        method: 'PUT',
        headers: { 'If-Match': `"${current.body.revision}"` },
        body: {
          x,
          y: element.y,
          width: element.width,
          height: element.height,
          rotationDegrees: element.rotationDegrees
        }
      },
      200,
      'mutate Layout outside the mounted editor');
  };

  await setInputByTestId('topology-property-display-name', 'Topology Draft Preserved For Reload');
  await mutateSystemName('Externally Reloaded Station');
  await clickByTestId('save-topology-target');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"editor-external-conflict\\"]"))'
    + ' && document.querySelector("[data-testid=\\"topology-property-display-name\\"]")?.value === "Topology Draft Preserved For Reload"'
    + ' && Boolean(document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge")))()',
    30000,
    'stale Topology revision to preserve the semantic editor draft');
  await clickByTestId('conflict-reload');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"editor-external-conflict\\"]")'
    + ' && document.querySelector("[data-testid=\\"topology-property-display-name\\"]")?.value === "Externally Reloaded Station"'
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge"))()',
    30000,
    'Topology conflict Reload to synchronize revision and dirty state');

  await setInputByTestId('topology-property-display-name', savedStationDisplayName);
  await mutateSystemName('External Station Before Overwrite');
  await clickByTestId('save-topology-target');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"editor-external-conflict\\"]"))'
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)})()`,
    30000,
    'second stale Topology revision before explicit overwrite');
  await clickByTestId('conflict-keep-editor');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"editor-external-conflict\\"]")'
    + ` && document.querySelector('[data-testid="topology-property-display-name"]')?.value === ${JSON.stringify(savedStationDisplayName)}`
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge"))()',
    30000,
    'Topology Keep Editor and Overwrite to persist the mounted draft');

  await setInputByTestId('layout-geometry-x', String(savedGeometryX));
  await mutateGeometryX(savedGeometryX + 13);
  await clickByTestId('save-layout-geometry');
  await waitForExpression(
    `(() => Boolean(document.querySelector('[data-testid="editor-external-conflict"]'))`
    + ` && Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value) === ${savedGeometryX})()`,
    30000,
    'stale Layout revision to preserve the geometry editor draft');
  await clickByTestId('conflict-keep-editor');
  await waitForExpression(
    `(() => !document.querySelector('[data-testid="editor-external-conflict"]')`
    + ` && Number(document.querySelector('[data-testid="layout-geometry-x"]')?.value) === ${savedGeometryX}`
    + ' && !document.querySelector("[data-testid=\\"editor-tab-topology\\"] .editor-dirty-badge"))()',
    30000,
    'Layout Keep Editor and Overwrite to synchronize revision and dirty state');
}

function assertSavedTopologyDrafts({
  topology,
  layout,
  diskTopology,
  diskLayout,
  stationSystemId,
  driverBindingId,
  capabilityAId,
  capabilityBId,
  capabilityDraftId,
  savedStationDisplayName,
  savedDriverProviderKey,
  savedGeometryX
}) {
  const station = topology?.systems?.find(system => system.systemId === stationSystemId);
  const driver = topology?.driverBindings?.find(binding => binding.bindingId === driverBindingId);
  const element = layout?.elements?.find(candidate => candidate.target?.targetId === stationSystemId);
  const diskStation = diskTopology?.systems?.find(system => system.systemId === stationSystemId);
  const diskDriver = diskTopology?.driverBindings?.find(binding => binding.bindingId === driverBindingId);
  const diskElement = diskLayout?.elements?.find(candidate => candidate.target?.targetId === stationSystemId);
  const capabilityIds = topology?.capabilities?.map(capability => capability.capabilityId) ?? [];
  const diskCapabilityIds = diskTopology?.capabilities?.map(capability => capability.capabilityId) ?? [];
  const stationCapabilitiesAreMigrated = value => (
    value?.requiredCapabilityIds?.length === 1
    && value.requiredCapabilityIds[0] === capabilityBId
    && value?.providedCapabilityIds?.length === 1
    && value.providedCapabilityIds[0] === capabilityBId);
  if (topology?.topologyId !== diskTopology?.topologyId
      || diskTopology?.schemaVersion !== 'openlineops.automation-topology'
      || diskLayout?.schemaVersion !== 'openlineops.site-layout'
      || station?.displayName !== savedStationDisplayName
      || diskStation?.displayName !== savedStationDisplayName
      || !stationCapabilitiesAreMigrated(station)
      || !stationCapabilitiesAreMigrated(diskStation)
      || capabilityIds.includes(capabilityAId) === false
      || !capabilityIds.includes(capabilityBId)
      || !capabilityIds.includes(capabilityDraftId)
      || !diskCapabilityIds.includes(capabilityDraftId)
      || driver?.capabilityId !== capabilityBId
      || diskDriver?.capabilityId !== capabilityBId
      || driver?.providerKey !== savedDriverProviderKey
      || diskDriver?.providerKey !== savedDriverProviderKey
      || Number(element?.x) !== savedGeometryX
      || Number(diskElement?.x) !== savedGeometryX) {
    throw new Error(`Hidden Topology Save All did not persist the complete multi-draft transaction: ${JSON.stringify({
      topology,
      layout,
      diskTopology,
      diskLayout
    })}`);
  }
}

async function assertBackendExitFailsClosed() {
  const running = await getBackendStatus();
  if (!Number.isSafeInteger(running.pid) || running.pid <= 0) {
    throw new Error(`Healthy backend did not expose a process identity: ${JSON.stringify(running)}`);
  }
  await killProcessTreeByPid(running.pid);

  const deadline = Date.now() + 15000;
  let stopped;
  while (Date.now() < deadline) {
    stopped = await getBackendStatus();
    if (!stopped.isRunning && stopped.pid === null && stopped.apiBaseUrl === null) {
      break;
    }
    await delay(100);
  }
  if (!stopped || stopped.isRunning || stopped.pid !== null || stopped.apiBaseUrl !== null) {
    throw new Error(`Backend exit did not revoke its active session: ${JSON.stringify(stopped)}`);
  }

  const refused = await evaluate(
    'window.openlineopsDesktop.apiRequest("/api/platform")');
  if (refused.ok || refused.status !== 0 || !refused.text.includes('No authenticated local API')) {
    throw new Error(`API request was not refused after backend exit: ${JSON.stringify(refused)}`);
  }
  const configResult = await evaluate(`window.openlineopsDesktop.getConfig()
    .then(() => ({ exposed: true, message: '' }))
    .catch(error => ({ exposed: false, message: String(error?.message ?? error) }))`);
  if (configResult.exposed || !configResult.message.includes('No authenticated local API')) {
    throw new Error(`Desktop config remained exposed after backend exit: ${JSON.stringify(configResult)}`);
  }
}

async function killProcessTreeByPid(pid) {
  if (process.platform !== 'win32') {
    throw new Error('Electron security smoke requires Windows process-tree termination.');
  }
  await new Promise((resolve, reject) => {
    const child = spawn(windowsSystemExecutablePath('taskkill.exe'), ['/pid', String(pid), '/t', '/f'], {
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });
    let errorOutput = '';
    child.stderr.on('data', chunk => {
      errorOutput += chunk.toString();
    });
    child.once('error', reject);
    child.once('exit', code => code === 0
      ? resolve()
      : reject(new Error(`taskkill failed with code ${code}: ${errorOutput.trim()}`)));
  });
}

async function isWindowsProcessRunning(pid) {
  if (process.platform !== 'win32') {
    throw new Error('Packaged parent-death inspection requires Windows.');
  }
  return new Promise((resolve, reject) => {
    const child = spawn(
      windowsSystemExecutablePath('tasklist.exe'),
      ['/fi', `PID eq ${pid}`, '/fo', 'csv', '/nh'],
      {
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true
      });
    let output = '';
    let errorOutput = '';
    child.stdout.on('data', chunk => {
      output += chunk.toString();
    });
    child.stderr.on('data', chunk => {
      errorOutput += chunk.toString();
    });
    child.once('error', reject);
    child.once('exit', code => {
      if (code !== 0) {
        reject(new Error(`tasklist failed with code ${code}: ${errorOutput.trim()}`));
        return;
      }
      const running = output.split(/\r?\n/u).some(line => {
        const match = /^"[^"]+","(\d+)"/u.exec(line.trim());
        return match !== null && Number(match[1]) === pid;
      });
      resolve(running);
    });
  });
}

async function waitForNoPluginHostProcesses() {
  const deadline = Date.now() + 15000;
  let lastCount = -1;
  while (Date.now() < deadline) {
    lastCount = await countPluginHostProcesses();
    if (lastCount === 0) {
      return;
    }
    await delay(200);
  }
  throw new Error(`Plugin provider trial left ${lastCount} PluginHost process(es) running.`);
}

async function countPluginHostProcesses() {
  return new Promise((resolve, reject) => {
    const powerShellHost = createWindowsPowerShellHost();
    const child = spawn(
      powerShellHost.executablePath,
      [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        '@(Get-CimInstance Win32_Process | Where-Object { '
          + '($_.Name -ieq "dotnet.exe" -or $_.Name -ieq "OpenLineOps.PluginHost.exe") '
          + '-and $_.CommandLine -like "*--openlineops-plugin-host*" }).Count'
      ],
      {
        env: powerShellHost.environment,
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true
      });
    let output = '';
    let errorOutput = '';
    child.stdout.on('data', chunk => { output += chunk.toString(); });
    child.stderr.on('data', chunk => { errorOutput += chunk.toString(); });
    child.once('error', reject);
    child.once('exit', code => {
      if (code !== 0) {
        reject(new Error(`PluginHost process inspection failed: ${errorOutput.trim()}`));
        return;
      }
      const count = Number.parseInt(output.trim(), 10);
      if (!Number.isSafeInteger(count) || count < 0) {
        reject(new Error(`PluginHost process inspection returned '${output.trim()}'.`));
        return;
      }
      resolve(count);
    });
  });
}

async function waitForHealthyBackend() {
  const deadline = Date.now() + 90000;
  let lastStatus;
  let lastPageState;

  while (Date.now() < deadline) {
    lastStatus = await getBackendStatus();
    if (lastStatus.lastExitCode !== null) {
      throw new Error(`Backend exited before becoming healthy: ${JSON.stringify(lastStatus, null, 2)}`);
    }

    if (lastStatus.health === 'Healthy') {
      await clickByTestId('refresh-backend');
      await waitForExpression(
        `(() => {
          const refreshButton = document.querySelector('[data-testid="refresh-backend"]');
          return document.body.innerText.includes("Runtime Healthy") && refreshButton?.disabled === false;
        })()`,
        15000,
        'renderer to reflect healthy backend');
      return;
    }

      lastPageState = await getPageState();
    if (lastPageState.refreshDisabled === false) {
      await clickByTestId('refresh-backend');
    }

    await delay(1500);
  }

  throw new Error(
    `Timed out waiting for backend health.\nBackend: ${JSON.stringify(lastStatus, null, 2)}\nPage: ${JSON.stringify(lastPageState, null, 2)}`);
}

async function ensureBackendStarted() {
  const status = await getBackendStatus();
  if (status.isRunning) {
    return;
  }

  const started = await evaluate('window.openlineopsDesktop.startBackend()');
  if (!started.isRunning) {
    throw new Error(`Backend could not be started: ${JSON.stringify(started, null, 2)}`);
  }
}

async function clickByTestId(testId) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!element) {
      throw new Error('Missing element: ${testId}');
    }
    if (element instanceof HTMLButtonElement && element.disabled) {
      throw new Error('Button is disabled: ${testId}');
    }
    if (element instanceof HTMLElement) {
      element.click();
    } else {
      element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    }
    return true;
  })()`);
}

async function clickTopologyHierarchyTarget(displayName) {
  await waitForExpression(
    `Array.from(document.querySelectorAll('.topology-hierarchy button'))`
      + `.some(candidate => candidate.querySelector('strong')?.textContent?.trim() === ${JSON.stringify(displayName)})`,
    15000,
    `Topology hierarchy target ${displayName} to load`);
  await evaluate(`(() => {
    const button = Array.from(document.querySelectorAll('.topology-hierarchy button'))
      .find(candidate => candidate.querySelector('strong')?.textContent?.trim() === ${JSON.stringify(displayName)});
    if (!(button instanceof HTMLButtonElement)) {
      throw new Error('Missing topology hierarchy target: ${displayName}');
    }
    button.click();
    return true;
  })()`);
}

async function clickDriverBinding(bindingId) {
  await evaluate(`(() => {
    const button = Array.from(document.querySelectorAll('.topology-driver-list button'))
      .find(candidate => candidate.querySelector('strong')?.textContent?.trim() === ${JSON.stringify(bindingId)});
    if (!(button instanceof HTMLButtonElement)) {
      throw new Error('Missing Driver binding: ${bindingId}');
    }
    button.click();
    return true;
  })()`);
  await waitForExpression(
    `document.querySelector('[data-testid="driver-binding-id"]')?.value === ${JSON.stringify(bindingId)}`,
    15000,
    `Driver binding ${bindingId} to be selected`);
}

async function ensureCapabilityContractForm() {
  const formExists = await evaluate(
    'Boolean(document.querySelector("[data-testid=\\"capability-contract-id\\"]"))');
  if (!formExists) {
    await clickByTestId('new-capability-contract');
    await waitForExpression(
      'Boolean(document.querySelector("[data-testid=\\"capability-contract-id\\"]"))',
      15000,
      'Capability contract form to expand');
  }
}

async function dragPaletteItemToCanvas(testId, xRatio, yRatio) {
  await evaluate(`(() => {
    const source = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    const canvas = document.querySelector('[data-testid="topology-canvas"]');
    if (!(source instanceof HTMLElement) || !(canvas instanceof HTMLElement)) {
      throw new Error('Missing topology drag source or canvas: ${testId}');
    }
    const transfer = new DataTransfer();
    const bounds = canvas.getBoundingClientRect();
    const clientX = bounds.left + bounds.width * ${JSON.stringify(xRatio)};
    const clientY = bounds.top + bounds.height * ${JSON.stringify(yRatio)};
    source.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: transfer }));
    canvas.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: transfer, clientX, clientY }));
    canvas.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer, clientX, clientY }));
    source.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: transfer, clientX, clientY }));
    return true;
  })()`);
}

async function dragElementByTestId(testId, deltaX, deltaY) {
  await cdp.send('Page.bringToFront');
  await cdp.send('Emulation.setFocusEmulationEnabled', { enabled: true });
  await evaluate(`(() => {
    window.focus();
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (element instanceof SVGElement) element.focus();
    return document.hasFocus();
  })()`);
  const locateTarget = attachEvidence => evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof SVGGraphicsElement)) {
      throw new Error('Missing SVG drag element: ${testId}');
    }
    const surface = element.querySelector('.topology-3d-face.top') ?? element;
    const rect = surface.getBoundingClientRect();
    const clientX = rect.left + rect.width / 2;
    const clientY = rect.top + rect.height / 2;
    const hit = document.elementFromPoint(clientX, clientY);
    if (!(hit instanceof Element) || (hit !== element && !element.contains(hit))) {
      throw new Error('SVG drag surface is not hit-testable: ${testId}');
    }
    if (${JSON.stringify(attachEvidence)}) {
      window.__openlineopsLast3DDrag = [];
      for (const eventName of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel']) {
        element.addEventListener(eventName, event => {
          window.__openlineopsLast3DDrag.push({
            eventName,
            pointerId: event.pointerId,
            clientX: event.clientX,
            clientY: event.clientY,
            button: event.button,
            buttons: event.buttons,
            captured: element.hasPointerCapture(event.pointerId)
          });
        });
      }
    }
    return { clientX, clientY };
  })()`);

  let startX = Number.NaN;
  let startY = Number.NaN;
  let pointerDownState = null;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    const initialBounds = await locateTarget(false);
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: 1,
      y: 1,
      button: 'none',
      buttons: 0
    });
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: initialBounds.clientX,
      y: initialBounds.clientY,
      button: 'none',
      buttons: 0
    });
    await delay(100);
    const bounds = await locateTarget(true);
    startX = bounds.clientX;
    startY = bounds.clientY;
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mousePressed',
      x: startX,
      y: startY,
      button: 'left',
      buttons: 1,
      clickCount: 1
    });
    await delay(50);
    pointerDownState = await evaluate(`(() => {
      const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
      const events = window.__openlineopsLast3DDrag ?? [];
      const pointerDown = events.find(event => event.eventName === 'pointerdown') ?? null;
      return {
        events,
        captured: pointerDown && element instanceof Element
          ? element.hasPointerCapture(pointerDown.pointerId)
          : false
      };
    })()`);
    if (pointerDownState.events.some(event => event.eventName === 'pointerdown')
        && pointerDownState.captured) {
      break;
    }
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseReleased',
      x: startX,
      y: startY,
      button: 'left',
      buttons: 0,
      clickCount: 1
    });
    await delay(125);
  }
  if (!pointerDownState?.events.some(event => event.eventName === 'pointerdown')
      || !pointerDownState.captured) {
    throw new Error(`3D drag did not capture its pointer after 3 attempts: ${JSON.stringify(pointerDownState)}`);
  }
  const endX = startX + deltaX;
  const endY = startY + deltaY;
  for (let step = 1; step <= 4; step += 1) {
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: startX + (deltaX * step) / 4,
      y: startY + (deltaY * step) / 4,
      button: 'none',
      buttons: 1
    });
    await delay(25);
  }
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseReleased',
    x: endX,
    y: endY,
    button: 'left',
    buttons: 0,
    clickCount: 1
  });
  const events = await evaluate('window.__openlineopsLast3DDrag');
  if (!events.some(event => event.eventName === 'pointermove')) {
    throw new Error(`3D drag emitted no pointer move: ${JSON.stringify(events)}`);
  }
  return events;
}

async function dragProductionOperationByTestId(testId, deltaX, deltaY) {
  const bounds = await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    const header = element?.querySelector('header');
    if (!(header instanceof HTMLElement)) {
      throw new Error('Missing production Operation drag header: ${testId}');
    }
    const rect = header.getBoundingClientRect();
    return { clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 };
  })()`);
  const startX = bounds.clientX;
  const startY = bounds.clientY;
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mousePressed',
    x: startX,
    y: startY,
    button: 'left',
    buttons: 1,
    clickCount: 1
  });
  for (let step = 1; step <= 4; step += 1) {
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: startX + (deltaX * step) / 4,
      y: startY + (deltaY * step) / 4,
      buttons: 1
    });
    await delay(25);
  }
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseReleased',
    x: startX + deltaX,
    y: startY + deltaY,
    button: 'left',
    buttons: 0,
    clickCount: 1
  });
}

async function captureSmokeScreenshot(fileName) {
  if (!smokeScreenshotDirectory) {
    return;
  }

  const result = await cdp.send('Page.captureScreenshot', {
    format: 'png',
    fromSurface: true,
    captureBeyondViewport: false
  });
  await fs.mkdir(smokeScreenshotDirectory, { recursive: true });
  await fs.writeFile(
    path.join(smokeScreenshotDirectory, fileName),
    Buffer.from(result.data, 'base64'));
}

async function setInputByTestId(testId, value) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof HTMLInputElement)) {
      throw new Error('Missing input: ${testId}');
    }

    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(element, ${JSON.stringify(value)});
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function setInputByFieldLabel(workbenchTestId, label, value) {
  await evaluate(`(() => {
    const workbench = document.querySelector(
      '[data-testid="${escapeSelectorValue(workbenchTestId)}"]');
    const field = Array.from(workbench?.querySelectorAll('label') ?? [])
      .find(candidate => candidate.querySelector(':scope > span')?.textContent?.trim() === ${JSON.stringify(label)});
    const input = field?.querySelector('input');
    if (!(input instanceof HTMLInputElement)) {
      throw new Error('Missing input field ${label} in ${workbenchTestId}');
    }
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, ${JSON.stringify(value)});
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function setSelectByFieldLabel(workbenchTestId, label, value) {
  await evaluate(`(() => {
    const workbench = document.querySelector(
      '[data-testid="${escapeSelectorValue(workbenchTestId)}"]');
    const field = Array.from(workbench?.querySelectorAll('label') ?? [])
      .find(candidate => candidate.querySelector(':scope > span')?.textContent?.trim() === ${JSON.stringify(label)});
    const select = field?.querySelector('select');
    if (!(select instanceof HTMLSelectElement)) {
      throw new Error('Missing select field ${label} in ${workbenchTestId}');
    }
    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
    setter?.call(select, ${JSON.stringify(value)});
    select.dispatchEvent(new Event('input', { bubbles: true }));
    select.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function setTextAreaByTestId(testId, value) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof HTMLTextAreaElement)) {
      throw new Error('Missing textarea: ${testId}');
    }

    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
    setter?.call(element, ${JSON.stringify(value)});
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function setCheckboxByTestId(testId, checked) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof HTMLInputElement) || element.type !== 'checkbox') {
      throw new Error('Missing checkbox: ${testId}');
    }
    if (element.checked !== ${JSON.stringify(checked)}) {
      element.click();
    }
    if (element.checked !== ${JSON.stringify(checked)}) {
      throw new Error('Checkbox did not reach the requested state: ${testId}');
    }
    return true;
  })()`);
}

async function setSelectByTestId(testId, value) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof HTMLSelectElement)) {
      throw new Error('Missing select: ${testId}');
    }

    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
    setter?.call(element, ${JSON.stringify(value)});
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function waitForGeneratedTransitionId(baseId, excludedIds, description) {
  return waitForExpression(
    `(() => {
      const baseId = ${JSON.stringify(baseId)};
      const excludedIds = new Set(${JSON.stringify(excludedIds)});
      return Array.from(document.querySelectorAll('[data-testid^="transition-id-"]'))
        .map(element => element instanceof HTMLInputElement ? element.value : '')
        .find(value => (value === baseId || value.startsWith(baseId + '-')) && !excludedIds.has(value))
        ?? '';
    })()`,
    15000,
    description);
}

async function getPublishedDesktopProcessDefinition() {
  const definition = await getLatestDesktopProcessDefinition(
    item => item.status === 'Published');

  if (!definition) {
    throw new Error('Published desktop process definition not found.');
  }

  return definition;
}

async function assertProductionLinePersisted() {
  if (!activeProjectApplicationScope) {
    throw new Error('Active project application scope was not captured.');
  }

  const response = await apiRequest(
    `/api/automation-projects/${encodeURIComponent(activeProjectApplicationScope.projectId)}`
    + `/applications/${encodeURIComponent(activeProjectApplicationScope.applicationId)}/production-lines`);
  const line = response.body?.find(
    candidate => candidate.operationCount === 2 && candidate.productModelCode === 'MAINBOARD-A');
  if (response.status !== 200 || !line) {
    throw new Error(`Production line was not persisted: ${response.text}`);
  }

  return line.lineDefinitionId;
}

async function getProductionLineLayout(lineDefinitionId) {
  if (!activeProjectApplicationScope) {
    throw new Error('Active project application scope was not captured.');
  }
  const response = await apiRequest(
    `/api/automation-projects/${encodeURIComponent(activeProjectApplicationScope.projectId)}`
    + `/applications/${encodeURIComponent(activeProjectApplicationScope.applicationId)}`
    + `/production-lines/${encodeURIComponent(lineDefinitionId)}`);
  if (response.status !== 200 || !response.body?.routeLayout) {
    throw new Error(`Production line route layout was not readable: ${response.text}`);
  }
  return response.body.routeLayout;
}

async function waitForProductionLineLayout(lineDefinitionId, predicate, description) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    const layout = await getProductionLineLayout(lineDefinitionId);
    if (predicate(layout)) {
      return layout;
    }
    await delay(150);
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

async function getLatestDesktopProcessDefinition(predicate = () => true) {
  if (!activeProjectApplicationScope) {
    throw new Error('Active project application scope was not captured.');
  }

  const collectionPath = `/api/automation-projects/${encodeURIComponent(activeProjectApplicationScope.projectId)}`
    + `/applications/${encodeURIComponent(activeProjectApplicationScope.applicationId)}/processes`;
  const response = await apiRequest(collectionPath);
  const definition = response.body
    ?.filter(item => item.processDefinitionId.startsWith('desktop-flow-') && predicate(item))
    ?.sort((left, right) => left.processDefinitionId.localeCompare(right.processDefinitionId))
    ?.at(-1);

  if (!definition) {
    throw new Error(`Desktop process definition not found: ${JSON.stringify(response.body)}`);
  }

  const detailResponse = await apiRequest(
    `${collectionPath}/${encodeURIComponent(definition.processDefinitionId)}`);
  if (detailResponse.status !== 200) {
    throw new Error(
      `Process detail lookup failed: expected 200, got ${detailResponse.status}. ${detailResponse.text}`);
  }

  return detailResponse.body;
}

async function waitForProcessDefinitionDisplayName(processDefinitionId, expectedDisplayName) {
  if (!activeProjectApplicationScope) {
    throw new Error('Active project application scope was not captured.');
  }

  const definitionPath = `/api/automation-projects/${encodeURIComponent(activeProjectApplicationScope.projectId)}`
    + `/applications/${encodeURIComponent(activeProjectApplicationScope.applicationId)}/processes/`
    + encodeURIComponent(processDefinitionId);
  const deadline = Date.now() + 30000;
  let lastResponse;

  while (Date.now() < deadline) {
    lastResponse = await apiRequest(definitionPath);
    if (lastResponse.status === 200 && lastResponse.body?.displayName === expectedDisplayName) {
      return lastResponse.body;
    }

    await delay(500);
  }

  throw new Error(
    `Timed out waiting for process draft update ${expectedDisplayName}: ${lastResponse?.text ?? 'no response'}`);
}

async function getAutomationProjectByPath(projectPath) {
  const response = await apiRequest('/api/automation-projects');
  const project = response.body
    ?.find(item => item.projectPath === projectPath);
  if (!project) {
    throw new Error(`Automation project not found for path ${projectPath}: ${JSON.stringify(response.body)}`);
  }

  const detailResponse = await apiRequest(
    `/api/automation-projects/${encodeURIComponent(project.projectId)}`);
  if (detailResponse.status !== 200) {
    throw new Error(
      `Automation project lookup failed: expected 200, got ${detailResponse.status}. ${detailResponse.text}`);
  }

  return detailResponse.body;
}

async function copyAndImportPortableApplication(sourceProjectPath, sourceProject, sourceApplication) {
  if (!sourceApplication.projectFilePath || !sourceApplication.topologyId) {
    throw new Error(
      `Portable Application source is incomplete: ${JSON.stringify(sourceApplication)}`);
  }

  const targetProjectPath = `${sourceProjectPath}-portable-target`;
  smokeProjectDirectories.push(targetProjectPath);
  const targetProjectId = `${sourceProject.projectId}-portable-target`;
  await expectApiStatus(
    '/api/automation-project-workspaces',
    {
      method: 'POST',
      body: {
        projectId: targetProjectId,
        displayName: 'Portable Application Target',
        projectPath: targetProjectPath,
        defaultApplicationId: null,
        defaultApplicationName: null
      }
    },
    201,
    'create portable Application target project');

  const sourceApplicationFile = path.join(
    sourceProjectPath,
    ...sourceApplication.projectFilePath.split('/'));
  const sourceApplicationRoot = path.dirname(sourceApplicationFile);
  const copiedApplicationRoot = path.join(
    targetProjectPath,
    'applications',
    'copied-primary-application');
  await fs.mkdir(path.dirname(copiedApplicationRoot), { recursive: true });
  await fs.cp(sourceApplicationRoot, copiedApplicationRoot, { recursive: true });
  const copiedApplicationFile = path.join(
    copiedApplicationRoot,
    path.basename(sourceApplicationFile));
  const sourceFingerprint = await fingerprintDirectory(sourceApplicationRoot);
  const copiedApplicationWriteTime = (await fs.stat(
    copiedApplicationFile,
    { bigint: true })).mtimeNs;

  const importResponse = await expectApiStatus(
    `/api/automation-projects/${encodeURIComponent(targetProjectId)}/applications/import`,
    {
      method: 'POST',
      body: { projectFilePath: copiedApplicationFile }
    },
    200,
    'import copied portable Application');
  const importedApplication = importResponse.body?.project?.applications?.find(
    application => application.applicationId === sourceApplication.applicationId);
  if (!importedApplication
      || importedApplication.projectFilePath
        !== `applications/copied-primary-application/${path.basename(sourceApplicationFile)}`) {
    throw new Error(`Copied Application was not linked by its .oloapp: ${importResponse.text}`);
  }

  const copiedFingerprint = await fingerprintDirectory(copiedApplicationRoot);
  if (copiedFingerprint !== sourceFingerprint) {
    throw new Error('Import rewrote files inside the copied portable Application directory.');
  }
  const importedApplicationWriteTime = (await fs.stat(
    copiedApplicationFile,
    { bigint: true })).mtimeNs;
  if (importedApplicationWriteTime !== copiedApplicationWriteTime) {
    throw new Error('Import replaced an unchanged copied .oloapp file.');
  }

  const topologyResponse = await apiRequest(
    `/api/automation-projects/${encodeURIComponent(targetProjectId)}`
    + `/applications/${encodeURIComponent(sourceApplication.applicationId)}`
    + `/topologies/${encodeURIComponent(sourceApplication.topologyId)}`);
  if (topologyResponse.status !== 200
      || topologyResponse.body?.topologyId !== sourceApplication.topologyId) {
    throw new Error(`Copied Application topology cannot be restored: ${topologyResponse.text}`);
  }

  await assertProjectFileLayout(targetProjectPath, 1);
  return {
    projectId: targetProjectId,
    projectPath: targetProjectPath
  };
}

async function openProjectByPathFromWorkbench(projectPath, projectId, applicationId) {
  await clickByTestId('switch-project-workspace');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"start-open-project-by-path\\"]")))()',
    15000,
    'start center to render for portable Application project switch');
  await clickByTestId('start-open-project-by-path');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"open-project-path-input\\"]")))()',
    15000,
    'open project path dialog to render for portable Application project switch');
  await setInputByTestId('open-project-path-input', projectPath);
  await clickByTestId('open-project-workspace');
  await waitForExpression(
    `(() => document.body.innerText.includes(${JSON.stringify(`Project opened ${projectId}`)})`
    + ` && document.querySelector('[data-testid="active-application-selector"]')?.value === ${JSON.stringify(applicationId)}`
    + ' && Boolean(document.querySelector("[data-testid=\\"new-application-id\\"]")))()',
    30000,
    `portable Application project ${projectId} to open`);
}

async function fingerprintDirectory(directory) {
  const hash = createHash('sha256');

  async function append(current, relativeRoot) {
    const entries = await fs.readdir(current, { withFileTypes: true });
    entries.sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of entries) {
      const relativePath = path.posix.join(relativeRoot, entry.name);
      const fullPath = path.join(current, entry.name);
      hash.update(relativePath);
      hash.update(entry.isDirectory() ? 'directory' : 'file');
      if (entry.isDirectory()) {
        await append(fullPath, relativePath);
      } else {
        hash.update(await fs.readFile(fullPath));
      }
    }
  }

  await append(directory, '');
  return hash.digest('hex');
}

async function createPublishedEngineeringSnapshot(definition) {
  if (!activeProjectApplicationScope) {
    throw new Error('Active project application scope was not captured.');
  }

  const engineeringBasePath = `/api/automation-projects/${encodeURIComponent(activeProjectApplicationScope.projectId)}`
    + `/applications/${encodeURIComponent(activeProjectApplicationScope.applicationId)}/engineering`;
  const suffix = Date.now().toString(36);
  const recipeId = `recipe-desktop-process-${suffix}`;
  const stationProfileId = `station-desktop-process-${suffix}`;
  const workspaceId = `workspace-desktop-process-${suffix}`;
  const projectId = `project-desktop-process-${suffix}`;
  const configurationSnapshotId = `snapshot-desktop-process-${suffix}`;
  const requiredCapabilities = [...new Set(
    (definition.nodes ?? [])
      .filter(node => node.kind === 'Command' && node.requiredCapability)
      .map(node => node.requiredCapability)
  )];
  if (requiredCapabilities.length === 0) {
    throw new Error(`Published process has no command capability: ${JSON.stringify(definition)}`);
  }

  await expectApiStatus(
    `${engineeringBasePath}/recipes`,
    {
      method: 'POST',
      body: {
        recipeId,
        versionId: `${recipeId}@1.0.0`,
        displayName: 'Desktop Process Runtime Recipe',
        parameters: [
          {
            key: 'scan.mode',
            value: 'desktop-smoke'
          }
        ]
      }
    },
    201,
    'create runtime recipe');
  await expectApiStatus(
    `${engineeringBasePath}/recipes/${recipeId}/publish`,
    { method: 'POST' },
    200,
    'publish runtime recipe');
  await expectApiStatus(
    `${engineeringBasePath}/station-profiles`,
    {
      method: 'POST',
      body: {
        stationProfileId,
        stationSystemId: `${activeProjectApplicationScope.applicationId}.station.1`,
        displayName: 'Desktop Process Runtime Station',
        deviceBindings: requiredCapabilities.map((capabilityId, index) => ({
          deviceBindingId: `runtime-primary-${index + 1}`,
          ownerSystemId: `${activeProjectApplicationScope.applicationId}.station.1`,
          capabilityId,
          deviceKey: `desktop-smoke-device-${index + 1}`
        }))
      }
    },
    201,
    'create runtime station profile');
  await expectApiStatus(
    `${engineeringBasePath}/workspaces`,
    {
      method: 'POST',
      body: {
        workspaceId,
        displayName: 'Desktop Runtime Workspace'
      }
    },
    201,
    'create runtime workspace');
  await expectApiStatus(
    `${engineeringBasePath}/projects`,
    {
      method: 'POST',
      body: {
        projectId,
        workspaceId,
        displayName: 'Desktop Runtime Project'
      }
    },
    201,
    'create runtime project');
  await expectApiStatus(
    `${engineeringBasePath}/projects/${projectId}/configuration-snapshots`,
    {
      method: 'POST',
      body: {
        snapshotId: configurationSnapshotId,
        processDefinitionId: definition.processDefinitionId,
        processVersionId: definition.versionId,
        recipeId,
        stationProfileId
      }
    },
    201,
    'publish runtime configuration snapshot');

  return configurationSnapshotId;
}

async function expectApiStatus(path, options, expectedStatus, description) {
  const response = await apiRequest(path, options);
  if (response.status !== expectedStatus) {
    throw new Error(
      `${description} failed: expected ${expectedStatus}, got ${response.status}. ${response.text}`);
  }

  return response;
}

async function editorMutationOptions(documentPath, options) {
  const current = await expectApiStatus(
    documentPath,
    {},
    200,
    `load editor revision for ${documentPath}`);
  const revision = current.body?.revision;
  if (typeof revision !== 'string' || revision.length === 0) {
    throw new Error(`Editor document ${documentPath} did not return a revision: ${current.text}`);
  }

  return {
    ...options,
    headers: {
      ...(options.headers ?? {}),
      'If-Match': `"${revision}"`
    }
  };
}

async function apiRequest(path, options = {}) {
  return evaluate(`window.openlineopsDesktop.apiRequest(${JSON.stringify(path)}, ${JSON.stringify(options)})`);
}

async function assertProjectFileLayout(projectPath, expectedApplicationCount) {
  const rootEntries = await fs.readdir(projectPath, { withFileTypes: true });
  const projectFiles = rootEntries.filter(entry =>
    entry.isFile() && entry.name.toLowerCase().endsWith('.oloproj'));
  if (projectFiles.length !== 1) {
    throw new Error(
      `Expected one .oloproj in ${projectPath}, found ${projectFiles.map(entry => entry.name).join(', ')}`);
  }

  const applicationsPath = path.join(projectPath, 'applications');
  const applicationDirectories = (await fs.readdir(applicationsPath, { withFileTypes: true }))
    .filter(entry => entry.isDirectory());
  let applicationFileCount = 0;
  for (const directory of applicationDirectories) {
    const entries = await fs.readdir(path.join(applicationsPath, directory.name), { withFileTypes: true });
    applicationFileCount += entries.filter(entry =>
      entry.isFile() && entry.name.toLowerCase().endsWith('.oloapp')).length;
  }

  if (applicationFileCount !== expectedApplicationCount) {
    throw new Error(
      `Expected ${expectedApplicationCount} .oloapp files in ${applicationsPath}, found ${applicationFileCount}`);
  }
}

async function readApplicationTopologyStorage(
  projectPath,
  applicationId,
  topologyId,
  layoutId
) {
  const rootEntries = await fs.readdir(projectPath, { withFileTypes: true });
  const projectFile = rootEntries.find(entry => entry.isFile() && entry.name.endsWith('.oloproj'));
  if (!projectFile) {
    throw new Error(`Project manifest was not found under ${projectPath}.`);
  }
  const projectDocument = JSON.parse(await fs.readFile(path.join(projectPath, projectFile.name), 'utf8'));
  const applicationReference = projectDocument.applications?.find(
    application => application.applicationId === applicationId);
  if (!applicationReference?.projectFile) {
    throw new Error(`Application ${applicationId} has no .oloapp reference.`);
  }
  const applicationRoot = path.dirname(path.resolve(projectPath, applicationReference.projectFile));
  const topologyResources = await readJsonResourceDirectory(path.join(applicationRoot, 'topology'));
  const layoutResources = await readJsonResourceDirectory(path.join(applicationRoot, 'layouts'));
  const topologyResource = topologyResources.find(resource => resource.document?.topologyId === topologyId);
  const layoutResource = layoutResources.find(resource => resource.document?.layoutId === layoutId);
  if (!topologyResource || !layoutResource) {
    throw new Error(
      `Application ${applicationId} is missing topology ${topologyId} or layout ${layoutId} on disk.`);
  }
  return {
    topologyPath: topologyResource.filePath,
    topologyText: topologyResource.text,
    topologyDocument: topologyResource.document,
    layoutPath: layoutResource.filePath,
    layoutText: layoutResource.text,
    layoutDocument: layoutResource.document
  };
}

async function readJsonResourceDirectory(directory) {
  const entries = await fs.readdir(directory, { withFileTypes: true });
  const resources = [];
  for (const entry of entries.filter(candidate => candidate.isFile() && candidate.name.endsWith('.json'))) {
    const filePath = path.join(directory, entry.name);
    const text = await fs.readFile(filePath, 'utf8');
    resources.push({ filePath, text, document: JSON.parse(text) });
  }
  return resources;
}

async function readApplicationExtensionStorage(projectPath, applicationId, portableId) {
  const rootEntries = await fs.readdir(projectPath, { withFileTypes: true });
  const projectFile = rootEntries.find(entry => entry.isFile() && entry.name.endsWith('.oloproj'));
  if (!projectFile) {
    throw new Error(`Project manifest was not found under ${projectPath}.`);
  }
  const projectDocument = JSON.parse(await fs.readFile(path.join(projectPath, projectFile.name), 'utf8'));
  const applicationReference = projectDocument.applications?.find(
    application => application.applicationId === applicationId);
  if (!applicationReference?.projectFile) {
    throw new Error(`Application ${applicationId} has no .oloapp reference.`);
  }
  const applicationFilePath = path.resolve(projectPath, applicationReference.projectFile);
  const applicationDocument = JSON.parse(await fs.readFile(applicationFilePath, 'utf8'));
  const expectedManifestPath = `plugins/${portableId}/manifest.json`;
  const reference = applicationDocument.pluginPackageReferences?.find(
    candidate => candidate.manifestPath === expectedManifestPath) ?? null;
  const manifestPath = path.resolve(
    path.dirname(applicationFilePath),
    ...expectedManifestPath.split('/'));
  return {
    applicationFilePath,
    reference,
    manifestPath,
    manifestExists: Boolean(await fs.stat(manifestPath).catch(() => null))
  };
}

async function waitForExpression(expression, timeoutMs, description) {
  const deadline = Date.now() + timeoutMs;
  let lastValue;

  while (Date.now() < deadline) {
    lastValue = await evaluate(expression);
    if (lastValue) {
      return lastValue;
    }

    await delay(500);
  }

  throw new Error(`Timed out waiting for ${description}. Last value: ${JSON.stringify(lastValue)}`);
}

async function evaluate(expression) {
  const response = await cdp.send('Runtime.evaluate', {
    expression,
    awaitPromise: true,
    returnByValue: true
  });

  if (response.exceptionDetails) {
    const description = response.exceptionDetails.exception?.description
      ?? response.exceptionDetails.text
      ?? 'Unknown evaluation error';
    throw new Error(description);
  }

  return response.result.value;
}

async function getBackendStatus() {
  return evaluate('window.openlineopsDesktop.getBackendStatus()');
}

async function getPageState() {
  return evaluate(`(() => ({
    text: document.body?.innerText ?? '',
    hubState: document.querySelector(".rail-footer .status-pill")?.textContent?.trim() ?? null,
    runDisabled: document.querySelector('[data-testid="run-active-project"]')?.disabled ?? null,
    refreshDisabled: document.querySelector('[data-testid="refresh-backend"]')?.disabled ?? null,
    events: window.__openlineopsSmokeEvents ?? null,
    last3DDrag: window.__openlineopsLast3DDrag ?? null
  }))()`);
}

async function collectDiagnostics() {
  if (!cdp) {
    return null;
  }

  try {
    return evaluate(`(async () => ({
      page: {
        text: document.body?.innerText ?? '',
        hubState: document.querySelector(".rail-footer .status-pill")?.textContent?.trim() ?? null,
        runDisabled: document.querySelector('[data-testid="run-active-project"]')?.disabled ?? null,
        refreshDisabled: document.querySelector('[data-testid="refresh-backend"]')?.disabled ?? null,
        events: window.__openlineopsSmokeEvents ?? null,
        last3DDrag: window.__openlineopsLast3DDrag ?? null
      },
      backendStatus: await window.openlineopsDesktop?.getBackendStatus?.(),
      config: await window.openlineopsDesktop?.getConfig?.(),
      cdpEvents: ${JSON.stringify(cdpEvents)}
    }))()`);
  } catch (error) {
    return {
      diagnosticsError: error instanceof Error ? error.message : String(error),
      cdpEvents
    };
  }
}

async function waitForCdpTarget(port, previewUrl, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const targets = await fetchJson(`http://127.0.0.1:${port}/json/list`);
      const target = targets.find(item => item.type === 'page'
        && (previewUrl ? item.url.startsWith(previewUrl) : item.url.startsWith('file:')));
      if (target?.webSocketDebuggerUrl) {
        return target;
      }
    } catch {
      // Electron may still be starting.
    }

    await delay(500);
  }

  throw new Error(`Timed out waiting for Electron CDP target on port ${port}.`);
}

async function waitForHttp(url, timeoutMs, description) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }
    } catch {
      // Server may still be starting.
    }

    await delay(400);
  }

  throw new Error(`Timed out waiting for ${description} at ${url}.`);
}

async function fetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`GET ${url} returned ${response.status}.`);
  }

  return response.json();
}

function spawnLogged(command, args, options, label) {
  const child = spawn(command, args, {
    ...options,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });

  child.stdout.on('data', chunk => appendChildLog(label, chunk));
  child.stderr.on('data', chunk => appendChildLog(label, chunk));
  child.on('exit', code => appendChildLog(label, `exited with code ${code ?? 'unknown'}`));

  return child;
}

function appendChildLog(label, chunk) {
  for (const rawLine of chunk.toString().split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line) {
      childLogs.push(`[${label}] ${line}`);
    }
  }

  if (childLogs.length > maxLogLines) {
    childLogs.splice(0, childLogs.length - maxLogLines);
  }
}

function appendCdpEvent(message) {
  if (
    message.method !== 'Runtime.exceptionThrown'
    && message.method !== 'Runtime.consoleAPICalled'
    && message.method !== 'Log.entryAdded'
    && message.method !== 'Page.javascriptDialogOpening'
  ) {
    return;
  }

  cdpEvents.push(message);
  if (cdpEvents.length > maxCdpEvents) {
    cdpEvents.splice(0, cdpEvents.length - maxCdpEvents);
  }
}

async function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        reject(new Error('Unable to allocate a local port.'));
        return;
      }

      const port = address.port;
      server.close(() => resolve(port));
    });
  });
}

function assertNodeRuntime() {
  if (typeof WebSocket === 'undefined') {
    throw new Error('Node.js 22 or newer is required because the smoke test uses the built-in WebSocket API.');
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function escapeSelectorValue(value) {
  return value.replace(/["\\]/g, '\\$&');
}

async function cleanup() {
  if (cdp) {
    try {
      await withTimeout(
        evaluate('window.openlineopsDesktop?.stopBackend?.()'),
        5000,
        'backend stop during cleanup');
    } catch {
      // Best effort.
    }

    try {
      await withTimeout(
        cdp.send('Browser.close'),
        3000,
        'Electron browser close during cleanup');
    } catch {
      // Best effort.
    } finally {
      cdp.close();
    }
  }

  await stopChild(electronProcess);
  await stopChild(previewProcess);
  if (apiSquatterServer) {
    await new Promise((resolve, reject) => {
      apiSquatterServer.close(error => error ? reject(error) : resolve());
    });
    apiSquatterServer = undefined;
  }
  for (const smokeProjectDirectory of new Set(smokeProjectDirectories)) {
    await fs.rm(smokeProjectDirectory, {
      recursive: true,
      force: true,
      maxRetries: 5,
      retryDelay: 200
    });
  }
  if (smokeUserDataDirectory) {
    await fs.rm(smokeUserDataDirectory, {
      recursive: true,
      force: true,
      maxRetries: 5,
      retryDelay: 200
    });
  }
  if (sampleExtensionArchive) {
    await sampleExtensionArchive.cleanup();
    sampleExtensionArchive = undefined;
  }
}

async function startApiSquatter() {
  const server = http.createServer((request, response) => {
    if (request.headers.authorization) {
      apiSquatterAuthorizationHeaders.push(request.headers.authorization);
    }
    response.writeHead(503, { 'cache-control': 'no-store' });
    response.end('untrusted loopback process');
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') {
    await new Promise(resolve => server.close(resolve));
    throw new Error('API squatter could not bind its held loopback port.');
  }
  return { server, baseUrl: `http://127.0.0.1:${address.port}` };
}

async function stopChild(child) {
  if (!child || child.killed || child.exitCode !== null) {
    return;
  }

  child.kill();
  const exited = await Promise.race([
    new Promise(resolve => child.once('exit', () => resolve(true))),
    delay(5000).then(() => false)
  ]);

  if (!exited && child.exitCode === null) {
    child.kill('SIGKILL');
  }
}

async function waitForChildExit(child, timeoutMs) {
  if (!child || child.exitCode !== null) {
    return true;
  }
  return Promise.race([
    new Promise(resolve => {
      child.once('exit', () => resolve(true));
      if (child.exitCode !== null) {
        resolve(true);
      }
    }),
    delay(timeoutMs).then(() => false)
  ]);
}

async function withTimeout(promise, timeoutMs, description) {
  return Promise.race([
    promise,
    delay(timeoutMs).then(() => {
      throw new Error(`Timed out waiting for ${description}.`);
    })
  ]);
}

class CdpClient {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 1;
    this.pending = new Map();
  }

  static connect(webSocketUrl) {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(webSocketUrl);
      const client = new CdpClient(socket);

      socket.addEventListener('open', () => resolve(client), { once: true });
      socket.addEventListener('error', event => reject(event.error ?? new Error('CDP socket error.')), { once: true });
      socket.addEventListener('message', event => client.handleMessage(event.data));
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    const payload = JSON.stringify({ id, method, params });

    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(payload);
    });
  }

  close() {
    for (const pending of this.pending.values()) {
      pending.reject(new Error('CDP socket closed.'));
    }

    this.pending.clear();
    if (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING) {
      this.socket.close();
    }
  }

  handleMessage(data) {
    const message = JSON.parse(data);
    if (!message.id) {
      appendCdpEvent(message);
      return;
    }

    const pending = this.pending.get(message.id);
    if (!pending) {
      return;
    }

    this.pending.delete(message.id);
    if (message.error) {
      pending.reject(new Error(message.error.message));
      return;
    }

    pending.resolve(message.result);
  }
}

main()
  .catch(async error => {
    console.error(error);
    const diagnostics = await collectDiagnostics();
    if (diagnostics) {
      console.error('\nSmoke diagnostics:');
      console.error(JSON.stringify(diagnostics, null, 2));
    }

    if (childLogs.length > 0) {
      console.error('\nRecent child process output:');
      console.error(childLogs.join('\n'));
    }

    process.exitCode = 1;
  })
  .finally(async () => {
    await cleanup();
  });
