using System;
using Rhino;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Adapted from the host-tested NURBS value candidate. This copies only
    // curve values, never serializes or duplicates source UserData. The approved
    // host boundary excludes concurrent native mutation/disposal and arbitrary
    // native subclasses; the exact managed type check is not a native sandbox.
    internal static class PanelNurbsSnapshot
    {
        internal static bool TryEstimate(NurbsCurve source, out long bytes)
        {
            bytes = 0;
            if (source.GetType() != typeof(NurbsCurve)) return false;
            int dim = source.Dimension, order = source.Order, count = source.Points.Count, knots = source.Knots.Count;
            return PanelNurbsLimits.Curve(dim, order, count, knots, out bytes);
        }

        internal static NurbsCurve? Create(NurbsCurve source, long reserved)
        {
            NurbsCurve? owned = null;
            try
            {
                if (!TryEstimate(source, out long estimate) || estimate > reserved) return null;
                int dim = source.Dimension, order = source.Order, count = source.Points.Count, knotCount = source.Knots.Count;
                bool rational = source.IsRational;
                var domain = source.Domain;
                if (!Valid(domain.T0) || !Valid(domain.T1) || domain.T0 >= domain.T1) return null;
                var knots = new double[knotCount];
                var points = new Point4d[count];
                for (int i = 0; i < knotCount; i++) if (!Valid(knots[i] = source.Knots[i])) return null;
                for (int i = 0; i < count; i++)
                {
                    // The indexer uses a const source pointer on Rhino 8.36.
                    var cv = source.Points[i];
                    var p = new Point4d(cv.X, cv.Y, cv.Z, cv.Weight);
                    if (!Valid(p.X) || !Valid(p.Y) || !Valid(p.Z) || !Valid(p.W) || p.W <= 0 || (dim == 2 && !Same(p.Z, 0))) return null;
                    points[i] = p;
                }
                owned = new NurbsCurve(dim, rational, order, count);
                for (int i = 0; i < knotCount; i++) owned.Knots[i] = knots[i];
                for (int i = 0; i < count; i++) if (!owned.Points.SetPoint(i, points[i])) return null;
                if (!owned.IsValid || owned.Dimension != dim || owned.Order != order || owned.IsRational != rational ||
                    owned.Points.Count != count || owned.Knots.Count != knotCount ||
                    !Same(owned.Domain.T0, domain.T0) || !Same(owned.Domain.T1, domain.T1)) return null;
                for (int i = 0; i < knotCount; i++) if (!Same(owned.Knots[i], knots[i])) return null;
                for (int i = 0; i < count; i++)
                {
                    var cv = owned.Points[i]; var expected = points[i];
                    if (!Same(cv.X, expected.X) || !Same(cv.Y, expected.Y) || !Same(cv.Z, expected.Z) || !Same(cv.Weight, expected.W)) return null;
                }
                var result = owned; owned = null; return result;
            }
            catch (Exception) { return null; }
            finally { owned?.Dispose(); }
        }

        private static bool Valid(double value) => RhinoMath.IsValidDouble(value);
        private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    }
}
