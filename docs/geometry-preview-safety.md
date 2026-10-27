# Geometry preview and row actions

This implementation is not release-ready: the current native fixture results
include failures, and the lifetime of submitted display resources through a
native pipeline flush is not established. The evidence and the exact builds
used are listed below. The parent owns final-package validation and review.

Panel+ uses result-local owned preview generations for bounds, labels and
geometry drawing. Exact wrappers and groups are flattened under per-root and
per-build budgets and are never retained as recursive preview targets. New
solutions and source expiration discard the generation; before expiration a
snapshot remains internally consistent when a source holder or group changes.
Independent standalone third-party preview and standalone point subclasses keep
their existing best-effort behavior. Arbitrary native subclasses and concurrent
external native mutation or disposal remain host limitations, not a sandbox.
Loaded referenced geometry uses the same terminal-leaf path, including when it
is reached through a group or wrapper; an unloaded reference is not loaded by
the preview or an action. The native fixtures exercise standalone, grouped and
wrapped loaded references and cancellation on reference retargeting.

Points, lines, arcs, circles, rectangles, boxes, bounded polylines and meshes
are reconstructed from value fields. Bounded NURBS curves use a value-only
reconstruction adapted from the 67-assertion Rhino 8.36 candidate at `7a2d0e8`:
all knots, control points, weights, order, dimension and domain are compared to
the owned curve before use. This narrow reconstruction does not duplicate or
remove live source UserData. When the narrow provider cannot admit a NURBS or
polyline, the compatibility path may instead use Rhino's owned native duplicate
after structural and metadata inspection. Its curve scan permits up to 32,768
points, subject to its other limits and estimates; this is not a promise that
every curve at that size is admitted. The 1,024-control-point NURBS and
2,048-point polyline limits apply to the narrow reconstruction paths, not to
all production preview and copy paths.

