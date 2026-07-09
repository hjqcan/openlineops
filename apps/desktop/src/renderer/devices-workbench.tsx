import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Cable,
  CheckCircle2,
  Cpu,
  PlugZap,
  RefreshCw,
  RotateCcw,
  Send,
  Unplug,
  XCircle
} from 'lucide-react';
import type {
  DeviceDefinitionResponse,
  DeviceInstanceResponse
} from './contracts';
import {
  connectDeviceInstance,
  createDeviceDefinition,
  disconnectDeviceInstance,
  faultDeviceInstance,
  listDeviceDefinitions,
  listDeviceInstances,
  registerDeviceInstance,
  resetDeviceFault
} from './api';

interface DevicesWorkbenchProps {
  isBackendHealthy: boolean;
  onMessage(message: string): void;
}

interface DeviceDraft {
  definitionId: string;
  definitionName: string;
  pluginId: string;
  capabilityId: string;
  capabilityName: string;
  commandDefinitionId: string;
  commandName: string;
  timeoutSeconds: number;
  maxRetries: number;
  instanceId: string;
  stationId: string;
  instanceName: string;
  protocol: string;
  address: string;
}

interface DeviceResources {
  definitions: DeviceDefinitionResponse[];
  instances: DeviceInstanceResponse[];
}

const emptyResources: DeviceResources = {
  definitions: [],
  instances: []
};

