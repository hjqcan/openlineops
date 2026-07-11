import React, { memo, useMemo, useRef } from 'react';
import { CircleDot, GitBranch, GripVertical, Workflow } from 'lucide-react';
import type {
  AutomationTopologyResponse,
  ProcessDefinitionSummary,
  ProductionOperationRequest,
  RouteTransitionRequest
} from './contracts';

export interface GraphPoint {
  x: number;
  y: number;
}

export type DesignerSelection =
  | { kind: 'operation'; id: string }
  | { kind: 'transition'; id: string }
  | null;

interface StationLane {
  stationSystemId: string;
  displayName: string;
  y: number;
}

export interface RouteGraphLayout {
  positions: Record<string, GraphPoint>;
  stationLanes: StationLane[];
  width: number;
  height: number;
}

interface ProductionRouteGraphProps {
  operations: ProductionOperationRequest[];
  transitions: RouteTransitionRequest[];
  topology: AutomationTopologyResponse | null;
  flows: ProcessDefinitionSummary[];
  entryOperationId: string;
  selection: DesignerSelection;
  positions: Record<string, GraphPoint>;
  operationProblemIds: ReadonlySet<string>;
  transitionProblemIds: ReadonlySet<string>;
  onSelect(selection: DesignerSelection): void;
  onMove(operationId: string, position: GraphPoint): void;
}

const NODE_WIDTH = 220;
const NODE_HEIGHT = 98;
const LANE_HEIGHT = 184;

export const ProductionRouteGraph = memo(function ProductionRouteGraph({
  operations,
  transitions,
  topology,
  flows,
  entryOperationId,
  selection,
  positions,
  operationProblemIds,
  transitionProblemIds,
  onSelect,
  onMove
}: ProductionRouteGraphProps): React.ReactElement {
  const layout = useMemo(
    () => createAutoRouteLayout(operations, transitions, topology),
    [operations, topology, transitions]);
  const effectivePositions = useMemo(
    () => ({ ...layout.positions, ...positions }),
    [layout.positions, positions]);
  const stationById = useMemo(
    () => new Map((topology?.systems ?? []).map(system => [system.systemId, system])),
    [topology]);
  const flowById = useMemo(
    () => new Map(flows.map(flow => [flow.processDefinitionId, flow])),
    [flows]);
  const forwardSources = useMemo(
    () => new Set(
      transitions
        .filter(transition => transition.kind !== 'Rework')
        .map(transition => transition.sourceOperationId)),
    [transitions]);

  if (operations.length === 0) {
    return (
      <div className="production-route-empty">
        <Workflow size={28} />
        <strong>No Operations yet</strong>
        <span>Add an Operation to establish the route entry.</span>
      </div>
    );
  }

  return (
    <div className="production-route-scroll" data-testid="production-route-graph">
      <div
        className="production-route-canvas"
        style={{ width: layout.width, height: layout.height }}
        onPointerDown={() => onSelect(null)}
      >
        {layout.stationLanes.map(lane => (
          <div
            className="production-station-lane"
            key={lane.stationSystemId}
            style={{ top: lane.y, height: LANE_HEIGHT - 12 }}
          >
            <div className="production-station-lane-title">
              <span>STATION</span>
              <strong>{lane.displayName}</strong>
              <small>{lane.stationSystemId}</small>
            </div>
          </div>
        ))}

        <svg
          className="production-route-edges"
          width={layout.width}
          height={layout.height}
          aria-label="Route transitions"
        >
          <defs>
            <marker
              id="production-route-arrow"
              viewBox="0 0 10 10"
              refX="8"
              refY="5"
              markerWidth="6"
              markerHeight="6"
              orient="auto-start-reverse"
            >
              <path d="M 0 0 L 10 5 L 0 10 z" />
            </marker>
          </defs>
          {transitions.map(transition => {
            const source = effectivePositions[transition.sourceOperationId];
            const target = effectivePositions[transition.targetOperationId];
            if (!source || !target) {
              return null;
            }
            return (
              <TransitionEdge
                key={transition.transitionId}
                transition={transition}
                source={source}
                target={target}
                selected={selection?.kind === 'transition' && selection.id === transition.transitionId}
                invalid={transitionProblemIds.has(transition.transitionId)}
                onSelect={() => onSelect({ kind: 'transition', id: transition.transitionId })}
              />
            );
          })}
        </svg>

        {operations.map(operation => {
          const position = effectivePositions[operation.operationId] ?? { x: 40, y: 40 };
          const station = stationById.get(operation.stationSystemId);
          const flow = flowById.get(operation.flowDefinitionId);
          return (
            <OperationNode
              key={operation.operationId}
              operation={operation}
              position={position}
              stationName={station?.displayName ?? 'Unbound Station'}
              flowName={flow?.displayName ?? 'Unbound Flow'}
              entry={operation.operationId === entryOperationId}
              terminal={!forwardSources.has(operation.operationId)}
              selected={selection?.kind === 'operation' && selection.id === operation.operationId}
              invalid={operationProblemIds.has(operation.operationId)}
              canvasWidth={layout.width}
              canvasHeight={layout.height}
              onSelect={() => onSelect({ kind: 'operation', id: operation.operationId })}
              onMove={next => onMove(operation.operationId, next)}
            />
          );
        })}
      </div>
    </div>
  );
});

