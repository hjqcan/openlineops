import {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  type IpcMainInvokeEvent,
  type MessageBoxOptions,
  type WebContents
} from 'electron';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import {
  createReadStream,
  existsSync,
  lstatSync,
  mkdirSync,
  openAsBlob,
  readFileSync,
  rmSync,
  writeFileSync
} from 'node:fs';
import {
  createHash,
  createHmac,
  createPrivateKey,
  createPublicKey,
  generateKeyPairSync,
  randomBytes
} from 'node:crypto';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import type {
  ApiRequestOptions,
  ApiResponse,
  ApplicationExtensionImportResult,
  BackendStatus,
  DesktopConfig,
  DesktopCloseCoordinatorBinding,
  EditorDocumentWriteOptions,
  ExternalProgramDirectorySelectionResult,
  SelectDirectoryOptions,
  SelectDirectoryResult,
  SelectProjectFileOptions,
  TraceArtifactSaveOptions
} from '../shared/desktop-api.js';
import { saveTraceArtifact } from './trace-artifact-save.js';
import {
  canonicalizeLocalBackendBaseUrl,
  fetchAuthenticatedBackend,
  resolveCanonicalBackendApiUrl
} from './backend-api-security.js';
import { classifyBackendProcessError } from './backend-process-error-policy.js';
import { verifyBackendProcessHandshakeServer } from './backend-process-handshake.js';
import {
  requireDevelopmentHostAssembly,
  resolveDevelopmentHostDefinitions
} from './development-host-contract.js';
import {
  BackendSessionLifecycle,
  type AuthenticatedBackendSession
} from './backend-session-lifecycle.js';
import {
  protectCredentialPath,
  verifyCredentialPathProtection
} from './api-credential-security.js';
import { createLocalSqliteConnectionString } from './local-sqlite-connection.js';
import {
  ensurePackagedRuntimeDataBinding,
  ensureCanonicalDesktopUserDataDirectory,
  validatePackagedContentUserDataSeparation,
  type PackagedRuntimeDataBindingResult
} from './packaged-runtime-data-binding.js';
import {
  canonicalizeTrustedDevRendererUrl,
  isTrustedRendererDocumentUrl,
  isTrustedRendererIpcContext,
  verifyTrustedDevRendererServer
} from './renderer-navigation-security.js';
import {
  assertApplicationExtensionArchiveUnchanged,
  deriveApplicationExtensionPortableId,
  inspectApplicationExtensionArchive
} from './application-extension-import-security.js';
import {
  assertExternalProgramDirectoryUnchanged,
  inspectExternalProgramDirectory,
  type ExternalProgramDirectoryIdentity
} from './external-program-directory-import-security.js';
import {
  bindActiveProjectStartupWorkspace,
  resolveActiveProjectFile
} from './backend-project-session.js';
import {
  DesktopCloseRequestCoordinator,
  desktopCloseBindingsEqual
} from './desktop-close-request-coordinator.js';
import {
  DesktopApplicationQuitCoordinator
} from './desktop-application-quit-coordinator.js';
import {
  hasDesktopBackendProcessExited,
  stopDesktopBackendForApplicationShutdown
} from './desktop-backend-shutdown.js';
import {
  DesktopRendererRecoveryCoordinator,
  type DesktopRendererRecoveryRequest
} from './desktop-renderer-recovery-coordinator.js';
import {
  DesktopRendererCloseReadiness
} from './desktop-renderer-close-readiness.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const desktopProcessCreationTime = process.getCreationTime();
if (desktopProcessCreationTime === null
    || !Number.isFinite(desktopProcessCreationTime)
    || desktopProcessCreationTime <= 0) {
  throw new Error('Electron did not expose its exact process creation identity.');
}
const desktopProcessStartedAtUnixMilliseconds = Math.trunc(
  desktopProcessCreationTime);

let mainWindow: BrowserWindow | null = null;
let backendProcess: ChildProcessWithoutNullStreams | null = null;
let backendStartedAtUtc: string | null = null;
let lastExitCode: number | null = null;
let trustedRendererDocumentUrl: string | null = null;
const backendSessionLifecycle =
  new BackendSessionLifecycle<ChildProcessWithoutNullStreams>();
let backendStartPromise: Promise<BackendStatus> | null = null;
let applicationShutdownPromise: Promise<void> | null = null;
let activeProjectFilePath: string | null = null;
let backendStatusSequence = 0;
let applicationQuitting = false;
const applicationQuitCoordinator = new DesktopApplicationQuitCoordinator();
const desktopCloseSessions = new Map<number, DesktopCloseWindowSession>();
const recentLogs: string[] = [];

const ownsPrimaryInstance = app.requestSingleInstanceLock();
if (!ownsPrimaryInstance) {
  app.quit();
}

if (ownsPrimaryInstance) {
  if (app.isPackaged) {
    validatePackagedContentUserDataSeparation(
      path.dirname(process.resourcesPath),
      app.getPath('userData'));
  }
  ensureCanonicalDesktopUserDataDirectory(app.getPath('userData'));
}

const apiCredentials = ownsPrimaryInstance
  ? provisionLocalApiCredentials()
  : null;
const desktopBaseConfig = apiCredentials
  ? createDesktopBaseConfig(apiCredentials.actorId)
  : null;

const backendHandshakeTimeoutMs = 30000;
const backendProcessTerminationTimeoutMs = 10000;

interface DesktopCloseWindowSession {
  readonly window: BrowserWindow;
  readonly webContents: WebContents;
  readonly coordinator: DesktopCloseRequestCoordinator;
  readonly recoveryCoordinator: DesktopRendererRecoveryCoordinator;
  readonly readiness: DesktopRendererCloseReadiness;
  binding: DesktopCloseCoordinatorBinding;
}

interface BackendLaunchConfig {
  executablePath: string;
  arguments: string[];
  workingDirectory: string;
  environment: NodeJS.ProcessEnv;
  packagedRuntimeBinding: PackagedRuntimeDataBindingResult | null;
}

interface LocalStationPackageProvisioning {
  environment: NodeJS.ProcessEnv;
}

interface LocalApiCredentials {
  actorId: string;
  standardToken: string;
  safetyToken: string;
  environment: NodeJS.ProcessEnv;
}

interface DesktopBaseConfig {
  apiActorId: string;
  logPath: string;
  isPackaged: boolean;
  publicEvidenceMode: boolean;
}

type ActiveBackendSession =
  AuthenticatedBackendSession<ChildProcessWithoutNullStreams>;

interface PendingExternalProgramDirectory {
  identity: ExternalProgramDirectoryIdentity;
  backendSessionNonce: string;
  projectId: string;
  applicationId: string;
  resourceId: string;
  inFlight: boolean;
  expiresAtMilliseconds: number;
}

const pendingExternalProgramDirectories = new Map<string, PendingExternalProgramDirectory>();
const externalProgramDirectorySelectionLifetimeMilliseconds = 30 * 60 * 1000;
const maximumPendingExternalProgramDirectories = 16;

interface BackendHandshakeDocument {
  ProcessId: number;
  Origin: string;
  StartedAtUnixMilliseconds: number;
}

function createDesktopBaseConfig(apiActorId: string): DesktopBaseConfig {
  const logPath = process.env.OPENLINEOPS_DESKTOP_LOG_PATH
    ? path.resolve(process.env.OPENLINEOPS_DESKTOP_LOG_PATH)
    : path.join(app.getPath('userData'), 'logs');

  return {
    apiActorId,
    logPath,
    isPackaged: app.isPackaged,
    publicEvidenceMode: process.env.OPENLINEOPS_PUBLIC_EVIDENCE_MODE === '1'
  };
}

function requireApiCredentials(): LocalApiCredentials {
  if (apiCredentials === null) {
    throw new Error('Only the primary Studio instance may provision local API credentials.');
  }
  return apiCredentials;
}

function requireDesktopBaseConfig(): DesktopBaseConfig {
  if (desktopBaseConfig === null) {
    throw new Error('Only the primary Studio instance may create a desktop configuration.');
  }
  return desktopBaseConfig;
}

function createDesktopConfig(session: ActiveBackendSession): DesktopConfig {
  return {
    ...requireDesktopBaseConfig(),
    apiBaseUrl: session.apiBaseUrl,
    apiAccessToken: session.standardToken
  };
}

