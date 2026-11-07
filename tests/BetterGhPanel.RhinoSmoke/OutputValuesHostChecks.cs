using static PanelTestAdapter;
using static SmokeAssertions;
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

internal static class OutputValuesHostChecks
{
    private static string Paths(PanelHostFixture.Fixture fixture, int output) =>
        string.Join(",", fixture.Sinks[output].VolatileData.Paths.Select(path => path.ToString()));

    internal static async Task Run(Assembly assembly)
    {
        SelectionWork(assembly);
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
        Check(PropertyValue<PanelOutputValues>(component, "OutputValues") == PanelOutputValues.Items, "New components default to Items");
        Call(component, "SetTreeOperations", 0, false, false, false, true);
        fixture.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Length });
        await fixture.Ready(() => Paths(fixture, 0) == "{0;0},{0;1}" && fixture.Sinks[0].VolatileData.DataCount == 5);
        Check(fixture.Sinks[0].VolatileData.AllData(false).All(item => item is GH_Line) && fixture.Values(3) == "0,2,3,1,4",
            "Set Items and paired F group every original member in tree order");
        Check(fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().All(item => item.Value), "Filtered set mask keeps every duplicate");
        await fixture.Ready(() => ReferenceEquals(component.GetType().GetField("_cachedOutputResult", AnyInstance)!.GetValue(component), fixture.Result));
        var filteredResult = fixture.Result;
        var filteredData = component.GetType().GetField("_filteredData", AnyInstance)!.GetValue(component);
        fixture.Select(1); fixture.Scope(PanelOutputScope.Selected);
        await fixture.Ready(() => Paths(fixture, 1) == "{0;1}" && fixture.Sinks[1].VolatileData.DataCount == 2 && fixture.Values(3) == "1,4");
        Check(fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(item => item.Value).SequenceEqual(new[] { false, true, false, false, true }),
            "Selected set mask marks all members and S/F retain k without renumbering");
        Check(!ReferenceEquals(filteredResult, fixture.Result) || ReferenceEquals(filteredData, component.GetType().GetField("_filteredData", AnyInstance)!.GetValue(component)),
            "Selection-only publication reuses filtered D for the same completed result");
        Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 1 });
        var highlight = component.GetType().GetField("_highlight", AnyInstance)!.GetValue(component)!;
        Check(PropertyValue<int>(highlight, "PreviewCount") == 2 && PropertyValue<BoundingBox>(highlight, "ClippingBox").Max.X >= 46,
            "Selected set preview covers both geometries including the later occurrence");
        Call(component, "SetPreviewMode", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelPreviewMode", true)!, "FilteredResults"));
        Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 1 });
        Check(PropertyValue<int>(highlight, "PreviewCount") == 5, "Filtered set preview includes every member alongside the selected group");
        Call(component, "SetPreviewMode", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelPreviewMode", true)!, "Selected"));

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
            if (half) Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, "Half"));
            component.Attributes.PerformLayout();
            RectangleF Bounds(string name) => PanelTestAdapter.LayoutRect(component.Attributes, name);
            var items = Bounds("OutItems"); var values = Bounds("OutValues"); var units = Bounds("Units");
            Check(!items.IsEmpty && values.Right + 4 <= Bounds("SelectAll").Left && items.Right == values.Left &&
                units.Width >= 48 && Bounds("Trim").Width == 22 && Bounds("OutLabel").Left >= Bounds("Panel").Left + 14 &&
                Bounds("Trim").Left >= Bounds("Panel").Left + 14 && values.Right <= Bounds("Panel").Right - 28,
                "Items/Values fits minimum " + (half ? "Half" : "Full") + " layout with native grip and resize clearance");
        }

        Call(component, "SetOutputValues", PanelOutputValues.Items);
        fixture.Scope(PanelOutputScope.Filtered);
        Call(component, "SetTreeOperations", 0, true, false, false, true);
        await fixture.Ready(() => Paths(fixture, 0) == "{0},{1}" && fixture.Sinks[0].VolatileData.DataCount == 5);
        Check(Paths(fixture, 3) == "{0},{1}", "Flattened set Items and F use {k}");
        await BranchSets(fixture);
        await TypedValues(fixture);
        await FooterLayout(fixture);
    }

    private static void SelectionWork(Assembly assembly)
    {
        var operations = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        foreach (int count in new[] { 16, 100000 })
        {
            var tree = new GH_Structure<IGH_Goo>();
            var rows = new PanelRow[count + 1];
            var branches = new PanelBranch[count];
            for (int i = 0; i < count; i++)
            {
                var path = new GH_Path(i, 2, 0);
                tree.Append(new GH_Integer(i), path);
                rows[i] = new PanelRow(0, path.ToString(), "Integer", i.ToString(), i);
                branches[i] = new PanelBranch(path.ToString(), i == count - 1 ? 2 : 1);
            }
            var last = tree.Paths[count - 1];
            tree.Append(new GH_Integer(count - 1), last);
            rows[count] = new PanelRow(1, last.ToString(), "Integer", (count - 1).ToString(), count - 1);
            var snapshot = new PanelSnapshot(rows, branches);
            var query = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
                { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Value });
            var values = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
                { OutputValues = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Value });
            var positions = new[] { count - 1 };
            var paths = new[] { last.ToString() };
            foreach (string operation in new[] { "SetItems", "DisplaySet", "Values" })
            {
                long visits = 0;
                Action<long> counter = value => visits = value;
                object[] args = operation == "SetItems"
                    ? new object[] { tree, query, positions, paths, false, counter }
                    : new object[] { tree, operation == "Values" ? values : query, positions, paths, counter };
                var method = operations.GetMethod(operation, BindingFlags.Static | BindingFlags.NonPublic)!;
                method.Invoke(null, args); // Warm the reflection and publication paths.
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var result = (GH_Structure<IGH_Goo>)method.Invoke(null, args)!;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                int expected = operation == "SetItems" ? 2 : 1;
                Check(result.PathCount == 1 && result.DataCount == expected && visits == 1 + expected && allocated < 65536,
                    operation + " visits only one touched path and its published members for " + count + " branches");
                Check(operation == "SetItems"
                    ? result.Paths[0].ToString() == last.AppendElement(0).ToString() && result.Branches[0].SequenceEqual(tree.Branches[count - 1])
                    : result.Paths[0].ToString() == last.ToString() && ((GH_Number)result.Branches[0][0]).Value == count - 1,
                    operation + " preserves the selected deep path, values and complete set membership");
            }
        }
    }

    private static async Task FooterLayout(PanelHostFixture.Fixture fixture)
    {
        var component = fixture.Component;
        var attributes = component.Attributes;
        using var canvas = new Grasshopper.GUI.Canvas.GH_Canvas { Document = fixture.Document, Size = new Size(1400, 700) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        RectangleF Bounds(string name) => PanelTestAdapter.LayoutRect(attributes, name);
        void Render()
        {
            attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        async Task Query(PanelSearchMode mode)
        {
            var previous = fixture.Result;
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = mode != PanelSearchMode.Text, SearchMode = mode });
            await fixture.Ready(() => !ReferenceEquals(previous, fixture.Result));
            Render();
        }
        foreach (float width in new[] { 300f, 360f, 640f })
        {
            PanelTestAdapter.Resize(component, width, 220);
            Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, "Full"));
            await Query(PanelSearchMode.Text);
            string[] controls = { "Trim", "Simplify", "Flatten", "Reverse", "CreateSet", "Units", "SelectAll", "Statistics" };
            var before = controls.Select(Bounds).ToArray();
            await Query(PanelSearchMode.Length);
            Check(controls.Select(Bounds).SequenceEqual(before), "Showing Out keeps existing footer controls fixed at width " + width);
            var values = Bounds("OutValues");
            var selectAll = Bounds("SelectAll");
            Check(values.Right <= selectAll.Left - 4 &&
                Math.Abs(values.Top + values.Height / 2 - selectAll.Top - selectAll.Height / 2) < 1,
                "Readable segmented Out sits immediately left of Select all on the same row at width " + width);
            var output = RectangleF.Union(Bounds("OutLabel"), RectangleF.Union(Bounds("OutItems"), values));
            Check(controls.All(name => !output.IntersectsWith(Bounds(name))),
                "Out does not overlap any footer control in X and Y at width " + width);
            var point = new PointF(Bounds("OutValues").X + 10, Bounds("OutValues").Y + 8);
            var tooltip = (Grasshopper.GUI.GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(Grasshopper.GUI.GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, System.Drawing.Point.Round(point) }, null)!;
            attributes.SetupTooltip(point, tooltip);
            Check(tooltip.Title == "Output: Items / Values", "Out tooltip survives upper-row statistics targeting at width " + width);
        }
        PanelTestAdapter.Resize(component, 300, 220);
        Call(component, "SetPanelSplit", true);
        foreach (string display in new[] { "Full", "Half" })
        {
            Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, display));
            await Query(PanelSearchMode.Text);
            await Query(PanelSearchMode.ListLength);
            var pairing = PropertyValue<object>(component, "PairedData");
            var pairedRows = (System.Collections.IDictionary)pairing.GetType().GetField("BranchSummaries", AnyInstance)!.GetValue(pairing)!;
            Check(pairedRows.Count > 0 && PanelTestAdapter.PaneField<RectangleF>(attributes, "List").Height >= 17 &&
                PanelTestAdapter.PaneField<RectangleF>(attributes, "List").Bottom <= Bounds("Footer").Top,
                "Minimum table " + display + " retains branch summaries and a full data row without footer overlap");
        }
    }

    private static void Archives(PanelHostFixture.Fixture fixture)
    {
        var chunk = new GH_IO.Serialization.GH_LooseChunk("Panel");
        Check(fixture.Component.Write(chunk), "Output mode settings write");
        void Read(PanelOutputValues expected, string message, bool enableValues = false)
        {
            var restored = (GH_Component)Activator.CreateInstance(fixture.Component.GetType())!;
            restored.CreateAttributes();
            Check(restored.Read(chunk) && PropertyValue<PanelOutputValues>(restored, "OutputValues") == expected, message);
            if (enableValues)
            {
                Call(PropertyValue<object>(restored, "Session"), "UpdateQuery",
                    new PanelQueryOptions { SearchMode = PanelSearchMode.Value, DisplayFilterValue = true });
                Check(PropertyValue<PanelOutputValues>(restored, "OutputValues") == PanelOutputValues.Items,
                    "Enabling value display after load does not resurrect a hidden Values choice");
            }
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
        chunk.SetInt32("PanelOutputValues", (int)PanelOutputValues.Values);
        chunk.RemoveItem("PanelSearchMode");
        chunk.SetInt32("PanelSearchMode", (int)PanelSearchMode.Text);
        chunk.RemoveItem("PanelDisplayFilterValue");
        chunk.SetBoolean("PanelDisplayFilterValue", false);
        Read(PanelOutputValues.Items, "Saved hidden Values normalises to Items on load", enableValues: true);
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
        Check(((GH_Number)values[0]).Value == 1.23 && values[1] == null, "Values retain presented precision and a null unavailable slot");
        fixture.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Closed });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).FirstOrDefault() is GH_Boolean);
        Check(fixture.Sinks[0].VolatileData.AllData(false).First() is GH_Boolean boolean && !boolean.Value, "Boolean properties publish typed GH_Boolean");
    }
}
