export interface EngineeringDraft {
  recipeId: string;
  recipeVersionId: string;
  recipeName: string;
  stationProfileId: string;
  stationSystemId: string;
  stationName: string;
  deviceBindingId: string;
  deviceOwnerSystemId: string;
  capabilityId: string;
  deviceKey: string;
  snapshotId: string;
  processDefinitionId: string;
  processVersionId: string;
}

export interface EngineeringDeviceOwnerOption {
  value: string;
  label: string;
}

export function createEngineeringDeviceOwnerOptions(
  systems: readonly { systemId: string; displayName: string }[]
): EngineeringDeviceOwnerOption[] {
  return [
    {
      value: '',
      label: systems.length === 0
        ? 'No System in selected Station'
        : 'Select a device owner System'
    },
    ...systems.map(system => ({
      value: system.systemId,
      label: `${system.displayName} (${system.systemId})`
    }))
  ];
}

export function engineeringSourceDraftsEqual(
  left: EngineeringDraft,
  right: EngineeringDraft
): boolean {
  return left.recipeId === right.recipeId
    && left.recipeVersionId === right.recipeVersionId
    && left.recipeName === right.recipeName
    && left.stationProfileId === right.stationProfileId
    && left.stationSystemId === right.stationSystemId
    && left.stationName === right.stationName
    && left.deviceBindingId === right.deviceBindingId
    && left.deviceOwnerSystemId === right.deviceOwnerSystemId
    && left.capabilityId === right.capabilityId
    && left.deviceKey === right.deviceKey;
}