function createBackendLaunchConfig(
  apiCredentialProvisioning: LocalApiCredentials,
  handshakePath: string,
  handshakeNonce: string,
  projectFilePath: string | null
): BackendLaunchConfig {
  const launchProjectFilePath = resolveActiveProjectFile(projectFilePath);
  const stationPackages = provisionLocalStationPackages();
  if (!app.isPackaged) {
    const appPath = app.getAppPath();
    const dataDirectory = path.join(app.getPath('userData'), 'data');
    const traceArtifactRoot = path.join(dataDirectory, 'trace-artifacts');
    mkdirSync(dataDirectory, { recursive: true });
    const repoRoot = process.env.OPENLINEOPS_REPO_ROOT
      ? path.resolve(process.env.OPENLINEOPS_REPO_ROOT)
      : path.resolve(appPath, '..', '..');
    const developmentHosts = resolveDevelopmentHostDefinitions(
      appPath,
      repoRoot);
    const apiAssemblyPath = requireDevelopmentHostAssembly(
      developmentHosts,
      'OpenLineOps.Api');
    const scriptWorkerAssemblyPath = requireDevelopmentHostAssembly(
      developmentHosts,
      'OpenLineOps.ScriptWorker');
    const pluginHostAssemblyPath = requireDevelopmentHostAssembly(
      developmentHosts,
      'OpenLineOps.PluginHost');
    if (!existsSync(apiAssemblyPath)
        || !existsSync(scriptWorkerAssemblyPath)
        || !existsSync(pluginHostAssemblyPath)) {
      throw new Error(
        'Development API, ScriptWorker, or PluginHost binaries are missing; build the .NET solution before starting Studio.');
    }

    return {
      executablePath: 'dotnet',
      arguments: [apiAssemblyPath, '--urls', 'http://127.0.0.1:0'],
      workingDirectory: path.dirname(apiAssemblyPath),
      environment: bindActiveProjectStartupWorkspace({
        ...process.env,
        ...stationPackages.environment,
        ...apiCredentialProvisioning.environment,
        OPENLINEOPS_DESKTOP_HANDSHAKE_FILE: handshakePath,
        OPENLINEOPS_DESKTOP_HANDSHAKE_NONCE: handshakeNonce,
        OPENLINEOPS_DESKTOP_PARENT_PROCESS_ID: String(process.pid),
        OPENLINEOPS_DESKTOP_PARENT_PROCESS_STARTED_AT_UNIX_MS:
          String(desktopProcessStartedAtUnixMilliseconds),
        ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT ?? 'Development',
        OpenLineOps__Traceability__ArtifactStorage__RootPath: traceArtifactRoot,
        OpenLineOps__Plugins__EventLog__DatabasePath: path.join(
          dataDirectory,
          'openlineops-plugin-events.sqlite'),
        OpenLineOps__Runtime__Scripting__Python__ExecutionMode: 'ProcessIsolated',
        OpenLineOps__Runtime__Scripting__Python__WorkerFileName: 'dotnet',
        OpenLineOps__Runtime__Scripting__Python__WorkerArguments:
          `"${scriptWorkerAssemblyPath}"`,
        OpenLineOps__Runtime__Scripting__Python__WorkerWorkingDirectory:
          path.dirname(scriptWorkerAssemblyPath),
        OpenLineOps__Plugins__ExternalHost__ExecutablePath: 'dotnet',
        OpenLineOps__Plugins__ExternalHost__ArgumentsTemplate:
          `"${pluginHostAssemblyPath}" --openlineops-plugin-host --manifest "{ManifestPath}" --entry "{EntryAssemblyPath}" --type "{EntryType}"`,
        OpenLineOps__Runtime__Scripting__Python__Sandbox__IsolationMode: 'ExternalProcess',
        OpenLineOps__Runtime__Scripting__Python__Sandbox__RequireLeastPrivilegeExecution: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection: 'false',
        OpenLineOps__Devices__ExternalProgramHost__RequireAppContainerIsolation: 'true',
        OpenLineOps__Devices__ExternalProgramHost__WorkspaceRootPath: path.join(
          dataDirectory,
          'external-program-workspaces'),
        OpenLineOps__Devices__ExternalProgramHost__EvidenceRootPath: traceArtifactRoot,
        OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileName:
          'OpenLineOps.Studio.ExternalPrograms'
      }, launchProjectFilePath),
      packagedRuntimeBinding: null
    };
  }

  const runtimeRoot = path.join(app.getAppPath(), 'runtime');
  const apiDirectory = path.join(runtimeRoot, 'api');
  const scriptWorkerDirectory = path.join(runtimeRoot, 'script-worker');
  const pluginHostDirectory = path.join(runtimeRoot, 'plugin-host');
  const packagedContentDirectory = path.dirname(process.resourcesPath);
  const runtimeBinding = ensurePackagedRuntimeDataBinding(
    packagedContentDirectory,
    app.getPath('userData'));
  const runtimeStateDirectory = runtimeBinding.runtimeStateDirectory;
  const traceArtifactRoot = path.join(runtimeStateDirectory, 'trace-artifacts');
  const productionCoordinationDatabasePath = path.join(
    runtimeStateDirectory,
    'openlineops-production-coordination.sqlite');

  return {
    executablePath: path.join(apiDirectory, 'OpenLineOps.Api.exe'),
    arguments: ['--urls', 'http://127.0.0.1:0'],
    workingDirectory: apiDirectory,
    environment: bindActiveProjectStartupWorkspace({
      ...process.env,
      ...stationPackages.environment,
      ...apiCredentialProvisioning.environment,
      OPENLINEOPS_DESKTOP_HANDSHAKE_FILE: handshakePath,
      OPENLINEOPS_DESKTOP_HANDSHAKE_NONCE: handshakeNonce,
      OPENLINEOPS_DESKTOP_PARENT_PROCESS_ID: String(process.pid),
      OPENLINEOPS_DESKTOP_PARENT_PROCESS_STARTED_AT_UNIX_MS:
        String(desktopProcessStartedAtUnixMilliseconds),
      ASPNETCORE_ENVIRONMENT: 'Production',
      DOTNET_ENVIRONMENT: 'Production',
      OpenLineOps__Runtime__Persistence__DatabasePath: path.join(
        runtimeStateDirectory,
        'openlineops-runtime.sqlite'),
      OpenLineOps__Runtime__Coordination__Provider: 'Sqlite',
      OpenLineOps__Runtime__Coordination__ConnectionString:
        createLocalSqliteConnectionString(productionCoordinationDatabasePath),
      OpenLineOps__Runtime__AgentTransport__Provider: 'Disabled',
      OpenLineOps__Runtime__StationExecution__Provider: 'InProcess',
      OpenLineOps__Traceability__Persistence__DatabasePath: path.join(
        runtimeStateDirectory,
        'openlineops-traceability.sqlite'),
      OpenLineOps__Traceability__ArtifactStorage__RootPath: traceArtifactRoot,
      OpenLineOps__Devices__Persistence__DatabasePath: path.join(
        runtimeStateDirectory,
        'openlineops-devices.sqlite'),
      OpenLineOps__Operations__Persistence__DatabasePath: path.join(
        runtimeStateDirectory,
        'openlineops-operations.sqlite'),
      OpenLineOps__Plugins__EventLog__DatabasePath: path.join(
        runtimeStateDirectory,
        'openlineops-plugin-events.sqlite'),
      OpenLineOps__Plugins__ExternalHost__ExecutablePath: path.join(
        pluginHostDirectory,
        'OpenLineOps.PluginHost.exe'),
      OpenLineOps__Plugins__ExternalHost__ArgumentsTemplate:
        '--openlineops-plugin-host --manifest "{ManifestPath}" --entry "{EntryAssemblyPath}" --type "{EntryType}"',
      OpenLineOps__Runtime__Scripting__Python__ExecutionMode: 'ProcessIsolated',
      OpenLineOps__Runtime__Scripting__Python__WorkerFileName: path.join(
        scriptWorkerDirectory,
        'OpenLineOps.ScriptWorker.exe'),
      OpenLineOps__Runtime__Scripting__Python__WorkerWorkingDirectory: scriptWorkerDirectory,
      OpenLineOps__Runtime__Scripting__Python__Sandbox__IsolationMode: 'ExternalProcess',
      OpenLineOps__Runtime__Scripting__Python__Sandbox__RequireLeastPrivilegeExecution: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection: 'false',
      OpenLineOps__Devices__ExternalProgramHost__RequireAppContainerIsolation: 'true',
      OpenLineOps__Devices__ExternalProgramHost__WorkspaceRootPath: path.join(
        runtimeStateDirectory,
        'external-program-workspaces'),
      OpenLineOps__Devices__ExternalProgramHost__EvidenceRootPath: traceArtifactRoot,
      OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileName:
        'OpenLineOps.Studio.ExternalPrograms',
      OpenLineOps__Desktop__AllowedOrigins__0: 'null'
    }, launchProjectFilePath),
    packagedRuntimeBinding: runtimeBinding
  };
}

function provisionLocalApiCredentials(): LocalApiCredentials {
  const actorId = process.env.OPENLINEOPS_API_ACTOR_ID ?? 'studio.local';
  if (!/^[A-Za-z0-9][A-Za-z0-9._:@/-]{0,127}$/.test(actorId)) {
    throw new Error('OPENLINEOPS_API_ACTOR_ID must be one canonical Actor identity.');
  }

  const securityDirectory = path.join(app.getPath('userData'), 'data', 'security');
  const configuredStandardPath = process.env.OPENLINEOPS_API_TOKEN_FILE;
  const configuredSafetyPath = process.env.OPENLINEOPS_API_SAFETY_TOKEN_FILE;
  if ((configuredStandardPath === undefined) !== (configuredSafetyPath === undefined)) {
    throw new Error(
      'OPENLINEOPS_API_TOKEN_FILE and OPENLINEOPS_API_SAFETY_TOKEN_FILE must be configured together.');
  }

  const externallyProvisioned = configuredStandardPath !== undefined;
  if (externallyProvisioned
      && (!configuredStandardPath?.trim() || !configuredSafetyPath?.trim())) {
    throw new Error('Externally provisioned API credential paths cannot be empty.');
  }
  if (externallyProvisioned
      && (!path.isAbsolute(configuredStandardPath!) || !path.isAbsolute(configuredSafetyPath!))) {
    throw new Error('Externally provisioned API credential paths must be absolute.');
  }

  const standardPath = externallyProvisioned
    ? path.normalize(configuredStandardPath!)
    : path.join(securityDirectory, 'studio-standard.token');
  const safetyPath = externallyProvisioned
    ? path.normalize(configuredSafetyPath!)
    : path.join(securityDirectory, 'studio-safety.token');
  if (standardPath === safetyPath) {
    throw new Error('Standard and Safety API credentials must use separate token files.');
  }

  const standardExists = existsSync(standardPath);
  const safetyExists = existsSync(safetyPath);
  if (externallyProvisioned && (!standardExists || !safetyExists)) {
    throw new Error(
      'Externally provisioned API credential files must both already exist; Studio never creates credentials outside its protected security directory.');
  }

  if (!externallyProvisioned && standardExists !== safetyExists) {
    throw new Error(
      'The local API credential pair is incomplete. Restore both token files or remove both to reprovision.');
  }

  if (externallyProvisioned) {
    verifyExternalCredentialFile(standardPath);
    verifyExternalCredentialFile(safetyPath);
  } else {
    mkdirSync(securityDirectory, { recursive: true, mode: 0o700 });
    protectCredentialPath(securityDirectory, true);
    if (!standardExists) {
      writeCredentialFile(standardPath, securityDirectory);
      writeCredentialFile(safetyPath, securityDirectory);
    }

    protectCredentialPath(standardPath, false);
    protectCredentialPath(safetyPath, false);
  }

  const standardToken = readCredentialFile(standardPath);
  const safetyToken = readCredentialFile(safetyPath);
  if (standardToken === safetyToken) {
    throw new Error('Standard and Safety API credentials must be cryptographically distinct.');
  }

  return {
    actorId,
    standardToken,
    safetyToken,
    environment: {
      OpenLineOps__Security__Callers__0__CredentialId: 'studio-standard',
      OpenLineOps__Security__Callers__0__ActorId: actorId,
      OpenLineOps__Security__Callers__0__TokenSha256: tokenSha256(standardToken),
      OpenLineOps__Security__Callers__0__Roles__0: 'Engineering',
      OpenLineOps__Security__Callers__0__Roles__1: 'Operator',
      OpenLineOps__Security__Callers__1__CredentialId: 'studio-safety',
      OpenLineOps__Security__Callers__1__ActorId: actorId,
      OpenLineOps__Security__Callers__1__TokenSha256: tokenSha256(safetyToken),
      OpenLineOps__Security__Callers__1__Roles__0: 'Safety'
    }
  };
}

