using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class OutputValuesChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, args);
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static string Paths(PanelHostFixture.Fixture fixture, int output) =>
        string.Join(",", fixture.Sinks[output].VolatileData.Paths.Select(path => path.ToString()));
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    internal static async Task Run(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        int[] lengths = { 2, 6, 2, 2, 6 };
        for (int i = 0; i < lengths.Length; i++)
        {
            fixture.Source.PersistentData.Append(new GH_Line(new Line(new Point3d(i * 10, 0, 0), new Point3d(i * 10 + lengths[i], 0, 0))), new GH_Path(0));
            fixture.Paired.PersistentData.Append(new GH_Integer(i), new GH_Path(0));
        }
        fixture.Solve(); await fixture.Ready(() => fixture.Result.RowPositions.Count == 5);
        Check(Property<PanelOutputValues>(component, "OutputValues") == PanelOutputValues.Items, "New components default to Items");
        Call(component, "SetTreeOperations", 0, false, false, false, true);
        fixture.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Length });
        await fixture.Ready(() => Paths(fixture, 0) == "{0;0},{0;1}" && fixture.Sinks[0].VolatileData.DataCount == 5);
        Check(fixture.Sinks[0].VolatileData.AllData(false).All(item => item is GH_Line) && fixture.Values(3) == "0,2,3,1,4",
            "Set Items and paired F group every original member in tree order");
        Check(fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().All(item => item.Value), "Filtered set mask keeps every duplicate");
        await fixture.Ready(() => ReferenceEquals(component.GetType().GetField("_cachedOutputResult", Instance)!.GetValue(component), fixture.Result));
        var filteredResult = fixture.Result;
        var filteredData = component.GetType().GetField("_filteredData", Instance)!.GetValue(component);
        fixture.Select(1); fixture.Scope(PanelOutputScope.Selected);
        await fixture.Ready(() => Paths(fixture, 1) == "{0;1}" && fixture.Sinks[1].VolatileData.DataCount == 2 && fixture.Values(3) == "1,4");
        Check(fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(item => item.Value).SequenceEqual(new[] { false, true, false, false, true }),
            "Selected set mask marks all members and S/F retain k without renumbering");
        Check(!ReferenceEquals(filteredResult, fixture.Result) || ReferenceEquals(filteredData, component.GetType().GetField("_filteredData", Instance)!.GetValue(component)),
            "Selection-only publication reuses filtered D for the same completed result");
        Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 1 });
        var highlight = component.GetType().GetField("_highlight", Instance)!.GetValue(component)!;
        Check(Property<int>(highlight, "PreviewCount") == 2 && Property<BoundingBox>(highlight, "ClippingBox").Max.X >= 46,
            "Selected set preview covers both geometries including the later occurrence");
        Call(component, "SetPreviewMode", Enum.Parse(assembly.GetType("BetterGhPanel.UI.PanelPreviewMode", true)!, "FilteredResults"));
        Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 1 });
        Check(Property<int>(highlight, "PreviewCount") == 5, "Filtered set preview includes every member alongside the selected group");
        Call(component, "SetPreviewMode", Enum.Parse(assembly.GetType("BetterGhPanel.UI.PanelPreviewMode", true)!, "Selected"));

        Call(component, "SetOutputValues", PanelOutputValues.Values);
        await fixture.Ready(() => Paths(fixture, 0) == "{0}" && fixture.Values(0) == "2,6" && fixture.Values(1) == "6");
        Check(Paths(fixture, 3) == "{0;1}" && fixture.Values(3) == "1,4", "Values keeps list-per-branch D/S while F remains grouped Items");
        fixture.Document.UndoServer.PerformUndo();
        await fixture.Ready(() => Paths(fixture, 0) == "{0;0},{0;1}" && Paths(fixture, 1) == "{0;1}");
        Check(fixture.SelectedCount == 1, "Output mode Undo preserves selection");
        fixture.Document.UndoServer.PerformRedo();
        await fixture.Ready(() => fixture.Values(0) == "2,6");
        Archives(fixture);

        // Minimum Full/Half footer controls keep their hit rectangles apart.
        PanelTestAdapter.Resize(component, 300, 220);
        foreach (bool half in new[] { false, true })
        {
            if (half) Call(component, "SetCanvasDisplay", Enum.Parse(assembly.GetType("BetterGhPanel.UI.CanvasPanelDisplay", true)!, "Half"));
            component.Attributes.PerformLayout();
            RectangleF Bounds(string name) => (RectangleF)component.Attributes.GetType().GetField(name, Instance)!.GetValue(component.Attributes)!;
            var items = Bounds("_outItems"); var values = Bounds("_outValues"); var units = Bounds("_units");
            Check(!items.IsEmpty && units.Right <= Bounds("_outLabel").Left && items.Right <= values.Left &&
                Bounds("_trim").Left >= Bounds("_panel").Left + 14 && values.Right <= Bounds("_panel").Right - 28,
                "Items/Values fits minimum " + (half ? "Half" : "Full") + " layout with native grip and resize clearance");
        }

        Call(component, "SetOutputValues", PanelOutputValues.Items);
        fixture.Scope(PanelOutputScope.Filtered);
        Call(component, "SetTreeOperations", 0, true, false, false, true);
        await fixture.Ready(() => Paths(fixture, 0) == "{0},{1}" && fixture.Sinks[0].VolatileData.DataCount == 5);
        Check(Paths(fixture, 3) == "{0},{1}", "Flattened set Items and F use {k}");
        await BranchSets(fixture);
        await TypedValues(fixture);
    }

    private static void Archives(PanelHostFixture.Fixture fixture)
    {
        var chunk = new GH_IO.Serialization.GH_LooseChunk("Panel");
        Check(fixture.Component.Write(chunk), "Output mode settings write");
        void Read(PanelOutputValues expected, string message)
        {
            var restored = (GH_Component)Activator.CreateInstance(fixture.Component.GetType())!;
            restored.CreateAttributes();
            Check(restored.Read(chunk) && Property<PanelOutputValues>(restored, "OutputValues") == expected, message);
            restored.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        }
        Read(PanelOutputValues.Values, "Saved Values output roundtrips");
        chunk.RemoveItem("PanelOutputValues");
        chunk.SetInt32("PanelOutputValues", (int)PanelOutputValues.Items);
        Read(PanelOutputValues.Items, "Explicit Items on a saved set roundtrips");
        chunk.RemoveItem("PanelOutputValues");
        Read(PanelOutputValues.Values, "Old saved sets migrate to Values");
        chunk.RemoveItem("PanelCreateSet");
        chunk.SetBoolean("PanelCreateSet", false);
        Read(PanelOutputValues.Items, "Old definitions without a set migrate to Items");
    }

    private static async Task BranchSets(PanelHostFixture.Fixture fixture)
    {
        fixture.Scope(PanelOutputScope.Filtered);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        for (int b = 0; b < 4; b++)
        {
            var path = new GH_Path(b);
            fixture.Source.PersistentData.EnsurePath(path); fixture.Paired.PersistentData.EnsurePath(path);
            if (b >= 2) continue;
            fixture.Source.PersistentData.Append(new GH_Integer(b), path);
            fixture.Paired.PersistentData.Append(new GH_String("F" + b), path);
        }
        Call(fixture.Component, "SetTreeOperations", 0, false, false, false, true);
        fixture.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        fixture.Solve();
        await fixture.Ready(() => Paths(fixture, 0) == "{0;0},{0;1},{1;2},{1;3}");
        Check(Paths(fixture, 3) == Paths(fixture, 0) && fixture.Values(3) == "F0,F1", "List-length set Items and F preserve all original and empty branches below {k;path}");
        fixture.SelectBranch("{2}");
        await fixture.Ready(() => Paths(fixture, 1) == "{1;2},{1;3}");
        Check(fixture.Sinks[1].VolatileData.DataCount == 0, "Selected empty branch set preserves both paths and its original k");
    }

    private static async Task TypedValues(PanelHostFixture.Fixture fixture)
    {
        Call(fixture.Component, "SetTreeOperations", 0, false, false, false, false);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(1.23456789, 0, 0))), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_String("missing"), new GH_Path(0));
        fixture.Paired.PersistentData.AppendRange(new IGH_Goo[] { new GH_Integer(0), new GH_Integer(1) }, new GH_Path(0));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.None, 2);
        Call(fixture.Component, "SetOutputValues", PanelOutputValues.Values);
        fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length });
        fixture.Solve();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 2 && fixture.Sinks[0].VolatileData.AllData(false).FirstOrDefault() is GH_Number);
        var values = fixture.Sinks[0].VolatileData.AllData(false).ToArray();
        Check(((GH_Number)values[0]).Value == 1.23456789 && values[1] == null, "Values retain full precision and a null unavailable slot");
        fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Closed });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).FirstOrDefault() is GH_Boolean);
        Check(fixture.Sinks[0].VolatileData.AllData(false).First() is GH_Boolean boolean && !boolean.Value, "Boolean properties publish typed GH_Boolean");
    }
}
