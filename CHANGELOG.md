# Changelog

## 0.2.6-beta

- **Your selection sticks.** S keeps the selected rows when an upstream change leaves the same paths and branch lengths (with Create set off), and clicking blank canvas or empty panel space no longer clears it.
- **Search in fewer steps.** Picking a comparison mode opens the editor straight away, one × clears the search, and clicking away applies what you typed (Esc cancels). Ctrl+C copies selected rows in every view.
- **Clearer controls.** Two resets, **Clear filters** and **Reset all** (header ↻), and one vocabulary: Preview and **B + F follow** choose **Results** or **Selection**.
- **Rounding that never rounds down.** With Round up by factor, Decimal places also rounds up, and Stats show the same numbers as the rows.
- **Behaviour changes:** Out Items | Values falls back to Items whenever the switch is hidden. The **·S** port suffix is gone. "Reset panel view" and "Reset tree tools" are replaced by Clear filters. Clicking away from any inline editor now applies a changed, valid value instead of discarding it. Inputs whose full-precision digits sit above a step (such as 0.1+0.2) still round up to the next step.

## 0.2.5-beta

- **Text Dots are fully supported.** Rows show the dot's text; X/Y/Z, font size and font face work as properties; selected dots preview, highlight, frame and copy in Rhino.
- **More types show real values.** Text entities, leaders, dimensions (`4.00 mm`, `90.00 °`), hatches, blocks, detail views, culture, location (`22.30° N, 114.17° E`), path, matrix, complex numbers, SubD, extrusions, point clouds, transforms and mesh faces now show values and properties instead of just their type name, following Units and Decimal places.
- **Behaviour changes:** SubD, Extrusion, Point Cloud, Transform, Complex and Mesh Face rows show summaries instead of `<SubD>`-style names, so a saved text filter like `<SubD>` stops matching. With values shown, text and true/false values are left-aligned.

## 0.2.4-beta

- **What you see is what you send.** Round up and Decimal places now apply to every number the panel shows, including Length, Area, Volume, properties and X/Y/Z. D and S send exactly those numbers, and filters and Stats use them too. Hover a rounded value to see the raw one.
- **D | F pairs by branch.** F only needs the same number of branches as D; item counts may differ. Missing and extra F items are marked in the pane.
- **Copy branch addresses.** Hover a branch heading to copy `{0;2;52}`, or right-click a row for `{0;2;52}(3)` or a table for Excel.
- **Data checks.** A short strip flags nulls, invalid items and branches whose length differs from the rest; click it to filter to them.
- **Behaviour changes:** Decimal places now rounds what D and S send, and numbers always use `.` as the decimal separator.

## 0.2.3-beta

- **Send items or values.** When the panel shows a value (Length, Area, Validity, …) or a set, the new **Out Items | Values** switch next to **Select all** chooses what D and S send: the original data, for example the curves you filtered by length, or the values you see.
- **One branch per set value.** With Create set and **Items**, every matching item goes to `{path;k}`, and set rows are numbered by that **k**. Selecting a set row sends, previews and frames all of its members.

## 0.2.2-beta

makes picking a filter mode show its values immediately, with filtering as an optional second step from the search field, and replaces typed Validity and Boolean comparisons with one-click value choices (Valid/Invalid/Unverified/Null, True/False). Clear (×) removes the filter first, then the displayed values. Saved query formats are unchanged.

## 0.2.1-beta

adds the D | F split view and fixes rows that could ignore clicks at fractional canvas zoom. It adds `row-selection` to the native inventory (21 fixtures), covering hit registration across zoom levels, subpixel pans, viewport edges, partial repaints and stale-result guards; `ui-ux` gains split-view interactions with distinct D/F values. `npm run check` also rejects C# sources that are not valid UTF-8.

## 0.1.26-beta

adds focused keyboard/wheel navigation, safe live search, clearer controls and responsive styling. It adds `ui-ux` to the native inventory (20 fixtures), exercising debounced host textbox previews, cancellation and no-op commits with synchronous and asynchronous queries, selection and scroll restoration, unchanged outputs and Undo/Redo history, committed-query serialization, search commit/Undo/Redo, mouse-to-keyboard selection, footer sizing and wrapped geometry action availability.

## 0.1.25-beta

adds selectable inspection properties and `property-inspection` to the native inventory (19 fixtures). It checks scalar and geometry property capture, offscreen planarity filtering, typed D/F outputs, numeric property sets and copying, display-mode persistence, subclass rejection and SubD/complex capture freshness.

## 0.1.24-beta

adds mesh area and closed-mesh volume, point/vector X/Y/Z properties, solid-Brep surface area, and saved upward rounding of numeric output by a positive factor after unit conversion. Geometry measurements use the existing owned-copy background queue. Rounding affects numeric inputs; geometry coordinates and measured properties retain their precision. No new tests are added; the existing menu expectations and reflective units adapter follow the new choices/signature.

## 0.1.20-beta.7

makes Create set operate after filters, units and decimal formatting, and materializes displayed numbers/text on D/S. Displayed list lengths become unique counts across branches, including selectable zero values from empty branches. Original source addresses drive occurrence details and B/F; selected pairs respect item filters. Changing precision republishes the set, and offscreen geometry is measured before grouping.

