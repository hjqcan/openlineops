import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { stopProcess } from './electron-cdp-harness.mjs';
import {
  resolveDotnetExecutablePath,
  spawnOwnedProcessTree
} from './owned-process-tree.mjs';
import { createWindowsPowerShellHost } from './windows-powershell-host.mjs';

export async function buildSampleExtensionArchive(repoRoot) {
  const dotnetExecutable = await resolveDotnetExecutablePath(process.env);
  const workingRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'openlineops-extension-e2e-'));
  const packageRoot = path.join(workingRoot, 'package');
  const archivePath = path.join(workingRoot, 'openlineops.samples.loopback-device.zip');
  const sampleRoot = path.join(
    repoRoot,
    'samples',
    'plugins',
    'OpenLineOps.SamplePlugins.LoopbackDevice');
  const sampleProject = path.join(sampleRoot, 'OpenLineOps.SamplePlugins.LoopbackDevice.csproj');
  const processTreeHostPath = path.join(
    repoRoot,
    'tools',
    'OpenLineOps.ProcessTreeHost',
    'bin',
    'Release',
    'net10.0',
    'OpenLineOps.ProcessTreeHost.exe');
  try {
    await fs.mkdir(packageRoot, { recursive: true });
    await run(
      dotnetExecutable,
      [
        'build',
        sampleProject,
        '--configuration',
        'Release',
        '--disable-build-servers',
        '--output',
        packageRoot,
        '--nologo',
        '-p:DebugSymbols=false',
        '-p:DebugType=None'
      ],
      repoRoot);
    await fs.copyFile(path.join(sampleRoot, 'manifest.json'), path.join(packageRoot, 'manifest.json'));

    const powerShellHost = createWindowsPowerShellHost({
      OPENLINEOPS_EXTENSION_SOURCE: packageRoot,
      OPENLINEOPS_EXTENSION_ARCHIVE: archivePath
    });
    await run(
      powerShellHost.executablePath,
      [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        '$ErrorActionPreference = "Stop"; '
          + 'Compress-Archive -Path (Join-Path $env:OPENLINEOPS_EXTENSION_SOURCE "*") '
          + '-DestinationPath $env:OPENLINEOPS_EXTENSION_ARCHIVE -CompressionLevel Optimal'
      ],
      repoRoot,
      powerShellHost.environment,
      processTreeHostPath);

    const archive = await fs.stat(archivePath);
    if (!archive.isFile() || archive.size === 0) {
      throw new Error(`Sample extension archive was not created: ${archivePath}`);
    }
    return {
      archivePath,
      async cleanup() {
        await fs.rm(workingRoot, { recursive: true, force: true });
      }
    };
  } catch (error) {
    await fs.rm(workingRoot, { recursive: true, force: true });
    throw error;
  }
}

async function run(
  command,
  args,
  cwd,
  environment = process.env,
  processTreeHostPath = path.join(
    cwd,
    'tools',
    'OpenLineOps.ProcessTreeHost',
    'bin',
    'Release',
    'net10.0',
    'OpenLineOps.ProcessTreeHost.exe')
) {
  const timeoutMilliseconds = 300_000;
  const deadline = Date.now() + timeoutMilliseconds;
  const child = await spawnOwnedProcessTree({
    processTreeHostPath,
    command,
    args,
    workingDirectory: cwd,
    environment,
    stdio: 'inherit',
    startupTimeoutMilliseconds: timeoutMilliseconds
  });
  await new Promise((resolve, reject) => {
    let settled = false;
    const complete = action => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      action();
    };
    const timeout = setTimeout(() => {
      if (settled) return;
      settled = true;
      void stopProcess(child, 15_000).then(
        () => reject(new Error(
          `${command} did not exit within ${timeoutMilliseconds} ms.`)),
        terminationError => reject(new AggregateError(
          [
            new Error(
              `${command} did not exit within ${timeoutMilliseconds} ms.`),
            terminationError
          ],
          `${command} timed out and its process tree could not be confirmed stopped.`)));
    }, remainingCommandMilliseconds(deadline, command));
    child.once('error', error => complete(() => reject(error)));
    void child.openlineopsClosePromise.then(({ exitCode, signalCode }) => complete(
      () => exitCode === 0 && signalCode === null
      ? resolve()
      : reject(new Error(
        `${command} closed with code ${exitCode ?? 'unknown'} and signal ${
          signalCode ?? 'none'}.`))));
  });
}

function remainingCommandMilliseconds(deadline, description) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) {
    throw new Error(`${description} exceeded its hard command deadline during startup.`);
  }
  return remaining;
}
