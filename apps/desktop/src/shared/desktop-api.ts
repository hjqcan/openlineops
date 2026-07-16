export interface DesktopConfig {
  apiBaseUrl: string;
  apiAccessToken: string;
  apiActorId: string;
  logPath: string;
  isPackaged: boolean;
}

export interface BackendStatus {
  isRunning: boolean;
  pid: number | null;
  health: 'Healthy' | 'Unreachable';
  apiBaseUrl: string | null;
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

export interface EditorDocumentWriteOptions {
  revision: string;
  force?: boolean;
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

export interface SelectProjectFileOptions {
  title?: string;
  defaultPath?: string;
  buttonLabel?: string;
}

export interface SelectExternalProgramFilesOptions {
  title?: string;
  defaultPath?: string;
  buttonLabel?: string;
  multiple?: boolean;
}

export interface SelectFilesResult {
  canceled: boolean;
  paths: string[];
}

export interface ExternalProgramUploadFile {
  sourcePath: string;
  resourceRelativePath: string;
}

export interface TraceArtifactSaveOptions {
  storageKey: string;
  fileName: string;
  expectedSizeBytes: number;
  expectedSha256: string;
}

export interface TraceArtifactSaveResult {
  canceled: boolean;
  path: string | null;
  sizeBytes: number | null;
  sha256: string | null;
}

export interface ApplicationExtensionImportResult<T = unknown> {
  canceled: boolean;
  portableId: string | null;
  fileName: string | null;
  sizeBytes: number | null;
  response: ApiResponse<T> | null;
}

export interface OpenLineOpsDesktopApi {
  getConfig(): Promise<DesktopConfig>;
  getBackendStatus(): Promise<BackendStatus>;
  startBackend(): Promise<BackendStatus>;
  stopBackend(): Promise<BackendStatus>;
  onCloseRequested(listener: (requestId: number) => void): () => void;
  respondToCloseRequest(requestId: number, allowClose: boolean): void;
  selectDirectory(options?: SelectDirectoryOptions): Promise<SelectDirectoryResult>;
  selectProjectFile(options?: SelectProjectFileOptions): Promise<SelectDirectoryResult>;
  selectApplicationProjectFile(options?: SelectProjectFileOptions): Promise<SelectDirectoryResult>;
  selectExternalProgramFiles(options?: SelectExternalProgramFilesOptions): Promise<SelectFilesResult>;
  uploadExternalProgram<T = unknown>(
    path: string,
    definition: unknown | null,
    files: ExternalProgramUploadFile[],
    headers?: Record<string, string>
  ): Promise<ApiResponse<T>>;
  importApplicationExtension<T = unknown>(
    projectId: string,
    applicationId: string
  ): Promise<ApplicationExtensionImportResult<T>>;
  saveTraceArtifact(options: TraceArtifactSaveOptions): Promise<TraceArtifactSaveResult>;
  apiRequest<T = unknown>(path: string, options?: ApiRequestOptions): Promise<ApiResponse<T>>;
}
