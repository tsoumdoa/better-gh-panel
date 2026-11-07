# Changelog

## 0.2.8-beta

- **Controls that say what they do.** The search field starts with a mode chip (Text, Length, Vertex count, …) that opens the mode menu; the footer range ("1–15/40") opens Jump to row (also Ctrl/Cmd+G); the footer reads "D = input", "D: 2 filters", "D: tools" or "D: values", with a × beside it that clears the filters; the Trim icon shows its depth; Reset all (↺) sits in the footer beside the tools and Units it resets.
- **Menus that fit.** Dropdowns, Stats, Units and the row menu extend beyond the panel, flip away from the edge of the view, and draw above neighbouring components. Dropdowns page the choices beyond twelve rows; the row menu never pages and scrolls under the wheel when it is taller than the view.
- **Double-click a row to view its full value.** Esc or a double-click on empty list space clears the selection.
- **Clearer feedback.** Selected rows get an accent bar, the active Units chip is readable ("→m ↑0.25 2dp"), the Paths placeholder matches the other dropdowns, tooltips are shorter, every undo step is named "Panel+ …", and disabled text follows the dark canvas theme.
- **Behaviour changes:** double-clicking a row no longer clears the selection; Jump to row and Reset all moved from the header to the footer; the right-click menu is no longer paged.
- **Validated natively again.** All 24 Rhino fixtures run and pass; expectations left stale by the waived 0.2.6 and 0.2.7 validations were refreshed, and the harness can save review screenshots on request.


## 0.2.7-beta

- **Maintenance release.** Shared model helpers, component responsibilities and canvas controls are organized for easier maintenance, with no intended change to controls, saved settings or data outputs.
- **Clearer regression coverage.** Core checks run as individual xUnit tests, and native preview checks are grouped by fixture with shared reflection helpers.
- **Easier contributor navigation.** Subsystem architecture, build instructions, release history and validation records now have dedicated documents.


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

Makes picking a filter mode show its values immediately, with filtering as an optional second step from the search field, and replaces typed Validity and Boolean comparisons with one-click value choices (Valid/Invalid/Unverified/Null, True/False). Clear (×) removes the filter first, then the displayed values. Saved query formats are unchanged.

## 0.2.1-beta

Adds the D | F split view and fixes rows that could ignore clicks at fractional canvas zoom.


## 0.2.0-beta

Renames the Yak package to `panel-plus` and introduces the green P+ icon. Component and assembly identities remain compatible. Remove the old package before installing the renamed one.


## 0.1.26-beta

Adds focused keyboard/wheel navigation, safe live search, clearer controls and responsive styling.


## 0.1.25-beta

Adds selectable inspection properties.


## 0.1.24-beta

Adds mesh area and closed-mesh volume, point/vector X/Y/Z properties, solid-Brep surface area, and saved upward rounding of numeric output by a positive factor after unit conversion. Geometry measurements use the existing owned-copy background queue. Rounding affects numeric inputs; geometry coordinates and measured properties retain their precision.


## 0.1.23-beta

Updates repository validation and startup diagnostics. Application code and behavior are unchanged from 0.1.22-beta.


## 0.1.22-beta

Repackages 0.1.21-beta under a new version. Application code and behavior are unchanged.


## 0.1.21-beta

Adds input-aware search choices, named geometry-group summaries and a scrollable View full value window. Empty text and unverified validity are clearer. Selection, capture, measurement, preview, framing and copying handle stale data and native resources more robustly.


## 0.1.20-beta.7

Makes Create set operate after filters, units and decimal formatting, and materializes displayed numbers/text on D/S. Displayed list lengths become unique counts across branches, including selectable zero values from empty branches. Original source addresses drive occurrence details and B/F; selected pairs respect item filters. Changing precision republishes the set, and offscreen geometry is measured before grouping.

## 0.1.20-beta.6

Adds **Display value** beside the comparison operators. List length shows one count per branch, including empty branches, after tree tools. Numeric and geometry modes show their metric with the current units and decimals; circle length uses circumference. No comparison is applied, while type/invalid/Paths restrictions remain active. The option saves with the definition and supports Undo/Redo, reset and minimum Half layout. Per-item metric copies use the displayed numeric formatting without unit labels; typed outputs retain their data.

## 0.1.20-beta.5

Adds selected data (S), an original-input Boolean mask (B), and optional paired data (F), while D keeps all filter matches. B/F default to Filtered scope and can follow Selected rows instead; the saved scope supports Undo/Redo. Strict pairing includes empty branches, retains the primary Create set representative, and preserves paired values without unit conversion. Legacy archives retain D identities and load without false corruption errors

