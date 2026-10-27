# Geometry preview and row actions

This work requires parent validation in isolated Rhino 8.36 processes before it
is release-ready. The fixture now registers `FixtureUserDataPlugin` in memory;
it does not install a plug-in or change Rhino's persistent plug-in list. Native
fixtures were compiled in this worktree but were not run while the parent owned
the sequential host suite. The original `item-order-capture` and
`highlight-overlap` fixtures and their assertions remain in place. The parent
reports that both passed in a normal Rhino 8.36 host on a separate inactive
Windows desktop; minimized or hidden hosts still crashed. The parent is handling
the isolated-desktop launcher and final-package verification.

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
the preview or an action. A fixture for standalone, grouped and wrapped loaded
references is compiled but has not been run in the host.

Points, lines, arcs, circles, rectangles, boxes, bounded polylines and meshes
are reconstructed from value fields. Bounded NURBS curves use a value-only
reconstruction adapted from the 67-assertion Rhino 8.36 candidate at `7a2d0e8`:
all knots, control points, weights, order, dimension and domain are compared to
the owned curve before use. Live source UserData is not duplicated or removed.

For polycurves, Breps, surfaces and SubD, a structural inspection with node,
collection, control-net and estimated-byte limits checks the exact managed SDK
geometry and its exposed nested components, including
Brep curves, surfaces, faces, loops, trims, edges, vertices and attached face
meshes, plus polycurve segments and revolution profiles. A native copy is attempted
only after exposed metadata is classified and a native-size estimate is reserved.
Unknown nested geometry or custom UserData is visibly unavailable for automatic preview.
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
while the effective parameters match, and disposed on parameter changes,
eviction or clear. Generated meshes use the face-index drawing overload, and
SubD evaluation caches are cleared. The native pipeline's possible retention of
mesh resources after a draw callback, including through a later flush, is not
proven by the managed lease and still needs host validation.
Oversized detail is reported as partial. One native duplication or meshing
call can still have an implementation-dependent allocation peak; the estimates
are not a hard process-memory guarantee.

Each root has a 4,096-visit, 1,024-leaf and depth-64 limit. Builds and actions
share a 20,000-visit budget. At most 1,024 root preview entries and an estimated
16 MiB are admitted. A later selected row may evict an unselected preview;
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
The metadata and ordinary-geometry Rhino fixtures have been
compiled, not validated in a host in this worktree. These are remaining
compatibility and release gaps. Automatic preview
can reconstruct simple value geometry and NURBS even when custom UserData is
attached. Automatic native preview does not duplicate unknown custom data.
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
same live-memory cap, and stale subsequent draws and labels stop. If deferred
retirement occupies the cap, a replacement can remain visibly partial until a
later selection or source refresh. The reentrant draw, frame and clipboard
fixtures are compiled but have not run in a host. Reinspection can
compare native object identity but has no complete bounded value witness for an
in-place mutation of the same Brep, surface, polycurve or SubD. Native calls and
unknown callbacks have no hard execution-time bound, and a reentrant mutation of
the same native object remains an unproven case. No such case is treated as
evidence that the operation is safe or release-ready.

Each preview row is prepared from the terminal leaves in one bounded graph
capture. A fixture injects replacement and restoration of a later group member
and wrapper while an earlier leaf is being prepared, checks the retained draw
value against its bounds, and checks host cache refresh and disposal. That
fixture has compiled but has not been run in this worktree.

The existing `row-actions` fixture and its mixed-geometry clipboard assertions
remain intact. Its previously observed deferred area/volume failure belongs to
the separate measurement provider; preview and explicit copy do not enable it.

The result-local compressed selection index preserves immutable ordered lists
and updates a single toggle in logarithmic index work. Preview membership uses
the matching selection revision to update only that position, including after a
dense or select-all gesture. Bulk changes, arbitrary external position lists,
output reconstruction and viewport drawing may still perform proportional work.
Managed visit-count tests and compiled host fixture source cover the single-row
gestures in both preview modes with labels on and off.
