#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
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
