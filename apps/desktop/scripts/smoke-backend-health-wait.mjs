export function captureBackendSessionIdentity(status, description) {
  const diagnostic = backendStatusDiagnostic(status);
  if (status?.isRunning !== true
      || !Number.isSafeInteger(status.pid)
      || status.pid <= 0
      || !Number.isSafeInteger(status.startedAtUnixMilliseconds)
      || status.startedAtUnixMilliseconds <= 0
      || typeof status.startedAtUtc !== 'string'
      || Date.parse(status.startedAtUtc) !== status.startedAtUnixMilliseconds
      || typeof status.apiBaseUrl !== 'string'
      || status.apiBaseUrl.length === 0
      || status.lastExitCode !== null
      || !['Healthy', 'Unreachable'].includes(status.health)) {
    throw new Error(
      `${description} has no exact authenticated backend session: ${JSON.stringify(diagnostic)}`);
  }

  return Object.freeze({
    pid: status.pid,
    startedAtUnixMilliseconds: status.startedAtUnixMilliseconds,
    startedAtUtc: status.startedAtUtc,
    apiBaseUrl: status.apiBaseUrl
  });
}

export async function waitForBoundBackendHealth({
  sessionIdentity,
  getStatus,
  timeoutMilliseconds,
  pollIntervalMilliseconds = 100,
  description,
  now = Date.now,
  wait = delay
}) {
  validatePositiveSafeInteger(timeoutMilliseconds, 'Backend health timeout');
  validatePositiveSafeInteger(pollIntervalMilliseconds, 'Backend health poll interval');
  if (typeof getStatus !== 'function'
      || typeof now !== 'function'
      || typeof wait !== 'function') {
    throw new Error('Backend health wait requires status, clock, and delay functions.');
  }

  const expected = captureBackendSessionIdentity(
    {
      ...sessionIdentity,
      isRunning: true,
      health: 'Unreachable',
      lastExitCode: null
    },
    description);
  const startedAt = now();
  const deadline = startedAt + timeoutMilliseconds;
  if (!Number.isSafeInteger(startedAt)
      || !Number.isSafeInteger(deadline)) {
    throw new Error('Backend health deadline is outside the safe integer range.');
  }

  let lastStatus = null;
  while (now() < deadline) {
    const probeTimeoutMilliseconds = deadline - now();
    lastStatus = await getStatus(probeTimeoutMilliseconds);
    assertSameBackendSession(expected, lastStatus, description);
    if (now() >= deadline) {
      break;
    }
    if (lastStatus.health === 'Healthy') {
      return lastStatus;
    }

    const remainingMilliseconds = deadline - now();
    if (remainingMilliseconds <= 0) {
      break;
    }
    await wait(Math.min(pollIntervalMilliseconds, remainingMilliseconds));
  }

  throw new Error(
    `Timed out after ${timeoutMilliseconds} ms waiting for ${description} `
    + `to become Healthy without changing session. Last status: ${
      JSON.stringify(backendStatusDiagnostic(lastStatus))}`);
}

function assertSameBackendSession(expected, status, description) {
  const current = backendStatusDiagnostic(status);
  if (status?.isRunning !== true
      || status.pid !== expected.pid
      || status.startedAtUnixMilliseconds !== expected.startedAtUnixMilliseconds
      || status.startedAtUtc !== expected.startedAtUtc
      || status.apiBaseUrl !== expected.apiBaseUrl
      || status.lastExitCode !== null
      || !['Healthy', 'Unreachable'].includes(status.health)) {
    throw new Error(
      `${description} changed or exited its bound backend session: ${JSON.stringify({
        expected,
        current
      })}`);
  }
}

function backendStatusDiagnostic(status) {
  return {
    isRunning: status?.isRunning ?? null,
    pid: status?.pid ?? null,
    startedAtUnixMilliseconds: status?.startedAtUnixMilliseconds ?? null,
    startedAtUtc: status?.startedAtUtc ?? null,
    apiBaseUrl: status?.apiBaseUrl ?? null,
    health: status?.health ?? null,
    lastExitCode: status?.lastExitCode ?? null
  };
}

function validatePositiveSafeInteger(value, description) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${description} must be a positive safe integer.`);
  }
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
