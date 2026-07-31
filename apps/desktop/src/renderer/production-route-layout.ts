import type {
  OperationCanvasPosition,
  ProductionOperationRequest,
  ProductionRouteLayout
} from './contracts';
import type { GraphPoint } from './production-route-graph';

export const ROUTE_COORDINATE_MINIMUM = 0;
export const ROUTE_COORDINATE_MAXIMUM = 100_000;

export function routeLayoutToGraphPoints(
  layout: ProductionRouteLayout
): Record<string, GraphPoint> {
  return Object.fromEntries(layout.operationPositions.map(position => [
    position.operationId,
    { x: position.x, y: position.y }
  ]));
}

export function createProductionRouteLayout(
  operations: ProductionOperationRequest[],
  points: Readonly<Record<string, GraphPoint>>
): ProductionRouteLayout {
  return {
    operationPositions: operations.map(operation => {
      const point = points[operation.operationId];
      if (!point) {
        throw new Error(`Route layout is missing Operation ${operation.operationId}.`);
      }
      return toCanvasPosition(operation.operationId, point);
    })
  };
}

export function moveOperationInRouteLayout(
  layout: ProductionRouteLayout,
  operationId: string,
  point: GraphPoint
): ProductionRouteLayout {
  let matched = false;
  const operationPositions = layout.operationPositions.map(position => {
    if (position.operationId !== operationId) {
      return position;
    }
    matched = true;
    return toCanvasPosition(operationId, point);
  });
  if (!matched) {
    throw new Error(`Route layout does not contain Operation ${operationId}.`);
  }
  return { operationPositions };
}

export function renameOperationInRouteLayout(
  layout: ProductionRouteLayout,
  originalOperationId: string,
  nextOperationId: string
): ProductionRouteLayout {
  return {
    operationPositions: layout.operationPositions.map(position => (
      position.operationId === originalOperationId
        ? { ...position, operationId: nextOperationId }
        : position))
  };
}

export function removeOperationFromRouteLayout(
  layout: ProductionRouteLayout,
  operationId: string
): ProductionRouteLayout {
  return {
    operationPositions: layout.operationPositions.filter(
      position => position.operationId !== operationId)
  };
}

function toCanvasPosition(operationId: string, point: GraphPoint): OperationCanvasPosition {
  return {
    operationId,
    x: normalizeCoordinate(point.x),
    y: normalizeCoordinate(point.y)
  };
}

function normalizeCoordinate(value: number): number {
  if (!Number.isFinite(value)) {
    throw new Error('Route coordinates must be finite numbers.');
  }
  return Math.min(
    Math.max(Math.round(value), ROUTE_COORDINATE_MINIMUM),
    ROUTE_COORDINATE_MAXIMUM);
}
