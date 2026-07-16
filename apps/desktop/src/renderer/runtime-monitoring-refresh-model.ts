export interface ApiResponseEnvelope<TBody> {
  ok: boolean;
  status: number;
  text: string;
  body: TBody | null | undefined;
}

export function requireApiResponseBody<TBody>(
  response: ApiResponseEnvelope<TBody>,
  action: string
): TBody {
  if (!response.ok || response.body === null || response.body === undefined) {
    throw new Error(`${action} failed: ${response.status} ${response.text}`.trimEnd());
  }
  return response.body;
}

export function requireApiItemsResponse<TItem>(
  response: ApiResponseEnvelope<{ items: TItem[] }>,
  action: string
): TItem[] {
  const body = requireApiResponseBody(response, action);
  if (!Array.isArray(body.items)) {
    throw new Error(`${action} failed: the response body does not contain an items array.`);
  }
  return body.items;
}

export interface RuntimeMonitoringProjection<TStation, TTarget, TAlarm, TTrace, TTimeline> {
  stations: TStation[];
  targets: TTarget[];
  alarms: TAlarm[];
  traces: TTrace[];
  timeline: TTimeline[];
}

export interface RuntimeMonitoringProjectionLoaders<TStation, TTarget, TAlarm, TTrace, TTimeline> {
  loadStations(): Promise<TStation[]>;
  loadTargets(stations: readonly TStation[]): Promise<TTarget[]>;
  loadAlarms(): Promise<TAlarm[]>;
  loadTraces(): Promise<TTrace[]>;
  loadTimeline(stations: readonly TStation[]): Promise<TTimeline[]>;
}

export async function loadRuntimeMonitoringProjection<
  TStation,
  TTarget,
  TAlarm,
  TTrace,
  TTimeline
>(
  loaders: RuntimeMonitoringProjectionLoaders<TStation, TTarget, TAlarm, TTrace, TTimeline>
): Promise<RuntimeMonitoringProjection<TStation, TTarget, TAlarm, TTrace, TTimeline>> {
  const [stations, alarms, traces] = await Promise.all([
    loaders.loadStations(),
    loaders.loadAlarms(),
    loaders.loadTraces()
  ]);
  const [targets, timeline] = await Promise.all([
    loaders.loadTargets(stations),
    loaders.loadTimeline(stations)
  ]);
  return { stations, targets, alarms, traces, timeline };
}
