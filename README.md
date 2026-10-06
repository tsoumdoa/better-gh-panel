<img src="assets/icon.svg" width="64" height="64" alt="">

# better-gh-panel

Created in [T3 Code](https://t3.codes).

**Panel+** is a Grasshopper data inspector for Rhino 8 on Windows and Mac. This repository contains the native MVP and the original interactive HTML design study.

## Native MVP

Find **Panel+** in **Params → Util** after installing the local Yak package. Connect any typed tree to **D**. With tree tools off, the output forwards that tree unchanged. Searching, filtering and sorting only affect your view.

The public Yak beta is **`better-gh-panel 0.1.2-beta.1`**. In Rhino 8, run `PackageManager`, enable prereleases, and search for `better-gh-panel`. Or close Rhino and run `yak install better-gh-panel 0.1.2-beta.1`, then restart. Windows native checks pass; Mac runtime validation is pending.

The next beta patch is **`0.1.3-beta.1`** (not yet published). It adds **Trim**, **Simplify**, **Flatten**, and **Reverse** directly to the component. Click Trim and enter a nonnegative depth (0 disables it); click the other tools to toggle them. Tools change the typed output and run in that order. Trim removes trailing path levels and merges matching branches; fully trimmed paths become `{0}`. Simplify removes shared path indices, Flatten merges into `{0}`, and Reverse reverses items within each output branch. Upstream data stays intact. Tool settings are saved with the definition and support undo. **Reset** clears only the view; **Reset tree tools** in the component menu restores the original output.

New panels start at **360 × 360** (1:1). Saved sizes and legacy size presets are preserved. All interaction stays on the canvas; the popup inspector and expand action have been removed.

The small **Units** dropdown at the bottom right opens upward inside the panel. Choose **mm ↔ m**, **mm² ↔ m²** (square millimeters / square meters), or **rad ↔ deg**. Conversion changes Number and Integer output values; converted integers become Numbers so fractions are preserved. Text, geometry, nulls, and empty branches pass through. Length and area conversions are separate: 1,000 mm becomes 1 m, while 1,000,000 mm² becomes 1 m². Conversion follows the tree tools, and search, sorting, and statistics use the converted values.

The same dropdown offers **Decimals: automatic** and **Decimal places…** (0–12). Decimal places format numeric rows, copied values, and statistics without rounding the values on the output wire. Conversion and decimal settings are saved with the definition and support undo. Existing definitions default to no conversion and automatic decimals. Reset view and Reset tree tools leave these settings intact; choose No conversion and Decimals: automatic to reset them.

The resizable canvas panel shows grouped branch headers and compact rows, with search, numeric comparisons (`> 800`, `<= 25`, `= 10`), type/invalid filters, value sorting, branch selection, exact branch jumps such as `{0;2;4}`, row jumps, a logical scrollbar, and numeric statistics. Click the search field and press Enter to apply a query. Double-click a row to copy its full value. Addresses describe the output tree after the active tools. Drag the bottom-right corner to resize; size and view settings are saved with the definition.

![Panel+ canvas tree tools, rendered by the native component with sample data](docs/images/panel-tree-tools.png)

[See the compact units menu](docs/images/panel-units-menu.png).

Primitive values and points/vectors have cached summaries. Heavy geometry and third-party data show a type summary; their full text is formatted on demand when copied. Text search operates on those cached summaries. Numeric statistics include finite Number/Integer values, and exclude NaN and infinity. This MVP inspects connected data; it does not author text like an unconnected stock Panel.

### Build and package

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

The beta artifact is `artifacts/better-gh-panel-0.1.3-beta.1/better-gh-panel-0.1.3-beta.1-rh8_0-any.yak`. Packaging follows HopperWire's AECTooling metadata and `net48` / `net7.0` layout. See [installation and publishing preparation](docs/packaging.md). The packaging script builds locally; public publication uses an explicit Yak push.

The native plug-in compiles for both Rhino runtime modes; the C# query harness covers filters, stats, stable sorting, empty branches, cancellation, and large data. Mac runtime validation requires a Mac with Rhino. CI is configured to compile and stage on Windows and Mac, and does not launch Rhino.

## Archived HTML design study

Run `npm start`, then open http://localhost:5173. No dependency installation is needed. Use the **Canvas / Inspector** switch to compare the compact component and expanded explorer.

Try searching `> 800`, filtering **Nulls**, sorting numbers, or entering `{0;2;4}` in the branch search and pressing Enter. The sample size menu switches between 65,536 and 262,144 generated items. `/` focuses search, `G` focuses branch search, and Escape resets the view. Select a row to inspect and copy its value. Filters and sorting only affect the view.

The files in `prototype/` are a preserved visual reference using generated data independently of Rhino. Its query behavior is illustrative; the C# core defines native behavior. See [the current architecture](docs/current-architecture.md) and [the original proposal](docs/architecture.md).
