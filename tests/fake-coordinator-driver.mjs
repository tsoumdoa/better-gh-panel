import { fileURLToPath } from 'node:url';
import { main } from './BetterGhPanel.RhinoSmoke/coordinator.mjs';

const [behavior, pluginPath, outputRoot] = process.argv.slice(2);
const fakeChild = fileURLToPath(new URL('./fake-rhino-child.mjs', import.meta.url));
process.exitCode = await main(['--plugin', pluginPath, '--timeout-ms', behavior === 'timeout' ? '1200' : '2000', '--fixture', 'capture-safety', '--fixture', 'preview-geometry'], {
  outputRoot,
  commandFor: (fixture, plugin) => [process.execPath, [fakeChild, fixture === 'capture-safety' ? behavior : 'pass', fixture, plugin]]
});
