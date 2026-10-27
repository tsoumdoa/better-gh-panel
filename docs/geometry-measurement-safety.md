# Geometry measurement integration

This experimental compatibility path restores deferred standalone curve length,
surface and open Brep trimmed area, and solid Brep volume. It uses beta.7's
public `DuplicateCurve` and `DuplicateBrep` APIs and existing measurement and
detail formatting when a bounded value provider cannot reconstruct the curve,
or no such provider exists. Exact built-in `GH_Curve`, `GH_Surface` and
`GH_Brep` holders are eligible, including loaded document references and valid
curves above the reconstruction limits. Unloaded, disposed, invalid, unsupported
or untrusted values can still be unavailable. No private Rhino ABI is used.
Group rows retain their name and direct count and acquire no aggregate metric;
the measurement fallback never recursively invokes or retains groups or wrappers.

Duplication is **not** callback-free or sandboxed. It may call installed native
or managed plug-in UserData duplication callbacks, including on nested objects.
Validation, duplication, native mass properties, their allocation and any plug-in
callback can be unbounded or block the UI or worker. Managed exceptions cause
preparation or measurement to fail where they propagate, but cannot guarantee
recovery from a native crash, hang, concurrent disposal or unmanaged failure.
Native serialization may omit UserData that its owner cannot or will not write;
the compatibility path does not strip data from the original source or explicit
typed output and does not promise preservation of nonserializable metadata in a
copy or archive. The fixture probes representative serializable root and nested
metadata, rather than claiming this holds for all installed plug-ins.

Capture keeps the exact inner source of every built-in curve, surface, Brep,
mesh, point-cloud and extrusion holder, including null, invalid and unverified
values, with the snapshot generation. It also records reference ID,
loaded/referenced state and curve edge
index. For references it records the
active Rhino document and referenced object's runtime serial numbers. Exact
holder/source, reference state, source lifetime and generation are checked after
preparation and before measurement publication or reuse. Referenced values are
used only when already loaded and the reference ID resolves in the active
document; this path does not explicitly load a reference. RhinoCommon does not
expose a public proof of which document originally loaded a holder, so matching
the active document is a context check, not a complete load provenance proof.
Rhino must keep the source stable while it is read and expire the solution after
in-place edits. A managed type check and
`GC.KeepAlive` do not prevent concurrent native mutation or disposal; replacing a
holder is detectable, but an unannounced in-place mutation in the same snapshot
is not. Corruption-testing mode is gated. Unknown holders and managed subclasses
remain unverified, and existing bounded group classification remains separate.
The active Rhino document's runtime identity, model unit system, unit label and
absolute tolerance also belong to the measurement context. They are rechecked at
dispatch, after preparation, completion and before publication or same-snapshot
reuse. A changed context invalidates the old result, and value witnesses are not
retained across a document-context change.

Exact `GH_PointCloud` and `GH_Extrusion` holders use the same best-effort native
validity and reference checks for capture, including when reached through a bounded
group or wrapper graph. Loaded nested references retain the active document and
object serials in the graph witness; unresolved references and unsupported holder
or inner subclasses remain unverified. These checks do not create new measurement
providers or group aggregate metrics. The Rhino host may perform unbounded native
validity work, and the witness cannot detect unannounced in-place native mutation.
In the Rhino 8.36 `capture-stock-geometry` fixture, 37 assertions passed for
valid, invalid and unverified holders and groups; real document references and
replacement; filters, statistics and typed D/S/B/F with Create Set on and off;
and recovery after capture-time replacement. The generic GH source in that host
turned unloaded standalone references into nulls before the panel received them.
The fixture separately invokes the real component solve through a controlled data
access seam to verify that exact unresolved holders are unverified when supplied.

Capture keeps transformed trees, source arrays and snapshot local until the
component's solve generation, document/session lifetime, D and F paths and ordered
members, value witnesses and source identities have been checked. Bounded group
and wrapper witnesses record ordered targets, names and trusted leaf values;
validity walks those captured members rather than rereading the live graph, so
temporary replacement and restoration cannot change the inspected child. Their
committed witnesses use weak references and do not own groups or wrappers. The
group and wrapper graph evidence is limited to 4,096 visits per root and 100,000
per capture. When this evidence is incomplete, deeper validity remains unverified.
The captured name, direct count and known identity of an exact group remain
available, including exact wrapper chains within the 64-node root depth limit; a
shallow witness rechecks each captured wrapper target and group name/count. The
shallow identity work is bounded per row and its retained evidence is proportional
to the rows and captured wrapper depth; chains beyond that limit cannot establish a
deeper identity or summary. Group summaries do not imply
validated children or an aggregate geometry metric.
Ordinary number, integer, boolean and text inputs are read into a temporary
per-row scalar array before callback-bearing inspection. Display, filtering and
unit conversion consume those captured values, so a temporary replacement and
restoration cannot alter the value that was inspected. Rows then reuse the frozen
values for a linear recheck, without per-row graph witnesses or copying strings;
the temporary scalar array is proportional to input size and is not retained with
the snapshot. Ordinary exact point and other supported managed value rows use a
separate linear witness containing immutable value copies, outside the graph
budget. Point reference identity, component parameters, loaded state and the
resolved document/object context are checked along with the coordinates; exact
plane and box reference contexts are checked too. Reference document lookups and
managed value validity checks that can enter native code are deferred until after
row and bounded-graph values have been captured. An exact box is first witnessed
as its raw value because its public goo constructor and setter may normalize it;
if a faithful copy cannot be made, its summary remains unavailable. Exact mesh
faces and simple stock transforms receive valid/invalid classification; compound
transforms containing unsupported or externally supplied implementations remain
unverified without invoking their methods. Unit conversion also checks the
pre-conversion inputs against the converted tree.
A changed capture receives at most one automatic retry; a continuing change
leaves it unavailable until the source expires again.

