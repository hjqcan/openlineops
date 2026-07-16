import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  mkdir,
  mkdtemp,
  readFile,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import test from 'node:test';

import {
  protectCredentialPath,
  verifyCredentialPathProtection
} from '../dist-electron/main/api-credential-security.js';
import { createLocalSqliteConnectionString } from '../dist-electron/main/local-sqlite-connection.js';
import {
  createWindowsPowerShellHost,
  windowsSystemExecutablePath
} from './windows-powershell-host.mjs';

const sourceUrl = new URL('../src/main/main.ts', import.meta.url);
const source = await readFile(sourceUrl, 'utf8');

test('external credential mode is structurally read-only and fail-closed', () => {
  const provision = section(
    source,
    'function provisionLocalApiCredentials()',
    'function writeCredentialFile(');
  const writer = section(
    source,
    'function writeCredentialFile(',
    'function verifyExternalCredentialFile(');
  const externalVerifier = section(
    source,
    'function verifyExternalCredentialFile(',
    'function readCredentialFile(');

  const externalMissingGuard = provision.indexOf(
    'if (externallyProvisioned && (!standardExists || !safetyExists))');
  const firstManagedWrite = provision.indexOf('writeCredentialFile(');
  const firstCredentialRead = provision.indexOf('readCredentialFile(');
  const externalVerification = provision.indexOf('verifyExternalCredentialFile(standardPath)');
  assert.match(
    provision,
    /externallyProvisioned[\s\S]*?!path\.isAbsolute\(configuredStandardPath!\)[\s\S]*?!path\.isAbsolute\(configuredSafetyPath!\)/,
    'Externally provisioned credential paths must be absolute.');
  assert.notEqual(externalMissingGuard, -1, 'External token files must be required to pre-exist.');
  assert.notEqual(firstManagedWrite, -1, 'Managed credentials must still be provisioned.');
  assert.ok(
    externalMissingGuard < firstManagedWrite,
    'The external-file existence guard must run before any credential write.');
  assert.ok(
    externalVerification !== -1 && externalVerification < firstCredentialRead,
    'Externally provisioned credential ACLs must be verified before token contents are read.');

  const externalBranch = section(
    provision,
    'if (externallyProvisioned) {',
    '} else {');
  assert.doesNotMatch(
    externalBranch,
    /(?:writeCredentialFile|mkdirSync|protectCredentialPath)\s*\(/,
    'External credential mode must not create or mutate any filesystem path.');

  const managedBranch = section(
    provision,
    '} else {',
    'const standardToken =');
  assert.match(
    managedBranch,
    /mkdirSync\(securityDirectory, \{ recursive: true, mode: 0o700 \}\)/,
    'Only managed credential mode may create the default security directory.');
  assert.match(
    managedBranch,
    /protectCredentialPath\(securityDirectory, true\)/,
    'Managed credential mode must protect its security directory before token creation.');

  assert.doesNotMatch(
    writer,
    /mkdirSync\s*\(/,
    'Credential creation must not create an arbitrary parent directory.');
  assert.match(
    writer,
    /path\.dirname\(filePath\) !== protectedDirectory/,
    'Credential creation must be pinned to the managed security directory.');
  const managedParentVerification = writer.indexOf(
    'verifyCredentialPathProtection(protectedDirectory, true)');
  const tokenWrite = writer.indexOf('writeFileSync(');
  assert.notEqual(
    managedParentVerification,
    -1,
    'Managed credential creation must verify its protected parent directory.');
  assert.notEqual(tokenWrite, -1, 'Managed credential creation must write with an exclusive file open.');
  assert.ok(
    managedParentVerification < tokenWrite,
    'The managed parent directory must be protected before a token file is created.');

  assert.match(
    externalVerifier,
    /verifyCredentialPathProtection\(path\.dirname\(filePath\), true\)/,
    'An external token parent directory must have a private ACL.');
  assert.match(
    externalVerifier,
    /verifyCredentialPathProtection\(filePath, false\)/,
    'An external token file must have a private ACL.');
});

test('packaged backend receives an explicit path-safe Production coordination connection string', () => {
  const packagedLaunch = section(
    source,
    "const runtimeRoot = path.join(app.getAppPath(), 'runtime');",
    'function provisionLocalApiCredentials()');
  assert.match(
    packagedLaunch,
    /OpenLineOps__Runtime__Coordination__ConnectionString:\s*createLocalSqliteConnectionString\(productionCoordinationDatabasePath\)/,
    'Packaged Studio must override the production appsettings connection string.');
  assert.doesNotMatch(
    packagedLaunch,
    /OpenLineOps__Runtime__Coordination__SqliteDatabasePath/,
    'Packaged Studio must not rely on a database-path fallback shadowed by appsettings.');

  const unusualAbsolutePath = path.join(
    path.parse(process.cwd()).root,
    'OpenLineOps;coordination=name"quoted',
    'production.sqlite');
  assert.equal(
    createLocalSqliteConnectionString(unusualAbsolutePath),
    `Data Source="${unusualAbsolutePath.replaceAll('"', '""')}"`);
  assert.throws(
    () => createLocalSqliteConnectionString('relative/production.sqlite'),
    /absolute filesystem path/);
  assert.throws(
    () => createLocalSqliteConnectionString(`${unusualAbsolutePath}\nsecond.sqlite`),
    /absolute filesystem path/);
});

test('managed credential paths receive exact private permissions', async () => {
  await withCredentialFixture(async ({ directory, tokenPath }) => {
    protectCredentialPath(directory, true);
    await writeFile(tokenPath, randomBytes(32).toString('base64url'), { flag: 'wx', mode: 0o600 });
    protectCredentialPath(tokenPath, false);
    verifyCredentialPathProtection(directory, true);
    verifyCredentialPathProtection(tokenPath, false);

    if (process.platform === 'win32') {
      const userSid = currentWindowsUserSid();
      assertExactWindowsAcl(await readWindowsAcl(directory), userSid, true);
      assertExactWindowsAcl(await readWindowsAcl(tokenPath), userSid, false);
    } else {
      assert.equal((await stat(directory)).mode & 0o777, 0o700);
      assert.equal((await stat(tokenPath)).mode & 0o777, 0o600);
    }
  });
});

test(
  'Windows verification rejects Users and Everyone access and protection repairs it',
  { skip: process.platform !== 'win32' },
  async () => {
    await withCredentialFixture(async ({ directory }) => {
      protectCredentialPath(directory, true);
      for (const unsafeGrant of [
        '*S-1-5-32-545:(OI)(CI)M',
        '*S-1-1-0:(OI)(CI)W'
      ]) {
        runIcacls(directory, ['/grant', unsafeGrant]);
        assert.throws(
          () => verifyCredentialPathProtection(directory, true),
          /API credential ACL verification failed/);
        protectCredentialPath(directory, true);
      }

      verifyCredentialPathProtection(directory, true);
      assertExactWindowsAcl(await readWindowsAcl(directory), currentWindowsUserSid(), true);
    });
  });

test(
  'Windows verification rejects enabled inheritance',
  { skip: process.platform !== 'win32' },
  async () => {
    await withCredentialFixture(async ({ directory }) => {
      protectCredentialPath(directory, true);
      runIcacls(directory, ['/inheritance:e']);
      assert.throws(
        () => verifyCredentialPathProtection(directory, true),
        /API credential ACL verification failed/);
    });
  });

test(
  'Windows ACL hosts ignore inherited PowerShell module path pollution',
  { skip: process.platform !== 'win32' },
  async () => {
    await withCredentialFixture(async ({ directory, tokenPath }) => {
      const pollutedModulePath = await createRejectedPowerShellSecurityModule(directory);
      const savedModulePathEntries = Object.entries(process.env)
        .filter(([name]) => name.toUpperCase() === 'PSMODULEPATH');
      removeEnvironmentVariableCaseInsensitively('PSModulePath');
      process.env.pSmOdUlEpAtH = [
        pollutedModulePath,
        path.join(
          process.env.ProgramFiles ?? 'C:\\Program Files',
          'PowerShell',
          '7',
          'Modules')
      ].join(path.delimiter);
      try {
        assertInheritedWindowsPowerShellModulePathIsRejected(directory);
        protectCredentialPath(directory, true);
        await writeFile(tokenPath, randomBytes(32).toString('base64url'), {
          flag: 'wx',
          mode: 0o600
        });
        protectCredentialPath(tokenPath, false);
        verifyCredentialPathProtection(directory, true);
        verifyCredentialPathProtection(tokenPath, false);

        const userSid = currentWindowsUserSid();
        assertExactWindowsAcl(await readWindowsAcl(directory), userSid, true);
        assertExactWindowsAcl(await readWindowsAcl(tokenPath), userSid, false);
      } finally {
        removeEnvironmentVariableCaseInsensitively('PSModulePath');
        for (const [name, value] of savedModulePathEntries) {
          process.env[name] = value;
        }
      }
    });
  });

test('external credential verification leaves ACL, contents, and timestamps unchanged', async () => {
  await withCredentialFixture(async ({ directory, tokenPath }) => {
    protectCredentialPath(directory, true);
    const token = randomBytes(32).toString('base64url');
    await writeFile(tokenPath, token, { flag: 'wx', mode: 0o600 });
    protectCredentialPath(tokenPath, false);

    const aclBefore = process.platform === 'win32' ? await readWindowsAcl(tokenPath) : null;
    const metadataBefore = await stat(tokenPath, { bigint: true });
    verifyCredentialPathProtection(directory, true);
    verifyCredentialPathProtection(tokenPath, false);
    const metadataAfter = await stat(tokenPath, { bigint: true });

    assert.equal(await readFile(tokenPath, 'utf8'), token);
    assert.equal(metadataAfter.mtimeNs, metadataBefore.mtimeNs);
    assert.equal(metadataAfter.size, metadataBefore.size);
    if (process.platform === 'win32') {
      assert.deepEqual(await readWindowsAcl(tokenPath), aclBefore);
    }
  });
});

async function createRejectedPowerShellSecurityModule(root) {
  const moduleRoot = path.join(root, 'polluted-modules');
  const moduleDirectory = path.join(moduleRoot, 'Microsoft.PowerShell.Security');
  await mkdir(moduleDirectory, { recursive: true });
  await writeFile(
    path.join(moduleDirectory, 'Microsoft.PowerShell.Security.psd1'),
    String.raw`@{
RootModule = 'Missing.Security.Commands.dll'
ModuleVersion = '999.0.0'
GUID = '7a3167f6-20d7-4934-94a6-24a6b45d5737'
CmdletsToExport = @('Get-Acl')
FunctionsToExport = @()
AliasesToExport = @()
}`,
    'utf8');
  return moduleRoot;
}

function assertInheritedWindowsPowerShellModulePathIsRejected(targetPath) {
  const powerShellHost = createWindowsPowerShellHost();
  const result = spawnSync(
    powerShellHost.executablePath,
    [
      '-NoLogo',
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      "$ErrorActionPreference = 'Stop'; Get-Acl -LiteralPath $env:OPENLINEOPS_TEST_ACL_PATH | Out-Null"
    ],
    {
      encoding: 'utf8',
      windowsHide: true,
      env: {
        ...process.env,
        OPENLINEOPS_TEST_ACL_PATH: targetPath
      }
    });
  assert.notEqual(
    result.status,
    0,
    'The test module path must reproduce the inherited Windows PowerShell module collision.');
}

async function withCredentialFixture(action) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'openlineops-api-credential-acl-'));
  try {
    await action({
      directory: root,
      tokenPath: path.join(root, 'credential.token')
    });
  } finally {
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}

