using System;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal static class GeometryHints
    {
        // Only analytic measurements and stored properties belong in the snapshot.
        internal static PanelGeometryInfo? Describe(IGH_Goo goo, string units)
        {
            switch (goo)
            {
                case GH_Line line: return Length("Ln", line.Value.Length, units);
                case GH_Arc arc: return ArcInfo(arc.Value, units);
                case GH_Circle circle: return new PanelGeometryInfo("Cir", circle.Value.Radius, units: units, metricName: "Radius", metricLabel: "R");
                case GH_Rectangle rectangle: return new PanelGeometryInfo("Rect", rectangle.Value.Area, 2, units, "Area", "A");
                case GH_Box box: return new PanelGeometryInfo("Box", box.Value.Volume, 3, units, "Volume", "V");
                case GH_Curve curve when curve.Value is LineCurve line: return Length("Ln", line.Line.Length, units);
                case GH_Curve curve when curve.Value is ArcCurve arc: return ArcInfo(arc.Arc, units);
                case GH_Curve curve when curve.Value is PolylineCurve polyline:
                    return CurveInfo(polyline);
                case GH_Curve curve when curve.Value != null:
                    return CurveInfo(curve.Value);
                case GH_Surface surface when surface.Value != null:
                    return SurfaceInfo(surface.Value);
                case GH_Brep brep when brep.Value != null:
                    return BrepInfo(brep.Value);
                case GH_Mesh mesh when mesh.Value != null:
                    return new PanelGeometryInfo("Mesh · " + mesh.Value.Vertices.Count + " vertices · " + Count(mesh.Value.Faces.Count, "face"));
                default: return null;
            }
        }

        // Capture an owned deep copy on the UI thread, outside painting. The returned
        // operation runs once on a worker and releases its copy even on failure.
        internal static Func<PanelGeometryInfo?>? PrepareMeasurement(IGH_Goo? goo, string units)
        {
            GeometryBase? geometry;
            bool surface = false;
            switch (goo)
            {
                case GH_Curve curve when curve.Value != null:
                    geometry = curve.Value.DuplicateCurve();
                    break;
                case GH_Surface value when value.Value != null:
                    geometry = value.Value.DuplicateBrep();
                    surface = true;
                    break;
                case GH_Brep brep when brep.Value != null:
                    geometry = brep.Value.DuplicateBrep();
                    break;
                default: return null;
            }
            if (geometry == null) return null;
            return () =>
            {
                using (geometry) return Measure(geometry, surface, units);
            };
        }

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
