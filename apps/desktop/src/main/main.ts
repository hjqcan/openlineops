import { app, BrowserWindow, dialog, ipcMain } from 'electron';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import type {
  ApiRequestOptions,
  ApiResponse,
  BackendStatus,
  DesktopConfig,
  SelectDirectoryOptions,
  SelectDirectoryResult,
  SelectProjectFileOptions
} from '../shared/desktop-api.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

let mainWindow: BrowserWindow | null = null;
let backendProcess: ChildProcessWithoutNullStreams | null = null;
let backendStartedAtUtc: string | null = null;
let lastExitCode: number | null = null;
const recentLogs: string[] = [];

const config = createDesktopConfig();

function createDesktopConfig(): DesktopConfig {
  const appPath = app.getAppPath();
  const repoRoot = process.env.OPENLINEOPS_REPO_ROOT
    ? path.resolve(process.env.OPENLINEOPS_REPO_ROOT)
    : path.resolve(appPath, '..', '..');
  const apiBaseUrl = trimTrailingSlash(process.env.OPENLINEOPS_API_BASE_URL ?? 'http://localhost:5135');
  const apiProjectPath = process.env.OPENLINEOPS_API_PROJECT
    ? path.resolve(process.env.OPENLINEOPS_API_PROJECT)
    : path.join(repoRoot, 'src', 'OpenLineOps.Api', 'OpenLineOps.Api.csproj');
  const logPath = process.env.OPENLINEOPS_DESKTOP_LOG_PATH
    ? path.resolve(process.env.OPENLINEOPS_DESKTOP_LOG_PATH)
    : path.join(app.getPath('userData'), 'logs');

  return {
    apiBaseUrl,
    repoRoot,
    apiProjectPath,
    logPath,
    isPackaged: app.isPackaged
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

  const devServerUrl = process.env.VITE_DEV_SERVER_URL ?? 'http://127.0.0.1:5173';
  if (!app.isPackaged) {
    await mainWindow.loadURL(devServerUrl);
    return;
  }

  await mainWindow.loadFile(path.join(__dirname, '..', '..', 'dist', 'index.html'));
}

ipcMain.handle('desktop:get-config', () => config);
ipcMain.handle('backend:get-status', async () => getBackendStatus());
ipcMain.handle('backend:start', async () => {
  if (!backendProcess) {
    backendProcess = spawn(
      'dotnet',
      ['run', '--project', config.apiProjectPath, '--urls', config.apiBaseUrl],
      {
        cwd: config.repoRoot,
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT ?? 'Development'
        },
        windowsHide: true
      });
    backendStartedAtUtc = new Date().toISOString();
    lastExitCode = null;

    backendProcess.stdout.on('data', chunk => appendLog(chunk.toString()));
    backendProcess.stderr.on('data', chunk => appendLog(chunk.toString()));
    backendProcess.on('exit', code => {
      lastExitCode = code;
      backendProcess = null;
      backendStartedAtUtc = null;
      appendLog(`OpenLineOps.Api exited with code ${code ?? 'unknown'}.`);
    });
  }

  return getBackendStatus();
});
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
ipcMain.handle('api:request', async (_event, requestPath: string, options?: ApiRequestOptions) =>
  apiRequest(requestPath, options));

app.whenReady().then(async () => {
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
