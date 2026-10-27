using System;
using System.Globalization;
using System.IO;
using BetterGhPanel.Core;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime;

namespace BetterGhPanel.Runtime
{
    // Experimental implementation for the host fixture only. Callers must know
    // independently that the source is the built-in native NURBS implementation,
    // owns its pointer, and is stable for the whole call. A managed type check is
    // not such a proof. Production input currently has no way to establish it.
    internal static class NurbsMeasurementCandidate
    {
        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareTrustedCurve(NurbsCurve source,
            string units, int unitSystem, double tolerance)
        {
            if (source == null || source.GetType() != typeof(NurbsCurve) || source.IsDocumentControlled ||
                CommonObject.PerformCorruptionTesting || !ValidContext(units, tolerance)) return null;
            int dimension = source.Dimension, order = source.Order, count = source.Points.Count, knotCount = source.Knots.Count;
            bool rational = source.IsRational;
            if (!NurbsCandidateLimits.Curve(dimension, order, count, knotCount, out int payload)) return null;
            IDisposable? reservation = NurbsCandidateLimits.TryReserve(payload);
            if (reservation == null) return null;
            NurbsCurve? owned = null;
            try
            {
                var domain = source.Domain;
                if (!ValidDomain(domain)) return null;
                var knots = new double[knotCount];
                var points = new Point4d[count];
                for (int i = 0; i < knots.Length; i++)
                    if (!ValidNumber(knots[i] = source.Knots[i])) return null;
                for (int i = 0; i < points.Length; i++)
                {
                    // The Point4d GetPoint overload uses NonConstPointer on the
                    // source; the indexer uses ConstPointer in Rhino 8.36.
                    var cv = source.Points[i];
                    points[i] = new Point4d(cv.X, cv.Y, cv.Z, cv.Weight);
                    if (!ValidPoint(points[i]) || (dimension == 2 && !Same(points[i].Z, 0.0))) return null;
                }
                owned = new NurbsCurve(dimension, rational, order, count);
                for (int i = 0; i < knots.Length; i++) owned.Knots[i] = knots[i];
                for (int i = 0; i < points.Length; i++) if (!owned.Points.SetPoint(i, points[i])) return null;
                if (!owned.IsValid || owned.Dimension != dimension || owned.Order != order || owned.IsRational != rational ||
                    owned.Points.Count != count || owned.Knots.Count != knotCount || !SameDomain(owned.Domain, domain)) return null;

                // Serialize only values read back from the exact worker copy, and
                // compare every value against the source capture before accepting it.
                var bytes = new byte[payload];
                using (var writer = new BinaryWriter(new MemoryStream(bytes, true)))
                {
                    writer.Write(dimension); writer.Write(rational); writer.Write(order); writer.Write(count); writer.Write(knotCount);
                    WriteDomain(writer, owned.Domain);
                    for (int i = 0; i < knots.Length; i++)
                    {
                        double value = owned.Knots[i];
                        if (!Same(value, knots[i])) return null;
                        writer.Write(value);
                    }
                    for (int i = 0; i < points.Length; i++)
                    {
                        var point = owned.Points[i];
                        var value = new Point4d(point.X, point.Y, point.Z, point.Weight);
                        if (!SamePoint(value, points[i])) return null;
                        WritePoint(writer, value);
                    }
                }
                var witness = new MeasurementWitness(Context("curve/length", units, unitSystem, tolerance), bytes);
                var worker = owned;
                var resource = new OwnedResource(worker, reservation);
                var prepared = new PreparedMeasurement<PanelGeometryInfo>(() => GeometryHints.Measure(worker, false, units), resource, witness);
                owned = null; reservation = null;
                return prepared;
            }
            finally { owned?.Dispose(); reservation?.Dispose(); }
        }

