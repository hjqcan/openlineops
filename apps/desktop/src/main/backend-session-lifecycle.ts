export interface PendingBackendSession<TProcess extends object> {
  readonly process: TProcess;
  readonly standardToken: string;
  readonly safetyToken: string;
  readonly nonce: string;
  readonly handshakePath: string;
}

export interface AuthenticatedBackendSession<TProcess extends object>
  extends PendingBackendSession<TProcess> {
  readonly apiBaseUrl: string;
}

type BackendSessionState<TProcess extends object> =
  | { readonly kind: 'Empty' }
  | {
      readonly kind: 'Pending';
      readonly session: PendingBackendSession<TProcess>;
    }
  | {
      readonly kind: 'Authenticated';
      readonly session: AuthenticatedBackendSession<TProcess>;
    };

export class BackendSessionLifecycle<TProcess extends object> {
  private state: BackendSessionState<TProcess> = { kind: 'Empty' };

  get pending(): PendingBackendSession<TProcess> | null {
    return this.state.kind === 'Pending'
      ? this.state.session
      : null;
  }

  get authenticated(): AuthenticatedBackendSession<TProcess> | null {
    return this.state.kind === 'Authenticated'
      ? this.state.session
      : null;
  }

  get process(): TProcess | null {
    return this.state.kind === 'Empty'
      ? null
      : this.state.session.process;
  }

  beginPending(
    session: PendingBackendSession<TProcess>
  ): PendingBackendSession<TProcess> {
    if (this.state.kind !== 'Empty') {
      throw new Error('A backend process session is already retained.');
    }
    if (!session.standardToken
        || !session.safetyToken
        || session.standardToken === session.safetyToken
        || !session.nonce
        || !session.handshakePath) {
      throw new Error(
        'A pending backend session requires distinct credentials, one nonce, and one handshake path.');
    }

    const retained = Object.freeze({ ...session });
    this.state = {
      kind: 'Pending',
      session: retained
    };
    return retained;
  }

  authenticate(
    process: TProcess,
    apiBaseUrl: string
  ): AuthenticatedBackendSession<TProcess> {
    if (this.state.kind !== 'Pending'
        || this.state.session.process !== process) {
      throw new Error(
        'Only the exact pending backend process can become authenticated.');
    }
    if (!apiBaseUrl) {
      throw new Error('An authenticated backend session requires its API origin.');
    }

    const authenticated = Object.freeze({
      ...this.state.session,
      apiBaseUrl
    });
    this.state = {
      kind: 'Authenticated',
      session: authenticated
    };
    return authenticated;
  }

  releaseFailedSpawn(
    process: TProcess
  ): PendingBackendSession<TProcess> | null {
    if (this.state.kind === 'Empty'
        || this.state.session.process !== process) {
      return null;
    }
    if (this.state.kind === 'Authenticated') {
      throw new Error(
        'An authenticated backend session cannot be released as a failed spawn.');
    }

    const released = this.state.session;
    this.state = { kind: 'Empty' };
    return released;
  }

  releaseConfirmedExit(
    process: TProcess,
    processExitConfirmed: boolean
  ): PendingBackendSession<TProcess> | AuthenticatedBackendSession<TProcess> | null {
    if (this.state.kind === 'Empty'
        || this.state.session.process !== process) {
      return null;
    }
    if (!processExitConfirmed) {
      throw new Error(
        'Backend identity and credentials cannot be released before confirmed exit.');
    }

    const released = this.state.session;
    this.state = { kind: 'Empty' };
    return released;
  }
}
