using System;
using System.Globalization;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal static class GeometryHints
    {
        // Only analytic primitive measurements and stored properties belong in the
        // snapshot. General curve length and mass properties are hover-only.
        internal static string? Summary(IGH_Goo goo, string units)
        {
            switch (goo)
            {
                case GH_Line line:
                    return Line(line.Value.Length, units);
                case GH_Arc arc:
                    return Arc(arc.Value, units);
                case GH_Circle circle:
                    return "Cir · R " + Measure(circle.Value.Radius, units);
                case GH_Rectangle rectangle:
                    return "Rect · A " + Measure(rectangle.Value.Area, units, 2);
                case GH_Box box:
                    return "Box · V " + Measure(box.Value.Volume, units, 3);
                case GH_Curve curve when curve.Value is LineCurve lineCurve:
                    return Line(lineCurve.Line.Length, units);
                case GH_Curve curve when curve.Value is ArcCurve arcCurve:
                    return Arc(arcCurve.Arc, units);
                case GH_Curve curve when curve.Value is PolylineCurve polyline:
                    return "Polyline · " + (polyline.PointCount - 1) + " segments · " + Closed(polyline.IsClosed);
                case GH_Curve curve when curve.Value != null:
                    return "Crv · degree " + curve.Value.Degree + " · " + Closed(curve.Value.IsClosed);
                case GH_Surface surface when surface.Value != null:
                    return "Srf · " + (surface.Value.IsSurface ? "untrimmed" : "trimmed");
                case GH_Brep brep when brep.Value != null:
                    return "Brep · " + Closed(brep.Value.IsSolid) + " · " + Count(brep.Value.Faces.Count, "face");
                case GH_Mesh mesh when mesh.Value != null:
                    return "Mesh · " + mesh.Value.Vertices.Count + " vertices · " + Count(mesh.Value.Faces.Count, "face");
                default:
                    return null;
            }
        }

        // Called on the Rhino UI thread only for a hovered row. The session caches
        // both successful results and unavailable measurements until the next solution.
        internal static string? Details(IGH_Goo? goo, string units)
        {
            switch (goo)
            {
                case GH_Curve curve when curve.Value != null:
                    var value = curve.Value;
                    var shape = value.IsLinear() ? "Linear" : value is PolylineCurve ? "Polyline" :
                        value is ArcCurve arc ? (arc.Arc.IsCircle ? "Circle" : "Arc") : "Curve";
                    return shape + " · " + Closed(value.IsClosed) + "\nLength: " + Measure(value.GetLength(), units);
                case GH_Surface surface when surface.Value != null:
                    return Area(surface.Value, units);
                case GH_Brep brep when brep.Value != null:
                    if (!brep.Value.IsSolid) return Area(brep.Value, units);
                    using (var mass = VolumeMassProperties.Compute(brep.Value, true, false, false, false))
                        return mass == null ? null : "Volume: " + Measure(Math.Abs(mass.Volume), units, 3);
                default:
                    return null;
            }
        }

        private static string? Area(Brep brep, string units)
        {
            // Measure the trimmed Brep, not its underlying untrimmed surface.
            using (var mass = AreaMassProperties.Compute(brep, true, false, false, false))
                return mass == null ? null : "Area: " + Measure(mass.Area, units, 2);
        }

        private static string Line(double length, string units) => "Ln · L " + Measure(length, units);
        private static string Arc(Arc arc, string units) => arc.IsCircle
            ? "Cir · R " + Measure(arc.Radius, units)
            : "Arc · L " + Measure(arc.Length, units);
        private static string Closed(bool closed) => closed ? "closed" : "open";
        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
        private static string Measure(double value, string units, int power = 1) =>
            double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? "unavailable" :
                value.ToString("G6", CultureInfo.InvariantCulture) + " " + units + (power == 2 ? "²" : power == 3 ? "³" : "");
    }
}