        internal static PreparedMeasurement<PanelGeometryInfo>? PrepareTrustedSurface(NurbsSurface source,
            string units, int unitSystem, double tolerance)
        {
            if (source == null || source.GetType() != typeof(NurbsSurface) || source.IsDocumentControlled ||
                CommonObject.PerformCorruptionTesting || !ValidContext(units, tolerance)) return null;
            bool rational = source.IsRational;
            // PointSize uses NonConstPointer: it is acceptable only under this
            // method's independently established, owned-pointer precondition.
            int dimension = source.Points.PointSize - (rational ? 1 : 0), orderU = source.OrderU, orderV = source.OrderV;
            int countU = source.Points.CountU, countV = source.Points.CountV;
            int knotCountU = source.KnotsU.Count, knotCountV = source.KnotsV.Count;
            if (!NurbsCandidateLimits.Surface(dimension, orderU, orderV, countU, countV, knotCountU, knotCountV, out int payload)) return null;
            IDisposable? reservation = NurbsCandidateLimits.TryReserve(payload);
            if (reservation == null) return null;
            NurbsSurface? owned = null;
            try
            {
                var domainU = source.Domain(0); var domainV = source.Domain(1);
                if (!ValidDomain(domainU) || !ValidDomain(domainV)) return null;
                var knotsU = new double[knotCountU]; var knotsV = new double[knotCountV];
                var points = new Point4d[checked(countU * countV)];
                for (int i = 0; i < knotCountU; i++) if (!ValidNumber(knotsU[i] = source.KnotsU[i])) return null;
                for (int i = 0; i < knotCountV; i++) if (!ValidNumber(knotsV[i] = source.KnotsV[i])) return null;
                for (int u = 0; u < countU; u++) for (int v = 0; v < countV; v++)
                {
                    if (!source.Points.GetPoint(u, v, out Point4d point) || !ValidPoint(point)) return null;
                    points[u * countV + v] = point;
                }
                owned = NurbsSurface.Create(dimension, rational, orderU, orderV, countU, countV);
                if (owned == null) return null;
                for (int i = 0; i < knotCountU; i++) owned.KnotsU[i] = knotsU[i];
                for (int i = 0; i < knotCountV; i++) owned.KnotsV[i] = knotsV[i];
                for (int u = 0; u < countU; u++) for (int v = 0; v < countV; v++)
                    if (!owned.Points.SetPoint(u, v, points[u * countV + v])) return null;
                if (!owned.IsValid || owned.Points.PointSize - (rational ? 1 : 0) != dimension || owned.IsRational != rational ||
                    owned.OrderU != orderU || owned.OrderV != orderV || owned.Points.CountU != countU || owned.Points.CountV != countV ||
                    owned.KnotsU.Count != knotCountU || owned.KnotsV.Count != knotCountV ||
                    !SameDomain(owned.Domain(0), domainU) || !SameDomain(owned.Domain(1), domainV)) return null;
                var bytes = new byte[payload];
                using (var writer = new BinaryWriter(new MemoryStream(bytes, true)))
                {
                    writer.Write(dimension); writer.Write(rational); writer.Write(orderU); writer.Write(orderV);
                    writer.Write(countU); writer.Write(countV); writer.Write(knotCountU); writer.Write(knotCountV);
                    WriteDomain(writer, owned.Domain(0)); WriteDomain(writer, owned.Domain(1));
                    for (int i = 0; i < knotCountU; i++)
                    { double value = owned.KnotsU[i]; if (!Same(value, knotsU[i])) return null; writer.Write(value); }
                    for (int i = 0; i < knotCountV; i++)
                    { double value = owned.KnotsV[i]; if (!Same(value, knotsV[i])) return null; writer.Write(value); }
                    for (int u = 0; u < countU; u++) for (int v = 0; v < countV; v++)
                    {
                        if (!owned.Points.GetPoint(u, v, out Point4d value) || !SamePoint(value, points[u * countV + v])) return null;
                        WritePoint(writer, value);
                    }
                }
                var witness = new MeasurementWitness(Context("surface/area", units, unitSystem, tolerance), bytes);
                var worker = owned;
                var resource = new OwnedResource(worker, reservation);
                var prepared = new PreparedMeasurement<PanelGeometryInfo>(() => MeasureSurface(worker, units), resource, witness);
                owned = null; reservation = null;
                return prepared;
            }
            finally { owned?.Dispose(); reservation?.Dispose(); }
        }

        private static PanelGeometryInfo? MeasureSurface(NurbsSurface surface, string units)
        {
            bool planar = surface.IsPlanar();
            using (var mass = AreaMassProperties.Compute(surface, true, false, false, false))
                return mass == null ? null : new PanelGeometryInfo("Srf · untrimmed · " + (planar ? "planar" : "nonplanar"),
                    mass.Area, 2, units, "Area", "A", details: "Trimming: Untrimmed\nDegree (U/V): " +
                    (surface.OrderU - 1) + "/" + (surface.OrderV - 1) + "\nPlanarity: " + (planar ? "Planar" : "Nonplanar"));
        }

        private static bool ValidContext(string units, double tolerance) => units != null && units.Length <= 128 && ValidNumber(tolerance);
        private static bool ValidNumber(double value) => RhinoMath.IsValidDouble(value);
        private static bool ValidDomain(Interval domain) => ValidNumber(domain.T0) && ValidNumber(domain.T1) && domain.T0 < domain.T1;
        private static bool ValidPoint(Point4d point) => ValidNumber(point.X) && ValidNumber(point.Y) && ValidNumber(point.Z) &&
            ValidNumber(point.W) && point.W > 0;
        private static bool Same(double first, double second) => BitConverter.DoubleToInt64Bits(first) == BitConverter.DoubleToInt64Bits(second);
        private static bool SameDomain(Interval first, Interval second) => Same(first.T0, second.T0) && Same(first.T1, second.T1);
        private static bool SamePoint(Point4d first, Point4d second) => Same(first.X, second.X) && Same(first.Y, second.Y) &&
            Same(first.Z, second.Z) && Same(first.W, second.W);
        private static void WriteDomain(BinaryWriter writer, Interval domain) { writer.Write(domain.T0); writer.Write(domain.T1); }
        private static void WritePoint(BinaryWriter writer, Point4d point) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); writer.Write(point.W); }
        private static string Context(string role, string units, int unitSystem, double tolerance) => "candidate/nurbs/" + role + "|" +
            unitSystem.ToString(CultureInfo.InvariantCulture) + "|" + BitConverter.DoubleToInt64Bits(tolerance).ToString(CultureInfo.InvariantCulture) +
            "|" + units.Length.ToString(CultureInfo.InvariantCulture) + ":" + units;

        private sealed class OwnedResource : IDisposable
        {
            private readonly IDisposable _geometry, _reservation;
            internal OwnedResource(IDisposable geometry, IDisposable reservation) { _geometry = geometry; _reservation = reservation; }
            public void Dispose() { try { _geometry.Dispose(); } finally { _reservation.Dispose(); } }
        }
    }
}
