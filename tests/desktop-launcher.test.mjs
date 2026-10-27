import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const executable = fileURLToPath(new URL('./BetterGhPanel.RhinoSmoke/bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe', import.meta.url));
const windowsOnly = { skip: process.platform !== 'win32' };

test('inactive desktop launcher preserves success and failure exit codes', windowsOnly, () => {
  for (const [mode, expected] of [['ok', 0], ['fail', 7]]) {
    const child = spawnSync(executable, ['--desktop-probe', mode], { encoding: 'utf8', windowsHide: true, timeout: 10000 });
    assert.equal(child.status, expected, child.stderr || child.error?.message);
    assert.match(child.stdout, /Inactive desktop probe ready/);
  }
});

test('a claimed desktop must match the actual thread desktop', windowsOnly, () => {
  const child = spawnSync(executable, ['--desktop-probe-child', 'ok'], {
    encoding: 'utf8', windowsHide: true, timeout: 10000,
    env: { ...process.env, PANEL_SMOKE_DESKTOP_NAME: 'PanelSmoke_not_the_current_desktop' }
  });
  assert.equal(child.status, 2, child.stderr || child.error?.message);
  assert.doesNotMatch(child.stdout, /Inactive desktop probe ready/);
});

test('terminating the desktop launcher also terminates its fixture process', windowsOnly, async () => {
  const child = spawnSync(executable, ['--desktop-probe', 'wait'], { encoding: 'utf8', windowsHide: true, timeout: 5000 });
  assert.equal(child.error?.code, 'ETIMEDOUT', child.stderr);
  assert.match(child.stdout, /Inactive desktop probe ready/);
  const match = /Inactive desktop fixture process: (\d+)/.exec(child.stdout);
  assert.ok(match, child.stdout);
  const pid = Number(match[1]);
  let running = true;
  for (let attempt = 0; attempt < 30; attempt++) {
    try { process.kill(pid, 0); }
    catch (error) {
      if (error.code !== 'ESRCH') throw error;
      running = false;
      break;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (running) {
    try { process.kill(pid); } catch { /* It may have exited after the last check. */ }
  }
  assert.equal(running, false, `Timed-out fixture process ${pid} is still running`);
});
