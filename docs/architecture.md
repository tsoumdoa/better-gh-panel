# Architecture

## Overview

The input tree passes through Simplify, Trim, Flatten and Reverse, then unit conversion and snapshot capture. Core queries the managed snapshot for filters, sets, statistics and view order. The component publishes typed outputs, the canvas displays the current result, and Rhino preview uses the current result or selection scope.

## Projects and layering

`BetterGhPanel.Core` is the Rhino-free netstandard2.0 data engine. `BetterGhPanel` targets net48 and net7.0 and references Core. Its Runtime directory adapts Grasshopper trees and Rhino geometry; UI contains canvas widgets and drawing helpers. Component and attributes partials coordinate host lifecycle, outputs, settings and interaction. Core does not reference Runtime or UI.

## Core

## Session and background work

Annotation/hatch/block/detail inspection concerns the captured native shape, not separate styles, patterns or definitions; those dependencies are not traversed or previewed/copied.

Referenced shapes still require document/object context.

Culture witnesses freeze name/English name strings; Matrix witnesses retain at most 4,096 managed cells and paths at most 4,096 indices, charging those sizes to the capture-wide 100,000 visit budget before retaining the witness.

Graph rechecks count the same sizes.

Numeric summaries retain bounded managed values and use the active precision for display, text filters, copies and set equality.

Matrices and transforms share one summary formatter.

Material/Field rows do not inspect assets/elements and remain unverified.

`GeometryHints` captures analytic primitive measurements and stored properties as immutable `PanelGeometryInfo` records with raw values, dimensions and model units.

Painting submits visible row positions to `PanelSession`; it never duplicates or measures geometry inline.

After an initial 40 ms scrolling delay, a UI callback reconstructs a bounded curve from values or duplicates a standalone curve/Brep using the compatibility fallback, and one background operation computes general curve length/linearity, trimmed surface/open Brep area or solid Brep volume.

The worker owns and disposes its copy and never accesses source goo or the Rhino document.

Fallback duplication may invoke installed UserData callbacks, and its native work and allocation are unbounded; see [geometry measurement integration](geometry-measurement-safety.md).

Pending rows show measuring status; automatic viewport measurements cache successful and failed results by source position and repaint the panel without another query or solution.

The queue continues without another scrolling delay for each row, while measurement redraws are coalesced over 60 ms.

Each session runs at most one geometry operation.

Only row bounds intersecting the canvas viewport enter the automatic pending queue; explicit query and copy batches can request offscreen rows.

Scrolling and canvas panning replace the viewport queue; offscreen panels and zooming below the row display threshold clear it.

Canvas clipping does not change table layout or scroll capacity.

Queued viewport rows that leave view are skipped.

A native mass-property call already running cannot be interrupted; it finishes and releases its copy, with stale publication rejected.

One `PanelSession` per component owns the current snapshot, copied options, and the published result.

`PanelQueryWork` gives managed queries a 120 ms UI time budget measured with a monotonic stopwatch, independent of row and branch count.

Checkpoints cover empty-branch scanning, each row, branch grouping, sorting and result construction.

When the budget expires, the same iterator continues on a worker with its scan position, statistics and merge-sort state intact; no partial result is published and no scan is restarted.

Worker completion bypasses deadline checks and iterator handoffs inside the loops.

The deadline is cooperative: individual string operations, allocations and copies can overrun it.

Regex and queries requiring native geometry measurements start in the background because a single operation cannot yield midway.

`HasCurrentResult` gates rows, hit regions, counts, type summaries, statistics and tooltips.

Pending views show Recomputing… with an animated spinner; the session owns its timer, runs it only for a visible busy panel, and stops it on completion or disposal.

Disabled components invalidate their input into an idle suspended state, cancel pending work and stop animation.

Their canvas shows Disabled until re-enabling supplies a new snapshot; query edits and late worker completions cannot restart them.

Errors hide stale results too.

Cancellation and request numbers reject obsolete results before UI publication.

Changed options mark the document modified through `QueryChanged`; query progress and completion invalidate the canvas directly.

Equal options do not trigger another scan.

The component's reset path explicitly closes draft editors and clears transient canvas state, even when the query options are already at their defaults.

## Capture and safety

`CanvasRowRenderer` draws the numbering gutter and aligns row text by font ascent against one canvas-unit baseline.

Asc/Desc sorts within each branch, retaining snapshot branch order, and shows the one-based index into the complete current result beside the original branch item index in a smaller bracketed slot.

These global view numbers remain the target for Jump to row.

