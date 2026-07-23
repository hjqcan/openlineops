import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  FileArchive,
  FileCode2,
  FlaskConical,
  Hash,
  Package,
  PackageOpen,
  RefreshCw,
  ShieldCheck,
  Trash2,
  Upload,
  XCircle
} from 'lucide-react';
import type {
  ApplicationExtensionPackageResponse,
  AutomationProjectWorkspaceResponse,
  ExternalProgramTrialResponse,
  PluginCommandDefinitionResponse,
  SaveExternalProgramResourceRequest
} from './contracts';
import {
  importApplicationExtension,
  listApplicationExtensions,
  removeApplicationExtension,
  trialExternalProgramDefinition,
  validateApplicationExtensions,
  type ProjectApplicationApiScope
} from './api';

interface PluginsWorkbenchProps {
  isBackendHealthy: boolean;
  activeWorkspace: AutomationProjectWorkspaceResponse | null;
  activeApplicationId: string | null;
  onMessage(message: string): void;
}

interface ExtensionProblem {
  code: string;
  message: string;
  severity: 'error' | 'warning';
}

interface ProviderCommandChoice {
  key: string;
  providerKind: 'PluginCommand' | 'ProcessCommandProvider';
  command: PluginCommandDefinitionResponse;
}

export function PluginsWorkbench({
  isBackendHealthy,
  activeWorkspace,
  activeApplicationId,
  onMessage
}: PluginsWorkbenchProps): React.ReactElement {
  const scope = useMemo<ProjectApplicationApiScope | null>(() => {
    if (!activeWorkspace || !activeApplicationId) {
      return null;
    }
    return {
      projectId: activeWorkspace.project.projectId,
      applicationId: activeApplicationId
    };
  }, [activeApplicationId, activeWorkspace]);
  const application = activeWorkspace?.project.applications.find(
    candidate => candidate.applicationId === activeApplicationId) ?? null;
  const [packages, setPackages] = useState<ApplicationExtensionPackageResponse[]>([]);
  const [selectedPluginId, setSelectedPluginId] = useState('');
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [operationalProblems, setOperationalProblems] = useState<ExtensionProblem[]>([]);
  const [removeConfirmation, setRemoveConfirmation] = useState<string | null>(null);
  const [selectedCommandKey, setSelectedCommandKey] = useState('');
  const [providerKey, setProviderKey] = useState('');
  const [trialIdentity, setTrialIdentity] = useState('board-001');
  const [trialModel, setTrialModel] = useState('sample-board');
  const [outcomePath, setOutcomePath] = useState('$.deviceInstanceId');
  const [passedToken, setPassedToken] = useState('protocol-trial-device');
  const [failedToken, setFailedToken] = useState('Failed');
  const [abortedToken, setAbortedToken] = useState('Aborted');
  const [trialResult, setTrialResult] = useState<ExternalProgramTrialResponse | null>(null);

  const selectedPackage = useMemo(
    () => packages.find(item => item.pluginId === selectedPluginId) ?? packages[0] ?? null,
    [packages, selectedPluginId]);
  const commandChoices = useMemo(
    () => selectedPackage ? collectProviderCommands(selectedPackage) : [],
    [selectedPackage]);
  const selectedCommand = useMemo(
    () => commandChoices.find(choice => choice.key === selectedCommandKey)
      ?? commandChoices[0]
      ?? null,
    [commandChoices, selectedCommandKey]);
  const validationProblems = useMemo<ExtensionProblem[]>(
    () => packages.flatMap(extension => extension.validationIssues.map(issue => ({
      code: issue.code,
      message: `${extension.pluginId}: ${issue.message}`,
      severity: 'error' as const
    }))),
    [packages]);
  const problems = [...operationalProblems, ...validationProblems];
  const canOperate = scope !== null && isBackendHealthy && busyAction === null;

  const recordFailure = useCallback((code: string, message: string) => {
    setOperationalProblems(current => [
      { code, message, severity: 'error' as const },
      ...current.filter(problem => problem.code !== code)
    ].slice(0, 12));
    onMessage(message);
  }, [onMessage]);

  const loadPackages = useCallback(async (preferredPluginId?: string) => {
    if (!scope || !isBackendHealthy) {
      setPackages([]);
      setSelectedPluginId('');
      return;
    }
    setBusyAction('refresh');
    try {
      const response = await listApplicationExtensions(scope);
      if (!response.ok || !response.body) {
        recordFailure('Extensions.ListFailed', describeApiFailure(response.status, response.text));
        return;
      }
      const nextPackages = response.body;
      setPackages(nextPackages);
      setSelectedPluginId(current => {
        const preferred = preferredPluginId ?? current;
        return nextPackages.some(item => item.pluginId === preferred)
          ? preferred
          : nextPackages[0]?.pluginId ?? '';
      });
      setOperationalProblems(current => current.filter(problem => problem.code !== 'Extensions.ListFailed'));
    } finally {
      setBusyAction(null);
    }
  }, [isBackendHealthy, recordFailure, scope]);

  useEffect(() => {
    setPackages([]);
    setSelectedPluginId('');
    setSelectedCommandKey('');
    setProviderKey('');
    setTrialResult(null);
    setOperationalProblems([]);
    setRemoveConfirmation(null);
    if (scope && isBackendHealthy) {
      void loadPackages();
    }
  }, [isBackendHealthy, loadPackages, scope?.applicationId, scope?.projectId]);

  useEffect(() => {
    const first = commandChoices[0] ?? null;
    setSelectedCommandKey(first?.key ?? '');
    setProviderKey(selectedPackage?.pluginId ?? '');
    setTrialResult(null);
  }, [selectedPackage?.pluginId]);

  const handleImport = useCallback(async () => {
    if (!scope) {
      return;
    }
    setBusyAction('import');
    try {
      const result = await importApplicationExtension(scope);
      if (result.canceled) {
        onMessage('Extension import canceled.');
        return;
      }
      if (!result.response?.ok || !result.response.body) {
        recordFailure(
          'Extensions.ImportFailed',
          `Extension import failed: ${describeApiFailure(result.response?.status ?? 0, result.response?.text ?? '')}`);
        return;
      }
      setOperationalProblems(current => current.filter(problem => problem.code !== 'Extensions.ImportFailed'));
      onMessage(
        `Imported ${result.response.body.pluginId} into Application ${scope.applicationId} as ${result.portableId}.`);
      await loadPackages(result.response.body.pluginId);
    } catch (error) {
      recordFailure('Extensions.ImportFailed', `Extension import failed: ${String(error)}`);
    } finally {
      setBusyAction(null);
    }
  }, [loadPackages, onMessage, recordFailure, scope]);

  const handleValidate = useCallback(async () => {
    if (!scope) {
      return;
    }
    setBusyAction('validate');
    try {
      const response = await validateApplicationExtensions(scope);
      if (!response.ok || !response.body) {
        recordFailure('Extensions.ValidationFailed', describeApiFailure(response.status, response.text));
        return;
      }
      setPackages(response.body);
      const invalidCount = response.body.filter(item => !item.isValid).length;
      onMessage(invalidCount === 0
        ? `Validated ${response.body.length} Application extension package(s).`
        : `Extension validation found ${invalidCount} invalid package(s).`);
      setOperationalProblems(current => current.filter(
        problem => problem.code !== 'Extensions.ValidationFailed'));
    } finally {
      setBusyAction(null);
    }
  }, [onMessage, recordFailure, scope]);

  const handleRemove = useCallback(async () => {
    if (!scope || !selectedPackage) {
      return;
    }
    if (removeConfirmation !== selectedPackage.pluginId) {
      setRemoveConfirmation(selectedPackage.pluginId);
      return;
    }
    setBusyAction('remove');
    try {
      const response = await removeApplicationExtension(selectedPackage.pluginId, scope);
      if (!response.ok) {
        recordFailure('Extensions.RemoveFailed', describeApiFailure(response.status, response.text));
        return;
      }
      onMessage(`Removed ${selectedPackage.pluginId} from Application ${scope.applicationId}.`);
      setRemoveConfirmation(null);
      await loadPackages();
    } finally {
      setBusyAction(null);
    }
  }, [loadPackages, onMessage, recordFailure, removeConfirmation, scope, selectedPackage]);

  const handleTrial = useCallback(async () => {
    if (!scope || !selectedPackage || !selectedCommand) {
      return;
    }
    setBusyAction('trial');
    setTrialResult(null);
    const resourceId = createProviderTrialResourceId(selectedPackage.pluginId, selectedCommand.command.id);
    try {
      const definition = createProviderTrialDefinition(
        resourceId,
        selectedPackage,
        selectedCommand,
        providerKey,
        outcomePath,
        passedToken,
        failedToken,
        abortedToken);
      const response = await trialExternalProgramDefinition(
        definition,
        {
          inputs: {
            identity: { kind: 'Text', canonicalValue: trialIdentity },
            model: { kind: 'Text', canonicalValue: trialModel }
          }
        },
        scope);
      if (!response.ok || !response.body) {
        recordFailure('Extensions.TrialFailed', describeApiFailure(response.status, response.text));
        return;
      }
      setTrialResult(response.body);
      setOperationalProblems(current => current.filter(problem =>
        problem.code !== 'Extensions.TrialFailed'));
      onMessage(
        `Provider trial ${response.body.executionStatus} / ${response.body.judgement} for ${selectedCommand.command.id}.`);
    } catch (error) {
      recordFailure('Extensions.TrialFailed', `Provider trial failed: ${String(error)}`);
    } finally {
      setBusyAction(null);
    }
  }, [
    abortedToken,
    failedToken,
    onMessage,
    outcomePath,
    passedToken,
    providerKey,
    recordFailure,
    scope,
    selectedCommand,
    selectedPackage,
    trialIdentity,
    trialModel
  ]);

  if (!scope || !application) {
    return (
      <section className="extensions-empty-state" data-testid="plugins-workbench">
        <PackageOpen size={34} />
        <h2>Open a Project Application</h2>
        <p>Extensions belong to one Application. Open a Project and select an Application before importing packages.</p>
      </section>
    );
  }

  return (
    <section className="plugins-workbench" data-testid="plugins-workbench">
      <header className="extensions-header">
        <div>
          <span className="extensions-eyebrow">Application Extensions</span>
          <h2>{application.displayName}</h2>
          <p>{scope.projectId} / {scope.applicationId}</p>
        </div>
        <div className="extensions-toolbar">
          <button type="button" className="button ghost" disabled={!canOperate} onClick={() => { void loadPackages(); }} data-testid="refresh-application-extensions">
            <RefreshCw size={15} /> Refresh
          </button>
          <button type="button" className="button" disabled={!canOperate} onClick={() => { void handleValidate(); }} data-testid="validate-application-extensions">
            <ShieldCheck size={15} /> Validate
          </button>
          <button type="button" className="button primary" disabled={!canOperate} onClick={() => { void handleImport(); }} data-testid="import-application-extension">
            <Upload size={15} /> Import ZIP
          </button>
        </div>
      </header>

      <aside className="extensions-package-rail">
        <div className="extensions-section-heading">
          <Package size={15} />
          <strong>Packages</strong>
          <span>{packages.length}</span>
        </div>
        <div className="extensions-package-list" data-testid="application-extension-list">
          {packages.length === 0 ? (
            <div className="extensions-zero">
              <FileArchive size={22} />
              <strong>No extensions in this Application</strong>
              <span>Import a signed-off ZIP package to add reusable automation capabilities.</span>
            </div>
          ) : packages.map(extension => (
            <button
              type="button"
              key={extension.pluginId}
              className={extension.pluginId === selectedPackage?.pluginId ? 'selected' : ''}
              onClick={() => {
                setSelectedPluginId(extension.pluginId);
                setRemoveConfirmation(null);
              }}
              data-testid={`extension-package-${extension.pluginId}`}
            >
              <span className={extension.isValid ? 'extension-status valid' : 'extension-status invalid'} />
              <span>
                <strong>{extension.manifest.name}</strong>
                <small>{extension.pluginId}</small>
              </span>
              <code>{extension.version}</code>
            </button>
          ))}
        </div>
        {selectedPackage ? (
          <button type="button" className="button danger extensions-remove" disabled={!canOperate} onClick={() => { void handleRemove(); }} data-testid="remove-application-extension">
            <Trash2 size={14} />
            {removeConfirmation === selectedPackage.pluginId ? 'Confirm remove' : 'Remove from Application'}
          </button>
        ) : null}
      </aside>

      <main className="extensions-inspector">
        {selectedPackage ? (
          <>
            <section className="extension-summary-card" data-testid="extension-manifest-preview">
              <div className="extension-summary-title">
                <div className={selectedPackage.isValid ? 'extension-validity valid' : 'extension-validity invalid'}>
                  {selectedPackage.isValid ? <CheckCircle2 size={18} /> : <XCircle size={18} />}
                  <span>{selectedPackage.isValid ? 'Package valid' : 'Validation blocked'}</span>
                </div>
                <span>{selectedPackage.manifest.kind}</span>
              </div>
              <h3>{selectedPackage.manifest.name}</h3>
              <p>{selectedPackage.manifest.id}</p>
              <dl>
                <dt>Portable ID</dt><dd>{selectedPackage.portableId}</dd>
                <dt>Entry</dt><dd>{selectedPackage.manifest.entryAssembly}</dd>
                <dt>Type</dt><dd>{selectedPackage.manifest.entryType}</dd>
                <dt>Contract</dt><dd>{selectedPackage.manifest.contractVersion}</dd>
                <dt>Platform</dt><dd>{selectedPackage.manifest.minimumPlatformVersion}</dd>
                <dt>Runtime</dt><dd>{selectedPackage.manifest.runtimeIdentifier}</dd>
                <dt>ABI</dt><dd>{selectedPackage.manifest.abiVersion}</dd>
                <dt>Manifest</dt><dd>{selectedPackage.manifestPath}</dd>
              </dl>
              <div className="extension-capability-tags">
                {selectedPackage.manifest.capabilities.map(capability => (
                  <span key={capability}>{capability}</span>
                ))}
              </div>
            </section>

            <section className="extension-evidence-card" data-testid="extension-hash-preview">
              <div className="extensions-section-heading">
                <Hash size={15} />
                <strong>Immutable identity</strong>
              </div>
              <HashRow label="Content" value={selectedPackage.contentSha256} testId="extension-content-sha256" />
              <HashRow label="Manifest" value={selectedPackage.manifestSha256} />
              <HashRow label="Entry assembly" value={selectedPackage.entryAssemblySha256} />
            </section>

            <section className="extension-files-card" data-testid="extension-file-preview">
              <div className="extensions-section-heading">
                <FileCode2 size={15} />
                <strong>Package files</strong>
                <span>{selectedPackage.files.length}</span>
              </div>
              <div className="extension-file-table">
                {selectedPackage.files.map(file => (
                  <article key={file.relativePath}>
                    <span>{file.relativePath}</span>
                    <small>{formatBytes(file.sizeBytes)}</small>
                    <code title={file.sha256}>{compactHash(file.sha256)}</code>
                  </article>
                ))}
              </div>
            </section>
          </>
        ) : (
          <div className="extension-inspector-empty">Select or import an Application extension package.</div>
        )}
      </main>

      <aside className="extensions-trial-column">
        <section className="extension-trial-card">
          <div className="extensions-section-heading">
            <FlaskConical size={15} />
            <strong>Provider protocol trial</strong>
          </div>
          {commandChoices.length === 0 ? (
            <p className="extension-muted">The selected package declares no runnable provider commands.</p>
          ) : (
            <>
              <label>
                <span>Command</span>
                <select value={selectedCommand?.key ?? ''} onChange={event => setSelectedCommandKey(event.target.value)} data-testid="extension-trial-command">
                  {commandChoices.map(choice => (
                    <option key={choice.key} value={choice.key}>
                      {choice.command.id} / {choice.providerKind}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>Provider key</span>
                <input value={providerKey} onChange={event => setProviderKey(event.target.value)} data-testid="extension-trial-provider-key" />
              </label>
              <div className="extension-trial-pair">
                <label><span>Product identity</span><input value={trialIdentity} onChange={event => setTrialIdentity(event.target.value)} /></label>
                <label><span>Product model</span><input value={trialModel} onChange={event => setTrialModel(event.target.value)} /></label>
              </div>
              <label>
                <span>Judgement path</span>
                <input value={outcomePath} onChange={event => setOutcomePath(event.target.value)} data-testid="extension-trial-outcome-path" />
              </label>
              <div className="extension-trial-token-grid">
                <label><span>Passed</span><input value={passedToken} onChange={event => setPassedToken(event.target.value)} data-testid="extension-trial-passed-token" /></label>
                <label><span>Failed</span><input value={failedToken} onChange={event => setFailedToken(event.target.value)} /></label>
                <label><span>Aborted</span><input value={abortedToken} onChange={event => setAbortedToken(event.target.value)} /></label>
              </div>
              <button type="button" className="button primary extension-trial-run" disabled={!canOperate || !selectedCommand} onClick={() => { void handleTrial(); }} data-testid="run-extension-provider-trial">
                <FlaskConical size={14} /> Run isolated trial
              </button>
            </>
          )}
          <TrialResult result={trialResult} />
        </section>

        <section className="extension-problems-card" data-testid="extension-problems">
          <div className="extensions-section-heading">
            <AlertTriangle size={15} />
            <strong>Problems</strong>
            <span>{problems.length}</span>
          </div>
          {problems.length === 0 ? (
            <div className="extensions-problems-clear"><CheckCircle2 size={16} /> No extension problems</div>
          ) : problems.map((problem, index) => (
            <article key={`${problem.code}-${index}`}>
              <strong>{problem.code}</strong>
              <span>{problem.message}</span>
            </article>
          ))}
        </section>
      </aside>
    </section>
  );
}

function HashRow({ label, value, testId }: { label: string; value: string; testId?: string }): React.ReactElement {
  return (
    <div className="extension-hash-row" data-testid={testId}>
      <span>{label}</span>
      <code title={value}>{value}</code>
    </div>
  );
}

function TrialResult({ result }: { result: ExternalProgramTrialResponse | null }): React.ReactElement | null {
  if (!result) {
    return null;
  }
  const successful = result.executionStatus === 'Completed' && result.judgement === 'Passed';
  return (
    <div className={successful ? 'extension-trial-result passed' : 'extension-trial-result failed'} data-testid="extension-provider-trial-result">
      <div>
        {successful ? <CheckCircle2 size={17} /> : <XCircle size={17} />}
        <strong>{result.executionStatus} / {result.judgement}</strong>
      </div>
      <span>{result.failureReason ?? result.resultPayload ?? 'No provider payload returned.'}</span>
    </div>
  );
}

function collectProviderCommands(
  extension: ApplicationExtensionPackageResponse
): ProviderCommandChoice[] {
  return [
    ...extension.manifest.deviceCommands.map(command => ({
      key: `device:${command.id}`,
      providerKind: 'PluginCommand' as const,
      command
    })),
    ...extension.manifest.processCommands.map(command => ({
      key: `process:${command.id}`,
      providerKind: 'ProcessCommandProvider' as const,
      command
    }))
  ];
}

function createProviderTrialDefinition(
  resourceId: string,
  extension: ApplicationExtensionPackageResponse,
  choice: ProviderCommandChoice,
  providerKey: string,
  outcomePath: string,
  passedToken: string,
  failedToken: string,
  abortedToken: string
): SaveExternalProgramResourceRequest {
  return {
    resourceId,
    displayName: `${extension.manifest.name} / ${choice.command.commandName} protocol trial`,
    capabilityId: choice.command.capability,
    commandName: choice.command.commandName,
    launchKind: 'Provider',
    entryPoint: null,
    providerKind: choice.providerKind,
    providerKey,
    argumentTemplates: [],
    inputMappings: [
      { source: '$product.identity', target: 'identity' },
      { source: '$product.model', target: 'model' }
    ],
    resultMappings: [
      { sourcePath: outcomePath, targetKey: 'extension.trial.result', valueKind: 'Text' }
    ],
    outcomeMapping: {
      sourcePath: outcomePath,
      passedToken,
      failedToken,
      abortedToken
    },
    permissionProfile: {
      profileName: 'Restricted',
      networkAccessAllowed: false,
      allowedEnvironmentVariables: []
    },
    executionLimits: {
      timeoutMilliseconds: Math.max(1_000, Math.min(choice.command.timeoutMilliseconds, 60_000)),
      maximumProcessCount: 1,
      maximumWorkingSetBytes: 128 * 1024 * 1024,
      maximumCpuTimeMilliseconds: 60_000,
      maximumStandardOutputBytes: 1024 * 1024,
      maximumStandardErrorBytes: 1024 * 1024,
      maximumArtifactCount: 2,
      maximumArtifactBytes: 1024 * 1024,
      maximumTotalArtifactBytes: 2 * 1024 * 1024
    }
  };
}

function createProviderTrialResourceId(pluginId: string, commandId: string): string {
  const suffix = `${pluginId}-${commandId}`
    .replace(/[^A-Za-z0-9._-]+/gu, '-')
    .replace(/^[._-]+|[._-]+$/gu, '')
    .slice(0, 54);
  return `extension-trial-${Date.now().toString(36)}-${suffix || 'command'}`;
}

function describeApiFailure(status: number, text: string): string {
  const detail = text.trim();
  return `${status || 'transport'}${detail ? `: ${detail}` : ''}`;
}

function compactHash(value: string): string {
  return value.length > 18 ? `${value.slice(0, 10)}…${value.slice(-6)}` : value;
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }
  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KiB`;
  }
  return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
}
