# Maintainability audit

This pass reduces duplicated feature definitions while keeping saved settings, typed output, query behavior and canvas interactions compatible. Geometry scheduling uses an owned queue; the session retains responsibility for query state and publication.

## Changes in 0.1.20-beta.4

- **Units:** `PanelUnits` defines each conversion's arithmetic, label, symbol, source units and geometry dimension together. Menu count comes from that catalog. Persisted enum values are explicit; append new values without renumbering existing definitions. Keep division as division because a reciprocal multiplication can change output precision. Unknown values retain the no-conversion fallback.
- **Query scope:** `PanelQueryScope` owns branch selection, branch-length restrictions, type and validity restrictions for both final queries and measurement candidates. Add cheap restrictions here so offscreen preparation follows the same scope as publication. Search predicates and measurement eligibility remain in `PanelSearchFilter`; sorting and aggregation remain in `PanelQueryEngine`. Branch-only filters retain empty branches, and list length uses transformed counts before item filtering.
- **Row actions:** named actions carry their labels and descriptions together; menu positions no longer determine which operation executes. The row-action partial groups the menu and copy-status lifecycle beside their dispatch. Add or remove a choice in its catalog and implement its named action without aligning parallel label, tooltip and numeric dispatch lists. Selection and stale-result guards remain owned by the canvas attributes.

## Feature change map

- Pure filtering, sorting, statistics and presentation policy belong in `BetterGhPanel.Core`. Its tests run without Rhino.
- Typed tree changes belong in `Runtime/PanelTreeOperations` or `PanelUnitOperations`; snapshots remain immutable managed records.
- Geometry preparation, measurement ownership and publication go through `PanelSession`. Workers must not read live goo or the Rhino document.
- Canvas policy belongs in `UI/`; Rhino event dispatch and drawing stay in `BetterPanelAttributes` and its feature partials. The core test project links pure UI policy explicitly.
- Saved settings, output publication and host lifetime belong to `BetterPanelComponent`. Removing a feature must account for older saved enum values and serialization keys before deleting them.
- `BetterPanelComponent.Outputs` owns the saved B/F scope, paired input cache and publication of D/S/B/F from one current snapshot. Component-owned `PanelRowSelection` notifies output scheduling; selection-only solutions reuse the snapshot. `PanelTreeOperations.ApplyWithSources` carries retained original addresses through every tree tool; filtering and selection share its tree-order traversal while retaining their different empty-branch behavior. `PanelOutputOperations` builds original-shape masks and paired trees without converting paired values. Match paths with `GH_Path.CompareTo`, since object equality does not compare their indices.
- Filtered D/B/F trees are cached per completed query result so selection gestures do not rebuild unchanged filters or masks. Selection notifications describe membership changes; preserving the same group or changing only the range anchor does not schedule output. Empty selections skip tree scans, and masks allocate one Boolean value per input item.
- `BetterPanelComponent.Serialization` upgrades only the known unversioned one-input/one-output archive before the host reads its parameters. It preserves D chunks/GUIDs and the original archive owner for relative paths and diagnostics, supplies defaults for the added ports, and leaves the source archive unchanged. New saves carry `PanelOutputSchema`; genuine missing chunks still report errors.
- Native wire picking uses a 12-unit radius and reverse parameter order, beyond the custom rectangle bounds. Port spacing must keep neighbouring grips outside that radius at each target edge and leave the Half resize corner clear. Presentation controls, rows, scroll tracks and menus stay at least 14 units inside the relevant side. The 96-high capsule gives its four outputs 24-unit spacing.

## Public-repository cleanup

Removed the unused `PanelSession.GetDisplayValue` / `SetDetailResolver` API and its live-goo callback. No production code called the lookup; the smoke assertions now exercise the canvas and copy paths used by the plug-in. This removes two public beta runtime members; consumers of those undocumented members must use captured row presentation instead. Saved Grasshopper definitions are unaffected.

Builds map the checkout root to `/_/` so assemblies and PDBs do not embed local user paths. Plug-in binaries, local environment files and signing keys are ignored. The archived prototype server now listens on loopback.

The 2026-10-24 audit found no credentials with Gitleaks in the tracked tree, reachable local/remote Git history, GitHub PR/release text, 25 extracted release packages, 47 historical CI logs, or 91 CI artifacts, including extracted binary strings. Commit email addresses use GitHub/no-reply domains; inspected documentation images and media metadata contain no personal data. The initial audit found local user paths in assembly debug records in all 25 release packages.

The release cleanup replaced all 25 GitHub downloads with copies containing only PDB basenames in their CodeView records, and refreshed published checksums. Validation compared all 100 assemblies and 24,876 method bodies: CLI metadata, executable instructions and versions are unchanged. Fresh downloads were checked for local user paths. The affected binaries were never tracked in Git, so no Git history rewrite was needed. GitHub Actions is disabled at repository level; validation remains local.

