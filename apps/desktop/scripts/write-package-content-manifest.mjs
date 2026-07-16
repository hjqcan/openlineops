import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

export const packageContentManifestFileName = 'openlineops-package-content.json';
export const requiredPackageContentPaths = [
  'OpenLineOps.exe',
  'resources/app/dist/index.html',
  'resources/app/dist-electron/main/main.js',
  'resources/app/dist-electron/preload/preload.cjs',
  'resources/app/package.json',
  'resources/app/runtime/api/OpenLineOps.Api.exe',
  'resources/app/runtime/api/OpenLineOps.Api.dll',
  'resources/app/runtime/api/OpenLineOps.Api.deps.json',
  'resources/app/runtime/api/OpenLineOps.Api.runtimeconfig.json',
  'resources/app/runtime/api/appsettings.json',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.dll',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.deps.json',
  'resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.runtimeconfig.json',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.dll',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.deps.json',
  'resources/app/runtime/plugin-host/OpenLineOps.PluginHost.runtimeconfig.json'
];

export async function writePackageContentManifest(packageRoot) {
  const canonicalPackageRoot = path.resolve(packageRoot);
  if (!path.isAbsolute(packageRoot) || canonicalPackageRoot !== path.normalize(packageRoot)) {
    throw new Error('Package root must be one canonical absolute path.');
  }
  const rootStat = await fs.lstat(canonicalPackageRoot);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    throw new Error(`Package root must be one plain directory: ${canonicalPackageRoot}`);
  }

  const files = [];
  await collectFiles(canonicalPackageRoot, canonicalPackageRoot, files);
  files.sort((left, right) => compareOrdinal(left.path, right.path));
  const inventory = new Set(files.map(file => file.path));
  const missing = requiredPackageContentPaths.filter(requiredPath => !inventory.has(requiredPath));
  if (missing.length > 0) {
    throw new Error(`Package content is missing formal runtime files: ${missing.join(', ')}`);
  }

  const manifestPath = path.join(canonicalPackageRoot, packageContentManifestFileName);
  await fs.rm(manifestPath, { force: true });
  await fs.writeFile(
    manifestPath,
    `${JSON.stringify({
      schema: 'openlineops.desktop-package-content',
      files
    }, null, 2)}\n`,
    { encoding: 'utf8', flag: 'wx', mode: 0o600 });
  return manifestPath;
}

async function collectFiles(packageRoot, currentDirectory, files) {
  const entries = await fs.readdir(currentDirectory, { withFileTypes: true });
  for (const entry of entries) {
    const entryPath = path.join(currentDirectory, entry.name);
    const metadata = await fs.lstat(entryPath);
    if (metadata.isSymbolicLink()) {
      throw new Error(`Package content cannot contain a symbolic link: ${entryPath}`);
    }
    if (metadata.isDirectory()) {
      await collectFiles(packageRoot, entryPath, files);
      continue;
    }
    if (!metadata.isFile() || metadata.nlink !== 1) {
      throw new Error(`Package content must contain only plain files and directories: ${entryPath}`);
    }
    const relativePath = path.relative(packageRoot, entryPath).replaceAll('\\', '/');
    if (relativePath === packageContentManifestFileName) {
      continue;
    }
    files.push({
      path: relativePath,
      sha256: await sha256File(entryPath),
      size: metadata.size
    });
  }
}

async function sha256File(filePath) {
  const content = createHash('sha256');
  await new Promise((resolve, reject) => {
    const stream = createReadStream(filePath);
    stream.on('data', chunk => content.update(chunk));
    stream.once('error', reject);
    stream.once('end', resolve);
  });
  return content.digest('hex');
}

function compareOrdinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

async function runCli() {
  const rootIndex = process.argv.indexOf('--package-root');
  if (rootIndex < 0 || rootIndex + 1 >= process.argv.length || process.argv.length !== 4) {
    throw new Error('Usage: node write-package-content-manifest.mjs --package-root <absolute-path>');
  }
  const manifestPath = await writePackageContentManifest(process.argv[rootIndex + 1]);
  console.log(`Package content manifest written: ${manifestPath}`);
}

if (process.argv[1]
    && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await runCli();
}
