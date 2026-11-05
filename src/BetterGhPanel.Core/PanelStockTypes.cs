using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    [Flags]
    public enum StockTypeFacets { None = 0, Validity = 1, Managed = 2, Capture = 4, Preview = 8 }

    // SDK names, presentation and independently audited capabilities. Runtime binds
    // these names to exact Types from the Grasshopper assembly, never to subclasses.
    // A name alone grants no permission to invoke a goo or its contained values.
    public sealed class PanelStockType
    {
        internal PanelStockType(string sdkName, string name, string label, StockTypeFacets facets)
        { SdkName = sdkName; Name = name; Label = label; Facets = facets; }
        public string SdkName { get; }
        public string Name { get; }
        public string Label { get; }
        public StockTypeFacets Facets { get; }
        public bool Has(StockTypeFacets facet) => (Facets & facet) == facet;
    }

    public static class PanelStockTypes
    {
        private const StockTypeFacets V = StockTypeFacets.Validity, M = V | StockTypeFacets.Managed;
        private const StockTypeFacets G = V | StockTypeFacets.Capture, P = G | StockTypeFacets.Preview;
        private static PanelStockType T(string sdk, string name, string? label = null, StockTypeFacets facets = StockTypeFacets.None)
            => new PanelStockType("GH_" + sdk, name, label ?? name, facets);
        public static IReadOnlyList<PanelStockType> All { get; } = Array.AsReadOnly(new[]
        {
            T("GeometryGroup", "Group"), T("Number", "Number", "Num", M), T("Integer", "Integer", "Int", M),
            T("String", "Text", facets: M), T("Boolean", "Boolean", "Bool", M), T("Colour", "Colour", "Col", M),
            T("Interval", "Domain", "Dom", M), T("Interval2D", "Domain²", "Dom²", M), T("Guid", "Guid", facets: M), T("Time", "Time", facets: M),
            T("Point", "Point", "Pt", M | StockTypeFacets.Preview), T("Plane", "Plane", "Pln", M), T("Vector", "Vector", "Vec", M),
            T("Line", "Line", "Ln", M | StockTypeFacets.Preview), T("Arc", "Arc", facets: M | StockTypeFacets.Preview),
            T("Circle", "Circle", "Cir", M | StockTypeFacets.Preview), T("Rectangle", "Rectangle", "Rect", M | StockTypeFacets.Preview),
            T("Box", "Box", facets: M | StockTypeFacets.Preview), T("Curve", "Curve", "Crv", P), T("Surface", "Surface", "Srf", P),
            T("Brep", "Brep", facets: P), T("Mesh", "Mesh", facets: P), T("SubD", "SubD", facets: P),
            T("Extrusion", "Extrusion", "Extr", P), T("PointCloud", "Point Cloud", "Cloud", P),
            T("Transform", "Transform", "Xform", M), T("MeshFace", "Meshface", "Face", M), T("ComplexNumber", "Complex", "Cplx", M),
            T("TextDot", "Text Dot", "Dot", P), T("TextEntity", "Text Entity", "Txt", G), T("Leader", "Leader", "Ldr", G),
            T("AngularDimension", "Angular Dimension", "AngDim", G), T("LinearDimension", "Linear Dimension", "Dim", G),
            T("OrdinateDimension", "Ordinate Dimension", "OrdDim", G), T("RadialDimension", "Radial Dimension", "RadDim", G),
            T("Centermark", "Radial Centermark", "Center", G), T("Hatch", "Hatch", facets: G),
            T("InstanceReference", "Block Instance", "Block", G), T("Light", "Light"), T("DetailView", "Detail View", "Detail", G),
            T("Culture", "Culture", facets: M), T("LonLatCoordinate", "Location", "Loc", M), T("StructurePath", "Path", facets: M),
            T("Matrix", "Matrix", facets: M), T("Material", "Material", "Mat"), T("Field", "Field"),
            T("MeshingParameters", "MeshParameters", "MeshParams"), T("Predicate", "Predicate")
        });
        private static readonly Dictionary<string, string> Labels = BuildLabels();
        private static Dictionary<string, string> BuildLabels()
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var type in All) labels.Add(type.Name, type.Label);
            // Presentation aliases retained for saved/synthetic snapshots.
            labels["Mesh Face"] = "Face"; labels["Complex Number"] = "Cplx"; labels["Geometry Group"] = "Geo group";
            return labels;
        }
        public static string ShortName(string name) => Labels.TryGetValue(name, out string label) ? label : name;
    }
}
