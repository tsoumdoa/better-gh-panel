import { spawn, spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const desktopFixtures = new Set([
  'highlight-overlap', 'item-order-capture', 'preview-safety', 'preview-reentrant-draw',
  'preview-native-resources', 'preview-conduit-resources', 'preview-single-pass-meshing', 'preview-single-pass-geometry',
  'preview-compatibility-draw', 'preview-compatibility-conduit', 'preview-retirement-seam', 'preview-stock-compatibility-draw', 'preview-stock-point'
]);
// These comparison captures are opt-in. Their results are diagnostic evidence
// and must not replace any required Panel+ fixture.
const diagnosticFixtures = new Set(['preview-stock-compatibility-draw', 'preview-compatibility-conduit']);

export const fixtureHostMode = fixture => desktopFixtures.has(fixture) ? 'inactive-desktop' : 'hidden';
export const fixtureCommand = (executable, fixture, plugin) =>
  [executable, [fixtureHostMode(fixture) === 'inactive-desktop' ? '--desktop-fixture' : '--fixture', fixture, plugin]];

export function loadInventory() {
  const fixtures = JSON.parse(readFileSync(join(here, 'fixtures.json'), 'utf8'));
  if (!Array.isArray(fixtures) || fixtures.length === 0 || fixtures.some(f => typeof f !== 'string' || !/^[a-z0-9-]+$/.test(f)) || new Set(fixtures).size !== fixtures.length)
    throw new Error('Invalid or duplicate native fixture inventory');
  return fixtures;
}

export const requiredFixtures = inventory => inventory.filter(name => !diagnosticFixtures.has(name));

const fileHash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const samePath = (a, b) => typeof a === 'string' && isAbsolute(a) &&
  (process.platform === 'win32' ? resolve(a).toLowerCase() === resolve(b).toLowerCase() : resolve(a) === resolve(b));

// A report is proof of the particular process and the actual two loaded payload files,
// not merely a marker left by a previous invocation or a successful startup assertion.
export function reportErrors(report, expected) {
  const errors = [];
  if (!report || typeof report !== 'object' || Array.isArray(report)) return ['malformed report'];
  if (report.schemaVersion !== 1) errors.push('wrong report schema');
  if (report.fixture !== expected.fixture) errors.push('wrong fixture');
  if (report.hostMode !== expected.hostMode) errors.push('wrong host mode');
  if (report.runId !== expected.runId) errors.push('wrong or stale run id');
  const started = typeof report.startedUtc === 'string' ? Date.parse(report.startedUtc) : NaN;
  if (!Number.isFinite(started) || started < expected.started - 2000 || started > Date.now() + 2000) errors.push('stale or invalid start time');
  if (report.completed !== true) errors.push('fixture did not complete');
  if (report.passed !== true || report.phase !== 'fixture-completed') errors.push('fixture reported failure');
  if (!Number.isSafeInteger(report.assertionCount) || report.assertionCount < 1) errors.push('no valid fixture assertions');
  for (const name of ['plugin', 'core']) {
    const actual = report[name];
    if (!actual || typeof actual !== 'object' || !samePath(actual.path, expected[name].path)) errors.push(`wrong loaded ${name} path`);
    if (!actual || typeof actual.sha256 !== 'string' || actual.sha256 !== expected[name].sha256) errors.push(`wrong loaded ${name} SHA-256`);
  }
  return errors;
}

function runChild(command, args, env, logPath, timeoutMs) {
  return new Promise(resolveResult => {
    const log = openSync(logPath, 'wx');
    let child;
    try { child = spawn(command, args, { env, stdio: ['ignore', log, log], windowsHide: true }); }
    catch (error) { closeSync(log); resolveResult({ error: error.message }); return; }
    let timedOut = false;
    let spawnError;
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, timeoutMs);
    child.on('error', error => { spawnError = error.message; });
    child.on('close', (code, signal) => {
      clearTimeout(timer);
      closeSync(log);
      resolveResult({ code, signal, timedOut, error: spawnError });
    });
  });
}

