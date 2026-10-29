using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Reads only exact, verified holders and captured managed copies. No reflected
    // property getters, arbitrary ToString calls, or retained native objects.
    internal static class PanelPropertyCapture
    {
        internal static Dictionary<PanelSearchMode, PanelPropertyValue?> Scalar(IGH_Goo goo, string units)
        {
            var values = new Dictionary<PanelSearchMode, PanelPropertyValue?>();
            void N(PanelSearchMode mode, double value, int power = 0) => values[mode] = PanelPropertyValue.Numeric(value, power, units);
            Type type = goo.GetType();
            if (type == typeof(GH_Vector)) N(PanelSearchMode.Magnitude, ((GH_Vector)goo).Value.Length, 1);
            else if (type == typeof(GH_Colour))
            {
                var colour = ((GH_Colour)goo).Value;
                N(PanelSearchMode.Red, colour.R); N(PanelSearchMode.Green, colour.G); N(PanelSearchMode.Blue, colour.B); N(PanelSearchMode.Alpha, colour.A);
                if (colour.IsNamedColor) values[PanelSearchMode.ColourName] = PanelPropertyValue.String(colour.Name);
            }
            else if (type == typeof(GH_Interval))
            { var d = ((GH_Interval)goo).Value; N(PanelSearchMode.DomainStart, d.T0); N(PanelSearchMode.DomainEnd, d.T1); }
            else if (type == typeof(GH_Interval2D))
            {
                var d = ((GH_Interval2D)goo).Value;
                N(PanelSearchMode.UStart, d.U.T0); N(PanelSearchMode.UEnd, d.U.T1); N(PanelSearchMode.VStart, d.V.T0); N(PanelSearchMode.VEnd, d.V.T1);
            }
            else if (type == typeof(GH_Time))
            {
                var date = ((GH_Time)goo).Value; values[PanelSearchMode.DateTime] = PanelPropertyValue.Time(date);
                N(PanelSearchMode.Year, date.Year); N(PanelSearchMode.Month, date.Month); N(PanelSearchMode.Day, date.Day);
                N(PanelSearchMode.Hour, date.Hour); N(PanelSearchMode.Minute, date.Minute); N(PanelSearchMode.Second, date.Second);
            }
            else if (type == typeof(GH_Guid)) values[PanelSearchMode.GuidValue] = PanelPropertyValue.String(((GH_Guid)goo).Value.ToString("D"));
            else if (type == typeof(GH_Line)) Analytic(values, 1, false, "Linear");
            else if (type == typeof(GH_Arc))
            {
                var arc = ((GH_Arc)goo).Value; Analytic(values, 2, arc.IsCircle, arc.IsCircle ? "Circle" : "Arc");
                N(PanelSearchMode.Radius, arc.Radius, 1); N(PanelSearchMode.Angle, arc.Angle, -1);
            }
            else if (type == typeof(GH_Circle))
            { Analytic(values, 2, true, "Circle"); N(PanelSearchMode.Radius, ((GH_Circle)goo).Value.Radius, 1); }
            else if (type == typeof(GH_Rectangle))
            {
                var rect = ((GH_Rectangle)goo).Value;
                N(PanelSearchMode.Width, Math.Abs(rect.Width), 1); N(PanelSearchMode.Height, Math.Abs(rect.Height), 1);
                N(PanelSearchMode.Perimeter, 2 * (Math.Abs(rect.Width) + Math.Abs(rect.Height)), 1);
            }
            else if (type == typeof(GH_Box))
            {
                var box = ((GH_Box)goo).Value;
                double x = Math.Abs(box.X.Length), y = Math.Abs(box.Y.Length), z = Math.Abs(box.Z.Length);
                N(PanelSearchMode.Width, x, 1); N(PanelSearchMode.Height, y, 1); N(PanelSearchMode.Depth, z, 1);
            }
            else if (type == typeof(GH_Plane))
            {
                var plane = ((GH_Plane)goo).Value;
                N(PanelSearchMode.OriginX, plane.OriginX, 1); N(PanelSearchMode.OriginY, plane.OriginY, 1); N(PanelSearchMode.OriginZ, plane.OriginZ, 1);
                N(PanelSearchMode.AxisXX, plane.XAxis.X); N(PanelSearchMode.AxisXY, plane.XAxis.Y); N(PanelSearchMode.AxisXZ, plane.XAxis.Z);
                N(PanelSearchMode.AxisYX, plane.YAxis.X); N(PanelSearchMode.AxisYY, plane.YAxis.Y); N(PanelSearchMode.AxisYZ, plane.YAxis.Z);
                N(PanelSearchMode.AxisZX, plane.ZAxis.X); N(PanelSearchMode.AxisZY, plane.ZAxis.Y); N(PanelSearchMode.AxisZZ, plane.ZAxis.Z);
            }
            else if (type == typeof(GH_ComplexNumber))
            {
                var complex = ((GH_ComplexNumber)goo).Value;
                N(PanelSearchMode.Real, complex.Real); N(PanelSearchMode.Imaginary, complex.Imaginary);
                // Scaled hypotenuse avoids overflow for finite large components.
                double scale = Math.Max(Math.Abs(complex.Real), Math.Abs(complex.Imaginary));
                N(PanelSearchMode.Magnitude, scale == 0 ? 0 : scale * Math.Sqrt(Math.Pow(complex.Real / scale, 2) + Math.Pow(complex.Imaginary / scale, 2)));
                N(PanelSearchMode.Phase, Math.Atan2(complex.Imaginary, complex.Real), -1);
            }
            else if (type == typeof(GH_Transform))
            {
                var transforms = ((GH_Transform)goo).CompoundTransforms;
                if (transforms.Count == 0 || transforms.Count == 1 && transforms[0]?.GetType() == typeof(Grasshopper.Kernel.Types.Transforms.Generic))
                {
                    var transform = transforms.Count == 0 ? Transform.Identity : ((Grasshopper.Kernel.Types.Transforms.Generic)transforms[0]).Transform;
                    // Determinant enters Rhino's native runtime; matrix fields and
                    // verified validity remain usable before that runtime starts.
                    try { N(PanelSearchMode.Determinant, transform.Determinant); }
                    catch (DllNotFoundException) { N(PanelSearchMode.Determinant, double.NaN); }
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) N(PanelSearchMode.M00 + r * 4 + c, transform[r, c]);
                }
            }
            return values;
        }

        internal static Dictionary<PanelSearchMode, PanelPropertyValue?> Geometry(GeometryBase geometry, bool surface, string units, bool measured)
        {
            var values = new Dictionary<PanelSearchMode, PanelPropertyValue?>();
            void N(PanelSearchMode mode, double value, int power = 0) => values[mode] = PanelPropertyValue.Numeric(value, power, units);
            void B(PanelSearchMode mode, bool value) => values[mode] = PanelPropertyValue.Boolean(value);
            if (geometry is Curve curve)
            {
                N(PanelSearchMode.Degree, curve.Degree); B(PanelSearchMode.Closed, curve.IsClosed);
                if (curve is PolylineCurve polyline) N(PanelSearchMode.SegmentCount, polyline.PointCount - 1);
                if (curve is LineCurve) Analytic(values, 1, false, "Linear");
                else if (curve is ArcCurve arc)
                {
                    Analytic(values, 2, arc.IsClosed, arc.Arc.IsCircle ? "Circle" : "Arc");
                    N(PanelSearchMode.Radius, arc.Radius, 1); N(PanelSearchMode.Angle, arc.Arc.Angle, -1);
                }
                else if (measured)
                {
                    bool linear = curve.IsLinear(); B(PanelSearchMode.Planar, linear || curve.IsPlanar());
                    values[PanelSearchMode.Shape] = PanelPropertyValue.String(linear ? "Linear" : curve is PolylineCurve ? "Polyline" : "Curve");
                }
                else { values[PanelSearchMode.Planar] = null; values[PanelSearchMode.Shape] = null; }
            }
            else if (geometry is Brep brep)
            {
                N(PanelSearchMode.FaceCount, brep.Faces.Count);
                if (!surface)
                { B(PanelSearchMode.Solid, brep.IsSolid); B(PanelSearchMode.Closed, brep.IsSolid); N(PanelSearchMode.EdgeCount, brep.Edges.Count); N(PanelSearchMode.VertexCount, brep.Vertices.Count); }
                if (brep.Faces.Count == 1)
                {
                    if (surface)
                    { B(PanelSearchMode.Trimmed, !brep.IsSurface); N(PanelSearchMode.DegreeU, brep.Faces[0].Degree(0)); N(PanelSearchMode.DegreeV, brep.Faces[0].Degree(1)); }
                    values[PanelSearchMode.Planar] = measured ? PanelPropertyValue.Boolean(brep.Faces[0].IsPlanar()) : null;
                }
            }
            else if (geometry is Mesh mesh)
            { B(PanelSearchMode.Closed, mesh.IsClosed); N(PanelSearchMode.VertexCount, mesh.Vertices.Count); N(PanelSearchMode.FaceCount, mesh.Faces.Count); }
            else if (geometry is SubD subd)
            { B(PanelSearchMode.Solid, subd.IsSolid); B(PanelSearchMode.Closed, subd.IsSolid); N(PanelSearchMode.VertexCount, subd.Vertices.Count); N(PanelSearchMode.EdgeCount, subd.Edges.Count); N(PanelSearchMode.FaceCount, subd.Faces.Count); }
            else if (geometry is Extrusion extrusion)
            { B(PanelSearchMode.Solid, extrusion.IsSolid); B(PanelSearchMode.Closed, extrusion.IsSolid); N(PanelSearchMode.ProfileCount, extrusion.ProfileCount); N(PanelSearchMode.Height, extrusion.PathStart.DistanceTo(extrusion.PathEnd), 1); }
            else if (geometry is PointCloud cloud) N(PanelSearchMode.PointCount, cloud.Count);
            return values;
        }

        private static void Analytic(Dictionary<PanelSearchMode, PanelPropertyValue?> values, int degree, bool closed, string shape)
        {
            values[PanelSearchMode.Degree] = PanelPropertyValue.Numeric(degree);
            values[PanelSearchMode.Closed] = PanelPropertyValue.Boolean(closed);
            values[PanelSearchMode.Planar] = PanelPropertyValue.Boolean(true);
            values[PanelSearchMode.Shape] = PanelPropertyValue.String(shape);
        }
    }
}
