import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '..');
const repoRoot = path.resolve(projectRoot, '..', '..');

const electronExecutable = require('electron');
const electronPackageJson = require.resolve('electron/package.json');
const electronRoot = path.dirname(electronPackageJson);
const electronDist = path.join(electronRoot, 'dist');
const releaseRoot = path.join(projectRoot, 'release', 'desktop');
const packageRoot = path.join(releaseRoot, 'win-unpacked');
const resourcesApp = path.join(packageRoot, 'resources', 'app');
const bundledRuntimeRoot = path.join(resourcesApp, 'runtime');
const bundledApiRoot = path.join(bundledRuntimeRoot, 'api');
const bundledScriptWorkerRoot = path.join(bundledRuntimeRoot, 'script-worker');
const bundledPluginsRoot = path.join(bundledRuntimeRoot, 'plugins');
const apiProject = path.join(repoRoot, 'src', 'OpenLineOps.Api', 'OpenLineOps.Api.csproj');
const scriptWorkerProject = path.join(
  repoRoot,
  'src',
  'OpenLineOps.ScriptWorker',
  'OpenLineOps.ScriptWorker.csproj');
const samplePluginRoot = path.join(
  repoRoot,
  'samples',
  'plugins',
  'OpenLineOps.SamplePlugins.LoopbackDevice');
const samplePluginProject = path.join(samplePluginRoot, 'OpenLineOps.SamplePlugins.LoopbackDevice.csproj');
const bundledSamplePluginRoot = path.join(
  bundledPluginsRoot,
  'OpenLineOps.SamplePlugins.LoopbackDevice');

async function assertDirectory(directory, label) {
  const stat = await fs.stat(directory).catch(() => null);
  if (!stat?.isDirectory()) {
    throw new Error(`${label} directory does not exist: ${directory}`);
  }
}

async function copyDirectory(source, destination) {
  await assertDirectory(source, 'Source');
  await fs.mkdir(destination, { recursive: true });
  await fs.cp(source, destination, {
    recursive: true,
    force: true,
    errorOnExist: false
  });
}

async function renameElectronExecutable() {
  const electronExe = path.join(packageRoot, 'electron.exe');
  const appExe = path.join(packageRoot, 'OpenLineOps.exe');
  const stat = await fs.stat(electronExe).catch(() => null);
  if (!stat?.isFile()) {
    throw new Error(`Electron runtime executable was not found: ${electronExe}`);
  }

  await fs.rm(appExe, { force: true });
  await fs.rename(electronExe, appExe);
}

async function run(command, args, cwd) {
  await new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd,
      stdio: 'inherit',
      windowsHide: true
    });
    child.once('error', reject);
    child.once('exit', code => {
      if (code === 0) {
        resolve();
        return;
      }

      reject(new Error(`${command} exited with code ${code ?? 'unknown'}.`));
    });
  });
}

