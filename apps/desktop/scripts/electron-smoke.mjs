import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import electronPath from 'electron';

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
const smokeProjectDirectories = [];
let smokeUserDataDirectory;

async function main() {
  assertNodeRuntime();

  const previewPort = packagedMode ? null : await getFreePort();
  const apiPort = await getFreePort();
  const cdpPort = await getFreePort();
  const previewUrl = previewPort === null ? null : `http://127.0.0.1:${previewPort}`;
  const apiBaseUrl = `http://127.0.0.1:${apiPort}`;
  smokeUserDataDirectory = await fs.mkdtemp(
    path.join(os.tmpdir(), 'openlineops-desktop-smoke-'));

  if (previewUrl) {
    previewProcess = spawnLogged(
      process.execPath,
      [viteCliPath, 'preview', '--host', '127.0.0.1', '--port', String(previewPort)],
      { cwd: desktopRoot },
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
  }

  electronProcess = spawnLogged(
    packagedMode ? packagedExecutable : electronPath,
    packagedMode
      ? [
          `--remote-debugging-port=${cdpPort}`,
          '--disable-gpu',
          `--user-data-dir=${smokeUserDataDirectory}`
        ]
      : [
          `--remote-debugging-port=${cdpPort}`,
          '--disable-gpu',
          `--user-data-dir=${smokeUserDataDirectory}`,
          desktopRoot
        ],
    {
      cwd: packagedMode ? path.dirname(packagedExecutable) : desktopRoot,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        OPENLINEOPS_API_BASE_URL: apiBaseUrl,
        OPENLINEOPS_REPO_ROOT: repoRoot,
        ...(previewUrl
          ? {
              VITE_DEV_SERVER_URL: previewUrl,
              OpenLineOps__Desktop__AllowedOrigins__0: previewUrl,
              OpenLineOps__Desktop__AllowedOrigins__1: previewUrl.replace('127.0.0.1', 'localhost')
            }
          : {})
      }
    },
    'electron');

  const target = await waitForCdpTarget(cdpPort, previewUrl, 30000);
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
  const backendNeedsStart = await evaluate(
    'Boolean(document.querySelector("[data-testid=\\"start-backend\\"]"))');
  if (backendNeedsStart) {
    await clickByTestId('start-backend');
  }
  await waitForHealthyBackend();
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
  await dragElementByTestId(slot3DTestId, 24, 14);
  await waitForExpression(
    '(() => Number(document.querySelector("[data-testid=\\"layout-geometry-x\\"]")?.value) !== 18'
    + ' && document.body.innerText.includes("Layout saved"))()',
    30000,
    '3D block drag to update the shared local geometry');
  let edited3DGeometry = { x: Number.NaN, y: Number.NaN };
  const geometryDeadline = Date.now() + 30000;
  while (Date.now() < geometryDeadline) {
    const persistedLayout = await apiRequest(
      `/api/automation-projects/${encodeURIComponent(openedProject.projectId)}`
      + `/applications/${encodeURIComponent(openedApplication.applicationId)}`
      + `/layouts/${encodeURIComponent(layoutId)}`);
    const persistedSlot = persistedLayout.body?.elements?.find(
      element => element.target?.targetId === `${openedApplication.applicationId}.station.1.group.1.slot.1`);
    if (persistedLayout.status === 200 && persistedSlot?.x !== 18) {
      edited3DGeometry = { x: persistedSlot.x, y: persistedSlot.y };
      break;
    }
    await delay(200);
  }
  if (!Number.isFinite(edited3DGeometry.x)
      || !Number.isFinite(edited3DGeometry.y)
      || edited3DGeometry.x === 18) {
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
  await clickByTestId('remove-route-transition');
  await clickByTestId('production-operation-node-operation-2');
  await clickByTestId('remove-production-operation');
  await waitForExpression(
    '(() => !document.querySelector("[data-testid=\\"production-operation-node-operation-2\\"]")'
    + ' && !document.querySelector("[data-testid=\\"production-transition-edge-route-1\\"]"))()',
    15000,
    'Condition editor exercise to return to the single-Operation runtime route');
  await clickByTestId('save-production-line');
  await waitForExpression(
    '(() => document.body.innerText.includes("Production line saved line-"))()',
    30000,
    'Product Model and Operation route graph to save');
  await captureSmokeScreenshot('line-designer.png');
  const productionLineDefinitionId = await assertProductionLinePersisted();
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
          }))
        })),
        transitions: liveLine.body.transitions.map(transition => ({
          transitionId: transition.transitionId,
          sourceOperationId: transition.sourceOperationId,
          targetOperationId: transition.targetOperationId,
          kind: transition.kind,
          requiredJudgement: transition.requiredJudgement,
          maxTraversals: transition.maxTraversals,
          parallelGroupId: transition.parallelGroupId,
          outputKey: transition.outputKey,
          expectedOutputKind: transition.expectedOutputKind,
          expectedOutputValue: transition.expectedOutputValue
        })),
        lineControllerAuthorizations: liveLine.body.lineControllerAuthorizations.map(
          authorization => ({ ...authorization }))
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
  await setInputByTestId('production-run-actor', 'desktop-smoke-operator');
  await clickByTestId('confirm-production-run');
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
    || traceRecordsAfterRuns.body?.items?.[0]?.operationCount < 1) {
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
    + ' && document.querySelector("[data-testid=\\"create-engineering-bundle\\"]")?.disabled === false)()',
    30000,
    'engineering workbench to render with published process selection');
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
    '(() => document.body.innerText.includes("Plugin Management")'
    + ' && document.body.innerText.includes("Loopback Device Sample")'
    + ' && Boolean(document.querySelector("[data-testid=\\"start-plugins\\"]")))()',
    30000,
    'plugins workbench to render');
  await clickByTestId('start-plugins');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"plugin-lifecycle-result\\"]")?.textContent?.includes("Initialized"))()',
    30000,
    'plugins to start from UI');
  await clickByTestId('stop-plugins');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"plugin-lifecycle-result\\"]")?.textContent?.includes("Stopped"))()',
    30000,
    'plugins to stop from UI');

  console.log(`${packagedMode ? 'Packaged ' : ''}Electron smoke passed against ${apiBaseUrl}.`);
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

