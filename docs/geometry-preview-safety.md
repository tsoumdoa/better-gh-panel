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
in-place mutation of the same Brep, surface, polycurve or SubD. Native calls and
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

Exact `GH_PointCloud`, `GH_Extrusion`, and wrapped `BoundingBox`, `Sphere` and
`Polyline` are currently reported as unsupported or skipped by these paths;
they are not silently counted as successfully previewed, framed or copied.
This remains a compatibility gap, including inside groups.

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
