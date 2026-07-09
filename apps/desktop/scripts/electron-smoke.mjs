import { spawn } from 'node:child_process';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import electronPath from 'electron';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const repoRoot = path.resolve(desktopRoot, '..', '..');
const viteCliPath = path.join(desktopRoot, 'node_modules', 'vite', 'bin', 'vite.js');

const childLogs = [];
const cdpEvents = [];
const maxLogLines = 160;
const maxCdpEvents = 80;
let previewProcess;
let electronProcess;
let cdp;

async function main() {
  assertNodeRuntime();

  const previewPort = await getFreePort();
  const apiPort = await getFreePort();
  const cdpPort = await getFreePort();
  const previewUrl = `http://127.0.0.1:${previewPort}`;
  const apiBaseUrl = `http://127.0.0.1:${apiPort}`;

  previewProcess = spawnLogged(
    process.execPath,
    [viteCliPath, 'preview', '--host', '127.0.0.1', '--port', String(previewPort)],
    { cwd: desktopRoot },
    'vite');

  await waitForHttp(previewUrl, 30000, 'Vite preview');

  electronProcess = spawnLogged(
    electronPath,
    [`--remote-debugging-port=${cdpPort}`, '--disable-gpu', desktopRoot],
    {
      cwd: desktopRoot,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        OPENLINEOPS_API_BASE_URL: apiBaseUrl,
        OPENLINEOPS_REPO_ROOT: repoRoot,
        VITE_DEV_SERVER_URL: previewUrl,
        OpenLineOps__Desktop__AllowedOrigins__0: previewUrl,
        OpenLineOps__Desktop__AllowedOrigins__1: previewUrl.replace('127.0.0.1', 'localhost')
      }
    },
    'electron');

  const target = await waitForCdpTarget(cdpPort, previewUrl, 30000);
  cdp = await CdpClient.connect(target.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');
  await cdp.send('Page.enable');

  await waitForExpression(
    '(() => document.body?.innerText.includes("Station Runtime Dashboard")'
    + ' && Boolean(window.openlineopsDesktop))()',
    30000,
    'desktop shell to render');

  await evaluate('window.__openlineopsSmokeEvents = {}');
  await clickByTestId('start-backend');
  await waitForHealthyBackend();
  await waitForExpression(
    '(() => document.querySelector(".rail-footer .status-pill")?.textContent?.trim() === "Connected")()',
    30000,
    'SignalR connection');

  const smokeProjectPath = path.join(
    repoRoot,
    'artifacts',
    'desktop-smoke-projects',
    `project-${Date.now().toString(36)}`);
  await clickByTestId('nav-projects');
  await waitForExpression(
    '(() => document.body.innerText.includes("Automation Projects")'
    + ' && Boolean(document.querySelector("[data-testid=\\"project-workspace-panel\\"]")))()',
    30000,
    'projects workbench to render');
  await setInputByTestId('project-path-input', smokeProjectPath);
  await clickByTestId('create-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project created project-")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Project Explorer")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Default Application"))()',
    30000,
    'automation project workspace to create from desktop');
  await clickByTestId('save-project-manifest');
  await waitForExpression(
    '(() => document.body.innerText.includes("Manifest saved")'
    + ' && document.body.innerText.includes("openlineops.project.json"))()',
    30000,
    'automation project manifest to save from desktop');
  await setInputByTestId('open-project-path-input', smokeProjectPath);
  await clickByTestId('open-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project opened project-")'
    + ' && document.querySelector("[data-testid=\\"project-workspace-panel\\"]")?.textContent?.includes("Automation Project"))()',
    30000,
    'automation project workspace to reopen from desktop');
  await clickByTestId('seed-project-topology');
  await waitForExpression(
    '(() => document.body.innerText.includes("Topology seeded project-")'
    + ' && document.querySelector("[data-testid=\\"project-topology-designer\\"]")?.textContent?.includes(".topology.main")'
    + ' && document.querySelector("[data-testid=\\"project-topology-designer\\"]")?.textContent?.includes("Modules")'
    + ' && document.querySelector("[data-testid=\\"project-topology-designer\\"]")?.textContent?.includes("Left Nest Slot 1")'
    + ' && document.querySelector("[data-testid=\\"site-layout-canvas\\"]")?.textContent?.includes("Station 1"))()',
    45000,
    'project topology and site layout to seed from desktop');
  await setInputByTestId('open-project-path-input', smokeProjectPath);
  await clickByTestId('open-project-workspace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project opened project-")'
    + ' && document.querySelector("[data-testid=\\"project-topology-designer\\"]")?.textContent?.includes(".topology.main")'
    + ' && document.querySelector("[data-testid=\\"site-layout-canvas\\"]")?.textContent?.includes("L1"))()',
    30000,
    'automation project topology link to persist through manifest reopen');

  await clickByTestId('nav-dashboard');
  await waitForExpression(
    '(() => document.body.innerText.includes("Station Runtime Dashboard")'
    + ' && Boolean(document.querySelector("[data-testid=\\"run-simulation\\"]")))()',
    15000,
    'dashboard to render before simulated runtime session');

  await evaluate('Math.random = () => 0.1');
  await clickByTestId('run-simulation');

  const result = await waitForExpression(
    `(() => {
      const text = document.body.innerText;
      const events = window.__openlineopsSmokeEvents ?? {};
      return text.includes("Simulated session Completed")
        && text.includes("station-desktop-")
        && text.includes("SN-")
        && (events.RuntimeEvent ?? 0) > 0
        && (events.StationStatusChanged ?? 0) > 0;
    })()`,
    45000,
    'completed simulated session with SignalR updates and trace row');

  if (!result) {
    throw new Error('Smoke assertions did not return true.');
  }

  await clickByTestId('nav-trace');
  await waitForExpression(
    '(() => document.body.innerText.includes("Traceability Search")'
    + ' && Boolean(document.querySelector("[data-testid=\\"trace-result-row\\"]")))()',
    30000,
    'trace workbench to render search results');
  await clickByTestId('trace-result-row');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("SN-")'
    + ' && document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("Measurements"))()',
    30000,
    'trace detail to load from selected row');
  await clickByTestId('trace-export-package');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"trace-detail-panel\\"]")?.textContent?.includes("openlineops.trace-package.v1"))()',
    30000,
    'trace export package to load');

  await clickByTestId('nav-processes');
  await waitForExpression(
    '(() => document.body.innerText.includes("Process Builder")'
    + ' && document.body.innerText.includes("Blockly")'
    + ' && document.body.innerText.includes("Automation Blocks")'
    + ' && document.body.innerText.includes("Transitions")'
    + ' && document.body.innerText.includes("Block Catalog")'
    + ' && document.querySelector("[data-testid=\\"project-target-panel\\"]")?.textContent?.includes("X Axis")'
    + ' && Boolean(document.querySelector("[data-testid=\\"blockly-workspace\\"]")))()',
    15000,
    'process workbench to render');
  await clickByTestId('apply-project-target-module-0');
  await waitForExpression(
    '(() => document.body.innerText.includes("Project target applied X Axis"))()',
    15000,
    'project topology target to apply to the PythonScript node');
  await clickByTestId('register-blockly-block');
  await waitForExpression(
    '(() => document.body.innerText.includes("Fixture Action")'
    + ' && document.body.innerText.includes("1 custom"))()',
    30000,
    'custom Blockly block to register from UI');
  await clickByTestId('register-blockly-block');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"block-version-history\\"]")?.textContent?.includes("v2")'
    + ' && document.querySelector("[data-testid=\\"block-version-history\\"]")?.textContent?.includes("v1"))()',
    30000,
    'custom Blockly block version history to load');
  await clickByTestId('restore-blockly-block-v1');
  await waitForExpression(
    '(() => document.body.innerText.includes("Restored user_fixture_action v1 as v3")'
    + ' && document.querySelector("[data-testid=\\"block-version-history\\"]")?.textContent?.includes("v3"))()',
    30000,
    'custom Blockly block version to restore from UI');
  await clickByTestId('add-command-node');
  await waitForExpression(
    '(() => Boolean(document.querySelector("[data-testid=\\"process-node-command-1\\"]")))()',
    15000,
    'process graph command node to be added');
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
  await setSelectByTestId(`transition-to-${retryTransitionId}`, 'normalize');
  await setInputByTestId(`transition-label-${retryTransitionId}`, 'retry');
  await setSelectByTestId(`transition-loop-policy-${retryTransitionId}`, 'Counted');
  await setInputByTestId(`transition-max-traversals-${retryTransitionId}`, '2');
  await setInputByTestId(`transition-id-${retryTransitionId}`, 'decision-1-to-normalize-retry');
  await waitForExpression(
    '(() => document.querySelector("[data-testid=\\"transition-loop-policy-decision-1-to-normalize-retry\\"]")?.value === "Counted"'
    + ' && document.querySelector("[data-testid=\\"transition-max-traversals-decision-1-to-normalize-retry\\"]")?.value === "2"'
    + ' && document.querySelector("[data-testid=\\"transition-to-decision-1-to-normalize-retry\\"]")?.value === "normalize")()',
    15000,
    'transition counted loop policy to be edited');
  await clickByTestId('save-process-definition');
  await waitForExpression(
    '(() => document.body.innerText.includes("Saved desktop-python-"))()',
    30000,
    'Blockly PythonScript process definition to save');
  const savedDefinition = await getLatestDesktopProcessDefinition();
  const savedPythonNode = savedDefinition.nodes
    ?.find(node => node.nodeId === 'normalize' && node.kind === 'PythonScript');
  if (!savedPythonNode?.inputPayload?.includes('.module.axis.x')
    || !savedPythonNode?.blocklyWorkspaceJson?.includes('.module.axis.x')) {
    throw new Error(`Project target payload was not persisted on PythonScript node: ${JSON.stringify(savedPythonNode)}`);
  }
  const countedTransition = savedDefinition.transitions
    ?.find(transition => transition.transitionId === 'decision-1-to-normalize-retry');
  if (countedTransition?.loopPolicy !== 'Counted' || countedTransition?.maxTraversals !== 2) {
    throw new Error(`Counted transition policy was not persisted: ${JSON.stringify(countedTransition)}`);
  }
  await clickByTestId('publish-process-definition');
  await waitForExpression(
    '(() => document.body.innerText.includes("Published desktop-python-")'
    + ' && document.body.innerText.includes("Published"))()',
    30000,
    'Blockly PythonScript process definition to publish');
  const publishedDefinition = await getPublishedDesktopProcessDefinition();
  const configurationSnapshotId = await createPublishedEngineeringSnapshot(publishedDefinition);
  await setInputByTestId('process-runtime-snapshot-id', configurationSnapshotId);
  await clickByTestId('publish-project-snapshot');
  const projectSnapshotId = await waitForExpression(
    '(() => {'
    + ' const result = document.querySelector("[data-testid=\\"project-snapshot-result\\"]");'
    + ' const snapshotId = result?.querySelector("strong")?.textContent?.trim() ?? "";'
    + ` return snapshotId.startsWith("project-snapshot-") && result?.textContent?.includes(${JSON.stringify(configurationSnapshotId)})`
    + '   ? snapshotId'
    + '   : "";'
    + '})()',
    45000,
    'project snapshot to publish from process runtime panel');
  const projectWithSnapshot = await getAutomationProjectByPath(smokeProjectPath);
  const publishedProjectSnapshot = projectWithSnapshot.snapshots
    ?.find(snapshot => snapshot.snapshotId === projectSnapshotId);
  if (projectWithSnapshot.activeSnapshotId !== projectSnapshotId || !publishedProjectSnapshot) {
    throw new Error(`Project snapshot was not activated: ${JSON.stringify(projectWithSnapshot)}`);
  }
  if (publishedProjectSnapshot.processDefinitionId !== publishedDefinition.processDefinitionId
    || publishedProjectSnapshot.processVersionId !== publishedDefinition.versionId
    || publishedProjectSnapshot.configurationSnapshotId !== configurationSnapshotId
    || !publishedProjectSnapshot.targetReferences?.some(target => target.targetId.includes('.module.axis.x'))
    || !publishedProjectSnapshot.capabilityBindings?.some(binding => binding.providerKey === 'simulator.axis.x')
    || !publishedProjectSnapshot.blockVersionIds?.some(blockVersionId => blockVersionId.startsWith('openlineops_move_axis@'))) {
    throw new Error(`Project snapshot payload was incomplete: ${JSON.stringify(publishedProjectSnapshot)}`);
  }
  await clickByTestId('start-published-process-session');
  await waitForExpression(
    '(() => document.body.innerText.includes("Started ")'
    + ' && document.body.innerText.includes("Completed")'
    + ` && document.querySelector("[data-testid=\\"runtime-start-result\\"]")?.textContent?.includes(${JSON.stringify(projectSnapshotId)})`
    + ' && document.body.innerText.includes("commands"))()',
    45000,
    'published project snapshot runtime session to complete');
  await clickByTestId('nav-engineering');
  await waitForExpression(
    '(() => document.body.innerText.includes("Engineering Configuration")'
    + ' && document.body.innerText.includes("Publish Snapshot")'
    + ' && document.querySelector("[data-testid=\\"create-engineering-bundle\\"]")?.disabled === false)()',
    30000,
    'engineering workbench to render with published process selection');
  await clickByTestId('create-engineering-bundle');
  await waitForExpression(
    '(() => document.body.innerText.includes("Snapshot published snapshot-desktop-")'
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

  console.log(`Electron smoke passed against ${apiBaseUrl}.`);
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
          const runButton = document.querySelector('[data-testid="run-simulation"]');
          return document.body.innerText.includes("Healthy") && runButton?.disabled === false;
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
    element.click();
    return true;
  })()`);
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

async function getLatestDesktopProcessDefinition(predicate = () => true) {
  const response = await apiRequest('/api/process-definitions');
  const definition = response.body
    ?.filter(item => item.processDefinitionId.startsWith('desktop-python-') && predicate(item))
    ?.sort((left, right) => left.processDefinitionId.localeCompare(right.processDefinitionId))
    ?.at(-1);

  if (!definition) {
    throw new Error(`Desktop process definition not found: ${JSON.stringify(response.body)}`);
  }

  const detailResponse = await apiRequest(
    `/api/process-definitions/${encodeURIComponent(definition.processDefinitionId)}`);
  if (detailResponse.status !== 200) {
    throw new Error(
      `Process detail lookup failed: expected 200, got ${detailResponse.status}. ${detailResponse.text}`);
  }

  return detailResponse.body;
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

async function createPublishedEngineeringSnapshot(definition) {
  const suffix = Date.now().toString(36);
  const recipeId = `recipe-desktop-process-${suffix}`;
  const stationProfileId = `station-desktop-process-${suffix}`;
  const workspaceId = `workspace-desktop-process-${suffix}`;
  const projectId = `project-desktop-process-${suffix}`;
  const configurationSnapshotId = `snapshot-desktop-process-${suffix}`;

  await expectApiStatus(
    '/api/engineering/recipes',
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
    `/api/engineering/recipes/${recipeId}/publish`,
    { method: 'POST' },
    200,
    'publish runtime recipe');
  await expectApiStatus(
    '/api/engineering/station-profiles',
    {
      method: 'POST',
      body: {
        stationProfileId,
        displayName: 'Desktop Process Runtime Station',
        deviceBindings: [
          {
            deviceBindingId: 'loopback-primary',
            capabilityId: 'device.loopback',
            deviceKey: 'loopback-01'
          }
        ]
      }
    },
    201,
    'create runtime station profile');
  await expectApiStatus(
    '/api/engineering/workspaces',
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
    '/api/engineering/projects',
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
    `/api/engineering/projects/${projectId}/configuration-snapshots`,
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

async function apiRequest(path, options = {}) {
  return evaluate(`window.openlineopsDesktop.apiRequest(${JSON.stringify(path)}, ${JSON.stringify(options)})`);
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
    runDisabled: document.querySelector('[data-testid="run-simulation"]')?.disabled ?? null,
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
        runDisabled: document.querySelector('[data-testid="run-simulation"]')?.disabled ?? null,
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
      const target = targets.find(item => item.type === 'page' && item.url.startsWith(previewUrl));
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