async function clickByTestId(testId) {
  await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!element) {
      throw new Error('Missing element: ${testId}');
    }
    if (element instanceof HTMLElement) {
      element.click();
    } else {
      element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    }
    return true;
  })()`);
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
  const bounds = await evaluate(`(() => {
    const element = document.querySelector('[data-testid="${escapeSelectorValue(testId)}"]');
    if (!(element instanceof SVGGraphicsElement)) {
      throw new Error('Missing SVG drag element: ${testId}');
    }
    const rect = element.getBoundingClientRect();
    return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
  })()`);
  const startX = bounds.left + bounds.width / 2;
  const startY = bounds.top + bounds.height / 2;
  const endX = startX + deltaX;
  const endY = startY + deltaY;
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mousePressed',
    x: startX,
    y: startY,
    button: 'left',
    buttons: 1,
    clickCount: 1
  });
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseMoved',
    x: startX + deltaX / 2,
    y: startY + deltaY / 2,
    button: 'left',
    buttons: 1
  });
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseMoved',
    x: endX,
    y: endY,
    button: 'left',
    buttons: 1
  });
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseReleased',
    x: endX,
    y: endY,
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
    candidate => candidate.operationCount === 1 && candidate.productModelCode === 'MAINBOARD-A');
  if (response.status !== 200 || !line) {
    throw new Error(`Production line was not persisted: ${response.text}`);
  }

  return line.lineDefinitionId;
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
    events: window.__openlineopsSmokeEvents ?? null
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
        events: window.__openlineopsSmokeEvents ?? null
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
