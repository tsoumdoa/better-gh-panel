# Contributing

```text
npm run check
```

Runs the core tests and packaging checks, builds and stages both Rhino runtimes, flags unused private members, usings and parameters (not unused internal or public members), and checks that C# source is UTF-8. On Windows with Rhino 8 installed it also compiles and style-checks the native smoke host (skipped otherwise), so don't run it while a native run is in progress. It never launches Rhino. Run it before opening or merging a PR; GitHub Actions CI is disabled.

To build a Yak package, run `dotnet build BetterGhPanel.sln -c Release` then `node scripts/package.mjs`. Run the native Rhino fixtures (`tests/BetterGhPanel.RhinoSmoke/run.ps1`) for capture, preview or host changes and for releases. See the [validation instructions](#validation) for commands. See also:

- [Build, packaging and release validation](docs/packaging.md)
- [Current architecture](docs/architecture.md)
- [Geometry measurement](docs/geometry-measurement-safety.md) and [preview](docs/geometry-preview-safety.md) safety

The original interactive HTML design study is preserved in `prototype/` (`npm start`, then <http://localhost:5173>); it uses generated data and is a visual reference only.


## Tests

Keep the suite small. Add a test only when it guards a contract that is hard to check by reading the code and costly to break silently:

- the presented-number pipeline (unit conversion, formula, round-up factor, decimals) and its agreement across display, filters, statistics and outputs on net48 and net8.0;
- query semantics (search, comparisons, regex, branch paths, sort stability, cancellation) and saved-setting compatibility;
- the measurement queue lifecycle (cancellation, replacement, disposal) and the preview memory budget;
- Rhino-hosted safety: capture must not mutate or hang on hostile input, and preview must release native resources.

Do not keep tests that only pinned a bug fix, a layout, a menu, a label or a performance counter once the change has shipped. Do not add test-only hooks to production code.

## Validation

Core.Tests runs without Rhino on net8.0 and, on Windows, net48. The native inventory in `tests/BetterGhPanel.RhinoSmoke/fixtures.json` has three fixtures, one Rhino process each: `capture-safety` (managed and native capture on hostile, grouped and cyclic input), `preview-safety` (preview mutation, budgets, actions, native resources and drawing, on a private inactive desktop) and `standalone-compatibility` (real Grasshopper solves through capture, measurement and outputs). Native Mac runtime validation remains a coverage gap.

```powershell
# Build the current plugin before testing it; run.ps1 builds only the test host and Core.
dotnet build BetterGhPanel.sln -c Release
tests/BetterGhPanel.RhinoSmoke/run.ps1                      # all fixtures
tests/BetterGhPanel.RhinoSmoke/run.ps1 -Fixture preview-safety

# Release validation, against the plugin extracted from the final Yak archive:
tests/BetterGhPanel.RhinoSmoke/run.ps1 -PluginPath <extracted-plugin-path>
```

Each fixture writes a result and log under `bin/Release/net7.0-windows/runs`. The coordinator rejects incomplete, failed, stale, mismatched or empty reports and abnormal exits; a host startup failure stops further launches. `-TimeoutSeconds` changes the per-process timeout. `--tree-only <plugin-path>` on the host executable runs the managed capture safety and group-cycle checks without starting Rhino.

Run only one native inventory at a time: the Windows clipboard and Rhino startup are shared across worktrees. Fixtures time out at host startup while the Windows session is disconnected (`query session` shows `Disc`). If a host fails in `host-startup`, check for a startup dialog or Windows Application Error, then rerun just that fixture. Do not change installed runtimes, plug-ins or user settings solely because startup failed.

## Release

Follow [build, staging, Yak and installation](docs/packaging.md).
