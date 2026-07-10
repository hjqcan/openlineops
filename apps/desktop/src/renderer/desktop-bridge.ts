import type { OpenLineOpsDesktopApi } from '../shared/desktop-api';

if (!window.openlineopsDesktop) {
  throw new Error('The OpenLineOps Electron preload bridge is required.');
}

export const desktop: OpenLineOpsDesktopApi = window.openlineopsDesktop;
