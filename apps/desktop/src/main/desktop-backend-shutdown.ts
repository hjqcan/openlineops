export async function stopDesktopBackendForApplicationShutdown<TBackendProcess>(
  backendProcess: TBackendProcess | null,
  terminateProcessTree: (process: TBackendProcess) => Promise<void>,
  releaseBackendState: (process: TBackendProcess | null) => void
): Promise<void> {
  if (backendProcess !== null) {
    await terminateProcessTree(backendProcess);
  }

  releaseBackendState(backendProcess);
}

export function hasDesktopBackendProcessExited(process: {
  exitCode: number | null;
  signalCode: string | null;
}): boolean {
  return process.exitCode !== null || process.signalCode !== null;
}
