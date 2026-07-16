import { contextBridge, ipcRenderer } from 'electron';
import type {
  ApiRequestOptions,
  ApiResponse,
  ApplicationExtensionImportResult,
  BackendStatus,
  DesktopConfig,
  ExternalProgramUploadFile,
  OpenLineOpsDesktopApi,
  SelectDirectoryOptions,
  SelectDirectoryResult,
  SelectExternalProgramFilesOptions,
  SelectFilesResult,
  SelectProjectFileOptions,
  TraceArtifactSaveOptions,
  TraceArtifactSaveResult
} from '../shared/desktop-api.js';

const desktopApi: OpenLineOpsDesktopApi = {
  getConfig: () => ipcRenderer.invoke('desktop:get-config') as Promise<DesktopConfig>,
  getBackendStatus: () => ipcRenderer.invoke('backend:get-status') as Promise<BackendStatus>,
  startBackend: () => ipcRenderer.invoke('backend:start') as Promise<BackendStatus>,
  stopBackend: () => ipcRenderer.invoke('backend:stop') as Promise<BackendStatus>,
  onCloseRequested: (listener: (requestId: number) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, requestId: number): void => listener(requestId);
    ipcRenderer.on('desktop:close-requested', handler);
    return () => ipcRenderer.removeListener('desktop:close-requested', handler);
  },
  respondToCloseRequest: (requestId: number, allowClose: boolean) => {
    ipcRenderer.send('desktop:close-response', requestId, allowClose);
  },
  selectDirectory: (options?: SelectDirectoryOptions) =>
    ipcRenderer.invoke('desktop:select-directory', options) as Promise<SelectDirectoryResult>,
  selectProjectFile: (options?: SelectProjectFileOptions) =>
    ipcRenderer.invoke('desktop:select-project-file', options) as Promise<SelectDirectoryResult>,
  selectApplicationProjectFile: (options?: SelectProjectFileOptions) =>
    ipcRenderer.invoke('desktop:select-application-project-file', options) as Promise<SelectDirectoryResult>,
  selectExternalProgramFiles: (options?: SelectExternalProgramFilesOptions) =>
    ipcRenderer.invoke('desktop:select-external-program-files', options) as Promise<SelectFilesResult>,
  uploadExternalProgram: <T = unknown,>(
    path: string,
    definition: unknown | null,
    files: ExternalProgramUploadFile[],
    headers?: Record<string, string>
  ) => ipcRenderer.invoke('api:upload-external-program', path, definition, files, headers) as Promise<ApiResponse<T>>,
  importApplicationExtension: <T = unknown,>(projectId: string, applicationId: string) =>
    ipcRenderer.invoke(
      'api:import-application-extension',
      projectId,
      applicationId
    ) as Promise<ApplicationExtensionImportResult<T>>,
  saveTraceArtifact: (options: TraceArtifactSaveOptions) =>
    ipcRenderer.invoke('trace:save-artifact', options) as Promise<TraceArtifactSaveResult>,
  apiRequest: <T = unknown,>(path: string, options?: ApiRequestOptions) =>
    ipcRenderer.invoke('api:request', path, options) as Promise<ApiResponse<T>>
};

contextBridge.exposeInMainWorld('openlineopsDesktop', desktopApi);