const OperationNode = memo(function OperationNode({
  operation,
  position,
  stationName,
  flowName,
  entry,
  terminal,
  selected,
  invalid,
  canvasWidth,
  canvasHeight,
  onSelect,
  onMove
}: {
  operation: ProductionOperationRequest;
  position: GraphPoint;
  stationName: string;
  flowName: string;
  entry: boolean;
  terminal: boolean;
  selected: boolean;
  invalid: boolean;
  canvasWidth: number;
  canvasHeight: number;
  onSelect(): void;
  onMove(position: GraphPoint): void;
}): React.ReactElement {
  const drag = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    origin: GraphPoint;
  } | null>(null);

  return (
    <article
      className={[
        'production-operation-node',
        selected ? 'selected' : '',
        invalid ? 'invalid' : ''
      ].filter(Boolean).join(' ')}
      style={{ transform: `translate(${position.x}px, ${position.y}px)` }}
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          onSelect();
        }
      }}
      onPointerDown={event => {
        event.stopPropagation();
        onSelect();
      }}
      data-testid={`production-operation-node-${operation.operationId}`}
    >
      <header
        onPointerDown={event => {
          event.stopPropagation();
          event.currentTarget.setPointerCapture(event.pointerId);
          drag.current = {
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            origin: position
          };
          onSelect();
        }}
        onPointerMove={event => {
          const active = drag.current;
          if (!active || active.pointerId !== event.pointerId) {
            return;
          }
          onMove({
            x: clamp(active.origin.x + event.clientX - active.startX, 8, canvasWidth - NODE_WIDTH - 8),
            y: clamp(active.origin.y + event.clientY - active.startY, 8, canvasHeight - NODE_HEIGHT - 8)
          });
        }}
        onPointerUp={event => {
          if (drag.current?.pointerId === event.pointerId) {
            drag.current = null;
          }
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
      >
        <GripVertical size={14} />
        <span>{stationName}</span>
        {invalid ? <b>!</b> : null}
      </header>
      <div className="production-operation-node-body">
        <div className="production-operation-glyph"><CircleDot size={17} /></div>
        <div>
          <strong>{operation.displayName || operation.operationId || 'Untitled Operation'}</strong>
          <span>{flowName}</span>
          <small>{operation.operationId || 'missing id'}</small>
        </div>
      </div>
      <footer>
        {entry ? <span className="entry"><GitBranch size={11} /> ENTRY</span> : <span />}
        {terminal ? <span className="terminal">COMPLETED</span> : null}
      </footer>
    </article>
  );
});

const TransitionEdge = memo(function TransitionEdge({
  transition,
  source,
  target,
  selected,
  invalid,
  onSelect
}: {
  transition: RouteTransitionRequest;
  source: GraphPoint;
  target: GraphPoint;
  selected: boolean;
  invalid: boolean;
  onSelect(): void;
}): React.ReactElement {
  const geometry = edgeGeometry(source, target, transition.kind === 'Rework');
  const className = [
    'production-route-edge',
    `kind-${transition.kind.toLowerCase()}`,
    selected ? 'selected' : '',
    invalid ? 'invalid' : ''
  ].filter(Boolean).join(' ');
  const label = transitionLabel(transition);
  return (
    <g
      className={className}
      onClick={event => {
        event.stopPropagation();
        onSelect();
      }}
      data-testid={`production-transition-edge-${transition.transitionId}`}
    >
      <path className="production-route-edge-hit" d={geometry.path} />
      <path className="production-route-edge-line" d={geometry.path} markerEnd="url(#production-route-arrow)" />
      <g transform={`translate(${geometry.label.x}, ${geometry.label.y})`}>
        <rect x={-42} y={-10} width={84} height={20} rx={2} />
        <text textAnchor="middle" dominantBaseline="central">{label}</text>
      </g>
    </g>
  );
});

