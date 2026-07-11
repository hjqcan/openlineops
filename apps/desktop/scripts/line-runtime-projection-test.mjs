import assert from 'node:assert/strict';
import path from 'node:path';
import process from 'node:process';
import { build } from 'esbuild';

const rendererModule = path.join(
  process.cwd(),
  'src',
  'renderer',
  'production-line-runtime-view.ts');
const rendererModuleSpecifier = `./${path.relative(process.cwd(), rendererModule).replaceAll('\\', '/')}`;
const topologyModule = path.join(
  process.cwd(),
  'src',
  'renderer',
  'topology-runtime-view.ts');
const topologyModuleSpecifier = `./${path.relative(process.cwd(), topologyModule).replaceAll('\\', '/')}`;
const testSource = `
  import { buildProductionLineRuntimeView } from ${JSON.stringify(rendererModuleSpecifier)};
  import { buildTopologyRuntimeView } from ${JSON.stringify(topologyModuleSpecifier)};

  const queueUnitId = '10000000-0000-0000-0000-000000000001';
  const runningUnitId = '10000000-0000-0000-0000-000000000002';
  const carrierUnitId = '10000000-0000-0000-0000-000000000003';
  const state = {
    productionLineDefinitionId: 'line.cold-start',
    generatedAtUtc: '2026-07-11T08:00:01.000Z',
    activeRunCount: 0,
    activeRuns: [],
    productionUnits: [
      {
        productionUnitId: queueUnitId,
        productModelId: 'board.main',
        identityKey: 'serialNumber',
        identityValue: 'SN-QUEUE-001',
        disposition: 'InProcess',
        judgement: 'Unknown',
        productionRunId: null,
        location: {
          kind: 'StationQueue', lineId: 'line.cold-start', stationSystemId: 'station.assembly',
          slotId: null, carrierId: null, carrierPositionId: null
        },
        lastTransitionAtUtc: '2026-07-11T08:00:00.000Z',
        activeOperationRunIds: []
      },
      {
        productionUnitId: runningUnitId,
        productModelId: 'board.main',
        identityKey: 'serialNumber',
        identityValue: 'SN-RUNNING-002',
        disposition: 'InProcess',
        judgement: 'Unknown',
        productionRunId: '20000000-0000-0000-0000-000000000001',
        location: {
          kind: 'Slot', lineId: 'line.cold-start', stationSystemId: 'station.assembly',
          slotId: 'slot.running', carrierId: null, carrierPositionId: null
        },
        lastTransitionAtUtc: '2026-07-11T08:00:00.000Z',
        activeOperationRunIds: ['operation-run-1']
      },
      {
        productionUnitId: carrierUnitId,
        productModelId: 'board.main',
        identityKey: 'serialNumber',
        identityValue: 'SN-CARRIER-003',
        disposition: 'InProcess',
        judgement: 'Passed',
        productionRunId: null,
        location: {
          kind: 'CarrierPosition', lineId: null, stationSystemId: null,
          slotId: null, carrierId: 'carrier.tray-01', carrierPositionId: 'position-01'
        },
        lastTransitionAtUtc: '2026-07-11T08:00:00.000Z',
        activeOperationRunIds: []
      }
    ],
    stations: [
      {
        stationSystemId: 'station.assembly',
        status: 'Running',
        queue: [{ materialKind: 'ProductionUnit', materialId: queueUnitId, queuedAtUtc: '2026-07-11T08:00:00.000Z' }],
        activeOperations: [
          {
            productionRunId: '20000000-0000-0000-0000-000000000001',
            productionUnitId: runningUnitId,
            productionUnitIdentity: { modelId: 'board.main', inputKey: 'serialNumber', value: 'SN-RUNNING-002' },
            operationRunId: 'operation-run-1',
            operationId: 'operation.assemble',
            executionStatus: 'Running',
            judgement: 'Unknown',
            startedAtUtc: '2026-07-11T08:00:00.000Z',
            resources: [
              {
                kind: 'Station', resourceId: 'station.assembly', status: 'Leased', fencingToken: 41,
                acquiredAtUtc: '2026-07-11T08:00:00.000Z', expiresAtUtc: '2026-07-11T08:10:00.000Z'
              },
              {
                kind: 'Slot', resourceId: 'slot.running', status: 'Leased', fencingToken: 42,
                acquiredAtUtc: '2026-07-11T08:00:00.000Z', expiresAtUtc: '2026-07-11T08:10:00.000Z'
              },
              {
                kind: 'Fixture', resourceId: 'fixture.main', status: 'RecoveryHeld', fencingToken: 43,
                acquiredAtUtc: '2026-07-11T08:00:00.000Z', expiresAtUtc: '9999-12-31T23:59:59.999Z'
              },
              {
                kind: 'Device', resourceId: 'device.vendor', status: 'Leased', fencingToken: 44,
                acquiredAtUtc: '2026-07-11T08:00:00.000Z', expiresAtUtc: '2026-07-11T08:10:00.000Z'
              }
            ]
          }
        ]
      }
    ],
    slots: [
      { stationSystemId: 'station.assembly', slotId: 'slot.available', status: 'Available', materialKind: null, materialId: null, lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' },
      { stationSystemId: 'station.assembly', slotId: 'slot.reserved', status: 'Reserved', materialKind: 'ProductionUnit', materialId: queueUnitId, lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' },
      { stationSystemId: 'station.assembly', slotId: 'slot.occupied', status: 'Occupied', materialKind: 'Carrier', materialId: 'carrier.tray-01', lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' },
      { stationSystemId: 'station.assembly', slotId: 'slot.running', status: 'Running', materialKind: 'ProductionUnit', materialId: runningUnitId, lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' },
      { stationSystemId: 'station.assembly', slotId: 'slot.blocked', status: 'Blocked', materialKind: null, materialId: null, lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' },
      { stationSystemId: 'station.assembly', slotId: 'slot.offline', status: 'Offline', materialKind: null, materialId: null, lastTransitionAtUtc: '2026-07-11T08:00:00.000Z' }
    ],
    carriers: [
      {
        carrierId: 'carrier.tray-01',
        carrierTypeId: 'tray.24-up',
        capacity: 24,
        location: {
          kind: 'StationQueue', lineId: 'line.cold-start', stationSystemId: 'station.assembly',
          slotId: null, carrierId: null, carrierPositionId: null
        },
        lastTransitionAtUtc: '2026-07-11T08:00:00.000Z',
        productionUnits: [
          { carrierPositionId: 'position-01', productionUnitId: carrierUnitId, disposition: 'InProcess', judgement: 'Passed' }
        ]
      }
    ]
  };

  const view = buildProductionLineRuntimeView(state);
  const station = view.stations[0];
  if (!station) throw new Error('Station projection was not created.');
  const topology = {
    systems: [
      {
        systemId: 'station.assembly', kind: 'Station', parentSystemId: null,
        displayName: 'Assembly', systemType: 'automation.station'
      },
      {
        systemId: 'system.vision', kind: 'System', parentSystemId: 'station.assembly',
        displayName: 'Vision', systemType: 'automation.vision'
      }
    ],
    slotGroups: [
      {
        slotGroupId: 'group.fixture', parentSystemId: 'station.assembly',
        displayName: 'Fixture', slotIds: ['slot.available']
      }
    ],
    slots: [
      {
        slotId: 'slot.available', parentSystemId: 'station.assembly',
        displayName: 'Slot 1', isEnabled: true
      }
    ]
  };
  const completedLineState = {
    ...state,
    activeRunCount: 0,
    activeRuns: [],
    productionUnits: [],
    stations: [],
    slots: [],
    carriers: []
  };
  const completedTopology = buildTopologyRuntimeView(topology, completedLineState);
  const offlineTopology = buildTopologyRuntimeView(topology, {
    ...completedLineState,
    stations: [
      {
        stationSystemId: 'station.assembly', status: 'Offline',
        queue: [], activeOperations: []
      }
    ],
    slots: [
      {
        stationSystemId: 'station.assembly', slotId: 'slot.available',
        status: 'Offline', materialKind: null, materialId: null,
        lastTransitionAtUtc: '2026-07-11T08:00:00.000Z'
      }
    ]
  });
  globalThis.__lineProjectionTestResult = {
    activeRunCount: state.activeRuns.length,
    stationStatus: station.status,
    queueLabel: station.queue[0]?.label,
    slotStatuses: Object.fromEntries(station.slots.map(slot => [slot.slotId, slot.status])),
    runningSlotLabel: station.slots.find(slot => slot.slotId === 'slot.running')?.materialLabel,
    carrierProduct: view.carriers[0]?.productionUnits[0]?.productionUnitLabel,
    deviceLease: station.resources.find(resource => resource.resourceId === 'device.vendor'),
    completedStationState: completedTopology.stationStatuses.get('station.assembly')?.operationalState,
    completedSystemState: completedTopology.targetStatusByKey
      .get(JSON.stringify(['station.assembly', 'System', 'system.vision']))?.operationalState,
    completedSlotState: completedTopology.slots[0]?.slotState,
    completedSlotOperationalState: completedTopology.slots[0]?.operationalState,
    offlineStationState: offlineTopology.stationStatuses.get('station.assembly')?.operationalState,
    offlineSlotState: offlineTopology.slots[0]?.slotState,
    offlineSlotOperationalState: offlineTopology.slots[0]?.operationalState
  };
`;

