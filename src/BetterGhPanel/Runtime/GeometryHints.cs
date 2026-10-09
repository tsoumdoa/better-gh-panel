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
                    return new PanelGeometryInfo("Polyline · " + Count(polyline.PointCount - 1, "segment") + " · " + Closed(polyline.IsClosed), canMeasure: true);
                case GH_Curve curve when curve.Value != null:
                    return new PanelGeometryInfo("Crv · degree " + curve.Value.Degree + " · " + Closed(curve.Value.IsClosed), canMeasure: true);
                case GH_Surface surface when surface.Value != null:
                    return new PanelGeometryInfo("Srf · " + (surface.Value.IsSurface ? "untrimmed" : "trimmed"), canMeasure: true);
                case GH_Brep brep when brep.Value != null:
                    return new PanelGeometryInfo("Brep · " + Closed(brep.Value.IsSolid) + " · " + Count(brep.Value.Faces.Count, "face"), canMeasure: true);
                case GH_Mesh mesh when mesh.Value != null:
                    return new PanelGeometryInfo("Mesh · " + mesh.Value.Vertices.Count + " vertices · " + Count(mesh.Value.Faces.Count, "face"));
                default: return null;
            }
        }

        // Synchronous, on the Rhino UI thread, only after an explicit Measure click.
        internal static PanelGeometryInfo? Measure(IGH_Goo? goo, string units)
        {
            switch (goo)
            {
                case GH_Curve curve when curve.Value != null:
                    var value = curve.Value;
                    var shape = value.IsLinear() ? "Linear" : value is PolylineCurve ? "Polyline" :
                        value is ArcCurve arc ? (arc.Arc.IsCircle ? "Circle" : "Arc") : "Curve";
                    return Length(shape + " · " + Closed(value.IsClosed), value.GetLength(), units);
                case GH_Surface surface when surface.Value != null: return Area("Srf", surface.Value, units);
                case GH_Brep brep when brep.Value != null:
                    if (!brep.Value.IsSolid) return Area("Brep · open", brep.Value, units);
                    using (var mass = VolumeMassProperties.Compute(brep.Value, true, false, false, false))
                        return mass == null ? null : new PanelGeometryInfo("Brep · closed", Math.Abs(mass.Volume), 3, units, "Volume", "V");
                default: return null;
            }
        }

        private static PanelGeometryInfo? Area(string description, Brep brep, string units)
        {
            // Measure the trimmed Brep, not its underlying untrimmed surface.
            using (var mass = AreaMassProperties.Compute(brep, true, false, false, false))
                return mass == null ? null : new PanelGeometryInfo(description, mass.Area, 2, units, "Area", "A");
        }

        private static PanelGeometryInfo Length(string description, double length, string units) => new PanelGeometryInfo(description, length, units: units);
        private static PanelGeometryInfo ArcInfo(Arc arc, string units) => arc.IsCircle
            ? new PanelGeometryInfo("Cir", arc.Radius, units: units, metricName: "Radius", metricLabel: "R")
            : Length("Arc", arc.Length, units);
        private static string Closed(bool closed) => closed ? "closed" : "open";
        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    }
}
