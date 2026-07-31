const forbiddenRequestPathCharacters = /[\\#\u0000-\u001f\u007f]/u;
const forbiddenEncodedRequestPathCharacters = /%(?:0[0-9A-F]|1[0-9A-F]|2F|5C|7F)/iu;
const absoluteUrlPrefix = /^[A-Za-z][A-Za-z0-9+.-]*:/u;
const exactEmergencyStopPath = /^\/api\/operations\/stations\/([^/?#]+)\/emergency-stop$/u;

export type BackendCredentialMode = 'route' | 'standard';

export interface AuthenticatedBackendRequest {
  apiBaseUrl: string;
  requestPath: string;
  standardToken: string;
  safetyToken: string;
  credentialMode: BackendCredentialMode;
  assertSessionActive: () => void;
  init?: RequestInit;
}

export function canonicalizeLocalBackendBaseUrl(value: string): string {
  if (typeof value !== 'string'
      || value.length === 0
      || value.trim() !== value
      || forbiddenRequestPathCharacters.test(value)) {
    throw new Error('OpenLineOps API base URL must be canonical local HTTP text.');
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('OpenLineOps API base URL must be one absolute local HTTP URL.');
  }

  if ((url.protocol !== 'http:' && url.protocol !== 'https:')
      || !isLoopbackHostname(url.hostname)
      || url.username.length > 0
      || url.password.length > 0
      || (url.pathname !== '/' && url.pathname !== '')
      || url.search.length > 0
      || url.hash.length > 0
      || (value !== url.origin && value !== `${url.origin}/`)) {
    throw new Error('OpenLineOps API base URL must be one canonical loopback origin.');
  }

  return url.origin;
}

export function resolveCanonicalBackendApiUrl(apiBaseUrl: string, requestPath: string): URL {
  const baseOrigin = canonicalizeLocalBackendBaseUrl(apiBaseUrl);
  if (typeof requestPath !== 'string'
      || requestPath.length === 0
      || requestPath.length > 8192
      || !requestPath.startsWith('/api/')
      || requestPath.startsWith('//')
      || absoluteUrlPrefix.test(requestPath)
      || forbiddenRequestPathCharacters.test(requestPath)
      || forbiddenEncodedRequestPathCharacters.test(requestPath)) {
    throw new Error('Backend API request path must be one canonical relative /api/ path.');
  }

  for (const escape of requestPath.matchAll(/%([0-9A-Fa-f]{2})/gu)) {
    if (escape[1] !== escape[1].toUpperCase()) {
      throw new Error('Backend API request path percent escapes must be uppercase.');
    }
  }
  if (requestPath.includes('%') && /%(?![0-9A-F]{2})/u.test(requestPath)) {
    throw new Error('Backend API request path contains an invalid percent escape.');
  }

  let url: URL;
  try {
    url = new URL(requestPath, `${baseOrigin}/`);
  } catch {
    throw new Error('Backend API request path is not a valid relative URL.');
  }
  const base = new URL(baseOrigin);
  if (url.protocol !== base.protocol
      || url.hostname !== base.hostname
      || url.port !== base.port
      || url.host !== base.host
      || url.origin !== base.origin
      || url.username.length > 0
      || url.password.length > 0
      || url.hash.length > 0
      || `${url.pathname}${url.search}` !== requestPath) {
    throw new Error('Backend API request URL escaped or normalized outside its canonical local origin.');
  }

  return url;
}

export function isExactEmergencyStopUrl(url: URL): boolean {
  if (url.search.length > 0 || url.hash.length > 0) {
    return false;
  }

  const match = exactEmergencyStopPath.exec(url.pathname);
  return match !== null
    && match[1] !== '.'
    && match[1] !== '..';
}

export async function fetchAuthenticatedBackend(
  request: AuthenticatedBackendRequest
): Promise<Response> {
  const url = resolveCanonicalBackendApiUrl(request.apiBaseUrl, request.requestPath);
  request.assertSessionActive();
  const token = request.credentialMode === 'route' && isExactEmergencyStopUrl(url)
    ? request.safetyToken
    : request.standardToken;
  const headers = new Headers(request.init?.headers);
  headers.set('authorization', `Bearer ${token}`);
  const response = await fetch(url, {
    ...request.init,
    headers,
    redirect: 'manual'
  });
  if (response.status >= 300 && response.status <= 399) {
    await response.body?.cancel();
    throw new Error(`Backend API redirects are forbidden (HTTP ${response.status}).`);
  }

  return response;
}

function isLoopbackHostname(hostname: string): boolean {
  return hostname === '127.0.0.1'
    || hostname === 'localhost'
    || hostname === '[::1]';
}
