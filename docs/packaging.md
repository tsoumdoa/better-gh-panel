# Build and Yak packaging

BetterGhPanel follows HopperWire's Rhino 8 package layout and AECTooling metadata. The package contains `net48/` for Rhino's .NET Framework mode on Windows and `net7.0/` for Rhino's .NET mode on Windows and Mac. The pure data engine targets `netstandard2.0` and is shipped beside each `.gha`. Rhino supplies Grasshopper, RhinoCommon, Eto, System.Drawing, and WinForms; those SDK assemblies are compile references and are not copied into the package.

Run these commands from the repository root:

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

Packaging finds Yak in the standard Rhino 8 installation. On a machine without Rhino, `node scripts/package.mjs --stage-only` builds the same folder without creating an archive. Use `--yak <absolute path>` or `YAK_PATH` for a custom installation. Use `--skip-build` only after a successful Release build.

The current beta patch is `0.1.20-beta`. The resulting `artifacts/better-gh-panel-0.1.20-beta/` contains `manifest.yml`, `icon.png`, and the two runtime folders. Yak builds a `better-gh-panel-0.1.20-beta-rh8_0-any.yak` archive there. No command in the packaging script signs in or publishes. `npm run check` runs the core, packaging, and unused-code checks and stages both runtimes locally on Windows or Mac; it does not install or launch Rhino. GitHub Actions CI is disabled.

To install a local package, close Rhino first, then run Yak using the artifact folder as the package source:

```text
yak install --source artifacts/better-gh-panel-0.1.20-beta better-gh-panel 0.1.20-beta
```

Replace `yak` with the full executable path if it is not on PATH. On Windows it is normally `C:\Program Files\Rhino 8\System\Yak.exe`; on Mac it is normally `/Applications/Rhino 8.app/Contents/Resources/bin/yak`. Restart Rhino and open Grasshopper afterward. Local install uses Yak's package directory and avoids copying a second `.gha` into the Grasshopper Libraries folder.

Release validation covers both runtime builds, 23 managed test groups, packaging regression, and Windows Rhino 8.35 .NET 7 host checks. Filter checks cover downstream typed output, whitespace-only regex patterns, internalised preview exclusion, disabled-state cancellation and recovery, numeric and geometry comparisons, full-precision converted thresholds, offscreen measurement, cancellation, bounded regex execution, compact menu entry, error recovery and saved modes. Highlight checks cover actual viewport rendering over upstream previews, native selected colors, Selected/Filtered results/Off scopes, offscreen matches, empty and failed queries, custom internalised preview, selection without camera movement, explicit framing, saved preview modes, undo/redo and document lifecycle. Row-action checks cover multi-row query-order selection, active camera framing and locked views, numeric-only converted values, offscreen measurement batches, cancellation and stale-copy rejection, dropdown/display double-click guards, clipboard preservation on failure, source/output invariance, and actual Rhino Paste of multiple geometry types including surfaces and SubD. Native geometry copying supports Windows with standard model units; Mac geometry clipboard and custom units are unsupported. Geometry checks cover automatic visible-row requests, deferred UI preparation, background computation on owned copies, viewport queue replacement, offscreen and partially visible panels, canvas panning and cached resumption, bounded concurrency, successful/failed caching, trimmed area and solid volume, dimension/source-unit guards, mm³ ↔ m³, pending and completed metric retention across unit changes, preserved raw values, unchanged geometry output, decimal formatting without a solution, invalid text labels and reasons, obsolete views and disposal. Display checks cover Full ↔ Half, undo/redo, serialization and migration of previously Collapsed panels to Full. Existing reset, compact menu/paging, query, tree-tool, conversion and typed-output checks remain part of the host suite. Mac and Windows .NET Framework host validation remain pending.

With Rhino/Grasshopper installed, run the managed tree checks without starting Rhino:

```text
dotnet build tests/BetterGhPanel.RhinoSmoke -c Release
tests/BetterGhPanel.RhinoSmoke/bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe --tree-only
```

Install the current public beta with:

```text
yak install better-gh-panel 0.1.20-beta
```

Beta `0.1.20-beta` republishes the Create set feature with the next beta version; behavior is unchanged from `0.1.19-beta.3`. The package is rebuilt for both runtimes and validated from its final archive.

Patch `0.1.19-beta.3` adds a Create set tree tool beside Reverse. Managed tree checks cover exact numeric/text/primitive equality, stable first occurrences, nulls, invalid and unsupported data, empty branches, combinations with other tools and a 100,000-item list. Native checks cover typed output, branch filters, save/load, legacy defaults, Undo/Redo, Reset, and Full/minimum Half controls. Release validation runs both hidden Rhino host suites against the plugin extracted from the final Yak archive.

