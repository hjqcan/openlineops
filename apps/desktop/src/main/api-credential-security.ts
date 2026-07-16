import { spawnSync } from 'node:child_process';
import { chmodSync, lstatSync } from 'node:fs';
import process from 'node:process';

const systemSid = 'S-1-5-18';
const administratorsSid = 'S-1-5-32-544';
const aclPathEnvironmentVariable = 'OPENLINEOPS_CREDENTIAL_ACL_PATH';
const aclUserSidEnvironmentVariable = 'OPENLINEOPS_CREDENTIAL_ACL_USER_SID';
const aclPathKindEnvironmentVariable = 'OPENLINEOPS_CREDENTIAL_ACL_PATH_KIND';

const protectWindowsCredentialPathScript = String.raw`
$ErrorActionPreference = 'Stop'
$path = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_PATH', 'Process')
$userSidValue = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_USER_SID', 'Process')
$directory = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_PATH_KIND', 'Process') -eq 'directory'
$item = Get-Item -LiteralPath $path -Force
if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { exit 31 }
if ($item.PSIsContainer -ne $directory) { exit 32 }

& icacls.exe $path '/inheritance:r' | Out-Null
if ($LASTEXITCODE -ne 0) { exit 41 }

$acl = Get-Acl -LiteralPath $path
$existingSids = @($acl.GetAccessRules(
  $true,
  $false,
  [System.Security.Principal.SecurityIdentifier]) | ForEach-Object {
    $_.IdentityReference.Value
  } | Sort-Object -Unique)
foreach ($sidValue in $existingSids) {
  & icacls.exe $path '/remove' ('*' + $sidValue) | Out-Null
  if ($LASTEXITCODE -ne 0) { exit 42 }
}

$rights = if ($directory) { '(OI)(CI)F' } else { 'F' }
$grantArguments = @(
  $path,
  '/grant:r',
  ('*' + $userSidValue + ':' + $rights),
  ('*S-1-5-18:' + $rights),
  ('*S-1-5-32-544:' + $rights))
& icacls.exe @grantArguments | Out-Null
if ($LASTEXITCODE -ne 0) { exit 43 }
`;

const verifyWindowsCredentialPathScript = String.raw`
$ErrorActionPreference = 'Stop'
$path = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_PATH', 'Process')
$userSidValue = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_USER_SID', 'Process')
$directory = [Environment]::GetEnvironmentVariable('OPENLINEOPS_CREDENTIAL_ACL_PATH_KIND', 'Process') -eq 'directory'
$item = Get-Item -LiteralPath $path -Force
if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { exit 31 }
if ($item.PSIsContainer -ne $directory) { exit 32 }

$acl = Get-Acl -LiteralPath $path
if (-not $acl.AreAccessRulesProtected) { exit 21 }
$allowed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
[void] $allowed.Add($userSidValue)
[void] $allowed.Add('S-1-5-18')
[void] $allowed.Add('S-1-5-32-544')
$ownerSid = $acl.Owner
try {
  $ownerSid = ([System.Security.Principal.NTAccount]::new($acl.Owner)).Translate(
    [System.Security.Principal.SecurityIdentifier]).Value
} catch {
  $ownerSid = ([System.Security.Principal.SecurityIdentifier]::new($acl.Owner)).Value
}
if (-not $allowed.Contains($ownerSid)) { exit 24 }

$expectedInheritance = if ($directory) {
  [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
} else {
  [System.Security.AccessControl.InheritanceFlags]::None
}
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$rules = $acl.GetAccessRules(
  $true,
  $true,
  [System.Security.Principal.SecurityIdentifier])
foreach ($rule in $rules) {
  $sid = $rule.IdentityReference.Value
  if ($rule.IsInherited) { exit 25 }
  if (-not $allowed.Contains($sid)) { exit 22 }
  if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { exit 26 }
  if ($rule.FileSystemRights -ne [System.Security.AccessControl.FileSystemRights]::FullControl) { exit 27 }
  if ($rule.InheritanceFlags -ne $expectedInheritance) { exit 28 }
  if ($rule.PropagationFlags -ne [System.Security.AccessControl.PropagationFlags]::None) { exit 29 }
  if (-not $seen.Add($sid)) { exit 30 }
}
if ($seen.Count -ne $allowed.Count) { exit 23 }
foreach ($sid in $allowed) {
  if (-not $seen.Contains($sid)) { exit 23 }
}
`;

export function protectCredentialPath(targetPath: string, directory: boolean): void {
  assertCredentialPathType(targetPath, directory);

  if (process.platform !== 'win32') {
    chmodSync(targetPath, directory ? 0o700 : 0o600);
    verifyCredentialPathProtection(targetPath, directory);
    return;
  }

  const userSid = currentWindowsUserSid();
  runWindowsAclScript(
    protectWindowsCredentialPathScript,
    targetPath,
    directory,
    userSid,
    'protection');
  verifyCredentialPathProtection(targetPath, directory);
}

export function verifyCredentialPathProtection(targetPath: string, directory: boolean): void {
  assertCredentialPathType(targetPath, directory);
  if (process.platform !== 'win32') {
    const protectedMode = lstatSync(targetPath).mode & 0o777;
    if (protectedMode !== (directory ? 0o700 : 0o600)) {
      throw new Error(`API credential permissions are not private: ${targetPath}`);
    }
    return;
  }

  const userSid = currentWindowsUserSid();
  runWindowsAclScript(
    verifyWindowsCredentialPathScript,
    targetPath,
    directory,
    userSid,
    'verification');
}

function assertCredentialPathType(targetPath: string, directory: boolean): void {
  const metadata = lstatSync(targetPath);
  if (metadata.isSymbolicLink()
      || (directory ? !metadata.isDirectory() : !metadata.isFile())
      || (!directory && metadata.nlink !== 1)) {
    throw new Error(`API credential ACL target has an unsafe filesystem type: ${targetPath}`);
  }
}

function currentWindowsUserSid(): string {
  const identity = spawnSync('whoami.exe', ['/user', '/fo', 'csv', '/nh'], {
    encoding: 'utf8',
    windowsHide: true
  });
  const userSid = identity.status === 0
    ? identity.stdout.match(/S-\d-(?:\d+-)+\d+/)?.[0]
    : undefined;
  if (!userSid) {
    throw new Error('The current Windows user SID could not be resolved for API credential ACLs.');
  }

  return userSid;
}

function runWindowsAclScript(
  script: string,
  targetPath: string,
  directory: boolean,
  userSid: string,
  operation: 'protection' | 'verification'): void {
  const encodedScript = Buffer.from(script, 'utf16le').toString('base64');
  const result = spawnSync(
    'powershell.exe',
    [
      '-NoLogo',
      '-NoProfile',
      '-NonInteractive',
      '-EncodedCommand',
      encodedScript
    ],
    {
      encoding: 'utf8',
      windowsHide: true,
      env: {
        ...process.env,
        [aclPathEnvironmentVariable]: targetPath,
        [aclUserSidEnvironmentVariable]: userSid,
        [aclPathKindEnvironmentVariable]: directory ? 'directory' : 'file'
      }
    });
  if (result.status !== 0) {
    const detail = [result.error?.message, result.stderr, result.stdout]
      .map(value => value?.trim())
      .filter((value): value is string => Boolean(value))
      .join(' | ');
    throw new Error(
      `API credential ACL ${operation} failed: ${targetPath}`
      + ` (exit ${result.status ?? 'not-started'}${detail ? `; ${detail}` : ''})`);
  }
}
