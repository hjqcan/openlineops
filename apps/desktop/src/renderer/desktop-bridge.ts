import type { OpenLineOpsDesktopApi } from '../shared/desktop-api';

const fallbackDesktopApi: OpenLineOpsDesktopApi = {
  getConfig: async () => ({
    apiBaseUrl: 'http://localhost:5135',
    repoRoot: 'browser-preview',
    apiProjectPath: 'src/OpenLineOps.Api/OpenLineOps.Api.csproj',
    logPath: 'browser-preview',
    isPackaged: false
  }),
  getBackendStatus: async () => ({
    isRunning: false,
    pid: null,
    health: 'Unreachable',
    apiBaseUrl: 'http://localhost:5135',
    startedAtUtc: null,
    lastExitCode: null,
    recentLogs: []
  }),
  startBackend: async () => ({
    isRunning: false,
    pid: null,
    health: 'Unreachable',
    apiBaseUrl: 'http://localhost:5135',
    startedAtUtc: null,
    lastExitCode: null,
    recentLogs: ['Backend lifecycle controls are available inside Electron.']
  }),
  stopBackend: async () => ({
    isRunning: false,
    pid: null,
    health: 'Unreachable',
    apiBaseUrl: 'http://localhost:5135',
    startedAtUtc: null,
    lastExitCode: null,
    recentLogs: []
  }),
  apiRequest: async () => ({
    ok: false,
    status: 503,
    body: null,
    text: ''
  })
};

export const desktop = window.openlineopsDesktop ?? fallbackDesktopApi;
