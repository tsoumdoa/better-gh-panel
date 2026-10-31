import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const desktopFixtures = new Set([
  'preview-safety', 'preview-reentrant-draw', 'preview-native-resources',
  'preview-compatibility-draw', 'preview-retirement-seam', 'preview-additional-draw'
]);
const startupPhases = new Set(['identity', 'host-startup', 'host-startup-failed', 'grasshopper-startup', 'host-preparation-failed']);

export const fixtureHostMode = fixture => desktopFixtures.has(fixture) ? 'inactive-desktop' : 'hidden';
export const fixtureCommand = (executable, fixture, plugin) =>
  [executable, [fixtureHostMode(fixture) === 'inactive-desktop' ? '--desktop-fixture' : '--fixture', fixture, plugin]];

export function loadInventory() {
  const fixtures = JSON.parse(readFileSync(join(here, 'fixtures.json'), 'utf8'));
  if (!Array.isArray(fixtures) || fixtures.length === 0 || fixtures.some(f => typeof f !== 'string' || !/^[a-z0-9-]+$/.test(f)) || new Set(fixtures).size !== fixtures.length)
    throw new Error('Invalid or duplicate native fixture inventory');
  return fixtures;
}

const fileHash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const samePath = (a, b) => {
  if (typeof a !== 'string' || !isAbsolute(a) || typeof b !== 'string' || !isAbsolute(b)) return false;
  try {
    const left = realpathSync.native(a), right = realpathSync.native(b);
    return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right;
  } catch { return false; }
};

// A report is proof of the particular process and the actual two loaded payload files,
// not merely a marker left by a previous invocation or a successful startup assertion.
export function reportErrors(report, expected) {
  const errors = [];
  if (!report || typeof report !== 'object' || Array.isArray(report)) return ['malformed report'];
  if (report.schemaVersion !== 1) errors.push('wrong report schema');
  if (report.fixture !== expected.fixture) errors.push('wrong fixture');
  if (report.hostMode !== expected.hostMode) errors.push('wrong host mode');
  // A host that never reached its message loop has no idle pump to validate yet.
  if ((!startupPhases.has(report.phase) || report.completed === true || report.rhinoIdlePump === true) &&
      report.rhinoIdlePump !== (expected.hostMode === 'inactive-desktop')) errors.push('wrong Rhino idle pump');
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
  const requested = resolve(pluginPath);
  if (!existsSync(requested)) throw new Error(`Requested plugin and adjacent Core are required: ${requested}`);
  const plugin = realpathSync.native(requested);
  const adjacentCore = join(dirname(plugin), 'BetterGhPanel.Core.dll');
  if (!existsSync(adjacentCore)) throw new Error(`Requested plugin and adjacent Core are required: ${plugin}, ${adjacentCore}`);
  const core = realpathSync.native(adjacentCore);
  if (!Array.isArray(fixtures) || fixtures.length === 0 || new Set(fixtures).size !== fixtures.length || fixtures.some(f => typeof f !== 'string' || !/^[a-z0-9-]+$/.test(f)))
    throw new Error('Fixtures must be a nonempty, unique list of valid names');
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1) throw new Error('Timeout must be a positive integer in milliseconds');
  mkdirSync(outputRoot, { recursive: true });
  const runDirectory = mkdtempSync(join(resolve(outputRoot), 'run-'));
  const payload = { plugin: { path: plugin, sha256: fileHash(plugin) }, core: { path: core, sha256: fileHash(core) } };
  const outcomes = [];
  let stopReason = null;
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
    const startupFailed = errors.length > 0 && report?.completed === false && report.assertionCount === 0 &&
      report.runId === runId && report.fixture === fixture && startupPhases.has(report.phase);
    if (startupFailed) errors.unshift(`Rhino host failed before fixture execution (${report.phase})${typeof report.failure === 'string' && report.failure ? `: ${report.failure.split(/\r?\n/)[0]}` : ''}`);
    const outcomePath = join(runDirectory, `${fixture}-${runId}.outcome.json`);
    const outcome = { fixture, runId, resultPath, logPath, outcomePath, child, report, errors };
    // The report is written by the host before shutdown, so it cannot attest
    // to the host's exit code. Persist the coordinator's assessment as well;
    // terminal output can disappear when an orchestration session reconnects.
    writeFileSync(outcomePath, JSON.stringify({ schemaVersion: 1, fixture, runId, resultPath, logPath,
      child, errors, payload, phase: report?.phase ?? null, failure: report?.failure ?? null,
      assertionCount: report?.assertionCount ?? null }), { flag: 'wx' });
    outcomes.push(outcome);
    if (!quiet) console.log(`${errors.length ? 'FAIL' : 'PASS'} ${fixture}: ${errors.length ? errors.join('; ') : `${report.assertionCount} fixture assertions`} (${basename(logPath)})`);
    if (startupFailed) {
      stopReason = `Rhino startup failed in ${fixture}; remaining fixtures were not launched. Resolve the host startup failure before rerunning.`;
      if (!quiet) console.error(stopReason);
      break;
    }
  }
  const notRunFixtures = fixtures.slice(outcomes.length);
  const completed = notRunFixtures.length === 0;
  const passed = completed && outcomes.every(outcome => outcome.errors.length === 0);
  writeFileSync(join(runDirectory, 'run-summary.json'), JSON.stringify({ schemaVersion: 1, completed,
    passed, payload, stopReason, notRunFixtures, outcomes: outcomes.map(({ fixture, runId, outcomePath }) => ({ fixture, runId, outcomePath })) }), { flag: 'wx' });
  return { runDirectory, outcomes, passed, completed, notRunFixtures, stopReason };
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
  const fixtures = selected.length ? selected : inventory;
  const executable = join(here, 'bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe');
  const summary = await runFixtures({ fixtures, pluginPath,
    outputRoot: overrides.outputRoot ?? join(here, 'bin/Release/net7.0-windows/runs'), timeoutMs,
    commandFor: overrides.commandFor ?? ((fixture, plugin) => fixtureCommand(executable, fixture, plugin)) });
  writeFileSync(join(summary.runDirectory, 'coordinator-status.json'), JSON.stringify({ schemaVersion: 1,
    passed: summary.passed, completed: summary.completed,
    fixtureCount: summary.outcomes.length, requestedFixtureCount: fixtures.length,
    notRunFixtures: summary.notRunFixtures, stopReason: summary.stopReason }), { flag: 'wx' });
  console.log(`${summary.outcomes.filter(result => result.errors.length === 0).length}/${fixtures.length} fixtures passed${summary.notRunFixtures.length ? `; ${summary.notRunFixtures.length} not run after Rhino startup failure` : ''}`);
  return summary.passed ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).then(code => { process.exitCode = code; }, error => { console.error(error.message); process.exitCode = 1; });
}
