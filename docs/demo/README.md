# Panel+ · See what flows

The [19-second short demo](panel-plus-short.mp4) follows a single example: inspect four curve lengths, filter for lengths greater than 4,000 mm, select and preview the two matches, then connect curve names to F and click D|F to identify them. Both panes highlight the same two selected rows. It is silent and uses illustrated sample data. Its clickable thumbnail is [short-poster.jpg](short-poster.jpg), showing the final paired result.

To regenerate it on Windows, install Pillow and ffmpeg, then run `python scripts/render-short-demo.py` from the repository root. The renderer uses Windows Segoe UI and Consolas fonts and produces a 1280 × 720 H.264 video at 24 fps, plus the poster.

A 60-second, silent, captioned explainer for the native Panel+ plugin. The graphics illustrate current behavior with sample data; they are not a recording of Rhino. The design uses the plugin's muted green palette and native tree-tool glyphs.

**Share:** [`panel-plus-demo.mp4`](panel-plus-demo.mp4). 1600 × 900, 30 fps, exactly 60 seconds, approximately 2.31 MB. H.264 video with captions burned into the image, suitable for viewing without sound. [`poster.jpg`](poster.jpg) shows the D | F chapter for a thumbnail or link preview.

**Japanese / 日本語版:** [`panel-plus-demo-ja.mp4`](panel-plus-demo-ja.mp4), with Japanese titles, captions, and explanatory callouts. It has the same duration, resolution, and frame rate. Native plugin controls keep their English names. Preview at <http://localhost:5174/ja.html>; Japanese copy and the complete transcript are in `demo-ja.js`, sharing the original animation and sample data. [`poster-ja.jpg`](poster-ja.jpg) is the Japanese thumbnail.

**GitHub inline playback:** use [`panel-plus-short.gif`](panel-plus-short.gif) (960 × 540, 19 seconds, 0.74 MB) or [`panel-plus-demo.gif`](panel-plus-demo.gif) (800 × 450, 60 seconds, 4.95 MB). Both loop at 12 fps; GitHub animates these repository GIFs inline, while the MP4 links open the videos. The MP4s retain larger text and smoother motion for full-size viewing.

**Preview / edit:** run `npm run demo` from the repository root and open <http://localhost:5174>. There are no dependencies to install. The HTML can also be opened directly from disk. Playback supports seeking, chapter jumps, speed selection, Space to pause, and arrow keys to move five seconds. Reduced-motion preferences disable autoplay; the transcript remains available below the player.

`demo.js` owns the timeline, captions, sample data, and vector drawing. `demo.css` styles the optional preview player. All graphics are local; the animation requires no fonts, images, services, or network access.

| Time | Demonstration |
| --- | --- |
| 0:00 | Connect a typed tree; inspect branches and types |
| 0:05 | Numeric filters change typed output; sorting changes only the view within each branch |
| 0:12 | Automatic geometry metrics, invalid labels, area filtering |
| 0:19 | Highlight without camera movement; explicitly frame, multi-select, and copy geometry |
| 0:26 | Simplify → Trim → Flatten → Reverse with before/after paths |
| 0:34 | Numeric unit conversion and decimal formatting without rounding wire values |
| 0:41 | D \| F split view: aligned raw values, shared selection, filtered/sorted rows, linked/independent scrolling, reveal partner, mismatch diagnostic |
| 0:51 | Full / Half mode with stationary wire grips (3 seconds) |
| 0:54 | Reset data settings; find Panel+ in Params → Util |

To record a new export, open `http://localhost:5174/?film=1&paused=1` in a 1600 × 900 browser viewport. Start recording, then run `panelDemo.seek(0); panelDemo.play()` in the page. Playback stops at 60 seconds. `panelDemo.state` reports progress; `panelDemo.seek(seconds)` renders any frame deterministically. Trim the captured lead-in and encode to H.264, `yuv420p`, with `+faststart` for sharing. Retain the illustrated-data label and Windows-only geometry clipboard caption when editing.

For the Japanese export, use `http://localhost:5174/ja.html?film=1&paused=1` with the same recording settings. Japanese body copy has explicit line breaks, and titles use a smaller size to fit the original composition. Japanese fonts come from the local system; no font download is needed.

For deterministic export, a temporary `puppeteer-core` install can launch local Chrome headless at 1600 × 900 (device scale factor 1), open either HTML file directly with `?film=1&paused=1`, and await `document.fonts.ready`. For frames 0–1799, call `panelDemo.seek(frame / 30)` and pipe the canvas PNGs to `ffmpeg -f image2pipe -framerate 30 -vcodec png -i - -an -c:v libx264 -crf 19 -pix_fmt yuv420p -movflags +faststart OUTPUT.mp4`. Export both posters from the canvas at 43.3 seconds. Keep browser tooling and dependencies in ignored `artifacts/`, outside the committed files.

Generate each GIF in two ffmpeg passes: `-vf "fps=12,scale=WIDTH:-1:flags=lanczos,palettegen=stats_mode=diff" -frames:v 1 -update 1 PALETTE.png`, then supply the MP4 and palette as inputs with `-filter_complex "[0:v]fps=12,scale=WIDTH:-1:flags=lanczos[v];[v][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -loop 0 OUTPUT.gif`. Use WIDTH 960 for the short and 800 for the EN tour; inspect extracted frames at their delivered size to check caption legibility.

The native README images are [`panel-split-view.png`](../images/panel-split-view.png) and [`panel-split-mismatch.png`](../images/panel-split-mismatch.png). They were freshly exported from the current Rhino 8 build with the temporary split-review capture probe and `tests/BetterGhPanel.RhinoSmoke/run.ps1 -Fixture ui-ux` (20 assertions passed), then cropped to the panel and wire grips with a white margin. The probe was restored byte-for-byte and the smoke host rebuilt afterward; no probe or browser tooling is committed.

Decimal formatting can change Text/Regex filter membership because those filters match displayed values; numeric wire values retain full precision.

Feature behavior was checked against `docs/current-architecture.md`, the repository README, and `PanelTreeOperations.cs`. Mac runtime validation remains pending, as documented by the native plugin.
