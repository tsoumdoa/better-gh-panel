using System;
using System.Globalization;
using System.IO;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal static class GeometryHints
    {
        // Only analytic measurements and stored properties belong in the snapshot.
        internal static PanelGeometryInfo? Describe(IGH_Goo goo, string units)
        {
            if (SafeGooInspection.IsGeometryHolder(goo.GetType()))
            {
                if (!SafeGooInspection.TryGetTrustedGeometry(goo, out GeometryBase? geometry) || geometry == null) return null;
                try
                {
                    if ((goo.GetType() == typeof(GH_Curve) || goo.GetType() == typeof(GH_Surface) || goo.GetType() == typeof(GH_Brep)) &&
                        !SafeGooInspection.MeasurementSourceCanRead(geometry)) return null;
                    return geometry.IsValid ? DescribeCaptured(goo, geometry, units) : null;
                }
                finally { GC.KeepAlive(geometry); }
            }
            return DescribeCaptured(goo, null, units);
        }

        // The caller validates this exact inner object before this method. Never read
        // a mutable holder again after checking it, and never dispatch through a subclass.
        internal static PanelGeometryInfo? DescribeCaptured(IGH_Goo goo, GeometryBase? geometry, string units)
        {
            Type type = goo.GetType();
            if (type == typeof(GH_Line)) return LineInfo(((GH_Line)goo).Value, units);
            if (type == typeof(GH_Arc)) return ArcInfo(((GH_Arc)goo).Value, units);
            if (type == typeof(GH_Circle)) return CircleInfo(((GH_Circle)goo).Value, units);
            if (type == typeof(GH_Rectangle)) return new PanelGeometryInfo("Rect", ((GH_Rectangle)goo).Value.Area, 2, units, "Area", "A");
            if (type == typeof(GH_Box)) return new PanelGeometryInfo("Box", ((GH_Box)goo).Value.Volume, 3, units, "Volume", "V");
            if (type == typeof(GH_Curve) && geometry is LineCurve line) return LineInfo(line.Line, units);
            if (type == typeof(GH_Curve) && geometry is ArcCurve arc) return ArcInfo(arc.Arc, units);
            if (type == typeof(GH_Curve) && geometry is Curve curve) return CurveInfo(curve);
            if (type == typeof(GH_Surface) && geometry is Brep surface) return SurfaceInfo(surface);
            if (type == typeof(GH_Brep) && geometry is Brep brep) return BrepInfo(brep);
            if (type == typeof(GH_Mesh) && geometry is Mesh mesh)
                return MeshInfo(mesh);
            return null;
        }

        // This entry point is also used by isolated host fixtures. Production passes
        // the holder and exact source captured with its snapshot generation instead.
        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareMeasurement(IGH_Goo? goo, string units,
            int unitSystem, double tolerance)
        {
            if (goo == null || !SafeGooInspection.TryCaptureStandaloneGeometry(goo, out var leaf) ||
                !leaf.Trusted || !leaf.HasReference || leaf.Geometry == null) return null;
            var source = leaf.Geometry;
            var reference = leaf.Reference;
            try
            {
                if (!SafeGooInspection.IsCurrentMeasurementSource(goo, source, reference)) return null;
                return source.IsValid ? PrepareStandaloneMeasurement(goo, source, reference, units, unitSystem, tolerance, false) : null;
            }
            finally { GC.KeepAlive(source); }
        }

        // The bounded value providers take priority. If one cannot reproduce an
        // eligible curve, or no provider exists, beta.7's public duplicate APIs
        // prepare a private worker copy. They can invoke plug-in UserData callbacks
        // and perform unbounded native work; this is not a sandbox or a witness.
        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareStandaloneMeasurement(IGH_Goo holder, GeometryBase source,
            MeasurementReferenceState reference, string units, int unitSystem, double tolerance, bool retryCapacity)
        {
            if (units == null || units.Length > 128 || !RhinoMath.IsValidDouble(tolerance) ||
                !SafeGooInspection.IsCurrentMeasurementSource(holder, source, reference)) return null;
            Type type = source.GetType();
            if (holder.GetType() == typeof(GH_Curve) && SafeGooInspection.CurveSourceCanRead(source) &&
                (type == typeof(LineCurve) || type == typeof(ArcCurve) ||
                 ((type == typeof(PolylineCurve) || type == typeof(NurbsCurve)) && SafeGooInspection.CurveCandidatePreflight(source))))
            {
                PreparedMeasurement<PanelGeometryInfo>? bounded;
                try { bounded = PrepareCapturedMeasurement(source, units, unitSystem, tolerance, true); }
                catch (MeasurementCapacityException) { if (retryCapacity) throw; return null; }
                if (bounded != null)
                {
                    if (SafeGooInspection.IsCurrentMeasurementSource(holder, source, reference)) return bounded;
                    bounded.Dispose();
                    return null;
                }
                // Failed analytic validation should not be bypassed by duplication.
                if (type == typeof(LineCurve) || type == typeof(ArcCurve)) return null;
            }

            IDisposable? admission = LegacyMeasurementAdmission.TryAcquire(out var released);
            if (admission == null)
            {
                if (retryCapacity) throw new MeasurementCapacityException(released);
                return null;
            }
            GeometryBase? owned = null;
            try
            {
                if (holder.GetType() == typeof(GH_Curve)) owned = ((Curve)source).DuplicateCurve();
                else if (holder.GetType() == typeof(GH_Surface) || holder.GetType() == typeof(GH_Brep)) owned = ((Brep)source).DuplicateBrep();
                else if (holder.GetType() == typeof(GH_Mesh)) owned = ((Mesh)source).DuplicateMesh();
                if (owned == null || !SafeGooInspection.IsCurrentMeasurementSource(holder, source, reference)) return null;
                bool surface = holder.GetType() == typeof(GH_Surface);
                var worker = owned;
                var resource = new OwnedMeasurementResource(worker, admission);
                var prepared = new PreparedMeasurement<PanelGeometryInfo>(() => Measure(worker, surface, units), resource, null);
                owned = null; admission = null;
                return prepared;
            }
            finally { try { owned?.Dispose(); } finally { try { admission?.Dispose(); } finally { GC.KeepAlive(source); } } }
        }

        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareCapturedMeasurement(GeometryBase source, string units,
            int unitSystem, double tolerance, bool retryCapacity)
        {
            try
            {
                if (source == null || units == null || units.Length > 128 || !RhinoMath.IsValidDouble(tolerance)) return null;
                if (source.GetType() == typeof(LineCurve) || source.GetType() == typeof(ArcCurve))
                    return PrepareAnalyticCurve(source, units);
                if (source.GetType() != typeof(NurbsCurve) && source.GetType() != typeof(PolylineCurve)) return null;
                if (!SafeGooInspection.CurveCandidatePreflight(source)) return null;
                if (source.GetType() == typeof(NurbsCurve))
                    return NurbsMeasurementCandidate.PrepareCapturedCurve((NurbsCurve)source, units, unitSystem, tolerance, retryCapacity);
                if (source.GetType() != typeof(PolylineCurve)) return null;
                return PreparePolyline((PolylineCurve)source, units, unitSystem, tolerance, retryCapacity);
            }
            finally { GC.KeepAlive(source); }
        }

        private static PreparedMeasurement<PanelGeometryInfo>? PrepareAnalyticCurve(GeometryBase source, string units)
        {
            try
            {
                if (!SafeGooInspection.CurveSourceCanRead(source)) return null;
                PanelGeometryInfo info;
                if (source.GetType() == typeof(LineCurve))
                {
                    Line line = ((LineCurve)source).Line;
                    if (!line.IsValid || !RhinoMath.IsValidDouble(line.Length) || line.Length <= 0) return null;
                    info = LineInfo(line, units);
                }
                else if (source.GetType() == typeof(ArcCurve))
                {
                    Arc arc = ((ArcCurve)source).Arc;
                    if (!arc.IsValid || !RhinoMath.IsValidDouble(arc.Length) || arc.Length <= 0) return null;
                    info = new PanelGeometryInfo(arc.IsCircle ? "Cir" : "Arc", arc.Length, units: units,
                        metricName: "Length", metricLabel: "L", details: ArcDetails(arc));
                }
                else return null;
                // Only immutable managed presentation crosses the worker boundary.
                return new PreparedMeasurement<PanelGeometryInfo>(() => info, null, null);
            }
            finally { GC.KeepAlive(source); }
        }

        private static PreparedMeasurement<PanelGeometryInfo>? PreparePolyline(PolylineCurve source, string units,
            int unitSystem, double tolerance, bool retryCapacity)
        {
            IDisposable? reservation = null;
            PolylineCurve? owned = null;
            try
            {
                if (!SafeGooInspection.CurveCandidatePreflight(source)) return null;
                int count = source.PointCount;
                if (!NurbsCandidateLimits.Polyline(source.Dimension, count, out int payload)) return null;
                reservation = NurbsCandidateLimits.TryReserve(payload, out var released);
                if (reservation == null)
                {
                    if (retryCapacity && released != null) throw new MeasurementCapacityException(released);
                    return null;
                }
                var points = new Point3d[count];
                var parameters = new double[count];
                for (int i = 0; i < count; i++)
                {
                    points[i] = source.Point(i);
                    parameters[i] = source.Parameter(i);
                    if (!points[i].IsValid || !RhinoMath.IsValidDouble(parameters[i]) ||
                        (i > 0 && parameters[i] <= parameters[i - 1])) return null;
                }
                owned = new PolylineCurve(points);
                for (int i = 0; i < count; i++) owned.SetParameter(i, parameters[i]);
                // Validate the reconstructed object independently of the source.
                if (!owned.IsValid || owned.Dimension != 3 || owned.PointCount != count) return null;
                var bytes = new byte[payload];
                using (var stream = new MemoryStream(bytes, true))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(count);
                    for (int i = 0; i < count; i++)
                    {
                        // The witness is read from the exact worker copy. Reject a
                        // reconstruction that changed coordinates or parameterization.
                        var point = owned.Point(i);
                        double parameter = owned.Parameter(i);
                        if (!Same(point.X, points[i].X) || !Same(point.Y, points[i].Y) ||
                            !Same(point.Z, points[i].Z) || !Same(parameter, parameters[i])) return null;
                        writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); writer.Write(parameter);
                    }
                }
                string context = "GH_Curve/polyline/length|" + unitSystem.ToString(CultureInfo.InvariantCulture) + "|" +
                    BitConverter.DoubleToInt64Bits(tolerance).ToString(CultureInfo.InvariantCulture) + "|" + units.Length + ":" + units;
                var witness = new MeasurementWitness(context, bytes);
                var workerCopy = owned;
                var resource = new OwnedMeasurementResource(workerCopy, reservation);
                var prepared = new PreparedMeasurement<PanelGeometryInfo>(() => Measure(workerCopy, false, units), resource, witness);
                owned = null; reservation = null;
                return prepared;
            }
            finally { try { owned?.Dispose(); } finally { try { reservation?.Dispose(); } finally { GC.KeepAlive(source); } } }
        }

        private static bool Same(double first, double second) =>
            BitConverter.DoubleToInt64Bits(first) == BitConverter.DoubleToInt64Bits(second);

        internal static PanelGeometryInfo? Measure(GeometryBase geometry, bool surface, string units)
        {
            switch (geometry)
            {
                case Curve value:
                    var curve = CurveInfo(value);
                    bool linear = value.IsLinear();
                    string shape = linear ? "linear" : value.IsPlanar() ? "planar" : "nonplanar";
                    string shapeDetails = value is ArcCurve arc ? (arc.Arc.IsCircle ? "Circle" : "Arc") :
                        linear ? "Linear" : value is PolylineCurve ? "Polyline" : "Curve";
                    double length = value.GetLength();
                    // Rhino also returns zero on a failed length calculation.
                    if (!RhinoMath.IsValidDouble(length) || length <= 0) return null;
                    return Metric(curve, length, 1, units, "Length", "L", shape,
                        "Shape: " + shapeDetails + "\nPlanarity: " + (linear || shape == "planar" ? "Planar" : "Nonplanar"));
                case Brep brep:
                    var summary = surface ? SurfaceInfo(brep) : BrepInfo(brep);
                    string? surfaceShape = brep.Faces.Count == 1 ? (brep.Faces[0].IsPlanar() ? "planar" : "nonplanar") : null;
                    string? surfaceDetails = surfaceShape == null ? null : "Planarity: " + (surfaceShape == "planar" ? "Planar" : "Nonplanar");
                    if (surface || !brep.IsSolid) return Area(summary, brep, units, surfaceShape, surfaceDetails);
                    using (var mass = VolumeMassProperties.Compute(brep, true, false, false, false))
                    using (var area = AreaMassProperties.Compute(brep, true, false, false, false))
                        return SolidMetric(summary, mass?.Volume, area?.Area, units, surfaceShape, surfaceDetails);
                case Mesh mesh:
                    var meshSummary = MeshInfo(mesh);
                    using (var area = AreaMassProperties.Compute(mesh, true, false, false, false))
                    {
                        if (!mesh.IsClosed)
                            return area == null ? null : Metric(meshSummary, area.Area, 2, units, "Area", "A");
                        using (var mass = VolumeMassProperties.Compute(mesh, true, false, false, false))
                            return SolidMetric(meshSummary, mass?.Volume, area?.Area, units);
                    }
                default: return null;
            }
        }

        private static PanelGeometryInfo? Area(PanelGeometryInfo summary, Brep brep, string units, string? shape, string? details)
        {
            // Measure the trimmed Brep, not its underlying untrimmed surface.
            using (var mass = AreaMassProperties.Compute(brep, true, false, false, false))
                return mass == null ? null : Metric(summary, mass.Area, 2, units, "Area", "A", shape, details);
        }

        private static PanelGeometryInfo CurveInfo(Curve curve)
        {
            string description = curve is PolylineCurve polyline
                ? "Polyline · " + Count(polyline.PointCount - 1, "segment")
                : "Crv · degree " + curve.Degree;
            string details = "Degree: " + curve.Degree + "\nTopology: " + (curve.IsClosed ? "Closed" : "Open");
            if (curve is PolylineCurve points) details += "\nSegments: " + (points.PointCount - 1);
            return new PanelGeometryInfo(description + " · " + Closed(curve.IsClosed), canMeasure: true, details: details);
        }

        private static PanelGeometryInfo SurfaceInfo(Brep brep)
        {
            if (brep.Faces.Count != 1)
                return new PanelGeometryInfo("Srf · " + Count(brep.Faces.Count, "face"), power: 2, metricName: "Area", metricLabel: "A", canMeasure: true,
                    details: "Faces: " + brep.Faces.Count);
            bool untrimmed = brep.IsSurface;
            var face = brep.Faces[0];
            return new PanelGeometryInfo("Srf · " + (untrimmed ? "untrimmed" : "trimmed"), power: 2, metricName: "Area", metricLabel: "A", canMeasure: true,
                details: "Trimming: " + (untrimmed ? "Untrimmed" : "Trimmed") +
                    "\nDegree (U/V): " + face.Degree(0) + "/" + face.Degree(1));
        }

        private static PanelGeometryInfo BrepInfo(Brep brep)
        {
            bool solid = brep.IsSolid;
            return new PanelGeometryInfo(
                "Brep · " + Closed(solid) + " · " + Count(brep.Faces.Count, "face"), power: solid ? 3 : 2,
                metricName: solid ? "Volume" : "Area", metricLabel: solid ? "V" : "A", canMeasure: true,
                hasArea: solid,
                details: "Topology: " + (solid ? "Closed solid" : "Open") + "\nFaces: " + brep.Faces.Count +
                    "\nEdges: " + brep.Edges.Count + "\nVertices: " + brep.Vertices.Count);
        }

        private static PanelGeometryInfo MeshInfo(Mesh mesh) => new PanelGeometryInfo(
            "Mesh · " + Closed(mesh.IsClosed) + " · " + mesh.Vertices.Count + " vertices · " + Count(mesh.Faces.Count, "face"),
            power: mesh.IsClosed ? 3 : 2, metricName: mesh.IsClosed ? "Volume" : "Area", metricLabel: mesh.IsClosed ? "V" : "A",
            canMeasure: true, hasArea: mesh.IsClosed,
            details: "Topology: " + (mesh.IsClosed ? "Closed" : "Open") + "\nVertices: " + mesh.Vertices.Count + "\nFaces: " + mesh.Faces.Count);

        private static PanelGeometryInfo SolidMetric(PanelGeometryInfo summary, double? volume, double? area, string units,
            string? shape = null, string? details = null) => new PanelGeometryInfo(
                summary.Description + (shape == null ? "" : " · " + shape), volume.HasValue ? Math.Abs(volume.Value) : (double?)null,
                3, units, "Volume", "V", details: summary.Details + (details == null ? "" : "\n" + details),
                measurementUnavailable: !volume.HasValue || !area.HasValue, hasArea: true, areaValue: area);

        private static PanelGeometryInfo Metric(PanelGeometryInfo summary, double value, int power, string units,
            string name, string label, string? shape = null, string? details = null) => new PanelGeometryInfo(
                summary.Description + (shape == null ? "" : " · " + shape), value, power, units, name, label,
                details: summary.Details + (details == null ? "" : "\n" + details));

        private static PanelGeometryInfo LineInfo(Line line, string units) => new PanelGeometryInfo("Ln", line.Length, units: units,
            details: "Degree: 1\nTopology: Open\nShape: Linear\nPlanarity: Planar");
        private static string ArcDetails(Arc arc) => "Degree: 2\nTopology: " + (arc.IsCircle ? "Closed" : "Open") +
            "\nShape: " + (arc.IsCircle ? "Circle" : "Arc") + "\nPlanarity: Planar";
        private static PanelGeometryInfo CircleInfo(Circle circle, string units) =>
            new PanelGeometryInfo("Cir", circle.Radius, units: units, metricName: "Radius", metricLabel: "R",
                details: "Degree: 2\nTopology: Closed\nShape: Circle\nPlanarity: Planar").WithAdditionalLength(circle.Circumference);
        private static PanelGeometryInfo ArcInfo(Arc arc, string units) => arc.IsCircle
            ? new PanelGeometryInfo("Cir", arc.Radius, units: units, metricName: "Radius", metricLabel: "R", details: ArcDetails(arc))
                .WithAdditionalLength(arc.Length)
            : new PanelGeometryInfo("Arc", arc.Length, units: units, details: ArcDetails(arc));
        private static string Closed(bool closed) => closed ? "closed" : "open";
        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    }
}