Source order uses a compact gutter for the original item index alone.

Both source and sorted rows use branch headings and omit the inline path column when there is room; panels too short for headings retain inline paths.

Gutter capacity uses the full snapshot item count, which bounds every captured branch index, so paint never scans the tree or resizes columns based on filtered or scrolled rows.

Numbering is transient presentation, adds no saved setting, and does not change selection identity, query order or typed output.

Row hits retain the view number for tooltip lookup without searching the result.

Numeric cells use right-aligned monospaced text; other data retains left alignment.

Row height stays 17 canvas units.

The native host checks cover digit-boundary filtering, row jumps beyond the viewport, selection retention, output invariance and the minimum Half layout.

Geometry filtering first validates the query and collects eligible branch/type/valid primary-metric candidates on a worker.

Displayed-text queries and nonnumeric value sorting also collect eligible measurements, including offscreen rows, before publishing their complete result.

The session measures those positions through its existing single geometry worker, then captures an immutable measurement map on the UI thread for the final managed query.

Scalar comparisons exclude geometry from that work.

The query retains the original snapshot identity and source positions; cancellation and request ownership reject obsolete results.

Metrics compare presented display-unit values using the same source-unit/dimension conversion guards as formatting.

Length covers curves and circle circumference; area covers rectangles, surfaces and open Breps; volume covers boxes and closed Breps.

Unsupported secondary metrics are excluded.

Visible-row measurement and copying share the same cache and queue.

A normal new solution invalidates geometry cache entries; a unit-only solution retains an entry only when a complete new witness matches the copied geometry and context.

Compatibility results have no witness and are remeasured on a new snapshot, while output-only solutions preserve the same snapshot's cache.

Entry identity and captured source checks reject stale callbacks across new solutions and disposal.

Invalid, obsolete and disposed views cannot start geometry work.

`PanelUnits.FormatGeometry` applies only conversions matching the model units and metric dimension, with explicit decimals rounding the presented number from cached raw values.

Geometry output is never scaled.

Invalid rows carry a captured validity reason and visible text label.

Completed asynchronous metrics and stable measurement-unavailable labels enter text/regex matching through the shared presentation formatter.

No live geometry enters the managed snapshot.

McNeel's [asynchronous execution guidance](https://developer.rhino3d.com/guides/scripting/advanced-async/) describes the separation between background work and document/UI access.

`PanelGeometryInfo` separates its compact description from labeled tooltip details.

The same summary builders capture pending metadata and construct measured results, so a metric augments the original trimming, degree, topology and count information.

Curve linearity/planarity and single-face surface planarity are computed on the owned background copy.

Rows keep the type, a few relevant properties and the metric; geometry rows do not repeat a separate type label.

Tooltips show the formatted metric once, followed by labeled properties, including surface U/V degree and Brep face/edge/vertex counts.

Pending and failed rows retain the available metadata, and unit/decimal changes reformat the metric without removing those details.

Planarity uses RhinoCommon's default zero tolerance.

See [geometry measurement safety](geometry-measurement-safety.md) and [geometry preview safety](geometry-preview-safety.md) for ownership, admission and freshness contracts.

## Preview and Rhino actions

The property inspection uses immutable `PanelPropertyValue` records on captured rows and geometry summaries.

`PanelProperties` owns property kinds, labels and lookup; input snapshots union property keys once for menu capabilities.

Null entries advertise deferred properties.

Exact stock holders are read by `PanelPropertyCapture`; general geometry shape/planarity is completed on owned worker copies.

`PanelPropertyFilter` compares typed numeric, Boolean, text and calendar-time values without parsing tooltip prose.

Display, value copy, displayed sets and property sorting share that representation.

Existing search-mode enum values remain unchanged, and new modes are appended.

`StockGooSupport` is the runtime catalog of compile-checked exact holder types, names and independent validity, managed capture, native capture, preview, measurement, shape-only load and group-reference validity facets.

Native entries own their typed value reader and exact inner types.

`BetterGhPanel.Core.StockTypeLabels` is a plain name-to-label/separate-label table shared by the runtime and pure UI tests.

Typed property readers and managed copy/equality remain explicit; Core and its pure menu tests do not load Rhino.

Text Dot uses the existing owned preview/copy pipeline and displays its text.

Dots and annotation/hatch origins use `PanelComponents` for X/Y/Z.

Dimensions use unit-aware length/angle metrics; angles follow the existing radians-to-degrees property convention.

Dot font size is screen pixels; annotation text height is omitted because stock value getters strip the dimension style.

Create set groups dots by displayed text.

