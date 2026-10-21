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

## Deferred findings

- `PanelSession.GetDisplayValue` and its live detail resolver have no current production caller; only old smoke assertions exercise them. They are public members, so removal needs an explicit compatibility decision and replacement of the obsolete assertions. Current canvas and copy behavior uses captured presentation instead.
- Query option cloning and equality enumerate settings in multiple places. Centralizing this may help future additions, but output-filter equality intentionally ignores sorting and sometimes decimal precision. Preserve those distinctions.
- Geometry scheduling could move out of `PanelSession`, but a useful extraction needs to preserve UI dispatch, in-flight measurement identity, cache retention across unit changes and cancellation. A file move alone does not improve those ownership boundaries.
- Selection gestures repeat cancellation, highlighting and repaint side effects. Consolidate only after distinguishing gesture updates from query-completion synchronization and document changes.
- The archived HTML prototype is documented as a visual reference and has an independent data engine. Removing it is a product/documentation decision, not dead-code cleanup in the native plugin.

## Validation

Run `npm run check` before opening or merging a PR. It covers managed behavior, packaging, both runtime builds and unused-code diagnostics. Native interaction changes also require the managed Grasshopper tree checks and both hidden Rhino suites in `tests/BetterGhPanel.RhinoSmoke/run.ps1`. Release validation loads the plugin extracted from the final Yak archive. Windows .NET Framework and Mac runtime validation remain separate coverage gaps.