For polycurves, Breps, surfaces and SubD, a structural inspection with node,
collection, control-net and estimated-byte limits checks the exact managed SDK
geometry and its exposed nested components, including
Brep curves, surfaces, faces, loops, trims, edges, vertices and attached face
meshes, plus polycurve segments and revolution profiles. A native copy is attempted
only after exposed metadata is classified and a native-size estimate is reserved.
Unknown nested geometry and detectably unsupported metadata are visibly
unavailable. Serializable managed custom UserData and opaque native UserData
are attempted through Rhino's normal best-effort duplication path for automatic
preview as well as explicit copy. Inspection can invoke managed `ShouldWrite`,
and duplication or serialization can invoke further callbacks. A callback that
throws or reports non-serializable data is rejected when detected. Unknown
native or nested metadata and arbitrary callback behavior cannot be completely
classified or bounded by this inspection.
Borrowed NURBS surface preflight reads control counts, knot counts and orders;
it never calls `PointSize`, which can duplicate a borrowed surface on Rhino 8.36.
The preflight does not call native Brep `IsValid`: the public API does not
provide a proven, allocation-free count of hidden vertex adjacency, and the
[openNURBS validity implementation](https://github.com/mcneel/opennurbs/blob/8.x/opennurbs_brep_isvalid.cpp)
can perform nested scans of that adjacency. A vertex `MemoryEstimate` cap is
only a heuristic, because it is not a documented adjacency-count contract and
native size arithmetic or implementation may change. In particular, these
limits do **not** prove that a malformed Brep's duplication, bounds, meshing,
or clipboard serialization will stay within a fixed time or memory bound.
The snapshot classifier in the base tree separately calls `Brep.IsValid` during
capture, before this preview/copy preflight; the parent must resolve that path
as well. The unpublished mangled C++ adjacency export is not used.

Owned Brep and SubD preview meshes are generated from the current meshing
parameters and kept inside the reserved generated-mesh limit. They are reused
while the effective parameters match. A parameter change retires the previous
generated meshes and face-index arrays before building the replacement; eviction
and clear retire the owning preview entries. Generated meshes use the face-index
drawing overload, and SubD evaluation caches are cleared. Retired resources that
may have been drawn are queued for a later `RhinoApp.Idle` callback rather than
disposed in the draw callback. Undrawn retired entries can be disposed directly.
Active callbacks suppress nested draws and defer disposal. McNeel documents
[`RhinoApp.Idle`](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinoapp/idle)
as occurring when the application finishes processing and is about to enter
idle. The [display-pipeline reference](https://developer.rhino3d.com/api/RhinoCommon/html/Methods_T_Rhino_Display_DisplayPipeline.htm)
describes drawing stages and says `Flush` forces cached geometry to the display,
but neither documents a mesh-lifetime boundary for nested frames. A nested message pump could
potentially deliver idle after the managed callback returns and before the
native pipeline has consumed its submitted pointers. The current implementation
therefore does **not** prove safety through a native display flush, including
reentrant redraw, mesh reset or component disposal. The latest isolated-host
fixtures also timed out waiting for idle retirement; eventual resource release
and idle-event unsubscription at shutdown remain unverified. The embedded fixture
uses a WinForms message loop rather than Rhino's ordinary interactive main loop;
that host integration may explain the missing idle event, but this is a
hypothesis, not evidence of either interactive-host behavior or safe flush timing.
Oversized detail is reported as partial. One native duplication or meshing
call can still have an implementation-dependent allocation peak; the estimates
are not a hard process-memory guarantee.

Each root has a 4,096-visit, 1,024-leaf and depth-64 limit. Builds and actions
share a 20,000-visit budget. At most 1,024 root preview entries and an estimated
16 MiB are admitted. The accounting includes admitted entries, estimates reserved
for leaves being staged, and retired entries and generated meshes awaiting
release. The entire staged group's estimate is reserved before leaf creation,
including across reentrant builds; a rejected stage releases its reservation
after its owned leaves are disposed. Retired resources remain charged until
release. These are estimates rather than a cap on all native or callback
allocations. A later selected row may evict an unselected preview;
sparse selection classification examines selected membership, not all results.
The component's `ExpirePreview(false)` event invalidates its owning document's
draw list and clipping cache without marking selection as a document edit.

Frame and explicit copy re-inspect the current graph and report skipped or
exhausted work even on partial success. Frame checks exposed shapes before
reading current bounds without duplicating UserData. Copy attempts ordinary
NURBS, polycurves, surfaces, Breps and SubD subject to the preflight limits;
its native operation is a candidate, not a proof of boundedness or of complete
metadata preservation for every SDK object. For exposed SDK
`SharedUserDictionary` entries and ordinary user strings, a scalar value witness
is compared after native duplication and after a 3DM clipboard archive round trip.
Explicit copy attempts managed serializable and opaque native UserData using
Rhino's normal duplication and archive callbacks. A non-serializable or throwing
managed callback is reported and skipped; a detected metadata count or type
mismatch aborts publication. Successful custom-data copy visibly warns that its
complete content was not verified: type, count and transform comparisons do not
prove preservation of arbitrary payloads. Common integer, double and string
dictionary arrays are witnessed by value; other dictionary value types are
attempted for explicit copy with the same incomplete-content warning. Unknown
nested geometry and oversized data are visibly skipped. Rhino's string
and dictionary APIs materialize names and keys before length checks, so a single
call can exceed the estimated transient bound. Arbitrary native callbacks may
also run for unbounded time or allocate unbounded memory. The user reports that
three plan reviewers agreed to this revised best-effort policy; the implementation
still requires exact-head code review and host validation.
Automatic preview can reconstruct simple value geometry and narrow NURBS without
duplicating attached custom UserData. The compatibility native preview path can
attempt custom data as described above; its successful scan does not prove that
all nested native metadata or arbitrary payloads were preserved.
The published [openNURBS user-string size implementation](https://github.com/mcneel/opennurbs/blob/8.x/opennurbs_userdata.cpp)
accounts for value lengths but does not establish a bound for key lengths, so
`MemoryEstimate` alone is insufficient to preflight a full user-string copy.

Preview admission, frame and copy cancel stale work when a callback changes the
captured result, source array, or selection generation. They re-inspect the
bounded ordered wrapper and group graph, terminal holder values, reference IDs,
component indexes, load state and inner geometry
after preparation; frame/copy do so again immediately before publication.
Frame also rechecks its Rhino document, view, viewport, active layout detail and
projection locks immediately before moving the camera. A retargeted wrapper or
group cancels the action even if the replacement has the same displayed value.
Copy binds to the active Rhino document identity, model units and absolute
tolerance, and aborts if any changes during preparation. Shared clipboard and
viewport action generations prevent an older component's reentrant action from
overwriting a newer action's clipboard, camera or action status; value copies
also participate in the clipboard generation. Clipboard commits and commits for
each viewport are serialized on the UI thread. A nested request is queued without
blocking the callback, then rechecks its captured source graph, selection,
document settings, viewport and action generation before it publishes. A failure
before publication leaves the clipboard unchanged. If the native publication
itself fails, its result is reported as uncertain; no blind restoration can
overwrite a newer owner.
Explicit copy rechecks the current row's captured graph after native duplication
and after `File3dmObjectTable.Add`, which may duplicate metadata again. These
rechecks consume the same action-wide inspection budget; exhaustion aborts the
publication rather than publishing an unchecked group. Third-party callbacks can
independently change the system clipboard, which Panel+ cannot roll back safely.
During wire and shaded drawing, retired generations stay alive until the active
callback exits; nested draws are suppressed, retired bytes count against the
same live-memory cap, and stale subsequent draws and labels stop. The additional
native-flush lifetime caveat above still applies after the callback exits. If deferred
retirement occupies the cap, a replacement can remain visibly partial until a
later selection or source refresh. Reinspection can
compare native object identity but has no complete bounded value witness for an
in-place mutation of the same Brep, surface, polycurve, SubD, point cloud or
extrusion. Cloud count and hidden-flag-array changes are rechecked, but a change
to point coordinates, colors, normals or values without changing those fields
can still escape an action freshness check. Native calls and
unknown callbacks have no hard execution-time bound, and a reentrant mutation of
the same native object remains an unproven case. No such case is treated as
evidence that the operation is safe or release-ready.

Each preview row is prepared from the terminal leaves in one bounded graph
capture. Fixtures inject replacement and restoration of a later group member
and wrapper while an earlier leaf is being prepared, check the retained value
against its bounds, and check host cache refresh and disposal.

The existing `row-actions` fixture and its mixed-geometry clipboard assertions
remain intact. Value copy currently revalidates the captured snapshot before
publication; this can scan all captured rows even for a sparse selection. The
parent's capture work provides the full `CapturedSnapshotCurrent` fence; it must
remain in the value-copy predicate after callback-bearing work, including for
deferred commits. A selected-row-only witness cannot prove that filtering or
set membership remains unchanged. The selection and clipboard action generation
are checked around that fence as well.

The result-local compressed selection index preserves immutable ordered lists
and updates a single toggle in logarithmic index work. Preview membership uses
the matching selection revision to update only that position, including after a
dense or select-all gesture. Bulk changes, arbitrary external position lists,
output reconstruction and viewport drawing may still perform proportional work.
Managed visit-count tests and host fixtures cover the single-row gestures in
both preview modes with labels on and off.

Exact `GH_PointCloud` and `GH_Extrusion` terminals, including those reached
through bounded group/wrapper traversal, use owned native duplicates after
inspection. Point-cloud preflight admits at most 32,768 points, subject also
to the native-size and live-memory limits. Drawing uses bounded point-value
batches from the owned duplicate, grouped into consecutive source-ARGB runs
to preserve point order when the cloud has colors. Uncolored clouds use the selection or filtered color, and hidden points
are not drawn. This avoids submitting a native point-cloud pointer through the
`DrawPointCloud` call that crashed in an isolated capture. It uses Rhino's
public point-cloud display style and radius when those host properties are
available, with GH point settings as the older-host fallback. Batches and their
construction are reserved in the preview budget. A cloud with many alternating
colors can require one draw call per point, so this path can be slower than
Rhino's native cloud renderer.
Rhino 8.36's 3DM roundtrip dropped a hidden-point flag in the fixture. Explicit
copy therefore visibly skips a cloud with a hidden-flag array, even if the
selection also contains supported geometry; the source remains untouched.
Extrusion
preflight bounds the profile count and checks exposed cached meshes. Rhino's
public profile API does not provide a borrowed, callback-free view of every
internal extrusion profile or its metadata, so native duplicate and archive
work remains best effort with the same callback and single-call peak limits as
other native geometry. Exact wrappers around `BoundingBox` and `Sphere` use
value copies and generate owned Breps for geometry copy; a wrapped `Polyline`
uses a bounded managed point snapshot and an owned curve. Per-operation work
and the preview staged/admitted/retired cap include those snapshots. Unsupported,
invalid or over-budget entries remain visibly partial or skipped.

The capture classifier recognizes exact `GH_PointCloud` and `GH_Extrusion`
holders, their supported inner objects and their reference context. Valid values
and groups containing only valid trusted leaves are verified valid; unsupported
subclasses and unresolved references remain unverified. The standalone displays
remain `<Point Cloud>` and `<Extrusion>`, their known type identities remain
available to type filters, and text/regex search uses the captured display or
group summary. These types do not add metric search modes; text, regex and
list-length searches remain available, and other rows can add metric modes to a
mixed input. Typed outputs remain intact. In the persistent-source fixture Grasshopper
delivered the named fixture group without its name, so the captured searchable
summary was `Group with 3 objects`; the test compares against the value actually
delivered rather than inventing the lost name.

## Fixture evidence and remaining gaps

The following local runs used Windows Rhino 8.36.26293.11001 and isolated,
sequential hosts. Their results describe the named builds, not an untested final
package. `FixtureUserData.EnsureRegistered()` calls the public, idempotent
`Rhino.Runtime.HostUtils.InitializeRhinoCommon()` and registers the fixture
plugin in memory; it installs nothing. The isolated Rhino.Inside host required
this initialization for SDK dictionary serialization. The installed
RhinoCommon documentation says initialization normally occurs automatically
when a RhinoCommon plugin is loaded, including a normal interactive host.

| Fixture and build | Observed evidence |
| --- | --- |
| Combined capture and preview, run `nHs3H2`, plugin SHA-256 `35e3a054c3d9dc7a6c25e85f3c012bc66bcd31d7b747a20089f3576e85d0f5db` | `capture-stock-geometry` passed 37 assertions, `preview-additional-geometry` 57, `preview-additional-draw` 11, `preview-single-pass-meshing` seven and `preview-single-pass-geometry` nine. The original `highlight-overlap` and `item-order-capture` fixtures passed nine and 10 assertions. `preview-reentrant-draw`, `preview-native-resources` and `preview-conduit-resources` failed on a later `DrawToBitmap`; `preview-compatibility-draw` crashed in its first capture. This intermediate desktop host processed Rhino events and idle tasks; the failed fixtures remain failed. |
| `preview-additional-geometry` and `preview-additional-draw`, run `j5Z1LG`, plugin SHA-256 `8dad66db0f86ef1f6ba0902bbef3595cc54bda058103ac508f1dc7ab754863c5` | Passed the same 57 and 11 assertions after correcting a source comment. The plugin behavior is unchanged from the preceding run. |
| `preview-additional-geometry` and `preview-additional-draw`, run `7PtSye`, plugin SHA-256 `765b0406319b3b5b5922a3c9c2010974018352d5b26d2f48af334bef826a1ee8` | Passed 57 and 11 assertions. Geometry coverage includes standalone/group preview and frame, mixed-group and standalone archive/real Paste with root custom data and cloud attributes, loaded-reference preview/frame/copy, stale reference and hidden-flag callbacks, bounds before/after cloud mutation and extrusion replacement, point/color ordering, 8,193-point admission, over-limit partial actions and typed D/S/B/F. The draw fixture checked actual selected/filtered pixels for standalone/group clouds and extrusions plus bounding-box, sphere and polyline wrappers, and the colored/hidden cloud. Its explicit cleanup seam does not prove the general idle/flush boundary. |
| `preview-additional-geometry` and `preview-additional-draw`, run `QzqeOz`, plugin SHA-256 `4e94ebce7458893b457061228873b875b16f7baf4c3224f059226e3b8154272d` | Passed 37 and 11 assertions on the then-current candidate. The former checked standalone/group bounds, frame and archive copies, real Paste of all five values, source metadata, reentrant graph changes, partial/exhausted actions and typed D/S/B/F. A direct Rhino 3DM control proved the hidden-flag loss and the copy action visibly rejected it. The latter checked actual selected/filtered wire and shaded pixels for standalone/grouped clouds and extrusions and the three wrapper values, plus a colored/hidden cloud and explicit post-capture cleanup. |
| `preview-additional-geometry` and `preview-additional-draw`, run `GCB3vH`, plugin SHA-256 `be4841f2d5ed5802d77b17ac449a10af26c46a8c51c7b0c0c1c1db99ba623a63` | The geometry fixture failed after observing that Rhino's archive cleared a source cloud's hidden flag while preserving the logged colors, normals, values and root custom-data payloads. The draw fixture passed 10 assertions using point batches. The later run explicitly rejects the unsupported hidden-flag copy. |
| `preview-additional-geometry` and `preview-additional-draw`, run `3tkfo2`, plugin SHA-256 `0734fcbb97ed26fe62b67c1e8f22940eb81b6bfb9197ab95ce7842fc20349ed8` | The geometry fixture failed its mixed-group metadata/attribute check. The draw fixture admitted all seven entries, then crashed in Rhino's native `CRhinoDisplayPipeline_DrawPointCloud` while Panel+ was drawing a cloud. The later point-batch result above does not make the crashed run a pass. |
| `preview-safety`, run `T38ZA5`, plugin SHA-256 `527d800a9c988432389376680d41e8a2213ebbd36da16e9cfb315b8cc93b6d1c` | Passed 199 assertions, including bounded staged reentry and exactly-once cleanup, successive sparse selections, source replacement, host preview-cache invalidation and actual drawing outside the old bounds. |
| `preview-geometry`, run `T38ZA5`, same plugin | Passed 79 assertions on the earlier fixture source. It covered owned ordinary geometry, loaded references, 1,025-point NURBS and 2,049-point polylines, root/nested SDK dictionaries and custom-data clipboard round trips, and actual Paste controls for ordinary user strings and custom data. |
| `preview-metadata-safety`, run `k3ARHE`, same plugin | Passed 76 assertions, including skipped-group mutations, same-value reference retargeting, same-ID object replacement, dense toggles, partial-action reporting and reentrant clipboard/frame publication. Unsupported types were visibly skipped; only a separate supported point was pasted in that partial-action control. |
| `row-actions`, run `k3ARHE`, same plugin | Passed 152 assertions, including selected-value copy freshness controls. An earlier run `T38ZA5` failed its mixed text/null clipboard assertion; the later pass does not explain that failure. |
| `preview-single-pass-geometry`, run `jqlhqq`, same plugin | Passed nine assertions on the earlier implementation, including actual standalone/grouped box wire and shaded pixels, old Brep pixels after mutation without expiration, rebuilt Brep pixels after expiration, bounds and cleanup. |
| `preview-single-pass-meshing`, run `T38ZA5`, same plugin | Passed six assertions on the earlier implementation, including actual shaded drawing, low/high/custom mesh regeneration and reuse without a source solve, and callback lifetime. That implementation synchronously flushed the pipeline and is no longer used. |
| Four-fixture batch, run `Aqlnf2`, plugin SHA-256 `ffbe81f6a8f1e923bfa1cf56d67e5a9f900ad7d7e16ad530f957aae76e5628c2` | **All four failed.** `preview-single-pass-meshing` passed five assertions and `preview-single-pass-geometry` passed eight actual drawing/bounds assertions, then each timed out waiting for idle retirement. `preview-compatibility-draw` crashed in Grasshopper's native display flush during its first capture. `preview-geometry` passed 67 assertions, then could not read the reported-success clipboard archive. |
| `preview-retirement-seam`, run `ySzCfi`, same plugin as `Aqlnf2` | Passed five assertions in an actual shaded capture. Explicitly invoking the retirement handler during active wire and mesh callbacks, nested redraw, mesh reset and document-close cleanup kept the resources alive; invoking it after capture returned released them without double accounting. A separate real `RhinoApp.Idle` observer saw zero events during capture and the following second. The direct invocation tests the managed seam, not real idle delivery or a general post-flush guarantee. |
| `preview-stock-compatibility-draw` and `preview-compatibility-draw`, run `uhoZ0k`, plugin SHA-256 `534fe189a4851840d06ab50e35a510d9cfe87333517145860da158caaee959aa` | Both crashed on their first inactive-desktop capture, at `GH_Document.PreviewDrawObjects` / `CRhinoDisplayPipeline_Flush`, before fixture assertions. The stock-only control used four Grasshopper curve parameters, the same long curves and viewport, and a Rhino draw conduit. It is diagnostic evidence, not a pass for the production fixture or its pixel assertions. |
| `preview-geometry`, run `uhoZ0k`, same plugin | Failed after 76 assertions. The diagnostic showed the verified prepared archive and actual clipboard bytes matched by SHA-256, with no clipboard sequence change during the read. Rhino accepted Paste, but the post-Paste type assertion failed: both degree-one NURBS came back as `PolylineCurve`, with the expected point count and user strings. |
| `preview-geometry` and `preview-metadata-safety`, run `AEZwGq`, plugin SHA-256 `e0bfea1510c5d353966db1c3671dea29a44c969acb056744420cb77feb73d69f` | Passed 88 and 80 assertions respectively. The actual clipboard matched the prepared archive; Paste retained all checked coordinates and metadata for the degree-one curves, the higher-degree NURBS copies and the polylines, while leaving the sources unchanged. Deferred frame/copy controls also rejected viewport-lock and tolerance changes made in the final source predicate. |
| `preview-safety` and `preview-retirement-seam`, run `otFTIW`, same plugin as `AEZwGq` | Passed 199 and five assertions respectively. The retirement-seam observer again saw zero real idle events during capture and the following second. |
| `preview-compatibility-conduit`, run `BuNAze`, same plugin as `AEZwGq` | Crashed in native `CRhinoDisplayPipeline_DrawToBitmap` with document preview disabled and no fixture assertions. The recorded stack did not include Grasshopper's managed preview callback; it does not establish whether the conduit callbacks executed. |

The updated `preview-geometry` fixture also failed the same real-Paste type
check in run `RQW1Z9`, after the archive retained both curve types, point counts
and user strings and Rhino accepted Paste. Run `uhoZ0k` identified the observed
degree-one normalization above. Its matching clipboard bytes do not explain the
intermittent unreadable clipboard archive in run `Aqlnf2`; neither failure is
resolved by the earlier 79-assertion pass. The fixture now retains the linear
curve's source/archive checks and compares all pasted points and metadata even
if Rhino normalizes its representation, and adds a curved higher-degree NURBS
control for post-Paste type, points, weights and knots. That revised control
passed in run `AEZwGq`; the earlier intermittent unreadable archive is still
unexplained and should be watched in the final combined run.

The former Eto/WPF byte-array writer was compared in run `rJsBMP`: a 7,342-byte
archive produced a 7,386-byte native clipboard payload beginning with the WPF
serialized-object marker. Both same-process managed readers returned the
original bytes and Rhino Paste succeeded in that trial. The replacement raw
WinForms stream writer passed three fresh `preview-geometry` runs of 89
assertions, including a separate process reading and hashing the native clipboard
handle and actual Rhino Paste. This explains the representational difference;
it does not prove the cause of every earlier intermittent managed read failure.

The stock-only long-curve comparison and the alternative Panel+ conduit capture
remain in the inventory and can be run explicitly with `--fixture
preview-stock-compatibility-draw` and `--fixture preview-compatibility-conduit`.
Both are opt-in diagnostics. The conduit capture disables the Grasshopper
document preview callback but retains the same Panel+ draw calls, source mutation
and pixel checks; it uses the explicit retirement seam for post-capture cleanup.
It failed in run `BuNAze` as recorded above. The production
`preview-compatibility-draw` and all original resource fixtures remain required.

The original `item-order-capture` and `highlight-overlap` assertions remain
unchanged; the parent reports 10 and nine passing assertions respectively on
an inactive Windows desktop. The original `preview-native-resources` and
`preview-conduit-resources` fixtures each drew a wrapped point, then failed on
their second `DrawToBitmap` call. `preview-reentrant-draw` passed its first wire
callback lifetime assertion and failed on its second capture. A stock-only
Grasshopper point diagnostic also failed its second capture, including with a
fresh view and an alternate `RhinoView.CaptureToBitmap` attempt. This is useful
host evidence, not a pass for the Panel+ fixtures. The single-pass fixtures
exercise several behaviors in one native frame but do not establish equivalent
between-frame coverage. An earlier explicit `DisplayPipeline.Flush` from a
draw callback was also followed by a fatal flush crash, so that call was removed;
the current compatibility-draw crash occurred without that explicit call.
The parent also reports an experimental `RhinoCore.Run` message loop that
delivered 16 real idle events after the retirement-seam capture and reached all
assertions in single-pass meshing and reentrant draw. Those processes exited
abnormally after `PostQuitMessage`, so the runs remain failed and do not yet
establish a supported shutdown or general nested-frame resource lifetime.
The parent subsequently reports that forwarding WinForms `Application.Idle`
to `RhinoCore.DoIdle` delivered `RhinoApp.Idle` and exited normally:
`preview-retirement-seam` passed five assertions and
`preview-single-pass-meshing` passed seven. `preview-reentrant-draw` still
failed on its second `DrawToBitmap`. The message-loop results support idle
delivery in that embedded configuration; they do not prove a general native
post-flush lifetime boundary or turn the failing repeated-capture fixture into
a pass.

The two repeated-capture resource fixtures' assertions map to the following
evidence. A passed assertion inside a failed fixture is identified as partial
evidence; it does not make the fixture pass.

| Original assertion | Evidence and limits |
| --- | --- |
| A wrapped point has actual viewport pixels; the conduit version also makes the live wrapper cyclic after admission. | Each original resource fixture passed its first assertion in `kbGGnf` before failing on its second capture. |
| Standalone and grouped boxes retain their bounds and bounded resources under low, high and custom mesh settings; the conduit version exercises wire and shaded callbacks. | `preview-single-pass-geometry` in `Aqlnf2` passed the corresponding in-frame bounds/resource checks and separate wire/shaded pixel checks for both boxes. It did not establish repeated-frame behavior or the original exact retained-byte equality after deferred retirement. |
| Standalone and grouped Breps regenerate bounded shaded meshes after each setting change and reuse them when settings do not change. | `preview-single-pass-meshing` in `Aqlnf2` passed one actual shaded capture and all three regeneration/reuse assertions within that capture; it then failed its idle-retirement wait. Earlier run `T38ZA5` passed six assertions with the now-removed synchronous flush implementation. |
| A live Brep changes, while old standalone/group bounds and actual drawing remain coherent without source expiration. | `preview-single-pass-geometry` in `Aqlnf2` passed the mutation/bounds check and separate old-generation pixel assertions for both Breps, before failing its idle wait. |
| After rebuilding, standalone/group bounds and actual drawing reflect the new Brep geometry. | The same `Aqlnf2` fixture passed its rebuilt bounds, document-cache and two new-generation pixel assertions within the same capture; it did not test a later native frame. |
| Clearing disposes generated meshes and document-close cleanup releases resources. | `preview-retirement-seam` in `ySzCfi` passed explicit post-capture cleanup and exactly-once accounting, including a document-close callback. It does not prove real idle delivery, shutdown cleanup or an end-of-frame boundary. The original immediate-clear assertion has not been reached under the deferred-retirement implementation. |
| Selection-only changes invalidate Grasshopper's component/document preview caches and draw at new bounds. | `preview-safety` in `otFTIW` passed the host cache and actual viewport checks on the current candidate; the combined final build still needs those original assertions rerun. |

All original fixtures remain in the inventory. These failures, eventual idle
cleanup, safe post-flush lifetimes, actual drawing of the larger compatibility
curves, and final-package validation require further investigation and review.
