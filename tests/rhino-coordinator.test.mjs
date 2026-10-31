import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { fixtureCommand, fixtureHostMode, main, reportErrors, runFixtures } from './BetterGhPanel.RhinoSmoke/coordinator.mjs';

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

test('pixel fixtures use an inactive desktop while other fixtures use the hidden host', () => {
  for (const name of ['preview-safety', 'preview-reentrant-draw', 'preview-native-resources',
    'preview-compatibility-draw', 'preview-retirement-seam', 'preview-additional-draw']) {
    assert.equal(fixtureHostMode(name), 'inactive-desktop');
    assert.deepEqual(fixtureCommand('host', name, 'plugin'), ['host', ['--desktop-fixture', name, 'plugin']]);
  }
  assert.equal(fixtureHostMode('preview-geometry'), 'hidden');
  assert.deepEqual(fixtureCommand('host', 'preview-geometry', 'plugin'), ['host', ['--fixture', 'preview-geometry', 'plugin']]);
});

test('CLI accepts desktop and hidden fixture reports in the same run', async t => {
  const payload = withPayload(t);
  const started = [];
  const code = await main(['--plugin', payload.pluginPath, '--fixture', 'preview-safety', '--fixture', 'preview-geometry'], {
    outputRoot: payload.outputRoot,
    commandFor: (fixture, plugin) => {
      started.push(fixture);
      return [process.execPath, [fakeChild, fixture === 'preview-safety' ? 'pass-desktop' : 'pass', fixture, plugin]];
    }
  });
  assert.equal(code, 0);
  assert.deepEqual(started, ['preview-safety', 'preview-geometry']);
  const [runDirectory] = readdirSync(payload.outputRoot);
  const status = JSON.parse(readFileSync(join(payload.outputRoot, runDirectory, 'coordinator-status.json'), 'utf8'));
  assert.equal(status.passed, true);
  assert.equal(status.fixtureCount, 2);
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
    const persisted = JSON.parse(readFileSync(outcome.outcomePath, 'utf8'));
    assert.equal(persisted.fixture, outcome.fixture);
    assert.equal(persisted.runId, outcome.runId);
    assert.equal(persisted.child.code, 0);
    assert.equal(persisted.child.signal, null);
    assert.equal(persisted.assertionCount, 3);
    assert.deepEqual(persisted.errors, []);
  }
  const persistedRun = JSON.parse(readFileSync(join(summary.runDirectory, 'run-summary.json'), 'utf8'));
  assert.equal(persistedRun.completed, true);
  assert.equal(persistedRun.passed, true);
  assert.deepEqual(persistedRun.outcomes.map(item => item.outcomePath), summary.outcomes.map(item => item.outcomePath));
});

test('payload identity accepts an alias of the same files and still compares hashes', async t => {
  const payload = withPayload(t);
  const targetDirectory = resolve(payload.pluginPath, '..');
  const aliasDirectory = join(targetDirectory, '..', 'panel-payload-alias-' + process.pid + '-' + Date.now());
  try { symlinkSync(targetDirectory, aliasDirectory, process.platform === 'win32' ? 'junction' : 'dir'); }
  catch (error) { t.skip(`directory aliases unavailable: ${error.message}`); return; }
  t.after(() => { try { unlinkSync(aliasDirectory); } catch (error) { if (error.code !== 'ENOENT') throw error; } });
  const aliasedPlugin = join(aliasDirectory, 'BetterGhPanel.gha');
  const summary = await runFixtures({ pluginPath: aliasedPlugin, outputRoot: payload.outputRoot,
    fixtures: ['first'], timeoutMs: 2000, quiet: true,
    commandFor: (fixture, plugin) => [process.execPath, [fakeChild, 'pass', fixture, plugin]] });
  assert.equal(summary.passed, true);
  const outcome = summary.outcomes[0];
  const persisted = JSON.parse(readFileSync(outcome.outcomePath, 'utf8'));
  assert.equal(persisted.payload.plugin.path, realpathSync.native(payload.pluginPath));
  const report = { ...outcome.report,
    plugin: { ...outcome.report.plugin, path: aliasedPlugin },
    core: { ...outcome.report.core, path: join(aliasDirectory, 'BetterGhPanel.Core.dll') } };
  const expected = { fixture: outcome.fixture, runId: outcome.runId, started: Date.parse(report.startedUtc),
    hostMode: 'hidden', ...persisted.payload };
  assert.deepEqual(reportErrors(report, expected), []);
  report.plugin.sha256 = '0'.repeat(64);
  assert.ok(reportErrors(report, expected).includes('wrong loaded plugin SHA-256'));
});

test('report validation rejects invalid identity, completion and assertion evidence', t => {
  const { pluginPath } = withPayload(t);
  const started = Date.now();
  const report = {
    schemaVersion: 1, fixture: 'first', runId: 'current-run', startedUtc: new Date(started).toISOString(),
    completed: true, passed: true, assertionCount: 3, phase: 'fixture-completed', hostMode: 'hidden', rhinoIdlePump: false,
    plugin: { path: pluginPath, sha256: 'plugin-hash' },
    core: { path: join(pluginPath, '..', 'BetterGhPanel.Core.dll'), sha256: 'core-hash' }
  };
  const expected = { ...report, started };
  assert.deepEqual(reportErrors(report, expected), []);
  for (const value of [null, [], 'invalid']) assert.deepEqual(reportErrors(value, expected), ['malformed report']);
  for (const [change, error] of [
    [{ schemaVersion: 2 }, 'wrong report schema'],
    [{ fixture: 'other' }, 'wrong fixture'],
    [{ hostMode: 'inactive-desktop' }, 'wrong host mode'],
    [{ rhinoIdlePump: true }, 'wrong Rhino idle pump'],
    [{ runId: 'old-run' }, 'wrong or stale run id'],
    [{ startedUtc: '2001-01-01T00:00:00Z' }, 'stale or invalid start time'],
    [{ completed: false }, 'fixture did not complete'],
    [{ passed: false }, 'fixture reported failure'],
    [{ phase: 'fixture-failed' }, 'fixture reported failure'],
    [{ assertionCount: 0 }, 'no valid fixture assertions'],
    [{ assertionCount: '3' }, 'no valid fixture assertions'],
    ...['plugin', 'core'].flatMap(name => [
      [{ [name]: { ...report[name], path: report[name].path + '.other' } }, `wrong loaded ${name} path`],
      [{ [name]: { ...report[name], sha256: 'wrong-hash' } }, `wrong loaded ${name} SHA-256`]
    ])
  ]) assert.ok(reportErrors({ ...report, ...change }, expected).includes(error), error);
});

