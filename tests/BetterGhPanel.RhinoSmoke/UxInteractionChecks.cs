using static PanelTestAdapter;
using static SmokeAssertions;
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
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, AnyInstance, null, args.Select(value => value!.GetType()).ToArray(), null)!.Invoke(owner, args);
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
        using var editorHost = new Forms.Form { ClientSize = canvas.Size, ShowInTaskbar = false };
        editorHost.Controls.Add(canvas);
        editorHost.Show();
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
            Check((bool)component.GetType().GetProperty("QueryPreviewActive", AnyInstance)!.GetValue(component)!,
                "Debounced host textbox input starts a live query preview");
        }

        foreach (string outcome in new[] { "blur", "enter", "unchanged", "escape", "cancel", "invalid", "reset" })
        {
            fixture.Query(new PanelQueryOptions { SearchMode = outcome == "invalid" ? PanelSearchMode.Regex : PanelSearchMode.Text, Search = outcome == "reset" ? "> -1" : "" });
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
            fixture.Document.UndoServer.Clear();
            var edit = Search();
            if (outcome != "unchanged") edit.Type(outcome == "invalid" ? "Wall(" : "> 50");
            if (outcome == "enter") { edit.Submit("> 50"); edit.Blur(); }
            else if (outcome == "escape") edit.Cancel();
            else if (outcome == "cancel") edit.ClickCancel();
            else if (outcome == "reset")
            {
                Render();
                var reset = PanelTestAdapter.Bounds(component, CanvasControl.Reset);
                PanelTestAdapter.CanvasClick(canvas, new PointF(reset.Left + reset.Width / 2, reset.Top + reset.Height / 2));
            }
            else edit.Blur();
            await Task.Delay(300);
            bool didCommit = outcome == "blur" || outcome == "enter";
            await fixture.Ready(() => fixture.Result.RowPositions.Count == (didCommit ? 29 : 80));
            Check(fixture.Document.UndoServer.UndoCount == (didCommit || outcome == "reset" ? 1 : 0),
                "Editor outcome has exactly the expected undo count: " + outcome);
            Check(Options(fixture).Search == (didCommit ? "> 50" : ""), "Editor outcome preserves or commits text: " + outcome);
        }
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        fixture.Document.UndoServer.Clear();
        var okEdit = Search(); okEdit.Type("Wall("); okEdit.ClickOk();
        await fixture.Ready(() => Options(fixture).Search == "Wall(", allowError: true);
        Check(fixture.Document.UndoServer.UndoCount == 1, "OK submits invalid input exactly like Enter");
        foreach (var choice in new[] { (Menu: "Branch", Index: 1), (Menu: "Units", Index: 10), (Menu: "Units", Index: 12) })
        {
            Call(component, "ResetAll");
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
            fixture.Select(0);
            fixture.Document.UndoServer.Clear();
            Call(attributes, "OpenMenu", Enum.Parse(assembly.GetType("BetterGhPanel.UI.CanvasMenuKind", true)!, choice.Menu), canvas);
            Call(attributes, "ChooseMenu", choice.Index, canvas);
            CanvasEditorAdapter.Active(assembly).Blur();
            await Task.Delay(50);
            Check(Options(fixture).BranchPath == null && Options(fixture).DecimalPlaces < 0 && Options(fixture).RoundUpFactor == 0 &&
                fixture.SelectedCount == 1 && fixture.Document.UndoServer.UndoCount == 0,
                "An unchanged prefilled default abandons on blur: " + choice);
        }
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        fixture.Document.UndoServer.Clear();
        var upstreamEdit = Search(); upstreamEdit.Type("> 50"); upstreamEdit.Blur();
        // No yield: expiry must finish the pending edit before replacing its input.
        fixture.Source.PersistentData.Append(new GH_Number(100), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 30);
        Check(Options(fixture).Search == "> 50" && fixture.Document.UndoServer.UndoCount == 1,
            "An upstream recompute accepts the pending text and applies it to new input");
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.AppendRange(Enumerable.Range(0, 80).Select(i => (IGH_Goo)new GH_Number(i)), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 29);
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        Render();
        var trim = PanelTestAdapter.Bounds(component, CanvasControl.Trim);
        PanelTestAdapter.CanvasClick(canvas, new PointF(trim.Left + trim.Width / 2, trim.Top + trim.Height / 2));
        var trimEdit = CanvasEditorAdapter.Active(assembly); trimEdit.Type("2"); trimEdit.Blur();
        int trimPublications = 0;
        var publicationHook = component.GetType().GetProperty("BeforeOutputValidationForTesting", AnyInstance)!;
        var previousHook = publicationHook.GetValue(component);
        publicationHook.SetValue(component, (Action)(() => trimPublications++));
        try
        {
            fixture.Solve();
            await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80);
            Check((int)component.GetType().GetProperty("TrimDepth", AnyInstance)!.GetValue(component)! == 2 && trimPublications == 1,
                "Trim committed during upstream expiry publishes once without a nested solution");
        }
        finally { publicationHook.SetValue(component, previousHook); }
        Call(component, "SetTreeOperations", 0, false, false, false, false);
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        Render();
        var rowEdit = Search();
        await Preview(rowEdit, "> 50", 29);
        Render();
        var filteredRow = PanelTestAdapter.RowBounds(component, 51);
        PanelTestAdapter.CanvasClick(canvas, new PointF(filteredRow.X + 80, filteredRow.Y + filteredRow.Height / 2));
        Check(Options(fixture).Search == "> 50" && PanelTestAdapter.SelectedPosition(component) == 51,
            "Clicking a filtered row applies the pending query and selects the row");
        using (var other = new PanelHostFixture.Fixture(assembly))
        using (var otherCanvas = new GH_Canvas { Document = other.Document, Size = canvas.Size })
        {
            editorHost.Controls.Add(otherCanvas);
            otherCanvas.BringToFront();
            otherCanvas.Viewport.Size = otherCanvas.Size; otherCanvas.Viewport.Zoom = 1; otherCanvas.Viewport.ComputeProjection();
            other.Component.Attributes.PerformLayout();
            other.Solve();
            await other.Ready(() => other.Result.Snapshot != null);
            fixture.Query(new PanelQueryOptions());
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
            canvas.BringToFront();
            var first = Search(); first.Type("> 50");
            Call(other.Component.Attributes, "EditSearch", otherCanvas);
            Check(Options(fixture).Search == "> 50", "Opening another Panel+ editor applies the first query");
            CanvasEditorAdapter.Active(assembly).Cancel();
            await Task.Delay(50);
            canvas.BringToFront();
        }
        foreach (string display in new[] { "Full", "Half" })
        {
            Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, display));
            Call(component, "SetCanvasSize", 1f, 1f);
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Length });
            attributes.PerformLayout();
            Call(attributes, "OpenMenu", Enum.Parse(assembly.GetType("BetterGhPanel.UI.CanvasMenuKind", true)!, "SearchMode"), canvas);
            Call(attributes, "ChooseMenu", 3, canvas);
            Check(Options(fixture).SearchMode == PanelSearchMode.Length && CanvasEditorAdapter.IsActive(assembly),
                "Picking Length opens the editor at minimum " + display + " size");
            CanvasEditorAdapter.Active(assembly).Cancel();
            await Task.Delay(50);
        }
        Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, "Full"));
        Call(component, "SetCanvasSize", 360f, 360f);

        foreach (bool asynchronous in new[] { false, true })
        {
            fixture.Query(new PanelQueryOptions { SearchMode = asynchronous ? PanelSearchMode.Regex : PanelSearchMode.Text,
                Search = asynchronous ? ".*" : "" });
            await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 80 &&
                ReferenceEquals(FieldValue<PanelQueryResult>(component, "_cachedOutputResult"), fixture.Result));
            SeedRedo();
            fixture.Select(0);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 &&
                FieldValue<long>(component, "_publishedSelectionVersion") == FieldValue<long>(component, "_selectionVersion"));
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

        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        fixture.Select(0);
        foreach (int position in new[] { 1, 2, 3, 4 }) fixture.Select(position, toggle: true);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 5);
        var interrupted = Search();
        await Preview(interrupted, "> 50", 29);
        var oldSnapshot = fixture.Result.Snapshot;
        fixture.Solve();
        await fixture.Ready(() => !ReferenceEquals(oldSnapshot, fixture.Result.Snapshot) && fixture.Result.RowPositions.Count == 80 &&
            fixture.Sinks[1].VolatileData.DataCount == 5);
        Check(fixture.SelectedCount == 5 && fixture.Values(1) == "0,1,2,3,4",
            "Upstream recompute cancels live search and re-binds all committed selected positions");
        Render();
        var panelSpace = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
        PanelTestAdapter.CanvasClick(canvas, new PointF(panelSpace.Left + 3, panelSpace.Top + panelSpace.Height / 2));
        Check(fixture.SelectedCount == 5, "Clicking empty panel space keeps the selection");
        fixture.Select(0);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = ".*" });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);

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
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, DisplayFilterValue = true });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        fixture.ClearSelection();
        Check(!(bool)input.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!,
            "Ctrl+C without selection passes through to Grasshopper");
        fixture.Select(0); fixture.Select(1, toggle: true);
        var savedClipboard = Forms.Clipboard.GetDataObject();
        try
        {
            Forms.Clipboard.SetText("sentinel");
            Check((bool)input.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!,
                "Ctrl+C handles selected list-length rows");
            await fixture.Ready(() => Forms.Clipboard.GetText() == "80");
            Check(Forms.Clipboard.GetText() == "80", "List-length copy emits one count per branch");
            Call(component, "SetPanelSplit", true);
            foreach (int partnerCount in new[] { 79, 81, 80 })
            {
                fixture.Paired.PersistentData.Clear();
                fixture.Paired.PersistentData.AppendRange(Enumerable.Range(0, partnerCount).Select(i => (IGH_Goo)new GH_String("F raw " + i)), new GH_Path(0));
                fixture.Solve();
                await fixture.Ready(() => fixture.Sinks[3].VolatileData.DataCount == partnerCount);
                attributes.PerformLayout(); Render();
                var pane = PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List");
                PanelTestAdapter.CanvasClick(canvas, new PointF(pane.Left + 50, pane.Top + 23));
                Forms.Clipboard.SetText("sentinel");
                Check((bool)input.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!,
                    "Ctrl+C handles the F branch in plain list-length view");
                string expected = string.Join(Environment.NewLine, Enumerable.Range(0, partnerCount).Select(i => "F raw " + i));
                await fixture.Ready(() => Forms.Clipboard.GetText() == expected && !FieldValue<bool>(attributes, "_copyPending"));
                Check(FieldValue<string>(attributes, "_actionStatus") == partnerCount + " values copied",
                    "F branch copy includes every raw item without missing-partner blank lines");
            }
            Call(component, "SetPanelSplit", false);

            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, DisplayFilterValue = true, CreateSet = true });
            await fixture.Ready(() => fixture.Result.DisplaySet?.IsBranchSet == true);
            fixture.ClearSelection(); fixture.SelectBranch("{0}");
            Forms.Clipboard.SetText("sentinel");
            Check((bool)input.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!,
                "Ctrl+C handles a selected list-length set");
            await fixture.Ready(() => Forms.Clipboard.GetText() == "80");

        }
        finally
        {
            if (savedClipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(savedClipboard, true);
        }
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        fixture.ClearSelection();
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
        PanelTestAdapter.Resize(component, 700, 360); attributes.PerformLayout(); Render();

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
        var dataList = PanelTestAdapter.PaneField<RectangleF>(attributes, false, "List");
        var pairedList = PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List");
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
            var actions = FieldValue<Array>(attributes, "_rowMenuActions").Cast<object>().ToArray();
            int copyIndex = Array.FindIndex(actions, action => action.GetType().GetProperty("Action", AnyInstance)!.GetValue(action)!.ToString() == "CopyValues");
            var menu = FieldValue<RectangleF>(attributes, "_rowMenuBounds");
            PanelTestAdapter.CanvasClick(canvas, new PointF(menu.X + 30, menu.Y + copyIndex * 20 + 10));
            await fixture.Ready(() => FieldValue<string>(attributes, "_actionStatus") == "Value copied");
            Check(Forms.Clipboard.GetText() == "F raw 74", "The F row menu copies the raw paired value rather than D's value");
            var captured = fixture.Result.Snapshot;
            var pairedValue = (GH_String)fixture.Paired.VolatileData.AllData(false).ElementAt(74);
            pairedValue.Value = "F mutated 74";
            string status = await (Task<string>)component.GetType().GetMethod("CopyPairedValuesAsync", AnyInstance)!
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
            var dataMenu = FieldValue<RectangleF>(attributes, "_rowMenuBounds");
            var menuPoint = new PointF(Math.Max(dataMenu.X + 30, pairedList.X + 10), dataMenu.Y + copyIndex * 20 + 10);
            PanelTestAdapter.CanvasClick(canvas, menuPoint);
            await fixture.Ready(() => !FieldValue<bool>(attributes, "_copyPending"));
            // The suppressed second click of a double-click lands on F once the menu has closed.
            Render(); PanelTestAdapter.CanvasClick(canvas, menuPoint, clicks: 2);
            Check(dataMenu.Right > pairedList.X + 10 && !FieldValue<bool>(attributes, "_copyPaired") && !FieldValue<bool>(attributes, "_keyboardPaired"),
                "Clicking a D row menu where it overlaps F keeps Ctrl+C and keyboard scrolling on D");
        }
        finally
        {
            if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true);
        }
        var link = PanelTestAdapter.LayoutRect(attributes, "ScrollLock");
        var linkPoint = new PointF(link.X + link.Width / 2, link.Y + link.Height / 2);
        Render(); PanelTestAdapter.CanvasClick(canvas, linkPoint); Render();
        var wheel = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!.GetMethod("HandleWheel")!;
        wheel.Invoke(attributes, new object[] { canvas, pairedPoint, -240 });
        int independentOffset = PanelTestAdapter.PaneField<int>(attributes, true, "Offset");
        Check(independentOffset > 0 && PanelTestAdapter.Offset(component) == 0 && !PanelTestAdapter.PaneField<RectangleF>(attributes, false, "TrackHit").IsEmpty,
            "Unlinked wheel scrolling moves only F and exposes D's independent track");
        Render();
        var splitSearch = Search();
        await Preview(splitSearch, "> 50", 29);
        splitSearch.Cancel();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render();
        Check(PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == independentOffset && PanelTestAdapter.Offset(component) == 0,
            "Esc restores the independent F offset after a filtered search preview");
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render();
        Check(PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == independentOffset && PanelTestAdapter.Offset(component) == 0,
            "Same-shape upstream recompute preserves both independent offsets");
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        Call(attributes, "SetPaneOffset", canvas, 0, false);
        Call(attributes, "SetPaneOffset", canvas, 60, true); Render();
        var f60 = PanelTestAdapter.RowBounds(component, 60);
        PanelTestAdapter.CanvasClick(canvas, new PointF(f60.X + 80, f60.Y + f60.Height / 2));
        for (int i = 0; i < 19; i++) { Key(Forms.Keys.Down); Render(); }
        bool f79Visible = FieldValue<System.Collections.IEnumerable>(attributes, "_rowHits").Cast<object>().Any(hit =>
            (int)hit.GetType().GetProperty("SourcePosition", AnyInstance)!.GetValue(hit)! == 79 &&
            pairedList.Contains((RectangleF)hit.GetType().GetProperty("Bounds", AnyInstance)!.GetValue(hit)!));
        int dataOffset = PanelTestAdapter.Offset(component);
        Key(Forms.Keys.Home); Render();
        bool home = PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == 0 && PanelTestAdapter.Offset(component) == dataOffset;
        Key(Forms.Keys.PageDown); Render();
        int pageOffset = PanelTestAdapter.PaneField<int>(attributes, true, "Offset");
        Key(Forms.Keys.PageUp); Render();
        bool pages = pageOffset > 0 && PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == 0 && PanelTestAdapter.Offset(component) == dataOffset;
        Key(Forms.Keys.End); Render();
        Check(PanelTestAdapter.SelectedPosition(component) == 79 && f79Visible && home && pages &&
            PanelTestAdapter.PaneField<int>(attributes, true, "Offset") > pageOffset && PanelTestAdapter.Offset(component) == dataOffset,
            "Independent F keyboard selection stays visible and Home/End/PgUp/PgDn scroll the last active pane");
        fixture.Query(new PanelQueryOptions { Search = "> 9", SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        Render(); PanelTestAdapter.CanvasClick(canvas, linkPoint); Render();
        Check(PanelTestAdapter.PaneField<int>(attributes, true, "Offset") == PanelTestAdapter.Offset(component) && FieldValue<bool>(attributes, "_scrollLocked") &&
            PanelTestAdapter.PaneField<RectangleF>(attributes, false, "TrackHit").IsEmpty,
            "Relinking snaps F back to D and removes D's middle track target");

        component.Params.Input[1].RemoveAllSources(); fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 70);
        attributes.PerformLayout(); Render();
        var disconnectedSplit = PanelTestAdapter.LayoutRect(attributes, "Split");
        PanelTestAdapter.CanvasClick(canvas, new PointF(disconnectedSplit.X + 14, disconnectedSplit.Y + 9));
        attributes.PerformLayout();
        Check(!disconnectedSplit.IsEmpty && !(bool)component.GetType().GetProperty("PanelSplit", AnyInstance)!.GetValue(component)! &&
            PanelTestAdapter.LayoutRect(attributes, "Split").IsEmpty,
            "Disconnecting F retains the active split toggle until it closes the split, then hides it");
        attributes.PerformLayout();
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);

        // Each action that hides Out must restore the choice in the same undo step.
        foreach (string action in new[] { "mode", "clear", "set", "filters", "all" })
        {
            Call(component, "ResetAll");
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
            if (action == "set") Call(component, "SetTreeOperations", 0, false, false, false, true);
            else fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, DisplayFilterValue = true });
            await fixture.Ready(() => !PropertyBusy());
            Call(component, "SetOutputValues", PanelOutputValues.Values);
            fixture.Select(0);
            fixture.Document.UndoServer.Clear();
            if (action == "mode") Call(attributes, "SetSearch", canvas, PanelSearchMode.Text, "", false);
            else if (action == "clear") Call(attributes, "ClearSearch", canvas);
            else if (action == "set") Call(component, "SetTreeOperations", 0, false, false, false, false);
            else Call(component, action == "filters" ? "ClearFilters" : "ResetAll");
            await fixture.Ready(() => !PropertyBusy());
            Check(ValuesChoice() == PanelOutputValues.Items && fixture.Document.UndoServer.UndoCount == 1,
                "Hiding Out resets Values to Items in one undo step: " + action);
            if (action == "clear") Check(((PanelQueryOptions)fixture.Session.GetType().GetProperty("Options")!.GetValue(fixture.Session)!).SearchMode == PanelSearchMode.Text && ((PanelQueryOptions)fixture.Session.GetType().GetProperty("Options")!.GetValue(fixture.Session)!).Search.Length == 0, "One clear returns to Text");
            if (action == "filters" || action == "all") Check(fixture.SelectedCount == 0, "Reset clears Selection: " + action);
            fixture.Document.UndoServer.PerformUndo();
            await fixture.Ready(() => !PropertyBusy());
            Check(ValuesChoice() == PanelOutputValues.Values && CanValues(), "Undo restores query/tree setting and Values: " + action);
            fixture.Document.UndoServer.PerformRedo();
            await fixture.Ready(() => !PropertyBusy());
            Check(ValuesChoice() == PanelOutputValues.Items && !CanValues(), "Redo restores Items fallback: " + action);
        }
        bool PropertyBusy() => (bool)fixture.Session.GetType().GetProperty("IsBusy")!.GetValue(fixture.Session)!;
        bool CanValues() => (bool)component.GetType().GetProperty("CanOutputValues", AnyInstance)!.GetValue(component)!;
        PanelOutputValues ValuesChoice() => (PanelOutputValues)component.GetType().GetProperty("OutputValues", AnyInstance)!.GetValue(component)!;

        Call(component, "SetTreeOperations", 1, false, true, false, false);
        Call(component, "SetOutputScope", PanelOutputScope.Selected);
        fixture.Query(new PanelQueryOptions { Search = "> 9", SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => !PropertyBusy());
        fixture.Document.UndoServer.Clear();
        Call(component, "ClearFilters");
        await fixture.Ready(() => !PropertyBusy());
        Check(Options(fixture).Search == "" && Options(fixture).SortMode == PanelSortMode.SourceOrder &&
            (bool)component.GetType().GetProperty("ReverseTree", AnyInstance)!.GetValue(component)! &&
            (PanelOutputScope)component.GetType().GetProperty("OutputScope", AnyInstance)!.GetValue(component)! == PanelOutputScope.Selected &&
            fixture.Document.UndoServer.UndoCount == 1, "Clear filters keeps tree tools and B/F scope");
        fixture.Document.UndoServer.PerformUndo();
        await fixture.Ready(() => !PropertyBusy());
        Check(Options(fixture).Search == "> 9", "Clear filters Undo restores filters");
        fixture.Document.UndoServer.Clear();
        Call(component, "ResetAll");
        await fixture.Ready(() => !PropertyBusy());
        Check(!(bool)component.GetType().GetProperty("ReverseTree", AnyInstance)!.GetValue(component)! &&
            (PanelOutputScope)component.GetType().GetProperty("OutputScope", AnyInstance)!.GetValue(component)! == PanelOutputScope.Filtered &&
            fixture.Document.UndoServer.UndoCount == 1, "Reset all resets tree tools and B/F scope in one step");
        fixture.Document.UndoServer.PerformUndo();
        await fixture.Ready(() => !PropertyBusy());
        Check((bool)component.GetType().GetProperty("ReverseTree", AnyInstance)!.GetValue(component)! && Options(fixture).Search == "> 9",
            "Reset all Undo restores tools and filters");
        Call(component, "ResetAll");

        fixture.Document.UndoServer.Clear();
        fixture.Select(0);
        Call(component, "ClearFilters");
        Check(fixture.Document.UndoServer.UndoCount == 0, "Clear filters with only a selection records no undo");
        fixture.Select(0);
        bool resetRecordedUndo = (bool)component.GetType().GetMethod("ResetAll", AnyInstance)!.Invoke(component, Array.Empty<object>())!;
        Check(!resetRecordedUndo && fixture.Document.UndoServer.UndoCount == 0 && fixture.SelectedCount == 0,
            "Selection-only Reset all reports that no settings undo was recorded");

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
            bool available = (bool)component.GetType().GetMethod("SelectionHasGeometry", AnyInstance)!
                .Invoke(component, new object[] { new[] { i } })!;
            Check(available == (i < 4), "Geometry actions recognize supported wrappers and reject plain values at row " + i);
        }
        Call(component, "SetCanvasSize", 360f, 360f);
    }
}