An output-only selection publication reuses the same snapshot. Output trees are
constructed from the captured structures first, then one complete freshness fence
runs immediately before output publication. The fence is linear in the rows,
tree structure and retained bounded graph evidence; it is not constant-time for a
large input. Diagnostic fixture counters report its visits separately from output
tree reconstruction and host output items. The preview build and output tree
construction can also take time proportional to their own selected or emitted data.

Bounded polyline and NURBS reconstruction still has priority for eligible local
curves. It reconstructs values and complete witnesses without duplicating source
UserData. The polyline provider accepts at most 2,048 3D points; the NURBS provider
accepts dimension 2 or 3, order at most 12 and at most 1,024 control points.
Both use the process-wide 4 MiB reservation for their own estimated payloads;
these estimates are not a bound on Rhino's allocator or measurement algorithm.
The separate witness retention budgets remain 128 entries and 2 MiB per session,
and 8 MiB across sessions. A new or unit-only snapshot retains a result only when
its complete witness matches; compatibility copies have no witness and are
remeasured. A publication-only solve of the same snapshot keeps its result.

Compatibility preparation acquires one of two process-wide permits before the
duplicate call. Other panels wait asynchronously, with cancellation and shared
demands honored. An unclaimed copy releases its permit after abandonment; a
claimed worker keeps it until completion, including after cancellation. These
permits limit concurrently owned compatibility work, not per-call native cost or
peak allocation. Each panel still schedules one worker at a time; preparation is
guarded against reentrant callbacks. Workers own and dispose their copies and do
not read live holders. A failed or abandoned copy cannot publish a stale result.

`standalone-compatibility` is an added real-GH solve fixture covering NURBS,
polycurves, oversized curves and length filtering, surfaces, open/trimmed Breps,
solids, real loaded references, output metadata, unit-only remeasurement,
callback exceptions, replacement during duplication and metadata roundtrips.
`capture-transaction` covers reentrant expiration, removal and locking; managed
leaf and numeric/text replace-and-restore; wrapper/group replace-and-restore;
D/F tree shape changes; and a 100,000-row scalar capture and recovery. The new
host-free capture-budget fixture also checks 100,001 points followed by a line,
shallow summaries for 4,096-member groups and wrappers, filters, typed D/S/B/F,
point-, plane- and box-reference retargeting, stock validity and hostile transform
behavior. The same fixture checks nested object and geometric wrappers after both
per-root and aggregate group budget exhaustion, including retarget and recovery.
In the coordinated Rhino host run, `capture-safety` passed 227 assertions and
`capture-transaction` passed 73. The latter exercises mesh/invalid-curve
replacement, loaded point references, the final output fence, and sparse, dense
and select-all gestures across both preview modes and label settings. A separate
managed run captured and checked 100,001 points in 142 ms and allocated 40,906,776
bytes on the fixture thread. These observations are not time or memory guarantees.

For each of the 12 host gestures over 80,000 points and 80,000 paired items, the
instrumented publication used one freshness fence and visited 480,000 witness or
structure entries. Sparse, dense and select-all gestures respectively recorded
240,001, 280,000 and 320,000 logical output reconstruction visits, and 160,002,
240,000 and 320,000 published items. Reconstruction visits are counted at the
tree-operation call sites and exclude selection bookkeeping, hashing, native
output internals and preview rendering. Elapsed time ranged from 24 to 51 ms
across the 12 cases. These counters distinguish validation, reconstruction and
host outputs; they are measurements of this fixture, not performance bounds.
The capture fixture uses explicit internal hooks
at callback boundaries; these are tests of the transaction, not a claim that native
callbacks are bounded or that host behavior has been exercised without Rhino.
The managed queue tests gate context changes during preparation and a claimed
worker; the host fixture additionally checks a real model-tolerance callback and
a unit change before output reuse.
The narrow-provider fixtures retain their zero-callback assertions; conflicting
expectations that production reject oversized or nested standalone geometry have
been revised. Managed queue tests exercise reentrant cross-panel admission,
cancellation, ownership and recovery. The combined branch still needs its
coordinated host run and final code reviews; successful compilation or these
isolated fixtures alone do not validate it.
