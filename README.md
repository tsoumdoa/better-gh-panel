<img src="assets/icon.svg" width="64" height="64" alt="">

# better-gh-panel

Created in [T3 Code](https://t3.codes).

**Panel+** is a Grasshopper data inspector for Rhino 8 on Windows and Mac. This repository contains the native MVP and the original interactive HTML design study.

## Native MVP

Find **Panel+** in **Params → Util** after installing the local Yak package. Connect any typed tree to **D**. With tree tools and unit conversion off, the output forwards that tree unchanged. Searching, filtering and sorting only affect your view.

The public Yak beta is **`better-gh-panel 0.1.8-beta.1`**. In Rhino 8, run `PackageManager`, enable prereleases, and search for `better-gh-panel`. Or close Rhino and run `yak install better-gh-panel 0.1.8-beta.1`, then restart. Managed Grasshopper tree checks and Windows Rhino 8.35 host checks pass, including the single display control and fixed wire connections. Mac runtime validation is pending.

This beta patch is **`0.1.8-beta.1`**. Geometry rows now show useful compact hints: line and arc length, circle radius, primitive area/volume, curve degree and closure, surface trimming, Brep closure and face count, and mesh vertex/face counts. Hover a curve for its shape and length, a surface or open Brep for area, or a closed Brep for volume. Hover measurements are cached until the next solution.

**Simplify**, **Trim**, **Flatten**, and **Reverse** change the typed output and run in that order. Click Trim and enter a nonnegative depth (0 disables it); click the other tools to toggle them. Simplify removes shared path indices before Trim removes trailing path levels and merges matching branches; fully trimmed paths become `{0}`. Flatten merges into `{0}`, and Reverse reverses items within each output branch. Upstream data stays intact. Tool settings are saved with the definition and support undo. **Reset** restores the original incoming data: it disables every tree tool and unit conversion, restores automatic decimals, clears search, filters, sorting and row selection, and scrolls to the top. It closes dropdowns and discards pending text edits on the first click. One Undo restores the previous settings. Panel dimensions, display mode and wire connections are retained. The component menu also offers **Reset panel view** and **Reset tree tools** for narrower resets.

New panels start at **360 × 360** (1:1). Saved sizes and legacy size presets are preserved. All interaction stays on the canvas; the popup inspector has been removed.

One small chevron in the header cycles **Full → Half → Collapsed → Full**. Full restores the saved height, Half shows half that height, and Collapsed keeps a 28-unit header. The body folds toward the wire connections, keeping both connection points fixed. Width, filters, selection, and data stay intact. The display mode is saved with the definition and supports undo. Resizing in Half updates the full height proportionally.

The panel uses a compact spreadsheet layout: one search/filter row and a small footer dock with the original Grasshopper **Trim**, **Simplify**, **Flatten**, and **Reverse** icons. Hover for each tool's name and trim depth; green icons indicate active tools. Type hints cover Grasshopper geometry and primitive data: **Pt**, **Srf**, **Mesh**, **Crv**, **Brep**, **SubD**, **Vec**, **Num**, and more. The filter summarises the two most common visible types, with a remaining-type count, and its tooltip lists full names and counts. Mixed and invalid-only rows show compact type labels; the menu retains full names, and other built-in or third-party types keep their own names. At the default size, the table has 41% more area than the previous layout. The icons are original Grasshopper assets, used for this Grasshopper plug-in under [McNeel's icon usage terms](https://developer.rhino3d.com/guides/grasshopper/grasshopper-icons/).

The small **Units** dropdown at the bottom right opens upward inside the panel. Choose **mm ↔ m**, **mm² ↔ m²** (square millimeters / square meters), or **rad ↔ deg**. Conversion changes Number and Integer output values; converted integers become Numbers so fractions are preserved. Text, geometry, nulls, and empty branches pass through. Length and area conversions are separate: 1,000 mm becomes 1 m, while 1,000,000 mm² becomes 1 m². Conversion follows the tree tools, and search, sorting, and statistics use the converted values.

The same dropdown offers **Decimals: automatic** and **Decimal places…** (0–12). Decimal places format numeric rows, copied values, and statistics without rounding the values on the output wire. Conversion and decimal settings are saved with the definition and support undo. Existing definitions default to no conversion and automatic decimals. The header Reset clears these settings too; the explicit Reset panel view and Reset tree tools menu commands retain them. Dropdowns use compact widths, aligned checkmarks and arrows, a divider between conversion and formatting, and hover highlights. Paging keeps every option reachable even in a minimum-size Half panel.

The resizable canvas panel shows grouped branch headers and compact rows, with search, numeric comparisons (`> 800`, `<= 25`, `= 10`), type/invalid filters, value sorting, branch selection, exact branch jumps such as `{0;2;4}`, row jumps, a logical scrollbar, and numeric statistics. Click the search field and press Enter to apply a query. Double-click a row to copy its full value. Addresses describe the output tree after the active tools. Drag the bottom-right corner to resize; size and view settings are saved with the definition.

![Compact Panel+ canvas with native tree-tool icons and row type hints](docs/images/panel-tree-tools.png)

[See the compact units menu](docs/images/panel-units-menu.png).

Primitive values, points/vectors, and cheap geometry properties have cached summaries. Rows use analytic measurements for lines, arcs, circles, rectangles and boxes; general curve lengths and surface/Brep mass properties are calculated only on hover, once per row per solution. Surface area respects trimming. Geometry measurements use Rhino model units (including squared/cubed units), independently of the numeric Units conversion. The layout stays unchanged. Third-party data keeps a type summary; full text is formatted on demand when copied. Text search operates on cached summaries, excluding hover-only measurements. Numeric statistics include finite Number/Integer values, and exclude NaN and infinity. This MVP inspects connected data; it does not author text like an unconnected stock Panel.

### Build and package

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

The beta artifact is `artifacts/better-gh-panel-0.1.8-beta.1/better-gh-panel-0.1.8-beta.1-rh8_0-any.yak`. Packaging follows HopperWire's AECTooling metadata and `net48` / `net7.0` layout. See [installation and publishing preparation](docs/packaging.md). The packaging script builds locally; public publication uses an explicit Yak push.

The native plug-in compiles for both Rhino runtime modes; the C# query harness covers filters, stats, stable sorting, empty branches, cancellation, and large data. Mac runtime validation requires a Mac with Rhino. CI is configured to compile and stage on Windows and Mac, and does not launch Rhino.

## Archived HTML design study

Run `npm start`, then open http://localhost:5173. No dependency installation is needed. Use the **Canvas / Inspector** switch to compare the compact component and expanded explorer.

Try searching `> 800`, filtering **Nulls**, sorting numbers, or entering `{0;2;4}` in the branch search and pressing Enter. The sample size menu switches between 65,536 and 262,144 generated items. `/` focuses search, `G` focuses branch search, and Escape resets the view. Select a row to inspect and copy its value. Filters and sorting only affect the view.

The files in `prototype/` are a preserved visual reference using generated data independently of Rhino. Its query behavior is illustrative; the C# core defines native behavior. See [the current architecture](docs/current-architecture.md) and [the original proposal](docs/architecture.md).
