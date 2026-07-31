import {
  createHmac,
  randomBytes,
  timingSafeEqual
} from 'node:crypto';
import { canonicalizeLocalBackendBaseUrl } from './backend-api-security.js';

export const backendProcessHandshakeEndpoint = '/health/desktop-process';
export const backendProcessHandshakeChallengeHeader = 'X-OpenLineOps-Desktop-Challenge';
export const backendProcessHandshakeProofHeader = 'X-OpenLineOps-Desktop-Proof';

const canonicalBase64Url256 = /^[A-Za-z0-9_-]{43}$/u;

export function computeBackendProcessHandshakeProof(
  nonce: string,
  challenge: string
): string {
  assertCanonicalBase64Url256(nonce, 'Backend process nonce');
  assertCanonicalBase64Url256(challenge, 'Backend process challenge');
  return createHmac('sha256', nonce)
    .update(challenge, 'utf8')
    .digest('base64url');
}

export async function verifyBackendProcessHandshakeServer(
  apiBaseUrl: string,
  nonce: string,
  assertProcessActive: () => void,
  timeoutMs = 3000
): Promise<void> {
  const origin = canonicalizeLocalBackendBaseUrl(apiBaseUrl);
  assertCanonicalBase64Url256(nonce, 'Backend process nonce');
  const challenge = randomBytes(32).toString('base64url');
  assertProcessActive();
  const response = await fetch(`${origin}${backendProcessHandshakeEndpoint}`, {
    method: 'GET',
    headers: { [backendProcessHandshakeChallengeHeader]: challenge },
    redirect: 'manual',
    cache: 'no-store',
    signal: AbortSignal.timeout(timeoutMs)
  });
  try {
    if (response.status !== 204) {
      throw new Error(`Spawned API handshake failed with HTTP ${response.status}.`);
    }

    const expected = Buffer.from(
      computeBackendProcessHandshakeProof(nonce, challenge),
      'utf8');
    const actual = Buffer.from(
      response.headers.get(backendProcessHandshakeProofHeader) ?? '',
      'utf8');
    if (actual.byteLength !== expected.byteLength || !timingSafeEqual(actual, expected)) {
      throw new Error('Spawned API handshake proof did not match this process launch.');
    }
    assertProcessActive();
  } finally {
    await response.body?.cancel().catch(() => undefined);
  }
}

function assertCanonicalBase64Url256(value: string, label: string): void {
  if (!canonicalBase64Url256.test(value)) {
    throw new Error(`${label} must encode exactly 256 bits as base64url.`);
  }
}