## 0.1.20-beta.6

adds **Display value** beside the comparison operators. List length shows one count per branch, including empty branches, after tree tools. Numeric and geometry modes show their metric with the current units and decimals; circle length uses circumference. No comparison is applied, while type/invalid/Paths restrictions remain active. The option saves with the definition and supports Undo/Redo, reset and minimum Half layout. Per-item metric copies use the displayed numeric formatting without unit labels; typed outputs retain their data.

## 0.1.20-beta.5

adds selected data (S), an original-input Boolean mask (B), and optional paired data (F), while D keeps all filter matches. B/F default to Filtered scope and can follow Selected rows instead; the saved scope supports Undo/Redo. Strict pairing includes empty branches, retains the primary Create set representative, and preserves paired values without unit conversion. Legacy archives retain D identities and load without false corruption errors

## 0.1.20-beta.4

consolidates unit-conversion definitions, shares query scope with measurement preparation, and gives row-menu actions stable named identities. Existing settings and behavior are preserved. See the [maintainability audit](docs/maintainability.md) for feature ownership and deferred findings.

## 0.1.20-beta.3

keeps Asc/Desc sorting inside each branch and retains tree-order branch groups. Sorted views show branch headings and preserve global row numbers for jumping. Copy order matches the grouped view; output remains in transformed tree order. It also releases closed-menu and expired-render caches, includes the MIT license in Yak packages, rejects stray staged files, and corrects the English/Japanese demo explanations.

## 0.1.20-beta.2

adds per-branch list-length comparisons and branch input with optional braces. Text/regex filters and nonnumeric sorting share the canvas's converted, decimal-formatted presentation, including offscreen geometry and stable measurement failures. Decimal changes refresh text-filter output without rounding wire values. Copy uses displayed coordinates and invalid markers; numeric and metric copies retain unit-free invariant formatting. Invalid numeric conversion preserves validity

## 0.1.20-beta.1

adds per-branch occurrence badges and expandable original input locations. Short tables omit pagination; hidden, disabled and stale views cannot expose old controls

## 0.1.20-beta

republishes the Create set feature with the next beta version; behavior is unchanged from `0.1.19-beta.3`. The package is rebuilt for both runtimes and validated from its final archive.

## 0.1.19-beta.3

adds a Create set tree tool beside Reverse

## 0.1.19-beta.2

uses zero-based item-order labels within each branch and adds a visible Select all button for all current matching rows

## 0.1.19-beta.1

removes the unused inline-path column from grouped source-order rows and shrinks their numbering gutter to the original index alone. Sorted rows keep their view numbers and addresses; compact panels retain inline paths when branch headings do not fit

## 0.1.19-beta

adds selection of the same zero-based item indices across branches through the row menu or Alt/Option-click

## 0.1.18-beta

adds row deselection by double-click or empty-space click, padded panel move/resize handles, item/branch counts, exact unique numeric/text frequencies, compact capsule minimization and optional viewport item-order labels. Host checks cover native pointer dispatch, minimum Half menus, filtered statistics, presentation-only undo/redo, capsule restoration, saved settings, stale-result cleanup and viewport label visibility. At that time, the full behavior suite and the order-label capture ran in separate hidden Rhino processes because running both capture fixtures in the same hidden host crashed the Rhino 8.35 display pipeline. Both processes had to pass assertions and exit normally. Release validation uses the plug-in extracted from the final Yak archive, passed with `-PluginPath`.

## 0.1.17-beta

adds automatic one-based row positions in Asc/Desc beside smaller original item indices. The native renderer uses a fixed full-tree gutter, shared text baselines and right-aligned numeric values without adding a toggle or changing the row height. Host checks cover sort directions, selection retention, row jumps beyond the viewport, filtered numbering, fixed gutter capacity, source order, output invariance and the minimum Half layout.

## 0.1.16-beta

adds Selected (default), Filtered results and Off preview modes, keeps the camera still on selection, and provides explicit Frame selection. It also fixes custom preview framing, pending copy status on document switches and row actions against disabled or stale results.

## 0.1.15-beta

adds current-result loading states, a resumable 120 ms query budget, and filtered typed output after tree tools and unit conversion. It also fixes whitespace-regex output, internalised preview filtering and disabled-state animation.

## 0.1.14-beta

is a version-only update of `0.1.13-beta.1` with no behavior changes.

## 0.1.13-beta.1

refreshes the tree-tool icons and aligns the footer.

## 0.1.12-beta.1

adds selected geometry highlighting, branch framing and measurement/regex filters. These and `0.1.11-beta.1` are the 2026-10-11 patches.

## 0.1.10-beta.1

was published on 2026-10-10. Previous betas `0.1.9-beta.1`, `0.1.8-beta.1` and `0.1.7-beta.1` were published on 2026-10-09. Betas `0.1.6-beta.1`, `0.1.5-beta.1` and `0.1.4-beta.1` were published on 2026-10-08. Earlier betas, `0.1.3-beta.1` and `0.1.2-beta.1`, were published on 2026-10-06 and 2026-10-05 respectively.
