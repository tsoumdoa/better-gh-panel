using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

internal static class DisplaySetChecks
{
    internal static async Task Run(Assembly assembly)
    {
        var previous = Grasshopper.Instances.DocumentEditor;
        using var owned = previous == null ? new Grasshopper.GUI.GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        var rhino = RhinoDoc.ActiveDoc;
        var units = rhino.ModelUnitSystem;
        rhino.ModelUnitSystem = UnitSystem.Millimeters;
        try
        {
            using var fixture = new OutputChecks.Fixture(assembly);
            using var canvas = new GH_Canvas { Size = new Size(800, 700), Document = fixture.Document };
            var component = fixture.Component;
            Load(new[] { 3, 3, 5, 0, 0 }.Select(count => Enumerable.Repeat<IGH_Goo>(new GH_Number(7), count).ToArray()).ToArray());
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            await fixture.Ready(() => fixture.Values(0) == "3,5,0");
            Check(fixture.Sinks[0].VolatileData.AllData(true).All(value => value is GH_Integer) &&
                fixture.Sinks[0].VolatileData.Paths.Select(path => path.ToString()).SequenceEqual(new[] { "{0}", "{2}", "{3}" }),
                "List-length Create set outputs numbers 3, 5, 0 in the representative paths, not input values or deduplicated input lengths");
            Check(fixture.Values(3) == "0:0,0:1,0:2,2:0,2:1,2:2,2:3,2:4" &&
                fixture.Values(2) == "True,True,True,False,False,False,True,True,True,True,True" && fixture.Result.Statistics.ItemCount == 3,
                "List-count masks and pairs follow the retained source branches while statistics describe the three output counts");
            Capture("display-set-list-length");
            fixture.SelectBranch("{3}");
            fixture.Scope(PanelOutputScope.Selected);
            await fixture.Ready(() => fixture.Values(1) == "0" && fixture.Sinks[3].VolatileData.PathCount == 1 && fixture.Values(3) == "");
            Check(fixture.Sinks[3].VolatileData.Paths[0].ToString() == "{3}" && fixture.Values(2).Split(',').All(value => value == "False"),
                "Selecting zero emits zero on S, preserves its empty paired branch and marks no nonexistent input items");
            fixture.ClearSelection();
            await fixture.Ready(() => fixture.Values(1) == "" && fixture.Sinks[3].VolatileData.PathCount == 0);
            fixture.Scope(PanelOutputScope.Filtered);
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength, BranchPath = "{1}" });
            await fixture.Ready(() => fixture.Values(0) == "3" && fixture.Values(3) == "1:0,1:1,1:2");
            Check(fixture.Sinks[0].VolatileData.Paths[0].ToString() == "{1}", "Branch filtering happens before equal-count grouping");
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
            PanelTestAdapter.TreeTools(component, 0, true, false, false, true);
            await fixture.Ready(() => fixture.Values(0) == "11");
            Check(fixture.Values(3).Split(',').Length == 11, "Flatten counts all displayed source items before Create set");
            PanelTestAdapter.TreeTools(component, 0, false, false, false, false);
            await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 11);
            Check(fixture.Sinks[0].VolatileData.AllData(true).Cast<GH_Number>().All(value => value.Value == 7),
                "Disabling Create set restores the complete original typed data");

            Load(new[] { new IGH_Goo[] { new GH_Number(7), new GH_String("hidden"), new GH_Number(8) } });
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength, TypeName = "Number" });
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            await fixture.Ready(() => fixture.Values(0) == "3" && fixture.Values(3) == "0:0,0:2");
            fixture.SelectBranch("{0}");
            fixture.Scope(PanelOutputScope.Selected);
            await fixture.Ready(() => fixture.Values(1) == "3" && fixture.Values(3) == "0:0,0:2" && fixture.Values(2) == "True,False,True");
            Check(fixture.Sinks[3].VolatileData.DataCount == 2, "Selected list-count pairs respect active item filters within the representative branch");
            fixture.Scope(PanelOutputScope.Filtered);
            PanelTestAdapter.TreeTools(component, 0, false, false, false, false);

            var geometry = new IGH_Goo[] { new GH_Circle(new Circle(Plane.WorldXY, 1000)),
                new GH_Curve(new LineCurve(new Point3d(10, 0, 0), new Point3d(6294, 0, 0)).ToNurbsCurve()) }
                .Concat(Enumerable.Range(0, 40).Select(index => (IGH_Goo)new GH_Curve(new LineCurve(new Point3d(index, 1, 0), new Point3d(index, 2001, 0)).ToNurbsCurve()))).ToArray();
            Load(new[] { geometry });
            PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length });
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            await fixture.Ready(() => fixture.Values(0) == "6.28,2" && fixture.Values(3) == "0:0,0:2");
            Check(fixture.Sinks[0].VolatileData.AllData(true).All(value => value is GH_Number) &&
                fixture.Result.SetOccurrencesAt(0)!.Count == 2 && fixture.Result.SetOccurrencesAt(2)!.Count == 40 &&
                fixture.Result.Statistics.NumericCount == 2 && fixture.Result.Statistics.Minimum == 2 &&
                ((GH_Circle)fixture.Source.PersistentData.Branches[0][0]).Value.Radius == 1000,
                "Length sets output converted displayed numbers, measure offscreen duplicates, and preserve original geometry");
            Capture("display-set-length");
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length, SortMode = PanelSortMode.ValueAscending });
            await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 2, 0 }));
            Check(fixture.Values(0) == "6.28,2" && PanelTestAdapter.CopyValue(component, fixture.Result.Snapshot, 0) == "6.28",
                "Sorted metric sets keep wire tree order and copy the displayed numeric value");
            PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 3);
            await fixture.Ready(() => fixture.Values(0) == "6.283,6.284,2" && fixture.Values(3) == "0:0,0:1,0:2");
            Check(fixture.Sinks[0].VolatileData.DataCount == 3, "Changing display precision automatically republishes the regrouped set");

            PanelTestAdapter.TreeTools(component, 0, false, false, false, false);
            PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);
            Load(new[] { new IGH_Goo[] { new GH_Number(1.234), new GH_Number(1.233), new GH_Number(2) } });
            fixture.Query(new PanelQueryOptions());
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            await fixture.Ready(() => fixture.Values(0) == "1.23,2" && fixture.Values(3) == "0:0,0:2");
            fixture.Query(new PanelQueryOptions { Search = "< 1.234" });
            await fixture.Ready(() => fixture.Values(0) == "1.23" && fixture.Values(3) == "0:1");
            Check(fixture.Result.SetOccurrencesAt(1)!.Count == 1 && fixture.Values(2) == "False,True,False",
                "Create set uses current filtered values, not a representative removed earlier from the input");
            PanelTestAdapter.Units(component, PanelUnitConversion.None, 3);
            await fixture.Ready(() => fixture.Values(0) == "1.233");
            fixture.Query(new PanelQueryOptions());
            PanelTestAdapter.TreeTools(component, 0, false, false, false, false);
            await fixture.Ready(() => fixture.Values(0) == "1.234,1.233,2");
            Check(fixture.Source.PersistentData.DataCount == 3, "All original precision returns when Create set is disabled");
            Console.WriteLine("PASS: Displayed-value sets publish numeric D/S values with matching masks, pairs, counts and source provenance.");

            void Load(IGH_Goo[][] branches)
            {
                fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
                for (int b = 0; b < branches.Length; b++)
                {
                    var path = new GH_Path(b);
                    fixture.Source.PersistentData.EnsurePath(path); fixture.Paired.PersistentData.EnsurePath(path);
                    for (int i = 0; i < branches[b].Length; i++)
                    {
                        fixture.Source.PersistentData.Append(branches[b][i], path);
                        fixture.Paired.PersistentData.Append(new GH_String(b + ":" + i), path);
                    }
                }
                fixture.Solve();
            }
            void Capture(string name)
            {
                PanelTestAdapter.Resize(component, 360, 300);
                component.Attributes.PerformLayout();
                canvas.Viewport.Size = canvas.Size; canvas.Viewport.Zoom = 1; canvas.Viewport.ComputeProjection();
                var bounds = component.Attributes.Bounds;
                using var bitmap = new Bitmap((int)(bounds.Width + 30) * 2, (int)(bounds.Height + 30) * 2);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.FromArgb(239, 240, 237)); graphics.ScaleTransform(2, 2);
                graphics.TranslateTransform(15 - bounds.X, 15 - bounds.Y);
                PanelTestAdapter.Render(component, canvas, graphics);
                bitmap.Save(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-" + name + ".png"));
            }
        }
        finally { rhino.ModelUnitSystem = units; setEditor.Invoke(null, new object?[] { previous }); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
