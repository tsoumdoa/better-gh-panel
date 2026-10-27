using System;
using System.Collections.Generic;
using System.Globalization;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal sealed class CapturedMeasurementSource
    {
        internal CapturedMeasurementSource(CapturedGeometryLeaf leaf) { Leaf = leaf; }
        internal CapturedGeometryLeaf Leaf { get; }
        internal GeometryBase? Geometry => Leaf.Geometry;
        internal MeasurementReferenceState Reference => Leaf.Reference;
    }

    internal sealed class CapturedInputSeed
    {
        internal CapturedInputSeed(bool[] incomplete, Dictionary<int, CaptureGraphWitness> graphs,
            Dictionary<int, CapturedGeometryLeaf> geometry, CapturedManagedValue[]? managed)
        { Incomplete = incomplete; Graphs = graphs; Geometry = geometry; Managed = managed; }
        internal bool[] Incomplete { get; }
        internal Dictionary<int, CaptureGraphWitness> Graphs { get; }
        internal Dictionary<int, CapturedGeometryLeaf> Geometry { get; }
        internal CapturedManagedValue[]? Managed { get; }
    }

    internal static class SnapshotCapture
    {
        public static PanelSnapshot Capture(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            PanelUnitConversion conversion = PanelUnitConversion.None, string geometryUnits = "units") =>
            CaptureWithSources(tree, out sourceItems, conversion, geometryUnits, null);

        internal static PanelSnapshot CaptureWithSources(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            PanelUnitConversion conversion, string geometryUnits, IReadOnlyList<PanelSourceAddress>? sourceAddresses)
            => CaptureCore(tree, out sourceItems, out _, out _, conversion, geometryUnits, sourceAddresses, null, null, null, null);

        // The returned inner references belong to this exact capture generation.
        // They are never obtained again to decide which object to reconstruct.
        internal static PanelSnapshot CaptureForMeasurements(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            out CapturedMeasurementSource?[] measurementSources, out CaptureGraphWitness[] graphWitnesses,
            PanelUnitConversion conversion, string geometryUnits, IReadOnlyList<PanelSourceAddress>? sourceAddresses,
            Action<int, IGH_Goo?>? inspectionObserver, Action<object>? graphVisitObserver, CapturedScalar[]? scalars, CapturedInputSeed? seed)
            => CaptureCore(tree, out sourceItems, out measurementSources, out graphWitnesses, conversion, geometryUnits, sourceAddresses, inspectionObserver, graphVisitObserver, scalars, seed);

        private static PanelSnapshot CaptureCore(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] sourceItems,
            out CapturedMeasurementSource?[] measurementSources, out CaptureGraphWitness[] graphWitnesses,
            PanelUnitConversion conversion, string geometryUnits, IReadOnlyList<PanelSourceAddress>? sourceAddresses,
            Action<int, IGH_Goo?>? inspectionObserver, Action<object>? graphVisitObserver, CapturedScalar[]? scalars, CapturedInputSeed? seed)
        {
            scalars ??= CapturedScalar.Capture(tree);
            if (scalars.Length != tree.DataCount) throw new ArgumentException("Captured scalar count does not match the tree.");
            seed ??= PrepareInputs(tree);
            if (seed.Incomplete.Length != tree.DataCount) throw new ArgumentException("Captured input count does not match the tree.");
            // Document lookups can reenter. Resolve only after every tree row and
            // bounded group graph has been captured from managed fields.
            if (seed.Managed != null)
                for (int i = 0; i < seed.Managed.Length; i++) seed.Managed[i] = CaptureGraphWitness.ResolveManagedContext(seed.Managed[i]);
            foreach (var graph in seed.Graphs.Values) graph.ResolveManagedContexts();
            var rows = new List<PanelRow>(tree.DataCount);
            var branches = new List<PanelBranch>(tree.PathCount);
            var source = new List<IGH_Goo?>(tree.DataCount);
            var geometrySources = new List<CapturedMeasurementSource?>(tree.DataCount);
            var graphs = new List<(int position, CaptureGraphWitness witness)>();
            var budget = new SafeGooInspection.Budget();
            // A summary is immutable for this capture generation and may be shared
            // across references; no ancestor-dependent validity is memoized.
            var groupSummaries = new Dictionary<GH_GeometryGroup, (string name, int count, PanelGroupSummary summary)>();
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
                    string type = item?.GetType().Name ?? "Null", display = item == null ? "<null>" : "<unavailable value>", reason = "";
                    string? stringValue = null;
                    double? number = null;
                    bool typeAvailable = false;
                    PanelValidity validity;
                    PanelGeometryInfo? geometry = null;
                    PanelGroupSummary? groupSummary = null;
                    PanelSearchCapabilities capabilities;
                    GeometryBase? capturedGeometry = null;
                    MeasurementReferenceState reference = default;
                    bool hasReference = false;
                    CapturedGeometryLeaf? directLeaf = null;
                    int position = rows.Count;
                    seed.Graphs.TryGetValue(position, out CaptureGraphWitness? graph);
                    bool graphComplete = !seed.Incomplete[position];
                    try
                    {
                        if (seed.Geometry.TryGetValue(position, out var rawLeaf))
                        {
                            directLeaf = rawLeaf;
                            var leaf = SafeGooInspection.ResolveCapturedGeometryContext(rawLeaf);
                            directLeaf = leaf; hasReference = leaf.HasReference; reference = leaf.Reference;
                        }
                        var identity = SafeGooInspection.ResolveIdentity(item, graph);
                        type = identity.Name;
                        typeAvailable = identity.Available;
                        if (identity.Group != null && graph != null)
                        {
                            if (graph == null || !graph.TryGroupSummary(identity.Group, out string name, out int count))
                                throw new InvalidOperationException("Captured group summary unavailable.");
                            if (groupSummaries.TryGetValue(identity.Group, out var cached) && cached.name == name && cached.count == count)
                                groupSummary = cached.summary;
                            else
                            {
                                long estimatedBytes = ((long)name.Length + 64) * sizeof(char);
                                bool retain = estimatedBytes <= maxRetainedSummaryBytes - retainedSummaryBytes;
                                groupSummary = new PanelGroupSummary(name, count, retain);
                                if (retain) retainedSummaryBytes += estimatedBytes;
                                if (groupSummaries.ContainsKey(identity.Group) || groupSummaries.Count < maxCachedGroupSummaries)
                                    groupSummaries[identity.Group] = (name, count, groupSummary);
                            }
                        }
                        var scalar = scalars[position];
                        var managed = seed.Managed != null ? seed.Managed[position] : default;
                        IGH_Goo? inspected = item == null ? null : managed.Available ? managed.Copy : graph?.ManagedCopy(item) ?? item;
                        if (scalar.Available)
                        {
                            validity = scalar.Validity;
                            reason = scalar.IsValid ? "" : "The value is invalid.";
                            display = scalar.Format(out number, out stringValue);
                        }
                        else if (managed.Available)
                        {
                            validity = managed.Validity;
                            if (validity == PanelValidity.VerifiedInvalid) reason = "The value is invalid.";
                            else if (validity == PanelValidity.Unverified) reason = "Validity could not be safely verified.";
                        }
                        else validity = graphComplete ? SafeGooInspection.Validity(item, budget, out reason, out capturedGeometry, graph, graphVisitObserver, directLeaf) : PanelValidity.Unverified;
                        if (!graphComplete) reason = "Validity could not be safely verified (capture graph limit).";
                        if (!scalar.Available && inspected != null && identity.Group == null && (graphComplete || graph != null))
                            display = FormatSummary(inspected, type, out number, out stringValue);

                        if (inspected != null && validity == PanelValidity.VerifiedValid)
                        {
                            geometry = GeometryHints.DescribeCaptured(inspected!, capturedGeometry, geometryUnits);
                            // Only a standalone exact holder and its captured inner object
                            // can enter either reconstruction or the compatibility fallback.
                            bool supported = capturedGeometry != null && hasReference && directLeaf.HasValue && directLeaf.Value.Trusted;
                            if (geometry?.CanMeasure == true && !supported) geometry = geometry.WithUnavailableMeasurement();
                            display = geometry?.FormatCompact(PanelUnitConversion.None, PanelUnits.AutomaticDecimals) ?? display;
                        }
                        capabilities = Capabilities(inspected, validity, capturedGeometry);
                        inspectionObserver?.Invoke(rows.Count, item);
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
                    geometrySources.Add(directLeaf.HasValue ? new CapturedMeasurementSource(directLeaf.Value) : null);
                    if (graph != null) graphs.Add((rows.Count - 1, graph));
                }
            }
            foreach (var graph in graphs)
            {
                if (graph.witness.IsCurrent()) continue;
                var row = rows[graph.position];
                rows[graph.position] = new PanelRow(row.OriginalIndex, row.Path, row.TypeName, "<unavailable value>", null,
                    PanelValidity.Unverified, typeIdentityAvailable: false, searchCapabilities: PanelSearchCapabilities.None,
                    isNull: row.IsNull, invalidReason: "Capture graph changed or exceeded its inspection limit.");
                geometrySources[graph.position] = null;
            }
            sourceItems = source.ToArray();
            if (seed.Managed != null)
            {
                for (int i = 0; i < seed.Managed.Length; i++)
                {
                    if (CaptureGraphWitness.ManagedValueCurrent(sourceItems[i], seed.Managed[i])) continue;
                    var row = rows[i];
                    rows[i] = new PanelRow(row.OriginalIndex, row.Path, row.TypeName, "<unavailable value>", null,
                        PanelValidity.Unverified, typeIdentityAvailable: false, searchCapabilities: PanelSearchCapabilities.None,
                        isNull: row.IsNull, invalidReason: "Captured managed value changed.");
                }
            }
            measurementSources = geometrySources.ToArray();
            graphWitnesses = new CaptureGraphWitness[graphs.Count + (seed.Managed == null ? 0 : 1)];
            for (int i = 0; i < graphs.Count; i++) graphWitnesses[i] = graphs[i].witness;
            if (seed.Managed != null) graphWitnesses[graphs.Count] = CaptureGraphWitness.ForManagedValues(sourceItems, seed.Managed);
            return new PanelSnapshot(rows, branches, conversion, sourceAddresses, budget.Visits,
                (int)retainedSummaryBytes, groupSummaries.Count);
        }

        internal static CapturedInputSeed PrepareInputs(GH_Structure<IGH_Goo> tree)
        {
            var incomplete = new bool[tree.DataCount];
            var graphs = new Dictionary<int, CaptureGraphWitness>();
            var geometry = new Dictionary<int, CapturedGeometryLeaf>();
            CapturedManagedValue[]? managed = null;
            int remaining = SafeGooInspection.MaxNodesPerCapture, position = 0;
            for (int b = 0; b < tree.PathCount; b++)
                foreach (var item in tree.Branches[b])
                {
                    try
                    {
                        if (CaptureGraphWitness.TryCopyStandaloneValue(item, out var value))
                        {
                            managed ??= new CapturedManagedValue[tree.DataCount];
                            managed[position] = value;
                        }
                        else if (item != null && CaptureGraphWitness.IsManagedValueType(item.GetType()) &&
                            item.GetType() != typeof(GH_Number) && item.GetType() != typeof(GH_Integer) &&
                            item.GetType() != typeof(GH_String) && item.GetType() != typeof(GH_Boolean)) incomplete[position] = true;
                        else
                        {
                            var shallow = CaptureGraphWitness.CaptureShallowIdentity(item);
                            if (!CaptureGraphWitness.TryCapture(item, ref remaining, out var graph))
                            { incomplete[position] = true; graph = shallow; }
                            if (graph != null) graphs.Add(position, graph);
                        }
                    }
                    catch (Exception) { incomplete[position] = true; }
                    if (item != null)
                    {
                        try { if (SafeGooInspection.TryCaptureStandaloneGeometryRaw(item, out var leaf)) geometry.Add(position, leaf); }
                        catch (Exception) { incomplete[position] = true; }
                    }
                    position++;
                }
            return new CapturedInputSeed(incomplete, graphs, geometry, managed);
        }

        internal static bool GraphSourcesCurrent(CaptureGraphWitness[] witnesses)
            => GraphSourcesCurrentWithVisits(witnesses, out _);

        internal static bool GraphSourcesCurrentWithVisits(CaptureGraphWitness[] witnesses, out long visits)
        {
            visits = 0;
            foreach (var witness in witnesses)
            {
                bool current = witness.IsCurrent(out int examined);
                visits += examined;
                if (!current) return false;
            }
            return true;
        }

        // PanelRow already owns immutable scalar values; checking them directly
        // keeps large numeric/text captures linear without duplicate string copies.
        internal static bool ManagedRowsCurrent(PanelSnapshot snapshot, IGH_Goo?[] holders)
            => ManagedRowsCurrentWithVisits(snapshot, holders, out _);

        internal static bool ManagedRowsCurrentWithVisits(PanelSnapshot snapshot, IGH_Goo?[] holders, out int visits)
        {
            visits = 0;
            if (snapshot.Rows.Count != holders.Length) return false;
            for (int i = 0; i < holders.Length; i++)
            {
                visits++;
                var holder = holders[i]; var row = snapshot.Rows[i];
                if (holder == null) continue;
                Type type = holder.GetType();
                if (type == typeof(GH_Number) && row.NumericValue.HasValue &&
                    BitConverter.DoubleToInt64Bits(((GH_Number)holder).Value) != BitConverter.DoubleToInt64Bits(row.NumericValue.Value)) return false;
                if (type == typeof(GH_Integer) && row.NumericValue.HasValue && ((GH_Integer)holder).Value != row.NumericValue.Value) return false;
                if (type == typeof(GH_String) && !string.Equals(((GH_String)holder).Value, row.StringValue, StringComparison.Ordinal)) return false;
                if (type == typeof(GH_Boolean) && row.DisplayValue != (((GH_Boolean)holder).Value ? "True" : "False")) return false;
            }
            return true;
        }

        internal static bool MeasurementSourcesCurrent(IGH_Goo?[] holders, CapturedMeasurementSource?[] sources)
            => MeasurementSourcesCurrentWithVisits(holders, sources, out _);

        internal static bool MeasurementSourcesCurrentWithVisits(IGH_Goo?[] holders, CapturedMeasurementSource?[] sources, out int visits)
        {
            visits = 0;
            if (holders.Length != sources.Length) return false;
            for (int i = 0; i < sources.Length; i++)
            {
                visits++;
                var source = sources[i];
                if (source != null && !SafeGooInspection.IsCurrentCapturedGeometry(holders[i], source.Leaf))
                    return false;
            }
            return true;
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
