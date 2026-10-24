# Maintainability audit

This pass reduces duplicated feature definitions while keeping saved settings, typed output, query behavior and canvas interactions compatible. It deliberately keeps ownership and cancellation in the existing component/session objects.

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

## Deferred findings

- `PanelSession.GetDisplayValue` and its live detail resolver have no current production caller; only old smoke assertions exercise them. They are public members, so removal needs an explicit compatibility decision and replacement of the obsolete assertions. Current canvas and copy behavior uses captured presentation instead.
- Query option cloning and equality enumerate settings in multiple places. Centralizing this may help future additions, but output-filter equality intentionally ignores sorting and sometimes decimal precision. Preserve those distinctions.
- Geometry scheduling could move out of `PanelSession`, but a useful extraction needs to preserve UI dispatch, in-flight measurement identity, cache retention across unit changes and cancellation. A file move alone does not improve those ownership boundaries.
- Selection gestures repeat cancellation, highlighting and repaint side effects. Consolidate only after distinguishing gesture updates from query-completion synchronization and document changes.
- The archived HTML prototype is documented as a visual reference and has an independent data engine. Removing it is a product/documentation decision, not dead-code cleanup in the native plugin.

## Validation

Run `npm run check` before opening or merging a PR. It covers managed behavior, packaging, both runtime builds and unused-code diagnostics. Native interaction changes also require the managed Grasshopper tree checks and both hidden Rhino suites in `tests/BetterGhPanel.RhinoSmoke/run.ps1`. Release validation loads the plugin extracted from the final Yak archive. Windows .NET Framework and Mac runtime validation remain separate coverage gaps.

`OutputChecks` adds provenance checks across all tree-tool combinations, downstream S/B/F updates, scope Undo/Redo, strict paired-tree validation, original mask shape and legacy port identities. The full host suite includes it; `BetterGhPanel.RhinoSmoke.exe --outputs-only` runs just the native output checks while `--tree-only` includes the managed provenance cases.

The output regressions also check real archive diagnostics, internalized numeric/null/duplicate paired values, cache reuse, unchanged-selection solution counts, native grip picking at target edges, and a gated geometry filter with replacement input before late completion. Archive checks use empty paired input independently of the runtime internalization cases, so the hidden host does not need to populate Grasshopper's private goo-type cache.
