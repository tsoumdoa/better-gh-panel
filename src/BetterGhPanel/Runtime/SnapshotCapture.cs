using System;
using System.Collections.Generic;
using System.Globalization;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    internal static class SnapshotCapture
    {
        public static PanelSnapshot Capture(GH_Structure<IGH_Goo> tree, long version, out IGH_Goo?[] sourceItems)
        {
            var rows = new List<PanelRow>(tree.DataCount);
            var branches = new List<PanelBranch>(tree.PathCount);
            var source = new List<IGH_Goo?>(tree.DataCount);
            for (var b = 0; b < tree.PathCount; b++)
            {
                var path = tree.Paths[b].ToString();
                var items = tree.Branches[b];
                branches.Add(new PanelBranch(b, path, items.Count));
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    string type, display;
                    double? number;
                    var valid = true;
                    try
                    {
                        type = item?.TypeName ?? "Null";
                        valid = item == null || item.IsValid;
                        display = FormatSummary(item, out number);
                    }
                    catch (Exception)
                    {
                        type = item?.GetType().Name ?? "Null";
                        display = "<unavailable value>";
                        number = null;
                        valid = false;
                    }
                    rows.Add(new PanelRow(rows.Count, b, i, path, type, display, number, item == null, valid));
                    source.Add(item);
                }
            }
            sourceItems = source.ToArray();
            return new PanelSnapshot(version, rows.AsReadOnly(), branches.AsReadOnly());
        }

        private static string FormatSummary(IGH_Goo? goo, out double? numeric)
        {
            numeric = null;
            if (goo == null) return "<null>";
            if (goo is GH_Number number)
            {
                numeric = number.Value;
                return number.Value.ToString("G12", CultureInfo.InvariantCulture);
            }
            if (goo is GH_Integer integer)
            {
                numeric = integer.Value;
                return integer.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (goo is GH_String text) return text.Value ?? "<null text>";
            if (goo is GH_Boolean boolean) return boolean.Value ? "True" : "False";
            if (goo is GH_Point point)
                return string.Format(CultureInfo.InvariantCulture, "({0:G8}, {1:G8}, {2:G8})", point.Value.X, point.Value.Y, point.Value.Z);
            if (goo is GH_Vector vector)
                return string.Format(CultureInfo.InvariantCulture, "<{0:G8}, {1:G8}, {2:G8}>", vector.Value.X, vector.Value.Y, vector.Value.Z);
            // Geometry and third-party goo stay in Rhino. Their potentially expensive
            // ToString is requested on the UI thread only when a user inspects a row.
            return "<" + goo.TypeName + ">";
        }
    }
}
