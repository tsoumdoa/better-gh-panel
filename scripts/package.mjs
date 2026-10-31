#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { join, resolve, sep } from 'node:path';
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
  const manifestTemplate = readFileSync(manifestPath, 'utf8');
  if (!version || !/^\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?$/.test(version) ||
      !/^version: "@VERSION@"$/m.test(manifestTemplate)) {
    throw new Error('Expected a semantic Version in Directory.Build.props and a version placeholder in yak/manifest.yml.');
  }
  const manifest = manifestTemplate.replace(/^version: "@VERSION@"$/m, `version: ${version}`);
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
  const stage = join(root, 'artifacts', `panel-plus-${version}`);
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
  for (const asset of ['assets/icon.png', 'LICENSE']) {
    const file = join(root, asset);
    if (!existsSync(file) || !statSync(file).isFile() || statSync(file).size === 0)
      throw new Error(`Missing packaging asset: ${file}`);
  }
  // Yak packages every file in its working directory. Reject unexpected root
  // entries before modifying a previous stage instead of shipping them silently.
  if (existsSync(stage)) {
    const allowed = new Set(['manifest.yml', 'icon.png', 'LICENSE', ...outputs.map(output => output.framework)]);
    const unexpected = readdirSync(stage).filter(name => !allowed.has(name) &&
      !(name.startsWith(`panel-plus-${version}-`) && name.endsWith('.yak') && statSync(join(stage, name)).isFile()));
    if (unexpected.length) throw new Error(`Unexpected staging entries: ${unexpected.join(', ')}. Move them out of ${stage} before packaging.`);
  }
  mkdirSync(stage, { recursive: true });
  writeFileSync(join(stage, 'manifest.yml'), manifest);
  copyFileSync(join(root, 'assets', 'icon.png'), join(stage, 'icon.png'));
  copyFileSync(join(root, 'LICENSE'), join(stage, 'LICENSE'));
  for (const { framework, source, names } of outputs) {
    const folder = resolve(stage, framework);
    if (!folder.startsWith(resolve(root, 'artifacts') + sep)) throw new Error(`Invalid staging directory: ${folder}`);
    // These generated runtime directories contain only this build's payload.
    rmSync(folder, { recursive: true, force: true });
    mkdirSync(folder, { recursive: true });
    for (const name of names) copyFileSync(join(source, name), join(folder, name));
  }
  console.log(`Staged ${version} at ${stage}`);
  if (values['stage-only']) process.exit(0);
  // Replace only this version's generated archives; preserve staged payload files.
  for (const name of readdirSync(stage)) {
    if (name.startsWith(`panel-plus-${version}-`) && name.endsWith('.yak')) {
      unlinkSync(join(stage, name));
    }
  }
  run(yak, ['build', '--platform', 'any'], stage);
  const packages = readdirSync(stage).filter(name => name.startsWith(`panel-plus-${version}-`) && name.endsWith('.yak'));
  if (packages.length !== 1) throw new Error(`Expected one Yak archive, found ${packages.length}.`);
  console.log(`Local package ready: ${join(stage, packages[0])}`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
