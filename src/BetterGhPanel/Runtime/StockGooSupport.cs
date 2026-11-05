using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    [Flags]
    internal enum StockTypeFacets { None = 0, Validity = 1, Managed = 2, Capture = 4, Preview = 8, Measure = 16 }

    // Exact SDK types only. Names/presentation do not grant inspection permission.
    internal static class StockGooSupport
    {
        private const StockTypeFacets V = StockTypeFacets.Validity, M = V | StockTypeFacets.Managed;
        private const StockTypeFacets G = V | StockTypeFacets.Capture, P = G | StockTypeFacets.Preview;
        internal sealed class Entry
        {
            internal Entry(Type? type, string name, string label, StockTypeFacets facets, bool separate = false,
                Func<IGH_Goo, GeometryBase?>? read = null, params Type[] inner)
            { Type = type; Name = name; Label = label; Facets = facets; SeparateLabel = separate; Read = read; Inner = inner; }
            internal Type? Type { get; }
            internal string Name { get; }
            internal string Label { get; }
            internal StockTypeFacets Facets { get; }
            internal bool SeparateLabel { get; }
            internal Func<IGH_Goo, GeometryBase?>? Read { get; }
            internal Type[] Inner { get; }
            internal bool Has(StockTypeFacets facet) => (Facets & facet) == facet;
        }
        private static Entry T(Type? type, string name, string label, StockTypeFacets facets) => new Entry(type, name, label, facets);
        private static Entry Native<THolder, TValue>(string name, string label, StockTypeFacets facets, Func<THolder, TValue?> read,
            bool separate = false, params Type[] inner) where THolder : IGH_Goo where TValue : GeometryBase =>
            new Entry(typeof(THolder), name, label, facets, separate, goo => read((THolder)goo), inner.Length == 0 ? new[] { typeof(TValue) } : inner);
        private static readonly Entry[] Entries =
        {
            T(typeof(GH_GeometryGroup), "Group", "Group", StockTypeFacets.None),
            T(typeof(GH_Number), "Number", "Num", M),
            T(typeof(GH_Integer), "Integer", "Int", M),
            T(typeof(GH_String), "Text", "Text", M),
            T(typeof(GH_Boolean), "Boolean", "Bool", M),
            T(typeof(GH_Colour), "Colour", "Col", M),
            T(typeof(GH_Interval), "Domain", "Dom", M),
            T(typeof(GH_Interval2D), "Domain²", "Dom²", M),
            T(typeof(GH_Guid), "Guid", "Guid", M),
            T(typeof(GH_Time), "Time", "Time", M),
            T(typeof(GH_Point), "Point", "Pt", M | StockTypeFacets.Preview),
            T(typeof(GH_Plane), "Plane", "Pln", M),
            T(typeof(GH_Vector), "Vector", "Vec", M),
            T(typeof(GH_Line), "Line", "Ln", M | StockTypeFacets.Preview),
            T(typeof(GH_Arc), "Arc", "Arc", M | StockTypeFacets.Preview),
            T(typeof(GH_Circle), "Circle", "Cir", M | StockTypeFacets.Preview),
            T(typeof(GH_Rectangle), "Rectangle", "Rect", M | StockTypeFacets.Preview),
            T(typeof(GH_Box), "Box", "Box", M | StockTypeFacets.Preview),
            Native<GH_Curve, Curve>("Curve", "Crv", P | StockTypeFacets.Measure, g => g.Value, false, typeof(LineCurve), typeof(ArcCurve), typeof(PolylineCurve), typeof(NurbsCurve), typeof(PolyCurve)),
            Native<GH_Surface, Brep>("Surface", "Srf", P | StockTypeFacets.Measure, g => g.Value),
            Native<GH_Brep, Brep>("Brep", "Brep", P | StockTypeFacets.Measure, g => g.Value),
            Native<GH_Mesh, Mesh>("Mesh", "Mesh", P | StockTypeFacets.Measure, g => g.Value),
            Native<GH_SubD, SubD>("SubD", "SubD", P, g => g.Value),
            Native<GH_Extrusion, Extrusion>("Extrusion", "Extr", P, g => g.Value),
            Native<GH_PointCloud, PointCloud>("Point Cloud", "Cloud", P, g => g.Value, true),
            T(typeof(GH_Transform), "Transform", "Xform", M),
            T(typeof(GH_MeshFace), "Meshface", "Face", M),
            T(typeof(GH_ComplexNumber), "Complex", "Cplx", M),
            Native<GH_TextDot, TextDot>("Text Dot", "Dot", P, g => g.Value, true),
            Native<GH_TextEntity, TextEntity>("Text Entity", "Txt", G, g => g.Value, true),
            Native<GH_Leader, Leader>("Leader", "Ldr", G, g => g.Value, true),
            Native<GH_AngularDimension, AngularDimension>("Angular Dimension", "ADim", G, g => g.Value, true),
            Native<GH_LinearDimension, LinearDimension>("Linear Dimension", "Dim", G, g => g.Value, true),
            Native<GH_OrdinateDimension, OrdinateDimension>("Ordinate Dimension", "ODim", G, g => g.Value, true),
            Native<GH_RadialDimension, RadialDimension>("Radial Dimension", "RDim", G, g => g.Value, true),
            Native<GH_Centermark, Centermark>("Radial Centermark", "Ctr", G, g => g.Value, true),
            Native<GH_Hatch, Hatch>("Hatch", "Hatch", G, g => g.Value, true),
            Native<GH_InstanceReference, InstanceReferenceGeometry>("Block Instance", "Block", G, g => g.Value, true),
            T(typeof(IGH_Goo).Assembly.GetType("Grasshopper.Kernel.Types.GH_Light"), "Light", "Light", StockTypeFacets.None),
            Native<GH_DetailView, DetailView>("Detail View", "Detail", G, g => g.Value, true),
            T(typeof(GH_Culture), "Culture", "Culture", M),
            T(typeof(GH_LonLatCoordinate), "Location", "Loc", M),
            T(typeof(GH_StructurePath), "Path", "Path", M),
            T(typeof(GH_Matrix), "Matrix", "Matrix", M),
            T(typeof(GH_Material), "Material", "Mat", StockTypeFacets.None),
            T(typeof(GH_Field), "Field", "Field", StockTypeFacets.None),
            T(typeof(GH_MeshingParameters), "MeshParameters", "MeshParams", StockTypeFacets.None),
            T(typeof(GH_Predicate), "Predicate", "Predicate", StockTypeFacets.None),
        };
        private static readonly Dictionary<Type, Entry> Types = new Dictionary<Type, Entry>();
        private static readonly Dictionary<string, Entry> Names = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<Type> PreviewGeometry = new HashSet<Type> { typeof(Point), typeof(NurbsSurface), typeof(PlaneSurface), typeof(RevSurface) };
        static StockGooSupport()
        {
            foreach (var entry in Entries)
            {
                if (entry.Type != null) Types.Add(entry.Type, entry); // Only the non-public GH_Light is optional.
                Names.Add(entry.Name, entry);
                if (entry.Has(StockTypeFacets.Preview)) foreach (var inner in entry.Inner) PreviewGeometry.Add(inner);
            }
        }
        internal static Entry? Find(Type type) => Types.TryGetValue(type, out var entry) ? entry : null;
        internal static bool Has(Type type, StockTypeFacets facet) => Find(type)?.Has(facet) == true;
        internal static bool TryGeometry(IGH_Goo goo, out GeometryBase? geometry)
        {
            var entry = Find(goo.GetType());
            geometry = entry?.Read?.Invoke(goo);
            return entry?.Read != null && (geometry == null || Array.IndexOf(entry.Inner, geometry.GetType()) >= 0);
        }
        internal static bool IsPreviewGeometry(Type type) => PreviewGeometry.Contains(type);
        internal static bool SeparateLabel(string name) => Names.TryGetValue(name, out var entry) && entry.SeparateLabel;
        internal static string ShortName(string name) => Names.TryGetValue(name, out var entry) ? entry.Label : name switch
        {
            "Mesh Face" => "Face", "Complex Number" => "Cplx", "Geometry Group" => "Geo group", _ => name
        };
    }
}

namespace BetterGhPanel.UI
{
    internal sealed partial class CanvasMenu
    {
        internal static partial string ShortTypeName(string name) => Runtime.StockGooSupport.ShortName(name);
    }
}
