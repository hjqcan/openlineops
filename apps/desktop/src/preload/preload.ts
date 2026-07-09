import { contextBridge, ipcRenderer } from 'electron';
import type { ApiRequestOptions, ApiResponse, BackendStatus, DesktopConfig, OpenLineOpsDesktopApi } from '../shared/desktop-api.js';

const desktopApi: OpenLineOpsDesktopApi = {
  getConfig: () => ipcRenderer.invoke('desktop:get-config') as Promise<DesktopConfig>,
  getBackendStatus: () => ipcRenderer.invoke('backend:get-status') as Promise<BackendStatus>,
  startBackend: () => ipcRenderer.invoke('backend:start') as Promise<BackendStatus>,
  stopBackend: () => ipcRenderer.invoke('backend:stop') as Promise<BackendStatus>,
  apiRequest: <T = unknown>(path: string, options?: ApiRequestOptions) =>
    ipcRenderer.invoke('api:request', path, options) as Promise<ApiResponse<T>>
};

contextBridge.exposeInMainWorld('openlineopsDesktop', desktopApi);