function writeCredentialFile(filePath: string, protectedDirectory: string): void {
  if (path.dirname(filePath) !== protectedDirectory) {
    throw new Error('Studio-managed API credentials can only be created in its protected security directory.');
  }

  verifyCredentialPathProtection(protectedDirectory, true);
  writeFileSync(filePath, randomBytes(32).toString('base64url'), {
    encoding: 'utf8',
    flag: 'wx',
    mode: 0o600
  });
  protectCredentialPath(filePath, false);
}

function verifyExternalCredentialFile(filePath: string): void {
  verifyCredentialPathProtection(path.dirname(filePath), true);
  verifyCredentialPathProtection(filePath, false);
}

function readCredentialFile(filePath: string): string {
  const metadata = lstatSync(filePath);
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.nlink !== 1 || metadata.size > 256) {
    throw new Error(`API credential path is not one private regular file: ${filePath}`);
  }

  const token = readFileSync(filePath, 'utf8');
  if (!/^[A-Za-z0-9_-]{43,86}$/.test(token)) {
    throw new Error(`API credential file does not contain one canonical token: ${filePath}`);
  }

  let decoded: Buffer;
  try {
    decoded = Buffer.from(token, 'base64url');
  } catch {
    throw new Error(`API credential file does not contain valid base64url: ${filePath}`);
  }
  if (decoded.byteLength < 32
      || decoded.byteLength > 64
      || decoded.toString('base64url') !== token) {
    throw new Error(`API credential must encode between 32 and 64 random bytes: ${filePath}`);
  }

  return token;
}

function tokenSha256(token: string): string {
  return createHash('sha256').update(token, 'utf8').digest('hex');
}

function deriveBackendSessionCredentials(
  roots: LocalApiCredentials,
  nonce: string
): LocalApiCredentials {
  const derive = (rootToken: string, purpose: string): string => createHmac(
    'sha256',
    Buffer.from(rootToken, 'base64url'))
    .update('OpenLineOps Studio backend session\0', 'utf8')
    .update(nonce, 'utf8')
    .update('\0', 'utf8')
    .update(purpose, 'utf8')
    .digest('base64url');
  const standardToken = derive(roots.standardToken, 'standard');
  const safetyToken = derive(roots.safetyToken, 'safety');
  return {
    actorId: roots.actorId,
    standardToken,
    safetyToken,
    environment: {
      OpenLineOps__Security__Callers__0__CredentialId: 'studio-standard-session',
      OpenLineOps__Security__Callers__0__ActorId: roots.actorId,
      OpenLineOps__Security__Callers__0__TokenSha256: tokenSha256(standardToken),
      OpenLineOps__Security__Callers__0__Roles__0: 'Engineering',
      OpenLineOps__Security__Callers__0__Roles__1: 'Operator',
      OpenLineOps__Security__Callers__1__CredentialId: 'studio-safety-session',
      OpenLineOps__Security__Callers__1__ActorId: roots.actorId,
      OpenLineOps__Security__Callers__1__TokenSha256: tokenSha256(safetyToken),
      OpenLineOps__Security__Callers__1__Roles__0: 'Safety'
    }
  };
}

