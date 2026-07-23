export interface ExternalProgramResourceIdentity {
  resourceId: string;
}

export interface LoadedExternalProgramResource<TResource extends ExternalProgramResourceIdentity> {
  resources: TResource[];
  resource: TResource;
}

export async function runLatestExternalProgramRequest<TResult>(
  request: () => Promise<TResult>,
  shouldUseResult: () => boolean
): Promise<TResult | null> {
  try {
    const result = await request();
    return shouldUseResult() ? result : null;
  } catch (error) {
    if (!shouldUseResult()) {
      return null;
    }
    throw error;
  }
}

export async function loadExternalProgramResourceCore<TResource extends ExternalProgramResourceIdentity>(
  resourceId: string,
  listResources: () => Promise<TResource[]>,
  shouldUseResult: () => boolean
): Promise<LoadedExternalProgramResource<TResource> | null> {
  const resources = await runLatestExternalProgramRequest(listResources, shouldUseResult);
  if (resources === null) {
    return null;
  }

  const resource = resources.find(candidate => candidate.resourceId === resourceId);
  if (!resource) {
    throw new Error(`External program ${resourceId} no longer exists.`);
  }
  return { resources, resource };
}
