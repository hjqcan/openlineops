import { contextBridge, ipcRenderer } from 'electron';
import type {
  ApiRequestOptions,
  ApiResponse,
  BackendStatus,
  DesktopConfig,
  OpenLineOpsDesktopApi,
  SelectDirectoryOptions,
  SelectDirectoryResult,
  SelectProjectFileOptions
} from '../shared/desktop-api.js';

const desktopApi: OpenLineOpsDesktopApi = {
  getConfig: () => ipcRenderer.invoke('desktop:get-config') as Promise<DesktopConfig>,
  getBackendStatus: () => ipcRenderer.invoke('backend:get-status') as Promise<BackendStatus>,
  startBackend: () => ipcRenderer.invoke('backend:start') as Promise<BackendStatus>,
  stopBackend: () => ipcRenderer.invoke('backend:stop') as Promise<BackendStatus>,
  selectDirectory: (options?: SelectDirectoryOptions) =>
    ipcRenderer.invoke('desktop:select-directory', options) as Promise<SelectDirectoryResult>,
  selectProjectFile: (options?: SelectProjectFileOptions) =>
    ipcRenderer.invoke('desktop:select-project-file', options) as Promise<SelectDirectoryResult>,
  apiRequest: <T = unknown>(path: string, options?: ApiRequestOptions) =>
    ipcRenderer.invoke('api:request', path, options) as Promise<ApiResponse<T>>
};

contextBridge.exposeInMainWorld('openlineopsDesktop', desktopApi);