function provisionLocalStationPackages(): LocalStationPackageProvisioning {
  const root = path.join(app.getPath('userData'), 'data', 'station-packages');
  const keyDirectory = path.join(root, 'keys');
  const distributionDirectory = path.join(root, 'distribution');
  const deploymentCatalogDirectory = path.join(root, 'deployment-catalog');
  const privateKeyPath = path.join(keyDirectory, 'release-signing-private.pem');
  const publicKeyPath = path.join(keyDirectory, 'release-signing-public.pem');
  mkdirSync(keyDirectory, { recursive: true });
  mkdirSync(distributionDirectory, { recursive: true });
  mkdirSync(deploymentCatalogDirectory, { recursive: true });

  const privateKeyExists = existsSync(privateKeyPath);
  const publicKeyExists = existsSync(publicKeyPath);
  if (privateKeyExists !== publicKeyExists) {
    throw new Error(
      `Local Station package signing identity is incomplete under ${keyDirectory}. `
      + 'Restore both key files or remove both to provision a new identity.');
  }

  if (!privateKeyExists) {
    const pair = generateKeyPairSync('rsa', {
      modulusLength: 3072,
      publicKeyEncoding: { type: 'spki', format: 'pem' },
      privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
    });
    writeFileSync(privateKeyPath, pair.privateKey, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    writeFileSync(publicKeyPath, pair.publicKey, { encoding: 'utf8', flag: 'wx' });
  }

  const privateKeyPem = readFileSync(privateKeyPath, 'utf8');
  const publicKeyPem = readFileSync(publicKeyPath, 'utf8');
  const privateKey = createPrivateKey(privateKeyPem);
  const derivedPublicKeyPem = createPublicKey(privateKey)
    .export({ type: 'spki', format: 'pem' })
    .toString();
  const configuredPublicKeyPem = createPublicKey(publicKeyPem)
    .export({ type: 'spki', format: 'pem' })
    .toString();
  if (derivedPublicKeyPem !== configuredPublicKeyPem) {
    throw new Error('Local Station package signing private key does not match its trust public key.');
  }

  const keyId = `studio-${createHash('sha256')
    .update(configuredPublicKeyPem, 'utf8')
    .digest('hex')
    .slice(0, 24)}`;
  return {
    environment: {
      OpenLineOps__Projects__StationPackages__DistributionDirectory: distributionDirectory,
      OpenLineOps__Projects__StationPackages__DeploymentCatalogDirectory:
        deploymentCatalogDirectory,
      OpenLineOps__Projects__StationPackages__SigningKeyId: keyId,
      OpenLineOps__Projects__StationPackages__SigningPrivateKeyPath: privateKeyPath,
      OpenLineOps__Runtime__AgentTransport__DeploymentCatalogDirectory:
        deploymentCatalogDirectory,
      OpenLineOps__Agent__PackageDistributionDirectory: distributionDirectory,
      [`OpenLineOps__Agent__TrustedPackagePublicKeyFiles__${keyId}`]: publicKeyPath
    }
  };
}

async function createWindow(): Promise<void> {
  const packagedRendererPath = path.join(__dirname, '..', '..', 'dist', 'index.html');
  if (app.isPackaged) {
    trustedRendererDocumentUrl = pathToFileURL(packagedRendererPath).href;
  } else {
    const configuredRendererUrl = process.env.VITE_DEV_SERVER_URL;
    if (!configuredRendererUrl) {
      throw new Error('Development renderer startup requires its launcher-assigned URL.');
    }
    const candidateRendererUrl = canonicalizeTrustedDevRendererUrl(
      configuredRendererUrl);
    const rendererNonce = process.env.OPENLINEOPS_RENDERER_NONCE;
    if (!rendererNonce) {
      throw new Error('Development renderer startup requires one launch nonce.');
    }
    await waitForTrustedDevRenderer(candidateRendererUrl, rendererNonce);
    trustedRendererDocumentUrl = candidateRendererUrl;
  }
  const ownedWindow = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1180,
    minHeight: 760,
    title: 'OpenLineOps',
    backgroundColor: '#f6f8fb',
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });
  mainWindow = ownedWindow;
  const ownedWebContents = ownedWindow.webContents;
  const ownedWebContentsId = ownedWebContents.id;
  const closeReadiness = new DesktopRendererCloseReadiness();
  const closeSession: DesktopCloseWindowSession = {
    window: ownedWindow,
    webContents: ownedWebContents,
    coordinator: new DesktopCloseRequestCoordinator(),
    recoveryCoordinator: new DesktopRendererRecoveryCoordinator(),
    readiness: closeReadiness,
    binding: {
      windowId: ownedWindow.id,
      webContentsId: ownedWebContentsId,
      rendererGeneration: closeReadiness.generation
    }
  };
  desktopCloseSessions.set(ownedWebContentsId, closeSession);

  const preventUntrustedNavigation = (event: Electron.Event, navigationUrl: string): void => {
    if (!trustedRendererDocumentUrl
        || !isTrustedRendererDocumentUrl(navigationUrl, trustedRendererDocumentUrl)) {
      event.preventDefault();
    }
  };
  ownedWebContents.on('will-navigate', preventUntrustedNavigation);
  ownedWebContents.on('will-redirect', preventUntrustedNavigation);
  const releaseOwnedCloseRequest = (reason: string): boolean => {
    const requestId = closeSession.coordinator.pendingId;
    if (closeSession.coordinator.release(closeSession.binding)) {
      appendLog(`Desktop close request ${requestId} released because ${reason}.`);
      return true;
    }
    return false;
  };
  const advanceRendererGeneration = (reason: string): void => {
    if (releaseOwnedCloseRequest(reason)) {
      cancelCoordinatedApplicationQuit(
        `the renderer generation changed during ${reason}`);
    }
    const rendererGeneration = closeSession.readiness.startNavigation();
    closeSession.binding = {
      windowId: ownedWindow.id,
      webContentsId: ownedWebContentsId,
      rendererGeneration
    };
  };
  const showRendererRecoveryMessageBox = (
    options: MessageBoxOptions
  ): Promise<Electron.MessageBoxReturnValue> => {
    if (ownedWebContents.isDestroyed()) {
      return dialog.showMessageBox(options);
    }
    return dialog.showMessageBox(ownedWindow, options);
  };
  const closeCoordinatorIsReady = (): boolean => closeSession.readiness.isReady;
  const presentRendererRecoveryRequest = (
    recoveryRequest: DesktopRendererRecoveryRequest
  ): void => {
    const continueRecoveryQueue = (): void => {
      const nextRequest = closeSession.recoveryCoordinator.complete(
        recoveryRequest,
        closeSession.readiness.generation);
      if (nextRequest !== null && !ownedWindow.isDestroyed()) {
        presentRendererRecoveryRequest(nextRequest);
      }
    };
    if (applicationQuitting
        || ownedWindow.isDestroyed()
        || recoveryRequest.rendererGeneration !== closeSession.readiness.generation) {
      cancelCoordinatedApplicationQuit('a stale renderer recovery request was dropped');
      continueRecoveryQueue();
      return;
    }

    const action = async (): Promise<void> => {
      if (recoveryRequest.reason === 'Unresponsive') {
        const result = await showRendererRecoveryMessageBox({
          type: 'warning',
          title: 'OpenLineOps is not responding',
          message: 'The Studio editor is not responding.',
          detail: 'Wait to preserve in-memory work. Force Close may lose unsaved editor changes.',
          buttons: ['Wait', 'Force Close'],
          defaultId: 0,
          cancelId: 0,
          noLink: true
        });
        if (ownedWindow.isDestroyed()
            || recoveryRequest.rendererGeneration !== closeSession.readiness.generation) {
          cancelCoordinatedApplicationQuit('the unresponsive renderer recovery became stale');
          return;
        }
        if (result.response !== 1) {
          cancelCoordinatedApplicationQuit('the operator chose Wait for the renderer');
          return;
        }
        if (closeCoordinatorIsReady()) {
          ownedWindow.close();
          return;
        }
        releaseOwnedCloseRequest('the operator explicitly chose Force Close');
        ownedWindow.destroy();
        return;
      }

      if (recoveryRequest.reason === 'RendererGone') {
        const result = await showRendererRecoveryMessageBox({
          type: 'error',
          title: 'OpenLineOps renderer stopped',
          message: 'The Studio editor process stopped unexpectedly.',
          detail: 'Reload Studio to reconnect to the current backend. Closing may lose unsaved in-memory changes.',
          buttons: ['Reload Studio', 'Close Studio'],
          defaultId: 0,
          cancelId: 0,
          noLink: true
        });
        if (ownedWindow.isDestroyed()
            || recoveryRequest.rendererGeneration !== closeSession.readiness.generation) {
          cancelCoordinatedApplicationQuit('the stopped-renderer recovery became stale');
          return;
        }
        if (result.response === 0) {
          cancelCoordinatedApplicationQuit('the operator chose to reload the renderer');
          ownedWebContents.reload();
          return;
        }
        releaseOwnedCloseRequest('the operator explicitly closed the stopped renderer');
        ownedWindow.destroy();
        return;
      }

      const result = await showRendererRecoveryMessageBox({
        type: 'warning',
        title: 'OpenLineOps editor unavailable',
        message: 'Studio could not verify that every editor is safe to close.',
        detail: 'Keep Studio open to preserve in-memory work. Close Studio may lose unsaved changes.',
        buttons: ['Keep Studio Open', 'Close Studio'],
        defaultId: 0,
        cancelId: 0,
        noLink: true
      });
      if (ownedWindow.isDestroyed()
          || recoveryRequest.rendererGeneration !== closeSession.readiness.generation) {
        cancelCoordinatedApplicationQuit('the close recovery became stale');
        return;
      }
      if (result.response !== 1) {
        cancelCoordinatedApplicationQuit('the operator kept Studio open');
        return;
      }
      releaseOwnedCloseRequest('the operator explicitly chose the native recovery close');
      ownedWindow.destroy();
    };

    void action()
      .catch(error => {
        appendLog(`Renderer recovery prompt failed: ${
          error instanceof Error ? error.message : String(error)}`);
        cancelCoordinatedApplicationQuit('the renderer recovery prompt failed');
      })
      .finally(continueRecoveryQueue);
  };
  const requestRendererRecovery = (
    reason: 'Unresponsive' | 'CloseCoordinationUnavailable' | 'RendererGone'
  ): void => {
    const evaluation = closeSession.recoveryCoordinator.request(
      reason,
      closeSession.readiness.generation);
    if (evaluation.action === 'Start') {
      presentRendererRecoveryRequest(evaluation.request);
      return;
    }
    if (evaluation.action === 'Queue') {
      appendLog(
        `Renderer recovery ${evaluation.request.reason} queued for generation ${
          evaluation.request.rendererGeneration}.`);
    }
  };
  ownedWebContents.on(
    'did-start-navigation',
    (_event, _navigationUrl, isInPlace, isMainFrame) => {
      if (isInPlace || !isMainFrame) {
        return;
      }
      advanceRendererGeneration('the main renderer document started navigating');
    });
  ownedWebContents.on('did-finish-load', () => {
    closeSession.readiness.completeLoad(closeSession.readiness.generation);
  });
  ownedWebContents.on('unresponsive', () => {
    closeSession.readiness.setResponsive(closeSession.readiness.generation, false);
    releaseOwnedCloseRequest('the renderer became unresponsive');
    requestRendererRecovery('Unresponsive');
  });
  ownedWebContents.on('responsive', () => {
    closeSession.readiness.setResponsive(closeSession.readiness.generation, true);
  });
  ownedWebContents.on('render-process-gone', (_event, details) => {
    const rendererGeneration = closeSession.readiness.generation;
    closeSession.readiness.setResponsive(closeSession.readiness.generation, false);
    const closeRequestReleased = releaseOwnedCloseRequest(
      `the renderer process exited (${details.reason})`);
    if (closeRequestReleased) {
      cancelCoordinatedApplicationQuit(
        `the renderer process exited (${details.reason}) during coordinated close`);
    }
    appendLog(`Renderer process exited (${details.reason}); a pending close request was released.`);
    if (details.reason === 'clean-exit') {
      setImmediate(() => {
        if (!ownedWindow.isDestroyed()
            && !ownedWebContents.isDestroyed()
            && rendererGeneration === closeSession.readiness.generation) {
          requestRendererRecovery('RendererGone');
        }
      });
      return;
    }
    requestRendererRecovery('RendererGone');
  });
  ownedWebContents.once('destroyed', () => {
    closeSession.readiness.setResponsive(closeSession.readiness.generation, false);
    closeSession.readiness.setCoordinatorRegistered(
      closeSession.readiness.generation,
      false);
    releaseOwnedCloseRequest('the renderer WebContents was destroyed');
    desktopCloseSessions.delete(ownedWebContentsId);
  });
  ownedWebContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  ownedWindow.on('close', event => {
    event.preventDefault();
    if (!closeCoordinatorIsReady() || ownedWebContents.isDestroyed()) {
      appendLog('Desktop close stayed fail-closed because the renderer is unavailable.');
      requestRendererRecovery('CloseCoordinationUnavailable');
      return;
    }
    const requestBinding = closeSession.binding;
    const requestId = closeSession.coordinator.request(requestBinding, expiredRequestId => {
      appendLog(`Desktop close request ${expiredRequestId} expired; the window remains open for retry.`);
      if (!ownedWindow.isDestroyed() && !ownedWebContents.isDestroyed()) {
        try {
          ownedWebContents.send('desktop:close-request-expired', expiredRequestId);
        } catch (error) {
          appendLog(`Desktop close expiry notification ${expiredRequestId} failed: ${
            error instanceof Error ? error.message : String(error)}`);
        }
      }
      cancelCoordinatedApplicationQuit(
        `desktop close request ${expiredRequestId} acknowledgement expired`);
      requestRendererRecovery('CloseCoordinationUnavailable');
    });
    if (requestId === null) {
      appendLog('Desktop close ignored because a renderer decision is already pending.');
      return;
    }
    appendLog(`Desktop close request ${requestId} sent to the renderer.`);
    try {
      ownedWebContents.send('desktop:close-requested', requestId);
    } catch (error) {
      closeSession.coordinator.release(requestBinding);
      appendLog(`Desktop close request ${requestId} could not reach the renderer: ${
        error instanceof Error ? error.message : String(error)}`);
      cancelCoordinatedApplicationQuit(`desktop close request ${requestId} delivery failed`);
      requestRendererRecovery('CloseCoordinationUnavailable');
    }
  });
  ownedWindow.on('closed', () => {
    releaseOwnedCloseRequest('the window closed');
    desktopCloseSessions.delete(ownedWebContentsId);
    if (mainWindow === ownedWindow) {
      mainWindow = null;
    }
  });

  if (!app.isPackaged) {
    await ownedWindow.loadURL(trustedRendererDocumentUrl);
    return;
  }

  await ownedWindow.loadFile(packagedRendererPath);
}

async function waitForTrustedDevRenderer(rendererUrl: string, nonce: string): Promise<void> {
  const deadline = Date.now() + backendHandshakeTimeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      await verifyTrustedDevRendererServer(rendererUrl, nonce, 1500);
      return;
    } catch (error) {
      lastError = error;
      await new Promise(resolve => setTimeout(resolve, 200));
    }
  }

  throw new Error(
    `Development renderer startup handshake failed: ${lastError instanceof Error
      ? lastError.message
      : String(lastError)}`);
}