Native text capture rejects strings beyond the preview text limit; admitted text uses the existing bounded canvas/tooltip presentation.

`PanelOrderLabels` maps ordinary rows to their zero-based `OriginalIndex` after tree tools, and all members of a set to its stable set index k.

Filtering, sorting and selection do not renumber labels.

Repeated indices across branches and coincident anchors are valid; the existing selected-first collision handling decides visibility.

Label caches are still discarded for obsolete results and inactive preview scopes.

With item order enabled, row tooltips show the matching branch path and index.

Row selection is bound to a snapshot and uses source positions, with ranges and copy order taken from the current query result.

Replacement-input solutions cancel live query previews, then explicitly re-bind committed selection by source position only when paths and branch lengths match and Create set is off; structural changes and Create set recomputes clear it.

Output-only solutions preserve it.

Filtering removes hidden selections and sorting retains matching source positions.

Actions reject obsolete snapshots, pending queries and stale row hit regions.

Row and branch-heading clicks select without moving the camera.

Double-clicking a row or branch heading clears selection.

Double-clicking empty table space does not open search.

Panel header clicks retain Grasshopper's component dragging behavior.

Blank-canvas and empty-panel clicks retain selection.

A canvas observer handles document switches and disposal, clearing selection and borrowed previews.

Branch filters clear selection and only narrow the query.

The explicit Frame selection context-menu action only moves the camera, using the same bounds resolver as preview, including custom preview goo.

Clearing row selection also cancels pending copying and closes its action menu while retaining the Results preview scope.

Right-click actions retain an existing multi-row selection; value and geometry copying remain explicit context-menu actions.

`PanelGeometryHighlight` participates in the component's native Grasshopper preview callbacks.

It borrows preview goo only for the current scope: selected positions in Selection mode, or every query match in Results mode; sets expand representatives to all member positions in either scope.

Off clears the preview cache.

No callback falls back to the base component's full-tree preview.

Selected rows use Grasshopper's selected wire/material settings; other matches use normal colors even when the component itself is selected.

Clipping bounds cover only the drawn scope and are empty when preview is off, hidden, locked or suspended.

Selected wires receive Grasshopper's small clip-depth bias and configured thickening when the component has not already received them from the native selection pass; the drawing transform is restored afterward.

This keeps upstream previews from covering the highlight without changing geometry or disabling depth testing.

Other components' previews remain independent.

Geometry copying duplicates supported geometry into a private 3DM archive with the source solution's model units and tolerance, then publishes the native Windows Rhino clipboard format.

No objects are baked into or deleted from the user's Rhino document.

Unsupported, invalid and non-geometric rows are skipped and reported.

An empty supported selection leaves the clipboard untouched.

Native Mac geometry clipboard and custom model units remain unsupported.

## Canvas UI

The footer's **Select all** button uses `BetterGhPanel.Core.PanelRowSelection.SelectAll` over the current query result, including offscreen rows.

It shares row-action currency guards, pending-copy cancellation, highlighting and selection cleanup.

Full and minimum Half layouts reserve space beside Stats, and capsule layout clears the control's hit target.

Repeated clicks preserve the full selection; selection leaves D unchanged, updates S and the Selection B/F scope, and never frames the camera.

The padded header move target delegates mouse-down to Grasshopper so native dragging, selection and undo stay intact.

Move/resize hover consumes the event after setting its cursor, preventing the host from replacing it.

The resize target is 24 × 24 canvas units; the footer ends 28 units before the right edge to keep controls separate.

Native host checks exercise header dragging and every padded corner in Full and Half layouts.

`PanelQueryOptions.SearchMode` selects Text, Regex, Value, Length, Area, Volume or ListLength while `Search` holds the text, pattern or comparison.

The search dropdown reuses the existing compact canvas menu and inline editor.

Text retains legacy numeric comparisons; comparison modes support greater/less comparisons, equality and inequality.

Text and regex use `PanelValuePresentation` for the same converted, decimal-formatted value shown on the canvas, including completed geometry measurements and stable measurement failures; Text also searches paths and types.

Regex uses case-insensitive, culture-invariant matching and time limits.

Whitespace in regex patterns remains significant in both query execution and output-filter detection.

ListLength compares each transformed branch's count before type or invalid-only item filters; matching empty branches survive when no item filter excludes them.

Branch input accepts `0;0;1` or `{0;0;1}`, canonicalizes valid paths and reports malformed paths as query errors.

Invalid expressions publish an error and disable stale row actions.

Modes persist with the definition, default to Text for older files, and reset with the view.