function runIcacls(targetPath, arguments_) {
  const result = spawnSync(windowsSystemExecutablePath('icacls.exe'), [targetPath, ...arguments_], {
    encoding: 'utf8',
    windowsHide: true
  });
  assert.equal(
    result.status,
    0,
    `icacls failed (${result.status}): ${result.stderr || result.stdout}`);
}

function currentWindowsUserSid() {
  const result = spawnSync(windowsSystemExecutablePath('whoami.exe'), ['/user', '/fo', 'csv', '/nh'], {
    encoding: 'utf8',
    windowsHide: true
  });
  assert.equal(result.status, 0, result.stderr);
  const sid = result.stdout.match(/S-\d-(?:\d+-)+\d+/)?.[0];
  assert.ok(sid, 'whoami did not return the current Windows user SID.');
  return sid;
}

async function readWindowsAcl(targetPath) {
  const script = String.raw`
$ErrorActionPreference = 'Stop'
$path = [Environment]::GetEnvironmentVariable('OPENLINEOPS_TEST_ACL_PATH', 'Process')
$acl = Get-Acl -LiteralPath $path
$ownerSid = try {
  ([System.Security.Principal.NTAccount]::new($acl.Owner)).Translate(
    [System.Security.Principal.SecurityIdentifier]).Value
} catch {
  ([System.Security.Principal.SecurityIdentifier]::new($acl.Owner)).Value
}
$rules = @($acl.GetAccessRules(
  $true,
  $true,
  [System.Security.Principal.SecurityIdentifier]) | ForEach-Object {
    [ordered]@{
      sid = $_.IdentityReference.Value
      rights = $_.FileSystemRights.ToString()
      type = $_.AccessControlType.ToString()
      inherited = $_.IsInherited
      inheritance = $_.InheritanceFlags.ToString()
      propagation = $_.PropagationFlags.ToString()
    }
  })
[ordered]@{
  protected = $acl.AreAccessRulesProtected
  ownerSid = $ownerSid
  rules = $rules
} | ConvertTo-Json -Depth 5 -Compress
`;
  const host = createWindowsPowerShellHost({
    OPENLINEOPS_TEST_ACL_PATH: targetPath
  });
  const result = spawnSync(
    host.executablePath,
    [
      '-NoLogo',
      '-NoProfile',
      '-NonInteractive',
      '-EncodedCommand',
      Buffer.from(script, 'utf16le').toString('base64')
    ],
    {
      encoding: 'utf8',
      windowsHide: true,
      env: host.environment
    });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout.trim());
}

function removeEnvironmentVariableCaseInsensitively(variableName) {
  for (const name of Object.keys(process.env)) {
    if (name.toUpperCase() === variableName.toUpperCase()) {
      delete process.env[name];
    }
  }
}

function assertExactWindowsAcl(acl, userSid, directory) {
  assert.equal(acl.protected, true);
  assert.equal(acl.ownerSid, userSid);
  assert.deepEqual(
    acl.rules.map(rule => rule.sid).sort(),
    [userSid, 'S-1-5-18', 'S-1-5-32-544'].sort());
  for (const rule of acl.rules) {
    assert.equal(rule.rights, 'FullControl');
    assert.equal(rule.type, 'Allow');
    assert.equal(rule.inherited, false);
    assert.equal(
      rule.inheritance,
      directory ? 'ContainerInherit, ObjectInherit' : 'None');
    assert.equal(rule.propagation, 'None');
  }
}

function section(content, startMarker, endMarker) {
  const start = content.indexOf(startMarker);
  const end = content.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(start, -1, `Missing source marker: ${startMarker}`);
  assert.notEqual(end, -1, `Missing source marker: ${endMarker}`);
  return content.slice(start, end);
}