export function DevicesWorkbench({
  isBackendHealthy,
  onMessage
}: DevicesWorkbenchProps): React.ReactElement {
  const [resources, setResources] = useState<DeviceResources>(emptyResources);
  const [draft, setDraft] = useState<DeviceDraft>(() => createDeviceDraft());
  const [selectedInstanceId, setSelectedInstanceId] = useState('');
  const [busy, setBusy] = useState(false);

  const selectedInstance = useMemo(
    () => resources.instances.find(instance => instance.deviceInstanceId === selectedInstanceId)
      ?? resources.instances[0]
      ?? null,
    [resources.instances, selectedInstanceId]);
  const canCreate = isBackendHealthy && !busy;
  const canOperate = canCreate && selectedInstance !== null;

  const loadResources = useCallback(async () => {
    if (!isBackendHealthy) {
      return;
    }

    const [definitions, instances] = await Promise.all([
      listDeviceDefinitions(),
      listDeviceInstances()
    ]);
    setResources({ definitions, instances });
    setSelectedInstanceId(current => current || (instances[0]?.deviceInstanceId ?? ''));
  }, [isBackendHealthy]);

  useEffect(() => {
    loadResources().catch(error => onMessage(`Devices load failed: ${String(error)}`));
  }, [loadResources, onMessage]);

  const resetDraft = useCallback(() => {
    setDraft(createDeviceDraft());
  }, []);

  const createBundle = useCallback(async () => {
    setBusy(true);
    try {
      const definition = await createDeviceDefinition({
        deviceDefinitionId: draft.definitionId,
        displayName: draft.definitionName,
        pluginId: draft.pluginId,
        capabilities: [
          {
            capabilityId: draft.capabilityId,
            displayName: draft.capabilityName
          }
        ],
        commands: [
          {
            commandDefinitionId: draft.commandDefinitionId,
            capabilityId: draft.capabilityId,
            commandName: draft.commandName,
            inputSchema: null,
            outputSchema: null,
            timeoutSeconds: draft.timeoutSeconds,
            maxRetries: draft.maxRetries
          }
        ]
      });
      if (!definition.ok) {
        onMessage(`Device definition failed: ${definition.status} ${definition.text}`);
        return;
      }

      const instance = await registerDeviceInstance({
        deviceInstanceId: draft.instanceId,
        deviceDefinitionId: draft.definitionId,
        stationId: draft.stationId,
        displayName: draft.instanceName,
        protocol: draft.protocol,
        address: draft.address
      });
      if (!instance.ok || !instance.body) {
        onMessage(`Device instance failed: ${instance.status} ${instance.text}`);
        return;
      }

      setSelectedInstanceId(instance.body.deviceInstanceId);
      onMessage(`Device registered ${instance.body.deviceInstanceId}`);
      await loadResources();
    } finally {
      setBusy(false);
    }
  }, [draft, loadResources, onMessage]);

  const changeStatus = useCallback(async (
    action: 'connect' | 'disconnect' | 'fault' | 'reset'
  ) => {
    if (!selectedInstance) {
      return;
    }

    setBusy(true);
    try {
      const response = action === 'connect'
        ? await connectDeviceInstance(selectedInstance.deviceInstanceId)
        : action === 'disconnect'
          ? await disconnectDeviceInstance(selectedInstance.deviceInstanceId, {
            reason: 'Disconnected from desktop'
          })
          : action === 'fault'
            ? await faultDeviceInstance(selectedInstance.deviceInstanceId, {
              reason: 'Fault marked from desktop'
            })
            : await resetDeviceFault(selectedInstance.deviceInstanceId);

      if (!response.ok || !response.body) {
        onMessage(`Device ${action} failed: ${response.status} ${response.text}`);
        return;
      }

      onMessage(`Device ${response.body.deviceInstanceId} ${response.body.status}`);
      await loadResources();
    } finally {
      setBusy(false);
    }
  }, [loadResources, onMessage, selectedInstance]);

  return (
    <section className="devices-workbench">
      <div className="panel devices-builder-panel">
        <div className="panel-title">
          <div>
            <PlugZap size={17} />
            <h2>Device Integration</h2>
          </div>
          <span>{resources.instances.length} instances</span>
        </div>

        <div className="devices-toolbar">
          <button type="button" className="button ghost" onClick={loadResources} disabled={!isBackendHealthy || busy}>
            <RefreshCw size={15} />
            Refresh
          </button>
          <button type="button" className="button ghost" onClick={resetDraft} disabled={busy}>
            <RotateCcw size={15} />
            New Seed
          </button>
          <button
            type="button"
            className="button primary"
            onClick={createBundle}
            disabled={!canCreate}
            data-testid="create-device-bundle"
          >
            <Send size={15} />
            Register Device
          </button>
        </div>

        <div className="devices-layout">
          <div className="devices-form">
            <FieldGroup title="Definition">
              <TextField label="Definition ID" value={draft.definitionId} onChange={value => setDraft(current => ({ ...current, definitionId: value }))} />
              <TextField label="Display Name" value={draft.definitionName} onChange={value => setDraft(current => ({ ...current, definitionName: value }))} />
              <TextField label="Plugin ID" value={draft.pluginId} onChange={value => setDraft(current => ({ ...current, pluginId: value }))} />
            </FieldGroup>
            <FieldGroup title="Capability And Command">
              <TextField label="Capability ID" value={draft.capabilityId} onChange={value => setDraft(current => ({ ...current, capabilityId: value }))} />
              <TextField label="Capability Name" value={draft.capabilityName} onChange={value => setDraft(current => ({ ...current, capabilityName: value }))} />
              <TextField label="Command ID" value={draft.commandDefinitionId} onChange={value => setDraft(current => ({ ...current, commandDefinitionId: value }))} />
              <TextField label="Command Name" value={draft.commandName} onChange={value => setDraft(current => ({ ...current, commandName: value }))} />
            </FieldGroup>
            <FieldGroup title="Instance">
              <TextField label="Instance ID" value={draft.instanceId} onChange={value => setDraft(current => ({ ...current, instanceId: value }))} />
              <TextField label="Station ID" value={draft.stationId} onChange={value => setDraft(current => ({ ...current, stationId: value }))} />
              <TextField label="Display Name" value={draft.instanceName} onChange={value => setDraft(current => ({ ...current, instanceName: value }))} />
              <TextField label="Protocol" value={draft.protocol} onChange={value => setDraft(current => ({ ...current, protocol: value }))} />
              <TextField label="Address" value={draft.address} onChange={value => setDraft(current => ({ ...current, address: value }))} />
            </FieldGroup>
          </div>

          <div className="devices-resource-grid">
            <DeviceDefinitionsList definitions={resources.definitions} />
            <DeviceInstancesList
              instances={resources.instances}
              selectedInstanceId={selectedInstance?.deviceInstanceId ?? ''}
              onSelect={setSelectedInstanceId}
            />
          </div>
        </div>
      </div>

      <div className="panel device-status-panel">
        <div className="panel-title">
          <div>
            <Cpu size={17} />
            <h2>Connection State</h2>
          </div>
          <span>{selectedInstance?.status ?? 'none'}</span>
        </div>
        {selectedInstance ? (
          <div className="device-status-card" data-testid="device-status-card">
            <StatusHeader instance={selectedInstance} />
            <dl>
              <dt>Instance</dt>
              <dd>{selectedInstance.deviceInstanceId}</dd>
              <dt>Definition</dt>
              <dd>{selectedInstance.deviceDefinitionId}</dd>
              <dt>Station</dt>
              <dd>{selectedInstance.stationId}</dd>
              <dt>Endpoint</dt>
              <dd>{selectedInstance.protocol} / {selectedInstance.address}</dd>
              <dt>Fault</dt>
              <dd>{selectedInstance.faultReason ?? 'none'}</dd>
            </dl>
            <div className="device-status-actions">
              <button type="button" className="button" onClick={() => { void changeStatus('connect'); }} disabled={!canOperate} data-testid="connect-device-instance">
                <Cable size={15} />
                Connect
              </button>
              <button type="button" className="button" onClick={() => { void changeStatus('disconnect'); }} disabled={!canOperate}>
                <Unplug size={15} />
                Disconnect
              </button>
              <button type="button" className="button danger" onClick={() => { void changeStatus('fault'); }} disabled={!canOperate}>
                <XCircle size={15} />
                Fault
              </button>
              <button type="button" className="button ghost" onClick={() => { void changeStatus('reset'); }} disabled={!canOperate}>
                <RotateCcw size={15} />
                Reset
              </button>
            </div>
          </div>
        ) : (
          <div className="devices-empty">Register a device instance to manage connection state.</div>
        )}
      </div>
    </section>
  );
}

