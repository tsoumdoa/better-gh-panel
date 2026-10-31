#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';

const root = fileURLToPath(new URL('../', import.meta.url));
const sources = spawnSync('git', ['ls-files', '-z', 'src/**/*.cs', 'tests/**/*.cs'], { cwd: root, encoding: 'utf8' });
if (sources.status !== 0) throw new Error('Could not list tracked C# sources');
for (const path of sources.stdout.split('\0').filter(Boolean)) {
  try { new TextDecoder('utf-8', { fatal: true }).decode(readFileSync(new URL(path, new URL('../', import.meta.url)))); }
  catch (error) { throw new Error(`${path}: invalid UTF-8`, { cause: error }); }
}
const checks = [
  ['dotnet', ['test', 'BetterGhPanel.sln', '-c', 'Release', '--nologo']],
  [process.execPath, ['--test', 'tests/rhino-coordinator.test.mjs']],
  [process.execPath, ['--test', 'tests/package.test.mjs']],
  [process.execPath, ['scripts/package.mjs', '--stage-only']],
  ['dotnet', ['format', 'style', 'BetterGhPanel.sln', '--verify-no-changes', '--no-restore',
    '--diagnostics', 'IDE0005', 'IDE0051', 'IDE0052', 'IDE0059', 'IDE0060', '--severity', 'info']],
];

for (const [command, args] of checks) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit' });
  if (result.error) console.error(result.error.message);
  if (result.status !== 0) process.exit(result.status ?? 1);
}
