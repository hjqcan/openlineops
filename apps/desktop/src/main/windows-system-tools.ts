import path from 'node:path';
import process from 'node:process';

const windowsPowerShellExecutableRelativePath = ['System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'];
const protectedPowerShellEnvironmentNames = new Set([
  'PSMODULEPATH',
  'SYSTEMROOT',
  'WINDIR'
]);

export function createWindowsPowerShellHost(
  additionalEnvironment: NodeJS.ProcessEnv = {}): {
    executablePath: string;
    environment: NodeJS.ProcessEnv;
    systemRoot: string;
  } {
  const systemRoot = windowsSystemRoot();
  const executablePath = path.win32.join(
    systemRoot,
    ...windowsPowerShellExecutableRelativePath);
  const powerShellHome = path.win32.dirname(executablePath);
  const additionalNames = new Set(
    Object.keys(additionalEnvironment).map(name => name.toUpperCase()));
  for (const name of additionalNames) {
    if (protectedPowerShellEnvironmentNames.has(name)) {
      throw new Error(`Windows PowerShell host environment cannot override ${name}.`);
    }
  }

  const environment: NodeJS.ProcessEnv = {};
  for (const [name, value] of Object.entries(process.env)) {
    const canonicalName = name.toUpperCase();
    if (value !== undefined
        && !protectedPowerShellEnvironmentNames.has(canonicalName)
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
    executablePath,
    environment,
    systemRoot
  };
}

export function windowsSystemExecutablePath(executableName: string): string {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$/u.test(executableName)) {
    throw new Error(`Windows system executable name is invalid: ${executableName}`);
  }

  return path.win32.join(windowsSystemRoot(), 'System32', executableName);
}

function windowsSystemRoot(): string {
  const configuredRoots = Object.entries(process.env)
    .filter(([name, value]) => name.toUpperCase() === 'SYSTEMROOT' && Boolean(value))
    .map(([, value]) => path.win32.normalize(value!));
  const uniqueRoots = new Set(configuredRoots.map(value => value.toUpperCase()));
  if (configuredRoots.length === 0
      || uniqueRoots.size !== 1
      || !path.win32.isAbsolute(configuredRoots[0])
      || !/^[A-Za-z]:\\/u.test(configuredRoots[0])) {
    throw new Error('The Windows system root could not be resolved.');
  }

  return configuredRoots[0];
}