`BetterGhPanel.Core` owns immutable snapshots, query options, stable source positions, filtering, cancellable sorting, and scalar statistics.

Query `RowPositions` indexes `Snapshot.Rows`; each row retains its path and original index within that branch.

Value sorting groups matches in snapshot branch order and stably sorts each branch independently.

Numeric comparisons use the presented number; nonnumeric comparisons use cached shared presentation text.

The canvas, range selection and copying all consume that same result order.

Snapshots use object identity for ownership checks, without separate version or row/branch identity fields.

The result also carries matching branch paths, including empty branches when browsing without item filters, for both statistics and typed output filtering.

Invalid finite numbers are excluded from numeric filtering and statistics along with null, NaN and infinite values.

`BetterPanelAttributes` keeps D and F viewport state in its private `_d` and `_f` `PaneView` instances (scroll offsets, visible counts, pane rectangles and captions).

`BetterPanelAttributes.Layout.cs` computes the immutable `PanelLayout` class of fixed controls and regions once per layout.

Pane rectangles remain in `PaneView`; set-details geometry stays in `CanvasSetDetails` because render and input also update it.

`BetterPanelAttributes.Controls.cs` owns the fixed-control hit table, click handlers and tooltip builders.

All paths walk one shared array order within named per-path steps, preserving nested-control winners and insertion points around split panes, menus, set details and painted warning targets.

`ComputePanelLayout` also updates the `_d`/`_f` pane rectangles, `_warningsHeight`, and `_singleLineFooter` in their original calculation order; it is not a side-effect-free helper.

Active scrollbar dragging uses one `_scrolling` flag, with `_dragPaired` selecting the pane.


## Outputs

Picking a comparison mode shows its values with `DisplayFilterValue` and an empty search expression; clicking the search field opens the optional filter picker.

Core filtering bypasses the comparison while retaining branch/type/invalid restrictions.

`PanelFilterValue` shares metric eligibility, circle circumference and presented-number rounding with comparisons; the canvas and metric copying use its formatter.

Geometry display retains the visible-row measurement queue.

`BetterPanelAttributes.FilterValues` renders list lengths once per matching branch, including empty branches, using transformed snapshot counts.

Scroll and jump addresses refer to branch rows in that view.

D/S choose Items or Values independently of the value display.

Values without a set are frozen typed property records at presented precision in displayed units; unavailable values retain a null slot.

Values output queries prepare offscreen measurements before publication.

Plain value display keeps visible-row measurement and does not wait for offscreen rows.

Statistics skip unmeasured values and use available presented numbers, including displayed metrics and properties; Numeric unique values group by numeric keys equivalent to presented text; Automatic keys use fast significant-digit rounding with exact midpoint fallback, and unique text is formatted lazily.

Automatic unique rows use the shared row formatter; their tooltip retains round-trip precision.

Legacy archives default to the ordinary item view.

The native host inventory focuses on geometry capture, resource ownership, drawing and clipboard integrity; routine filter and UI smoke fixtures have been removed.

`BetterPanelComponent` applies structural tree tools in order: Simplify, Trim, Flatten, Reverse.

Unit conversion follows, then the query filters rows and Create set groups the resulting displayed values.

With all operations and filters off, it forwards Grasshopper's typed input tree unchanged, including negative zero.

Original and presented scalar witnesses are captured once per solve and reused for tree conversion and currency checks; freshness still compares the live original bits.

A transformed overflow is unavailable, not invalid or null in source diagnostics; typed output retains a null slot.

`PanelTreeOperations` copies paths and branch lists before changing structure, preserving typed goo and upstream data.

Simplify removes shared path indices before Trim removes trailing path indices, merging coincident branches in source order; fully trimmed paths become `{0}`.

`PanelDisplaySet` groups full formatted strings per branch after filters, with original input addresses carried by `PanelSnapshot.SourceAddressAt`.

Its immutable result stores displayed values, stable per-branch set indices, original occurrence locations and transformed member positions, accumulated in the cooperative tree-order query.

`PanelOutputOperations.DisplaySet` emits unique frozen values; `SetItems` groups D/S members in `{path;k}` (flattened `{k}`).

`PanelPairedData.Output` uses those same groups for F in both modes.

List-length sets use `{k;path}` for member branches, including empty branches.

B and geometry actions expand all members.

Set row/order labels use k, independently of view sorting.

The saved `PanelOutputValues` enum defaults to Items; missing keys on old sets migrate to Values.

Selection explicitly tracks empty branch paths.

