import type { OpenLineOpsDesktopApi } from '../shared/desktop-api';

declare global {
  interface Window {
    openlineopsDesktop?: OpenLineOpsDesktopApi;
  }
}

export {};