function FieldGroup({
  title,
  children
}: {
  title: string;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <fieldset className="devices-fieldset">
      <legend>{title}</legend>
      {children}
    </fieldset>
  );
}

function TextField({
  label,
  value,
  onChange
}: {
  label: string;
  value: string;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input value={value} onChange={event => onChange(event.target.value)} />
    </label>
  );
}

function DeviceDefinitionsList({
  definitions
}: {
  definitions: DeviceDefinitionResponse[];
}): React.ReactElement {
  return (
    <section className="device-resource-list">
      <div>
        <strong>Definitions</strong>
        <span>{definitions.length}</span>
      </div>
      {definitions.length === 0 ? (
        <p>No device definitions</p>
      ) : definitions.slice(0, 6).map(definition => (
        <article key={definition.deviceDefinitionId}>
          <strong>{definition.displayName}</strong>
          <span>{definition.deviceDefinitionId}</span>
          <small>{definition.capabilities.length} capabilities / {definition.commands.length} commands</small>
        </article>
      ))}
    </section>
  );
}

function DeviceInstancesList({
  instances,
  selectedInstanceId,
  onSelect
}: {
  instances: DeviceInstanceResponse[];
  selectedInstanceId: string;
  onSelect(instanceId: string): void;
}): React.ReactElement {
  return (
    <section className="device-resource-list">
      <div>
        <strong>Instances</strong>
        <span>{instances.length}</span>
      </div>
      {instances.length === 0 ? (
        <p>No device instances</p>
      ) : instances.slice(0, 8).map(instance => (
        <button
          type="button"
          key={instance.deviceInstanceId}
          className={instance.deviceInstanceId === selectedInstanceId ? 'selected' : ''}
          onClick={() => onSelect(instance.deviceInstanceId)}
        >
          <strong>{instance.displayName}</strong>
          <span>{instance.deviceInstanceId}</span>
          <small>{instance.status} / {instance.stationId}</small>
        </button>
      ))}
    </section>
  );
}

function StatusHeader({ instance }: { instance: DeviceInstanceResponse }): React.ReactElement {
  const isConnected = instance.status === 'Connected';
  return (
    <div className={isConnected ? 'device-status-header connected' : 'device-status-header'}>
      <CheckCircle2 size={18} />
      <div>
        <strong>{instance.status}</strong>
        <span>{instance.displayName}</span>
      </div>
    </div>
  );
}

function createDeviceDraft(): DeviceDraft {
  const seed = Date.now().toString(36);

  return {
    definitionId: `device-definition-desktop-${seed}`,
    definitionName: 'Desktop Loopback Device',
    pluginId: 'openlineops.loopback',
    capabilityId: 'device.loopback',
    capabilityName: 'Loopback Device',
    commandDefinitionId: `loopback-echo-${seed}`,
    commandName: 'Echo',
    timeoutSeconds: 15,
    maxRetries: 1,
    instanceId: `device-instance-desktop-${seed}`,
    stationId: `station-device-${seed}`,
    instanceName: 'Desktop Loopback 01',
    protocol: 'simulator',
    address: 'loopback://desktop-01'
  };
}
