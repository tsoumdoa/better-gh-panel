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
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
    private static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, args);
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static string Paths(PanelHostFixture.Fixture f, int port) => string.Join(",", f.Sinks[port].VolatileData.Paths);
    private static string Branch(PanelHostFixture.Fixture f, int port, string path)
    {
        var tree = f.Sinks[port].VolatileData;
        int b = tree.Paths.ToList().FindIndex(candidate => candidate.ToString() == path);
        return b < 0 ? "<missing>" : string.Join(",", tree.get_Branch(b).Cast<IGH_Goo>().Select(value => value?.ToString() ?? "<null>"));
    }
    private static async Task Verify(Assembly assembly)
    {
        using var f = new PanelHostFixture.Fixture(assembly);
        var component = f.Component; var attributes = component.Attributes;
        Task Ready(Func<bool> condition) => f.Ready(() =>
            ReferenceEquals(Field<PanelQueryResult?>(component, "_cachedOutputResult"), f.Result) && condition());
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
            var pane = Field<RectangleF>(attributes, paired ? "_pairedList" : "_list");
            foreach (var hit in Field<IEnumerable>(attributes, "_rowHits"))
                if (Property<int>(hit!, "SourcePosition") == position && pane.Contains(Property<RectangleF>(hit!, "Bounds")))
                    return Property<RectangleF>(hit!, "Bounds");
            throw new InvalidOperationException("Expected visible paired row " + position);
        }
        Render();
        f.Select(3); await Ready(() => f.Sinks[1].VolatileData.DataCount == 1);
        Render("uneven-split");
        var display = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        var extraRows = display.Where(row => Field<int>(row, "Extra") >= 0).ToArray();
        Check(display.Any(row => Field<string>(row, "Label") == "no F items (11 rows)"),
            "Missing runs use a row count, never a potentially reversed index range");
        Check(extraRows.Length == 2 && Field<string>(extraRows[0], "Label") == "+2 only in F" &&
            Field<string>(extraRows[1], "Label") == "" && Property<object>(component, "PairedSnapshot") is PanelSnapshot pairedSnapshot &&
            pairedSnapshot.Rows[Field<int>(extraRows[0], "Extra")].OriginalIndex == 2,
            "F extras retain raw item indices and keep the first-row note separate from data");
        bool offsetsAgree = true;
        int successor = f.Result.RowPositions.Count;
        for (int offset = display.Length - 1; offset >= 0; offset--)
        {
            int item = Field<int>(display[offset], "Item");
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
        Check(hits == 32 && Field<int>(attributes, "_shown") == 18, "F-only rows and D spacers have no selection hits");
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
            Check(status == "3 values copied" && Forms.Clipboard.GetText() == "Branch name" + Environment.NewLine + Environment.NewLine + "F2-0",
                "F copying uses raw mapped values and an empty line for a missing F item");
        }
        finally { if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true); }
        // Uneven rows share selection coordinates but F-only rows never enter navigation.
        PanelTestAdapter.Resize(component, 560, 260); Render();
        var link = Field<RectangleF>(attributes, "_scrollLock");
        PanelTestAdapter.CanvasClick(canvas, new PointF(link.X + 8, link.Y + 7)); Render();
        Call(attributes, "SetPaneOffset", canvas, 13, false); Render();
        var row13 = PaneRow(13, false);
        PanelTestAdapter.CanvasClick(canvas, new PointF(row13.X + 90, row13.Y + 8)); Render();
        Check(PaneRow(13, true).Y >= Field<RectangleF>(attributes, "_pairedList").Y,
            "Selecting D in an unlinked uneven split reveals its F partner");
        var input = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!;
        input.GetMethod("HandleKey")!.Invoke(attributes, new object[] { canvas, Forms.Keys.Down }); Render();
        Check(PanelTestAdapter.SelectedPosition(component) == 14 && !PaneRow(14, true).IsEmpty,
            "Keyboard navigation skips F extras and D spacers while revealing the next paired D row");
        int dataOffset = PanelTestAdapter.Offset(component);
        input.GetMethod("HandleWheel")!.Invoke(attributes, new object[] { canvas, Field<RectangleF>(attributes, "_pairedList").Location, 240 }); Render();
        Check(PanelTestAdapter.Offset(component) == dataOffset && Field<int>(attributes, "_pairedOffset") < dataOffset,
            "Unlinked F wheel scrolling leaves D's augmented row offset unchanged");
        PanelTestAdapter.CanvasClick(canvas, new PointF(link.X + 8, link.Y + 7)); Render();
        Check(Field<int>(attributes, "_pairedOffset") == PanelTestAdapter.Offset(component), "Relinking uneven panes snaps their augmented scroll offsets together");
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
        Check(Property<string>(Property<object>(component, "PairedData"), "Caption") ==
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
        Check(lengthSummary.Contains("F1-0") && lengthSummary.Contains("F1-3") && lengthSummary.EndsWith(" …"),
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
        Check(flatView.Count(row => Field<int>(row, "Extra") >= 0) == 1 &&
            Field<string>(Call(Property<object>(component, "PairedData"), "Heading", "{0}", true)!, "Count") == "2 items",
            "Flatten emits and shows the empty D partner at its merged destination with a matching heading");
        f.Query(new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        await Ready(() => f.Values(3) == "b,empty-partner"); Render();
        Check((string)Call(attributes, "PairedBranchValue", "{0}")! == "b \u00b7 empty-partner",
            "Flatten list-length summary exactly matches emitted F membership");
        foreach (var query in new[] { new PanelQueryOptions { Search = "= 2" }, new PanelQueryOptions { TypeName = "Integer" },
            new PanelQueryOptions { BranchPath = "{0}" }, new PanelQueryOptions { CreateSet = true },
            new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength } })
        {
            PanelTestAdapter.TreeTools(component, 0, true, false, false, query.CreateSet);
            f.Query(query); await Ready(() => f.Values(3) == "b"); Render();
            Check(Field<string>(Call(Property<object>(component, "PairedData"), "Heading", "{0}", false)!, "Count") == "1 item",
                "Every filter and either Create set exclude merged empty partners from output and heading totals");
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
        Check(filteredView.Where(row => Field<int>(row, "Extra") >= 0).Select(row => Field<int>(row, "Extra")).SequenceEqual(new[] { 3 }),
            "Merged filtered pane excludes every F value from unrelated original branches");
        f.Query(new PanelQueryOptions { Search = "= 99" });
        await Ready(() => f.Result.RowPositions.Count == 0 && f.Sinks[3].VolatileData.DataCount == 0);
        Check((int)Call(attributes, "ViewItemAtOrAfter", f.Result, -1)! == 0,
            "Empty paired index retains the ordinary successor lookup boundary behavior");
        PanelTestAdapter.TreeTools(component, 0, true, false, false, true);
        f.Query(new PanelQueryOptions { CreateSet = true, Search = "= 2" });
        await Ready(() => f.Result.DisplaySet != null && f.Values(3) == "b,b-extra"); Render();
        Check(((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Count(row => Field<int>(row, "Extra") >= 0) == 1,
            "Merged set pane derives F extras from expanded current members");
        f.Query(new PanelQueryOptions { CreateSet = true });
        f.Source.PersistentData.Branches[0][0] = new GH_Integer(2); f.Solve();
        await Ready(() => f.Result.DisplaySet?.Members.Values.Single().Count == 2 && f.Values(3) == "a,b,a-extra,b-extra"); Render();
        Check(((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Where(row => Field<int>(row, "Extra") >= 0)
            .Select(row => Field<int>(row, "Extra")).SequenceEqual(new[] { 2, 1, 3 }),
            "Set pane keeps nonrepresentative partners before F-only tails in wire order");
        // A run of extras fills both panes; Down must resume at the following D item.
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions());
        for (int i = 2; i < 40; i++) f.Paired.PersistentData.Append(new GH_String("tail-" + i), new GH_Path(0, 0));
        f.Solve();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 42);
        PanelTestAdapter.Resize(component, 560, 260);
        Call(attributes, "SetPaneOffset", canvas, 4, false); Render();
        f.ClearSelection();
        attributes.GetType().GetField("_keyboardCursor", Instance)!.SetValue(attributes, -1);
        Check(Field<int>(attributes, "_firstShownItem") == -1, "Regression viewport contains only D spacers");
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
        var headings = Field<IDictionary>(Property<object>(component, "PairedData"), "Headings");
        var heading = headings["{0}"]!;
        Check(Field<string>(heading, "Count") == "24 items" && Field<string>(heading, "DataCount") == "D 12" &&
            Field<bool>(heading, "Unequal") && Field<bool>(heading, "ByOrder") &&
            Field<string>(heading, "Paths") == "{1;0}, {1;1}, {1;2} …",
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
        Check(!((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Any(row => Field<int>(row, "Extra") == 2),
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
        Check(!Property<bool>(attributes, "HasPairView") && (int)Call(attributes, "ViewCount", f.Result)! == f.Result.ViewIndex.Count &&
            Field<IList>(attributes, "_pairView").Count == 0,
            "Equal branch pairs use ViewIndex directly without allocating augmented rows");
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int i = 0; i < 4; i++) f.Source.PersistentData.Append(new GH_Integer(i), new GH_Path(0));
        for (int i = 0; i < 6; i++) f.Paired.PersistentData.Append(new GH_String("f" + i), new GH_Path(0));
        PanelTestAdapter.TreeTools(component, 0, false, true, false);
        f.Query(new PanelQueryOptions { Search = "= 1" }); f.Solve();
        await Ready(() => f.Values(3) == "f3,f2,f1,f0,f4,f5");
        Call(attributes, "SetPaneOffset", canvas, 0, false); Render("uneven-hidden-reverse");
        var reversedView = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
        var rawF = Property<PanelSnapshot>(component, "PairedSnapshot");
        string paneValues = string.Join(",", reversedView.Where(row => Field<int>(row, "Extra") >= 0 || Field<int>(row, "Item") >= 0)
            .Select(row => Field<int>(row, "Extra") >= 0 ? rawF.Rows[Field<int>(row, "Extra")].StringValue
                : ((PanelRow)Call(component, "PairedRow", f.Result.RowPositions[Field<int>(row, "Item")])!).StringValue));
        Check(paneValues == f.Values(3), "Reverse plus filter keeps every F pane row in exact wire order f3,f2,f1,f0,f4,f5");
        Check(reversedView.Where(row => Field<int>(row, "Extra") >= 0 || Field<int>(row, "Item") >= 0)
            .Select(row => Field<int>(row, "Extra") >= 0 ? (rawF.Rows[Field<int>(row, "Extra")].OriginalIndex, false)
                : ((int, bool))Call(attributes, "RowAddress", f.Result, f.Result.RowPositions[Field<int>(row, "Item")], true)!)
            .SequenceEqual(new[] { (3, false), (2, false), (1, false), (0, false), (4, false), (5, false) }),
            "Reverse plus filter shows F's own original index for every unequal partner, hidden partner and F-only row");
        Check(reversedView.Where(row => Field<string>(row, "Label") == "paired with hidden D rows")
            .Select(row => Field<int>(row, "Extra")).SequenceEqual(new[] { 3, 0 }),
            "Reverse plus filter labels the first row of each hidden-D run at f3 and f0, leaving f2 unlabelled");
        Check(reversedView.Where(row => Field<int>(row, "Extra") >= 0 && Field<string>(row, "Label").Contains("only in F"))
            .Select(row => Field<int>(row, "Extra")).SequenceEqual(new[] { 4 }) &&
            reversedView.Any(row => Field<int>(row, "Extra") == 3 && Field<string>(row, "Label") == "paired with hidden D rows") &&
            reversedView.Any(row => Field<int>(row, "Extra") == 4 && Field<string>(row, "Label") == "+2 only in F"),
            "Only f4/f5 are F-only tails; f3/f2/f0 retain the separate hidden-D note");
        f.Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        await Ready(() => f.Result.RowPositions.Count == 4); Render();
        Check(((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().Where(row => Field<int>(row, "Item") >= 0)
            .Select(row => Field<int>(row, "Item")).SequenceEqual(Enumerable.Range(0, 4)),
            "Unequal pane augmentation retains D's sorted view order");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        f.Query(new PanelQueryOptions()); Seed();
        await Ready(() => f.Sinks[3].VolatileData.DataCount == 7);
        f.Paired.PersistentData.EnsurePath(new GH_Path(9)); f.Solve();
        await Ready(() => Property<string?>(component, "PairedError") != null);
        Render("branch-count-mismatch");
        Check(Property<string>(component, "PairedError") == "F has 4 branches, D has 3." &&
            !Property<bool>(component, "CanSplit") && f.Sinks[3].VolatileData.PathCount == 0,
            "Only branch count mismatch blocks pairing, clears F output and publishes the port warning");
    }
}
