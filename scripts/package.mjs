#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, statSync, unlinkSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const root = fileURLToPath(new URL('../', import.meta.url));
try {
  const { values } = parseArgs({ options: {
    'stage-only': { type: 'boolean' }, 'skip-build': { type: 'boolean' },
    yak: { type: 'string' }, help: { type: 'boolean', short: 'h' },
  } });
  if (values.help) {
    console.log('Usage: node scripts/package.mjs [--stage-only] [--skip-build] [--yak <executable>]\n\nBuilds Release net48 + net7.0 and creates a Rhino 8 Windows/Mac Yak package.\n--stage-only prepares artifacts without requiring Rhino/Yak.\nYAK_PATH overrides the default Rhino 8 installation. This script never publishes.');
    process.exit(0);
  }
  const version = readFileSync(join(root, 'Directory.Build.props'), 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1];
  const manifestPath = join(root, 'yak', 'manifest.yml');
  const manifest = readFileSync(manifestPath, 'utf8');
  if (!version || manifest.match(/^version:\s*(.+)$/m)?.[1].trim() !== version) {
    throw new Error('Directory.Build.props and yak/manifest.yml versions must match.');
  }
  const yak = resolve(values.yak || process.env.YAK_PATH || (process.platform === 'darwin'
    ? '/Applications/Rhino 8.app/Contents/Resources/bin/yak'
    : join(process.env.ProgramFiles || 'C:\\Program Files', 'Rhino 8', 'System', 'Yak.exe')));
  if (!values['stage-only'] && !existsSync(yak)) {
    throw new Error('Yak was not found. Use --yak <path>, YAK_PATH, or --stage-only.');
  }
  function run(command, args, cwd = root) {
    const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: false });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`${command} failed with exit code ${result.status}.`);
  }
  if (!values['skip-build']) run('dotnet', ['build', 'src/BetterGhPanel/BetterGhPanel.csproj', '-c', 'Release', '--nologo']);
  const stage = join(root, 'artifacts', `better-gh-panel-${version}`);
  const outputs = ['net48', 'net7.0'].map(framework => {
    const source = join(root, 'src', 'BetterGhPanel', 'bin', 'Release', framework);
    const names = ['BetterGhPanel.gha', 'BetterGhPanel.Core.dll'];
    if (framework === 'net7.0') names.push('BetterGhPanel.deps.json');
    for (const name of names) {
      const file = join(source, name);
      if (!existsSync(file) || !statSync(file).isFile() || statSync(file).size === 0) {
        throw new Error(`Missing build output: ${file}`);
      }
    }
    return { framework, source, names };
  });
  mkdirSync(stage, { recursive: true });
  copyFileSync(manifestPath, join(stage, 'manifest.yml'));
  copyFileSync(join(root, 'assets', 'icon.png'), join(stage, 'icon.png'));
  for (const { framework, source, names } of outputs) {
    const folder = join(stage, framework);
    mkdirSync(folder, { recursive: true });
    for (const name of names) copyFileSync(join(source, name), join(folder, name));
  }
  console.log(`Staged ${version} at ${stage}`);
  if (values['stage-only']) process.exit(0);
  // Replace only this version's generated archives; preserve staged payload files.
  for (const name of readdirSync(stage)) {
    if (name.startsWith(`better-gh-panel-${version}-`) && name.endsWith('.yak')) {
      unlinkSync(join(stage, name));
    }
  }
  run(yak, ['build', '--platform', 'any'], stage);
  const packages = readdirSync(stage).filter(name => name.startsWith(`better-gh-panel-${version}-`) && name.endsWith('.yak'));
  if (packages.length !== 1) throw new Error(`Expected one Yak archive, found ${packages.length}.`);
  console.log(`Local package ready: ${join(stage, packages[0])}`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
