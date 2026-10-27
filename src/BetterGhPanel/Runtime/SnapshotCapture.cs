using System;
using System.Collections.Generic;
using System.Globalization;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal static class SnapshotCapture
    {
        public static PanelSnapshot Capture(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            PanelUnitConversion conversion = PanelUnitConversion.None, string geometryUnits = "units") =>
            CaptureWithSources(tree, out sourceItems, conversion, geometryUnits, null);

        internal static PanelSnapshot CaptureWithSources(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            PanelUnitConversion conversion, string geometryUnits, IReadOnlyList<PanelSourceAddress>? sourceAddresses)
            => CaptureCore(tree, out sourceItems, out _, conversion, geometryUnits, sourceAddresses);

        // The returned inner references belong to this exact capture generation.
        // They are never obtained again to decide which object to reconstruct.
        internal static PanelSnapshot CaptureForMeasurements(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            out GeometryBase?[] measurementSources, PanelUnitConversion conversion, string geometryUnits,
            IReadOnlyList<PanelSourceAddress>? sourceAddresses)
            => CaptureCore(tree, out sourceItems, out measurementSources, conversion, geometryUnits, sourceAddresses);

        private static PanelSnapshot CaptureCore(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            out GeometryBase?[] measurementSources, PanelUnitConversion conversion, string geometryUnits,
            IReadOnlyList<PanelSourceAddress>? sourceAddresses)
        {
            var rows = new List<PanelRow>(tree.DataCount);
            var branches = new List<PanelBranch>(tree.PathCount);
            var source = new List<IGH_Goo?>(tree.DataCount);
            var geometrySources = new List<GeometryBase?>(tree.DataCount);
            var budget = new SafeGooInspection.Budget();
            // A summary is immutable for this capture generation and may be shared
            // across references; no ancestor-dependent validity is memoized.
            var groupSummaries = new Dictionary<GH_GeometryGroup, PanelGroupSummary>();
            const long maxRetainedSummaryBytes = 1024 * 1024;
            const int maxCachedGroupSummaries = 16384;
            long retainedSummaryBytes = 0;
            for (var b = 0; b < tree.PathCount; b++)
            {
                var path = tree.Paths[b].ToString();
                var items = tree.Branches[b];
                branches.Add(new PanelBranch(path, items.Count));
                for (var i = 0; i < items.Count; i++)
                {
                    IGH_Goo? item = items[i];
                    string type = item?.GetType().Name ?? "Null", display = "<unavailable value>", reason;
                    string? stringValue = null;
                    double? number = null;
                    bool typeAvailable = false;
                    PanelValidity validity;
                    PanelGeometryInfo? geometry = null;
                    PanelGroupSummary? groupSummary = null;
                    PanelSearchCapabilities capabilities;
                    GeometryBase? capturedGeometry = null;
                    try
                    {
                        var identity = SafeGooInspection.ResolveIdentity(item, budget);
                        type = identity.Name;
                        typeAvailable = identity.Available;
                        if (identity.Group != null)
                        {
                            if (!groupSummaries.TryGetValue(identity.Group, out groupSummary))
                            {
                                string name = identity.Group.Name ?? string.Empty;
                                int count = identity.Group.Objects.Count;
                                long estimatedBytes = ((long)name.Length + 64) * sizeof(char);
                                bool retain = estimatedBytes <= maxRetainedSummaryBytes - retainedSummaryBytes;
                                groupSummary = new PanelGroupSummary(name, count, retain);
                                if (retain) retainedSummaryBytes += estimatedBytes;
                                if (groupSummaries.Count < maxCachedGroupSummaries) groupSummaries.Add(identity.Group, groupSummary);
                            }
                        }
                        validity = SafeGooInspection.Validity(item, budget, out reason, out capturedGeometry);
                        if (identity.Group == null) display = FormatSummary(item, type, out number, out stringValue);

                        if (item != null && validity == PanelValidity.VerifiedValid)
                        {
                            geometry = GeometryHints.DescribeCaptured(item, capturedGeometry, geometryUnits);
                            // Only the bounded exact curve candidates have a value
                            // reconstruction provider. Other deferred shapes stay unavailable.
                            bool supported = item.GetType() == typeof(GH_Curve) && capturedGeometry != null &&
                                (capturedGeometry.GetType() == typeof(PolylineCurve) || capturedGeometry.GetType() == typeof(NurbsCurve));
                            if (geometry?.CanMeasure == true && !supported) geometry = geometry.WithUnavailableMeasurement();
                            display = geometry?.FormatCompact(PanelUnitConversion.None, PanelUnits.AutomaticDecimals) ?? display;
                        }
                        capabilities = Capabilities(item, validity, capturedGeometry);
                    }
                    catch (Exception)
                    {
                        validity = PanelValidity.Unverified;
                        reason = "Value could not be safely inspected.";
                        number = null;
                        stringValue = null;
                        geometry = null;
                        capabilities = PanelSearchCapabilities.None;
                    }
                    finally { GC.KeepAlive(capturedGeometry); }
                    rows.Add(new PanelRow(i, path, type, display, number, validity,
                        typeIdentityAvailable: typeAvailable, searchCapabilities: capabilities, isNull: item == null,
                        geometryInfo: geometry, invalidReason: reason, stringValue: stringValue, groupSummary: groupSummary));
                    source.Add(item);
                    geometrySources.Add(validity == PanelValidity.VerifiedValid && geometry?.CanMeasure == true
                        ? capturedGeometry : null);
                }
            }
            sourceItems = source.ToArray();
            measurementSources = geometrySources.ToArray();
            return new PanelSnapshot(rows, branches, conversion, sourceAddresses, budget.Visits,
                (int)retainedSummaryBytes, groupSummaries.Count);
        }

        private static PanelSearchCapabilities Capabilities(IGH_Goo? item, PanelValidity validity, GeometryBase? geometry)
        {
            if (item == null) return PanelSearchCapabilities.None;
            Type type = item.GetType();
            if (type == typeof(GH_Number) || type == typeof(GH_Integer)) return PanelSearchCapabilities.Value;
            if (validity == PanelValidity.Unverified) return PanelSearchCapabilities.None;
            if (type == typeof(GH_Line) || type == typeof(GH_Arc) || type == typeof(GH_Circle) || type == typeof(GH_Curve))
                return PanelSearchCapabilities.Length;
            if (type == typeof(GH_Rectangle) || type == typeof(GH_Surface)) return PanelSearchCapabilities.Area;
            if (type == typeof(GH_Box)) return PanelSearchCapabilities.Volume;
            if (type == typeof(GH_Brep) && validity == PanelValidity.VerifiedValid && geometry is Brep brep)
                return brep.IsSolid ? PanelSearchCapabilities.Volume : PanelSearchCapabilities.Area;
            return PanelSearchCapabilities.None;
        }

        private static string FormatSummary(IGH_Goo? goo, string type, out double? numeric, out string? stringValue)
        {
            numeric = null;
            stringValue = null;
            if (goo == null) return "<null>";
            Type runtime = goo.GetType();
            if (runtime == typeof(GH_Number))
            {
                double value = ((GH_Number)goo).Value;
                numeric = value;
                return value.ToString("G12", CultureInfo.InvariantCulture);
            }
            if (runtime == typeof(GH_Integer))
            {
                int value = ((GH_Integer)goo).Value;
                numeric = value;
                return value.ToString(CultureInfo.InvariantCulture);
            }
            if (runtime == typeof(GH_String))
            {
                stringValue = ((GH_String)goo).Value;
                return stringValue ?? "<null text>";
            }
            if (runtime == typeof(GH_Boolean)) return ((GH_Boolean)goo).Value ? "True" : "False";
            if (runtime == typeof(GH_Point))
            {
                var value = ((GH_Point)goo).Value;
                return string.Format(CultureInfo.InvariantCulture, "({0:G8}, {1:G8}, {2:G8})", value.X, value.Y, value.Z);
            }
            if (runtime == typeof(GH_Vector))
            {
                var value = ((GH_Vector)goo).Value;
                return string.Format(CultureInfo.InvariantCulture, "<{0:G8}, {1:G8}, {2:G8}>", value.X, value.Y, value.Z);
            }
            if (runtime == typeof(GH_Colour))
            {
                var value = ((GH_Colour)goo).Value;
                return value.IsNamedColor ? value.Name : value.A == byte.MaxValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", value.R, value.G, value.B)
                    : string.Format(CultureInfo.InvariantCulture, "{0},{1},{2} ({3})", value.R, value.G, value.B, value.A);
            }
            if (runtime == typeof(GH_Interval)) return IntervalText(((GH_Interval)goo).Value);
            if (runtime == typeof(GH_Interval2D))
            {
                var value = ((GH_Interval2D)goo).Value;
                return "u:{" + IntervalText(value.U) + "} v:{" + IntervalText(value.V) + "}";
            }
            if (runtime == typeof(GH_Guid)) return ((GH_Guid)goo).Value.ToString("D");
            if (runtime == typeof(GH_Time)) return ((GH_Time)goo).Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return "<" + type + ">";
        }

        private static string IntervalText(Interval value) => string.Format(CultureInfo.InvariantCulture, "{0:G12} To {1:G12}", value.T0, value.T1);
    }
}
