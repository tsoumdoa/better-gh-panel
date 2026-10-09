# Build and Yak packaging

BetterGhPanel follows HopperWire's Rhino 8 package layout and AECTooling metadata. The package contains `net48/` for Rhino's .NET Framework mode on Windows and `net7.0/` for Rhino's .NET mode on Windows and Mac. The pure data engine targets `netstandard2.0` and is shipped beside each `.gha`. Rhino supplies Grasshopper, RhinoCommon, Eto, System.Drawing, and WinForms; those SDK assemblies are compile references and are not copied into the package.

Run these commands from the repository root:

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

Packaging finds Yak in the standard Rhino 8 installation. On a machine without Rhino, `node scripts/package.mjs --stage-only` builds the same folder without creating an archive. Use `--yak <absolute path>` or `YAK_PATH` for a custom installation. Use `--skip-build` only after a successful Release build.

The current beta patch is `0.1.9-beta.1`. The resulting `artifacts/better-gh-panel-0.1.9-beta.1/` contains `manifest.yml`, `icon.png`, and the two runtime folders. Yak builds a `better-gh-panel-0.1.9-beta.1-rh8_0-any.yak` archive there. No command in the packaging script signs in or publishes. `npm run check` runs the core, packaging, and unused-code checks and stages both runtimes locally on Windows or Mac; it does not install or launch Rhino. GitHub Actions CI is disabled.

To install a local package, close Rhino first, then run Yak using the artifact folder as the package source:

```text
yak install --source artifacts/better-gh-panel-0.1.9-beta.1 better-gh-panel 0.1.9-beta.1
```

Replace `yak` with the full executable path if it is not on PATH. On Windows it is normally `C:\Program Files\Rhino 8\System\Yak.exe`; on Mac it is normally `/Applications/Rhino 8.app/Contents/Resources/bin/yak`. Restart Rhino and open Grasshopper afterward. Local install uses Yak's package directory and avoids copying a second `.gha` into the Grasshopper Libraries folder.

Current beta validation: both runtime builds passed without warnings, core checks and packaging regression passed, managed net48 tree checks passed, and the full Windows Rhino 8.35 .NET 7 host checks passed. Geometry checks cover synchronous selected-row Measure clicks, zero calculations on hover/selection, successful/failed caching, trimmed area and solid volume, dimension/source-unit guards, mm³ ↔ m³, converted metric formatting without remeasurement, preserved raw values, unchanged geometry output, decimal formatting without a solution, invalid text labels and reasons, obsolete views and disposal. Existing reset, compact menu/paging, display, serialization, query, tree-tool, conversion and typed-output checks also pass. Mac and Windows .NET Framework host validation remain pending.

With Rhino/Grasshopper installed, run the managed tree checks without starting Rhino:

```text
dotnet build tests/BetterGhPanel.RhinoSmoke -c Release
tests/BetterGhPanel.RhinoSmoke/bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe --tree-only
```

Install the current public beta with:

```text
yak install better-gh-panel 0.1.9-beta.1
```

Previous betas `0.1.8-beta.1` and `0.1.7-beta.1` were published on 2026-10-09. Betas `0.1.6-beta.1`, `0.1.5-beta.1` and `0.1.4-beta.1` were published on 2026-10-08. Earlier betas, `0.1.3-beta.1` and `0.1.2-beta.1`, were published on 2026-10-06 and 2026-10-05 respectively.

In Rhino 8's `PackageManager`, enable prereleases to find the beta. For CLI discovery, use `yak search --all --prerelease better-gh-panel`.

The build version lives in `Directory.Build.props`. Packaging fills the `@VERSION@` placeholder in `yak/manifest.yml` and recreates the generated runtime staging directories after verifying the build outputs, so obsolete DLLs cannot enter a rebuilt package. Before a stable release, test Rhino 8 on Windows in both .NET and .NET Framework modes, and on Mac in .NET mode. Verify empty input, mixed/null data, large trees, document removal, saved settings, each tree tool and their combinations, unchanged upstream data, and unchanged typed output with tools off.

Publication was an explicit `yak push` of the validated archive. GitHub releases and future public versions are separate release actions; local build and check scripts never publish automatically.
