import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Boxes,
  CheckCircle2,
  ListChecks,
  Package,
  Play,
  RefreshCw,
  ServerCog,
  Square,
  Terminal,
  XCircle
} from 'lucide-react';
import type {
  ExternalPluginProcessEventResponse,
  PluginCapabilityResponse,
  PluginCommandResponse,
  PluginLifecycleRecordResponse,
  PluginManagementOverviewResponse,
  PluginPackageResponse
} from './contracts';
import {
  getPluginOverview,
  listPluginEvents,
  startPlugins,
  stopPlugins
} from './api';

interface PluginsWorkbenchProps {
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

const emptyOverview: PluginManagementOverviewResponse = {
  packages: [],
  capabilities: [],
  deviceCommands: [],
  processCommands: [],
  recentEvents: []
};

export function PluginsWorkbench({
  isBackendHealthy,
  onMessage
}: PluginsWorkbenchProps): React.ReactElement {
  const [overview, setOverview] = useState<PluginManagementOverviewResponse>(emptyOverview);
  const [lifecycleRecords, setLifecycleRecords] = useState<PluginLifecycleRecordResponse[]>([]);
  const [events, setEvents] = useState<ExternalPluginProcessEventResponse[]>([]);
  const [selectedPluginId, setSelectedPluginId] = useState('');
  const [busy, setBusy] = useState(false);

  const selectedPackage = useMemo(
    () => overview.packages.find(item => item.manifest.id === selectedPluginId)
      ?? overview.packages[0]
      ?? null,
    [overview.packages, selectedPluginId]);
  const selectedLifecycle = useMemo(
    () => lifecycleRecords.find(record => record.manifest.id === selectedPackage?.manifest.id)
      ?? lifecycleRecords[0]
      ?? null,
    [lifecycleRecords, selectedPackage?.manifest.id]);
  const canOperate = isBackendHealthy && !busy;

  const loadOverview = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    const nextOverview = await getPluginOverview();
    const nextEvents = await listPluginEvents(undefined, undefined, 50);
    const resolvedOverview = nextOverview ?? emptyOverview;
    setOverview(resolvedOverview);
    setEvents(nextEvents.length > 0 ? nextEvents : resolvedOverview.recentEvents);
    setSelectedPluginId(current => current || (resolvedOverview.packages[0]?.manifest.id ?? ''));
  }, [isBackendHealthy]);

  useEffect(() => {
    loadOverview().catch(error => onMessage(`Plugins load failed: ${String(error)}`));
  }, [loadOverview, onMessage]);

  const runLifecycle = useCallback(async (action: 'start' | 'stop') => {
    setBusy(true);
    try {
      const response = action === 'start'
        ? await startPlugins()
        : await stopPlugins();
      if (!response.ok || !response.body) {
        onMessage(`Plugins ${action} failed: ${response.status} ${response.text}`);
        return;
      }

      setLifecycleRecords(response.body);
      const record = response.body.find(item => item.manifest.id === selectedPackage?.manifest.id)
        ?? response.body[0];
      onMessage(record
        ? `Plugin ${record.manifest.id} ${record.state}`
        : `Plugins ${action} returned no records`);
      await loadOverview();
    } finally {
      setBusy(false);
    }
  }, [loadOverview, onMessage, selectedPackage?.manifest.id]);

  return (
    <section className="plugins-workbench" data-testid="plugins-workbench">
      <div className="panel plugins-catalog-panel">
        <div className="panel-title">
          <div>
            <Package size={17} />
            <h2>Plugin Management</h2>
          </div>
          <span>{overview.packages.length} packages</span>
        </div>

        <div className="plugins-toolbar">
          <button type="button" className="button ghost" onClick={() => { void loadOverview(); }} disabled={!canOperate}>
            <RefreshCw size={15} />
            Refresh
          </button>
          <button type="button" className="button primary" onClick={() => { void runLifecycle('start'); }} disabled={!canOperate} data-testid="start-plugins">
            <Play size={15} />
            Start
          </button>
          <button type="button" className="button" onClick={() => { void runLifecycle('stop'); }} disabled={!canOperate} data-testid="stop-plugins">
            <Square size={15} />
            Stop
          </button>
        </div>

        <div className="plugins-layout">
          <PluginPackageList
            packages={overview.packages}
            selectedPluginId={selectedPackage?.manifest.id ?? ''}
            onSelect={setSelectedPluginId}
          />
          <PluginManifestDetail pluginPackage={selectedPackage} />
        </div>
      </div>

      <div className="panel plugin-lifecycle-panel">
        <div className="panel-title">
          <div>
            <ServerCog size={17} />
            <h2>Lifecycle</h2>
          </div>
          <span>{selectedLifecycle?.state ?? 'idle'}</span>
        </div>
        <PluginLifecycleCard record={selectedLifecycle} />
        <PluginEventList events={events} />
      </div>

      <div className="panel plugin-inventory-panel">
        <div className="panel-title">
          <div>
            <ListChecks size={17} />
            <h2>Capability Catalog</h2>
          </div>
          <span>{overview.capabilities.length + overview.deviceCommands.length + overview.processCommands.length} entries</span>
        </div>
        <div className="plugin-inventory-grid">
          <PluginCapabilityList capabilities={overview.capabilities} />
          <PluginCommandList title="Device Commands" commands={overview.deviceCommands} />
          <PluginCommandList title="Process Commands" commands={overview.processCommands} />
        </div>
      </div>
    </section>
  );
}

