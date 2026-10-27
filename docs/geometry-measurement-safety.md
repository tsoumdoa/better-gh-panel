# Geometry measurement integration

This work is not release-ready. The component's polyline candidate is disabled
until the isolated Rhino 8.36 fixture establishes the required UserData and
metadata behavior and input capture has a trustworthy native provenance gate.
The fixture is compiled but has not been run. Existing native geometry
expectations are kept.

Analytic line, arc, circle, rectangle, and box values continue to come from the
existing snapshot presentation. Scheduled measurements of ordinary NURBS and
polycurves, surfaces, and Breps have no verified bounded provider. They are
reported unavailable when eligible for scheduling; their typed inputs and
outputs are retained. This loss of measurement functionality is unresolved.

The candidate reconstructs an exact 3D `GH_Curve`/`PolylineCurve` with at most
2,048 points from coordinates and parameters. It rejects a reconstruction if
the worker copy differs from those captured values. Its witness is read from
that copy before the worker can claim it, and includes the complete point and
parameter values, measurement role, model units, unit system, and tolerance.
Non-default parameterizations that cannot be reproduced are unavailable. This
path neither duplicates nor serializes the source. Exact managed types do not
prove the absence of custom native behavior; concurrent external mutation or
disposal of native objects remains a host limitation.

Before enabling it, integrate the capture classifier's single captured inner
reference, prove native provenance, and admit only the demonstrated case. General
curves and Breps need separate, bounded, callback-free providers and complete
value witnesses. The nested PolyCurve and Brep UserData checks in the native
fixtures exercise conservative rejection, not support for copying those objects.
The fixtures assert that nested data was attached before the plugin call and
are registered as isolated cases; they have not been run as part of this change.

The queue retains at most 128 witnesses and 2 MiB of witness data per session,
and at most 8 MiB of witness payloads across all sessions. Entries
without proof are remeasured on a unit-only snapshot replacement. Workers own
claimed resources until they finish, while cancellation can abandon only an
unclaimed copy. Native retention tests use owned work with explicit synthetic
test witnesses; their retention expectations have not been removed. Deterministic
managed tests exercise the production queue through
injected UI, time, and worker boundaries.

## Experimental exact-value NURBS candidate

`NurbsMeasurementCandidate` is **not connected to automatic input**. The
isolated `nurbs-value-candidate` fixture is the only caller. Its source objects
are newly constructed for the fixture; the candidate requires independent proof
of the built-in native type, an owned source pointer and source stability for
the whole capture. RhinoCommon's exact managed wrapper does not provide that
proof. `IsDocumentControlled == false` is not, by itself, a provenance or
lifetime guarantee, and managed wrappers do not sandbox native subclasses or
arbitrary native code. Do not enable this path for an ordinary `GH_Curve`,
`GH_Surface` or `GH_Brep` on that basis.

Under those preconditions, the candidate attempts to reconstruct standalone 2D/3D NURBS
curves and 3D NURBS surfaces using every knot, homogeneous control point and
weight, degree/order, dimensions, rational state, and domain. It checks that
the worker copy reproduces all of these values bit-for-bit and is valid before
dispatch; the witness is read from that copy and includes role, metric,
model units, unit system and tolerance. Invalid/unset/non-finite values,
nonpositive weights, incompatible dimensions, large counts, and failed or
inexact reconstructions are rejected. This preserves the candidate's selected
values and parameterization, but is not evidence that an arbitrary native
subclass evaluates like the built-in NURBS base class.

Curve order is limited to 12 and control points to 1,024; surface order is
limited to 12 per direction, each axis to 128 points and the product to 4,096.
Checked sizes precede value-array and worker allocations. A shared 4 MiB
in-flight reservation covers estimated value buffers, witness clone and owned
NURBS payloads, with an additional fixed allowance per candidate. The queue's
separate shared retention budget accounts for witness payloads before retention.
Neither budget proves a hard process peak: object headers, allocator overhead,
Rhino caches and the later native length/mass-property algorithms are not
bounded by these estimates.

The installed Rhino 8.36 managed implementation was inspected to choose the
curve control-point indexer (the `Point4d GetPoint` overload can copy on write)
and the surface `Point4d GetPoint` overload. The surface dimension check uses
`PointSize` only under the fixture's owned-pointer precondition, with corruption
testing disabled; a borrowed Brep surface cannot use that assumption. These
are static observations about the installed version, not a cross-version API
guarantee or a host test result.

The registered fixture source covers benign and throwing managed UserData,
source metadata preservation, weighted and periodic curves, non-default
domains, a weighted standalone surface, nested data in a trimmed Brep,
point-count rejection, metric equivalence, and changed/unchanged unit-only
retention. It has compiled, but has not run in Rhino. Managed UserData counters
cannot detect native geometry virtual callbacks or native UserData `SizeOf`
callbacks. Native subclass instrumentation, a supported exact-native-type
and lifetime gate, host metric/callback checks, and a source-stability contract
remain necessary before automatic admission. General trimmed Breps, solids,
untrimmed Breps, polycurves, planes/revolutions and other opaque or borrowed
surfaces remain unavailable for deferred work; their input/output values are
not replaced. Existing native curve/surface/Brep expectations remain in place.
