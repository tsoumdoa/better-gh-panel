# Geometry measurement integration

This work is not release-ready. The component's polyline candidate is disabled
until the isolated Rhino 8.36 fixture establishes the required UserData and
metadata behavior and input capture admits that exact case. The fixture is
compiled but has not been run. Existing native geometry expectations are kept.

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

Before enabling it, integrate the capture classifier's single captured trusted
inner reference and admit only the demonstrated safe polyline case. General
curves and Breps need separate, bounded, callback-free providers and complete
value witnesses. The nested PolyCurve and Brep UserData checks in the new native
fixture exercise conservative rejection, not support for copying those objects.
The host fixture must prove that the nested data was actually attached before
the plugin call. The fixture also needs to be registered as its own isolated
case by the native coordinator.

The queue retains at most 128 witnesses and 2 MiB of witness data; entries
without proof are remeasured on a unit-only snapshot replacement. Workers own
claimed resources until they finish, while cancellation can abandon only an
unclaimed copy. Existing native tests that inject the legacy bare delegate have
no proof and must be adapted to inject owned work with explicit test witnesses
before they can assert retention. Their retention expectations should not be
removed. Deterministic managed tests exercise the production queue through
injected UI, time, and worker boundaries.
