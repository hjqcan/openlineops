import { createRequire } from 'node:module';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '..');

const electronExecutable = require('electron');
const electronPackageJson = require.resolve('electron/package.json');
const electronRoot = path.dirname(electronPackageJson);
const electronDist = path.join(electronRoot, 'dist');
const releaseRoot = path.join(projectRoot, 'release', 'desktop');
const packageRoot = path.join(releaseRoot, 'win-unpacked');
const resourcesApp = path.join(packageRoot, 'resources', 'app');

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

async function writePackageNotes() {
  const notes = [
    'OpenLineOps unsigned Windows desktop development package.',
    '',
    'This package contains the Electron desktop shell only. The .NET API is',
    'released separately and can also be run from source during development.',
    '',
    'Useful environment variables:',
    '- OPENLINEOPS_API_BASE_URL: API URL, defaults to http://localhost:5135',
    '- OPENLINEOPS_REPO_ROOT: source checkout root for local backend startup',
    '- OPENLINEOPS_API_PROJECT: explicit OpenLineOps.Api.csproj path',
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

  await fs.rm(releaseRoot, { recursive: true, force: true });
  await fs.mkdir(resourcesApp, { recursive: true });

  await copyDirectory(electronDist, packageRoot);
  await renameElectronExecutable();
  await copyDirectory(path.join(projectRoot, 'dist'), path.join(resourcesApp, 'dist'));
  await copyDirectory(path.join(projectRoot, 'dist-electron'), path.join(resourcesApp, 'dist-electron'));
  await fs.copyFile(path.join(projectRoot, 'package.json'), path.join(resourcesApp, 'package.json'));
  await fs.copyFile(path.join(projectRoot, 'README.md'), path.join(resourcesApp, 'README.md'));
  await writePackageNotes();

  console.log(`OpenLineOps Windows desktop package created: ${packageRoot}`);
}

await main();
