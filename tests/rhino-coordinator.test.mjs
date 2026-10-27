import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { fixtureCommand, fixtureHostMode, loadInventory, main, requiredFixtures, runFixtures } from './BetterGhPanel.RhinoSmoke/coordinator.mjs';

const fakeChild = fileURLToPath(new URL('./fake-rhino-child.mjs', import.meta.url));

function withPayload(t) {
  const dir = mkdtempSync(join(tmpdir(), 'panel-native-coordinator-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const pluginPath = join(dir, 'BetterGhPanel.gha');
  writeFileSync(pluginPath, 'plugin bytes');
  writeFileSync(join(dir, 'BetterGhPanel.Core.dll'), 'core bytes');
  return { pluginPath, outputRoot: join(dir, 'results') };
}

function run(payload, behavior, timeoutMs = 2000) {
  return runFixtures({ ...payload, fixtures: ['first', 'after'], timeoutMs, quiet: true,
    commandFor: (fixture, plugin) => [process.execPath, [fakeChild, fixture === 'first' ? behavior : 'pass', fixture, plugin]] });
}

test('inventory includes every original fixture and the independently crash-prone portions', () => {
  assert.deepEqual(loadInventory(), [
    'panel-handle', 'output', 'output-pending', 'row-numbering',
    'index-selection', 'capsule', 'capsule-double-click', 'recompute-presentation',
    'recompute-output', 'recompute-regex', 'recompute-local-preview', 'recompute-disabled',
    'geometry-hint', 'analytic-curve-details', 'geometry-group-persistent', 'geometry-group-volatile', 'geometry-group-cycles',
    'capture-safety', 'polyline-safety', 'preview-safety', 'preview-geometry', 'preview-additional-geometry', 'preview-additional-draw', 'clipboard-interop-diagnostic', 'preview-reentrant-draw',
    'preview-metadata-safety', 'preview-native-resources', 'preview-conduit-resources', 'preview-single-pass-meshing',
    'preview-single-pass-geometry', 'preview-compatibility-draw', 'preview-compatibility-conduit', 'preview-retirement-seam', 'preview-stock-compatibility-draw', 'preview-stock-point',
    'nurbs-value-candidate', 'production-curve-measurement', 'standalone-compatibility', 'capture-transaction',
    'geometry-canvas-viewport', 'measurement-priority', 'measurement-cancellation',
    'measurement-invalidation', 'measurement-failures', 'row-actions', 'highlight-overlap',
    'filter-metadata', 'filter-measurements', 'filter-cancellation', 'filter-snapshot-replacement',
    'filter-lifecycle-paths', 'filter-displayed-output', 'filter-displayed-geometry', 'filter-ui', 'filter-ui-choices', 'full-value', 'view-freshness',
    'filter-footer-statistics', 'filter-preview-undo', 'filter-display-values', 'statistics-ui',
    'create-set', 'set-details', 'display-set', 'item-order-ui',
    'item-order-capture', 'integration'
  ]);
});

test('comparison diagnostics are opt-in and do not replace required preview fixtures', () => {
  const inventory = loadInventory();
  const required = requiredFixtures(inventory);
  assert.ok(inventory.includes('preview-stock-compatibility-draw'));
  assert.ok(!required.includes('preview-stock-compatibility-draw'));
  assert.ok(inventory.includes('preview-compatibility-conduit'));
  assert.ok(!required.includes('preview-compatibility-conduit'));
  assert.ok(inventory.includes('clipboard-interop-diagnostic'));
  assert.ok(!required.includes('clipboard-interop-diagnostic'));
  for (const fixture of ['preview-compatibility-draw', 'preview-native-resources', 'preview-conduit-resources'])
    assert.ok(required.includes(fixture));
});

test('pixel fixtures use an inactive desktop while other fixtures use the hidden host', () => {
  for (const name of ['highlight-overlap', 'item-order-capture', 'preview-safety', 'preview-reentrant-draw',
    'preview-native-resources', 'preview-conduit-resources', 'preview-single-pass-meshing', 'preview-single-pass-geometry',
    'preview-compatibility-draw', 'preview-compatibility-conduit', 'preview-retirement-seam', 'preview-stock-compatibility-draw', 'preview-stock-point', 'preview-additional-draw']) {
    assert.equal(fixtureHostMode(name), 'inactive-desktop');
    assert.deepEqual(fixtureCommand('host', name, 'plugin'), ['host', ['--desktop-fixture', name, 'plugin']]);
  }
  assert.equal(fixtureHostMode('output'), 'hidden');
  assert.deepEqual(fixtureCommand('host', 'output', 'plugin'), ['host', ['--fixture', 'output', 'plugin']]);
});

test('a failed desktop launcher check fails the run without skipping other fixtures', async t => {
  const payload = withPayload(t);
  const started = [];
  const code = await main(['--plugin', payload.pluginPath, '--fixture', 'highlight-overlap', '--fixture', 'output'], {
    outputRoot: payload.outputRoot, checkDesktop: () => false,
    commandFor: (fixture, plugin) => {
      started.push(fixture);
      return [process.execPath, [fakeChild, fixture === 'highlight-overlap' ? 'pass-desktop' : 'pass', fixture, plugin]];
    }
  });
  assert.equal(code, 1);
  assert.deepEqual(started, ['highlight-overlap', 'output']);
});

test('successful children provide unique fixture-only evidence and unique files', async t => {
  const summary = await run(withPayload(t), 'pass');
  assert.equal(summary.passed, true);
  assert.equal(summary.outcomes.length, 2);
  assert.notEqual(summary.outcomes[0].runId, summary.outcomes[1].runId);
  assert.notEqual(summary.outcomes[0].resultPath, summary.outcomes[1].resultPath);
  assert.notEqual(summary.outcomes[0].logPath, summary.outcomes[1].logPath);
  for (const outcome of summary.outcomes) {
    assert.deepEqual(outcome.errors, []);
    assert.equal(JSON.parse(readFileSync(outcome.resultPath, 'utf8')).fixture, outcome.fixture);
    assert.equal(outcome.report.assertionCount, 3);
  }
});

for (const [behavior, expected] of [
  ['missing', 'missing report'], ['malformed', 'malformed report'], ['wrong-fixture', 'wrong fixture'], ['wrong-host-mode', 'wrong host mode'],
  ['wrong-idle-pump', 'wrong Rhino idle pump'],
  ['wrong-id', 'wrong or stale run id'], ['stale', 'stale or invalid start time'],
  ['incomplete', 'fixture did not complete'], ['no-assertions', 'no valid fixture assertions'],
  ['bad-assertions', 'no valid fixture assertions'], ['bad-schema', 'wrong report schema'],
  ['failed-check', 'fixture reported failure'], ['wrong-plugin-path', 'wrong loaded plugin path'],
  ['wrong-plugin-hash', 'wrong loaded plugin SHA-256'], ['wrong-core-path', 'wrong loaded core path'],
  ['wrong-core-hash', 'wrong loaded core SHA-256'], ['native-failure', 'native exit code'],
  ['timeout', 'timeout after']
]) {
  test(`rejects ${behavior} and still runs the next fixture`, async t => {
    const summary = await run(withPayload(t), behavior, behavior === 'timeout' ? 1000 : 2000);
    assert.equal(summary.passed, false);
    assert.ok(summary.outcomes[0].errors.some(error => error.includes(expected)), summary.outcomes[0].errors.join('; '));
    assert.deepEqual(summary.outcomes[1].errors, []);
  });
}

test('rejects invalid fixture lists and timeout before starting children', async t => {
  const payload = withPayload(t);
  const commandFor = () => { throw new Error('must not start'); };
  await assert.rejects(runFixtures({ ...payload, fixtures: [], timeoutMs: 1, commandFor }), /nonempty/);
  await assert.rejects(runFixtures({ ...payload, fixtures: ['same', 'same'], timeoutMs: 1, commandFor }), /unique/);
  await assert.rejects(runFixtures({ ...payload, fixtures: ['../bad'], timeoutMs: 1, commandFor }), /valid names/);
  await assert.rejects(runFixtures({ ...payload, fixtures: ['valid'], timeoutMs: 0, commandFor }), /Timeout/);
});

test('CLI rejects unknown modes without starting Rhino', async () => {
  const { spawnSync } = await import('node:child_process');
  const coordinator = resolve(fileURLToPath(new URL('./BetterGhPanel.RhinoSmoke/coordinator.mjs', import.meta.url)));
  for (const args of [['--bad-mode'], ['--fixture', 'bogus']]) {
    const result = spawnSync(process.execPath, [coordinator, ...args], { encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Unknown/);
  }
});

test('coordinator CLI exits nonzero on a failed child after running the next fixture', async t => {
  const { spawnSync } = await import('node:child_process');
  const payload = withPayload(t);
  const driver = fileURLToPath(new URL('./fake-coordinator-driver.mjs', import.meta.url));
  for (const [behavior, expectedStatus] of [['pass', 0], ...[
    'missing', 'malformed', 'wrong-fixture', 'wrong-host-mode', 'wrong-idle-pump', 'wrong-id', 'stale', 'incomplete', 'no-assertions', 'bad-assertions',
    'bad-schema', 'failed-check', 'wrong-plugin-path', 'wrong-plugin-hash', 'wrong-core-path', 'wrong-core-hash',
    'native-failure', 'timeout'
  ].map(name => [name, 1])]) {
    const result = spawnSync(process.execPath, [driver, behavior, payload.pluginPath, payload.outputRoot], { encoding: 'utf8' });
    assert.equal(result.status, expectedStatus, result.stderr);
    assert.match(result.stdout, /PASS output/);
    if (expectedStatus) assert.match(result.stdout, /FAIL panel-handle/);
  }
});

test('a child that cannot start fails without suppressing the next fixture', async t => {
  const payload = withPayload(t);
  const summary = await runFixtures({ ...payload, fixtures: ['first', 'after'], timeoutMs: 2000, quiet: true,
    commandFor: (fixture, plugin) => fixture === 'first'
      ? [join(payload.outputRoot, 'nonexistent-executable'), []]
      : [process.execPath, [fakeChild, 'pass', fixture, plugin]] });
  assert.equal(summary.passed, false);
  assert.ok(summary.outcomes[0].errors.some(error => error.includes('failed to start')));
  assert.deepEqual(summary.outcomes[1].errors, []);
});
