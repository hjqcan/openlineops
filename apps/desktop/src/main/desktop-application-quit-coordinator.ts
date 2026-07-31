export type DesktopApplicationQuitPhase =
  | 'Idle'
  | 'WaitingForWindowClose'
  | 'StoppingBackend'
  | 'ShutdownFailed'
  | 'Finalizing';

export type DesktopApplicationBeforeQuitAction =
  | 'BeginCoordinatedWindowClose'
  | 'WaitForCoordinatedWindowClose'
  | 'BeginFinalShutdown'
  | 'WaitForFinalShutdown'
  | 'AllowFinalShutdown';

export class DesktopApplicationQuitCoordinator {
  private currentPhase: DesktopApplicationQuitPhase = 'Idle';

  public handleBeforeQuit(hasLiveWindow: boolean): DesktopApplicationBeforeQuitAction {
    if (typeof hasLiveWindow !== 'boolean') {
      throw new Error('Desktop application quit live-window state must be boolean.');
    }

    if (this.currentPhase === 'Finalizing') {
      return 'AllowFinalShutdown';
    }

    if (this.currentPhase === 'StoppingBackend') {
      return 'WaitForFinalShutdown';
    }

    if (this.currentPhase === 'ShutdownFailed') {
      this.currentPhase = hasLiveWindow
        ? 'WaitingForWindowClose'
        : 'StoppingBackend';
      return hasLiveWindow
        ? 'BeginCoordinatedWindowClose'
        : 'BeginFinalShutdown';
    }

    if (!hasLiveWindow) {
      this.currentPhase = 'StoppingBackend';
      return 'BeginFinalShutdown';
    }

    if (this.currentPhase === 'WaitingForWindowClose') {
      return 'WaitForCoordinatedWindowClose';
    }

    this.currentPhase = 'WaitingForWindowClose';
    return 'BeginCoordinatedWindowClose';
  }

  public cancelCoordinatedWindowClose(): boolean {
    if (this.currentPhase !== 'WaitingForWindowClose') {
      return false;
    }

    this.currentPhase = 'Idle';
    return true;
  }

  public failFinalShutdown(): boolean {
    if (this.currentPhase !== 'StoppingBackend') {
      return false;
    }

    this.currentPhase = 'ShutdownFailed';
    return true;
  }

  public resumeAfterFailedShutdown(): boolean {
    if (this.currentPhase !== 'ShutdownFailed') {
      return false;
    }

    this.currentPhase = 'Idle';
    return true;
  }

  public completeFinalShutdown(): boolean {
    if (this.currentPhase !== 'StoppingBackend') {
      return false;
    }

    this.currentPhase = 'Finalizing';
    return true;
  }

  public get phase(): DesktopApplicationQuitPhase {
    return this.currentPhase;
  }
}
