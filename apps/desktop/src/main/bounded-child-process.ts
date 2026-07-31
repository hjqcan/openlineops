import { spawn } from 'node:child_process';
import path from 'node:path';

export interface BoundedChildProcessRequest {
  executablePath: string;
  arguments: readonly string[];
  cwd: string;
  environment: NodeJS.ProcessEnv;
  input: string;
  maximumInputBytes: number;
  maximumStdoutBytes: number;
  maximumStderrBytes: number;
  maximumTotalOutputBytes: number;
  timeoutMs: number;
}

export interface BoundedChildProcessResult {
  stdout: string;
  stderr: string;
}

export function runBoundedChildProcess(
  request: BoundedChildProcessRequest
): Promise<BoundedChildProcessResult> {
  assertRequest(request);

  return new Promise((resolve, reject) => {
    const child = spawn(request.executablePath, [...request.arguments], {
      cwd: request.cwd,
      env: request.environment,
      shell: false,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true
    });
    const stdoutChunks: Buffer[] = [];
    const stderrChunks: Buffer[] = [];
    let stdoutBytes = 0;
    let stderrBytes = 0;
    let terminationError: Error | null = null;

    const terminate = (error: Error): void => {
      if (terminationError !== null) {
        return;
      }
      terminationError = error;
      if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL');
      }
    };

    const capture = (stream: 'stdout' | 'stderr', value: Buffer | string): void => {
      if (terminationError !== null) {
        return;
      }
      const chunk = Buffer.isBuffer(value) ? value : Buffer.from(value);
      const nextStdoutBytes = stdoutBytes + (stream === 'stdout' ? chunk.byteLength : 0);
      const nextStderrBytes = stderrBytes + (stream === 'stderr' ? chunk.byteLength : 0);
      if (nextStdoutBytes > request.maximumStdoutBytes
          || nextStderrBytes > request.maximumStderrBytes
          || nextStdoutBytes + nextStderrBytes > request.maximumTotalOutputBytes) {
        terminate(new Error('Bounded child process exceeded its output limit.'));
        return;
      }

      stdoutBytes = nextStdoutBytes;
      stderrBytes = nextStderrBytes;
      (stream === 'stdout' ? stdoutChunks : stderrChunks).push(chunk);
    };

    child.stdout.on('data', value => capture('stdout', value));
    child.stderr.on('data', value => capture('stderr', value));
    child.stdin.on('error', error => {
      if ((error as NodeJS.ErrnoException).code !== 'EPIPE') {
        terminate(new Error(`Bounded child process stdin failed: ${error.message}`));
      }
    });
    child.once('error', error => {
      terminate(new Error(`Bounded child process failed to start: ${error.message}`));
    });

    const timeout = setTimeout(() => {
      terminate(new Error(`Bounded child process timed out after ${request.timeoutMs} ms.`));
    }, request.timeoutMs);

    child.once('close', (exitCode, signal) => {
      clearTimeout(timeout);
      if (terminationError !== null) {
        reject(terminationError);
        return;
      }

      const stdout = Buffer.concat(stdoutChunks, stdoutBytes).toString('utf8');
      const stderr = Buffer.concat(stderrChunks, stderrBytes).toString('utf8');
      if (signal !== null || exitCode !== 0) {
        const status = signal === null ? `exit code ${exitCode ?? 'unknown'}` : `signal ${signal}`;
        const detail = stderr.trim();
        reject(new Error(detail.length > 0
          ? `Bounded child process ended with ${status}: ${detail}`
          : `Bounded child process ended with ${status}.`));
        return;
      }

      resolve({ stdout, stderr });
    });

    child.stdin.end(request.input);
  });
}

function assertRequest(request: BoundedChildProcessRequest): void {
  if (!path.isAbsolute(request.executablePath)
      || request.arguments.some(argument => typeof argument !== 'string')
      || !path.isAbsolute(request.cwd)) {
    throw new Error('Bounded child process executable, arguments, or working directory is invalid.');
  }
  for (const [name, value] of Object.entries({
    maximumInputBytes: request.maximumInputBytes,
    maximumStdoutBytes: request.maximumStdoutBytes,
    maximumStderrBytes: request.maximumStderrBytes,
    maximumTotalOutputBytes: request.maximumTotalOutputBytes,
    timeoutMs: request.timeoutMs
  })) {
    if (!Number.isSafeInteger(value) || value <= 0) {
      throw new Error(`Bounded child process ${name} must be a positive safe integer.`);
    }
  }
  if (Buffer.byteLength(request.input, 'utf8') > request.maximumInputBytes) {
    throw new Error('Bounded child process input exceeds its limit.');
  }
}