## 0.1.20-beta.4

Consolidates unit-conversion definitions, shares query scope with measurement preparation, and gives row-menu actions stable named identities. Existing settings and behavior are preserved.


## 0.1.20-beta.3

Keeps Asc/Desc sorting inside each branch and retains tree-order branch groups. Sorted views show branch headings and preserve global row numbers for jumping. Copy order matches the grouped view; output remains in transformed tree order. It also releases closed-menu and expired-render caches, includes the MIT license in Yak packages, rejects stray staged files, and corrects the English/Japanese demo explanations.

## 0.1.20-beta.2

Adds per-branch list-length comparisons and branch input with optional braces. Text/regex filters and nonnumeric sorting share the canvas's converted, decimal-formatted presentation, including offscreen geometry and stable measurement failures. Decimal changes refresh text-filter output without rounding wire values. Copy uses displayed coordinates and invalid markers; numeric and metric copies retain unit-free invariant formatting. Invalid numeric conversion preserves validity

## 0.1.20-beta.1

Adds per-branch occurrence badges and expandable original input locations. Short tables omit pagination; hidden, disabled and stale views cannot expose old controls

## 0.1.20-beta

Republishes the Create set feature with the next beta version; behavior is unchanged from `0.1.19-beta.3`.

## 0.1.19-beta.3

Adds a Create set tree tool beside Reverse

## 0.1.19-beta.2

Uses zero-based item-order labels within each branch and adds a visible Select all button for all current matching rows

## 0.1.19-beta.1

Removes the unused inline-path column from grouped source-order rows and shrinks their numbering gutter to the original index alone. Sorted rows keep their view numbers and addresses; compact panels retain inline paths when branch headings do not fit

## 0.1.19-beta

Adds selection of the same zero-based item indices across branches through the row menu or Alt/Option-click

## 0.1.18-beta

Adds row deselection by double-click or empty-space click, padded panel move/resize handles, item/branch counts, exact unique numeric/text frequencies, compact capsule minimization and optional viewport item-order labels.


## 0.1.17-beta

Adds automatic one-based row positions in Asc/Desc beside smaller original item indices.


## 0.1.16-beta

Adds Selected (default), Filtered results and Off preview modes, keeps the camera still on selection, and provides explicit Frame selection. It also fixes custom preview framing, pending copy status on document switches and row actions against disabled or stale results.

## 0.1.15-beta

Adds current-result loading states, a resumable 120 ms query budget, and filtered typed output after tree tools and unit conversion. It also fixes whitespace-regex output, internalised preview filtering and disabled-state animation.

## 0.1.14-beta

A version-only update of `0.1.13-beta.1`, with no behaviour changes.

## 0.1.13-beta.1

Refreshes the tree-tool icons and aligns the footer.

## 0.1.12-beta.1

Adds selected geometry highlighting, branch framing and measurement/regex filters.


## 0.1.11-beta.1

Adds row selection and explicit value or geometry copying for Rhino Paste. At this release, selecting geometry frames it and double-click copies values. Copies follow displayed order and finish pending measurements before replacing clipboard contents.


## 0.1.10-beta.1

Measures visible geometry automatically in the background, retaining shape metadata. Queued work follows canvas visibility and rejects obsolete results. Replaces the display cycle with Full/Half; saved Collapsed panels reopen in Full.


## 0.1.9-beta.1

Replaces hover measurement with an explicit Measure action for one selected row. Adds cubic-unit conversion, cached-metric formatting and visible invalid labels with reasons.


## 0.1.8-beta.1

Adds compact geometry metadata and cached hover measurements for length, trimmed area and solid volume in model units.


## 0.1.7-beta.1

Makes Reset restore incoming data and default tools, filters, units and decimals in one Undo. Improves dropdown spacing, highlights and paging.


## 0.1.6-beta.1

Adds Full, Half and Collapsed modes with fixed wire positions, saved dimensions and Undo. Half resizing keeps its corner under the pointer.


## 0.1.5-beta.1

Corrects tool order to Simplify → Trim → Flatten → Reverse → unit conversion. Definitions using Simplify and Trim recompute with trimmed simplified paths while preserving upstream data.


## 0.1.4-beta.1

Gives the default table more room with one filter strip and footer tree-tool icons. Adds compact type hints and readable Trim and Jump editors at small zoom levels.
