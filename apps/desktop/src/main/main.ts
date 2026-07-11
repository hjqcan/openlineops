import { app, BrowserWindow, dialog, ipcMain } from 'electron';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import {
  createReadStream,
  existsSync,
  mkdirSync,
  openAsBlob,
  readFileSync,
  statSync,
  writeFileSync
} from 'node:fs';
import {
  createHash,
  createPrivateKey,
  createPublicKey,
  generateKeyPairSync
} from 'node:crypto';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import type {
  ApiRequestOptions,
  ApiResponse,
  BackendStatus,
  DesktopConfig,
  ExternalProgramUploadFile,
  SelectDirectoryOptions,
  SelectDirectoryResult,
  SelectExternalProgramFilesOptions,
  SelectFilesResult,
  SelectProjectFileOptions
} from '../shared/desktop-api.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

let mainWindow: BrowserWindow | null = null;
let backendProcess: ChildProcessWithoutNullStreams | null = null;
let backendStartedAtUtc: string | null = null;
let lastExitCode: number | null = null;
let closeRequestSequence = 0;
let pendingCloseRequestId: number | null = null;
let closeApproved = false;
const recentLogs: string[] = [];

const config = createDesktopConfig();
const backendLaunch = createBackendLaunchConfig();

interface BackendLaunchConfig {
  executablePath: string;
  arguments: string[];
  workingDirectory: string;
  environment: NodeJS.ProcessEnv;
}

interface LocalStationPackageProvisioning {
  environment: NodeJS.ProcessEnv;
}

function createDesktopConfig(): DesktopConfig {
  const apiBaseUrl = trimTrailingSlash(process.env.OPENLINEOPS_API_BASE_URL ?? 'http://localhost:5135');
  const logPath = process.env.OPENLINEOPS_DESKTOP_LOG_PATH
    ? path.resolve(process.env.OPENLINEOPS_DESKTOP_LOG_PATH)
    : path.join(app.getPath('userData'), 'logs');

  return {
    apiBaseUrl,
    logPath,
    isPackaged: app.isPackaged
  };
}