async function publishBundledRuntime() {
  await run(
    'dotnet',
    [
      'publish',
      apiProject,
      '--configuration',
      'Release',
      '--runtime',
      'win-x64',
      '--self-contained',
      'true',
      '--output',
      bundledApiRoot,
      '--nologo',
      '-p:DebugSymbols=false',
      '-p:DebugType=None'
    ],
    repoRoot);
  await assertDirectory(bundledApiRoot, 'Bundled API');

  await run(
    'dotnet',
    [
      'publish',
      scriptWorkerProject,
      '--configuration',
      'Release',
      '--runtime',
      'win-x64',
      '--self-contained',
      'true',
      '--output',
      bundledScriptWorkerRoot,
      '--nologo',
      '-p:DebugSymbols=false',
      '-p:DebugType=None'
    ],
    repoRoot);
  await assertDirectory(bundledScriptWorkerRoot, 'Bundled Python script worker');

  await run(
    'dotnet',
    [
      'publish',
      samplePluginProject,
      '--configuration',
      'Release',
      '--output',
      bundledSamplePluginRoot,
      '--nologo',
      '-p:DebugSymbols=false',
      '-p:DebugType=None'
    ],
    repoRoot);
  await fs.copyFile(
    path.join(samplePluginRoot, 'manifest.json'),
    path.join(bundledSamplePluginRoot, 'manifest.json'));

  const apiExecutable = path.join(bundledApiRoot, 'OpenLineOps.Api.exe');
  const apiExecutableStat = await fs.stat(apiExecutable).catch(() => null);
  if (!apiExecutableStat?.isFile()) {
    throw new Error(`Bundled API executable was not found: ${apiExecutable}`);
  }

  const scriptWorkerExecutable = path.join(
    bundledScriptWorkerRoot,
    'OpenLineOps.ScriptWorker.exe');
  const scriptWorkerExecutableStat = await fs.stat(scriptWorkerExecutable).catch(() => null);
  if (!scriptWorkerExecutableStat?.isFile()) {
    throw new Error(`Bundled Python script worker executable was not found: ${scriptWorkerExecutable}`);
  }

  const configurationPath = path.join(bundledApiRoot, 'appsettings.json');
  const configuration = JSON.parse(await fs.readFile(configurationPath, 'utf8'));
  const runtimeProvider = configuration?.OpenLineOps?.Runtime?.Persistence?.Provider;
  const traceProvider = configuration?.OpenLineOps?.Traceability?.Persistence?.Provider;
  const pythonExecutionMode = configuration?.OpenLineOps?.Runtime?.Scripting?.Python?.ExecutionMode;
  if (runtimeProvider !== 'Sqlite' || traceProvider !== 'Sqlite') {
    throw new Error(
      'Bundled Production runtime and traceability persistence must both use Sqlite.');
  }
  if (pythonExecutionMode !== 'ProcessIsolated') {
    throw new Error('Bundled Python execution must be ProcessIsolated.');
  }
}

async function writePackageNotes() {
  const notes = [
    'OpenLineOps unsigned Windows desktop development package.',
    '',
    'This package contains the Electron automation IDE, its self-contained',
    'OpenLineOps.Api runtime, its process-isolated Python ScriptWorker,',
    'and the bundled loopback sample plugin.',
    'Runtime databases are stored under the current user profile.',
    '',
    'Useful environment variables:',
    '- OPENLINEOPS_API_BASE_URL: API URL, defaults to http://localhost:5135',
    '',
    'For production release, sign the package contents before creating',
    'release archives, manifests, and checksums.',
    ''
  ].join('\n');

  await fs.writeFile(path.join(packageRoot, 'OPENLINEOPS-PACKAGE-NOTES.txt'), notes, 'utf8');
}

async function main() {
  await assertDirectory(electronDist, 'Electron runtime');
  const electronExecutableStat = await fs.stat(electronExecutable).catch(() => null);
  if (!electronExecutableStat?.isFile()) {
    throw new Error(`Electron runtime executable was not found: ${electronExecutable}`);
  }

  await assertDirectory(path.join(projectRoot, 'dist'), 'Renderer build');
  await assertDirectory(path.join(projectRoot, 'dist-electron'), 'Electron main/preload build');

  await fs.rm(releaseRoot, {
    recursive: true,
    force: true,
    maxRetries: 10,
    retryDelay: 250
  });
  await fs.mkdir(resourcesApp, { recursive: true });

  await copyDirectory(electronDist, packageRoot);
  await renameElectronExecutable();
  await copyDirectory(path.join(projectRoot, 'dist'), path.join(resourcesApp, 'dist'));
  await copyDirectory(path.join(projectRoot, 'dist-electron'), path.join(resourcesApp, 'dist-electron'));
  await publishBundledRuntime();
  await fs.copyFile(path.join(projectRoot, 'package.json'), path.join(resourcesApp, 'package.json'));
  await fs.copyFile(path.join(projectRoot, 'README.md'), path.join(resourcesApp, 'README.md'));
  await writePackageNotes();

  console.log(`OpenLineOps Windows desktop package created: ${packageRoot}`);
}

await main();
