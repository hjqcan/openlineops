import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

export async function buildSampleExtensionArchive(repoRoot, { buildDevelopmentHost = false } = {}) {
  const workingRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'openlineops-extension-e2e-'));
  const packageRoot = path.join(workingRoot, 'package');
  const archivePath = path.join(workingRoot, 'openlineops.samples.loopback-device.zip');
  const sampleRoot = path.join(
    repoRoot,
    'samples',
    'plugins',
    'OpenLineOps.SamplePlugins.LoopbackDevice');
  const sampleProject = path.join(sampleRoot, 'OpenLineOps.SamplePlugins.LoopbackDevice.csproj');
  try {
    await fs.mkdir(packageRoot, { recursive: true });
    await run(
      'dotnet',
      [
        'build',
        sampleProject,
        '--configuration',
        'Release',
        '--output',
        packageRoot,
        '--nologo',
        '-p:DebugSymbols=false',
        '-p:DebugType=None'
      ],
      repoRoot);
    await fs.copyFile(path.join(sampleRoot, 'manifest.json'), path.join(packageRoot, 'manifest.json'));

    if (buildDevelopmentHost) {
      await run(
        'dotnet',
        [
          'build',
          path.join(repoRoot, 'src', 'OpenLineOps.PluginHost', 'OpenLineOps.PluginHost.csproj'),
          '--configuration',
          'Debug',
          '--nologo'
        ],
        repoRoot);
    }

    await run(
      'powershell.exe',
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
      {
        OPENLINEOPS_EXTENSION_SOURCE: packageRoot,
        OPENLINEOPS_EXTENSION_ARCHIVE: archivePath
      });

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

async function run(command, args, cwd, extraEnvironment = {}) {
  await new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd,
      env: { ...process.env, ...extraEnvironment },
      stdio: 'inherit',
      windowsHide: true
    });
    child.once('error', reject);
    child.once('exit', code => code === 0
      ? resolve()
      : reject(new Error(`${command} exited with code ${code ?? 'unknown'}.`)));
  });
}