ipcMain.handle('desktop:get-config', event => {
  assertTrustedRendererIpcSender(event);
  return createDesktopConfig(requireActiveBackendSession());
});
ipcMain.handle('backend:get-status', async event => {
  assertTrustedRendererIpcSender(event);
  return getBackendStatus();
});
ipcMain.handle('backend:start', async event => {
  assertTrustedRendererIpcSender(event);
  return startBackend();
});
ipcMain.handle('backend:stop', async event => {
  assertTrustedRendererIpcSender(event);
  const child = backendProcess;
  if (child !== null) {
    await terminateBackendProcessTree(child);
    releaseBackendSessionAfterConfirmedExit(child);
    if (backendProcess === child) {
      backendProcess = null;
      backendStartedAtUtc = null;
    }
  }
  const status = await getBackendStatus();
  await notifyBackendStatusChanged(status);
  return status;
});
ipcMain.handle('desktop:set-active-project-file', (event, projectFilePath: string | null) => {
  assertTrustedRendererIpcSender(event);
  activeProjectFilePath = resolveActiveProjectFile(projectFilePath);
});
ipcMain.handle('desktop:get-close-coordinator-binding', event => {
  assertTrustedRendererIpcSender(event);
  const closeSession = desktopCloseSessions.get(event.sender.id);
  if (!closeSession
      || closeSession.window.isDestroyed()
      || closeSession.webContents !== event.sender) {
    throw new Error('Desktop close coordinator has no active renderer binding.');
  }
  return { ...closeSession.binding };
});
ipcMain.on('desktop:close-coordinator-ready', (
  event,
  binding: unknown,
  ready: boolean
) => {
  try {
    assertTrustedRendererIpcSender(event);
  } catch {
    return;
  }
  const closeSession = desktopCloseSessions.get(event.sender.id);
  if (typeof ready !== 'boolean'
      || !closeSession
      || closeSession.window.isDestroyed()
      || closeSession.webContents !== event.sender
      || !desktopCloseBindingsEqual(binding, closeSession.binding)) {
    return;
  }
  if (!ready) {
    closeSession.readiness.setCoordinatorRegistered(
      closeSession.readiness.generation,
      false);
    const requestId = closeSession.coordinator.pendingId;
    if (closeSession.coordinator.release(closeSession.binding)) {
      appendLog(
        `Desktop close request ${requestId} released because the renderer close coordinator became unavailable.`);
      cancelCoordinatedApplicationQuit(
        `renderer close coordinator became unavailable during request ${requestId}`);
    }
    return;
  }
  closeSession.readiness.setCoordinatorRegistered(
    closeSession.readiness.generation,
    true);
  appendLog(
    `Renderer close coordinator registered for generation ${
      closeSession.readiness.generation}.`);
});
ipcMain.on('desktop:close-request-acknowledged', (event, requestId: number) => {
  try {
    assertTrustedRendererIpcSender(event);
  } catch {
    return;
  }
  const closeSession = desktopCloseSessions.get(event.sender.id);
  if (!Number.isSafeInteger(requestId)
      || requestId <= 0
      || !closeSession
      || closeSession.window.isDestroyed()
      || closeSession.webContents !== event.sender) {
    return;
  }
  if (closeSession.coordinator.acknowledge(closeSession.binding, requestId)) {
    appendLog(`Desktop close request ${requestId} acknowledged by the renderer.`);
  }
});
ipcMain.on('desktop:close-response', (event, requestId: number, allowClose: boolean) => {
  try {
    assertTrustedRendererIpcSender(event);
  } catch {
    return;
  }
  const closeSession = desktopCloseSessions.get(event.sender.id);
  const windowToClose = closeSession?.window ?? null;
  if (!Number.isSafeInteger(requestId)
      || requestId <= 0
      || typeof allowClose !== 'boolean'
      || !closeSession
      || !windowToClose
      || windowToClose.isDestroyed()
      || event.sender !== closeSession.webContents
      || !closeSession.coordinator.complete(closeSession.binding, requestId)) {
    return;
  }
  appendLog(`Desktop close request ${requestId} completed with allowClose=${allowClose}.`);
  if (!allowClose) {
    cancelCoordinatedApplicationQuit(
      `the renderer denied desktop close request ${requestId}`);
    return;
  }
  try {
    windowToClose.destroy();
  } catch (error) {
    cancelCoordinatedApplicationQuit(
      `approved desktop close request ${requestId} could not destroy its window`);
    appendLog(`Approved desktop close request ${requestId} failed: ${
      error instanceof Error ? error.message : String(error)}`);
  }
});

async function startBackend(): Promise<BackendStatus> {
  if (backendSessionLifecycle.authenticated !== null) {
    requireActiveBackendSession();
    return getBackendStatus();
  }
  if (backendStartPromise !== null) {
    return backendStartPromise;
  }

  backendStartPromise = launchAndAuthenticateBackend();
  try {
    return await backendStartPromise;
  } finally {
    backendStartPromise = null;
  }
}

async function launchAndAuthenticateBackend(): Promise<BackendStatus> {
  if (backendProcess !== null || backendSessionLifecycle.process !== null) {
    throw new Error('Backend process exists without one authenticated Studio session.');
  }

  const launchProjectFilePath = resolveActiveProjectFile(activeProjectFilePath);
  const nonce = randomBytes(32).toString('base64url');
  const sessionCredentials = deriveBackendSessionCredentials(requireApiCredentials(), nonce);
  const handshakePath = createBackendHandshakePlaceholder();
  let launch: BackendLaunchConfig;
  try {
    launch = createBackendLaunchConfig(
      sessionCredentials,
      handshakePath,
      nonce,
      launchProjectFilePath);
  } catch (error) {
    cleanupBackendHandshakeFile(handshakePath);
    throw error;
  }
  let child: ChildProcessWithoutNullStreams;
  try {
    launch.packagedRuntimeBinding?.verify();
    child = spawn(
      launch.executablePath,
      launch.arguments,
      {
        cwd: launch.workingDirectory,
        env: launch.environment,
        windowsHide: true
      });
  } catch (error) {
    cleanupBackendHandshakeFile(handshakePath);
    try {
      launch.packagedRuntimeBinding?.rollback();
    } catch (rollbackError) {
      throw new AggregateError(
        [error, rollbackError],
        'Backend launch failed and its pending runtime state could not be rolled back.');
    }
    throw error;
  }
  backendSessionLifecycle.beginPending({
    process: child,
    standardToken: sessionCredentials.standardToken,
    safetyToken: sessionCredentials.safetyToken,
    nonce,
    handshakePath
  });
  let spawnConfirmed = false;
  child.once('spawn', () => {
    spawnConfirmed = true;
  });
  child.on('error', error => {
    const disposition = classifyBackendProcessError(child, spawnConfirmed);
    if (disposition === 'ReleaseFailedSpawn') {
      releaseBackendSessionAfterFailedSpawn(child);
    } else if (disposition === 'ReleaseExitedProcess') {
      releaseBackendSessionAfterConfirmedExit(child);
    }
    if (disposition !== 'RetainLiveProcess') {
      if (backendProcess === child) {
        backendProcess = null;
        backendStartedAtUtc = null;
        lastExitCode = -1;
      }
    }
    appendLog(disposition === 'RetainLiveProcess'
      ? `OpenLineOps.Api process control failed while the exact process remains active; retaining its identity and credentials: ${error.message}`
      : `OpenLineOps.Api failed to start or exited with an operating-system error: ${error.message}`);
    void notifyBackendStatusChanged();
  });
  child.on('exit', code => {
    releaseBackendSessionAfterConfirmedExit(child);
    if (backendProcess === child) {
      backendProcess = null;
      backendStartedAtUtc = null;
      lastExitCode = code;
    }
    appendLog(`OpenLineOps.Api exited with code ${code ?? 'unknown'}.`);
    void notifyBackendStatusChanged();
  });
  if (child.pid === undefined) {
    releaseBackendSessionAfterFailedSpawn(child);
    launch.packagedRuntimeBinding?.rollback();
    throw new Error('OpenLineOps.Api did not receive an operating-system process identity.');
  }

  backendProcess = child;
  backendStartedAtUtc = null;
  lastExitCode = null;
  child.stdout.on('data', chunk => appendLog(chunk.toString()));
  child.stderr.on('data', chunk => appendLog(chunk.toString()));

  try {
    const document = await waitForBackendHandshakeDocument(child, handshakePath);
    backendStartedAtUtc = new Date(
      document.StartedAtUnixMilliseconds).toISOString();
    const apiBaseUrl = canonicalizeLocalBackendBaseUrl(document.Origin);
    await verifyBackendProcessHandshakeServer(
      apiBaseUrl,
      nonce,
      () => assertBackendProcessAlive(child));
    assertBackendProcessAlive(child);
    backendSessionLifecycle.authenticate(child, apiBaseUrl);
    launch.packagedRuntimeBinding?.verify();
    launch.packagedRuntimeBinding?.commit();
    const status = await getBackendStatus();
    await notifyBackendStatusChanged(status);
    return status;
  } catch (error) {
    try {
      await terminateBackendProcessTree(child);
    } catch (terminationError) {
      throw new AggregateError(
        [error, terminationError],
        'Backend authentication failed and its process tree could not be confirmed stopped.');
    }
    let rollbackError: unknown;
    try {
      launch.packagedRuntimeBinding?.rollback();
    } catch (candidate) {
      rollbackError = candidate;
    }
    releaseBackendSessionAfterConfirmedExit(child);
    if (backendProcess === child) {
      backendProcess = null;
      backendStartedAtUtc = null;
      lastExitCode = -1;
    }
    if (rollbackError !== undefined) {
      throw new AggregateError(
        [error, rollbackError],
        'Backend startup failed and its pending runtime state could not be rolled back.');
    }
    throw error;
  } finally {
    cleanupBackendHandshakeFile(handshakePath);
  }
}

function createBackendHandshakePlaceholder(): string {
  const securityDirectory = path.join(app.getPath('userData'), 'data', 'security');
  mkdirSync(securityDirectory, { recursive: true, mode: 0o700 });
  protectCredentialPath(securityDirectory, true);
  const handshakePath = path.join(
    securityDirectory,
    `backend-handshake-${randomBytes(32).toString('hex')}.json`);
  writeFileSync(handshakePath, '', { encoding: 'utf8', flag: 'wx', mode: 0o600 });
  protectCredentialPath(handshakePath, false);
  return handshakePath;
}

