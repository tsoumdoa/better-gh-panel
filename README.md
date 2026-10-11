<img src="assets/icon.svg" width="64" height="64" alt="">

# better-gh-panel

Created in [T3 Code](https://t3.codes).

**Panel+** is a Grasshopper data inspector for Rhino 8 on Windows and Mac. This repository contains the native MVP and the original interactive HTML design study.

## Native MVP

Find **Panel+** in **Params → Util** after installing the local Yak package. Connect any typed tree to **D**. With tree tools and unit conversion off, the output forwards that tree unchanged. Searching, filtering and sorting only affect your view.

The public Yak beta is **`better-gh-panel 0.1.12-beta.1`**. In Rhino 8, run `PackageManager`, enable prereleases, and search for `better-gh-panel`. Or close Rhino and run `yak install better-gh-panel 0.1.12-beta.1`, then restart. Managed Grasshopper tree checks and Windows Rhino 8.35 host checks pass, including the single display control and fixed wire connections. Mac runtime validation is pending.

Beta patch **`0.1.12-beta.1`** adds Grasshopper-style green highlighting for selected rows, branch selection and framing, and compact filters for numeric values, length, area, volume and regular expressions. Use the search dropdown to choose a mode and comparison. Select rows with Ctrl/Cmd-click or Shift-click, or click a branch heading to select its matching rows. Right-click for framing and value/geometry copying.

The panel measures visible curves, surfaces and Breps automatically in the background. Rows show **measuring…** while pending, then add the cached length, area or volume to the existing geometry summary. Trimming, curve degree, segment count and Brep face count stay visible after measurement; curves and single-face surfaces also gain shape or planarity hints. For example, a row shows **Srf · untrimmed · planar · A 100 mm²**. Hover for labeled details such as surface U/V degree or Brep edge and vertex counts; the metric appears once at the top. No Measure button or row selection is needed. **Units** converts geometry metrics, including volume (mm³ ↔ m³). Invalid geometry has a visible **Invalid** label and a tooltip with its reason.

**Simplify**, **Trim**, **Flatten**, and **Reverse** change the typed output and run in that order. Click Trim and enter a nonnegative depth (0 disables it); click the other tools to toggle them. Simplify removes shared path indices before Trim removes trailing path levels and merges matching branches; fully trimmed paths become `{0}`. Flatten merges into `{0}`, and Reverse reverses items within each output branch. Upstream data stays intact. Tool settings are saved with the definition and support undo. **Reset** restores the original incoming data: it disables every tree tool and unit conversion, restores automatic decimals, clears search, filters, sorting and row selection, and scrolls to the top. It closes dropdowns and discards pending text edits on the first click. One Undo restores the previous settings. Panel dimensions, display mode and wire connections are retained. The component menu also offers **Reset panel view** and **Reset tree tools** for narrower resets.

New panels start at **360 × 360** (1:1). Saved sizes and legacy size presets are preserved. All interaction stays on the canvas; the popup inspector has been removed.

One small chevron in the header toggles **Full ↔ Half**. Full restores the saved height and Half shows half that height. Both connection points stay fixed. Width, filters, selection, and data stay intact. The display mode is saved with the definition and supports undo. Resizing in Half updates the full height proportionally. Definitions saved with the removed Collapsed mode reopen in Full.

The panel uses a compact spreadsheet layout: one search/filter row and a small footer dock with the original Grasshopper **Trim**, **Simplify**, **Flatten**, and **Reverse** icons. Hover for each tool's name and trim depth; green icons indicate active tools. Type hints cover Grasshopper geometry and primitive data: **Pt**, **Srf**, **Mesh**, **Crv**, **Brep**, **SubD**, **Vec**, **Num**, and more. The filter summarises the two most common visible types, with a remaining-type count, and its tooltip lists full names and counts. Mixed and invalid-only rows show compact type labels. Geometry summaries already name their type, so they use that space for metadata instead of repeating a type label. The menu retains full names, and other built-in or third-party types keep their own names. At the default size, the table has 41% more area than the previous layout. The icons are original Grasshopper assets, used for this Grasshopper plug-in under [McNeel's icon usage terms](https://developer.rhino3d.com/guides/grasshopper/grasshopper-icons/).

The small **Units** dropdown at the bottom right opens upward inside the panel. Choose **mm ↔ m**, **mm² ↔ m²** (area), **mm³ ↔ m³** (volume), or **rad ↔ deg**. Conversion changes Number and Integer output values; converted integers become Numbers so fractions are preserved. Text, geometry, nulls, and empty branches pass through. Geometry metrics use the same menu for display only, applying a conversion only when its source model units and dimension match. Length, area and volume are separate: 1,000 mm, 1,000,000 mm² and 1,000,000,000 mm³ become 1 m, 1 m² and 1 m³ respectively. Conversion follows the tree tools, and search, sorting, and statistics use converted numeric values.

The same dropdown offers **Decimals: automatic** and **Decimal places…** (0–12). Decimal places format geometry metrics, numeric rows, copied numeric values, and statistics without rounding the values on the output wire. Conversion and decimal settings are saved with the definition and support undo. Existing definitions default to no conversion and automatic decimals. The header Reset clears these settings too; the explicit Reset panel view and Reset tree tools menu commands retain them. Dropdowns use compact widths, aligned checkmarks and arrows, a divider between conversion and formatting, and hover highlights. Paging keeps every option reachable even in a minimum-size Half panel.

The resizable canvas panel shows grouped branch headers and compact rows, with search, numeric comparisons (`> 800`, `<= 25`, `= 10`), type/invalid filters, value sorting, branch selection, exact branch jumps such as `{0;2;4}`, row jumps, a logical scrollbar, and numeric statistics. Click the search field and press Enter to apply a query. Its dropdown offers **Text**, **Regex**, **Value**, **Length**, **Area**, and **Volume**. Measurement modes offer greater than, at least, less than, at most, equal, and not equal comparisons. You can also edit expressions directly (`>= 25`, `!= 0`); a number alone means equality. Text retains the original plain-text and numeric-search behavior. Regex matches captured value text, case-insensitively, with execution limits and readable errors for invalid or overly expensive patterns. Addresses describe the output tree after the active tools. Drag the bottom-right corner to resize; size and view settings are saved with the definition.

Geometry filters compare full-precision measurements in the units shown by the panel; decimal formatting never changes matches. **Length** covers lines, arcs and curves, and uses circumference for circles even when their row summary shows radius. **Area** covers rectangles, surfaces and open Breps. **Volume** covers boxes and closed solid Breps. These filters use the panel's primary geometry measurements; secondary measurements such as solid surface area, mesh measurements and unsupported geometry types are excluded. Filtering measures eligible offscreen rows through the same background queue before publishing the result. Changing the query cancels obsolete work. Type and branch restrictions combine with the measurement filter; the output tree stays unchanged.

Click a geometry row to frame it in Rhino's active model viewport and highlight it with Grasshopper's selected preview color (green by default). Ctrl-click (Cmd-click on Mac) toggles rows, and Shift-click selects a range in the current sorted and filtered view. Selected geometry is framed together; clicking an already-selected row keeps the group. Other geometry retains its normal preview color. Choose a branch in **Paths**, enter an exact path, or click a branch heading to select, highlight and frame its matching rows, including offscreen rows. Branch filters focus once after the result is ready; sorting and repainting do not move the camera. Clearing Paths clears the branch selection without framing the entire tree. Highlighting follows Grasshopper preview visibility and clears on reset, new input and document switches. It does not select or bake Rhino document objects.

Right-click a row for **Frame selection**, **Copy value(s)**, or **Copy geometry for Rhino**. Double-click also copies selected values. Copies follow the displayed row order. Numeric values and geometry measurements copy as numbers only, using the selected conversion and decimal formatting. Other rows retain their full text. If an area, length or volume is still pending, copying waits for it, including selected rows offscreen; a failed measurement leaves the clipboard unchanged. Changing selection, query, units, or input cancels a pending copy.

**Copy geometry for Rhino** places supported valid geometry on the native Windows clipboard for Rhino's normal **Paste** command. It preserves source model units and geometry coordinates; numeric display conversions do not scale geometry. Non-geometric, invalid and unsupported rows are skipped and counted. Copying does not bake objects into the Rhino document. Mac native geometry copying and custom model unit systems are currently unsupported; value copying and viewport framing remain available. New solutions clear row selection; filtering drops hidden selections, and sorting retains the selected rows.

![Compact Panel+ canvas with native tree-tool icons and row type hints](docs/images/panel-tree-tools.png)

[See the compact units menu](docs/images/panel-units-menu.png).

[See the filter modes](docs/images/panel-filters.png), [comparison choices](docs/images/panel-filter-comparisons.png), and [native selected geometry preview](docs/images/panel-selection-preview.png). The preview shows green selected curves and tinted selected mesh faces beside normal red geometry, with upstream previews enabled in both drawing orders.

[See the asynchronous geometry metric](docs/images/panel-measure.png) and [invalid geometry label](docs/images/panel-invalid.png).

Primitive values, points/vectors, and cheap geometry properties have cached summaries. Rows automatically show analytic measurements for lines, arcs, circles, rectangles and boxes. General curves, surfaces and Breps request length, trimmed area or solid volume when their rows are visible or explicitly selected for value copying. Painting queues positions; after a short delay, the UI thread duplicates one geometry and a background worker measures that private copy. Each panel runs one measurement at a time. Scrolling and canvas panning replace queued work so rows outside the visible canvas are skipped; an operation already running finishes and releases its copy. Results, including failures, remain cached until new geometry is solved. Obsolete results cannot enter a newer solution or a closed panel. Units and decimal changes reuse raw measurements, including pending work when sources still match. Metrics start in Rhino model units; matching conversions change their display only. Invalid rows have a text label and captured validity reason. Third-party data keeps a type summary; full text is formatted on demand when copied. Text search operates on captured summaries, excluding asynchronous metrics. Numeric statistics include finite Number/Integer values, and exclude NaN and infinity. This MVP inspects connected data; it does not author text like an unconnected stock Panel.

### Build and package

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

The beta artifact is `artifacts/better-gh-panel-0.1.12-beta.1/better-gh-panel-0.1.12-beta.1-rh8_0-any.yak`. Packaging follows HopperWire's AECTooling metadata and `net48` / `net7.0` layout. See [installation and publishing preparation](docs/packaging.md). The packaging script builds locally; public publication uses an explicit Yak push.

The native plug-in compiles for both Rhino runtime modes; the C# query harness covers filters, stats, stable sorting, empty branches, cancellation, and large data. Mac runtime validation requires a Mac with Rhino.

Run the local checks before opening or merging a PR:

```text
npm run check
```

This runs the core and packaging checks, builds and stages both Rhino runtimes, and checks for unused parameters, private members, assignments, and imports. The diagnostics are explicitly enabled in `.editorconfig`. It runs locally on Windows or Mac and stops on the first failure. It does not launch Rhino or publish a package. GitHub Actions CI is disabled.

Periodic reviews still need to trace public APIs and reflection consumers and assess duplicate code and test value; static analysis cannot establish those on its own.

## Archived HTML design study

Run `npm start`, then open http://localhost:5173. No dependency installation is needed. Use the **Canvas / Inspector** switch to compare the compact component and expanded explorer.

Try searching `> 800`, filtering **Nulls**, sorting numbers, or entering `{0;2;4}` in the branch search and pressing Enter. The sample size menu switches between 65,536 and 262,144 generated items. `/` focuses search, `G` focuses branch search, and Escape resets the view. Select a row to inspect and copy its value. Filters and sorting only affect the view.

The files in `prototype/` are a preserved visual reference using generated data independently of Rhino. Its query behavior is illustrative; the C# core defines native behavior. See [the current architecture](docs/current-architecture.md) and [the original proposal](docs/architecture.md).