Separate public copies of 27 versions remain on McNeel's Yak registry, including two earlier versions without GitHub releases. Registry downloads confirmed local user paths in all 27; 25 archives exactly match the original GitHub assets. Replacing GitHub downloads does not update the registry. Its [CLI reference](https://developer.rhino3d.com/guides/yak/yak-cli-reference/#yank) says published versions cannot be overwritten, and yanking only hides versions from searches while retaining exact-version downloads. Removing stored registry copies requires McNeel support.

`npm run check`, both runtime builds, the dependency vulnerability query, and managed Grasshopper tree checks passed. The full Windows Rhino 8.35 smoke run crashed in `CRhinoDisplayPipeline_DrawShadedMeshes` during `RowActionChecks.VerifyNativePreviewOverlap`, called through `DrawToBitmap`. The same crash reproduced against the unchanged published `v0.1.20-beta.5` package with the current harness, so the full native suite is not currently green. This reproduces in the hidden test host; normal interactive Rhino behavior has not been established by this audit.

On Windows Rhino 8.36 the isolated overlap fixture also crashed at the same
`DrawShadedMeshes` / `DrawToBitmap` stack against the exact public Yak
`0.1.20-beta.7` and the in-progress build. The baseline archive SHA-256 was
`5ad3ef3e804977be7f056c243fe13661ac267e3b1f3d869d2724963abd81ba39`;
the host recorded the baseline plugin and Core hashes from the extracted
archive before starting Rhino. Both runs remain failed fixtures, not passes.
The separate item-order viewport capture also crashed in
`CRhinoDisplayPipeline_Flush` / `DrawToBitmap` for both the in-progress build
and that same public beta.7 payload. It is an additional failed fixture; a
matching baseline does not verify its rendering assertions.

## Deferred findings

- Query option cloning and equality enumerate settings in multiple places. Centralizing this may help future additions, but output-filter equality intentionally ignores sorting and sometimes decimal precision. Preserve those distinctions.
- Geometry scheduling could move out of `PanelSession`, but a useful extraction needs to preserve UI dispatch, in-flight measurement identity, cache retention across unit changes and cancellation. A file move alone does not improve those ownership boundaries.
- Selection gestures repeat cancellation, highlighting and repaint side effects. Consolidate only after distinguishing gesture updates from query-completion synchronization and document changes.
- The archived HTML prototype is documented as a visual reference and has an independent data engine. Removing it is a product/documentation decision, not dead-code cleanup in the native plugin.

## Validation

Run `npm run check` before opening or merging a PR. It runs the discoverable solution tests, native coordinator fake-child checks, packaging checks, both runtime builds and unused-code diagnostics. Native interaction changes also require the managed Grasshopper tree checks and all isolated Rhino fixtures. Release validation uses the plugin extracted from the final Yak archive. Windows .NET Framework and Mac runtime validation remain separate coverage gaps.

```powershell
dotnet build tests/BetterGhPanel.RhinoSmoke -c Release
tests/BetterGhPanel.RhinoSmoke/bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe --tree-only <absolute-plugin-path>
tests/BetterGhPanel.RhinoSmoke/run.ps1 -PluginPath <absolute-plugin-path>
```

The full run starts one sequential Rhino process per entry in `tests/BetterGhPanel.RhinoSmoke/fixtures.json`; each has a timeout and unique result and log under `bin/Release/net7.0-windows/runs`. The result records the actual loaded plugin and Core paths and SHA-256 before Rhino starts, and a count of assertions performed in that fixture. It rejects incomplete, failed, stale, mismatched, empty or malformed reports and abnormal process exits while continuing the remaining fixtures. `-Fixture output,filter-metadata,filter-ui` selects individual fixtures for diagnosis; release validation requires the full inventory. `-TimeoutSeconds` changes the per-process timeout. `--identity-only <fixture> <plugin-path>` on the host executable writes the pre-start identity report without starting Rhino; it remains incomplete and is not a fixture pass. The build removes any stale host-local Core copy so the host can bind the Core next to the requested plugin.

`OutputChecks` adds provenance checks across all tree-tool combinations, downstream S/B/F updates, scope Undo/Redo, strict paired-tree validation, original mask shape and legacy port identities. The `output` fixture runs the native cases; `--tree-only` includes the managed provenance cases.

The output regressions also check real archive diagnostics, internalized numeric/null/duplicate paired values, cache reuse, unchanged-selection solution counts, native grip picking at target edges, and a gated geometry filter with replacement input before late completion. Archive checks use empty paired input independently of the runtime internalization cases, so the hidden host does not need to populate Grasshopper's private goo-type cache.