Set statistics describe emitted values, and sorting uses their numeric/text keys without changing wire order.

Geometry needed by set equality completes through the query measurement batch, including offscreen rows.

The component owns a structural copy of the transformed tree and captures a presentation snapshot before filtering.

Without Create set, query row positions select typed goo from that same captured tree; matching branch paths preserve empty branches when no item filter is active.

Output keeps transformed tree order independently of view sorting.

A pending or failed filter or set query outputs an empty tree.

Query changes and completion schedule a normal Grasshopper solution to update downstream objects.

An internal publication solve reuses the captured tree and query, avoiding repeated geometry calculation.

Normal upstream expiration clears that reuse flag, cancels obsolete work and invalidates the UI before replacement input arrives.

Snapshot identity, document ownership and request numbers guard scheduled output callbacks.

`SnapshotCapture` caches immutable summaries; the component retains current live goo for typed output, geometry actions and UI-thread measurement preparation.

Branch headings share the row action menu renderer.

Path, address and TSV copying extend `CopyValuesCoreAsync`, using its clipboard generation, selection/snapshot freshness fences and deferred commit.

Ordinary addresses use the original index; Create-set Items addresses use the actual D output branch {path;k} (or {k} with Flatten).

F heading path actions resolve the clicked D destination to its original F paths; F row addresses and TSV cells resolve selection positions through the pairing map to raw F paths, original indices and unconverted values.

Outlier warning counts apply only to D headings; F retains its D-count badge and emitted-versus-total count.

The warning strip reserves the split-caption budget in Full and Half.

Hover copy glyphs follow the complete drawn label and have a hit region only while visible; clipped labels have no glyph.

Warning clicks require the same painted-view and row currency fences as row actions; unique paths follow snapshot branch order.

Compact menus page all actions.

D/S retain their names in both output modes, while B/F keep their names and describe Results or Selection in their parameter tooltips.

`PanelPairing.Match` reserves every exact original path before pairing remaining branches in tree order.

Only original branch-count differences block pairing.

`PanelPairedData` holds that map, the transformed D-position → optional raw F-position lookup, original branch lengths, and original-branch destination paths.

A branch marker tree carries empty branch identities through the same structural tools as D.

The raw F snapshot includes every F item, including extras, so its existing capture/freshness fences cover all published and displayed values.

## Saved settings and compatibility

Width and height are canonical saved settings.

New panels use 360 × 360; existing dimensions and size presets retain their original behavior.

Preset check marks are derived from dimensions.

Serialization retains the legacy `PanelSize` key, and reads it as a fallback when dimensions are absent.

Missing tree tool settings default to disabled, preserving older definitions' output.

Document closure releases sessions; a tab switch closes the editor and clears automatic viewport measurement requests while retaining the snapshot and cache.

An operation already running can finish.

Automatic measurement resumes when the panel becomes visible; explicit filter batches may continue in the background, with viewport highlighting and camera framing suspended for the inactive document.

`CanvasPanelDisplay` is a separate saved presentation setting: Full uses the canonical height and Half derives half that height.

One header chevron toggles Full ↔ Half.

Display changes record undo and invalidate layout without solving or changing query options.

Wire grips use the canonical full-size coordinates in both modes; the visible body uses a derived top offset.

Resizing in Half maps the visible height back to the canonical full height.

Missing, invalid or previously Collapsed (`PanelDisplay = 2`) values restore Full for older definitions.

`PanelMinimized` is an independent saved boolean, defaulting to false for older definitions.

The header close button displays a 216 × 96 capsule at the canonical pivot, without overwriting Full/Half or the saved dimensions.

Capsule grips use its visible edges, and the enclosing bounds add only the grip allowance.

Restore derives the original rectangle and grips again.

A dedicated `GH_UndoAction` changes only the minimized flag, expires no solution, and preserves the session, typed output, selection, scroll and preview on undo/redo.

Transitions close owned drafts, dropdowns and row actions; minimized layout clears every expanded control and row hit region, and rendering cancels visible-row geometry requests.

Capsule summaries use `HasCurrentResult` before reading counts and show explicit busy, disabled or error states.

Host tests exercise both restore modes, persistence and legacy compatibility, exact grip positions, hidden targets, presentation-only undo/redo, and native render captures.

## Testing

Core xUnit tests run without Rhino on net48 and net8.0. They cover deterministic queries, numeric presentation, sets, pairing and selection. Native fixtures exercise capture, outputs, host interaction, drawing, geometry ownership and clipboard behavior in Rhino. See [contributor validation](../CONTRIBUTING.md) for commands.
