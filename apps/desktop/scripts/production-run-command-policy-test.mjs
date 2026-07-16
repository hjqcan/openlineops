import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const source = await readFile(
  new URL('../src/renderer/production-run-command-policy.ts', import.meta.url),
  'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022
  },
  fileName: 'production-run-command-policy.ts'
}).outputText;
const policy = await import(
  `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

const operation = executionStatus => ({ executionStatus });
const run = (controlState, operations = [], isTerminal = false, executionStatus = 'Running') => ({
  controlState,
  operations,
  isTerminal,
  executionStatus
});

assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Active', [operation('Completed')])), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Paused', [operation('Completed')])), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Held')), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Held', [operation('Running')])), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Held', [operation('Completed')])), true);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Held', [operation('Completed')], true)), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('Held', [operation('Completed')], false, 'Pending')), false);
assert.equal(policy.canIssueProductionRunCommand(
  'Rework',
  run('RecoveryRequired', [operation('Running')])), false);

assert.equal(policy.canIssueProductionRunCommand('Pause', run('Active')), true);
assert.equal(policy.canIssueProductionRunCommand(
  'Pause', run('Active', [], false, 'Pending')), false);
assert.equal(policy.canIssueProductionRunCommand('Continue', run('Paused')), true);
assert.equal(policy.canIssueProductionRunCommand(
  'Hold', run('Active', [], false, 'Pending')), false);
assert.equal(policy.canIssueProductionRunCommand('Release', run('Held')), true);
assert.equal(policy.canIssueProductionRunCommand(
  'Release', run('Held', [], false, 'Pending')), false);

process.stdout.write('production run command policy tests passed\n');