const bundle = await build({
  stdin: {
    contents: testSource,
    loader: 'ts',
    resolveDir: process.cwd(),
    sourcefile: 'line-runtime-projection-test.ts'
  },
  bundle: true,
  format: 'esm',
  platform: 'node',
  target: 'node22',
  write: false,
  logLevel: 'silent'
});
const executable = bundle.outputFiles[0]?.text;
assert.ok(executable, 'Projection test bundle was not emitted.');
await import(`data:text/javascript;base64,${Buffer.from(executable).toString('base64')}`);

const result = globalThis.__lineProjectionTestResult;
assert.equal(result.activeRunCount, 0, 'Cold-start projection must not depend on active renderer Runs.');
assert.equal(result.stationStatus, 'Running');
assert.equal(result.queueLabel, 'SN-QUEUE-001');
assert.deepEqual(result.slotStatuses, {
  'slot.available': 'Available',
  'slot.reserved': 'Reserved',
  'slot.occupied': 'Occupied',
  'slot.running': 'Running',
  'slot.blocked': 'Blocked',
  'slot.offline': 'Offline'
});
assert.equal(result.runningSlotLabel, 'SN-RUNNING-002');
assert.equal(result.carrierProduct, 'SN-CARRIER-003');
assert.equal(result.deviceLease.status, 'Leased');
assert.equal(result.deviceLease.fencingToken, 44);
assert.equal(result.completedStationState, 'Idle');
assert.equal(result.completedSystemState, 'Idle');
assert.equal(result.completedSlotState, 'Available');
assert.equal(result.completedSlotOperationalState, 'Idle');
assert.equal(result.offlineStationState, 'Offline');
assert.equal(result.offlineSlotState, 'Offline');
assert.equal(result.offlineSlotOperationalState, 'Offline');
console.log('Production Line cold-start projection test passed.');