Patch `0.1.19-beta.2` uses zero-based item-order labels within each branch and adds a visible Select all button for all current matching rows. Native checks cover duplicate branch indices, filtering and sorting without renumbering, transformed-tree indices, offscreen selection, stale/disabled/failed query guards, Full/Half/capsule interactions, and unchanged output and camera.

Patch `0.1.19-beta.1` removes the unused inline-path column from grouped source-order rows and shrinks their numbering gutter to the original index alone. Sorted rows keep their view numbers and addresses; compact panels retain inline paths when branch headings do not fit. Validation includes the local checks, both runtime builds, managed Grasshopper tree checks, and both hidden Rhino host suites against the plugin extracted from the final Yak archive.

Beta `0.1.19-beta` adds selection of the same zero-based item indices across branches through the row menu or Alt/Option-click. Managed checks cover distinct indices, short/empty branches, sorting, filter scope, range anchors and new-input cleanup. Windows host checks cover actual pointer dispatch and Alt state, offscreen geometry preview, unchanged camera and output, multi-index expansion, minimum Half menu bounds, trailing double-clicks and stale/disabled actions. The full suite runs against the plugin extracted from the final Yak archive; Mac and Windows .NET Framework host validation remain pending.

Beta `0.1.18-beta` adds row deselection by double-click or empty-space click, padded panel move/resize handles, item/branch counts, exact unique numeric/text frequencies, compact capsule minimization and optional viewport item-order labels. Host checks cover native pointer dispatch, minimum Half menus, filtered statistics, presentation-only undo/redo, capsule restoration, saved settings, stale-result cleanup and viewport label visibility. `tests/BetterGhPanel.RhinoSmoke/run.ps1` runs the full behavior suite and the order-label capture in separate hidden Rhino processes because running both capture fixtures in the same hidden host crashes the Rhino 8.35 display pipeline. Both processes must pass assertions and exit normally. Release validation uses the plug-in extracted from the final Yak archive, passed with `-PluginPath`.

Beta `0.1.17-beta` adds automatic one-based row positions in Asc/Desc beside smaller original item indices. The native renderer uses a fixed full-tree gutter, shared text baselines and right-aligned numeric values without adding a toggle or changing the row height. Host checks cover sort directions, selection retention, row jumps beyond the viewport, filtered numbering, fixed gutter capacity, source order, output invariance and the minimum Half layout.

Beta `0.1.16-beta` adds Selected (default), Filtered results and Off preview modes, keeps the camera still on selection, and provides explicit Frame selection. It also fixes custom preview framing, pending copy status on document switches and row actions against disabled or stale results. Beta `0.1.15-beta` adds current-result loading states, a resumable 120 ms query budget, and filtered typed output after tree tools and unit conversion. It also fixes whitespace-regex output, internalised preview filtering and disabled-state animation. Beta `0.1.14-beta` is a version-only update of `0.1.13-beta.1` with no behavior changes. Beta `0.1.13-beta.1` refreshes the tree-tool icons and aligns the footer. Beta `0.1.12-beta.1` adds selected geometry highlighting, branch framing and measurement/regex filters. These and `0.1.11-beta.1` are the 2026-10-11 patches. Beta `0.1.10-beta.1` was published on 2026-10-10. Previous betas `0.1.9-beta.1`, `0.1.8-beta.1` and `0.1.7-beta.1` were published on 2026-10-09. Betas `0.1.6-beta.1`, `0.1.5-beta.1` and `0.1.4-beta.1` were published on 2026-10-08. Earlier betas, `0.1.3-beta.1` and `0.1.2-beta.1`, were published on 2026-10-06 and 2026-10-05 respectively.

In Rhino 8's `PackageManager`, enable prereleases to find the beta. For CLI discovery, use `yak search --all --prerelease better-gh-panel`.

The build version lives in `Directory.Build.props`. Packaging fills the `@VERSION@` placeholder in `yak/manifest.yml` and recreates the generated runtime staging directories after verifying the build outputs, so obsolete DLLs cannot enter a rebuilt package. Before a stable release, test Rhino 8 on Windows in both .NET and .NET Framework modes, and on Mac in .NET mode. Verify empty input, mixed/null data, large trees, document removal, saved settings, each tree tool and their combinations, unchanged upstream data, and unchanged typed output with tools off.

Publication was an explicit `yak push` of the validated archive. GitHub releases and future public versions are separate release actions; local build and check scripts never publish automatically.
