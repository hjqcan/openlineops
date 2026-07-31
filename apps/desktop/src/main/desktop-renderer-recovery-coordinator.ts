export type DesktopRendererRecoveryReason =
  | 'Unresponsive'
  | 'CloseCoordinationUnavailable'
  | 'RendererGone';

export interface DesktopRendererRecoveryRequest {
  readonly rendererGeneration: number;
  readonly reason: DesktopRendererRecoveryReason;
}

export type DesktopRendererRecoveryRequestAction =
  | { action: 'Start'; request: DesktopRendererRecoveryRequest }
  | { action: 'Queue'; request: DesktopRendererRecoveryRequest }
  | { action: 'Ignore' };

const reasonPriority: Readonly<Record<DesktopRendererRecoveryReason, number>> = {
  Unresponsive: 1,
  CloseCoordinationUnavailable: 2,
  RendererGone: 3
};

export class DesktopRendererRecoveryCoordinator {
  private activeRequest: DesktopRendererRecoveryRequest | null = null;
  private queuedRequest: DesktopRendererRecoveryRequest | null = null;

  public request(
    reason: DesktopRendererRecoveryReason,
    rendererGeneration: number
  ): DesktopRendererRecoveryRequestAction {
    assertReason(reason);
    assertRendererGeneration(rendererGeneration);
    const request = { reason, rendererGeneration };

    if (this.activeRequest === null) {
      this.activeRequest = request;
      return { action: 'Start', request };
    }

    if (requestsEqual(this.activeRequest, request)
        || (this.queuedRequest !== null && requestsEqual(this.queuedRequest, request))) {
      return { action: 'Ignore' };
    }

    const comparisonRequest = this.queuedRequest ?? this.activeRequest;
    if (!requestPreferredOver(request, comparisonRequest)) {
      return { action: 'Ignore' };
    }

    this.queuedRequest = request;
    return { action: 'Queue', request };
  }

  public complete(
    request: DesktopRendererRecoveryRequest,
    currentRendererGeneration: number
  ): DesktopRendererRecoveryRequest | null {
    assertRequest(request);
    assertRendererGeneration(currentRendererGeneration);
    if (this.activeRequest === null || !requestsEqual(this.activeRequest, request)) {
      return null;
    }

    this.activeRequest = null;
    const queuedRequest = this.queuedRequest;
    this.queuedRequest = null;
    if (queuedRequest === null
        || queuedRequest.rendererGeneration !== currentRendererGeneration) {
      return null;
    }

    this.activeRequest = queuedRequest;
    return queuedRequest;
  }

  public get active(): DesktopRendererRecoveryRequest | null {
    return this.activeRequest;
  }

  public get queued(): DesktopRendererRecoveryRequest | null {
    return this.queuedRequest;
  }
}

function assertRequest(request: DesktopRendererRecoveryRequest): void {
  if (!request) {
    throw new Error('Desktop renderer recovery request is required.');
  }
  assertReason(request.reason);
  assertRendererGeneration(request.rendererGeneration);
}

function assertReason(reason: DesktopRendererRecoveryReason): void {
  if (!Object.hasOwn(reasonPriority, reason)) {
    throw new Error('Desktop renderer recovery reason is invalid.');
  }
}

function assertRendererGeneration(rendererGeneration: number): void {
  if (!Number.isSafeInteger(rendererGeneration) || rendererGeneration <= 0) {
    throw new Error(
      'Desktop renderer recovery generation must be a positive safe integer.');
  }
}

function requestsEqual(
  left: DesktopRendererRecoveryRequest,
  right: DesktopRendererRecoveryRequest
): boolean {
  return left.rendererGeneration === right.rendererGeneration
    && left.reason === right.reason;
}

function requestPreferredOver(
  candidate: DesktopRendererRecoveryRequest,
  current: DesktopRendererRecoveryRequest
): boolean {
  if (candidate.rendererGeneration !== current.rendererGeneration) {
    return candidate.rendererGeneration > current.rendererGeneration;
  }
  return reasonPriority[candidate.reason] > reasonPriority[current.reason];
}
