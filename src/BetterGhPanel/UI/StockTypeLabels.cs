using System;
using System.Collections.Generic;

namespace BetterGhPanel.UI
{
    // Presentation only: names never grant permission to inspect a value.
    internal static class StockTypeLabels
    {
        private static readonly Dictionary<string, (string Label, bool Separate)> Names =
            new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
            {
                ["Number"] = ("Num", false),
                ["Integer"] = ("Int", false),
                ["Boolean"] = ("Bool", false),
                ["Colour"] = ("Col", false),
                ["Domain"] = ("Dom", false),
                ["Domain²"] = ("Dom²", false),
                ["Point"] = ("Pt", false),
                ["Plane"] = ("Pln", false),
                ["Vector"] = ("Vec", false),
                ["Line"] = ("Ln", false),
                ["Circle"] = ("Cir", false),
                ["Rectangle"] = ("Rect", false),
                ["Curve"] = ("Crv", false),
                ["Surface"] = ("Srf", false),
                ["Extrusion"] = ("Extr", false),
                ["Point Cloud"] = ("Cloud", true),
                ["Transform"] = ("Xform", false),
                ["Meshface"] = ("Face", false),
                ["Complex"] = ("Cplx", false),
                ["Text Dot"] = ("Dot", true),
                ["Text Entity"] = ("TxtEnt", true),
                ["Leader"] = ("Ldr", true),
                ["Angular Dimension"] = ("ADim", true),
                ["Linear Dimension"] = ("LDim", true),
                ["Ordinate Dimension"] = ("ODim", true),
                ["Radial Dimension"] = ("RDim", true),
                ["Radial Centermark"] = ("CMark", true),
                ["Hatch"] = ("Hatch", true),
                ["Block Instance"] = ("Block", true),
                ["Detail View"] = ("Detail", true),
                ["Location"] = ("Loc", false),
                ["Material"] = ("Mat", false),
                ["MeshParameters"] = ("MeshParams", false),
            };
        internal static string ShortName(string name) => Names.TryGetValue(name, out var entry) ? entry.Label : name;
        internal static bool SeparateLabel(string name) => Names.TryGetValue(name, out var entry) && entry.Separate;
    }
}
