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

Those 80,000 point rows do not populate the deferred measurement cache. They
therefore did not expose the additional cache scan in `PublishOutputs` at parent
commit `adfc34f6b9d986cced5f85c5d8ca0407b92deb70`. That call copied every cached
measurement entry, then called `MeasurementSourceCurrent` for each before output
reconstruction. Its holder/inner-geometry/role/reference and document-unit-tolerance
checks repeat a subset of the final `CapturedSnapshotCurrent` fence. The latter
checks every captured geometry source, including unmeasured or invalid ones,
plus graph/value witnesses, conversion, ordered D/F structure, component lifetime,
solution ownership and context before and after validation. The publication owner
also checks result, selection and output scope before and after that fence.

The two APIs are not interchangeable in general. The queue scan also checks its
attached snapshot and protects its enumeration from reentrant entry changes.
Output reconstruction consumes an immutable, already-published query result and
captured trees, not cache entries, so it needs no cache enumeration. Its final
capture fence now additionally checks queue attachment before and after validation
with the callback-free `IsAttachedTo` check. `PublishOutputs` no longer calls the
cache scan before reconstruction. No source check at measurement preparation,
dispatch, completion, detail access, measured-query publication or value-copy
publication has been removed. There is no cached freshness success across gestures.

Opt-in production diagnostics now count actual measurement-cache slot reads
(successful dictionary lookups, copied slots and enumerated slots), copied entry
counts, source-resolver calls and full cache scans, including accesses outside
`SourcesCurrent`. These are separate from capture-validation passes/visits,
capture measurement-source visits, logical D/S/B/F reconstruction and published
items. The managed production-queue control populated 4,096 completed entries
through its preparation/worker boundaries. One unchanged `SourcesCurrent` call
recorded exactly 16,384 cache-slot reads, 4,096 copied entries and 4,096 source
checks. Same-snapshot attachment and output-reuse ownership checks recorded zero
of each and did not remeasure. An ordinary detail read recorded three cache-slot
reads and one source check, proving the instrumentation is not restricted to the
full-scan helper. Stale-source, reentrant replacement, suspension and disposal
controls still reject the corresponding old work. These are deterministic managed
operation counts, not native geometry timings or an end-to-end host measurement.

The extended `capture-transaction` fixture retains the original point gestures and
adds 2,048 actual polyline/NURBS rows and 2,048 paired integers. It populates all
measurement entries through the unchanged production resolver/workers and executes
a Length filter. For Selected/Filtered results preview, labels off/on and
sparse/dense/select-all selection, its timed interval covers production selection,
owned preview preparation, scheduled GH output publication, and the actual canvas
renderer (including measured details and visible measurement demand). It records
preview build rows/nodes/entries too; native viewport pixels and deferred native
display lifetime remain the responsibility of the existing rendering fixtures.

For this measured-tree fixture the enforced expected counts per gesture are one
complete capture fence with 10,240 visits across five linear checks (2,048 geometry
holders, managed-row checks, conversion mappings, D identities and F identities).
It requires zero full cache scans or entry copies; cache reads/source checks are
limited to rendered rows, with ceilings of eight/four per visible row. With
1/1,024/2,048 selected rows, respectively, the output reconstruction counts are
6,145/7,168/8,192 and published D/S/B/F counts are 4,098/6,144/8,192. Thus a sparse
gesture still has full input freshness and original-shape output work. The change
removes the additional measurement-cache pass, not those necessary linear checks
or the dense preview/selection work. Another added control replaces an already
measured curve after output reconstruction and requires zero `SetDataTree` calls,
then recovery to fresh Length membership; unchanged output-only reuse is retained.

The combined intermediate build passed `capture-transaction` in Rhino 8.36 with
126 assertions in run `FezgKh`, using plugin SHA-256
`2c9990e422a546a79a316191f6189fb2588ccb7a365e99484ad71292f1cf7131`.
All 12 measured-curve gestures met the stated counts, with elapsed times of
17–37 ms; these times exclude native viewport drawing. The fixture still performs
one full capture validation per gesture. The final extracted package and combined
implementation still require their own coordinator run and independent reviews.
The existing native callback/lifetime and silent in-place geometry mutation limits
remain, as do the outstanding exact-package rendering/release gates and unavailable
Mac and Windows .NET Framework host evidence.

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
