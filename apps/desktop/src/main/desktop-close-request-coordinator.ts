export const desktopCloseRequestAcknowledgementTimeoutMilliseconds = 10_000;

export interface DesktopCloseRequestClock {
  schedule(callback: () => void, timeoutMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

const systemClock: DesktopCloseRequestClock = {
  schedule(callback, timeoutMilliseconds) {
    const timeout = setTimeout(callback, timeoutMilliseconds);
    timeout.unref();
    return timeout;
  },
  cancel(handle) {
    clearTimeout(handle as ReturnType<typeof setTimeout>);
  }
};

export type DesktopCloseRequestPhase =
  | 'WaitingForAcknowledgement'
  | 'AwaitingDecision';

export class DesktopCloseRequestCoordinator {
  private sequence = 0;
  private pendingRequestId: number | null = null;
  private pendingPhase: DesktopCloseRequestPhase | null = null;
  private timeout: unknown | null = null;

  public constructor(
    private readonly acknowledgementTimeoutMilliseconds =
      desktopCloseRequestAcknowledgementTimeoutMilliseconds,
    private readonly clock: DesktopCloseRequestClock = systemClock
  ) {
    if (!Number.isSafeInteger(acknowledgementTimeoutMilliseconds)
        || acknowledgementTimeoutMilliseconds <= 0) {
      throw new Error(
        'Desktop close request acknowledgement timeout must be a positive safe integer.');
    }
  }

  public request(onExpired: (requestId: number) => void): number | null {
    if (this.pendingRequestId !== null) {
      return null;
    }

    const requestId = ++this.sequence;
    this.pendingRequestId = requestId;
    this.pendingPhase = 'WaitingForAcknowledgement';
    this.timeout = this.clock.schedule(() => {
      if (this.pendingRequestId !== requestId
          || this.pendingPhase !== 'WaitingForAcknowledgement') {
        return;
      }

      this.pendingRequestId = null;
      this.pendingPhase = null;
      this.timeout = null;
      onExpired(requestId);
    }, this.acknowledgementTimeoutMilliseconds);
    return requestId;
  }

  public acknowledge(requestId: number): boolean {
    if (requestId !== this.pendingRequestId
        || this.pendingPhase !== 'WaitingForAcknowledgement'
        || this.timeout === null) {
      return false;
    }

    this.clock.cancel(this.timeout);
    this.timeout = null;
    this.pendingPhase = 'AwaitingDecision';
    return true;
  }

  public complete(requestId: number): boolean {
    if (requestId !== this.pendingRequestId
        || this.pendingPhase !== 'AwaitingDecision') {
      return false;
    }

    this.clearPendingRequest();
    return true;
  }

  public reset(): void {
    this.clearPendingRequest();
  }

  public get pendingId(): number | null {
    return this.pendingRequestId;
  }

  public get phase(): DesktopCloseRequestPhase | null {
    return this.pendingPhase;
  }

  private clearPendingRequest(): void {
    if (this.timeout !== null) {
      this.clock.cancel(this.timeout);
      this.timeout = null;
    }
    this.pendingRequestId = null;
    this.pendingPhase = null;
  }
}
