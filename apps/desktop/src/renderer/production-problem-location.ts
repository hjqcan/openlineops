import type { DesignerSelection } from './production-route-graph';
import {
  productionProblemFields,
  type ProductionDesignerProblem,
  type ProductionDesignerProblemFieldLocator,
  type ProductionDesignerProblemScope
} from './production-route-validation';

export interface ProductionProblemLocation {
  scope: ProductionDesignerProblemScope;
  entityId: string;
  fieldLocator: ProductionDesignerProblemFieldLocator | null;
}

type SelectionChanged = (selection: DesignerSelection) => void;

const scopes: ProductionDesignerProblemScope[] = [
  'Line',
  'Product',
  'Operation',
  'Transition'
];
const fieldLocators = new Set<ProductionDesignerProblemFieldLocator>(
  Object.values(productionProblemFields));

export function serializeProductionProblemLocation(
  problem: Pick<ProductionDesignerProblem, 'scope' | 'entityId' | 'fieldLocator'>
): string {
  const location = createProductionProblemLocation(problem);
  return JSON.stringify([location.scope, location.entityId, location.fieldLocator]);
}

export function parseProductionProblemLocation(value: string | null): ProductionProblemLocation | null {
  if (value === null) {
    return null;
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (!Array.isArray(parsed)
        || parsed.length !== 3
        || !isScope(parsed[0])
        || typeof parsed[1] !== 'string'
        || (parsed[2] !== null && !isFieldLocator(parsed[2]))) {
      return null;
    }

    return createProductionProblemLocation({
      scope: parsed[0],
      entityId: parsed[1],
      fieldLocator: parsed[2]
    });
  } catch {
    return null;
  }
}

export function createProductionProblemLocation(
  problem: Pick<ProductionDesignerProblem, 'scope' | 'entityId' | 'fieldLocator'>
): ProductionProblemLocation {
  const location = {
    scope: problem.scope,
    entityId: problem.entityId,
    fieldLocator: problem.fieldLocator
  };
  assertProductionProblemLocation(location);
  return location;
}

export function focusProductionProblem(
  problem: Pick<ProductionDesignerProblem, 'scope' | 'entityId' | 'fieldLocator'>,
  onSelectionChanged: SelectionChanged,
  root?: ParentNode
): boolean {
  return focusProductionProblemLocation(
    createProductionProblemLocation(problem),
    onSelectionChanged,
    root);
}

export function focusProductionProblemLocation(
  location: ProductionProblemLocation,
  onSelectionChanged: SelectionChanged,
  root?: ParentNode
): boolean {
  assertProductionProblemLocation(location);
  if (location.scope === 'Operation') {
    onSelectionChanged({ kind: 'operation', id: location.entityId });
    return true;
  }
  if (location.scope === 'Transition') {
    onSelectionChanged({ kind: 'transition', id: location.entityId });
    return true;
  }

  onSelectionChanged(null);
  if (location.fieldLocator === null) {
    return false;
  }

  const queryRoot = root ?? document;
  const target = queryRoot.querySelector(
    productionProblemFieldSelector(location.fieldLocator)) as FocusableProblemField | null;
  if (!target
      || typeof target.scrollIntoView !== 'function'
      || typeof target.focus !== 'function') {
    return false;
  }

  target.scrollIntoView({ block: 'center', inline: 'nearest' });
  target.focus({ preventScroll: true });
  return true;
}

export function productionProblemFieldSelector(
  fieldLocator: ProductionDesignerProblemFieldLocator
): string {
  if (!isFieldLocator(fieldLocator)) {
    throw new Error(`Unsupported Production problem field locator: ${String(fieldLocator)}`);
  }
  return `[data-production-problem-field="${fieldLocator}"]`;
}

function assertProductionProblemLocation(location: ProductionProblemLocation): void {
  if (!isScope(location.scope)) {
    throw new Error(`Unsupported Production problem scope: ${String(location.scope)}`);
  }
  if (!location.entityId) {
    throw new Error('Production problem entity id must be non-empty.');
  }
  if (location.fieldLocator !== null && !isFieldLocator(location.fieldLocator)) {
    throw new Error(`Unsupported Production problem field locator: ${String(location.fieldLocator)}`);
  }

  const expectedPrefix = location.scope === 'Line'
    ? 'line.'
    : location.scope === 'Product'
      ? 'product.'
      : null;
  if (expectedPrefix === null && location.fieldLocator !== null) {
    throw new Error(`${location.scope} problems must select their graph entity without a field locator.`);
  }
  if (expectedPrefix !== null
      && location.fieldLocator !== null
      && !location.fieldLocator.startsWith(expectedPrefix)) {
    throw new Error(`${location.scope} problem field locator must start with '${expectedPrefix}'.`);
  }
}

function isScope(value: unknown): value is ProductionDesignerProblemScope {
  return typeof value === 'string' && scopes.some(scope => scope === value);
}

function isFieldLocator(value: unknown): value is ProductionDesignerProblemFieldLocator {
  return typeof value === 'string'
    && fieldLocators.has(value as ProductionDesignerProblemFieldLocator);
}

interface FocusableProblemField extends Element {
  scrollIntoView(options?: ScrollIntoViewOptions | boolean): void;
  focus(options?: FocusOptions): void;
}
