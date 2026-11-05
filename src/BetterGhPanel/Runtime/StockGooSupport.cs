using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Load and group-reference policies are independent of drawing/measurement.
    [Flags]
    internal enum StockTypeFacets { None = 0, Validity = 1, Managed = 2, Capture = 4, Preview = 8, Measure = 16, UnverifiedGroupReference = 32, ShapeOnlyLoad = 64 }

    // Exact SDK types only. Names/presentation do not grant inspection permission.
    internal static class StockGooSupport
    {
        private const StockTypeFacets V = StockTypeFacets.Validity, M = V | StockTypeFacets.Managed;
        private const StockTypeFacets G = V | StockTypeFacets.Capture, P = G | StockTypeFacets.Preview;
        internal sealed class Entry
        {
            internal Entry(Type? type, string name, StockTypeFacets facets,
                Func<IGH_Goo, GeometryBase?>? read = null, params Type[] inner)
            { Type = type; Name = name; Facets = facets; Read = read; Inner = inner; }
            internal Type? Type { get; }
            internal string Name { get; }
            internal StockTypeFacets Facets { get; }
            internal Func<IGH_Goo, GeometryBase?>? Read { get; }
            internal Type[] Inner { get; }
            internal bool Has(StockTypeFacets facet) => (Facets & facet) == facet;
        }
        private static Entry T(Type? type, string name, StockTypeFacets facets) => new Entry(type, name, facets);
        private static Entry Native<THolder, TValue>(string name, StockTypeFacets facets, Func<THolder, TValue?> read,
            params Type[] inner) where THolder : IGH_Goo where TValue : GeometryBase =>
            new Entry(typeof(THolder), name, facets, goo => read((THolder)goo), inner.Length == 0 ? new[] { typeof(TValue) } : inner);
        private static readonly Entry[] Entries =
        {
            T(typeof(GH_GeometryGroup), "Group", StockTypeFacets.None),
            T(typeof(GH_Number), "Number", M),
            T(typeof(GH_Integer), "Integer", M),
            T(typeof(GH_String), "Text", M),
            T(typeof(GH_Boolean), "Boolean", M),
            T(typeof(GH_Colour), "Colour", M),
            T(typeof(GH_Interval), "Domain", M),
            T(typeof(GH_Interval2D), "Domain²", M),
            T(typeof(GH_Guid), "Guid", M),
            T(typeof(GH_Time), "Time", M),
            T(typeof(GH_Point), "Point", M | StockTypeFacets.Preview),
            T(typeof(GH_Plane), "Plane", M),
            T(typeof(GH_Vector), "Vector", M),
            T(typeof(GH_Line), "Line", M | StockTypeFacets.Preview),
            T(typeof(GH_Arc), "Arc", M | StockTypeFacets.Preview),
            T(typeof(GH_Circle), "Circle", M | StockTypeFacets.Preview),
            T(typeof(GH_Rectangle), "Rectangle", M | StockTypeFacets.Preview),
            T(typeof(GH_Box), "Box", M | StockTypeFacets.Preview),
            Native<GH_Curve, Curve>("Curve", P | StockTypeFacets.Measure | StockTypeFacets.UnverifiedGroupReference, g => g.Value, typeof(LineCurve), typeof(ArcCurve), typeof(PolylineCurve), typeof(NurbsCurve), typeof(PolyCurve)),
            Native<GH_Surface, Brep>("Surface", P | StockTypeFacets.Measure | StockTypeFacets.UnverifiedGroupReference, g => g.Value),
            Native<GH_Brep, Brep>("Brep", P | StockTypeFacets.Measure | StockTypeFacets.UnverifiedGroupReference, g => g.Value),
            Native<GH_Mesh, Mesh>("Mesh", P | StockTypeFacets.Measure | StockTypeFacets.UnverifiedGroupReference, g => g.Value),
            Native<GH_SubD, SubD>("SubD", P, g => g.Value),
            Native<GH_Extrusion, Extrusion>("Extrusion", P, g => g.Value),
            Native<GH_PointCloud, PointCloud>("Point Cloud", P, g => g.Value),
            T(typeof(GH_Transform), "Transform", M),
            T(typeof(GH_MeshFace), "Meshface", M),
            T(typeof(GH_ComplexNumber), "Complex", M),
            Native<GH_TextDot, TextDot>("Text Dot", P, g => g.Value),
            Native<GH_TextEntity, TextEntity>("Text Entity", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_Leader, Leader>("Leader", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_AngularDimension, AngularDimension>("Angular Dimension", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_LinearDimension, LinearDimension>("Linear Dimension", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_OrdinateDimension, OrdinateDimension>("Ordinate Dimension", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_RadialDimension, RadialDimension>("Radial Dimension", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_Centermark, Centermark>("Radial Centermark", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_Hatch, Hatch>("Hatch", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            Native<GH_InstanceReference, InstanceReferenceGeometry>("Block Instance", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            T(typeof(IGH_Goo).Assembly.GetType("Grasshopper.Kernel.Types.GH_Light"), "Light", StockTypeFacets.None),
            Native<GH_DetailView, DetailView>("Detail View", G | StockTypeFacets.ShapeOnlyLoad, g => g.Value),
            T(typeof(GH_Culture), "Culture", M),
            T(typeof(GH_LonLatCoordinate), "Location", M),
            T(typeof(GH_StructurePath), "Path", M),
            T(typeof(GH_Matrix), "Matrix", M),
            T(typeof(GH_Material), "Material", StockTypeFacets.None),
            T(typeof(GH_Field), "Field", StockTypeFacets.None),
            T(typeof(GH_MeshingParameters), "MeshParameters", StockTypeFacets.None),
            T(typeof(GH_Predicate), "Predicate", StockTypeFacets.None),
        };
        private static readonly Dictionary<Type, Entry> Types = new Dictionary<Type, Entry>();
        private static readonly HashSet<Type> PreviewGeometry = new HashSet<Type> { typeof(Point), typeof(NurbsSurface), typeof(PlaneSurface), typeof(RevSurface) };
        static StockGooSupport()
        {
            foreach (var entry in Entries)
            {
                if (entry.Type != null) Types.Add(entry.Type, entry); // Only the non-public GH_Light is optional.
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
    }
}
