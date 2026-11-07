<img src="assets/icon.svg" width="64" height="64" alt="">

# Panel+

Created in [T3 Code](https://t3.codes).

**Panel+** (`panel-plus`) is a Grasshopper data inspector for Rhino 8 on Windows and Mac. Connect any data tree and see what flows: browse branches, measure geometry, search and filter, select rows to preview them in Rhino, and send exactly what you picked downstream.

[![Panel+ in 20 seconds: inspect curve lengths, filter them, preview the matches and see their names side by side](docs/demo/panel-plus-short.gif)](docs/demo/panel-plus-short.mp4)

<sub>Illustrated sample data. [MP4 version](docs/demo/panel-plus-short.mp4).</sub>

## Install

Panel+ is published on Yak as **`panel-plus`** (beta). In Rhino 8, run `PackageManager`, tick **Include pre-releases**, search for `panel-plus` and install. Or close Rhino and run:

```text
yak install panel-plus 0.2.7-beta
```

If the earlier `better-gh-panel` package is installed, remove it first so the two copies don't both load. Find **Panel+** in **Params → Util** and connect a tree to **D**. Release notes for every version are on [GitHub Releases](https://github.com/tsoumdoa/better-gh-panel/releases).

## One-minute tour

[![Panel+ one-minute tour](docs/demo/panel-plus-demo.gif)](docs/demo/panel-plus-demo.mp4)

<sub>Captioned, silent, illustrated sample data. [MP4](docs/demo/panel-plus-demo.mp4) · [日本語版 / Japanese](docs/demo/panel-plus-demo-ja.mp4) · run `npm run demo` for the editable animation ([source and recording notes](docs/demo/README.md)).</sub>

## What's new

**[`0.2.7-beta`](https://github.com/tsoumdoa/better-gh-panel/releases/tag/v0.2.7-beta)**

- **Maintenance release.** Shared model helpers, component responsibilities and canvas controls are organized for easier maintenance, with no intended change to controls, saved settings or data outputs.
- **Clearer regression coverage.** Core checks run as individual xUnit tests, and native preview checks are grouped by fixture with shared reflection helpers.
- **Easier contributor navigation.** Subsystem architecture, build instructions, release history and validation records now have dedicated documents.

See [CHANGELOG](CHANGELOG.md) for earlier releases.

## Features

![Panel+ with branch headings, typed rows, tree tools and the footer dock](docs/images/panel-tree-tools.png)

### Inspect

- **Typed rows grouped by branch.** Branch headings show their path and item count; rows show compact type labels (**Pt**, **Crv**, **Srf**, **Brep**, **Mesh**, **Num**, …), empty branches, `<empty>` text and geometry-group names with their object counts.
- **Geometry metrics, automatically.** Visible curves, surfaces, Breps and meshes are measured in the background — length, area, volume and shape hints such as **Srf · untrimmed · planar · A 100 mm²**. Rows show **measuring…** until ready, and Panel+ never moves the camera.
- **Invalid and unverified values are marked** with a label, a **!** or **?** badge and the reason in the tooltip.
- **Data checks.** A quiet, clickable **Check:** line flags null, invalid and unverified items and strong branch-length outliers before search filters; every warning segment is amber. Healthy trees use no extra space.
- **Counts and Stats.** The footer shows matched/total items and branches. **Stats** lists min/max/average plus every unique value with its frequency, using the presented numbers.
- **Long values** open in a read-only, scrollable **View full value** window when you double-click a row, or from its right-click menu.

### Search, filter and sort

Type in the search field: rows update as you type, **Enter** or clicking away applies the query and **Esc** restores the previous one. The mode chip at the start of the field (**Text ▾**, **Length ▾**, **List ▾**, …) picks what to match:

| Mode | Example |
| --- | --- |
| Text (values, paths, types) / regular expression | `facade`, `^A-0[1-4]` |
| Numeric value, Length, Area, Volume, X/Y/Z | `> 4000`, `<= 25`, `!= 0` |
| List length · per branch | `>= 3`, `= 0` |
| Input-specific properties (below) | `>= 2`, `>= 2026-01-01` |

Comparisons test the numbers shown, after unit conversion, factor rounding and any explicit decimal places. Automatic keeps full precision. Picking a comparison mode (numbers, dates, text properties) shows its values and opens the editor. Validity offers Valid/Invalid/Unverified/Null; Boolean properties offer True/False. **×** clears the expression and returns to Text mode; Undo restores both. Combine searches with the **Types** filter (including *invalid only*) and **Paths** (`0;2;4` or `{0;2;4}`, including descendants). **Asc/Desc** sorts within each branch and adds Excel-style row numbers; wire outputs always keep tree order.

**Behaviour changes:** Numbers now always use "." as the decimal separator, so saved text/regex filters written with "," need updating. Decimal places now also rounds numbers sent on D and S.

<details>
<summary>Selectable properties by input type</summary>

| Input | Properties |
| --- | --- |
| Curves, lines, arcs, circles | Degree, closed, shape and planar; polyline segment count; radius and arc angle |
| Breps / meshes | Face and vertex counts, closed; Brep edge count and solid; planarity for single-face Breps |
| Surfaces | Face count; trimmed, U/V degree and planar for single-face surfaces |
| Rectangles / boxes | Width, height; rectangle perimeter; box depth and surface area |
| Vectors / planes | Vector magnitude; plane origin and axis components |
| Groups | Name and direct object count |
| Colours / domains | R/G/B/alpha and named colour; domain endpoints |
| Text / Boolean / GUID / time | Text value and length; Boolean/GUID value; date-time and its parts |
| Complex numbers / transforms | Real, imaginary, magnitude, phase; determinant and matrix entries |
| SubD / extrusion / point cloud | Vertex/edge/face counts, solid/closed; extrusion height and profiles; point count |
| Text Dots | Text, character count, X/Y/Z, font size (screen pixels) and font face |
| Text entities / leaders / dimensions / centermarks | Text, character count, origin X/Y/Z; dimension length and angle |
| Hatches | Pattern index, scale, rotation and origin X/Y/Z |
| Culture / location / path / matrix | Culture name and English name; latitude/longitude (degrees); path depth; matrix row/column counts |
| Block instances / detail views | Block definition ID; detail projection, projection lock and page-to-model ratio |
| Every input | Data type, validity, branch path and zero-based item index |

Text Dots display their text and support coordinate filters, viewport preview and geometry copy.

Unsupported properties show `n/a`. Text comparisons are exact; quote text to keep surrounding spaces (`= " 001 "`).

</details>

### Reshape the tree

The footer's tree tools run in a fixed order — **Simplify → Trim → Flatten → Reverse** — then unit conversion, filters and **Create set**. An active Trim shows its depth as a small badge on its icon.

- **Create set** groups equal displayed values per branch (or across the tree with Flatten). Rows use set index **k**. **Out Items** sends all members in **{path;k}** branches (**{k}** when flattened); **Out Values** sends each unique value once at displayed precision. List-length sets group original branches as **{k;path}**. An occurrence badge such as **9×** lists every original path and index.
- **Units** converts numbers and geometry metrics (mm ↔ m, mm² ↔ m², mm³ ↔ m³, rad ↔ deg), sets **Decimal places**, and can **Round up by factor**. Unit conversion, Round up by factor, then explicit Decimal places define one presented number shared by the rows, Values output, clipboard, comparisons and Stats. Numeric Items also leave at this precision; geometry is never modified. Factors apply to plain numbers, geometry metrics, X/Y/Z and dimensional properties, including angles. Counts, indices, dates, colour channels, axis components and matrix entries ignore the factor; integer properties stay integers. With Round up by factor, Panel+ rounds each number's full-precision decimal form up to the factor, then Decimal places also rounds up. Inputs whose full-precision digits sit above a step (e.g. 0.1+0.2 = 0.30000000000000004) round up to the next step. Without a factor, decimal rounding remains half away from zero. Rounding uses the faithful 15-digit decimal form when possible, so 2.675 at 2 decimal places becomes 2.68, as in Excel. Hover a rounded row to see its raw value and rounding. When active, the Units chip reads compactly, for example **→m² ↑0.25 3dp** (target unit, factor, decimals), and widens to fit; its tooltip spells out the full setting.
- **Clear filters** clears search, mode, type, path, invalid-only, sorting, selection and scroll. Use the **×** on the footer's **D: 1 filter** chip or the component's right-click menu. When only sorting or a search mode is set, the chip names it instead (**Sorted ×**, **Length ×**, **Length · sorted ×**). One Undo restores the filters.
- **Reset all** (footer ↺, beside Units and set apart from the tree tools) also resets tree tools, units, rounding, decimals, Out Items | Values and B + F follow. It keeps Preview mode, item order, D|F split and size. One Undo restores the settings.

### Select, preview and copy

- **Click** a row to select it, **Ctrl/Cmd-click** to toggle, **Shift-click** for a range, a **branch heading** for the whole branch, and **Alt/Option-click** to pick the same item index in every branch. **Select all** takes every current match.
- **Preview** draws **Selection** (default), **Results**, or **Off**, in Grasshopper's selection colour. The header reads **Preview: Selection**, **Preview: Results** or **Preview: Off**. **Show item order** adds numbered dots in Rhino.
- **Branch addresses.** Hover a branch heading to copy its path with the small copy glyph. Right-click for path formats, selected paths or **Show only this branch**. Row menus offer **Copy address(es)** (`{path}(index)`; Create-set Items copy the D branch `{path;k}`, or `{k}` with Flatten) and **Copy as table** (Path, Index, shown Value as TSV).
- **Right-click** a row to **Frame selection** in Rhino, **Copy value(s)**, **View full value**, or **Copy geometry for Rhino** (Windows) to paste with Rhino's normal Paste.

### D | F split view

Connect matching data to the optional **F** input and click **D|F** in the header. The panel doubles in width and shows F's values beside D, row for row, through the same tree tools, filters, sorting and selection — for example, which named facade panels passed a length filter.

![D | F split view with two rows selected in both panes](docs/images/panel-split-view.png)

- **Linked scrolling** (the chain link between the pane captions) moves both panes with one scrollbar. Unlink it to scroll each pane on its own; selecting a row brings its partner into view, and relinking snaps F back to D.
- Copying and **View full value** from the F pane use F's raw values, unconverted. F row addresses and tables use F's own original path and index. Heading **Copy path** copies the clicked pane's path, including F's own path for order-paired branches. Hover copy glyphs follow the complete drawn label and disappear when it is clipped. Data checks mark D headings; F keeps its pairing badge and counts.
- F must have **the same number of branches as D**, before tree tools. All identical paths pair first; remaining branches pair in tree order. The F heading shows its own path when paired by order.
- Items pair by index within each original branch pair. Missing F cells stay empty, with a label on the first row of each run; additional F items appear as muted rows beside blank D spacers. Only indices beyond the original D branch length are labelled **only in F**; retained values whose D rows are hidden carry **paired with hidden D rows**. These muted rows are not selectable. Every F item keeps its original F index, including after Reverse, filtering and sorting. An amber **D 12** badge beside F's count marks unequal lengths; F headings show the matching count, such as **3 of 12 items**.
- With **Create set**, each item or list-length set has one F cell: a bounded, single-line summary of exactly the values F emits for that group, in output order (for example, `f0 · f1 · f2 · f3 … +7 in this set`). Sets have no hidden-partner or only-in-F notes. Selecting an F set cell scopes its complete group; copying it includes every emitted F value. Use **Ctrl+C** on a selected F list-length set to copy the group.
- When branch counts differ, the F pane explains the mismatch:

![F pane explaining the first mismatch between D and F](docs/images/panel-split-mismatch.png)

The **D|F** chip is drawn greyed whenever F is wired but has no usable data. Split view saves with the definition and supports Undo.

<details>
<summary>F output pairing details</summary>

F sends the F items paired with the D items that B + F follow, at D's transformed paths. Values and types are unchanged.

- Equal branches follow item membership; scoped unequal branches emit their whole F branch once per group.
- Empty D partners are included when D retains the branch, without item filters or Create set; path and list-length filters preserve them.
- Sets use {path;k} ({k} when flattened; {k;path} for list-length sets), in both output modes.
- F must have the same branch count as D; exact paths pair first, then remaining branches in tree order.

</details>

### Outputs

| Port | Output |
| --- | --- |
| **D** | Matching **Items** (default) or displayed **Values**, with explicit rounding as shown, in tree order. Automatic sends full precision; Create set Values sends compact displayed set values. Set Items: all members in one branch per value; set Values: one list of unique values per source branch. |
| **S** | Selected Items or Values. Selected sets include all members and retain D's group paths. Empty when nothing is selected. |
| **B** | A True/False mask in the original D tree shape, including every member of kept or selected sets. |
| **F** | F sends the F items paired with the D items that B + F follow, at D's transformed paths. Values and types are unchanged. |

**Out Items | Values** appears in the footer when showing values or creating a set; it is also in the component's right-click menu. With explicit decimals, the Values tooltip says "Sends the values exactly as shown." In Automatic with a factor it says "Sends the rounded values at full precision; rows may be abbreviated." Without a factor it says "Sends full-precision values; rows may be abbreviated." Sets say "Sends the displayed set values." The ports always read **D** and **S**. Numbers use the shown units and explicit rounding; Automatic keeps full wire precision except for compact displayed Create set Values; unavailable values output null to preserve row alignment. Older saved sets retain Values output.

The Out Items | Values switch resets to Items whenever hidden (for example, returning to Text with Create set off). Undo restores both settings.

Right-click the component → **B + F follow** to make B and F follow the **Results** (default) or only the **Selection**. Ports always read **D**, **S**, **B**, **F**; B/F tooltips state the current scope. With Items output and nothing filtered or transformed, D forwards its input unchanged.

### Layout

- Drag the dotted header grip to move the panel and the bottom-right corner to resize it. The header holds **Preview** (and **D|F** when F is wired or the split is on), the chevron that toggles **Full ↔ Half** height, and **–**, which minimizes the panel to a capsule that still shows counts. Wires stay connected throughout.
- The footer shows the visible range (**1–15/40**; on wide panels, the item and branch summary) and what D sends: **D = input**, **D: 1 filter**, **D: filtered**, **D: tools** or **D: values**. Click the range, or press **Ctrl/Cmd + G**, to jump to a row. With filters to clear, the D part is a chip whose **×** clears them; on narrow panels the chip takes priority over the range, and it hides only when a notice (such as **N selected** or **Recomputing…**) replaces the footer status. Selected rows carry a green bar at their left edge.
- On a dark Grasshopper canvas Panel+ uses a matching dark palette. Below 50% zoom it shows a large item/branch summary.

### Keyboard and mouse

Clicking inside the panel focuses it and keeps your selection; the first Esc clears the selection and the next releases focus. A green ring shows focus; without focus, keys and wheel go to Grasshopper as usual. Double-clicking a row opens its full value in one reusable window; double-clicking empty list space, Clear filters, Reset all and changes to the input's structure clear the selection. Menus may extend past the panel and draw above other components; near the edge of the view they open on the other side of their button. Clicking elsewhere on the canvas closes a menu and the click still reaches what you clicked.

| Key | Action |
| --- | --- |
| **↑ / ↓** (+ **Shift**) | Move (or extend) the selection |
| **PgUp / PgDn / Home / End** | Scroll |
| **Ctrl/Cmd + C** | Copy the selected values (Grasshopper objects when no rows are selected) |
| **Ctrl/Cmd + Shift + C** | Copy unique paths of selected rows, in tree order |
| **Ctrl/Cmd + A** | Select all rows |
| **Ctrl/Cmd + F** or **/** | Search |
| **Ctrl/Cmd + G** | Jump to a row |
| **Esc** | Clear the selection, then release focus |
| Mouse wheel | Scroll the rows or menu under the pointer; hold any modifier to zoom the canvas instead |

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation.

## License

[MIT](LICENSE). The license is also included in locally generated Yak packages.
