using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Data;
using Forms = System.Windows.Forms;

internal static class BranchPairingChecks
{
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static string Paths(PanelHostFixture.Fixture f, int port) => string.Join(",", f.Sinks[port].VolatileData.Paths);
    private static string Branch(PanelHostFixture.Fixture f, int port, string path)
    {
        var tree = f.Sinks[port].VolatileData;
        int b = tree.Paths.ToList().FindIndex(candidate => candidate.ToString() == path);
        return b < 0 ? "<missing>" : string.Join(",", tree.get_Branch(b).Cast<IGH_Goo>().Select(value => value?.ToString() ?? "<null>"));
    }
    private static async Task Verify(Assembly assembly)
    {
        await VerifySummaryFreshness(assembly);
        using var f = new PanelHostFixture.Fixture(assembly);
        var component = f.Component; var attributes = component.Attributes;
        Task Ready(Func<bool> condition) => f.Ready(() =>
            ReferenceEquals(FieldValue<PanelQueryResult?>(component, "_cachedOutputResult"), f.Result) && condition());
        void Seed(bool order = false, bool empty = false)
        {
            f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
            for (int b = 0; b < (empty ? 4 : 3); b++)
            {
                var path = new GH_Path(0, b);
                f.Source.PersistentData.EnsurePath(path);
                int count = b == 0 ? 12 : b == 3 ? 0 : 2;
                for (int i = 0; i < count; i++) f.Source.PersistentData.Append(new GH_Integer(b == 0 ? i % 2 : b == 1 ? 10 + i : 20), path);
                var pairedPath = order && b != 1 ? new GH_Path(1, b) : path;
                int pairedCount = b == 0 ? 1 : b == 1 ? 4 : b == 3 ? 1 : 2;
                for (int i = 0; i < pairedCount; i++)
                    f.Paired.PersistentData.Append(new GH_String(b == 0 ? "Branch name" : "F" + b + "-" + i), pairedPath);
            }
            f.Solve();
        }
        Seed();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 7);
        Check(Branch(f, 3, "{0;0}") == "Branch name" && Branch(f, 3, "{0;1}") == "F1-0,F1-1,F1-2,F1-3",
            "Unequal original branches publish whole raw F once, without null padding");
        Check(Paths(f, 2) == Paths(f, 0) && f.Sinks[2].VolatileData.DataCount == 16, "B keeps D original paths and counts");
        Call(component, "SetPanelSplit", true);
        using var canvas = new GH_Canvas { Document = f.Document, Size = new Size(1240, 900) };
        _ = canvas.Handle; canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty; canvas.Viewport.Zoom = 1; canvas.Viewport.ComputeProjection();
        var visible = canvas.Viewport.VisibleRegion;
        attributes.Pivot = new PointF(visible.Left + 40, visible.Top + 30);
        PanelTestAdapter.Resize(component, 560, 650);
        string screenshots = Path.Combine(AppContext.BaseDirectory, "pairing-screenshots"); Directory.CreateDirectory(screenshots);
        void Render(string? name = null)
        {
            if (name == null) { Draw(null); return; }
            var previous = GH_Skin.canvas_back;
            try
            {
                foreach (bool dark in new[] { false, true })
                {
                    GH_Skin.canvas_back = dark ? Color.FromArgb(35, 38, 36) : Color.FromArgb(229, 229, 229);
                    Draw(name + (dark ? "-dark" : "-light"));
                }
            }
            finally { GH_Skin.canvas_back = previous; }
        }
        void Draw(string? name)
        {
            attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(GH_Skin.canvas_back);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
            if (name != null)
            {
                string file = Path.Combine(screenshots, name + ".png"); bitmap.Save(file, ImageFormat.Png);
                Console.WriteLine("SCREENSHOT: " + file);
            }
        }
        RectangleF PaneRow(int position, bool paired)
        {
            var pane = PanelTestAdapter.PaneField<RectangleF>(attributes, paired, "List");
            foreach (var hit in FieldValue<IEnumerable>(attributes, "_rowHits"))
                if (PropertyValue<int>(hit!, "SourcePosition") == position && pane.Contains(PropertyValue<RectangleF>(hit!, "Bounds")))
                    return PropertyValue<RectangleF>(hit!, "Bounds");
            throw new InvalidOperationException("Expected visible paired row " + position);
        }
        Render();
        f.Select(3); await Ready(() => f.Sinks[1].VolatileData.DataCount == 1);
        Render("uneven-split");
        var display = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        var extraRows = display.Where(row => FieldValue<int>(row, "Extra") >= 0).ToArray();
        Check(display.Any(row => FieldValue<string>(row, "Label") == "no F items (11 rows)"),
            "Missing runs use a row count, never a potentially reversed index range");
        Check(extraRows.Length == 2 && FieldValue<string>(extraRows[0], "Label") == "+2 only in F" &&
            FieldValue<string>(extraRows[1], "Label") == "" && PropertyValue<object>(component, "PairedSnapshot") is PanelSnapshot pairedSnapshot &&
            pairedSnapshot.Rows[FieldValue<int>(extraRows[0], "Extra")].OriginalIndex == 2,
            "F extras retain raw item indices and keep the first-row note separate from data");
        bool offsetsAgree = true;
        int successor = f.Result.RowPositions.Count;
        for (int offset = display.Length - 1; offset >= 0; offset--)
        {
            int item = FieldValue<int>(display[offset], "Item");
            if (item >= 0) { successor = item; offsetsAgree &= (int)Call(attributes, "ViewOffset", f.Result, item)! == offset; }
            offsetsAgree &= (int)Call(attributes, "ViewItemAtOrAfter", f.Result, offset)! == successor;
        }
        Check(offsetsAgree && ReferenceEquals(display[0], ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().First()),
            "Cached item offsets and spacer successors agree with the paired index and reuse its rows");
        Check(PaneRow(3, true).Y == PaneRow(3, false).Y && PaneRow(14, true).Y == PaneRow(14, false).Y,
            "Missing F cells and F extras with D spacers preserve linked row alignment");
        Check(Call(component, "PairedRow", 3) == null && ((PanelRow)Call(component, "PairedRow", 14)!).StringValue == "F2-0",
            "Explicit D to optional F mapping skips missing cells without shifting following branches");
        int hits = PanelTestAdapter.Hits(component, "_rowHits");
        Check(hits == 32 && PanelTestAdapter.PaneField<int>(attributes, false, "Shown") == 18, "F-only rows and D spacers have no selection hits");
        using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            canvas.Viewport.ApplyProjection(graphics);
            graphics.SetClip(PaneRow(3, true));
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        Check(PanelTestAdapter.Hits(component, "_rowHits") == hits, "Partial repaint of a missing F cell preserves hit coverage without duplicate row hits");
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            f.ClearSelection(); f.Select(0); f.Select(3, toggle: true); f.Select(14, toggle: true);
            string status = await (Task<string>)Call(component, "CopyPairedValuesAsync", f.Result.Snapshot, new[] { 0, 3, 14 })!;
            await f.Ready(() => Forms.Clipboard.GetText() == "Branch name" + Environment.NewLine + Environment.NewLine + "F2-0");
            Check(status == "3 values copied" && Forms.Clipboard.GetText() == "Branch name" + Environment.NewLine + Environment.NewLine + "F2-0",
                "F copying uses raw mapped values and an empty line for a missing F item");
        }
        finally { if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true); }
        // Uneven rows share selection coordinates but F-only rows never enter navigation.
        PanelTestAdapter.Resize(component, 560, 260); Render();
        var link = PanelTestAdapter.LayoutRect(attributes, "ScrollLock");
        PanelTestAdapter.CanvasClick(canvas, new PointF(link.X + 8, link.Y + 7)); Render();
        Call(attributes, "SetPaneOffset", canvas, 13, false); Render();
        var row13 = PaneRow(13, false);
        PanelTestAdapter.CanvasClick(canvas, new PointF(row13.X + 90, row13.Y + 8)); Render();
        Check(PaneRow(13, true).Y >= PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List").Y,
            "Selecting D in an unlinked uneven split reveals its F partner");
        var input = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!;
        input.GetMethod("HandleKey")!.Invoke(attributes, new object[] { canvas, Forms.Keys.Down }); Render();
        Check(PanelTestAdapter.SelectedPosition(component) == 14 && !PaneRow(14, true).IsEmpty,
            "Keyboard navigation skips F extras and D spacers while revealing the next paired D row");
        int dataOffset = PanelTestAdapter.Offset(component);
        input.GetMethod("HandleWheel")!.Invoke(attributes, new object[] { canvas, PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List").Location, 240 }); Render();
        Check(PanelTestAdapter.Offset(component) == dataOffset && PanelTestAdapter.PaneField<int>(attributes, true, "Offset") < dataOffset,
            "Unlinked F wheel scrolling leaves D's augmented row offset unchanged");
        PanelTestAdapter.CanvasClick(canvas, new PointF(link.X + 8, link.Y + 7)); Render();
        Check(PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == PanelTestAdapter.Offset(component), "Relinking uneven panes snaps their augmented scroll offsets together");
        Call(attributes, "SetPaneOffset", canvas, 0, false);
        PanelTestAdapter.Resize(component, 560, 650); Render();
        f.ClearSelection(); f.Select(3); f.Scope(PanelOutputScope.Selected);
        await Ready(() => f.Values(3) == "Branch name");
        Check(f.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Count(value => value.Value) == 1,
            "Selected missing-F row still emits its whole unequal branch while B marks only D membership");
        f.ClearSelection(); await Ready(() => f.Sinks[3].VolatileData.PathCount == 0);
        f.Scope(PanelOutputScope.Filtered);
        f.Query(new PanelQueryOptions { Search = "= 11" });
        await Ready(() => f.Values(0) == "11" && f.Values(3) == "F1-0,F1-1,F1-2,F1-3");
        Check(Paths(f, 3) == "{0;1}", "Filtered scope emits a whole unequal branch exactly once at D's path");
        f.Query(new PanelQueryOptions { Search = "= 20" });
        await Ready(() => f.Values(3) == "F2-0,F2-1");
        Check(f.Sinks[3].VolatileData.DataCount == 2, "Equal original counts retain item-level filtering");
        f.Query(new PanelQueryOptions());
        Seed(order: true);
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 7);
        Render("order-paired-paths");
        Check(PropertyValue<string>(PropertyValue<object>(component, "PairedData"), "Caption") ==
            "Paired by branch · 2 by order · 2 of 3 differ in length",
            "Caption includes both order fallback and unequal lengths without zero-count parts");
        Check(Branch(f, 3, "{0;1}") == "F1-0,F1-1,F1-2,F1-3" && Branch(f, 3, "{0;0}") == "Branch name",
            "Exact path reservation precedes fallback even when an unmatched D branch comes first");
        foreach (var tools in new[] { (0, false, false, true), (1, false, false, false), (0, true, true, false), (1, true, true, true) })
        {
            int solutions = f.Solutions;
            PanelTestAdapter.TreeTools(component, tools.Item1, tools.Item2, tools.Item3, tools.Item4);
            await Ready(() => f.Solutions > solutions && f.Sinks[3].VolatileData.DataCount == 7 && Paths(f, 3) == Paths(f, 0));
            Check(f.Values(3).Split(',').OrderBy(value => value).SequenceEqual(new[] { "Branch name", "F1-0", "F1-1", "F1-2", "F1-3", "F2-0", "F2-1" }.OrderBy(value => value)),
                "Original branch identity survives Simplify/Trim/Flatten/Reverse: " + tools);
        }
        PanelTestAdapter.TreeTools(component, 0, false, true, false);
        await Ready(() => Branch(f, 3, "{0;1}") == "F1-1,F1-0,F1-2,F1-3");
        Check(Branch(f, 3, "{0;2}") == "F2-1,F2-0", "Reverse keeps both equal and unequal F prefixes aligned to D, with F-only tails after D rows");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        Seed(empty: true);
        await Ready(() => Branch(f, 3, "{0;3}") == "F3-0");
        Check(Branch(f, 0, "{0;3}") == "", "A retained empty D path includes its complete F branch");
        f.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        await Ready(() => f.Result.BranchPaths.Count == 4);
        Render();
        Check(PanelTestAdapter.Hits(component, "_branchHits") == 8 &&
            ((string)Call(attributes, "PairedBranchValue", "{0;0}")!).Contains("Branch name") &&
            ((string)Call(attributes, "PairedBranchValue", "{0;3}")!).Contains("F3-0"),
            "Branch-level list lengths show raw F summaries, including the F partner of empty D");
        foreach (bool flatten in new[] { false, true })
        {
            PanelTestAdapter.TreeTools(component, 0, flatten, false, false, true);
            f.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Value });
            await Ready(() => f.Result.DisplaySet?.IsBranchSet == false && Paths(f, 3) == Paths(f, 0) &&
                f.Sinks[3].VolatileData.AllData(false).Count(value => value.ToString() == "Branch name") == 2);
            var tree = f.Sinks[3].VolatileData;
            Check(tree.AllData(false).Count(value => value.ToString() == "Branch name") == 2 &&
                tree.AllData(false).Count(value => value.ToString() == "F1-3") == 2,
                "Create set duplicates each whole unequal F branch into every D member group, once per group; flatten=" + flatten);
            Check(Paths(f, 3) == Paths(f, 0), "Create-set F paths exactly match set Items {path;k} groups; flatten=" + flatten);
            f.Select(f.Result.RowPositions[0]); f.Scope(PanelOutputScope.Selected);
            await Ready(() => Paths(f, 3) == Paths(f, 1) && f.Values(3) == "Branch name");
            Check(Paths(f, 3) == Paths(f, 1), "Selected set F retains S's stable group path");
            Call(component, "SetOutputValues", PanelOutputValues.Values);
            await Ready(() => f.Values(3) == "Branch name");
            Check(f.Sinks[3].VolatileData.PathCount == 1, "Values mode keeps paired F grouped by set Items paths");
            Call(component, "SetOutputValues", PanelOutputValues.Items); f.Scope(PanelOutputScope.Filtered);
        }
        PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
        f.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        await Ready(() => f.Result.DisplaySet?.IsBranchSet == true && Paths(f, 3) == Paths(f, 0));
        Render();
        string lengthTwo = f.Result.BranchPaths.Single(path => f.Result.Snapshot.BranchItemCounts[path] == 2);
        string lengthSummary = (string)Call(attributes, "PairedBranchValue", lengthTwo)!;
        Check(lengthSummary == "F1-0 · F1-1 · F1-2 · F1-3 … +2 in this set",
            "List-length set summary expands every branch occurrence with a bounded preview");
        Console.WriteLine("Branch-set outputs: D=" + Paths(f, 0) + "; F=" + Paths(f, 3) + "; values=" + f.Values(3));
        Check(f.Result.BranchPaths.Any(path => f.Result.DisplaySet!.BranchOccurrences[path].Contains("{0;3}") &&
            Branch(f, 3, "{" + f.Result.DisplaySet.BranchIndices[path] + ";0;3}") == ""), "List-length sets retain D group paths but exclude empty D partners");
        f.SelectBranch("{0;3}"); f.Scope(PanelOutputScope.Selected);
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 0);
        Check(f.Sinks[3].VolatileData.DataCount == 0, "Even selected list-length sets exclude empty D partners");
        f.ClearSelection(); f.Scope(PanelOutputScope.Filtered);
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions());
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        f.Source.PersistentData.EnsurePath(new GH_Path(0, 0));
        f.Source.PersistentData.Append(new GH_Integer(2), new GH_Path(0, 1));
        f.Paired.PersistentData.Append(new GH_String("empty-partner"), new GH_Path(0, 0));
        f.Paired.PersistentData.Append(new GH_String("b"), new GH_Path(0, 1));
        f.Solve();
        await Ready(() => f.Result.RowPositions.Count == 1);
        PanelTestAdapter.TreeTools(component, 0, true, false, false);
        await Ready(() => f.Result.Snapshot.Rows[0].Path == "{0}");
        await Ready(() => f.Values(3) == "b,empty-partner"); Render();
        var flatView = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        Check(flatView.Count(row => FieldValue<int>(row, "Extra") >= 0) == 1 &&
            FieldValue<string>(Call(PropertyValue<object>(component, "PairedData"), "Heading", "{0}", true)!, "Count") == "2 items",
            "Flatten emits and shows the empty D partner at its merged destination with a matching heading");
        f.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        await Ready(() => f.Values(3) == "b,empty-partner"); Render();
        Check((string)Call(attributes, "PairedBranchValue", "{0}")! == "b \u00b7 empty-partner",
            "Flatten list-length summary exactly matches emitted F membership");
        foreach (var query in new[] { new PanelQueryOptions { BranchPath = "{0}" },
            new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "= 1" } })
        {
            f.Query(query); await Ready(() => f.Values(3) == "b,empty-partner"); Render();
            Check((string)Call(PropertyValue<object>(component, "PairedData"), "HeadingCount", "{0}", true)! == "2 items",
                "Path and list-length restrictions retain merged empty partners when D retains the destination");
        }
        foreach (var query in new[] { new PanelQueryOptions { Search = "= 2" }, new PanelQueryOptions { TypeName = "Integer" },
            new PanelQueryOptions { CreateSet = true },
            new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength } })
        {
            PanelTestAdapter.TreeTools(component, 0, true, false, false, query.CreateSet);
            f.Query(query); await Ready(() => f.Values(3) == "b"); Render();
            Check(FieldValue<string>(Call(PropertyValue<object>(component, "PairedData"), "Heading", "{0}", false)!, "Count") == "1 item",
                "Item filters and either Create set exclude merged empty partners from output and heading totals");
        }
        PanelTestAdapter.TreeTools(component, 0, true, false, false);
        f.Query(new PanelQueryOptions()); await Ready(() => f.Values(3) == "b,empty-partner");
        f.Select(0); f.Scope(PanelOutputScope.Selected);
        await Ready(() => f.Values(3) == "b");
        Check(f.Values(3) == "b", "Selected merged D row never scopes an unrelated empty original branch");
        f.ClearSelection(); f.Scope(PanelOutputScope.Filtered);
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int b = 0; b < 2; b++)
        {
            f.Source.PersistentData.Append(new GH_Integer(b + 1), new GH_Path(0, b));
            string label = b == 0 ? "a" : "b";
            f.Paired.PersistentData.Append(new GH_String(label), new GH_Path(0, b));
            f.Paired.PersistentData.Append(new GH_String(label + "-extra"), new GH_Path(0, b));
        }
        f.Solve();
        f.Query(new PanelQueryOptions { Search = "= 2" });
        await Ready(() => f.Values(3) == "b,b-extra"); Render();
        var filteredView = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        Check(filteredView.Where(row => FieldValue<int>(row, "Extra") >= 0).Select(row => FieldValue<int>(row, "Extra")).SequenceEqual(new[] { 3 }),
            "Merged filtered pane excludes every F value from unrelated original branches");
        f.Query(new PanelQueryOptions { Search = "= 99" });
        await Ready(() => f.Result.RowPositions.Count == 0 && f.Sinks[3].VolatileData.DataCount == 0);
        Check((int)Call(attributes, "ViewItemAtOrAfter", f.Result, -1)! == 0,
            "Empty paired index retains the ordinary successor lookup boundary behavior");
        PanelTestAdapter.TreeTools(component, 0, true, false, false, true);
        f.Query(new PanelQueryOptions { CreateSet = true, Search = "= 2" });
        await Ready(() => f.Result.DisplaySet != null && f.Values(3) == "b,b-extra"); Render();
        Check(!PropertyValue<bool>(attributes, "HasPairView") && (string)Call(attributes, "PairedSetValue", f.Result.RowPositions.Single())! == "b · b-extra",
            "Merged set pane summarizes expanded current members in one cell");
        f.Query(new PanelQueryOptions { CreateSet = true });
        f.Source.PersistentData.Branches[0][0] = new GH_Integer(2); f.Solve();
        await Ready(() => f.Result.DisplaySet?.Members.Values.Single().Count == 2 && f.Values(3) == "a,b,a-extra,b-extra"); Render();
        Check((string)Call(attributes, "PairedSetValue", f.Result.RowPositions.Single())! == "a · b · a-extra · b-extra",
            "One set cell keeps nonrepresentative partners and F-only tails in wire order");
        // A run of extras fills both panes; Down must resume at the following D item.
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions());
        for (int i = 2; i < 40; i++) f.Paired.PersistentData.Append(new GH_String("tail-" + i), new GH_Path(0, 0));
        f.Solve();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 42);
        PanelTestAdapter.Resize(component, 560, 260);
        Call(attributes, "SetPaneOffset", canvas, 4, false); Render();
        f.ClearSelection();
        attributes.GetType().GetField("_keyboardCursor", AnyInstance)!.SetValue(attributes, -1);
        Check(FieldValue<int>(attributes, "_firstShownItem") == -1, "Regression viewport contains only D spacers");
        Call(attributes, "MoveKeyboardCursor", canvas, f.Result, 1, false);
        Check(PanelTestAdapter.SelectedPosition(component) == 1, "Down from a spacer-only D viewport selects the next D item");
        PanelTestAdapter.Resize(component, 560, 650);
        f.ClearSelection();
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int b = 0; b < 12; b++)
        {
            f.Source.PersistentData.Append(new GH_Integer(b), new GH_Path(0, b));
            f.Paired.PersistentData.Append(new GH_String("first"), new GH_Path(1, b));
            f.Paired.PersistentData.Append(new GH_String("extra"), new GH_Path(1, b));
        }
        PanelTestAdapter.TreeTools(component, 0, true, false, false); f.Solve();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 24); Render();
        var headings = FieldValue<IDictionary>(PropertyValue<object>(component, "PairedData"), "Headings");
        var heading = headings["{0}"]!;
        Check(FieldValue<string>(heading, "Count") == "24 items" && FieldValue<string>(heading, "DataCount") == "D 12" &&
            FieldValue<bool>(heading, "Unequal") && FieldValue<bool>(heading, "ByOrder") &&
            FieldValue<string>(heading, "Paths") == "{1;0}, {1;1}, {1;2} …",
            "Merged heading caches counts, flags and at most three F paths");
        PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
        f.Query(new PanelQueryOptions { CreateSet = true });
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        f.Source.PersistentData.Append(new GH_Integer(1), new GH_Path(0, 0));
        f.Source.PersistentData.Append(new GH_Integer(2), new GH_Path(0, 0));
        f.Source.PersistentData.EnsurePath(new GH_Path(0, 1));
        f.Paired.PersistentData.Append(new GH_String("x"), new GH_Path(0, 0));
        f.Paired.PersistentData.Append(new GH_String("y"), new GH_Path(0, 0));
        f.Paired.PersistentData.Append(new GH_String("z"), new GH_Path(0, 1));
        f.Solve(); await Ready(() => f.Values(3) == "x,y"); Render();
        Check(!((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Any(row => FieldValue<int>(row, "Extra") == 2),
            "Item Create set never shows or emits the partner of an empty D branch");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength, TypeName = "Integer" });
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        f.Source.PersistentData.Append(new GH_Integer(1), new GH_Path(0));
        f.Source.PersistentData.Append(new GH_String("hidden-D"), new GH_Path(0));
        f.Paired.PersistentData.Append(new GH_String("visible-F"), new GH_Path(0));
        f.Paired.PersistentData.Append(new GH_String("hidden-F"), new GH_Path(0));
        f.Solve(); await Ready(() => f.Values(3) == "visible-F"); Render();
        Check((string)Call(attributes, "PairedBranchValue", "{0}")! == "visible-F",
            "Equal branch list-length summary uses current D item membership");
        Check(!PropertyValue<bool>(attributes, "HasPairView") && (int)Call(attributes, "ViewCount", f.Result)! == f.Result.ViewIndex.Count &&
            FieldValue<IList>(attributes, "_pairView").Count == 0,
            "Equal branch pairs use ViewIndex directly without allocating augmented rows");
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int i = 0; i < 4; i++) f.Source.PersistentData.Append(new GH_Integer(i), new GH_Path(0));
        for (int i = 0; i < 6; i++) f.Paired.PersistentData.Append(new GH_String("f" + i), new GH_Path(0));
        PanelTestAdapter.TreeTools(component, 0, false, true, false);
        f.Query(new PanelQueryOptions { Search = "= 1" }); f.Solve();
        await Ready(() => f.Values(3) == "f3,f2,f1,f0,f4,f5");
        Call(attributes, "SetPaneOffset", canvas, 0, false); Render("uneven-hidden-reverse");
        var reversedView = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        var rawF = PropertyValue<PanelSnapshot>(component, "PairedSnapshot");
        string paneValues = string.Join(",", reversedView.Where(row => FieldValue<int>(row, "Extra") >= 0 || FieldValue<int>(row, "Item") >= 0)
            .Select(row => FieldValue<int>(row, "Extra") >= 0 ? rawF.Rows[FieldValue<int>(row, "Extra")].StringValue
                : ((PanelRow)Call(component, "PairedRow", f.Result.RowPositions[FieldValue<int>(row, "Item")])!).StringValue));
        Check(paneValues == f.Values(3), "Reverse plus filter keeps every F pane row in exact wire order f3,f2,f1,f0,f4,f5");
        Check(reversedView.Where(row => FieldValue<int>(row, "Extra") >= 0 || FieldValue<int>(row, "Item") >= 0)
            .Select(row => FieldValue<int>(row, "Extra") >= 0 ? (rawF.Rows[FieldValue<int>(row, "Extra")].OriginalIndex, false)
                : ((int, bool))Call(attributes, "RowAddress", f.Result, f.Result.RowPositions[FieldValue<int>(row, "Item")], true)!)
            .SequenceEqual(new[] { (3, false), (2, false), (1, false), (0, false), (4, false), (5, false) }),
            "Reverse plus filter shows F's own original index for every unequal partner, hidden partner and F-only row");
        Check(reversedView.Where(row => FieldValue<string>(row, "Label") == "paired with hidden D rows")
            .Select(row => FieldValue<int>(row, "Extra")).SequenceEqual(new[] { 3, 0 }),
            "Reverse plus filter labels the first row of each hidden-D run at f3 and f0, leaving f2 unlabelled");
        Check(reversedView.Where(row => FieldValue<int>(row, "Extra") >= 0 && FieldValue<string>(row, "Label").Contains("only in F"))
            .Select(row => FieldValue<int>(row, "Extra")).SequenceEqual(new[] { 4 }) &&
            reversedView.Any(row => FieldValue<int>(row, "Extra") == 3 && FieldValue<string>(row, "Label") == "paired with hidden D rows") &&
            reversedView.Any(row => FieldValue<int>(row, "Extra") == 4 && FieldValue<string>(row, "Label") == "+2 only in F"),
            "Only f4/f5 are F-only tails; f3/f2/f0 retain the separate hidden-D note");
        f.Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        await Ready(() => f.Result.RowPositions.Count == 4); Render();
        Check(((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Where(row => FieldValue<int>(row, "Item") >= 0)
            .Select(row => FieldValue<int>(row, "Item")).SequenceEqual(Enumerable.Range(0, 4)),
            "Unequal pane augmentation retains D's sorted view order");

        // Every item-set cell previews exactly its wire group, independently of other branches.
        var savedClipboard = Forms.Clipboard.GetDataObject();
        try
        {
            foreach (string scenario in new[] { "all-equal", "mixed", "unequal-three", "bounded" })
            {
                PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
                f.Query(new PanelQueryOptions { CreateSet = true });
                f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
                int branches = scenario == "all-equal" || scenario == "mixed" ? 2 : 1;
                for (int b = 0; b < branches; b++)
                {
                    var path = new GH_Path(0, b);
                    f.Source.PersistentData.Append(new GH_Integer(b + 1), path);
                    f.Source.PersistentData.Append(new GH_Integer(scenario == "unequal-three" || scenario == "bounded" ? 2 : b + 1), path);
                    int count = scenario == "bounded" ? 12 : scenario == "unequal-three" || scenario == "mixed" && b == 1 ? 3 : 2;
                    for (int i = 0; i < count; i++) f.Paired.PersistentData.Append(new GH_String("f" + b + "-" + i), path);
                }
                f.Solve(); await Ready(() => f.Result.DisplaySet != null && f.Sinks[3].VolatileData.DataCount > 0);
                Render(scenario == "unequal-three" ? "uneven-create-set" : null);
                Check(!PropertyValue<bool>(attributes, "HasPairView") && (int)Call(attributes, "ViewCount", f.Result)! == f.Result.ViewIndex.Count &&
                    !((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Any(),
                    scenario + ": Create set has one pane row per set and no hidden/F-only rows");
                long outputVisits = 0;
                Call(PropertyValue<object>(component, "PairedData"), "Output", f.Result, f.Result.RowPositions, f.Result.BranchPaths,
                    false, false, (Action<long>)(visits => outputVisits += visits), false);
                Check(outputVisits >= 2L * f.Sinks[3].VolatileData.DataCount + f.Result.DisplaySet!.Members.Values.Sum(members => members.Count),
                    scenario + ": output visit accounting includes every repeated F emission and D group member");
                foreach (string dataPath in f.Result.BranchPaths)
                {
                    string prefix = dataPath.TrimEnd('}') + ";";
                    int count = f.Sinks[3].VolatileData.Paths.Select((path, b) => (path, b)).Where(value => value.path.ToString().StartsWith(prefix, StringComparison.Ordinal))
                        .Sum(value => f.Sinks[3].VolatileData.get_Branch(value.b).Count);
                    Check((string)Call(PropertyValue<object>(component, "PairedData"), "HeadingCount", dataPath, false)! == count + (count == 1 ? " item" : " items"),
                        scenario + ": F set heading counts all emitted group values");
                }
                foreach (int position in f.Result.RowPositions)
                {
                    int k = f.Result.DisplaySet!.MemberIndices[position];
                    string path = f.Result.Snapshot.Rows[position].Path.TrimEnd('}') + ";" + k + "}";
                    string[] emitted = Branch(f, 3, path).Split(',');
                    string expected = string.Join(" · ", emitted.Take(4)) + (emitted.Length > 4 ? " … +" + (emitted.Length - 4) + " in this set" : "");
                    Check((string)Call(attributes, "PairedSetValue", position)! == expected,
                        scenario + ": bounded F cell equals emitted group in output order at " + path);
                    f.ClearSelection(); f.Select(position);
                    await Ready(() => FieldValue<long>(component, "_publishedSelectionVersion") == FieldValue<long>(component, "_selectionVersion"));
                    string copyStatus = await (Task<string>)Call(component, "CopyPairedValuesAsync", f.Result.Snapshot, new[] { position })!;
                    await f.Ready(() => Forms.Clipboard.GetText() == string.Join(Environment.NewLine, emitted));
                    Check(Forms.Clipboard.GetText() == string.Join(Environment.NewLine, emitted),
                        scenario + ": copying an F set cell includes every emitted group member exactly once (" + copyStatus + ")");
                }
            }
            // Branch-set copy uses the same output order as the list-length summary.
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            f.Query(new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
            await Ready(() => f.Result.DisplaySet?.IsBranchSet == true);
            string representative = f.Result.BranchPaths.Single();
            f.ClearSelection(); f.SelectBranch(representative);
            await Ready(() => FieldValue<long>(component, "_publishedSelectionVersion") == FieldValue<long>(component, "_selectionVersion"));
            await (Task<string>)Call(component, "CopyPairedValuesAsync", f.Result.Snapshot, Array.Empty<int>())!;
            await f.Ready(() => Forms.Clipboard.GetText() == string.Join(Environment.NewLine, f.Values(3).Split(',')));
            Check(Forms.Clipboard.GetText() == string.Join(Environment.NewLine, f.Values(3).Split(',')),
                "List-length F set copying includes its full emitted group");
            Render();
            Check((string)Call(attributes, "PairedBranchValue", representative)! == "f0-0 · f0-1 · f0-2 · f0-3 … +8 in this set",
                "List-length sets use the same bounded F summary rule as item sets");
            var branchPane = PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List");
            var branchHit = FieldValue<IEnumerable>(attributes, "_branchHits").Cast<object>().Single(hit =>
                PropertyValue<string>(hit, "Path") == representative && branchPane.Contains(PropertyValue<RectangleF>(hit, "Bounds")));
            var branchBounds = PropertyValue<RectangleF>(branchHit, "Bounds");
            PanelTestAdapter.CanvasClick(canvas, new PointF(branchBounds.X + 120, branchBounds.Y + 8));
            await Ready(() => FieldValue<long>(component, "_publishedSelectionVersion") == FieldValue<long>(component, "_selectionVersion"));
            Forms.Clipboard.SetText("before F set copy");
            bool copied = (bool)input.GetMethod("HandleKey")!.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!;
            await f.Ready(() => Forms.Clipboard.GetText() == string.Join(Environment.NewLine, f.Values(3).Split(',')));
            Check(copied, "Ctrl+C on a selected F list-length set copies its complete emitted group");
        }
        finally { if (savedClipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(savedClipboard, true); }

        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions()); f.ClearSelection();
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        f.Source.PersistentData.EnsurePath(new GH_Path(0));
        f.Source.PersistentData.Append(new GH_Integer(1), new GH_Path(1));
        f.Paired.PersistentData.Append(new GH_String("included"), new GH_Path(1));
        f.Paired.PersistentData.Append(new GH_String("excluded-empty"), new GH_Path(9));
        f.Solve(); await Ready(() => f.Values(3) == "excluded-empty,included");
        var emptyHeading = Call(PropertyValue<object>(component, "PairedData"), "Heading", "{0}", false)!;
        Check(FieldValue<string>(emptyHeading, "Count") == "0 items" && !FieldValue<bool>(emptyHeading, "Unequal") &&
            !FieldValue<bool>(emptyHeading, "ByOrder") && FieldValue<string>(emptyHeading, "Paths") == "",
            "An excluded empty-only partner has no amber D 0 badge or order-paired path");
        PanelTestAdapter.TreeTools(component, 0, true, false, false);
        f.Query(new PanelQueryOptions { TypeName = "Integer" });
        await Ready(() => f.Values(3) == "included"); Render();
        var scopedHeading = Call(PropertyValue<object>(component, "PairedData"), "Heading", "{0}", false)!;
        Check(FieldValue<string>(scopedHeading, "Count") == "1 item" && FieldValue<string>(scopedHeading, "DataCount") == "D 1" &&
            !FieldValue<bool>(scopedHeading, "Unequal") && !FieldValue<bool>(scopedHeading, "ByOrder") && FieldValue<string>(scopedHeading, "Paths") == "{1}",
            "Excluded empty partner contributes no count, amber badge, order flag or F path to a merged heading");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "= 0", BranchPath = "{0}" });
        await Ready(() => f.Values(3) == "excluded-empty" && f.Result.BranchPaths.Count == 1);
        Check(Branch(f, 0, "{0}") == "", "Path and list-length filters keep an empty D branch and its F partner together");

        // Mixed equal/unequal numbering uses raw F indices and one sorting policy.
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int b = 0; b < 2; b++)
        {
            for (int i = 0; i < 3; i++) f.Source.PersistentData.Append(new GH_Integer(2 - i), new GH_Path(b));
            for (int i = 0; i < (b == 0 ? 3 : 2); i++) f.Paired.PersistentData.Append(new GH_String("f" + b + "-" + i), new GH_Path(b));
        }
        PanelTestAdapter.TreeTools(component, 0, false, true, false);
        f.Query(new PanelQueryOptions()); f.Solve(); await Ready(() => f.Result.RowPositions.Count == 6);
        foreach (var sort in new[] { PanelSortMode.SourceOrder, PanelSortMode.ValueAscending })
        {
            f.Query(new PanelQueryOptions { SortMode = sort });
            await f.Ready(() => PropertyValue<PanelQueryOptions>(f.Session, "Options").SortMode == sort);
            Render();
            foreach (int position in f.Result.RowPositions)
            {
                if (Call(component, "PairedRow", position) is not PanelRow pairedRow) continue;
                Check(((int, bool))Call(attributes, "RowAddress", f.Result, position, true)! == (pairedRow.OriginalIndex, sort != PanelSortMode.SourceOrder),
                    "Equal and unequal F gutters retain raw original indices with the same sorted flag");
                var bounds = PaneRow(position, true);
                var point = new PointF(bounds.X + 80, bounds.Y + 8);
                var tooltip = (Grasshopper.GUI.GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(Grasshopper.GUI.GH_TooltipDisplayEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
                attributes.SetupTooltip(point, tooltip);
                Check(tooltip.Title.Contains("[" + pairedRow.OriginalIndex + "]"), "F tooltip index agrees with its gutter");
            }
        }
        // A missing F cell still precedes a hidden partner at its reversed D position.
        f.Query(new PanelQueryOptions { Search = "!= 1" });
        await Ready(() => f.Result.RowPositions.Count == 4); Render("reverse-filter-placement");
        var reversePlacement = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>()
            .Where(row => FieldValue<string>(row, "Path") == "{1}").ToArray();
        Check(FieldValue<int>(reversePlacement[0], "Item") >= 0 && FieldValue<int>(reversePlacement[1], "Extra") == 4 && FieldValue<int>(reversePlacement[2], "Item") >= 0,
            "Reverse inserts hidden f1 between the missing-F a2 cell and a0, preserving F output order");
        f.Query(new PanelQueryOptions());
        PanelTestAdapter.TreeTools(component, 0, true, false, false);
        await Ready(() => f.Result.Snapshot.Branches.Count == 1 && f.Values(3) == "f0-0,f0-1,f0-2,f1-0,f1-1");
        Render("mixed-flatten");
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int b = 0; b < 2; b++)
        {
            f.Source.PersistentData.Append(new GH_Integer(0), new GH_Path(b));
            f.Source.PersistentData.Append(new GH_Integer(1), new GH_Path(b));
            f.Paired.PersistentData.EnsurePath(new GH_Path(b));
        }
        f.Paired.PersistentData.Append(new GH_String("b0"), new GH_Path(1));
        PanelTestAdapter.TreeTools(component, 0, true, true, false);
        f.Query(new PanelQueryOptions { Search = "> 0" }); f.Solve();
        await Ready(() => f.Result.RowPositions.Count == 2 && f.Values(3) == "b0"); Render();
        var splitMissing = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        Check(splitMissing.Length == 3 && FieldValue<int>(splitMissing[0], "Item") >= 0 &&
            FieldValue<int>(splitMissing[1], "Extra") == 0 && FieldValue<int>(splitMissing[2], "Item") >= 0 &&
            splitMissing.Select(row => FieldValue<string>(row, "Label")).SequenceEqual(new[]
                { "no F item", "paired with hidden D rows", "no F item" }),
            "Flatten plus Reverse labels each missing-F run separated by a hidden partner independently");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int i = 0; i < 12; i++)
        {
            f.Source.PersistentData.Append(new GH_Integer(i), new GH_Path(0));
            f.Paired.PersistentData.Append(new GH_String("f" + i), new GH_Path(0));
        }
        f.Query(new PanelQueryOptions { Search = "< 3" }); f.Solve();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 3); Render();
        Check((string)Call(PropertyValue<object>(component, "PairedData"), "HeadingCount", "{0}", false)! == "3 of 12 items",
            "Filtered F heading reports matching items against its original F count");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions()); Seed();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 7);
        f.Paired.PersistentData.EnsurePath(new GH_Path(9)); f.Solve();
        await Ready(() => PropertyValue<string?>(component, "PairedError") != null);
        Render("branch-count-mismatch");
        Check(PropertyValue<string>(component, "PairedError") == "F has 4 branches, D has 3." &&
            !PropertyValue<bool>(component, "CanSplit") && f.Sinks[3].VolatileData.PathCount == 0,
            "Only branch count mismatch blocks pairing, clears F output and publishes the port warning");
    }

    private static async Task VerifySummaryFreshness(Assembly assembly)
    {
        using var f = new PanelHostFixture.Fixture(assembly);
        var component = f.Component;
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int i = 0; i < 2; i++)
        {
            f.Source.PersistentData.Append(new GH_Integer(i), new GH_Path(0));
            f.Paired.PersistentData.Append(new GH_String("f" + i), new GH_Path(0));
        }
        PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
        f.Query(new PanelQueryOptions { CreateSet = true }); f.Solve();
        await f.Ready(() => f.Values(3) == "f0,f1" && PropertyValue<bool>(component, "PairedSummaryCurrent"));
        var pairing = PropertyValue<object>(component, "PairedData");
        var oldSummary = PropertyValue<PanelQueryResult>(pairing, "SummaryResult");
        Check(f.Result.RowPositions.Count == 2 && (string)Call(pairing, "HeadingCount", "{0}", false)! == "2 items",
            "Freshness regression starts with two item sets and a published F summary");
        bool? currentBeforeRebuild = null;
        var changed = f.Session.GetType().GetEvent("ResultChanged")!;
        EventHandler onResult = (_, __) => currentBeforeRebuild = PropertyValue<bool>(component, "PairedSummaryCurrent");
        changed.AddEventHandler(f.Session, onResult);
        try
        {
            f.Query(new PanelQueryOptions { CreateSet = true, SearchMode = PanelSearchMode.Regex, Search = "^1$" });
            Check(PropertyValue<bool>(f.Session, "IsBusy"), "Regex query remains asynchronous before its UI callback");
            // Exercise a busy publication without committing trees through the final fence.
            Call(component, "PublishOutputs", null!, (Func<bool>)(() => false));
            Check(FieldValue<PanelQueryOptions>(component, "_outputOptions").Search == "^1$" &&
                ReferenceEquals(oldSummary, PropertyValue<PanelQueryResult>(pairing, "SummaryResult")) &&
                !PropertyValue<bool>(component, "PairedSummaryCurrent"),
                "Busy publication updates output options but leaves the old F summaries stale");
            await f.Ready(() => f.Values(3) == "f1" && PropertyValue<bool>(component, "PairedSummaryCurrent"));
        }
        finally { changed.RemoveEventHandler(f.Session, onResult); }
        Check(currentBeforeRebuild == false && f.Result.RowPositions.Count == 1 &&
            (string)Call(pairing, "HeadingCount", "{0}", false)! == "1 of 2 items",
            "Completed regex keeps the old heading pending until summaries rebuild to one of two items");
        var filteredSummary = PropertyValue<PanelQueryResult>(pairing, "SummaryResult");
        f.Query(new PanelQueryOptions { CreateSet = true, SearchMode = PanelSearchMode.Regex, Search = "^1$",
            SortMode = PanelSortMode.ValueDescending });
        await f.Ready(() => !ReferenceEquals(filteredSummary, f.Result));
        Check(PropertyValue<bool>(component, "PairedSummaryCurrent") &&
            ReferenceEquals(filteredSummary, PropertyValue<PanelQueryResult>(pairing, "SummaryResult")),
            "Sort-only results reuse published F summaries with unchanged membership");
    }
}
