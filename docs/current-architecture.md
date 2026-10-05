# Current architecture

`BetterPanelComponent` forwards Grasshopper's typed input tree unchanged and captures a presentation snapshot once per solution. `SnapshotCapture` caches primitive summaries and keeps live goo references in the component for full-value formatting on the Rhino UI thread.

`BetterGhPanel.Core` owns immutable snapshots, query options, stable source positions, filtering, cancellable sorting, and statistics. Histogram values are calculated once when requested; native views use scalar statistics. Row IDs can differ from positions in `Snapshot.Rows`; query `RowIds` contains positions, not those IDs.

One `PanelSession` per component owns the current snapshot, copied options, and the published result. Queries run against managed snapshots in the background. Cancellation and request numbers reject obsolete results before UI publication. Query revisions synchronize controls even when an equal reset must clear an unsubmitted draft. Equal options do not trigger another scan.

`BetterPanelAttributes` draws the canvas and handles row interaction, scrolling, inline editing, and resizing. `CanvasMenu` owns dropdown lifetime and paging; `CanvasPanelSize` owns dimension validation and presets. The optional Eto `PanelInspector` uses the same session and a fixed-size `VirtualRowView`. Selection and full-value lookup use source positions with snapshot ownership checks.

Width and height are canonical saved settings. Preset check marks are derived from them. Serialization retains the legacy `PanelSize` key, and reads it as a fallback when dimensions are absent. Document closure releases sessions and inspectors; a tab switch closes the inspector and editor while retaining the snapshot.

The managed test runner covers query behavior and pure canvas menu/size logic without Rhino. The separate Windows Rhino smoke harness checks the host integration, saved settings, unchanged output, and UI interactions. Its test adapter contains the reflection needed to inspect the loaded `.gha` and host-owned controls.

Packaging builds `net48` and `net7.0`, derives manifest versions from `Directory.Build.props`, and stages only the expected runtime payloads. CI runs on Windows and Mac; it does not launch Rhino. The archived browser study in `prototype/` is a visual reference with an independent sample-data engine. `docs/architecture.md` preserves the original proposal.
