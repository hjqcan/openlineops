import path from 'node:path';
import process from 'node:process';

const protectedEnvironmentNames = new Set([
  'PSMODULEPATH',
  'SYSTEMROOT',
  'WINDIR'
]);

export function createWindowsPowerShellHost(
  additionalEnvironment = {},
  inheritedEnvironment = process.env) {
  const systemRoot = resolveWindowsSystemRoot(inheritedEnvironment);
  const powerShellHome = path.win32.join(
    systemRoot,
    'System32',
    'WindowsPowerShell',
    'v1.0');
  const additionalNames = new Set(
    Object.keys(additionalEnvironment).map(name => name.toUpperCase()));
  for (const name of additionalNames) {
    if (protectedEnvironmentNames.has(name)) {
      throw new Error(`Windows PowerShell host environment cannot override ${name}.`);
    }
  }

  const environment = {};
  for (const [name, value] of Object.entries(inheritedEnvironment)) {
    const canonicalName = name.toUpperCase();
    if (value !== undefined
        && !protectedEnvironmentNames.has(canonicalName)
        && !additionalNames.has(canonicalName)) {
      environment[name] = value;
    }
  }

  environment.SystemRoot = systemRoot;
  environment.windir = systemRoot;
  environment.PSModulePath = path.win32.join(powerShellHome, 'Modules');
  for (const [name, value] of Object.entries(additionalEnvironment)) {
    if (value !== undefined) {
      environment[name] = value;
    }
  }

  return {
    executablePath: path.win32.join(powerShellHome, 'powershell.exe'),
    environment,
    systemRoot
  };
}

export function windowsSystemExecutablePath(
  executableName,
  inheritedEnvironment = process.env) {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$/u.test(executableName)) {
    throw new Error(`Windows system executable name is invalid: ${executableName}`);
  }

  return path.win32.join(
    resolveWindowsSystemRoot(inheritedEnvironment),
    'System32',
    executableName);
}

function resolveWindowsSystemRoot(environment) {
  const roots = Object.entries(environment)
    .filter(([name, value]) => name.toUpperCase() === 'SYSTEMROOT' && Boolean(value))
    .map(([, value]) => path.win32.normalize(value));
  const uniqueRoots = [...new Set(roots.map(value => value.toUpperCase()))];
  if (roots.length === 0
      || uniqueRoots.length !== 1
      || !path.win32.isAbsolute(roots[0])
      || !/^[A-Za-z]:\\/u.test(roots[0])) {
    throw new Error('The Windows system root could not be resolved for a PowerShell host.');
  }

  return roots[0];
}