// commandFor is injectable solely so managed tests can exercise real child-process failures.
export async function runFixtures({ fixtures, pluginPath, outputRoot, timeoutMs, commandFor, quiet = false }) {
  const plugin = resolve(pluginPath);
  const core = join(dirname(plugin), 'BetterGhPanel.Core.dll');
  if (!existsSync(plugin) || !existsSync(core)) throw new Error(`Requested plugin and adjacent Core are required: ${plugin}, ${core}`);
  if (!Array.isArray(fixtures) || fixtures.length === 0 || new Set(fixtures).size !== fixtures.length || fixtures.some(f => typeof f !== 'string' || !/^[a-z0-9-]+$/.test(f)))
    throw new Error('Fixtures must be a nonempty, unique list of valid names');
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1) throw new Error('Timeout must be a positive integer in milliseconds');
  mkdirSync(outputRoot, { recursive: true });
  const runDirectory = mkdtempSync(join(resolve(outputRoot), 'run-'));
  const payload = { plugin: { path: plugin, sha256: fileHash(plugin) }, core: { path: core, sha256: fileHash(core) } };
  const outcomes = [];
  if (!quiet) console.log(`Native fixture results: ${runDirectory}`);
  for (const fixture of fixtures) {
    const runId = randomUUID();
    const resultPath = join(runDirectory, `${fixture}-${runId}.json`);
    const logPath = join(runDirectory, `${fixture}-${runId}.log`);
    const started = Date.now();
    const [command, args] = commandFor(fixture, plugin);
    const childEnvironment = { ...process.env, PANEL_SMOKE_RESULT: resultPath, PANEL_SMOKE_RUN_ID: runId };
    delete childEnvironment.PANEL_SMOKE_DESKTOP_NAME;
    const child = await runChild(command, args, childEnvironment, logPath, timeoutMs);
    const errors = [];
    if (child.error) errors.push(`child failed to start: ${child.error}`);
    if (child.timedOut) errors.push(`timeout after ${timeoutMs} ms`);
    if (child.signal || child.code !== 0) errors.push(`native exit code ${child.code}, signal ${child.signal ?? 'none'}`);
    let report;
    try { report = JSON.parse(readFileSync(resultPath, 'utf8')); }
    catch (error) { errors.push(error.code === 'ENOENT' ? 'missing report' : `malformed report: ${error.message}`); }
    if (report !== undefined) errors.push(...reportErrors(report, { fixture, runId, started, hostMode: fixtureHostMode(fixture), ...payload }));
    const outcome = { fixture, runId, resultPath, logPath, report, errors };
    outcomes.push(outcome);
    if (!quiet) console.log(`${errors.length ? 'FAIL' : 'PASS'} ${fixture}: ${errors.length ? errors.join('; ') : `${report.assertionCount} fixture assertions`} (${basename(logPath)})`);
  }
  return { runDirectory, outcomes, passed: outcomes.every(outcome => outcome.errors.length === 0) };
}

export async function main(args, overrides = {}) {
  let pluginPath = resolve(here, '../../src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha');
  let timeoutMs = 180000;
  const selected = [];
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--plugin' && args[i + 1]) pluginPath = resolve(args[++i]);
    else if (args[i] === '--timeout-ms' && args[i + 1]) timeoutMs = Number(args[++i]);
    else if (args[i] === '--fixture' && args[i + 1]) selected.push(args[++i]);
    else throw new Error(`Unknown or incomplete option: ${args[i]}`);
  }
  const inventory = loadInventory();
  if (selected.some(name => !inventory.includes(name))) throw new Error(`Unknown fixture: ${selected.find(name => !inventory.includes(name))}`);
  const fixtures = selected.length ? selected : requiredFixtures(inventory);
  if (!selected.length) console.log(`Optional preview diagnostics (run explicitly with --fixture): ${[...diagnosticFixtures].join(', ')}`);
  let desktopCheckPassed = true;
  if (fixtures.some(name => desktopFixtures.has(name))) {
    const checkDesktop = overrides.checkDesktop ?? (() => {
      const check = spawnSync(process.execPath, ['--test', join(here, '../desktop-launcher.test.mjs')], { stdio: 'inherit', windowsHide: true });
      return !check.error && check.status === 0;
    });
    try { desktopCheckPassed = await checkDesktop(); } catch { desktopCheckPassed = false; }
    if (!desktopCheckPassed) console.error('Inactive desktop launcher checks failed; fixtures will still run.');
  }
  const executable = join(here, 'bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe');
  const summary = await runFixtures({ fixtures, pluginPath,
    outputRoot: overrides.outputRoot ?? join(here, 'bin/Release/net7.0-windows/runs'), timeoutMs,
    commandFor: overrides.commandFor ?? ((fixture, plugin) => fixtureCommand(executable, fixture, plugin)) });
  console.log(`${summary.outcomes.filter(result => result.errors.length === 0).length}/${summary.outcomes.length} fixtures passed`);
  return summary.passed && desktopCheckPassed ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).then(code => { process.exitCode = code; }, error => { console.error(error.message); process.exitCode = 1; });
}