function createBackendLaunchConfig(): BackendLaunchConfig {
  const stationPackages = provisionLocalStationPackages();
  if (!app.isPackaged) {
    const appPath = app.getAppPath();
    const dataDirectory = path.join(app.getPath('userData'), 'data');
    mkdirSync(dataDirectory, { recursive: true });
    const repoRoot = process.env.OPENLINEOPS_REPO_ROOT
      ? path.resolve(process.env.OPENLINEOPS_REPO_ROOT)
      : path.resolve(appPath, '..', '..');
    const apiProjectPath = process.env.OPENLINEOPS_API_PROJECT
      ? path.resolve(process.env.OPENLINEOPS_API_PROJECT)
      : path.join(repoRoot, 'src', 'OpenLineOps.Api', 'OpenLineOps.Api.csproj');

    return {
      executablePath: 'dotnet',
      arguments: ['run', '--project', apiProjectPath, '--urls', config.apiBaseUrl],
      workingDirectory: repoRoot,
      environment: {
        ...process.env,
        ...stationPackages.environment,
        ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT ?? 'Development',
        OpenLineOps__Runtime__Scripting__Python__ExecutionMode: 'ProcessIsolated',
        OpenLineOps__Runtime__Scripting__Python__WorkerFileName: 'dotnet',
        OpenLineOps__Runtime__Scripting__Python__WorkerArguments:
          `run --project "${path.join(repoRoot, 'src', 'OpenLineOps.ScriptWorker', 'OpenLineOps.ScriptWorker.csproj')}" --no-launch-profile`,
        OpenLineOps__Runtime__Scripting__Python__WorkerWorkingDirectory: repoRoot,
        OpenLineOps__Runtime__Scripting__Python__Sandbox__IsolationMode: 'ExternalProcess',
        OpenLineOps__Runtime__Scripting__Python__Sandbox__RequireLeastPrivilegeExecution: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireAppContainerIsolation: 'true',
        OpenLineOps__Devices__ExternalProgramHost__WorkspaceRootPath: path.join(
          dataDirectory,
          'external-program-workspaces'),
        OpenLineOps__Devices__ExternalProgramHost__EvidenceRootPath: path.join(
          dataDirectory,
          'external-program-evidence'),
        OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileName:
          'OpenLineOps.Studio.ExternalPrograms'
      }
    };
  }

  const runtimeRoot = path.join(app.getAppPath(), 'runtime');
  const apiDirectory = path.join(runtimeRoot, 'api');
  const scriptWorkerDirectory = path.join(runtimeRoot, 'script-worker');
  const dataDirectory = path.join(app.getPath('userData'), 'data');
  mkdirSync(dataDirectory, { recursive: true });

  return {
    executablePath: path.join(apiDirectory, 'OpenLineOps.Api.exe'),
    arguments: ['--urls', config.apiBaseUrl],
    workingDirectory: apiDirectory,
    environment: {
      ...process.env,
      ...stationPackages.environment,
      ASPNETCORE_ENVIRONMENT: 'Production',
      DOTNET_ENVIRONMENT: 'Production',
      OpenLineOps__Runtime__Persistence__DatabasePath: path.join(
        dataDirectory,
        'openlineops-runtime.sqlite'),
      OpenLineOps__Runtime__Coordination__Provider: 'Sqlite',
      OpenLineOps__Runtime__Coordination__SqliteDatabasePath: path.join(
        dataDirectory,
        'openlineops-production-coordination.sqlite'),
      OpenLineOps__Runtime__AgentTransport__Provider: 'Disabled',
      OpenLineOps__Runtime__StationExecution__Provider: 'InProcess',
      OpenLineOps__Traceability__Persistence__DatabasePath: path.join(
        dataDirectory,
        'openlineops-traceability.sqlite'),
      OpenLineOps__Traceability__ArtifactStorage__RootPath: path.join(
        dataDirectory,
        'trace-artifacts'),
      OpenLineOps__Devices__Persistence__DatabasePath: path.join(
        dataDirectory,
        'openlineops-devices.sqlite'),
      OpenLineOps__Operations__Persistence__DatabasePath: path.join(
        dataDirectory,
        'openlineops-operations.sqlite'),
      OpenLineOps__Plugins__EventLog__DatabasePath: path.join(
        dataDirectory,
        'openlineops-plugin-events.sqlite'),
      OpenLineOps__Plugins__PackageRoot: path.join(runtimeRoot, 'plugins'),
      OpenLineOps__Runtime__Scripting__Python__ExecutionMode: 'ProcessIsolated',
      OpenLineOps__Runtime__Scripting__Python__WorkerFileName: path.join(
        scriptWorkerDirectory,
        'OpenLineOps.ScriptWorker.exe'),
      OpenLineOps__Runtime__Scripting__Python__WorkerWorkingDirectory: scriptWorkerDirectory,
      OpenLineOps__Runtime__Scripting__Python__Sandbox__IsolationMode: 'ExternalProcess',
      OpenLineOps__Runtime__Scripting__Python__Sandbox__RequireLeastPrivilegeExecution: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireAppContainerIsolation: 'true',
      OpenLineOps__Devices__ExternalProgramHost__WorkspaceRootPath: path.join(
        dataDirectory,
        'external-program-workspaces'),
      OpenLineOps__Devices__ExternalProgramHost__EvidenceRootPath: path.join(
        dataDirectory,
        'external-program-evidence'),
      OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileName:
        'OpenLineOps.Studio.ExternalPrograms',
      OpenLineOps__Desktop__AllowedOrigins__0: 'null'
    }
  };
}

