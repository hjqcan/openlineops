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
import {
  acceptSubmittedEditorDraft,
  createEditorDraftBaseline,
  isEditorDraftDirty,
  replaceEditorDraft,
  revertEditorDraft
} from './editor-draft-baseline-model';
import { useEditorDocument } from './editor-workspace';
import type { EditorProblem } from './editor-workspace-model';

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
  const [draftState, setDraftState] = useState(
    () => createEditorDraftBaseline(createDeviceDraft()));
  const draft = draftState.current;
  const setDraft = useCallback((update: React.SetStateAction<DeviceDraft>) => {
    setDraftState(state => replaceEditorDraft(
      state,
      typeof update === 'function'
        ? update(state.current)
        : update));
  }, []);
  const draftDirty = isEditorDraftDirty(draftState, deviceDraftsEqual);
  const [selectedInstanceId, setSelectedInstanceId] = useState('');
  const [busy, setBusy] = useState(false);

  const selectedInstance = useMemo(
    () => resources.instances.find(instance => instance.deviceInstanceId === selectedInstanceId)
      ?? resources.instances[0]
      ?? null,
    [resources.instances, selectedInstanceId]);
  const canCreate = isBackendHealthy && !busy;
  const canOperate = canCreate && selectedInstance !== null;
  const editorProblems = useMemo<EditorProblem[]>(() => {
    const problems: EditorProblem[] = [];
    const requiredFields: Array<[keyof DeviceDraft, string, string]> = [
      ['definitionId', 'Device definition ID is required.', 'device-definition-id'],
      ['definitionName', 'Device definition name is required.', 'device-definition-name'],
      ['pluginId', 'Provider plugin ID is required.', 'device-plugin-id'],
      ['capabilityId', 'Device capability ID is required.', 'device-capability-id'],
      ['capabilityName', 'Device capability name is required.', 'device-capability-name'],
      ['commandDefinitionId', 'Command definition ID is required.', 'device-command-id'],
      ['commandName', 'Command name is required.', 'device-command-name'],
      ['instanceId', 'Device instance ID is required.', 'device-instance-id'],
      ['stationId', 'Station identity is required.', 'device-station-id'],
      ['instanceName', 'Device instance name is required.', 'device-instance-name'],
      ['protocol', 'Device protocol is required.', 'device-protocol'],
      ['address', 'Device address is required.', 'device-address']
    ];
    for (const [field, message, targetId] of requiredFields) {
      if (typeof draft[field] === 'string' && !draft[field].trim()) {
        problems.push({ id: `device-${field}`, severity: 'Error', message, targetId });
      }
    }
    if (!Number.isInteger(draft.timeoutSeconds) || draft.timeoutSeconds <= 0) {
      problems.push({
        id: 'device-timeout',
        severity: 'Error',
        message: 'Command timeout must be a positive whole number of seconds.',
        targetId: 'device-command-id'
      });
    }
    if (!Number.isInteger(draft.maxRetries) || draft.maxRetries < 0) {
      problems.push({
        id: 'device-retries',
        severity: 'Error',
        message: 'Command retries must be a non-negative whole number.',
        targetId: 'device-command-id'
      });
    }
    return problems;
  }, [draft]);

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

  const revertDraft = useCallback(async () => {
    setDraftState(revertEditorDraft);
    onMessage('Device fields discarded; the last registered values were restored.');
  }, [onMessage]);

  const createBundle = useCallback(async () => {
    const submittedDraft = draft;
    setBusy(true);
    try {
      const existingDefinition = resources.definitions.find(
        definition => definition.deviceDefinitionId === submittedDraft.definitionId) ?? null;
      if (existingDefinition && !deviceDefinitionMatchesDraft(existingDefinition, submittedDraft)) {
        throw new Error(
          `Device definition ${submittedDraft.definitionId} already exists with different immutable content.`);
      }
      if (!existingDefinition) {
        const definition = await createDeviceDefinition({
          deviceDefinitionId: submittedDraft.definitionId,
          displayName: submittedDraft.definitionName,
          pluginId: submittedDraft.pluginId,
          capabilities: [
            {
              capabilityId: submittedDraft.capabilityId,
              displayName: submittedDraft.capabilityName
            }
          ],
          commands: [
            {
              commandDefinitionId: submittedDraft.commandDefinitionId,
              capabilityId: submittedDraft.capabilityId,
              commandName: submittedDraft.commandName,
              inputSchema: null,
              outputSchema: null,
              timeoutSeconds: submittedDraft.timeoutSeconds,
              maxRetries: submittedDraft.maxRetries
            }
          ]
        });
        if (!definition.ok) {
          throw new Error(`Device definition failed: ${definition.status} ${definition.text}`);
        }
      }

      const existingInstance = resources.instances.find(
        instance => instance.deviceInstanceId === submittedDraft.instanceId) ?? null;
      if (existingInstance && !deviceInstanceMatchesDraft(existingInstance, submittedDraft)) {
        throw new Error(
          `Device instance ${submittedDraft.instanceId} already exists with different immutable content.`);
      }
      let registeredInstance = existingInstance;
      if (!registeredInstance) {
        const instance = await registerDeviceInstance({
          deviceInstanceId: submittedDraft.instanceId,
          deviceDefinitionId: submittedDraft.definitionId,
          stationId: submittedDraft.stationId,
          displayName: submittedDraft.instanceName,
          protocol: submittedDraft.protocol,
          address: submittedDraft.address
        });
        if (!instance.ok || !instance.body) {
          throw new Error(`Device instance failed: ${instance.status} ${instance.text}`);
        }
        registeredInstance = instance.body;
      }

      setSelectedInstanceId(registeredInstance.deviceInstanceId);
      setDraftState(state => acceptSubmittedEditorDraft(state, submittedDraft));
      onMessage(`Device registered ${registeredInstance.deviceInstanceId}`);
      try {
        await loadResources();
      } catch (error) {
        onMessage(
          `Device registered ${registeredInstance.deviceInstanceId}; resource refresh failed: ${String(error)}`);
      }
    } catch (error) {
      try {
        await loadResources();
      } catch (refreshError) {
        onMessage(`Device save failed and resource refresh also failed: ${String(refreshError)}`);
      }
      throw error;
    } finally {
      setBusy(false);
    }
  }, [draft, loadResources, onMessage, resources.definitions, resources.instances]);

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

  useEditorDocument({
    dirty: draftDirty,
    editRevision: draft,
    canSave: canCreate && editorProblems.length === 0,
    save: createBundle,
    revert: revertDraft,
    focus: targetId => {
      if (targetId) {
        document.querySelector<HTMLElement>(`[data-testid="${targetId}"]`)?.focus();
      }
    },
    problems: editorProblems
  });

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
            className="button ghost"
            onClick={() => void revertDraft()}
            disabled={!draftDirty || busy}
            data-testid="discard-device-draft"
          >
            <RotateCcw size={15} />
            Discard Changes
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => void createBundle().catch(error => onMessage(String(error)))}
            disabled={!canCreate || editorProblems.length > 0}
            data-testid="create-device-bundle"
          >
            <Send size={15} />
            Register Device
          </button>
        </div>

        <div className="devices-layout">
          <div className="devices-form">
            <FieldGroup title="Definition">
              <TextField testId="device-definition-id" label="Definition ID" value={draft.definitionId} onChange={value => setDraft(current => ({ ...current, definitionId: value }))} />
              <TextField testId="device-definition-name" label="Display Name" value={draft.definitionName} onChange={value => setDraft(current => ({ ...current, definitionName: value }))} />
              <TextField testId="device-plugin-id" label="Plugin ID" value={draft.pluginId} onChange={value => setDraft(current => ({ ...current, pluginId: value }))} />
            </FieldGroup>
            <FieldGroup title="Capability And Command">
              <TextField testId="device-capability-id" label="Capability ID" value={draft.capabilityId} onChange={value => setDraft(current => ({ ...current, capabilityId: value }))} />
              <TextField testId="device-capability-name" label="Capability Name" value={draft.capabilityName} onChange={value => setDraft(current => ({ ...current, capabilityName: value }))} />
              <TextField testId="device-command-id" label="Command ID" value={draft.commandDefinitionId} onChange={value => setDraft(current => ({ ...current, commandDefinitionId: value }))} />
              <TextField testId="device-command-name" label="Command Name" value={draft.commandName} onChange={value => setDraft(current => ({ ...current, commandName: value }))} />
            </FieldGroup>
            <FieldGroup title="Instance">
              <TextField testId="device-instance-id" label="Instance ID" value={draft.instanceId} onChange={value => setDraft(current => ({ ...current, instanceId: value }))} />
              <TextField testId="device-station-id" label="Station ID" value={draft.stationId} onChange={value => setDraft(current => ({ ...current, stationId: value }))} />
              <TextField testId="device-instance-name" label="Display Name" value={draft.instanceName} onChange={value => setDraft(current => ({ ...current, instanceName: value }))} />
              <TextField testId="device-protocol" label="Protocol" value={draft.protocol} onChange={value => setDraft(current => ({ ...current, protocol: value }))} />
              <TextField testId="device-address" label="Address" value={draft.address} onChange={value => setDraft(current => ({ ...current, address: value }))} />
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
  testId,
  onChange
}: {
  label: string;
  value: string;
  testId?: string;
  onChange(value: string): void;
}): React.ReactElement {
  return (
    <label>
      <span>{label}</span>
      <input data-testid={testId} value={value} onChange={event => onChange(event.target.value)} />
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

function deviceDraftsEqual(left: DeviceDraft, right: DeviceDraft): boolean {
  return left.definitionId === right.definitionId
    && left.definitionName === right.definitionName
    && left.pluginId === right.pluginId
    && left.capabilityId === right.capabilityId
    && left.capabilityName === right.capabilityName
    && left.commandDefinitionId === right.commandDefinitionId
    && left.commandName === right.commandName
    && left.timeoutSeconds === right.timeoutSeconds
    && left.maxRetries === right.maxRetries
    && left.instanceId === right.instanceId
    && left.stationId === right.stationId
    && left.instanceName === right.instanceName
    && left.protocol === right.protocol
    && left.address === right.address;
}

function deviceDefinitionMatchesDraft(
  definition: DeviceDefinitionResponse,
  draft: DeviceDraft
): boolean {
  const capability = definition.capabilities.length === 1 ? definition.capabilities[0] : null;
  const command = definition.commands.length === 1 ? definition.commands[0] : null;
  return definition.displayName === draft.definitionName
    && definition.pluginId === draft.pluginId
    && capability?.capabilityId === draft.capabilityId
    && capability.displayName === draft.capabilityName
    && command?.commandDefinitionId === draft.commandDefinitionId
    && command.capabilityId === draft.capabilityId
    && command.commandName === draft.commandName
    && command.timeoutSeconds === draft.timeoutSeconds
    && command.maxRetries === draft.maxRetries;
}

function deviceInstanceMatchesDraft(
  instance: DeviceInstanceResponse,
  draft: DeviceDraft
): boolean {
  return instance.deviceDefinitionId === draft.definitionId
    && instance.stationId === draft.stationId
    && instance.displayName === draft.instanceName
    && instance.protocol === draft.protocol
    && instance.address === draft.address;
}
