const sensitiveAssignmentPattern =
  /((?:apiAccessToken|artifactUploadBearerToken|bearerToken|clientSecret|password|standardToken|safetyToken|nonce)\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s,;}]+)/giu;
const sensitiveJsonPropertyPattern =
  /("(?:apiAccessToken|artifactUploadBearerToken|bearerToken|authorization|clientSecret|password|standardToken|safetyToken|nonce)"\s*:\s*)"[^"]*"/giu;
const authorizationValuePattern =
  /(\bauthorization\s*[=:]\s*)[^\r\n,;}]+/giu;
const authorizationSchemeCredentialPattern =
  /(\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]+/giu;
const credentialUriPattern =
  /(\b[a-z][a-z0-9+.-]*:\/\/)[^/@\s]+@/giu;
const sensitivePropertyNames = new Set([
  'apiaccesstoken',
  'artifactuploadbearertoken',
  'bearertoken',
  'authorization',
  'clientsecret',
  'nonce',
  'password',
  'safetytoken',
  'standardtoken'
]);

export function redactDiagnosticText(value) {
  return String(value)
    .replace(credentialUriPattern, '$1<redacted>@')
    .replace(authorizationValuePattern, '$1<redacted>')
    .replace(authorizationSchemeCredentialPattern, '$1<redacted>')
    .replace(sensitiveJsonPropertyPattern, '$1<redacted>"')
    .replace(sensitiveAssignmentPattern, '$1<redacted>');
}

export function sanitizeDiagnosticValue(value, seen = new WeakSet()) {
  if (typeof value === 'string') {
    return redactDiagnosticText(value);
  }
  if (value === null || typeof value !== 'object') {
    return value;
  }
  if (seen.has(value)) {
    return '<circular>';
  }
  seen.add(value);
  if (Array.isArray(value)) {
    return value.map(item => sanitizeDiagnosticValue(item, seen));
  }

  const sanitized = {};
  for (const [key, item] of Object.entries(value)) {
    sanitized[key] = sensitivePropertyNames.has(key.toLowerCase())
      ? '<redacted>'
      : sanitizeDiagnosticValue(item, seen);
  }
  return sanitized;
}

export function formatDiagnosticError(error) {
  if (error instanceof Error) {
    return redactDiagnosticText(error.stack ?? `${error.name}: ${error.message}`);
  }

  return redactDiagnosticText(error);
}

export function createSafeCdpEvent(message) {
  const method = typeof message?.method === 'string'
    ? message.method
    : 'Unknown';
  const parameters = message?.params;
  if (method === 'Runtime.consoleAPICalled') {
    return {
      method,
      type: typeof parameters?.type === 'string' ? parameters.type : null,
      timestamp: typeof parameters?.timestamp === 'number' ? parameters.timestamp : null,
      argumentCount: Array.isArray(parameters?.args) ? parameters.args.length : 0
    };
  }
  if (method === 'Runtime.exceptionThrown') {
    return {
      method,
      timestamp: typeof parameters?.timestamp === 'number' ? parameters.timestamp : null,
      text: redactDiagnosticText(parameters?.exceptionDetails?.text ?? ''),
      description: redactDiagnosticText(
        parameters?.exceptionDetails?.exception?.description ?? '')
    };
  }
  if (method === 'Log.entryAdded') {
    const entry = parameters?.entry;
    return {
      method,
      source: typeof entry?.source === 'string' ? entry.source : null,
      level: typeof entry?.level === 'string' ? entry.level : null,
      text: redactDiagnosticText(entry?.text ?? ''),
      url: typeof entry?.url === 'string' ? entry.url : null,
      lineNumber: typeof entry?.lineNumber === 'number' ? entry.lineNumber : null
    };
  }
  if (method === 'Page.javascriptDialogOpening') {
    return {
      method,
      type: typeof parameters?.type === 'string' ? parameters.type : null,
      message: redactDiagnosticText(parameters?.message ?? ''),
      url: typeof parameters?.url === 'string' ? parameters.url : null
    };
  }

  return { method };
}
