# Architecture

## Overview

Panel+ passes its input tree through Simplify → Trim → Flatten → Reverse, unit conversion and snapshot capture. Core queries the managed snapshot for filters, sets, statistics and view order. The component reconstructs typed D/S/B/F outputs from source identities, the canvas displays the current result, and Rhino preview/actions use its result and selection scope.

Core targets netstandard2.0 without Rhino; the host targets net48/net7.0 and references Core. Core references no Rhino or Grasshopper types; everything else lives in the host project, where `Runtime/` adapts Grasshopper and Rhino, `UI/` supplies canvas widgets and drawing helpers, and the component and attributes partials coordinate host events and lifetime. `Runtime/` and `UI/` are folders in one assembly, not layers enforced by the compiler, and a few references cross them: `PanelOrderLabels` (Runtime) uses `LabelCollisionGrid` (UI), and `CanvasTextInput` (UI) checks its owner against `BetterPanelComponent`.

Extension rules: keep pure behaviour in Core, typed transforms in Runtime, owned background work and publication behind the session, and host lifetime and settings in the mapped partials. New geometry providers must keep the production compatibility admission and queue ownership checks rather than bypass them. Selection consolidation remains subject to [reset and lifetime boundaries](#reset-and-lifetime).

| Component file | Responsibility |
| --- | --- |
| `BetterPanelComponent.cs` | Construction, ports, session wiring, scheduling, document lifecycle |
| `.Settings.cs` | Settings, undo, archive migration |
| `.Capture.cs` | Capture, freshness, publication fences |
| `.RowActions.cs` | Copy, framing, action lifetime |
| `.Outputs.cs` | D/S/B/F reconstruction, scope, caches |

| Attributes file | Responsibility |
| --- | --- |
| `BetterPanelAttributes.cs` | Shared state and rendering coordination |
| `.Layout.cs` | Fixed controls and bounds |
| `.Controls.cs` | Hit table, handlers, tooltips |
| `.RowView.cs` | Shared view index, headings and copy affordances |
| `.Input.cs` | Mouse/keyboard, focus, gestures |
| `.RowActions.cs` | Named menus, dispatch, copy status |
| `.SplitView.cs` | D/F mapping, scrolling, captions |
| `.SetDetails.cs` | Occurrence details |
| `.FilterValues.cs` | Projected values and branch-count rows |
| `.DataWarnings.cs` | Warnings and filter actions |

## Core model

### Snapshots and queries

`PanelSnapshot` holds managed presentation records, never live geometry. Query `RowPositions` index `Snapshot.Rows`; rows retain paths/original indices and `SourceAddressAt` carries original provenance through tree tools. Snapshot object identity owns results, without separate version or row/branch identity fields. Results retain matching branch paths, including admitted empties, for statistics and output filtering.

`PanelQueryEngine` owns filtering, cancellable sorting and aggregation. Asc/Desc stably sorts inside each branch, retaining snapshot branch order; ranges/copy consume this result order. Numeric comparisons use presented numbers, nonnumeric comparisons cached presentation text. Null, verified-invalid finite numbers, NaN and infinity are excluded from numeric filtering/statistics.

Search modes cover text/regex, scalar Value, Length/Area/Volume, ListLength, X/Y/Z and typed properties. Property categories include validity/identity, dimensions/counts, Boolean flags, text/GUIDs, calendar time/parts, colours/domains, topology/shape, complex-number parts/magnitude/phase, annotations, transforms/matrices, culture/location and reference metadata. `PanelProperties` owns labels/lookup; `PanelPropertyFilter` compares typed numbers, Booleans, text and calendar values without parsing tooltips. Snapshots union property keys once; null deferred entries advertise pending work. Display/copy/property sorting/set equality share immutable `PanelPropertyValue` records.

Text searches values, paths and types, retaining legacy numeric comparisons. Regex is case-insensitive, culture-invariant and time-limited; whitespace remains significant in matching/output-filter detection. Comparisons support relational operators/equality/inequality; Validity and Boolean use discrete choices. ListLength tests transformed counts before item restrictions and retains empties without excluding item filters. Paths accept `0;0;1` or `{0;0;1}`, canonicalize valid input and report malformed paths as query errors; invalid expressions hide stale results/actions.

Picking a comparison mode enables `DisplayFilterValue` with an empty expression, showing values without comparison while retaining branch/type/invalid restrictions. `PanelFilterValue` shares metric eligibility, circumference and the [number contract](#numbers-and-rounding). List-length display has one row per matching branch, including empties; scroll/jump addresses refer to these branch rows. Plain geometry display measures visible rows, whereas displayed-text queries, nonnumeric sorting, sets and Values output prepare eligible offscreen measurements before publication. Candidate collection validates queries and shares cheap restrictions through `PanelQueryScope`; scalar comparisons do not request geometry work.

### Sets, statistics and data checks

`PanelDisplaySet` groups full formatted values per branch after filters; Flatten makes one tree-wide branch. Its immutable result stores stable first-occurrence indices k, displayed values, transformed members and original occurrence addresses during cooperative tree-order traversal. Numeric keys preserve typed presented values, and required geometry completes before grouping. Sorting does not renumber k; selection/geometry actions expand every member. Occurrence badges expose original locations, and list-length sets can retain empty member branches.

Statistics use available presented numbers, including metrics/properties, and skip unmeasured values. Unique values merge by shown text: numeric keys are equivalent to that text, while Automatic uses significant-digit keys with exact midpoint fallback and lazy formatting. Automatic unique rows share the row formatter and retain round-trip tooltip precision. Set statistics describe emitted values rather than expanded source members.

`PanelDataWarnings` collects disjoint null/invalid/unverified counts and branch-length histograms before restrictions during budgeted traversal. Majority length requires at least four branches and 75% agreement, including empties. Paint reads aggregates rather than scanning input. Warning actions clear search/type/invalid/path restrictions in one Undo before applying the corresponding existing filter.

### Selection

Component-owned `PanelRowSelection` uses snapshot positions and explicit empty-branch paths, with ranges in current result order. Replacement input cancels live previews and rebinds committed positions only when paths/branch lengths match and Create set is off. Structural changes/set recomputes clear selection; output-only solutions preserve it. Filtering removes hidden members, sorting preserves identities, and unchanged membership or range-anchor-only changes do not schedule output.

Clicks, Ctrl/Cmd toggles, Shift ranges, headings and Alt/Option same-index gestures select current matches. Select all includes offscreen matches and is idempotent. Row/heading double-click clears selection, blank canvas/empty panel clicks retain it, and empty-table double-click does not open search. Right-click retains multi-selection; clearing selection cancels copy/closes actions while keeping Results preview. Document switches clear selection; obsolete snapshots, pending queries and stale hits reject actions.

## Numbers and rounding

### Presented numbers

The pipeline is **units → round up by factor → decimal places**. Rows/copy/comparisons/Stats/Values/numeric Items share the presented number; geometry is never scaled. Factors apply to plain numbers, geometry metrics, X/Y/Z and dimensional properties including angles. Counts, indices, dates, colour channels, axes and matrix entries ignore factors; integer properties stay integers. Tooltips retain raw values/rounding details.

Without a factor, explicit decimals use half away from zero on a faithful 15-digit decimal cast when it round-trips: 2.675 at two places becomes 2.68. Otherwise `PanelNumber` rounds the exact binary rational, including subnormals. Values ≥2^53 retain exact integer digits. Invariant fixed/compact formatting avoids runtime double-formatter differences, and decimal-to-double conversion corrects scale-dependent last-bit cast errors using exact rational arithmetic. Operands share that conversion, with an integer-rational parser outside decimal range.

With a factor, the guarded path uses input/factor shortest round-trip decimal forms with System.Decimal remainder/ceiling arithmetic. It ceilings to the factor, then to explicit decimals when required by the grids, followed by one correctly rounded double conversion. Faithful decimal casts bypass parsing when they yield the same operand. On .NET Framework the shared formatter supplies round-trip digits because native `R` is not shortest. Thus 0.1+0.2 = 0.30000000000000004 advances to the next step rather than being treated as 0.3.

NaN/infinity, magnitudes ≥2^53, nonzero magnitudes <1e-27, unrepresentable factors, inexact decimal inputs and overflow retain the base factor/decimal path. That factor path guards binary resolution/huge quotients, avoids tiny-positive underflow and does not lower finite input. Re-application is stable only while the factor substantially exceeds binary precision, roughly |v| < 1e12 for 0.001 steps. Incompatible factor/decimal grids can advance again on re-application; universal idempotence is not guaranteed.

Automatic keeps full wire precision after conversion/factor while rows use G12 scalar/G6 geometry text. Automatic Create set Values intentionally uses compact displayed grouping/values. Explicit decimals range 0–12; transformed overflow is unavailable with a null output slot, without changing source-invalid/null diagnostics. Both test runtimes run the same seeded 100,000-value, 0–12-place text/bit fingerprint and independent arithmetic oracle. These check text, wire bits and operands across runtimes.

### Unit adapters and extension rules

`PanelUnitOperations` transforms valid Number/Integer goo after tree tools, copying paths/lists and retaining other items/empties. Decimal-only changes preserve Integer goo; conversion/factor can produce Number goo. Invalid numeric goo, including finite unset sentinels, is preserved rather than scaled into validity. Original/presented scalar witnesses are captured once per solve and reused for conversion/currency; freshness still checks original bits. Defaults are units/factor off and Automatic decimals.

`PanelUnits` catalogs arithmetic, labels, symbols, source units and dimensions together, also supplying menu count. Modes distinguish mm↔m, mm²↔m², mm³↔m³ and rad↔deg. Geometry conversion requires matching source units/dimension and reformats cached raw metrics without changing geometry output. Persisted enum values are explicit/append-only; unknown values fall back to no conversion. Preserve division rather than reciprocal multiplication, and cache guarded factor conversion/per-solve transforms.

## Session and background work

`PanelSession` owns snapshot/options/result. `ImmediateQueryBudget` supplies a 120 ms soft deadline to `PanelQueryWork.TryComplete`, measured with a monotonic stopwatch. Checkpoints cover empty branches, rows, grouping, sorting and construction; expiration hands the same iterator to a worker retaining scan/statistics/merge-sort state. No scan restarts or partial result publishes, and worker completion bypasses deadline/handoff checks inside loops. Individual strings/copies/allocations can exceed the cooperative budget, so regex/native-measurement queries start in background.

`GeometryMeasurementQueue` owns prepared work, cancellation and handoff; the session retains query state/publication. After an initial 40 ms scrolling delay, UI preparation reconstructs bounded curves or duplicates admitted compatibility geometry; one worker owns/measures/disposes copies without live goo/document access. Rows continue without another scroll delay, with redraws coalesced over 60 ms. Each session has at most one geometry operation shared by visible rows and explicit query/copy batches. The UI thread freezes a completed measurement map before the final managed query.

Only canvas-intersecting rows enter automatic requests. Scrolling/panning replaces the queue; offscreen panels, minimized rendering and zoom below row display clear it, while explicit batches include offscreen rows. Success/failure caches by source position and repaints without a query/solution. Running native mass-property calls cannot be interrupted: they finish/dispose, then stale publication is rejected. New solutions normally invalidate caches; unit-only solves retain fully witnessed geometry/context matches, compatibility results without witnesses remeasure, and output-only solves preserve caches.

`HasCurrentResult` gates rows/hits/counts/summaries/statistics/tooltips. Busy panels show Recomputing… with a session timer only while visible/busy; completion/disposal stops it. Disabled components suspend input/cancel work and show Disabled until enabled input, rejecting late workers/query restarts. Errors hide stale results; cancellation/request/snapshot/document fences protect publication. `QueryChanged` dirties changed options, progress/completion invalidates directly, and equal options do not rescan.

Document closure releases sessions. Tab switches close editors/automatic requests but retain snapshot/cache; running work and explicit filter batches may finish. Highlighting/framing suspend in inactive documents and automatic work resumes when visible. New providers must retain UI dispatch, ownership/context checks and safe unit-change retention, as detailed below.

## Capture and geometry safety

`SnapshotCapture` freezes summaries/properties; live goo remains separately retained for typed output, actions and UI preparation. `StockGooSupport` catalogs exact holders with independent validity, managed/native capture, preview, measurement, shape-only load and group-reference facets. Native entries own typed readers/exact inner types; managed copy/equality/property readers remain explicit. Core's `StockTypeLabels` is only a name-to-label table. Validity distinguishes verified valid/invalid and unverified; invalid rows retain reasons, while Material/Field rows do not traverse assets/elements and remain unverified.

Witnesses check source graphs/context before work/publication. Culture freezes name/English-name strings; matrices retain at most 4,096 cells and paths at most 4,096 indices, charged before retention to the capture-wide 100,000-visit budget. Rechecks charge the same sizes. Bounded numeric summaries follow active precision for display/search/copy/sets; matrices/transforms share formatting. Empty text displays `<empty>` without changing semantics, and geometry groups retain name/direct count for search/sets.

Text Dots display text, capture X/Y/Z and screen-pixel font size, and group by displayed text. Dimensions use unit-aware length/angle metrics with the radians-to-degrees convention. Annotation text height is omitted because stock getters strip dimension style; native text beyond the preview limit is rejected. Annotation/hatch/block/detail inspection covers captured shape only, without traversing/previewing/copying separate styles/patterns/definitions. Referenced shapes require document/object context.

`GeometryHints` captures analytic values/stored properties without general inline measurement. Worker results augment descriptions with length/linearity, trimmed area, volume, topology, degrees and planarity; pending/failure states retain metadata. Tooltips show metrics once then labeled details; planarity uses RhinoCommon's default zero tolerance. Eligibility includes circle circumference, surface/open-Brep area, solid-Brep volume and mesh area/closed volume, excluding unsupported secondary metrics. Paint submits positions and never duplicates/measures inline.

Read [measurement safety](geometry-measurement-safety.md) for reconstruction/admission/witnesses/retention and [preview safety](geometry-preview-safety.md) for borrowed resources/callbacks/disposal. Compatibility duplication can invoke installed UserData callbacks with unbounded native work/allocation. Workers cannot read live goo/documents. Immutable summaries do not replace source freshness, preparation ownership and publication checks.

## Preview and Rhino actions

`PanelPreviewMode` saves under that key and defaults to `Selected` (UI: Selection) for missing/unknown values, with Undo but no solve. Results uses `FilteredResults`; Off clears caches, and sets expand all members. Borrowed previews clear on query start, new solutions, saved reads, unload/removal and document switches; publication synchronizes scope independently of painting, including offscreen matches. Reactivation restores current Results where appropriate; empty/failed results draw nothing. Repaint/sort/filter/selection/mode changes never frame, bake or change Rhino object selection.

`PanelGeometryHighlight` uses native Grasshopper callbacks without base full-tree fallback. Selected rows use selected wire/material settings; others use normal colours even if the component is selected. Clipping covers drawn scope only and is empty when off/hidden/locked/suspended. Selected wires receive native clip-depth bias/thickening when not already applied by selection, restoring transforms afterward. Other components' previews remain independent.

`PanelOrderLabels` uses zero-based original indices or stable k for all set members; filters/sort/selection do not renumber. Repeated indices/coincident anchors use selected-first collision handling, and caches discard obsolete/inactive scopes. Tooltips include path/index when labels are enabled. Explicit Frame selection uses preview bounds including custom preview goo and moves only the camera. Geometry copy creates a private 3DM archive with source units/tolerance for Windows Rhino Paste, skips/reports unsupported/invalid/non-geometric rows and leaves clipboard untouched when none are supported.

One `Runtime/ClipboardGeneration` governs text/geometry copy. Value copy follows result order, emits unit-free invariant numbers/metrics and full text despite clipping, and shares summaries for other rows including invalid markers/coordinates. Offscreen measurement shares the session worker; unavailable measurements fail the entire value copy without replacing clipboard. Cancellation, selection/snapshot freshness and deferred UI-thread commit reject stale writes after new snapshots/disposal. Path/address/TSV copy extends `CopyValuesCoreAsync` through the same fences, and geometry copy never adds/deletes document objects.

## Canvas UI

### Layout and controls

`PanelLayout` is an immutable class computed once per layout for fixed regions. The search mode chip and Units widths come from label widths measured at paint (`_searchChipWidth`, `_unitsWidth`); `Render` re-runs `Layout()` when they change. `ComputePanelLayout` also updates `_d`/`_f` rectangles, `_warningsHeight` and `_singleLineFooter` in calculation order, so it is not pure. Private `PaneView` instances own offsets, visible counts, rectangles/captions; `_scrolling` marks dragging and `_dragPaired` selects the pane. Set-details geometry stays in `CanvasSetDetails` because render/input update it. Core's `CanvasPanelSize` validates dimensions/presets.

`Controls.cs` builds one ordered `ControlEntry` table of rectangles, visibility/hit predicates, handlers, tooltips and cursors. Click/hover/tooltip traverse that array within named steps, preserving nested winners and precedence around menus/panes/details/warnings. `ControlStep` values are `None`, `Header`, `BeforeDetails`, `BeforeTrack`, `Main`, `AfterStaleList`, `Capsule`, `BeforeStatisticsMenu` and `BeforeRowActions`; cursor entries use Main. Add a control via a layout region and table entry with appropriate per-path steps and named handlers/tooltips. Keep nested clears/filters before fields, SelectAll before statistics, Footer after controls; painted warnings/rows retain dedicated freshness checks.

New panels use 360×360 canonical dimensions. Full uses saved height; Half derives half height, maps resizing back to full and keeps grips at canonical coordinates with a derived visible top offset. Minimize uses a 216×96 capsule at canonical pivot with visible-edge grips and only grip allowance in bounds; restore derives original geometry. Presentation-only Undo preserves session/output/selection/scroll/preview, and transitions close drafts/menus/actions and clear hidden hits. The padded move target delegates to Grasshopper; resize is 24×24, hover retains its cursor, and footer ends 28 units before the right edge.

Wire picking uses a 12-unit radius and reverse parameter order outside custom bounds. Neighbouring grips must stay outside that radius and clear the Half resize corner; controls/rows/tracks/menus stay at least 14 units inside the relevant side. Capsule outputs have 24-unit spacing. Split doubles canonical width with presentation-only Undo. Linked scrolling snaps F to D on relinking; unlinked partner reveal translates pane indices.

### Rows, menus and editors

`RowView` supplies shared indexing/headings. `CanvasRowRenderer` uses 17-unit rows, font-ascent baseline alignment and gutter capacity based on full snapshot count, avoiding paint scans/column resizing. Source order shows original indices; sort adds one-based global result numbers beside smaller originals, and Jump targets global numbers. Headings replace inline paths when height permits; compact layouts retain paths and hits store view numbers for direct tooltips. Numeric cells are right-aligned monospaced text, others left-aligned; numbering affects neither saved state nor wire order.

Headings share row-action rendering for paths/addresses/tables. D addresses use original indices except set Items uses actual output groups; F addresses/TSV use raw paths/indices/unconverted values and heading copies resolve D destinations to F originals. Unique paths follow snapshot branch order; glyphs follow complete labels and clipped labels have no glyph/hit. Only D headings show outliers; F retains pairing/count badges. Warnings show only in expanded D with room for one row, reserve split-caption space in Full/Half and hide in compact layouts.

`CanvasMenu` owns layout/selection/hover/paging without navigation overlapping choices. Vector tree glyphs use canvas units/muted-accent colours and restore drawing state; footer controls share vertical centre and Stats uses status font/compact gap. `CanvasTextInput` defers decisions until host events finish: Enter/OK submit, other closes accept only changed valid text. Esc, Cancel, Panel+'s own closes and dead/disabled/changed owners abandon. Live-search abandonment restores committed query/selection/scroll; saved options exclude temporary previews. Preview edits update only the view: wire publication, document modification and downstream solutions wait for commit.

Clicking inside focuses the panel without clearing selection; a green ring marks focus. Esc clears selection first, then releases focus; unfocused keys/wheel go to Grasshopper. Arrows/ranges, page/home/end navigation, search shortcuts and copy operate through `PanelCanvasInput`, and modified wheel gestures remain canvas zoom. Dark canvases use a matching palette, and low zoom uses item/branch summaries instead of rows. Long values open in a read-only scrollable window from the row menu.

### Reset and lifetime

Clear filters clears search/mode/type/path/invalid-only/sort/selection/scroll in one Undo, retaining tree tools/units/rounding/decimals/presentation, subject to the hidden Values-switch fallback described under outputs. Reset all additionally restores tree tools, units/factor/decimals, Items/Values and B + F follow, keeping Preview mode/item order/split/size. It recomputes original input when tools were active, closes menus before dispatch and abandons owned editors even when options already equal defaults. Tree tools record Undo, dirty the document, expire solutions and clear selections where paths change.

Closing menus releases snapshot/type choices/statistics; handlers retain chosen paths/types first. Expiration/disabling/closure release painted-result/borrowed-source references before another render. Capsule layouts clear expanded hits and gate summaries through `HasCurrentResult`. Selection gesture cancellation/highlight/repaint effects remain distinct from completion synchronization/document changes when considering consolidation.

## Outputs and D|F pairing

### Ports and publication

| Port | Contract |
| --- | --- |
| D | Matching Items or presented Values in transformed tree order |
| S | Selected Items/Values at D group paths; empty with no selection |
| B | Original D-shape Boolean mask, expanding set membership |
| F | Raw paired F at D destinations, following Results or Selection |

Items is default; Values is independent of display and B/F scope. The switch appears for values/sets and falls back to Items when hidden, with Undo restoring both settings. Values tooltips distinguish explicit precision (exactly as shown), Automatic with factor (rounded full precision), Automatic without factor (full precision), and sets (displayed set values). Non-set Values freezes typed records at presented precision with null unavailable slots; [Automatic precision](#numbers-and-rounding) still applies. Set Items uses `{path;k}` (Flatten: `{k}`), set Values emits unique values per source branch, and F uses group paths in both modes. List-length sets use `{k;path}` for D/S member branches, including empties.

B + F follow defaults to Results or can use Selection; names remain D/S/B/F with scope in B/F tooltips. With operations/filters off D forwards input unchanged, including negative zero. Structural tools copy paths/lists first: Simplify removes shared indices before Trim removes trailing ones, merging coincident branches in source order and using `{0}` for fully trimmed paths. `ApplyWithSources` carries original addresses with values; paths compare via `GH_Path.CompareTo`, not object equality. Retained transformed structure selects typed goo by snapshot positions independently of view sort.

Pending/failed filters/sets publish empty trees. Query edits/completion schedule normal solutions; internal publication solves reuse captured tree/query rather than recalculating geometry. Upstream expiration clears reuse/cancels work/invalidates UI, with snapshot/document/request fences on callbacks. D/B/F caches are per result, selection-only solves reuse them, empties skip scans and B allocates one Boolean per original item. `PanelQueryPolicy` separates equality/filter detection/output equivalence: sort is output-equivalent, explicit decimals/factor are not.

### Pairing identities and edge cases

`PanelPairing.Match` reserves exact original paths then pairs remaining branches in tree order. Only original branch-count differences block pairing; unequal lengths work, and unusable wired F greys the split chip with mismatch guidance. `PanelPairedData` holds original lengths/destinations and transformed D-position → optional raw F-position maps. A marker tree preserves empty identities through D tools, and split's raw F snapshot includes extras under freshness fences. Closing split releases F presentation references/summaries without disabling F output.

| Boundary | Identity rule |
| --- | --- |
| Pair construction | Explicit map, no fabricated rows/placeholder goo |
| Capture / split | Raw F tree, valid map, current D result |
| Painter / tooltip / full value | Optional mapped F row; missing cells empty |
| Captions / headings | F paths, fallback pairing, emitted/total counts, original D-count badge |
| Scroll / keyboard / reveal | Augmented index translation; extras unselectable |
| Ordinary F copy | Raw mapped value, empty line for missing F |
| D/S traversal | Shared typed filter/selection; F uses original scope/destinations |
| Set F | Equal lengths follow members, unequal lengths emit whole branch once per group |
| Publication | Query-cached mapped F, selected-scope rebuild, final capture fence |

Equal pairs follow item membership; scoped unequal pairs emit whole F branches once per destination group. Retained indices below original D length are “paired with hidden D rows”; only those beyond it are “only in F.” Extras are muted/nonselectable opposite D spacers; missing runs label the first empty cell. Raw F indices survive Reverse/filter/sort. Reverse reverses the paired prefix with D but leaves extra tails after it; merged tails follow merged D rows in original branch order, and sort remains presentation-only.

Unequal F duplicates into every set Items group containing an original D member. Item/list-length sets each have one bounded single-line F summary in emitted order with four cached preview positions and total/remaining count, without hidden/only-in-F notes. Selection scopes the complete group and copy traverses every emitted value, including Ctrl+C on F list-length sets, without paint expanding members. Empty D partners require a retained destination, no item filters and Create set off. Path/list-length restrictions preserve them, including destinations merged with nonempty branches.

Output/panes/summaries/counts share empty-partner admission. Summaries expand unequal branches but use current matches for equal pairs; named heading counts cache with/without empties. Augmented indices cache offsets/next-D spacer positions by result/map identity; equal-length trees/sets use `ViewIndex` without augmented allocations. Pair indices resolve originals directly, gutters/tooltips keep raw F index with sorting flag, and filtered headings use scoped emitted/total metadata.

## Saved settings and compatibility

Query keys are `PanelSearch`, `PanelSearchMode`, `PanelDisplayFilterValue`, `PanelType`, `PanelBranch`, `PanelPrefix`, `PanelInvalid`, `PanelSort`. Presentation keys are `PanelSize`, `PanelWidth`, `PanelHeight`, `PanelDisplay`, `PanelMinimized`, `PanelSplit`, `PanelPreviewMode`, `PanelShowItemOrder`. Data keys are `PanelTrimDepth`, `PanelFlatten`, `PanelReverse`, `PanelSimplify`, `PanelCreateSet`, `PanelUnitConversion`, `PanelDecimalPlaces`, `PanelRoundUpFactor`, `PanelOutputScope`, `PanelOutputValues`; new saves write `PanelOutputSchema = 1`. Enum values remain stable and modes append.

Dimensions take precedence with legacy `PanelSize` fallback and dimension-derived presets. Missing tools default off, search to Text/ordinary display, units/factor off and decimals Automatic. Missing/invalid/previously Collapsed `PanelDisplay = 2` restores Full; minimized/split default false. Output defaults to Items except old sets missing the key migrate to Values. Preview fallback is in [preview](#preview-and-rhino-actions), and presentation changes retain canonical dimensions.

Before host parameter reading, migration upgrades only known unversioned one-input/one-output archives. It preserves D chunks/GUIDs and original archive owner for relative paths/diagnostics, supplies added-port defaults and leaves source archives unchanged. Genuine missing chunks still report errors. Feature removal must account for enum/key/archive compatibility; component/assembly identity survives the package rename.

## Testing

Core xUnit runs without Rhino on net8.0 and, on Windows, net48. Deterministic yield policies test resumable scans/empty branches/sort/cancellation/handoff without timing-sensitive assertions; fingerprints/oracles check numeric consistency. Pure canvas/menu/size policy uses Core and explicitly linked drawing policy. Native fixtures test capture freshness, ownership/disposal, drawing, clipboard/Paste, outputs and host/UI interaction, with reflection isolated in the adapter. See [CONTRIBUTING](../CONTRIBUTING.md#validation) for commands/fixture selection and [packaging](packaging.md) for build/staging.

## Known limits

Native geometry clipboard supports Windows standard units; Mac copying/custom model units are unsupported. Native Mac and Windows Framework host execution remain coverage gaps despite both builds and Windows Framework Core tests. Installed-host preview checks do not establish universal GPU/driver completion, and compatibility duplication can invoke unbounded callbacks as safety docs explain. These limits are separate from managed query guarantees.

Historical GitHub downloads were refreshed to remove CodeView paths without changing code or version, but McNeel's Yak registry still serves original packages with local paths: published versions cannot be overwritten, and yanking hides searches while exact-version downloads remain. Removing stored copies requires McNeel support; see [Yak CLI reference](https://developer.rhino3d.com/guides/yak/yak-cli-reference/#yank).

The archived `prototype/` is a loopback-served visual study with its own generated-data engine, histogram and paging; removing it is a product decision.