export function createAutoRouteLayout(
  operations: ProductionOperationRequest[],
  transitions: RouteTransitionRequest[],
  topology: AutomationTopologyResponse | null
): RouteGraphLayout {
  const operationIds = new Set(operations.map(operation => operation.operationId));
  const forward = transitions.filter(transition => (
    transition.kind !== 'Rework'
    && operationIds.has(transition.sourceOperationId)
    && operationIds.has(transition.targetOperationId)));
  const indegrees = new Map(operations.map(operation => [operation.operationId, 0]));
  const rank = new Map(operations.map(operation => [operation.operationId, 0]));
  for (const transition of forward) {
    indegrees.set(transition.targetOperationId, (indegrees.get(transition.targetOperationId) ?? 0) + 1);
  }
  const pending = operations
    .filter(operation => (indegrees.get(operation.operationId) ?? 0) === 0)
    .map(operation => operation.operationId);
  while (pending.length > 0) {
    const sourceId = pending.shift();
    if (!sourceId) {
      continue;
    }
    for (const transition of forward.filter(candidate => candidate.sourceOperationId === sourceId)) {
      rank.set(
        transition.targetOperationId,
        Math.max(rank.get(transition.targetOperationId) ?? 0, (rank.get(sourceId) ?? 0) + 1));
      const nextDegree = (indegrees.get(transition.targetOperationId) ?? 0) - 1;
      indegrees.set(transition.targetOperationId, nextDegree);
      if (nextDegree === 0) {
        pending.push(transition.targetOperationId);
      }
    }
  }

  const usedStationIds = new Set(operations.map(operation => operation.stationSystemId));
  const topologyStations = (topology?.systems ?? [])
    .filter(system => system.kind === 'Station' && usedStationIds.has(system.systemId));
  const knownStationIds = new Set(topologyStations.map(station => station.systemId));
  const missingStationIds = [...usedStationIds].filter(stationId => !knownStationIds.has(stationId));
  const laneIds = [
    ...topologyStations.map(station => station.systemId),
    ...missingStationIds
  ];
  const normalizedLaneIds = laneIds.length > 0 ? laneIds : ['unbound-station'];
  const stationLanes: StationLane[] = normalizedLaneIds.map((stationSystemId, index) => ({
    stationSystemId,
    displayName: topologyStations.find(station => station.systemId === stationSystemId)?.displayName
      ?? (stationSystemId || 'Unbound Station'),
    y: 18 + index * LANE_HEIGHT
  }));
  const laneIndex = new Map(stationLanes.map((lane, index) => [lane.stationSystemId, index]));
  const collisionCount = new Map<string, number>();
  const positions: Record<string, GraphPoint> = {};
  let maxRank = 0;
  operations.forEach((operation, operationIndex) => {
    const operationRank = rank.get(operation.operationId) ?? operationIndex;
    maxRank = Math.max(maxRank, operationRank);
    const operationLane = laneIndex.get(operation.stationSystemId) ?? 0;
    const collisionKey = `${operationLane}:${operationRank}`;
    const collision = collisionCount.get(collisionKey) ?? 0;
    collisionCount.set(collisionKey, collision + 1);
    positions[operation.operationId] = {
      x: 168 + operationRank * 258 + collision * 24,
      y: stationLanes[operationLane].y + 48 + collision * 18
    };
  });

  return {
    positions,
    stationLanes,
    width: Math.max(1080, 168 + (maxRank + 1) * 258 + NODE_WIDTH + 70),
    height: Math.max(620, stationLanes.length * LANE_HEIGHT + 38)
  };
}

function edgeGeometry(
  source: GraphPoint,
  target: GraphPoint,
  rework: boolean
): { path: string; label: GraphPoint } {
  if (!rework && target.x > source.x + NODE_WIDTH * 0.65) {
    const startX = source.x + NODE_WIDTH;
    const startY = source.y + NODE_HEIGHT / 2;
    const endX = target.x;
    const endY = target.y + NODE_HEIGHT / 2;
    const controlOffset = Math.max(48, (endX - startX) * 0.42);
    return {
      path: `M ${startX} ${startY} C ${startX + controlOffset} ${startY}, ${endX - controlOffset} ${endY}, ${endX} ${endY}`,
      label: { x: (startX + endX) / 2, y: (startY + endY) / 2 - 12 }
    };
  }

  const startX = source.x + NODE_WIDTH / 2;
  const startY = source.y + NODE_HEIGHT;
  const endX = target.x + NODE_WIDTH / 2;
  const endY = target.y + NODE_HEIGHT;
  const bendY = Math.max(startY, endY) + (rework ? 64 : 44);
  return {
    path: `M ${startX} ${startY} C ${startX} ${bendY}, ${endX} ${bendY}, ${endX} ${endY}`,
    label: { x: (startX + endX) / 2, y: bendY }
  };
}

function transitionLabel(transition: RouteTransitionRequest): string {
  switch (transition.kind) {
    case 'Judgement':
      return transition.requiredJudgement ?? 'Judgement';
    case 'Condition':
      return `${transition.outputKey ?? 'output'} = ${transition.expectedOutputValue ?? '?'}`;
    case 'Rework':
      return `${transition.requiredJudgement ?? 'Rework'} ×${transition.maxTraversals ?? '?'}`;
    case 'ParallelFork':
      return 'Fork';
    case 'ParallelJoin':
      return 'Join';
    default:
      return 'Sequence';
  }
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(Math.max(value, minimum), maximum);
}
