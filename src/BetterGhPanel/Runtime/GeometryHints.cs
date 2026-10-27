using System;
using System.Globalization;
using System.IO;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal static class GeometryHints
    {
        // Enabled only after the isolated target-host UserData fixture proves this
        // value reconstruction path and capture explicitly admits its exact type.
        internal static readonly bool PolylineSnapshotVerified = false;

        // Only analytic measurements and stored properties belong in the snapshot.
        internal static PanelGeometryInfo? Describe(IGH_Goo goo, string units)
        {
            if (SafeGooInspection.IsGeometryHolder(goo.GetType()))
            {
                if (!SafeGooInspection.TryGetTrustedGeometry(goo, out GeometryBase? geometry) || geometry == null || !geometry.IsValid) return null;
                return DescribeCaptured(goo, geometry, units);
            }
            return DescribeCaptured(goo, null, units);
        }

        // The caller validates this exact inner object before this method. Never read
        // a mutable holder again after checking it, and never dispatch through a subclass.
        internal static PanelGeometryInfo? DescribeCaptured(IGH_Goo goo, GeometryBase? geometry, string units)
        {
            Type type = goo.GetType();
            if (type == typeof(GH_Line)) return Length("Ln", ((GH_Line)goo).Value.Length, units);
            if (type == typeof(GH_Arc)) return ArcInfo(((GH_Arc)goo).Value, units);
            if (type == typeof(GH_Circle)) return new PanelGeometryInfo("Cir", ((GH_Circle)goo).Value.Radius, units: units, metricName: "Radius", metricLabel: "R");
            if (type == typeof(GH_Rectangle)) return new PanelGeometryInfo("Rect", ((GH_Rectangle)goo).Value.Area, 2, units, "Area", "A");
            if (type == typeof(GH_Box)) return new PanelGeometryInfo("Box", ((GH_Box)goo).Value.Volume, 3, units, "Volume", "V");
            if (type == typeof(GH_Curve) && geometry is LineCurve line) return Length("Ln", line.Line.Length, units);
            if (type == typeof(GH_Curve) && geometry is ArcCurve arc) return ArcInfo(arc.Arc, units);
            if (type == typeof(GH_Curve) && geometry is Curve curve) return CurveInfo(curve);
            if (type == typeof(GH_Surface) && geometry is Brep surface) return SurfaceInfo(surface);
            if (type == typeof(GH_Brep) && geometry is Brep brep) return BrepInfo(brep);
            if (type == typeof(GH_Mesh) && geometry is Mesh mesh)
                return new PanelGeometryInfo("Mesh · " + mesh.Vertices.Count + " vertices · " + Count(mesh.Faces.Count, "face"));
            return null;
        }

        // Reconstruct only bounded, exact polylines from value fields. This never
        // duplicates or serializes the source (which may carry hostile UserData).
        // Other curves and Breps require a separately proven bounded snapshot path.
        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareMeasurement(IGH_Goo? goo, string units,
            int unitSystem, double tolerance)
        {
            const int maxPoints = 2048;
            if (goo == null || goo.GetType() != typeof(GH_Curve) || units == null || units.Length > 128) return null;
            var source = ((GH_Curve)goo).Value;
            if (source == null || source.GetType() != typeof(PolylineCurve)) return null;
            var polyline = (PolylineCurve)source;
            int count = polyline.PointCount;
            // The point and parameter arrays, worker curve and witness are all
            // bounded before allocating or entering any copy/serialization API.
            if (count < 2 || count > maxPoints || polyline.Dimension != 3) return null;
            var points = new Point3d[count];
            var parameters = new double[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = polyline.Point(i);
                parameters[i] = polyline.Parameter(i);
                if (!points[i].IsValid || double.IsNaN(parameters[i]) || double.IsInfinity(parameters[i])) return null;
            }
            PolylineCurve? owned = null;
            try
            {
                owned = new PolylineCurve(points);
                if (owned.Dimension != 3 || owned.PointCount != count) return null;
                var bytes = new byte[sizeof(int) + count * sizeof(double) * 4];
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
                var prepared = new PreparedMeasurement<PanelGeometryInfo>(() => Measure(workerCopy, false, units), workerCopy, witness);
                owned = null;
                return prepared;
            }
            finally { owned?.Dispose(); }
        }

        private static bool Same(double first, double second) =>
            BitConverter.DoubleToInt64Bits(first) == BitConverter.DoubleToInt64Bits(second);

        private static PanelGeometryInfo? Measure(GeometryBase geometry, bool surface, string units)
        {
            switch (geometry)
            {
                case Curve value:
                    var curve = CurveInfo(value);
                    bool linear = value.IsLinear();
                    string shape = linear ? "linear" : value.IsPlanar() ? "planar" : "nonplanar";
                    string shapeDetails = value is ArcCurve arc ? (arc.Arc.IsCircle ? "Circle" : "Arc") :
                        linear ? "Linear" : value is PolylineCurve ? "Polyline" : "Curve";
                    return Metric(curve, value.GetLength(), 1, units, "Length", "L", shape,
                        "Shape: " + shapeDetails + "\nPlanarity: " + (linear || shape == "planar" ? "Planar" : "Nonplanar"));
                case Brep brep:
                    var summary = surface ? SurfaceInfo(brep) : BrepInfo(brep);
                    string? surfaceShape = brep.Faces.Count == 1 ? (brep.Faces[0].IsPlanar() ? "planar" : "nonplanar") : null;
                    string? surfaceDetails = surfaceShape == null ? null : "Planarity: " + (surfaceShape == "planar" ? "Planar" : "Nonplanar");
                    if (surface || !brep.IsSolid) return Area(summary, brep, units, surfaceShape, surfaceDetails);
                    using (var mass = VolumeMassProperties.Compute(brep, true, false, false, false))
                        return mass == null ? null : Metric(summary, Math.Abs(mass.Volume), 3, units, "Volume", "V", surfaceShape, surfaceDetails);
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
                details: "Topology: " + (solid ? "Closed solid" : "Open") + "\nFaces: " + brep.Faces.Count +
                    "\nEdges: " + brep.Edges.Count + "\nVertices: " + brep.Vertices.Count);
        }

        private static PanelGeometryInfo Metric(PanelGeometryInfo summary, double value, int power, string units,
            string name, string label, string? shape = null, string? details = null) => new PanelGeometryInfo(
                summary.Description + (shape == null ? "" : " · " + shape), value, power, units, name, label,
                details: summary.Details + (details == null ? "" : "\n" + details));

        private static PanelGeometryInfo Length(string description, double length, string units) => new PanelGeometryInfo(description, length, units: units);
        private static PanelGeometryInfo ArcInfo(Arc arc, string units) => arc.IsCircle
            ? new PanelGeometryInfo("Cir", arc.Radius, units: units, metricName: "Radius", metricLabel: "R")
            : Length("Arc", arc.Length, units);
        private static string Closed(bool closed) => closed ? "closed" : "open";
        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    }
}
