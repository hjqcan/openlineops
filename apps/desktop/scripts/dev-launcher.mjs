import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import electronPath from 'electron';

const scriptPath = fileURLToPath(import.meta.url);
const desktopRoot = path.resolve(path.dirname(scriptPath), '..');
const viteCliPath = path.join(desktopRoot, 'node_modules', 'vite', 'bin', 'vite.js');
const rendererPort = await allocateLoopbackPort();
const rendererNonce = randomBytes(32).toString('base64url');
const rendererUrl = `http://127.0.0.1:${rendererPort}`;
const configuredUserDataDirectory = process.env.OPENLINEOPS_DEV_USER_DATA_DIRECTORY;
if (configuredUserDataDirectory !== undefined && !path.isAbsolute(configuredUserDataDirectory)) {
  throw new Error('OPENLINEOPS_DEV_USER_DATA_DIRECTORY must be absolute.');
}
const environment = {
  ...process.env,
  OPENLINEOPS_RENDERER_NONCE: rendererNonce,
  OPENLINEOPS_RENDERER_PORT: String(rendererPort),
  VITE_DEV_SERVER_URL: rendererUrl,
  OpenLineOps__Desktop__AllowedOrigins__0: rendererUrl,
  OpenLineOps__Desktop__AllowedOrigins__1: rendererUrl.replace('127.0.0.1', 'localhost')
};

const vite = spawn(
  process.execPath,
  [viteCliPath, '--host', '127.0.0.1', '--port', String(rendererPort), '--strictPort'],
  { cwd: desktopRoot, env: environment, stdio: 'inherit', windowsHide: true });
const electron = spawn(
  electronPath,
  [
    ...(configuredUserDataDirectory === undefined
      ? []
      : [`--user-data-dir=${path.normalize(configuredUserDataDirectory)}`]),
    desktopRoot
  ],
  { cwd: desktopRoot, env: environment, stdio: 'inherit', windowsHide: true });

if (process.env.OPENLINEOPS_DEV_LAUNCHER_SMOKE === '1') {
  process.stdout.write(`OPENLINEOPS_DEV_LAUNCHER_STATE ${JSON.stringify({
    electronPid: electron.pid ?? null,
    vitePid: vite.pid ?? null
  })}\n`);
}

let stopping = false;
const stop = () => {
  if (stopping) {
    return;
  }
  stopping = true;
  terminateKnownProcessTree(electron);
  terminateKnownProcessTree(vite);
};
process.once('SIGINT', stop);
process.once('SIGTERM', stop);
const onStandardInput = chunk => {
  if (chunk.toString().split(/\r?\n/u).some(line => line.trim() === 'stop')) {
    stop();
  }
};
process.stdin.on('data', onStandardInput);
vite.once('exit', code => {
  if (!stopping) {
    process.exitCode = code ?? 1;
    stop();
  }
});
electron.once('exit', code => {
  if (!stopping) {
    process.exitCode = code ?? 0;
    stop();
  }
});
await Promise.all([
  new Promise(resolve => vite.once('close', resolve)),
  new Promise(resolve => electron.once('close', resolve))
]);
process.stdin.off('data', onStandardInput);
process.stdin.pause();

function terminateKnownProcessTree(child) {
  if (child.pid === undefined || child.exitCode !== null) {
    return;
  }
  if (process.platform === 'win32') {
    spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], {
      stdio: 'ignore',
      windowsHide: true
    });
    return;
  }
  child.kill('SIGTERM');
}

async function allocateLoopbackPort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        server.close();
        reject(new Error('Could not allocate a loopback renderer port.'));
        return;
      }
      server.close(error => error ? reject(error) : resolve(address.port));
    });
  });
}
