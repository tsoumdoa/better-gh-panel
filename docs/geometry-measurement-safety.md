# Geometry measurement integration

This is a partial restoration. The production resolver can prepare deferred
lengths for exact `GH_Curve` holders containing bounded `PolylineCurve` or
`NurbsCurve` values. General polycurves, Breps and surfaces still have no
verified bounded provider and remain unavailable for deferred measurement.
Analytic line, arc, circle, rectangle and box values continue to use the
existing snapshot presentation. Existing native expectations for unsupported
geometry are retained and are not evidence that those paths have been restored.

Capture reads the exact inner geometry once and keeps that reference with the
snapshot generation. Referenced Grasshopper holders and document-controlled,
disposed or corruption-testing sources are rejected. For candidate curves,
dimension, point count, NURBS order and knot count are checked before `IsValid`
or further source validation. The resolver reconstructs from the captured
reference, keeps it alive through native reads, and checks the holder again
before preparation and before a result can be published or reused. Replacement
or disposal hides the result and invalidates the snapshot for recapture.
Preparation never duplicates or serializes source geometry or its UserData.

The polyline path accepts at most 2,048 three-dimensional points. It copies
coordinates and every parameter, applies the parameters with `SetParameter`,
validates the independent worker copy, and verifies every copied value bit for
bit. The NURBS curve path accepts dimensions 2 or 3, order at most 12 and at
most 1,024 control points; it reconstructs knots, homogeneous control points,
weights, rational state and domain. It independently validates the worker and
compares those values bit for bit. Each witness is built from the actual worker
copy and includes measurement role, model units, unit system and tolerance.
Unsupported or inexact values are unavailable. A zero returned by Rhino's
length API is treated as unavailable because the same return can indicate
failure.

Both curve providers share a process-wide 4 MiB reservation before allocating
value arrays, witness buffers or worker geometry. Reservations cover estimated
payloads and a fixed per-candidate allowance, and are released when unclaimed
work is abandoned or a claimed worker completes. Capacity exhaustion is
temporary: the queue waits asynchronously for a release, without polling, and
honors cancellation and snapshot replacement. A separate retention budget
limits witnesses to 128 and 2 MiB per session and 8 MiB across sessions. A
unit-only solve retains a measurement only when the new worker witness matches;
otherwise it is remeasured. These estimates do not bound object headers,
Rhino's allocator, caches, or the later native length algorithm.

Exact managed wrapper types and ownership checks do **not** prove the native
subclass, sandbox arbitrary third-party native behavior, or prevent another
thread from mutating or disposing a native object concurrently with a read.
The integration relies on the host to keep a source stable for its snapshot
generation until preparation completes, and to expire the solution after an
in-place change. `GC.KeepAlive` protects managed lifetime, not concurrent
external disposal. These are platform limitations, not claims of native
isolation. The standalone NURBS surface candidate remains restricted to its
explicitly trusted fixture and is not connected to production input.

The registered `production-curve-measurement` Rhino fixture uses an actual
`GH_Document`, component, session and resolver. It covers visible and offscreen
measurements, metadata and output identity, preflight gates, replacement and
disposal, unit-only retention, cancellation, and shared capacity recovery across
panels. The existing isolated fixtures cover UserData, weighted and periodic
NURBS and explicitly reparameterized/arc-length polylines. Managed tests cover
the production queue and shared budget with deterministic boundaries. Earlier
isolated Windows Rhino 8.36 runs passed 27 capture-safety, 28 polyline-safety,
and 67 NURBS-candidate assertions. The integrated production fixture and the
updated polyline cases have been compiled but still require a sequential Rhino
host run. Managed UserData counters do not detect all possible native virtual
or UserData calls.