async function waitForBackendHandshakeDocument(
  child: ChildProcessWithoutNullStreams,
  handshakePath: string
): Promise<BackendHandshakeDocument> {
  const deadline = Date.now() + backendHandshakeTimeoutMs;
  while (Date.now() < deadline) {
    assertBackendProcessAlive(child);
    const metadata = lstatSync(handshakePath);
    if (!metadata.isFile()
        || metadata.isSymbolicLink()
        || metadata.nlink !== 1
        || metadata.size > 1024) {
      throw new Error('Backend handshake path changed to an unsafe filesystem object.');
    }
    if (metadata.size > 0) {
      let parsed: unknown;
      try {
        parsed = JSON.parse(readFileSync(handshakePath, 'utf8'));
      } catch (error) {
        if (isIncompleteBackendHandshakeRead(error)) {
          await new Promise(resolve => setTimeout(resolve, 25));
          continue;
        }
        throw error;
      }
      verifyCredentialPathProtection(handshakePath, false);
      if (!parsed
          || typeof parsed !== 'object'
          || Array.isArray(parsed)
          || Object.keys(parsed).sort().join(',')
            !== 'Origin,ProcessId,StartedAtUnixMilliseconds') {
        throw new Error('Backend handshake document has unexpected fields.');
      }
      const document = parsed as Partial<BackendHandshakeDocument>;
      const processId = document.ProcessId;
      if (typeof processId !== 'number'
          || !Number.isSafeInteger(processId)
          || processId !== child.pid
          || typeof document.Origin !== 'string'
          || typeof document.StartedAtUnixMilliseconds !== 'number'
          || !Number.isSafeInteger(document.StartedAtUnixMilliseconds)
          || document.StartedAtUnixMilliseconds <= 0) {
        throw new Error('Backend handshake document does not identify the spawned API process.');
      }
      return {
        ProcessId: processId,
        Origin: document.Origin,
        StartedAtUnixMilliseconds: document.StartedAtUnixMilliseconds
      };
    }
    await new Promise(resolve => setTimeout(resolve, 50));
  }

  throw new Error('Timed out waiting for the spawned API process handshake document.');
}

function isIncompleteBackendHandshakeRead(error: unknown): boolean {
  if (error instanceof SyntaxError) {
    return true;
  }
  if (!error || typeof error !== 'object' || !('code' in error)) {
    return false;
  }
  return ['EACCES', 'EBUSY', 'EPERM', 'UNKNOWN'].includes(String(error.code));
}

function assertBackendProcessAlive(child: ChildProcessWithoutNullStreams): void {
  if (backendProcess !== child
      || child.pid === undefined
      || child.exitCode !== null
      || child.signalCode !== null) {
    throw new Error('Spawned API process exited before its authenticated session was usable.');
  }
}

function requireActiveBackendSession(): ActiveBackendSession {
  const session = backendSessionLifecycle.authenticated;
  if (session === null) {
    throw new Error('No authenticated local API process session is active.');
  }
  try {
    assertBackendProcessAlive(session.process);
  } catch (error) {
    if (hasDesktopBackendProcessExited(session.process)) {
      releaseBackendSessionAfterConfirmedExit(session.process);
    }
    throw error;
  }
  return session;
}

function releaseBackendSessionAfterFailedSpawn(
  child: ChildProcessWithoutNullStreams
): void {
  const releasedSession = backendSessionLifecycle.releaseFailedSpawn(child);
  if (releasedSession === null) {
    return;
  }
  pendingExternalProgramDirectories.clear();
  cleanupBackendHandshakeForSession(releasedSession.handshakePath);
}

function releaseBackendSessionAfterConfirmedExit(
  child: ChildProcessWithoutNullStreams
): void {
  const releasedSession = backendSessionLifecycle.releaseConfirmedExit(
    child,
    hasDesktopBackendProcessExited(child));
  if (releasedSession === null) {
    return;
  }
  pendingExternalProgramDirectories.clear();
  cleanupBackendHandshakeForSession(releasedSession.handshakePath);
}

function cleanupBackendHandshakeForSession(handshakePath: string): void {
  cleanupBackendHandshakeFile(handshakePath);
}

function cleanupBackendHandshakeFile(handshakePath: string): void {
  const securityDirectory = path.join(app.getPath('userData'), 'data', 'security');
  if (path.dirname(handshakePath) !== securityDirectory) {
    throw new Error('Refusing to remove a handshake file outside the Studio security directory.');
  }
  rmSync(handshakePath, { force: true, recursive: false });
}

async function terminateBackendProcessTree(
  child: ChildProcessWithoutNullStreams
): Promise<void> {
  if (child.pid === undefined || hasDesktopBackendProcessExited(child)) {
    return;
  }

  // ChildProcess.kill targets the process handle owned by this exact spawn,
  // avoiding a stale/reused PID. The desktop-bound API owns a kill-on-close
  // Windows Job, so terminating that root also terminates its complete tree.
  const terminationAccepted = child.kill();
  try {
    await waitForBackendProcessExit(child, backendProcessTerminationTimeoutMs);
  } catch (error) {
    throw new Error(
      terminationAccepted
        ? 'Backend root process did not exit after exact-handle termination.'
        : 'Backend process rejected exact-handle termination and remained alive.',
      { cause: error });
  }
}

async function waitForBackendProcessExit(
  child: ChildProcessWithoutNullStreams,
  timeoutMs: number
): Promise<void> {
  if (hasDesktopBackendProcessExited(child)) {
    return;
  }
  await new Promise<void>((resolve, reject) => {
    const onExit = (): void => {
      clearTimeout(timeout);
      child.off('error', onError);
      resolve();
    };
    const onError = (error: Error): void => {
      clearTimeout(timeout);
      child.off('exit', onExit);
      reject(error);
    };
    const timeout = setTimeout(() => {
      child.off('exit', onExit);
      child.off('error', onError);
      reject(new Error(`Backend process did not exit within ${timeoutMs} ms.`));
    }, timeoutMs);
    child.once('exit', onExit);
    child.once('error', onError);
    if (hasDesktopBackendProcessExited(child)) {
      onExit();
    }
  });
}
ipcMain.handle('desktop:select-directory', async (
  event,
  options?: SelectDirectoryOptions
): Promise<SelectDirectoryResult> => {
  assertTrustedRendererIpcSender(event);
  const properties: Array<'openDirectory' | 'createDirectory'> = ['openDirectory'];
  if (options?.createDirectory) {
    properties.push('createDirectory');
  }

  const dialogOptions = {
    title: options?.title ?? 'Select project folder',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Select',
    properties
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-project-file', async (
  event,
  options?: SelectProjectFileOptions
): Promise<SelectDirectoryResult> => {
  assertTrustedRendererIpcSender(event);
  const dialogOptions = {
    title: options?.title ?? 'Open OpenLineOps project',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Open Project',
    properties: ['openFile'] as Array<'openFile'>,
    filters: [
      { name: 'OpenLineOps Projects', extensions: ['oloproj'] }
    ]
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-application-project-file', async (
  event,
  options?: SelectProjectFileOptions
): Promise<SelectDirectoryResult> => {
  assertTrustedRendererIpcSender(event);
  const dialogOptions = {
    title: options?.title ?? 'Add existing OpenLineOps Application',
    defaultPath: options?.defaultPath,
    buttonLabel: options?.buttonLabel ?? 'Add Application',
    properties: ['openFile'] as Array<'openFile'>,
    filters: [
      { name: 'OpenLineOps Applications', extensions: ['oloapp'] }
    ]
  };
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, dialogOptions)
    : await dialog.showOpenDialog(dialogOptions);

  return {
    canceled: result.canceled,
    path: result.filePaths[0] ?? null
  };
});
ipcMain.handle('desktop:select-external-program-directory', async (
  event,
  projectId: string,
  applicationId: string,
  resourceId: string
): Promise<ExternalProgramDirectorySelectionResult> => {
  assertTrustedRendererIpcSender(event);
  return selectExternalProgramDirectory(projectId, applicationId, resourceId);
});
ipcMain.handle('desktop:release-external-program-directory-selection', (
  event,
  selectionId: string
): void => {
  assertTrustedRendererIpcSender(event);
  if (!/^[a-f0-9]{64}$/u.test(selectionId)) {
    throw new Error('External program directory selection identity is invalid.');
  }
  pendingExternalProgramDirectories.delete(selectionId);
});
ipcMain.handle('api:request', async (event, requestPath: string, options?: ApiRequestOptions) => {
  assertTrustedRendererIpcSender(event);
  return apiRequest(requestPath, options);
});
ipcMain.handle('api:import-external-program-directory', async (
  event,
  projectId: string,
  applicationId: string,
  definition: unknown,
  selectionId: string,
  write?: EditorDocumentWriteOptions
) => {
  assertTrustedRendererIpcSender(event);
  return importExternalProgramDirectory(
    projectId,
    applicationId,
    definition,
    selectionId,
    write);
});
ipcMain.handle('api:import-application-extension', async (
  event,
  projectId: string,
  applicationId: string
) => {
  assertTrustedRendererIpcSender(event);
  return selectAndImportApplicationExtension(projectId, applicationId);
});
ipcMain.handle('trace:save-artifact', async (
  event,
  options: TraceArtifactSaveOptions
) => {
  assertTrustedRendererIpcSender(event);
  const session = requireActiveBackendSession();
  return saveTraceArtifact(
    session.apiBaseUrl,
    session.standardToken,
    options,
    () => assertSameActiveBackendSession(session));
});

if (ownsPrimaryInstance) {
  app.on('second-instance', () => {
    if (applicationQuitCoordinator.phase === 'ShutdownFailed') {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.destroy();
      }
      app.quit();
      return;
    }
    if (!mainWindow) {
      return;
    }
    if (mainWindow.isMinimized()) {
      mainWindow.restore();
    }
    mainWindow.show();
    mainWindow.focus();
  });

  app.whenReady().then(async () => {
    await startBackend();
    await createWindow();

    app.on('activate', async () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        await createWindow();
      }
    });
  }).catch(async error => {
    const message = `Studio startup failed closed: ${error instanceof Error
      ? error.message
      : String(error)}`;
    appendLog(message);
    console.error(message);
    if (recentLogs.length > 1) {
      console.error(recentLogs.join('\n'));
    }
    const child = backendProcess;
    try {
      await stopBackendAndReleaseState(child);
    } catch (terminationError) {
      const terminationDetail = terminationError instanceof Error
        ? terminationError.message
        : String(terminationError);
      appendLog(
        `Backend termination failed during Studio startup shutdown: ${terminationDetail}`);
      try {
        dialog.showErrorBox(
          'OpenLineOps startup failed safely',
          `${message}\n\nThe local automation backend could not be confirmed stopped. OpenLineOps will retry through its guarded shutdown flow.\n\n${
            terminationDetail}`);
      } catch (dialogError) {
        appendLog(`Studio startup shutdown error dialog failed: ${
          dialogError instanceof Error ? dialogError.message : String(dialogError)}`);
      }
      if (mainWindow && !mainWindow.isDestroyed()) {
        try {
          mainWindow.destroy();
        } catch (windowError) {
          appendLog(`Studio startup window cleanup failed: ${
            windowError instanceof Error ? windowError.message : String(windowError)}`);
        }
      }
      app.quit();
      return;
    }
    app.exit(1);
  });
}

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin'
      || applicationQuitCoordinator.phase === 'WaitingForWindowClose') {
    app.quit();
  }
});

