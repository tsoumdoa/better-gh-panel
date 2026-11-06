# Build and Yak packaging

Panel+ (`panel-plus`) follows HopperWire's Rhino 8 package layout and AECTooling metadata. The package contains `net48/` for Rhino's .NET Framework mode on Windows and `net7.0/` for Rhino's .NET mode on Windows and Mac. The pure data engine targets `netstandard2.0` and is shipped beside each `.gha`. Rhino supplies Grasshopper, RhinoCommon, Eto, System.Drawing, and WinForms; those SDK assemblies are compile references and are not copied into the package.

Run these commands from the repository root:

```text
dotnet build BetterGhPanel.sln -c Release
dotnet test BetterGhPanel.sln -c Release
node scripts/package.mjs
```

Packaging finds Yak in the standard Rhino 8 installation. On a machine without Rhino, `node scripts/package.mjs --stage-only` builds the same folder without creating an archive. Use `--yak <absolute path>` or `YAK_PATH` for a custom installation. Use `--skip-build` only after a successful Release build.

`Directory.Build.props` maps the checkout root to `/_/` in debug records, keeping local usernames and checkout paths out of newly built assemblies and PDBs. Rebuild before packaging.

New builds use the Yak package name `panel-plus` and stage to `artifacts/panel-plus-<version>/`. The old published package retains its original name; installing the renamed package may require removing the old package first to avoid loading both copies.

To install a local package, close Rhino first, then run Yak using the artifact folder as the package source:

```text
yak install --source artifacts/panel-plus-0.2.6-beta panel-plus 0.2.6-beta
```

Replace `yak` with the full executable path if it is not on PATH. On Windows it is normally `C:\Program Files\Rhino 8\System\Yak.exe`; on Mac it is normally `/Applications/Rhino 8.app/Contents/Resources/bin/yak`. Restart Rhino and open Grasshopper afterward. Local install uses Yak's package directory and avoids copying a second `.gha` into the Grasshopper Libraries folder.

With Rhino/Grasshopper installed, run managed capture safety checks without starting Rhino:

```text
dotnet build tests/BetterGhPanel.RhinoSmoke -c Release
tests/BetterGhPanel.RhinoSmoke/bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe --tree-only
```

Install the current public beta with:

```text
yak install panel-plus 0.2.6-beta
```

The release notes below record the validation and host modes used at the time. For the current isolated fixture runner, see [validation](maintainability.md#validation).

Validation: `npm run check`, managed `--tree-only`, and focused `--sets-only`, `--outputs-only`, and `--filters-only` against the final extracted Yak archive. The set host checks numeric and converted geometry outputs, equal displayed lengths, empty branches, list counts before grouping, precision changes, filters, source masks/pairs, selection, persistence, Undo/Redo, occurrence tables and Full/Half canvas rendering. Both runtime payloads build; host execution uses Windows .NET 7 and Rhino 8.36. The existing full viewport capture crash described below still limits full-suite validation.

For beta.6, run `npm run check`, then the managed tree checks and focused `--filters-only` and `--outputs-only` host modes against the extracted final Yak package. The full hidden-host suite on Rhino 8.36.26293.11001 hits a native `CRhinoDisplayPipeline_DrawShadedMeshes` access violation in the viewport overlap capture; the previously published beta.5 GitHub package reproduces the same failure. Full native viewport validation is therefore incomplete on this host. Focused filter/UI checks include all display-value interactions, asynchronous measurements, converted circumference, empty branches, persistence and Undo/Redo. Mac and Windows .NET Framework host execution remain untested.

In Rhino 8's `PackageManager`, enable prereleases to find the beta. For CLI discovery, use `yak search --all --prerelease better-gh-panel`.

The build version lives in `Directory.Build.props`. Packaging fills the `@VERSION@` placeholder in `yak/manifest.yml` and recreates the generated runtime staging directories after verifying the build outputs, so obsolete DLLs cannot enter a rebuilt package. Before a stable release, test Rhino 8 on Windows in both .NET and .NET Framework modes, and on Mac in .NET mode. Verify empty input, mixed/null data, large trees, document removal, saved settings, each tree tool and their combinations, unchanged upstream data, and unchanged typed output with tools off.

Publication was an explicit `yak push` of the validated archive. GitHub releases and future public versions are separate release actions; local build and check scripts never publish automatically.


## Record validation

Run the [contributor checks](../CONTRIBUTING.md) and the full native inventory against the plugin extracted from the final Yak archive. Record source commit, archive SHA-256, runtime, fixture results and coverage gaps in [release validation](release-validation.md), newest first. Publish only the validated archive.