function provisionLocalStationPackages(): LocalStationPackageProvisioning {
  const root = path.join(app.getPath('userData'), 'data', 'station-packages');
  const keyDirectory = path.join(root, 'keys');
  const distributionDirectory = path.join(root, 'distribution');
  const deploymentCatalogDirectory = path.join(root, 'deployment-catalog');
  const privateKeyPath = path.join(keyDirectory, 'release-signing-private.pem');
  const publicKeyPath = path.join(keyDirectory, 'release-signing-public.pem');
  mkdirSync(keyDirectory, { recursive: true });
  mkdirSync(distributionDirectory, { recursive: true });
  mkdirSync(deploymentCatalogDirectory, { recursive: true });

  const privateKeyExists = existsSync(privateKeyPath);
  const publicKeyExists = existsSync(publicKeyPath);
  if (privateKeyExists !== publicKeyExists) {
    throw new Error(
      `Local Station package signing identity is incomplete under ${keyDirectory}. `
      + 'Restore both key files or remove both to provision a new identity.');
  }

  if (!privateKeyExists) {
    const pair = generateKeyPairSync('rsa', {
      modulusLength: 3072,
      publicKeyEncoding: { type: 'spki', format: 'pem' },
      privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
    });
    writeFileSync(privateKeyPath, pair.privateKey, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    writeFileSync(publicKeyPath, pair.publicKey, { encoding: 'utf8', flag: 'wx' });
  }

  const privateKeyPem = readFileSync(privateKeyPath, 'utf8');
  const publicKeyPem = readFileSync(publicKeyPath, 'utf8');
  const privateKey = createPrivateKey(privateKeyPem);
  const derivedPublicKeyPem = createPublicKey(privateKey)
    .export({ type: 'spki', format: 'pem' })
    .toString();
  const configuredPublicKeyPem = createPublicKey(publicKeyPem)
    .export({ type: 'spki', format: 'pem' })
    .toString();
  if (derivedPublicKeyPem !== configuredPublicKeyPem) {
    throw new Error('Local Station package signing private key does not match its trust public key.');
  }

  const keyId = `studio-${createHash('sha256')
    .update(configuredPublicKeyPem, 'utf8')
    .digest('hex')
    .slice(0, 24)}`;
  return {
    environment: {
      OpenLineOps__Projects__StationPackages__DistributionDirectory: distributionDirectory,
      OpenLineOps__Projects__StationPackages__DeploymentCatalogDirectory:
        deploymentCatalogDirectory,
      OpenLineOps__Projects__StationPackages__SigningKeyId: keyId,
      OpenLineOps__Projects__StationPackages__SigningPrivateKeyPath: privateKeyPath,
      OpenLineOps__Runtime__AgentTransport__DeploymentCatalogDirectory:
        deploymentCatalogDirectory,
      OpenLineOps__Agent__PackageDistributionDirectory: distributionDirectory,
      [`OpenLineOps__Agent__TrustedPackagePublicKeyFiles__${keyId}`]: publicKeyPath
    }
  };
}

async function createWindow(): Promise<void> {
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1180,
    minHeight: 760,
    title: 'OpenLineOps',
    backgroundColor: '#f6f8fb',
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  });

  mainWindow.on('close', event => {
    if (closeApproved || !mainWindow || mainWindow.webContents.isDestroyed()) {
      return;
    }
    event.preventDefault();
    if (pendingCloseRequestId !== null) {
      return;
    }
    pendingCloseRequestId = ++closeRequestSequence;
    mainWindow.webContents.send('desktop:close-requested', pendingCloseRequestId);
  });
  mainWindow.on('closed', () => {
    mainWindow = null;
    pendingCloseRequestId = null;
    closeApproved = false;
  });

  const devServerUrl = process.env.VITE_DEV_SERVER_URL ?? 'http://127.0.0.1:5173';
  if (!app.isPackaged) {
    await mainWindow.loadURL(devServerUrl);
    return;
  }

  await mainWindow.loadFile(path.join(__dirname, '..', '..', 'dist', 'index.html'));
}