app.on('before-quit', event => {
  const windowToCoordinate = mainWindow && !mainWindow.isDestroyed()
    ? mainWindow
    : null;
  const action = applicationQuitCoordinator.handleBeforeQuit(
    windowToCoordinate !== null);
  if (action === 'AllowFinalShutdown') {
    return;
  }

  event.preventDefault();
  if (action === 'WaitForCoordinatedWindowClose'
      || action === 'WaitForFinalShutdown') {
    return;
  }
  if (action === 'BeginCoordinatedWindowClose') {
    try {
      windowToCoordinate?.close();
    } catch (error) {
      cancelCoordinatedApplicationQuit('the application quit close request failed');
      appendLog(`Application quit could not request coordinated window close: ${
        error instanceof Error ? error.message : String(error)}`);
    }
    return;
  }

  beginFinalApplicationShutdown();
});

function cancelCoordinatedApplicationQuit(reason: string): void {
  if (applicationQuitCoordinator.cancelCoordinatedWindowClose()) {
    appendLog(`Application quit coordination canceled because ${reason}.`);
  }
}

function beginFinalApplicationShutdown(): void {
  if (applicationShutdownPromise !== null) {
    return;
  }

  applicationQuitting = true;
  const child = backendProcess;
  const shutdownAttempt = (async () => {
    await stopBackendAndReleaseState(child);
    if (!applicationQuitCoordinator.completeFinalShutdown()) {
      throw new Error(
        `Application shutdown completion arrived in unexpected phase ${
          applicationQuitCoordinator.phase}.`);
    }
  })();
  applicationShutdownPromise = shutdownAttempt;
  void shutdownAttempt.then(
    () => {
      if (applicationShutdownPromise === shutdownAttempt) {
        applicationShutdownPromise = null;
      }
      app.quit();
    },
    error => {
      if (applicationShutdownPromise === shutdownAttempt) {
        applicationShutdownPromise = null;
      }
      applicationQuitting = false;
      const detail = error instanceof Error ? error.message : String(error);
      appendLog(`Backend termination failed during Studio shutdown: ${detail}`);
      if (!applicationQuitCoordinator.failFinalShutdown()) {
        appendLog(
          `Application shutdown failure arrived in unexpected phase ${
            applicationQuitCoordinator.phase}.`);
        return;
      }
      void presentApplicationShutdownFailure(detail);
    });
}

async function presentApplicationShutdownFailure(detail: string): Promise<void> {
  try {
    const decision = await dialog.showMessageBox({
      type: 'error',
      title: 'OpenLineOps could not shut down safely',
      message: 'The local automation backend is still running.',
      detail: `${detail}\n\nStudio kept the backend identity and credentials so shutdown can be retried safely.`,
      buttons: ['Retry Shutdown', 'Reopen Studio'],
      defaultId: 0,
      cancelId: 1,
      noLink: true
    });
    if (decision.response === 0) {
      app.quit();
      return;
    }

    if (applicationQuitCoordinator.phase !== 'ShutdownFailed') {
      return;
    }
    await createWindow();
    if (!applicationQuitCoordinator.resumeAfterFailedShutdown()) {
      throw new Error(
        `Application shutdown recovery arrived in unexpected phase ${
          applicationQuitCoordinator.phase}.`);
    }
  } catch (error) {
    const recoveryDetail = error instanceof Error ? error.message : String(error);
    appendLog(`Application shutdown recovery failed: ${recoveryDetail}`);
    try {
      dialog.showErrorBox(
        'OpenLineOps remains running',
        `The local automation backend could not be stopped and Studio recovery failed.\n\n${
          recoveryDetail}\n\nLaunch OpenLineOps again to retry shutdown.`);
    } catch (dialogError) {
      appendLog(`Application shutdown recovery error dialog failed: ${
        dialogError instanceof Error ? dialogError.message : String(dialogError)}`);
    }
  }
}

async function stopBackendAndReleaseState(
  child: ChildProcessWithoutNullStreams | null
): Promise<void> {
  await stopDesktopBackendForApplicationShutdown(
    child,
    terminateBackendProcessTree,
    stoppedProcess => {
      if (backendProcess !== null && backendProcess !== stoppedProcess) {
        throw new Error(
          'Backend process identity changed during Studio shutdown.');
      }
      if (stoppedProcess !== null) {
        releaseBackendSessionAfterConfirmedExit(stoppedProcess);
      }
      backendProcess = null;
      backendStartedAtUtc = null;
    });
}

async function getBackendStatus(): Promise<BackendStatus> {
  const health = await probeHealth();

  return {
    isRunning: backendProcess !== null,
    pid: backendProcess?.pid ?? null,
    health,
    apiBaseUrl: backendSessionLifecycle.authenticated?.apiBaseUrl ?? null,
    startedAtUtc: backendStartedAtUtc,
    startedAtUnixMilliseconds: backendStartedAtUtc === null
      ? null
      : Date.parse(backendStartedAtUtc),
    lastExitCode,
    recentLogs: recentLogs.slice(-80)
  };
}

async function notifyBackendStatusChanged(status?: BackendStatus): Promise<void> {
  const sequence = ++backendStatusSequence;
  try {
    const currentStatus = status ?? await getBackendStatus();
    if (mainWindow && !mainWindow.isDestroyed() && !mainWindow.webContents.isDestroyed()) {
      mainWindow.webContents.send('backend:status-changed', {
        sequence,
        status: currentStatus
      });
    }
  } catch (error) {
    appendLog(`Backend status notification failed: ${error instanceof Error
      ? error.message
      : String(error)}`);
  }
}

async function apiRequest<T = unknown>(
  requestPath: string,
  options: ApiRequestOptions = {}
): Promise<ApiResponse<T>> {
  const headers = new Headers(options.headers ?? {});
  let body: BodyInit | undefined;

  if (options.body !== undefined) {
    headers.set('content-type', headers.get('content-type') ?? 'application/json');
    body = JSON.stringify(options.body);
  }

  let response: Response;
  try {
    const session = requireActiveBackendSession();
    response = await fetchAuthenticatedBackend({
      apiBaseUrl: session.apiBaseUrl,
      requestPath,
      standardToken: session.standardToken,
      safetyToken: session.safetyToken,
      credentialMode: 'route',
      assertSessionActive: () => assertSameActiveBackendSession(session),
      init: {
        method: options.method ?? (body ? 'POST' : 'GET'),
        headers,
        body
      }
    });
  } catch (error) {
    return {
      ok: false,
      status: 0,
      body: null,
      text: error instanceof Error ? error.message : String(error)
    };
  }

  const text = await response.text();

  return {
    ok: response.ok,
    status: response.status,
    body: parseBody<T>(text),
    text
  };
}

async function selectExternalProgramDirectory(
  projectId: string,
  applicationId: string,
  resourceId: string
): Promise<ExternalProgramDirectorySelectionResult> {
  assertCanonicalApplicationScopeValue(projectId, 'Project');
  assertCanonicalApplicationScopeValue(applicationId, 'Application');
  assertCanonicalExternalProgramResourceId(resourceId);
  const initialSession = requireActiveBackendSession();
  const automatedDirectoryPath = process.env.OPENLINEOPS_E2E_EXTERNAL_PROGRAM_DIRECTORY_PATH;
  let selectedPath: string | null;
  if (process.env.OPENLINEOPS_E2E_ALLOW_EXTERNAL_PROGRAM_DIRECTORY_DIALOG_BYPASS === '1'
      && automatedDirectoryPath) {
    selectedPath = path.resolve(automatedDirectoryPath);
  } else {
    const dialogOptions = {
      title: 'Select external program directory',
      buttonLabel: 'Use Program Directory',
      properties: ['openDirectory'] as Array<'openDirectory'>
    };
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, dialogOptions)
      : await dialog.showOpenDialog(dialogOptions);
    if (result.canceled || result.filePaths.length === 0) {
      return {
        canceled: true,
        selectionId: null,
        directoryName: null,
        totalBytes: null,
        files: []
      };
    }
    if (result.filePaths.length !== 1) {
      throw new Error('External program import accepts exactly one directory.');
    }
    selectedPath = result.filePaths[0];
  }

  const identity = await inspectExternalProgramDirectory(selectedPath);
  assertSameActiveBackendSession(initialSession);
  pruneExpiredExternalProgramDirectories();
  for (const [existingSelectionId, pending] of pendingExternalProgramDirectories) {
    if (!pending.inFlight
        && pending.backendSessionNonce === initialSession.nonce
        && pending.projectId === projectId
        && pending.applicationId === applicationId
        && pending.resourceId === resourceId) {
      pendingExternalProgramDirectories.delete(existingSelectionId);
    }
  }
  if (pendingExternalProgramDirectories.size >= maximumPendingExternalProgramDirectories) {
    throw new Error(
      'Too many external program directories are pending; save or close an existing editor before selecting another.');
  }
  const selectionId = randomBytes(32).toString('hex');
  pendingExternalProgramDirectories.set(selectionId, {
    identity,
    backendSessionNonce: initialSession.nonce,
    projectId,
    applicationId,
    resourceId,
    inFlight: false,
    expiresAtMilliseconds: Date.now() + externalProgramDirectorySelectionLifetimeMilliseconds
  });
  return {
    canceled: false,
    selectionId,
    directoryName: identity.directoryName,
    totalBytes: identity.totalBytes,
    files: identity.files.map(file => ({
      relativePath: file.relativePath,
      resourceRelativePath: file.resourceRelativePath,
      sizeBytes: file.sizeBytes,
      sha256: file.sha256
    }))
  };
}

