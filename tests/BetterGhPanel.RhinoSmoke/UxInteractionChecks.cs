using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static class UxInteractionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
    private static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Instance, null, args.Select(value => value!.GetType()).ToArray(), null)!.Invoke(owner, args);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static PanelQueryOptions Options(PanelHostFixture.Fixture fixture) =>
        (PanelQueryOptions)fixture.Session.GetType().GetProperty("Options")!.GetValue(fixture.Session)!;

    private static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        foreach (int i in Enumerable.Range(0, 80))
        {
            fixture.Source.PersistentData.Append(new GH_Number(i), new GH_Path(0));
            fixture.Paired.PersistentData.Append(new GH_String("F raw " + i), new GH_Path(0));
        }
        fixture.Solve();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80);
        var component = fixture.Component;
        var attributes = component.Attributes;
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1000, 700) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1; canvas.Viewport.ComputeProjection();
        attributes.PerformLayout();
        void Render()
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        CanvasEditorAdapter Search()
        {
            Call(attributes, "EditSearch", canvas);
            return CanvasEditorAdapter.Active(assembly);
        }
        void SeedRedo()
        {
            fixture.Document.UndoServer.Clear();
            var before = Options(fixture);
            var after = before.Clone(); after.Search = "seed";
            Call(component, "RecordQueryUndo", "Seed redo", before, after);
            fixture.Document.UndoServer.PerformUndo();
            Check(fixture.Document.UndoServer.RedoCount == 1, "The fixture starts with an existing redo record");
        }
        async Task Preview(CanvasEditorAdapter editor, string text, int matches)
        {
            editor.Type(text);
            await fixture.Ready(() => fixture.Result.RowPositions.Count == matches);
            Check((bool)component.GetType().GetProperty("QueryPreviewActive", Instance)!.GetValue(component)!,
                "Debounced host textbox input starts a live query preview");
        }

        foreach (bool asynchronous in new[] { false, true })
        {
            fixture.Query(new PanelQueryOptions { SearchMode = asynchronous ? PanelSearchMode.Regex : PanelSearchMode.Text,
                Search = asynchronous ? ".*" : "" });
            await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80 &&
                ReferenceEquals(Field<PanelQueryResult>(component, "_cachedOutputResult"), fixture.Result));
            SeedRedo();
            fixture.Select(0);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 &&
                Field<long>(component, "_publishedSelectionVersion") == Field<long>(component, "_selectionVersion"));
            Call(attributes, "SetOffset", canvas, 18);
            Render();
            int solutions = fixture.Solutions;
            fixture.Document.IsModified = false;
            var editor = Search();
            await Preview(editor, asynchronous ? "7[0-9]" : "> 50", asynchronous ? 10 : 29);
            var chunk = new GH_IO.Serialization.GH_LooseChunk("Panel");
            Check(component.Write(chunk) && chunk.GetString("PanelSearch") == (asynchronous ? ".*" : ""),
                "Saving during preview serializes the committed query");
            Console.WriteLine($"Preview state: async={asynchronous}, undo={fixture.Document.UndoServer.UndoCount}, " +
                $"redo={fixture.Document.UndoServer.RedoCount}, modified={fixture.Document.IsModified}, solutions={fixture.Solutions}/{solutions}, S={fixture.Values(1)}");
            Check(fixture.Document.UndoServer.RedoCount == 1 && fixture.Document.UndoServer.UndoCount == 0 &&
                !fixture.Document.IsModified && fixture.Solutions == solutions && fixture.Values(1) == "0",
                "Preview leaves undo/redo, document modification, selected output and downstream solutions unchanged");
            editor.Cancel();
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80 && fixture.SelectedCount == 1);
            Render();
            Check(PanelTestAdapter.SelectedPosition(component) == 0 && PanelTestAdapter.Offset(component) == 18 &&
                fixture.Values(1) == "0" && fixture.Document.UndoServer.RedoCount == 1 &&
                fixture.Document.UndoServer.UndoCount == 0 && !fixture.Document.IsModified && fixture.Solutions == solutions,
                "Esc restores query, selection and scroll without touching outputs or history, including asynchronous queries");

            editor = Search();
            await Preview(editor, asynchronous ? "7[0-9]" : "> 50", asynchronous ? 10 : 29);
            editor.Submit(asynchronous ? ".*" : "");
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80 && fixture.SelectedCount == 1);
            Check(fixture.Document.UndoServer.RedoCount == 1 && fixture.Document.UndoServer.UndoCount == 0 &&
                !fixture.Document.IsModified && fixture.Solutions == solutions,
                "Returning to the original query and pressing Enter preserves redo and selection as a no-op");
        }

        var committed = Search();
        await Preview(committed, "7[0-9]", 10);
        committed.Submit("7[0-9]");
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 10 && fixture.Sinks[1].VolatileData.DataCount == 0);
        Check(fixture.Document.UndoServer.UndoCount == 1 && fixture.Document.UndoServer.RedoCount == 0,
            "Enter commits a live search as one undo step and publishes the pruned selection");
        fixture.Document.UndoServer.PerformUndo();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80);
        Check(Options(fixture).Search == ".*", "Undo restores the query from before typing");
        fixture.Document.UndoServer.PerformRedo();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 10);
        Check(Options(fixture).Search == "7[0-9]", "Redo reapplies the committed query");

        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80);
        Call(attributes, "SetOffset", canvas, 0);
        Render();
        var input = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!.GetMethod("HandleKey")!;
        void Key(Forms.Keys key) => input.Invoke(attributes, new object[] { canvas, key });
        Key(Forms.Keys.Down); Key(Forms.Keys.Down);
        Render();
        var row = PanelTestAdapter.RowBounds(component, 5);
        PanelTestAdapter.CanvasClick(canvas, new PointF(row.X + 80, row.Y + row.Height / 2));
        Key(Forms.Keys.Down);
        Check(PanelTestAdapter.SelectedPosition(component) == 6, "Arrow navigation continues from the last mouse-selected row");
        Key(Forms.Keys.Escape);
        Key(Forms.Keys.Down);
        Check(PanelTestAdapter.SelectedPosition(component) == 0, "Clearing the selection also clears the keyboard cursor");

        fixture.ClearSelection();
        Call(attributes, "SetOffset", canvas, 0);
        PanelTestAdapter.Resize(component, 500, 360); attributes.PerformLayout(); Render();
        Check(!Field<bool>(attributes, "_singleLineFooter"), "A 500-unit panel retains two footer lines when its summary does not fit");
        PanelTestAdapter.Resize(component, 700, 360); attributes.PerformLayout(); Render();
        Check(Field<bool>(attributes, "_singleLineFooter"), "A wide panel still gains a row when its footer text fits");
        Call(attributes, "ShowNotice", canvas, new string('W', 90)); Render();
        Check(!Field<bool>(attributes, "_singleLineFooter"), "A long notice returns the footer to two lines");

        Call(component, "SetPanelSplit", true);
        attributes.PerformLayout(); Render();
        var splitArchive = new GH_IO.Serialization.GH_LooseChunk("Panel");
        Check(component.Write(splitArchive), "Split view writes its canonical dimensions and display mode");
        foreach (bool legacy in new[] { false, true })
        {
            if (legacy) splitArchive.RemoveItem("PanelSplit");
            var restored = (Grasshopper.Kernel.GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            Check(restored.Read(splitArchive), "Split view archive reads successfully");
            restored.Attributes.PerformLayout();
            Check(PanelTestAdapter.Bounds(restored, CanvasControl.Panel).Width == PanelTestAdapter.Size(component).Width * (legacy ? 1 : 2) &&
                PanelTestAdapter.Size(restored) == PanelTestAdapter.Size(component),
                legacy ? "Archives without PanelSplit restore the ordinary width" : "Split view roundtrips without doubling the canonical saved width");
            restored.DocumentContextChanged(fixture.Document, Grasshopper.Kernel.GH_DocumentContext.Close);
        }
        // One split interaction scenario uses different values so a D lookup cannot pass as F.
        fixture.Query(new PanelQueryOptions { Search = "> 9", SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        PanelTestAdapter.Resize(component, 400, 360); attributes.PerformLayout(); Render();
        var dataList = Field<RectangleF>(attributes, "_list");
        var pairedList = Field<RectangleF>(attributes, "_pairedList");
        var dataRow = PanelTestAdapter.RowBounds(component, 74);
        var pairedPoint = new PointF(dataRow.X + pairedList.X - dataList.X + 80, dataRow.Y + dataRow.Height / 2);
        PanelTestAdapter.CanvasClick(canvas, pairedPoint);
        Check(fixture.SelectedCount == 1 && PanelTestAdapter.SelectedPosition(component) == 74,
            "Clicking F with sorting and a filter selects the paired source position in the shared selection");
        Render();
        PanelTestAdapter.CanvasClick(canvas, pairedPoint, Forms.MouseButtons.Right);
        Render();
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            var actions = Field<Array>(attributes, "_rowMenuActions").Cast<object>().ToArray();
            int copyIndex = Array.FindIndex(actions, action => action.GetType().GetProperty("Action", Instance)!.GetValue(action)!.ToString() == "CopyValues");
            var menu = Field<RectangleF>(attributes, "_rowMenuBounds");
            PanelTestAdapter.CanvasClick(canvas, new PointF(menu.X + 30, menu.Y + copyIndex * 20 + 10));
            await fixture.Ready(() => Field<string>(attributes, "_actionStatus") == "Value copied");
            Check(Forms.Clipboard.GetText() == "F raw 74", "The F row menu copies the raw paired value rather than D's value");
            var captured = fixture.Result.Snapshot;
            var pairedValue = (GH_String)fixture.Paired.VolatileData.AllData(false).ElementAt(74);
            pairedValue.Value = "F mutated 74";
            string status = await (Task<string>)component.GetType().GetMethod("CopyPairedValuesAsync", Instance)!
                .Invoke(component, new object[] { captured, new[] { 74 } })!;
            Check(status == "Copy cancelled" && Forms.Clipboard.GetText() == "F raw 74" &&
                !(bool)Call(component, "CapturedSnapshotCurrent", captured)!,
                "An in-place F mutation rejects stale F copy and full-value row actions through the shared freshness guard");
            pairedValue.Value = "F raw 74";
            fixture.Solve();
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);

            // A D row menu that extends over F must not switch Ctrl+C or keyboard scrolling to F.
            Render();
            PanelTestAdapter.CanvasClick(canvas, new PointF(dataList.Right - 10, dataRow.Y + dataRow.Height / 2), Forms.MouseButtons.Right);
            Render();
            var dataMenu = Field<RectangleF>(attributes, "_rowMenuBounds");
            var menuPoint = new PointF(Math.Max(dataMenu.X + 30, pairedList.X + 10), dataMenu.Y + copyIndex * 20 + 10);
            PanelTestAdapter.CanvasClick(canvas, menuPoint);
            await fixture.Ready(() => !Field<bool>(attributes, "_copyPending"));
            // The suppressed second click of a double-click lands on F once the menu has closed.
            Render(); PanelTestAdapter.CanvasClick(canvas, menuPoint, clicks: 2);
            Check(dataMenu.Right > pairedList.X + 10 && !Field<bool>(attributes, "_copyPaired") && !Field<bool>(attributes, "_keyboardPaired"),
                "Clicking a D row menu where it overlaps F keeps Ctrl+C and keyboard scrolling on D");
        }
        finally
        {
            if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true);
        }
        var link = Field<RectangleF>(attributes, "_scrollLock");
        var linkPoint = new PointF(link.X + link.Width / 2, link.Y + link.Height / 2);
        Render(); PanelTestAdapter.CanvasClick(canvas, linkPoint); Render();
        var wheel = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!.GetMethod("HandleWheel")!;
        wheel.Invoke(attributes, new object[] { canvas, pairedPoint, -240 });
        int independentOffset = Field<int>(attributes, "_pairedOffset");
        Check(independentOffset > 0 && PanelTestAdapter.Offset(component) == 0 && !Field<RectangleF>(attributes, "_trackHit").IsEmpty,
            "Unlinked wheel scrolling moves only F and exposes D's independent track");
        Render();
        var splitSearch = Search();
        await Preview(splitSearch, "> 50", 29);
        splitSearch.Cancel();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render();
        Check(Field<int>(attributes, "_pairedOffset") == independentOffset && PanelTestAdapter.Offset(component) == 0,
            "Esc restores the independent F offset after a filtered search preview");
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render();
        Check(Field<int>(attributes, "_pairedOffset") == independentOffset && PanelTestAdapter.Offset(component) == 0,
            "Same-shape upstream recompute preserves both independent offsets");
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        Call(attributes, "SetPaneOffset", canvas, 0, false);
        Call(attributes, "SetPaneOffset", canvas, 60, true); Render();
        var f60 = PanelTestAdapter.RowBounds(component, 60);
        PanelTestAdapter.CanvasClick(canvas, new PointF(f60.X + 80, f60.Y + f60.Height / 2));
        for (int i = 0; i < 19; i++) { Key(Forms.Keys.Down); Render(); }
        bool f79Visible = Field<System.Collections.IEnumerable>(attributes, "_rowHits").Cast<object>().Any(hit =>
            (int)hit.GetType().GetProperty("SourcePosition", Instance)!.GetValue(hit)! == 79 &&
            pairedList.Contains((RectangleF)hit.GetType().GetProperty("Bounds", Instance)!.GetValue(hit)!));
        int dataOffset = PanelTestAdapter.Offset(component);
        Key(Forms.Keys.Home); Render();
        bool home = Field<int>(attributes, "_pairedOffset") == 0 && PanelTestAdapter.Offset(component) == dataOffset;
        Key(Forms.Keys.PageDown); Render();
        int pageOffset = Field<int>(attributes, "_pairedOffset");
        Key(Forms.Keys.PageUp); Render();
        bool pages = pageOffset > 0 && Field<int>(attributes, "_pairedOffset") == 0 && PanelTestAdapter.Offset(component) == dataOffset;
        Key(Forms.Keys.End); Render();
        Check(PanelTestAdapter.SelectedPosition(component) == 79 && f79Visible && home && pages &&
            Field<int>(attributes, "_pairedOffset") > pageOffset && PanelTestAdapter.Offset(component) == dataOffset,
            "Independent F keyboard selection stays visible and Home/End/PgUp/PgDn scroll the last active pane");
        fixture.Query(new PanelQueryOptions { Search = "> 9", SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render(); PanelTestAdapter.CanvasClick(canvas, linkPoint); Render();
        Check(Field<int>(attributes, "_pairedOffset") == PanelTestAdapter.Offset(component) && Field<bool>(attributes, "_scrollLocked") &&
            Field<RectangleF>(attributes, "_trackHit").IsEmpty,
            "Relinking snaps F back to D and removes D's middle track target");

        component.Params.Input[1].RemoveAllSources(); fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        attributes.PerformLayout(); Render();
        var disconnectedSplit = Field<RectangleF>(attributes, "_split");
        PanelTestAdapter.CanvasClick(canvas, new PointF(disconnectedSplit.X + 14, disconnectedSplit.Y + 9));
        attributes.PerformLayout();
        Check(!disconnectedSplit.IsEmpty && !(bool)component.GetType().GetProperty("PanelSplit", Instance)!.GetValue(component)! &&
            Field<RectangleF>(attributes, "_split").IsEmpty,
            "Disconnecting F retains the active split toggle until it closes the split, then hides it");
        attributes.PerformLayout();
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);

        using var curve = new LineCurve(Point3d.Origin, new Point3d(1, 2, 3));
        var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin));
        var inputs = new IGH_Goo[] { new GH_ObjectWrapper(Point3d.Origin), new GH_ObjectWrapper(curve),
            new GH_ObjectWrapper(group), new GH_ObjectWrapper(new GH_ObjectWrapper(Point3d.Origin)),
            new GH_String("text"), new GH_ObjectWrapper(new GH_Number(2)) };
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.AppendRange(inputs, new GH_Path(0)); fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == inputs.Length);
        for (int i = 0; i < inputs.Length; i++)
        {
            bool available = (bool)component.GetType().GetMethod("SelectionHasGeometry", Instance)!
                .Invoke(component, new object[] { new[] { i } })!;
            Check(available == (i < 4), "Geometry actions recognize supported wrappers and reject plain values at row " + i);
        }
    }
}
