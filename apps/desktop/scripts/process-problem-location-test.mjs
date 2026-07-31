import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const source = await readFile(
  new URL('../src/renderer/process-problem-location.ts', import.meta.url),
  'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'process-problem-location.ts'
}).outputText;
const location = await import(
  `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

const issues = [
  issue('Processes.GraphStartNodeCountInvalid', 'Graph', 'flow.main'),
  issue('Processes.NodeUnreachable', 'Node', 'node.orphan'),
  issue('Processes.TransitionTargetMissing', 'Transition', 'transition.to-missing')
];
const problems = location.createProcessEditorProblems(issues);

assert.deepEqual(
  problems.map(problem => location.parseProcessProblemLocation(problem.targetId)),
  [
    { targetKind: 'Graph', targetId: 'flow.main' },
    { targetKind: 'Node', targetId: 'node.orphan' },
    { targetKind: 'Transition', targetId: 'transition.to-missing' }
  ]);
assert.notEqual(problems[1].targetId, problems[2].targetId);
assert.equal(location.parseProcessProblemLocation(null), null);
assert.equal(location.parseProcessProblemLocation('["Node",""]'), null);
assert.equal(location.parseProcessProblemLocation('["Unknown","node"]'), null);
assert.throws(
  () => location.createProcessEditorProblems([issue('bad', 'Unknown', 'node')]),
  /Unsupported process validation target kind/);

process.stdout.write('process problem location tests passed\n');

function issue(code, targetKind, targetId) {
  return {
    severity: 'Error',
    code,
    message: code,
    targetKind,
    targetId
  };
}