ipcMain.handle('desktop:get-config', () => config);
ipcMain.handle('backend:get-status', async () => getBackendStatus());
ipcMain.handle('backend:start', async () => startBackend());
ipcMain.on('desktop:close-response', (event, requestId: number, allowClose: boolean) => {
  if (!mainWindow
      || event.sender !== mainWindow.webContents
      || requestId !== pendingCloseRequestId) {
    return;
  }
  pendingCloseRequestId = null;
  if (!allowClose) {
    return;
  }
  closeApproved = true;
  mainWindow.close();
});

async function startBackend(): Promise<BackendStatus> {
  if (!backendProcess) {
    backendProcess = spawn(
      backendLaunch.executablePath,
      backendLaunch.arguments,
      {
        cwd: backendLaunch.workingDirectory,
        env: backendLaunch.environment,
        windowsHide: true
      });
    backendStartedAtUtc = new Date().toISOString();
    lastExitCode = null;

    backendProcess.stdout.on('data', chunk => appendLog(chunk.toString()));
    backendProcess.stderr.on('data', chunk => appendLog(chunk.toString()));
    backendProcess.on('error', error => {
      appendLog(`OpenLineOps.Api failed to start: ${error.message}`);
      backendProcess = null;
      backendStartedAtUtc = null;
      lastExitCode = -1;
    });
    backendProcess.on('exit', code => {
      lastExitCode = code;
      backendProcess = null;
      backendStartedAtUtc = null;
      appendLog(`OpenLineOps.Api exited with code ${code ?? 'unknown'}.`);
    });
  }

  return getBackendStatus();
}
ipcMain.handle('backend:stop', async () => {
  if (backendProcess) {
    backendProcess.kill();
    backendProcess = null;
    backendStartedAtUtc = null;
  }

  return getBackendStatus();
});
ipcMain.handle('desktop:select-directory', async (
  _event,
  options?: SelectDirectoryOptions
): Promise<SelectDirectoryResult> => {
  const properties: Array<'openDirectory' | 'createDirectory'> = ['openDirectory'];
  if (options?.createDirectory) {
    properties.push('createDirectory');
  }

  const dialogOptions = {
    title: options?.title ?? 'Select project folder',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Select',
    properties
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-project-file', async (
  _event,
  options?: SelectProjectFileOptions
): Promise<SelectDirectoryResult> => {
  const dialogOptions = {
    title: options?.title ?? 'Open OpenLineOps project',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Open Project',
    properties: ['openFile'] as Array<'openFile'>,
    filters: [
      { name: 'OpenLineOps Projects', extensions: ['oloproj'] }
    ]
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-application-project-file', async (
  _event,
  options?: SelectProjectFileOptions
): Promise<SelectDirectoryResult> => {
  const dialogOptions = {
    title: options?.title ?? 'Add existing OpenLineOps Application',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Add Application',
    properties: ['openFile'] as Array<'openFile'>,
    filters: [
      { name: 'OpenLineOps Applications', extensions: ['oloapp'] }
    ]
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-external-program-files', async (
  _event,
  options?: SelectExternalProgramFilesOptions
): Promise<SelectFilesResult> => {
  const properties: Array<'openFile' | 'multiSelections'> = ['openFile'];
  if (options?.multiple) {
    properties.push('multiSelections');
  }
  const dialogOptions = {
    title: options?.title ?? 'Import external program files',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Import',
    properties
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);
  return { canceled: result.canceled, paths: result.filePaths };
});
ipcMain.handle('api:request', async (_event, requestPath: string, options?: ApiRequestOptions) =>
  apiRequest(requestPath, options));
ipcMain.handle('api:upload-external-program', async (
  _event,
  requestPath: string,
  definition: unknown | null,
  files: ExternalProgramUploadFile[],
  headers?: Record<string, string>
) => uploadExternalProgram(requestPath, definition, files, headers));

app.whenReady().then(async () => {
  await startBackend();
  await createWindow();

  app.on('activate', async () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      await createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

app.on('before-quit', () => {
  if (backendProcess) {
    backendProcess.kill();
    backendProcess = null;
  }
});

async function getBackendStatus(): Promise<BackendStatus> {
  const health = await probeHealth();

  return {
    isRunning: backendProcess !== null,
    pid: backendProcess?.pid ?? null,
    health,
    apiBaseUrl: config.apiBaseUrl,
    startedAtUtc: backendStartedAtUtc,
    lastExitCode,
    recentLogs: recentLogs.slice(-80)
  };
}

async function apiRequest<T = unknown>(
  requestPath: string,
  options: ApiRequestOptions = {}
): Promise<ApiResponse<T>> {
  const url = new URL(requestPath, `${config.apiBaseUrl}/`);
  const headers = new Headers(options.headers ?? {});
  let body: BodyInit | undefined;

  if (options.body !== undefined) {
    headers.set('content-type', headers.get('content-type') ?? 'application/json');
    body = JSON.stringify(options.body);
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? (body ? 'POST' : 'GET'),
      headers,
      body
    });
  } catch (error) {
    return {
      ok: false,
      status: 0,
      body: null,
      text: error instanceof Error ? error.message : String(error)
    };
  }

  const text = await response.text();

  return {
    ok: response.ok,
    status: response.status,
    body: parseBody<T>(text),
    text
  };
}

async function uploadExternalProgram<T = unknown>(
  requestPath: string,
  definition: unknown | null,
  files: ExternalProgramUploadFile[],
  requestHeaders: Record<string, string> = {}
): Promise<ApiResponse<T>> {
  if (files.length === 0 || files.length > 64) {
    throw new Error('External program uploads require between one and 64 files.');
  }

  const form = new FormData();
  if (definition !== null) {
    form.set('definition', JSON.stringify(definition));
  }
  const manifest = [];
  for (const [index, file] of files.entries()) {
    const fieldName = `file-${index + 1}`;
    const metadata = statSync(file.sourcePath, { throwIfNoEntry: false });
    if (!metadata?.isFile()) {
      throw new Error(`External program upload source is not a regular file: ${file.sourcePath}`);
    }
    const sha256 = await calculateFileSha256(file.sourcePath);
    manifest.push({
      fieldName,
      resourceRelativePath: file.resourceRelativePath,
      sizeBytes: metadata.size,
      sha256
    });
    form.append(fieldName, await openAsBlob(file.sourcePath), path.basename(file.sourcePath));
  }
  form.set('uploadManifest', JSON.stringify(manifest));

  const url = new URL(requestPath, `${config.apiBaseUrl}/`);
  try {
    const response = await fetch(url, { method: 'POST', body: form, headers: requestHeaders });
    const text = await response.text();
    return {
      ok: response.ok,
      status: response.status,
      body: parseBody<T>(text),
      text
    };
  } catch (error) {
    return {
      ok: false,
      status: 0,
      body: null,
      text: error instanceof Error ? error.message : String(error)
    };
  }
}

async function calculateFileSha256(filePath: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) {
    hash.update(chunk);
  }
  return hash.digest('hex');
}

async function probeHealth(): Promise<BackendStatus['health']> {
  try {
    const response = await fetch(`${config.apiBaseUrl}/health/live`, {
      signal: AbortSignal.timeout(1000)
    });

    return response.ok ? 'Healthy' : 'Unreachable';
  } catch {
    return 'Unreachable';
  }
}

function appendLog(value: string): void {
  for (const line of value.split(/\r?\n/)) {
    const normalized = line.trim();
    if (normalized.length > 0) {
      recentLogs.push(normalized);
    }
  }

  if (recentLogs.length > 200) {
    recentLogs.splice(0, recentLogs.length - 200);
  }
}

function parseBody<T>(text: string): T | null {
  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

function trimTrailingSlash(value: string): string {
  return value.endsWith('/') ? value.slice(0, -1) : value;
}