// Keep process tests for failures that report validation alone cannot exercise.
for (const [behavior, expected] of [
  ['missing', 'missing report'], ['malformed', 'malformed report'], ['wrong-fixture', 'wrong fixture'],
  ['native-failure', 'native exit code'], ['timeout', 'timeout after']
]) {
  test(`rejects ${behavior} and still runs the next fixture`, async t => {
    const summary = await run(withPayload(t), behavior, behavior === 'timeout' ? 1000 : 2000);
    assert.equal(summary.passed, false);
    assert.ok(summary.outcomes[0].errors.some(error => error.includes(expected)), summary.outcomes[0].errors.join('; '));
    assert.deepEqual(summary.outcomes[1].errors, []);
    const persisted = JSON.parse(readFileSync(summary.outcomes[0].outcomePath, 'utf8'));
    assert.ok(persisted.errors.some(error => error.includes(expected)));
    if (behavior === 'native-failure') {
      assert.equal(persisted.child.code, 7);
      assert.equal(persisted.assertionCount, 3); // a passing host report cannot hide a failed exit
    }
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
  for (const args of [['--bad-mode'], ['--fixture', 'bogus'], ['--suite', 'typo']]) {
    const result = spawnSync(process.execPath, [coordinator, ...args], { encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Unknown/);
  }
});

test('coordinator CLI exits nonzero on a failed child after running the next fixture', async t => {
  const { spawnSync } = await import('node:child_process');
  const payload = withPayload(t);
  const driver = fileURLToPath(new URL('./fake-coordinator-driver.mjs', import.meta.url));
  for (const [behavior, expectedStatus] of [['pass', 0], ['native-failure', 1]]) {
    const result = spawnSync(process.execPath, [driver, behavior, payload.pluginPath, payload.outputRoot], { encoding: 'utf8' });
    assert.equal(result.status, expectedStatus, result.stderr);
    assert.match(result.stdout, /PASS preview-geometry/);
    if (expectedStatus) assert.match(result.stdout, /FAIL capture-safety/);
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

for (const behavior of ['startup-failure', 'startup-timeout']) {
  test(`${behavior} stops further Rhino launches and preserves an incomplete failed run`, async t => {
    const payload = withPayload(t);
    const started = [];
    const summary = await runFixtures({ ...payload, fixtures: ['preview-reentrant-draw', 'preview-geometry'],
      timeoutMs: behavior === 'startup-timeout' ? 1000 : 2000, quiet: true,
      commandFor: (fixture, plugin) => {
        started.push(fixture);
        return [process.execPath, [fakeChild, behavior, fixture, plugin]];
      }
    });
    assert.deepEqual(started, ['preview-reentrant-draw']);
    assert.equal(summary.passed, false);
    assert.equal(summary.completed, false);
    assert.deepEqual(summary.notRunFixtures, ['preview-geometry']);
    assert.match(summary.outcomes[0].errors[0], /Rhino host failed before fixture execution/);
    assert.ok(!summary.outcomes[0].errors.includes('wrong Rhino idle pump'));
    const persisted = JSON.parse(readFileSync(summary.outcomes[0].outcomePath, 'utf8'));
    assert.equal(persisted.assertionCount, 0);
    assert.equal(persisted.phase, behavior === 'startup-failure' ? 'host-startup-failed' : 'host-startup');
    if (behavior === 'startup-failure') assert.match(persisted.failure, /COMException: E_FAIL/);
    else assert.equal(persisted.child.timedOut, true);
    const runReport = JSON.parse(readFileSync(join(summary.runDirectory, 'run-summary.json'), 'utf8'));
    assert.equal(runReport.completed, false);
    assert.equal(runReport.passed, false);
    assert.deepEqual(runReport.notRunFixtures, ['preview-geometry']);
    assert.match(runReport.stopReason, /Rhino startup failed/);
  });
}

test('CLI marks a startup-aborted inventory incomplete and exits unsuccessfully', async t => {
  const payload = withPayload(t);
  const code = await main(['--plugin', payload.pluginPath, '--fixture', 'preview-reentrant-draw', '--fixture', 'preview-geometry'], {
    outputRoot: payload.outputRoot,
    commandFor: (fixture, plugin) => [process.execPath, [fakeChild, 'startup-failure', fixture, plugin]]
  });
  assert.equal(code, 1);
  const [runDirectory] = readdirSync(payload.outputRoot);
  const status = JSON.parse(readFileSync(join(payload.outputRoot, runDirectory, 'coordinator-status.json'), 'utf8'));
  assert.equal(status.passed, false);
  assert.equal(status.completed, false);
  assert.equal(status.fixtureCount, 1);
  assert.equal(status.requestedFixtureCount, 2);
  assert.deepEqual(status.notRunFixtures, ['preview-geometry']);
});
