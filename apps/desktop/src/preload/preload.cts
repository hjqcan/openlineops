import { contextBridge, ipcRenderer } from 'electron';
import type {
  ApiRequestOptions,
  ApiResponse,
  ApplicationExtensionImportResult,
  BackendStatus,
  DesktopConfig,
  EditorDocumentWriteOptions,
  ExternalProgramDirectorySelectionResult,
  OpenLineOpsDesktopApi,
  SelectDirectoryOptions,
  SelectDirectoryResult,
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
  selectExternalProgramDirectory: (projectId: string, applicationId: string, resourceId: string) =>
    ipcRenderer.invoke(
      'desktop:select-external-program-directory',
      projectId,
      applicationId,
      resourceId
    ) as Promise<ExternalProgramDirectorySelectionResult>,
  releaseExternalProgramDirectorySelection: (selectionId: string) =>
    ipcRenderer.invoke('desktop:release-external-program-directory-selection', selectionId) as Promise<void>,
  importExternalProgramDirectory: <T = unknown,>(
    projectId: string,
    applicationId: string,
    definition: unknown,
    selectionId: string,
    write?: EditorDocumentWriteOptions
  ) => ipcRenderer.invoke(
    'api:import-external-program-directory',
    projectId,
    applicationId,
    definition,
    selectionId,
    write
  ) as Promise<ApiResponse<T>>,
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
