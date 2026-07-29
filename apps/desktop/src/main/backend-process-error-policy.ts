export type BackendProcessErrorDisposition =
  | 'ReleaseFailedSpawn'
  | 'ReleaseExitedProcess'
  | 'RetainLiveProcess';

export interface BackendProcessErrorState {
  readonly exitCode: number | null;
  readonly signalCode: string | null;
}

export function classifyBackendProcessError(
  processState: BackendProcessErrorState,
  spawnConfirmed: boolean
): BackendProcessErrorDisposition {
  if (!spawnConfirmed) {
    return 'ReleaseFailedSpawn';
  }
  if (processState.exitCode !== null || processState.signalCode !== null) {
    return 'ReleaseExitedProcess';
  }
  return 'RetainLiveProcess';
}
