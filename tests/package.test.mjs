import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

test('staging derives the version, drops obsolete payloads, and preserves a stage when inputs are missing', t => {
  const prefix = join(resolve(tmpdir()), 'panel-package-');
  const fixture = mkdtempSync(prefix);
  t.after(() => {
    assert.ok(resolve(fixture).startsWith(prefix) && dirname(fixture) === resolve(tmpdir()));
    rmSync(fixture, { recursive: true, force: true });
  });
  function write(path, contents) {
    const destination = join(fixture, path);
    mkdirSync(dirname(destination), { recursive: true });
    writeFileSync(destination, contents);
  }
  write('Directory.Build.props', '<Project><PropertyGroup><Version>1.2.3-beta.4</Version></PropertyGroup></Project>');
  write('yak/manifest.yml', 'name: better-gh-panel\nversion: "@VERSION@"\n');
  write('assets/icon.png', 'fixture icon');
  write('LICENSE', 'fixture MIT license');
  write('scripts/package.mjs', '');
  copyFileSync(fileURLToPath(new URL('../scripts/package.mjs', import.meta.url)), join(fixture, 'scripts/package.mjs'));
  const payloads = { net48: ['BetterGhPanel.gha', 'BetterGhPanel.Core.dll'],
    'net7.0': ['BetterGhPanel.gha', 'BetterGhPanel.Core.dll', 'BetterGhPanel.deps.json'] };
  for (const [framework, names] of Object.entries(payloads))
    for (const name of names) write(`src/BetterGhPanel/bin/Release/${framework}/${name}`, `${framework} ${name}`);

  const stage = join(fixture, 'artifacts/better-gh-panel-1.2.3-beta.4');
  write('artifacts/better-gh-panel-1.2.3-beta.4/net48/obsolete.dll', 'obsolete');
  write('artifacts/better-gh-panel-1.2.3-beta.4/net7.0/obsolete/nested.dll', 'obsolete');
  const run = () => spawnSync(process.execPath, [join(fixture, 'scripts/package.mjs'), '--stage-only', '--skip-build'], { encoding: 'utf8' });
  const first = run();
  assert.equal(first.status, 0, first.stderr);
  assert.match(readFileSync(join(stage, 'manifest.yml'), 'utf8'), /^version: 1\.2\.3-beta\.4$/m);
  assert.equal(readFileSync(join(stage, 'LICENSE'), 'utf8'), 'fixture MIT license');
  assert.deepEqual(readdirSync(stage).sort(), ['LICENSE', 'icon.png', 'manifest.yml', 'net48', 'net7.0'].sort());
  for (const [framework, names] of Object.entries(payloads))
    assert.deepEqual(readdirSync(join(stage, framework)).sort(), [...names].sort());

  write('artifacts/better-gh-panel-1.2.3-beta.4/net48/obsolete.dll', 'obsolete');
  assert.equal(run().status, 0);
  assert.ok(!existsSync(join(stage, 'net48/obsolete.dll')));

  // A failed source validation must leave the last complete stage intact.
  const missing = join(fixture, 'src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.Core.dll');
  assert.ok(missing.startsWith(resolve(fixture) + sep));
  rmSync(missing);
  const failed = run();
  assert.notEqual(failed.status, 0);
  assert.match(failed.stderr, /Missing build output/);
  assert.equal(readFileSync(join(stage, 'net7.0/BetterGhPanel.Core.dll'), 'utf8'), 'net7.0 BetterGhPanel.Core.dll');
  write('src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.Core.dll', 'replacement build');

  // A stray root DLL or file would otherwise be included by Yak. Refuse the
  // whole stage before changing its previous payload, and preserve user files.
  for (const name of ['obsolete.dll', 'accidental.txt', 'unexpected/nested.txt']) {
    write(`artifacts/better-gh-panel-1.2.3-beta.4/${name}`, 'unexpected');
    const rejected = run();
    assert.notEqual(rejected.status, 0);
    assert.match(rejected.stderr, /Unexpected staging entries/);
    assert.equal(readFileSync(join(stage, name), 'utf8'), 'unexpected');
    assert.equal(readFileSync(join(stage, 'net7.0/BetterGhPanel.Core.dll'), 'utf8'), 'net7.0 BetterGhPanel.Core.dll');
    const extra = join(stage, name.split('/')[0]);
    assert.ok(extra.startsWith(resolve(fixture) + sep));
    rmSync(extra, { recursive: true, force: true });
  }
  assert.equal(run().status, 0);
  assert.equal(readFileSync(join(stage, 'net7.0/BetterGhPanel.Core.dll'), 'utf8'), 'replacement build');
});
