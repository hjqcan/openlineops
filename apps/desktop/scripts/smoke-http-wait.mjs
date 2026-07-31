export async function waitForHttp(
  url,
  timeoutMilliseconds,
  description,
  child = null
) {
  assertPositiveTimeout(timeoutMilliseconds);
  const deadline = Date.now() + timeoutMilliseconds;

  while (Date.now() < deadline) {
    const remaining = Math.max(1, deadline - Date.now());
    try {
      const response = await fetch(url, {
        signal: AbortSignal.timeout(Math.min(2_000, remaining))
      });
      if (response.ok) {
        return;
      }
    } catch {
      // Server may still be starting.
    }

    assertChildStillStarting(child, description);
    await delay(Math.max(1, Math.min(400, deadline - Date.now())));
  }

  assertChildStillStarting(child, description);
  throw new Error(`Timed out waiting for ${description} at ${url}.`);
}

function assertChildStillStarting(child, description) {
  if (child?.openlineopsLaunchError instanceof Error) {
    throw new Error(
      `${description} process failed to start: ${child.openlineopsLaunchError.message}`,
      { cause: child.openlineopsLaunchError });
  }
  if (child
      && (child.exitCode !== null || child.signalCode !== null)) {
    throw new Error(
      `${description} process exited before its HTTP endpoint was ready. `
      + `ExitCode=${child.exitCode ?? 'null'}, SignalCode=${child.signalCode ?? 'null'}.`);
  }
}

function assertPositiveTimeout(timeoutMilliseconds) {
  if (!Number.isSafeInteger(timeoutMilliseconds)
      || timeoutMilliseconds <= 0) {
    throw new Error('HTTP wait timeout must be a positive safe integer.');
  }
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