function PluginPackageList({
  packages,
  selectedPluginId,
  onSelect
}: {
  packages: PluginPackageResponse[];
  selectedPluginId: string;
  onSelect(pluginId: string): void;
}): React.ReactElement {
  return (
    <section className="plugin-package-list">
      <div>
        <strong>Packages</strong>
        <span>{packages.length}</span>
      </div>
      {packages.length === 0 ? (
        <p>No plugin packages discovered.</p>
      ) : packages.map(pluginPackage => (
        <button
          type="button"
          key={pluginPackage.manifest.id}
          className={pluginPackage.manifest.id === selectedPluginId ? 'selected' : ''}
          onClick={() => onSelect(pluginPackage.manifest.id)}
        >
          <strong>{pluginPackage.manifest.name}</strong>
          <span>{pluginPackage.manifest.id}</span>
          <small>{pluginPackage.manifest.kind} / {pluginPackage.manifest.version}</small>
        </button>
      ))}
    </section>
  );
}

function PluginManifestDetail({
  pluginPackage
}: {
  pluginPackage: PluginPackageResponse | null;
}): React.ReactElement {
  if (!pluginPackage) {
    return <div className="plugin-empty">No plugin manifest selected.</div>;
  }

  return (
    <section className="plugin-manifest-card" data-testid="plugin-manifest-card">
      <div className={pluginPackage.isValid ? 'plugin-validity valid' : 'plugin-validity invalid'}>
        {pluginPackage.isValid ? <CheckCircle2 size={17} /> : <XCircle size={17} />}
        <strong>{pluginPackage.isValid ? 'Valid Manifest' : 'Invalid Manifest'}</strong>
      </div>
      <dl>
        <dt>ID</dt>
        <dd>{pluginPackage.manifest.id}</dd>
        <dt>Name</dt>
        <dd>{pluginPackage.manifest.name}</dd>
        <dt>Kind</dt>
        <dd>{pluginPackage.manifest.kind}</dd>
        <dt>Contract</dt>
        <dd>{pluginPackage.manifest.contractVersion}</dd>
        <dt>Minimum Platform</dt>
        <dd>{pluginPackage.manifest.minimumPlatformVersion}</dd>
        <dt>Entry Assembly</dt>
        <dd>{pluginPackage.manifest.entryAssembly}</dd>
        <dt>Entry Type</dt>
        <dd>{pluginPackage.manifest.entryType}</dd>
        <dt>Manifest Path</dt>
        <dd>{pluginPackage.manifestPath}</dd>
      </dl>
      {pluginPackage.validationIssues.length > 0 ? (
        <div className="plugin-validation-list">
          {pluginPackage.validationIssues.map(issue => (
            <article key={`${issue.code}-${issue.message}`}>
              <strong>{issue.code}</strong>
              <span>{issue.message}</span>
            </article>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function PluginLifecycleCard({
  record
}: {
  record: PluginLifecycleRecordResponse | null;
}): React.ReactElement {
  return (
    <div className="plugin-lifecycle-card" data-testid="plugin-lifecycle-result">
      {record ? (
        <>
          <div className={record.state === 'Initialized' ? 'plugin-lifecycle-state running' : 'plugin-lifecycle-state'}>
            <ServerCog size={18} />
            <div>
              <strong>{record.state}</strong>
              <span>{record.manifest.name}</span>
            </div>
          </div>
          <dl>
            <dt>Plugin</dt>
            <dd>{record.manifest.id}</dd>
            <dt>Status</dt>
            <dd>{record.initializationStatus}</dd>
            <dt>Failure</dt>
            <dd>{record.failureReason ?? 'none'}</dd>
            <dt>Issues</dt>
            <dd>{record.validationIssues.length}</dd>
          </dl>
        </>
      ) : (
        <div className="plugin-empty">No lifecycle operation has run.</div>
      )}
    </div>
  );
}

function PluginEventList({
  events
}: {
  events: ExternalPluginProcessEventResponse[];
}): React.ReactElement {
  return (
    <section className="plugin-events">
      <div>
        <Terminal size={15} />
        <strong>Process Events</strong>
      </div>
      {events.length === 0 ? (
        <p>No external process events.</p>
      ) : events.slice(0, 8).map((event, index) => (
        <article key={`${event.pluginId}-${event.occurredAtUtc}-${index}`}>
          <strong>{event.kind}</strong>
          <span>{event.pluginId}</span>
          <small>{event.message} / {formatDateTime(event.occurredAtUtc)}</small>
        </article>
      ))}
    </section>
  );
}

function PluginCapabilityList({
  capabilities
}: {
  capabilities: PluginCapabilityResponse[];
}): React.ReactElement {
  return (
    <section className="plugin-inventory-list">
      <div>
        <Boxes size={15} />
        <strong>Capabilities</strong>
        <span>{capabilities.length}</span>
      </div>
      {capabilities.length === 0 ? (
        <p>No capabilities</p>
      ) : capabilities.map(capability => (
        <article key={`${capability.pluginId}-${capability.capability}`}>
          <strong>{capability.capability}</strong>
          <span>{capability.pluginName}</span>
          <small>{capability.pluginKind}</small>
        </article>
      ))}
    </section>
  );
}

function PluginCommandList({
  title,
  commands
}: {
  title: string;
  commands: PluginCommandResponse[];
}): React.ReactElement {
  return (
    <section className="plugin-inventory-list">
      <div>
        <Terminal size={15} />
        <strong>{title}</strong>
        <span>{commands.length}</span>
      </div>
      {commands.length === 0 ? (
        <p>No commands</p>
      ) : commands.map(command => (
        <article key={`${command.pluginId}-${command.commandDefinitionId}`}>
          <strong>{command.commandName}</strong>
          <span>{command.capability}</span>
          <small>{command.timeoutMilliseconds}ms / {command.maxRetries} retries</small>
        </article>
      ))}
    </section>
  );
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}