async function importExternalProgramDirectory<T = unknown>(
  projectId: string,
  applicationId: string,
  definition: unknown,
  selectionId: string,
  write?: EditorDocumentWriteOptions
): Promise<ApiResponse<T>> {
  assertCanonicalApplicationScopeValue(projectId, 'Project');
  assertCanonicalApplicationScopeValue(applicationId, 'Application');
  if (!/^[a-f0-9]{64}$/u.test(selectionId)) {
    throw new Error('External program directory selection identity is invalid.');
  }
  const requestHeaders = editorDocumentWriteHeaders(write);
  const resourceId = externalProgramDefinitionResourceId(definition);
  const initialSession = requireActiveBackendSession();
  pruneExpiredExternalProgramDirectories();
  const pending = pendingExternalProgramDirectories.get(selectionId);
  if (!pending
      || pending.backendSessionNonce !== initialSession.nonce
      || pending.projectId !== projectId
      || pending.applicationId !== applicationId
      || pending.resourceId !== resourceId
      || pending.expiresAtMilliseconds <= Date.now()) {
    pendingExternalProgramDirectories.delete(selectionId);
    throw new Error('The external program directory selection expired; select the directory again.');
  }
  if (pending.inFlight) {
    throw new Error('The external program directory selection is already being imported.');
  }
  pending.inFlight = true;
  try {
    await assertExternalProgramDirectoryUnchanged(pending.identity);
    const form = new FormData();
    form.set('definition', JSON.stringify(definition));
    const manifest = [];
    for (const [index, file] of pending.identity.files.entries()) {
      const fieldName = `file-${index + 1}`;
      manifest.push({
        fieldName,
        resourceRelativePath: file.resourceRelativePath,
        sizeBytes: file.sizeBytes,
        sha256: file.sha256
      });
      form.append(fieldName, await openAsBlob(file.path), path.basename(file.path));
    }
    form.set('uploadManifest', JSON.stringify(manifest));
    await assertExternalProgramDirectoryUnchanged(pending.identity);
    assertSameActiveBackendSession(initialSession);
    if (pendingExternalProgramDirectories.get(selectionId) !== pending) {
      throw new Error('The external program directory selection was released before import dispatch.');
    }

    const requestPath = `/api/automation-projects/${encodeURIComponent(projectId)}`
      + `/applications/${encodeURIComponent(applicationId)}/external-programs/directory-import`;
    resolveCanonicalBackendApiUrl(initialSession.apiBaseUrl, requestPath);
    const session = requireActiveBackendSession();
    if (session !== initialSession) {
      throw new Error('Backend session changed while external program files were prepared.');
    }
    const response = await fetchAuthenticatedBackend({
      apiBaseUrl: session.apiBaseUrl,
      requestPath,
      standardToken: session.standardToken,
      safetyToken: session.safetyToken,
      credentialMode: 'standard',
      assertSessionActive: () => assertSameActiveBackendSession(session),
      init: { method: 'POST', body: form, headers: requestHeaders }
    });
    const text = await response.text();
    if (response.ok) {
      pendingExternalProgramDirectories.delete(selectionId);
    } else if (pendingExternalProgramDirectories.get(selectionId) === pending) {
      pending.inFlight = false;
    }
    return {
      ok: response.ok,
      status: response.status,
      body: parseBody<T>(text),
      text
    };
  } catch (error) {
    if (pendingExternalProgramDirectories.get(selectionId) === pending) {
      pending.inFlight = false;
    }
    return {
      ok: false,
      status: 0,
      body: null,
      text: error instanceof Error ? error.message : String(error)
    };
  }
}

function externalProgramDefinitionResourceId(definition: unknown): string {
  if (definition === null
      || typeof definition !== 'object'
      || Array.isArray(definition)
      || !('resourceId' in definition)) {
    throw new Error('External program directory import definition has no resource identity.');
  }
  const resourceId = definition.resourceId;
  assertCanonicalExternalProgramResourceId(resourceId as string);
  return resourceId as string;
}

function pruneExpiredExternalProgramDirectories(): void {
  const now = Date.now();
  const activeSessionNonce = backendSessionLifecycle.authenticated?.nonce;
  for (const [selectionId, pending] of pendingExternalProgramDirectories) {
    if (pending.expiresAtMilliseconds <= now
        || pending.backendSessionNonce !== activeSessionNonce) {
      pendingExternalProgramDirectories.delete(selectionId);
    }
  }
}

function editorDocumentWriteHeaders(write?: EditorDocumentWriteOptions): Record<string, string> {
  if (write === undefined) {
    return {};
  }
  if (!/^[a-f0-9]{64}$/u.test(write.revision)
      || write.force !== undefined && typeof write.force !== 'boolean') {
    throw new Error('External program directory import revision is invalid.');
  }
  return write.force
    ? {
      'If-Match': '*',
      'X-OpenLineOps-Conflict-Resolution': 'overwrite'
    }
    : { 'If-Match': `"${write.revision}"` };
}

async function selectAndImportApplicationExtension<T = unknown>(
  projectId: string,
  applicationId: string
): Promise<ApplicationExtensionImportResult<T>> {
  assertCanonicalApplicationScopeValue(projectId, 'Project');
  assertCanonicalApplicationScopeValue(applicationId, 'Application');
  const initialSession = requireActiveBackendSession();
  const automatedArchivePath = process.env.OPENLINEOPS_E2E_EXTENSION_ARCHIVE_PATH;
  let selectedPath: string | null;
  if (process.env.OPENLINEOPS_E2E_ALLOW_EXTENSION_DIALOG_BYPASS === '1'
      && automatedArchivePath) {
    selectedPath = path.resolve(automatedArchivePath);
  } else {
    const dialogOptions = {
      title: `Import extension into ${applicationId}`,
      buttonLabel: 'Import Extension',
      properties: ['openFile'] as Array<'openFile'>,
      filters: [{ name: 'OpenLineOps Extension Packages', extensions: ['zip'] }]
    };
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, dialogOptions)
      : await dialog.showOpenDialog(dialogOptions);
    if (result.canceled || result.filePaths.length === 0) {
      return {
        canceled: true,
        portableId: null,
        fileName: null,
        sizeBytes: null,
        response: null
      };
    }
    if (result.filePaths.length !== 1) {
      throw new Error('Application extension import accepts exactly one ZIP archive.');
    }
    selectedPath = result.filePaths[0];
  }

  assertSameActiveBackendSession(initialSession);
  const archive = inspectApplicationExtensionArchive(selectedPath);
  const contentSha256 = await calculateFileSha256(archive.path);
  assertApplicationExtensionArchiveUnchanged(archive);
  const portableId = deriveApplicationExtensionPortableId(archive.fileName, contentSha256);
  const form = new FormData();
  form.set('portableId', portableId);
  form.set('package', await openAsBlob(archive.path), archive.fileName);
  assertApplicationExtensionArchiveUnchanged(archive);
  assertSameActiveBackendSession(initialSession);

  const requestPath = `/api/automation-projects/${encodeURIComponent(projectId)}`
    + `/applications/${encodeURIComponent(applicationId)}/extensions/import`;
  try {
    const response = await fetchAuthenticatedBackend({
      apiBaseUrl: initialSession.apiBaseUrl,
      requestPath,
      standardToken: initialSession.standardToken,
      safetyToken: initialSession.safetyToken,
      credentialMode: 'standard',
      assertSessionActive: () => assertSameActiveBackendSession(initialSession),
      init: { method: 'POST', body: form }
    });
    assertSameActiveBackendSession(initialSession);
    const text = await response.text();
    assertSameActiveBackendSession(initialSession);
    return {
      canceled: false,
      portableId,
      fileName: archive.fileName,
      sizeBytes: archive.sizeBytes,
      response: {
        ok: response.ok,
        status: response.status,
        body: parseBody<T>(text),
        text
      }
    };
  } catch (error) {
    return {
      canceled: false,
      portableId,
      fileName: archive.fileName,
      sizeBytes: archive.sizeBytes,
      response: {
        ok: false,
        status: 0,
        body: null,
        text: error instanceof Error ? error.message : String(error)
      }
    };
  }
}

async function calculateFileSha256(filePath: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) {
    hash.update(chunk);
  }
  return hash.digest('hex');
}

async function probeHealth(): Promise<BackendStatus['health']> {
  try {
    const session = requireActiveBackendSession();
    const response = await fetch(`${session.apiBaseUrl}/health/live`, {
      redirect: 'manual',
      signal: AbortSignal.timeout(1000)
    });
    requireActiveBackendSession();
    return response.status === 204 ? 'Healthy' : 'Unreachable';
  } catch {
    return 'Unreachable';
  }
}

function appendLog(value: string): void {
  for (const line of value.split(/\r?\n/)) {
    const normalized = line.trim();
    if (normalized.length > 0) {
      recentLogs.push(normalized);
    }
  }

  if (recentLogs.length > 200) {
    recentLogs.splice(0, recentLogs.length - 200);
  }
}

function parseBody<T>(text: string): T | null {
  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

function assertTrustedRendererIpcSender(event: IpcMainInvokeEvent): void {
  const senderFrameUrl = event.senderFrame?.url ?? '';
  const senderDocumentUrl = event.sender.getURL();
  if (!mainWindow
      || event.sender !== mainWindow.webContents
      || event.senderFrame !== event.sender.mainFrame
      || trustedRendererDocumentUrl === null
      || !isTrustedRendererIpcContext(
        senderFrameUrl,
        senderDocumentUrl,
        trustedRendererDocumentUrl)) {
    throw new Error('Privileged desktop IPC is restricted to the trusted renderer document.');
  }
}

function assertCanonicalApplicationScopeValue(value: string, label: string): void {
  if (typeof value !== 'string'
      || value.length === 0
      || value.length > 256
      || value !== value.trim()
      || value.includes('/')
      || value.includes('\\')
      || value.includes('?')
      || value.includes('#')
      || [...value].some(character => character.charCodeAt(0) < 32)) {
    throw new Error(`${label} identity is not one canonical API path segment.`);
  }
}

function assertCanonicalExternalProgramResourceId(value: string): void {
  if (typeof value !== 'string'
      || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$/u.test(value)) {
    throw new Error('External program resource identity must start with an ASCII letter or digit and use at most 96 letters, digits, dot, dash, or underscore characters.');
  }
}

function assertSameActiveBackendSession(expected: ActiveBackendSession): void {
  if (requireActiveBackendSession() !== expected) {
    throw new Error('Authenticated local API process session changed before request dispatch.');
  }
}
