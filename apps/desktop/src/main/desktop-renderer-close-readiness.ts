export class DesktopRendererCloseReadiness {
  private currentGeneration = 1;
  private loadCompletedGeneration: number | null = null;
  private coordinatorRegisteredGeneration: number | null = null;
  private responsive = true;

  public startNavigation(): number {
    this.currentGeneration += 1;
    this.loadCompletedGeneration = null;
    this.coordinatorRegisteredGeneration = null;
    this.responsive = true;
    return this.currentGeneration;
  }

  public completeLoad(rendererGeneration: number): boolean {
    assertRendererGeneration(rendererGeneration);
    if (rendererGeneration !== this.currentGeneration) {
      return false;
    }
    this.loadCompletedGeneration = rendererGeneration;
    return true;
  }

  public setCoordinatorRegistered(
    rendererGeneration: number,
    registered: boolean
  ): boolean {
    assertRendererGeneration(rendererGeneration);
    if (typeof registered !== 'boolean') {
      throw new Error('Desktop renderer close coordinator registration must be boolean.');
    }
    if (rendererGeneration !== this.currentGeneration) {
      return false;
    }
    this.coordinatorRegisteredGeneration = registered
      ? rendererGeneration
      : null;
    return true;
  }

  public setResponsive(rendererGeneration: number, responsive: boolean): boolean {
    assertRendererGeneration(rendererGeneration);
    if (typeof responsive !== 'boolean') {
      throw new Error('Desktop renderer responsiveness must be boolean.');
    }
    if (rendererGeneration !== this.currentGeneration) {
      return false;
    }
    this.responsive = responsive;
    return true;
  }

  public get isReady(): boolean {
    return this.responsive
      && this.loadCompletedGeneration === this.currentGeneration
      && this.coordinatorRegisteredGeneration === this.currentGeneration;
  }

  public get generation(): number {
    return this.currentGeneration;
  }
}

function assertRendererGeneration(rendererGeneration: number): void {
  if (!Number.isSafeInteger(rendererGeneration) || rendererGeneration <= 0) {
    throw new Error(
      'Desktop renderer close readiness generation must be a positive safe integer.');
  }
}
