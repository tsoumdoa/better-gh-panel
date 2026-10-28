import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const [behavior, fixture, pluginArg] = process.argv.slice(2);
const plugin = resolve(pluginArg);
const core = join(dirname(plugin), 'BetterGhPanel.Core.dll');
const hash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const report = {
  schemaVersion: 1, fixture, runId: process.env.PANEL_SMOKE_RUN_ID, startedUtc: new Date().toISOString(),
  completed: true, passed: true, assertionCount: 3, phase: 'fixture-completed', hostMode: 'hidden', rhinoIdlePump: false,
  plugin: { path: plugin, sha256: hash(plugin) }, core: { path: core, sha256: hash(core) }
};
const result = process.env.PANEL_SMOKE_RESULT;
switch (behavior) {
  case 'pass': break;
  case 'pass-desktop': report.hostMode = 'inactive-desktop'; report.rhinoIdlePump = true; break;
  case 'wrong-idle-pump': report.rhinoIdlePump = true; break;
  case 'missing': process.exit(0); break;
  case 'malformed': writeFileSync(result, '{'); process.exit(0); break;
  case 'wrong-fixture': report.fixture = 'another-fixture'; break;
  case 'wrong-host-mode': report.hostMode = 'inactive-desktop'; break;
  case 'wrong-id': report.runId = 'old-run'; break;
  case 'stale': report.startedUtc = '2001-01-01T00:00:00Z'; break;
  case 'incomplete': report.completed = false; report.phase = 'fixture'; break;
  case 'no-assertions': report.assertionCount = 0; break;
  case 'bad-assertions': report.assertionCount = '3'; break;
  case 'bad-schema': report.schemaVersion = 2; break;
  case 'failed-check': report.passed = false; report.phase = 'fixture-failed'; break;
  case 'wrong-plugin-path': report.plugin.path += '.other'; break;
  case 'wrong-plugin-hash': report.plugin.sha256 = '0'.repeat(64); break;
  case 'wrong-core-path': report.core.path += '.other'; break;
  case 'wrong-core-hash': report.core.sha256 = '0'.repeat(64); break;
  case 'native-failure': break;
  case 'timeout': report.completed = false; report.phase = 'fixture'; break;
  case 'startup-failure':
  case 'startup-timeout':
    report.completed = false; report.passed = false; report.assertionCount = 0;
    report.phase = behavior === 'startup-failure' ? 'host-startup-failed' : 'host-startup';
    report.failure = behavior === 'startup-failure' ? 'COMException: E_FAIL\nStartupInProcess' : null;
    report.hostMode = 'inactive-desktop';
    break;
  default: throw new Error(`Unknown fake child behavior ${behavior}`);
}
writeFileSync(result, JSON.stringify(report));
if (behavior === 'timeout' || behavior === 'startup-timeout') setTimeout(() => {}, 30000);
else if (behavior === 'native-failure' || behavior === 'failed-check' || behavior === 'startup-failure') process.exitCode = 7;
