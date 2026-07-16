import { createHash, timingSafeEqual } from 'node:crypto';

export function canonicalizeTrustedDevRendererUrl(value: string): string {
  if (typeof value !== 'string'
      || value.length === 0
      || value.trim() !== value
      || /[\\\u0000-\u001f\u007f]/u.test(value)) {
    throw new Error('Vite renderer URL must be canonical local HTTP text.');
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('Vite renderer URL must be one absolute local HTTP URL.');
  }
  const loopback = url.hostname === '127.0.0.1'
    || url.hostname === 'localhost'
    || url.hostname === '[::1]';
  if ((url.protocol !== 'http:' && url.protocol !== 'https:')
      || !loopback
      || url.username.length > 0
      || url.password.length > 0
      || (url.pathname !== '/' && url.pathname !== '')
      || url.search.length > 0
      || url.hash.length > 0
      || (value !== url.origin && value !== `${url.origin}/`)) {
    throw new Error('Vite renderer URL must be one canonical loopback origin.');
  }

  return `${url.origin}/`;
}

export function isTrustedRendererDocumentUrl(candidate: string, trustedUrl: string): boolean {
  if (typeof candidate !== 'string'
      || candidate.length === 0
      || /[\u0000-\u001f\u007f]/u.test(candidate)) {
    return false;
  }

  try {
    const candidateUrl = new URL(candidate);
    const expectedUrl = new URL(trustedUrl);
    return candidateUrl.href === expectedUrl.href
      && candidateUrl.username.length === 0
      && candidateUrl.password.length === 0
      && candidateUrl.hash.length === 0;
  } catch {
    return false;
  }
}

export function isTrustedRendererIpcContext(
  senderFrameUrl: string,
  senderDocumentUrl: string,
  trustedUrl: string
): boolean {
  return isTrustedRendererDocumentUrl(senderFrameUrl, trustedUrl)
    && isTrustedRendererDocumentUrl(senderDocumentUrl, trustedUrl);
}

export function rendererNonceProof(nonce: string): string {
  if (!/^[A-Za-z0-9_-]{43}$/u.test(nonce)) {
    throw new Error('Renderer startup nonce must encode exactly 256 bits as base64url.');
  }

  return createHash('sha256')
    .update('OpenLineOps renderer startup\0', 'utf8')
    .update(nonce, 'utf8')
    .digest('base64url');
}

export async function verifyTrustedDevRendererServer(
  rendererUrl: string,
  nonce: string,
  timeoutMs = 3000
): Promise<void> {
  const trustedUrl = canonicalizeTrustedDevRendererUrl(rendererUrl);
  const expectedProof = Buffer.from(rendererNonceProof(nonce), 'utf8');
  const response = await fetch(trustedUrl, {
    method: 'GET',
    redirect: 'manual',
    cache: 'no-store',
    signal: AbortSignal.timeout(timeoutMs)
  });
  if (response.status !== 200) {
    await response.body?.cancel();
    throw new Error(`Trusted renderer startup probe failed with HTTP ${response.status}.`);
  }

  const receivedValue = response.headers.get('x-openlineops-renderer-proof') ?? '';
  const receivedProof = Buffer.from(receivedValue, 'utf8');
  if (receivedProof.byteLength !== expectedProof.byteLength
      || !timingSafeEqual(receivedProof, expectedProof)) {
    await response.body?.cancel();
    throw new Error('Trusted renderer startup proof did not match this Electron launch.');
  }
  await response.body?.cancel();
}
