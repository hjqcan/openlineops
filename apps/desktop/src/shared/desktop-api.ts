export interface DesktopConfig {
  apiBaseUrl: string;
  repoRoot: string;
  apiProjectPath: string;
  logPath: string;
  isPackaged: boolean;
}

export interface BackendStatus {
  isRunning: boolean;
  pid: number | null;
  health: 'Healthy' | 'Unreachable';
  apiBaseUrl: string;
  startedAtUtc: string | null;
  lastExitCode: number | null;
  recentLogs: string[];
}

export interface ApiRequestOptions {
  method?: string;
  body?: unknown;
  headers?: Record<string, string>;
}

export interface ApiResponse<T = unknown> {
  ok: boolean;
  status: number;
  body: T | null;
  text: string;
}

export interface SelectDirectoryOptions {
  title?: string;
  defaultPath?: string;
  buttonLabel?: string;
  createDirectory?: boolean;
}

export interface SelectDirectoryResult {
  canceled: boolean;
  path: string | null;
}

export interface OpenLineOpsDesktopApi {
  getConfig(): Promise<DesktopConfig>;
  getBackendStatus(): Promise<BackendStatus>;
  startBackend(): Promise<BackendStatus>;
  stopBackend(): Promise<BackendStatus>;
  selectDirectory(options?: SelectDirectoryOptions): Promise<SelectDirectoryResult>;
  apiRequest<T = unknown>(path: string, options?: ApiRequestOptions): Promise<ApiResponse<T>>;
}
