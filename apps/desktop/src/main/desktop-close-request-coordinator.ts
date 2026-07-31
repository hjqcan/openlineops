import type {
  DesktopCloseCoordinatorBinding
} from '../shared/desktop-api.js';

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
  private pendingBinding: DesktopCloseCoordinatorBinding | null = null;
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

  public request(
    binding: DesktopCloseCoordinatorBinding,
    onExpired: (requestId: number) => void
  ): number | null {
    assertBinding(binding);
    if (this.pendingRequestId !== null) {
      return null;
    }

    const requestId = ++this.sequence;
    this.pendingRequestId = requestId;
    this.pendingPhase = 'WaitingForAcknowledgement';
    this.pendingBinding = binding;
    this.timeout = this.clock.schedule(() => {
      if (this.pendingRequestId !== requestId
          || this.pendingPhase !== 'WaitingForAcknowledgement'
          || !desktopCloseBindingsEqual(this.pendingBinding, binding)) {
        return;
      }

      this.pendingRequestId = null;
      this.pendingPhase = null;
      this.pendingBinding = null;
      this.timeout = null;
      onExpired(requestId);
    }, this.acknowledgementTimeoutMilliseconds);
    return requestId;
  }

  public acknowledge(binding: DesktopCloseCoordinatorBinding, requestId: number): boolean {
    assertBinding(binding);
    if (requestId !== this.pendingRequestId
        || this.pendingPhase !== 'WaitingForAcknowledgement'
        || !desktopCloseBindingsEqual(this.pendingBinding, binding)
        || this.timeout === null) {
      return false;
    }

    this.clock.cancel(this.timeout);
    this.timeout = null;
    this.pendingPhase = 'AwaitingDecision';
    return true;
  }

  public complete(binding: DesktopCloseCoordinatorBinding, requestId: number): boolean {
    assertBinding(binding);
    if (requestId !== this.pendingRequestId
        || this.pendingPhase !== 'AwaitingDecision'
        || !desktopCloseBindingsEqual(this.pendingBinding, binding)) {
      return false;
    }

    this.clearPendingRequest();
    return true;
  }

  public release(binding: DesktopCloseCoordinatorBinding): boolean {
    assertBinding(binding);
    if (!desktopCloseBindingsEqual(this.pendingBinding, binding)) {
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

  public get binding(): DesktopCloseCoordinatorBinding | null {
    return this.pendingBinding;
  }

  private clearPendingRequest(): void {
    if (this.timeout !== null) {
      this.clock.cancel(this.timeout);
      this.timeout = null;
    }
    this.pendingRequestId = null;
    this.pendingPhase = null;
    this.pendingBinding = null;
  }
}

function assertBinding(binding: DesktopCloseCoordinatorBinding): void {
  if (!binding
      || !Number.isSafeInteger(binding.windowId)
      || binding.windowId <= 0
      || !Number.isSafeInteger(binding.webContentsId)
      || binding.webContentsId <= 0
      || !Number.isSafeInteger(binding.rendererGeneration)
      || binding.rendererGeneration <= 0) {
    throw new Error(
      'Desktop close request binding must contain positive safe integer identities.');
  }
}

export function desktopCloseBindingsEqual(
  candidate: unknown,
  expected: DesktopCloseCoordinatorBinding
): boolean {
  if (candidate === null || typeof candidate !== 'object') {
    return false;
  }
  const value = candidate as Record<string, unknown>;
  return value.windowId === expected.windowId
    && value.webContentsId === expected.webContentsId
    && value.rendererGeneration === expected.rendererGeneration;
}
