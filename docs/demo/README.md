# Panel+ · See what flows

A 60-second, silent, captioned explainer for the native Panel+ plugin. The graphics illustrate current behavior with sample data; they are not a recording of Rhino. The design uses the plugin's muted green palette and native tree-tool glyphs.

**Share:** [`panel-plus-demo.mp4`](panel-plus-demo.mp4). 1600 × 900, 30 fps, exactly 60 seconds, approximately 2.0 MB. H.264 video with captions burned into the image, suitable for viewing without sound. [`poster.jpg`](poster.jpg) is a still for a thumbnail or link preview.

**Japanese / 日本語版:** [`panel-plus-demo-ja.mp4`](panel-plus-demo-ja.mp4), with Japanese titles, captions, and explanatory callouts. It has the same duration, resolution, and frame rate. Native plugin controls keep their English names. Preview at <http://localhost:5174/ja.html>; Japanese copy and the complete transcript are in `demo-ja.js`, sharing the original animation and sample data. [`poster-ja.jpg`](poster-ja.jpg) is the Japanese thumbnail.

**Preview / edit:** run `npm run demo` from the repository root and open <http://localhost:5174>. There are no dependencies to install. The HTML can also be opened directly from disk. Playback supports seeking, chapter jumps, speed selection, Space to pause, and arrow keys to move five seconds. Reduced-motion preferences disable autoplay; the transcript remains available below the player.

`demo.js` owns the timeline, captions, sample data, and vector drawing. `demo.css` styles the optional preview player. All graphics are local; the animation requires no fonts, images, services, or network access.

| Time | Demonstration |
| --- | --- |
| 0:00 | Connect a typed tree; inspect branches and types |
| 0:06 | Numeric filters change typed output; sorting changes only the view within each branch |
| 0:14 | Automatic geometry metrics, invalid labels, area filtering |
| 0:22 | Highlight without camera movement; explicitly frame, multi-select, and copy geometry |
| 0:30 | Simplify → Trim → Flatten → Reverse with before/after paths |
| 0:40 | Numeric unit conversion and decimal formatting without rounding wire values |
| 0:48 | Full / Half mode with stationary wire grips |
| 0:54 | Reset data settings; find Panel+ in Params → Util |

To record a new export, open `http://localhost:5174/?film=1&paused=1` in a 1600 × 900 browser viewport. Start recording, then run `panelDemo.seek(0); panelDemo.play()` in the page. Playback stops at 60 seconds. `panelDemo.state` reports progress; `panelDemo.seek(seconds)` renders any frame deterministically. Trim the captured lead-in and encode to H.264, `yuv420p`, with `+faststart` for sharing. Retain the illustrated-data label and Windows-only geometry clipboard caption when editing.

For the Japanese export, use `http://localhost:5174/ja.html?film=1&paused=1` with the same recording settings. Japanese body copy has explicit line breaks, and titles use a smaller size to fit the original composition. Japanese fonts come from the local system; no font download is needed.

Decimal formatting can change Text/Regex filter membership because those filters match displayed values; numeric wire values retain full precision.

Feature behavior was checked against `docs/current-architecture.md`, the repository README, and `PanelTreeOperations.cs`. Mac runtime validation remains pending, as documented by the native plugin.
