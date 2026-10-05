<img src="assets/icon.svg" width="64" height="64" alt="">

# better-gh-panel

Created in [T3 Code](https://t3.codes).

**Panel+** is a Grasshopper data inspector for Rhino 8 on Windows and Mac. This repository contains the native MVP and the original interactive HTML design study.

## Native MVP

Find **Panel+** in **Params → Util** after installing the local Yak package. Connect any typed tree to **D**. The output forwards that tree unchanged: searching, filtering and sorting only affect your view.

The public Yak beta is **`better-gh-panel 0.1.2-beta.1`**. In Rhino 8, run `PackageManager`, enable prereleases, and search for `better-gh-panel`. Or close Rhino and run `yak install better-gh-panel 0.1.2-beta.1`, then restart. Windows native checks pass; Mac runtime validation is pending.

The resizable canvas panel is the primary workspace. It shows grouped branch headers and compact rows, with search, type filtering, sorting, branch selection, row jumps, a logical scrollbar, and numeric statistics. Click the search field and press Enter to apply a text or numeric query. Drag the bottom-right corner to resize; the size and query are saved with the definition.

The small expand action and component menu open the optional modeless inspector for deeper inspection and full values.

The inspector supports debounced text search, numeric comparisons (`> 800`, `<= 25`, `= 10`), type/invalid filters, value sorting, exact branch jumps such as `{0;2;4}`, direct row jumps, virtual scrolling, original item addresses, copying full values, and scoped numeric/type statistics. Use Home/End and Page Up/Down in the row view. View settings are saved with the Grasshopper definition.

Primitive values and points/vectors have cached summaries. Heavy geometry and third-party data show a type summary; their full text is formatted on demand when selected. Text search operates on those cached summaries. Numeric statistics include finite Number/Integer values, and exclude NaN and infinity. This MVP inspects connected data; it does not author text like an unconnected stock Panel.

### Build and package

```text
dotnet build BetterGhPanel.sln -c Release
dotnet run --project tests/BetterGhPanel.Core.Tests -c Release
node scripts/package.mjs
```

The beta artifact is `artifacts/better-gh-panel-0.1.2-beta.1/better-gh-panel-0.1.2-beta.1-rh8_0-any.yak`. Packaging follows HopperWire's AECTooling metadata and `net48` / `net7.0` layout. See [installation and publishing preparation](docs/packaging.md). The packaging script builds locally; public publication uses an explicit Yak push.

The native plug-in compiles for both Rhino runtime modes; the C# query harness covers filters, stats, stable sorting, empty branches, cancellation, and large data. Mac runtime validation requires a Mac with Rhino. CI is configured to compile and stage on Windows and Mac, and does not launch Rhino.

## Archived HTML design study

Run `npm start`, then open http://localhost:5173. No dependency installation is needed. Use the **Canvas / Inspector** switch to compare the compact component and expanded explorer.

Try searching `> 800`, filtering **Nulls**, sorting numbers, or entering `{0;2;4}` in the branch search and pressing Enter. The sample size menu switches between 65,536 and 262,144 generated items. `/` focuses search, `G` focuses branch search, and Escape resets the view. Select a row to inspect and copy its value. Filters and sorting only affect the view.

The files in `prototype/` are a preserved visual reference using generated data independently of Rhino. Its query behavior is illustrative; the C# core defines native behavior. See [the current architecture](docs/current-architecture.md) and [the original proposal](docs/architecture.md).
