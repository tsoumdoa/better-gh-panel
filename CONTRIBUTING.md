# Contributing

```text
npm run check
```

Runs the core tests, native coordinator and packaging checks, builds and stages both Rhino runtimes, flags unused private members, usings and parameters (not unused internal or public members), and checks that C# source is UTF-8. On Windows with Rhino 8 installed it also compiles and style-checks the native smoke host (skipped otherwise), so don't run it while a native run is in progress. It never launches Rhino. Run it before opening or merging a PR; GitHub Actions CI is disabled.

To build a Yak package, run `dotnet build BetterGhPanel.sln -c Release` then `node scripts/package.mjs`. During development, run affected native Rhino regressions with `tests/BetterGhPanel.RhinoSmoke/run.ps1 -Fixture ui-ux,row-selection` (choose names from `fixtures.json`). Run the full inventory for broad runtime changes and releases; launcher self-tests run separately. See the [validation instructions](#validation) for commands and optional diagnostics. See also:

- [Build, packaging and release validation](docs/packaging.md)
- [Current architecture](docs/architecture.md)
- [Geometry measurement](docs/geometry-measurement-safety.md) and [preview](docs/geometry-preview-safety.md) safety

The original interactive HTML design study is preserved in `prototype/` (`npm start`, then <http://localhost:5173>); it uses generated data and is a visual reference only.


## Validation

During development, run the native fixtures affected by the change with `-Fixture`: for example, `ui-ux,row-selection` for canvas interactions, `property-inspection` for inspection properties, or the relevant capture/measurement/preview fixtures for runtime changes. Changes across the shared host, capture or preview lifecycle warrant the full inventory. Release validation requires the full inventory against the plugin extracted from the final Yak archive. Core.Tests exercises the deterministic number pipeline on Windows .NET Framework and net8.0; native Mac runtime validation remains a separate coverage gap.

```powershell
# Build the current plugin before testing it; run.ps1 builds only the test host and Core.
dotnet build BetterGhPanel.sln -c Release
tests/BetterGhPanel.RhinoSmoke/run.ps1 -Fixture ui-ux,row-selection

# Release validation (also run launcher checks when changing the host or runner):
dotnet build tests/BetterGhPanel.RhinoSmoke -c Release
node --test tests/desktop-launcher.test.mjs
tests/BetterGhPanel.RhinoSmoke/run.ps1 -PluginPath <extracted-plugin-path>
```

The current inventory contains 24 fixtures. The full run starts one sequential Rhino process per entry in `tests/BetterGhPanel.RhinoSmoke/fixtures.json`; each has a timeout and unique result and log under `bin/Release/net7.0-windows/runs`. Viewport drawing fixtures use a normal window on a private inactive desktop and RhinoCore's native message loop, including its idle processing; the rest use a hidden Rhino host. The desktop host requests Rhino shutdown before posting the loop's quit message, then disposes normally. A Windows job terminates the desktop fixture process when the coordinator times out. Launcher self-tests run separately, so individual viewport runs do not repeat the launcher timeout probe.

Reports record the host mode, idle integration, loaded plugin and Core paths and SHA-256, and fixture assertion count. The coordinator rejects incomplete, failed, stale, mismatched, empty or malformed reports and abnormal process exits. Fixture failures allow the remaining fixtures to run; an identified host startup failure stops further launches and records which fixtures did not run. `-TimeoutSeconds` changes the per-process timeout. `--identity-only <fixture> <plugin-path>` on the host executable writes the pre-start identity report without starting Rhino; it remains incomplete and is not a fixture pass. The build removes any stale host-local Core copy so the host can bind the Core next to the requested plugin.

For capture changes, also use `--tree-only <plugin-path>` on the host executable to run managed capture safety and group-cycle checks with the installed SDK assemblies, without starting Rhino. It shares the managed safety suite with `capture-safety` and adds a focused cycle/search check.

The suite checks capture freshness, ownership, preview disposal, drawing and Paste, output publication, live-search history and row hits. Property capture uses representative values per handler and a settings round trip.

For clipboard transport diagnosis only, set `$env:PANEL_SMOKE_RAW_CLIPBOARD = '1'` before running `-Fixture preview-geometry`, then remove it with `Remove-Item Env:PANEL_SMOKE_RAW_CLIPBOARD`. This adds the separate-process raw-byte/hash probe. Normal runs still verify clipboard archive contents and actual Rhino Paste.

The full inventory takes roughly three minutes for 24 fixtures, one Rhino process per fixture. Run only one native inventory at a time: the Windows clipboard is shared across worktrees.

If a host fails in `host-startup`, save the run directory and check for a startup dialog or Windows Application Error before rerunning. After saving work and closing interactive Rhino sessions, reproduce just the affected fixture with `run.ps1 -PluginPath <path> -Fixture preview-admission-freshness -TimeoutSeconds 45` (substitute the recorded fixture name). A passing repeat establishes recovery only; it does not establish the earlier cause. Run the complete inventory against the same extracted package before treating validation as complete. Do not change installed runtimes, plug-ins or user settings solely because startup failed.

## Release

Follow [build, staging, Yak and installation](docs/packaging.md).
