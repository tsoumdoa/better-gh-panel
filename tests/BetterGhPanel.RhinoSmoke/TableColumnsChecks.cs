using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Forms = System.Windows.Forms;

internal static class TableColumnsChecks
{
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        var attributes = component.Attributes;
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1500, 900) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Zoom = 1; canvas.Viewport.Target = Point.Empty; canvas.Viewport.ComputeProjection();
        attributes.Pivot = new PointF(canvas.Viewport.VisibleRegion.Left + 45, canvas.Viewport.VisibleRegion.Top + 35);
        string? directory = Environment.GetEnvironmentVariable("PANEL_UX_SCREENSHOTS");
        void Render(string? name = null)
        {
            attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(GH_Skin.canvas_back); canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
            if (directory != null && name != null)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name + ".png"); bitmap.Save(path); Console.WriteLine("SCREENSHOT: " + path);
            }
        }
        string Fingerprint(int output)
        {
            var chunk = new GH_LooseChunk("Output");
            if (!((GH_Structure<IGH_Goo>)fixture.Sinks[output].VolatileData).Write(chunk)) throw new InvalidOperationException("Output serialization failed");
            return Convert.ToBase64String(chunk.Serialize_Binary());
        }
        PointF Center(RectangleF rect) => new PointF(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        GH_CanvasMouseEvent Mouse(PointF point, Forms.MouseButtons button = Forms.MouseButtons.Left, int clicks = 1) =>
            new GH_CanvasMouseEvent(canvas.Viewport, new Forms.MouseEventArgs(button, clicks, (int)canvas.Viewport.ProjectPoint(point).X, (int)canvas.Viewport.ProjectPoint(point).Y, 0));
        RectangleF[] Headers() => FieldValue<System.Collections.Generic.List<RectangleF>>(attributes, "_columnRects").ToArray();
        component.Params.Input[1].RemoveAllSources(); fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 7);
        Render("table-default-no-columns");
        Check(LayoutRect(attributes, "Split").IsEmpty && FieldValue<RectangleF>(attributes, "_tableHeader").IsEmpty && component.Params.Input.Count == 2,
            "A new panel has no Table chip, header or added input");
        var defaultChunk = new GH_LooseChunk("Panel"); component.Write(defaultChunk);
        Check(!defaultChunk.ItemExists("PanelSortColumn") && !defaultChunk.ChunkExists("PanelColumnWidths") && !defaultChunk.ItemExists("PanelTableWidthScale"), "Default saves contain no new table settings");
        component.Params.Input[1].AddSource(fixture.Paired);
        Call(component, "AddColumn"); Call(component, "AddColumn");
        Check(component.Params.Input.Count == 4 && component.Params.Input[2].NickName == "C1" && component.Params.Input[3].NickName == "C2", "Add column appends named generic tree inputs after F");
        var variable = (IGH_VariableParameterComponent)component;
        Check(!variable.CanInsertParameter(GH_ParameterSide.Input, 4) && !variable.CanRemoveParameter(GH_ParameterSide.Input, 3), "Variable inputs persist without ZUI insertion or removal");
        var area = new Param_GenericObject(); var mismatch = new Param_GenericObject();
        area.CreateAttributes(); mismatch.CreateAttributes(); fixture.Document.AddObject(area, false); fixture.Document.AddObject(mismatch, false);
        foreach (int b in Enumerable.Range(0, 3)) area.PersistentData.EnsurePath(new GH_Path(7, b));
        area.PersistentData.AppendRange(new[] { 40d, 10d, 30d, 20d }.Select(value => (IGH_Goo)new GH_Number(value)), new GH_Path(7, 0));
        area.PersistentData.AppendRange(new[] { 9d, 5d, 7d }.Select(value => (IGH_Goo)new GH_Number(value)), new GH_Path(7, 1));
        mismatch.PersistentData.Append(new GH_String("wrong branch count"), new GH_Path(0));
        mismatch.PersistentData.Append(new GH_String("still wrong"), new GH_Path(1));
        component.Params.Input[2].AddSource(area); component.Params.Input[3].AddSource(mismatch);
        fixture.Solve(); await fixture.Ready(() => fixture.Result.RowPositions.Count == 7);
        string[] unsorted = Enumerable.Range(0, 4).Select(Fingerprint).ToArray();
        Call(component, "SetPanelSplit", true); PanelTestAdapter.Resize(component, 520, 420); Render();
        Check(Headers().Length == 4 && !FieldValue<RectangleF>(attributes, "_tableHeader").IsEmpty, "One table header contains D, wired F, C1 and C2");
        var snapshot = fixture.Result.Snapshot;
        component.Params.Input[2].NickName = "Area"; Render();
        Check((string)Call(attributes, "ColumnTitle", 2)! == "Area" && ReferenceEquals(snapshot, fixture.Result.Snapshot), "An input nickname retitles the header without recomputing");
        var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { canvas, Point.Round(Center(Headers()[3])) }, null)!;
        attributes.SetupTooltip(Center(Headers()[3]), tooltip);
        Check(tooltip.Description == "C2 has 2 branches; D has 3.", "A mismatched column has a precise header diagnostic");
        Render("table-mismatch-header");
        Check(((PanelTableIndex)Call(attributes, "TableIndex", fixture.Result)!).Entries.Count(row => row.Extra) == 1 && (string)Call(component, "TableTsv", fixture.Result, new[] { 0 })! ==
            "Path\tIndex\tD\tF\tArea\tC2" + Environment.NewLine + "{7;0}\t0\t12\tA\t40\t" + Environment.NewLine,
            "Mismatched cells stay empty while independently paired columns retain their values");
        // Click through the complete ascending / descending / off cycle.
        foreach (var direction in new[] { PanelSortMode.ValueAscending, PanelSortMode.ValueDescending, PanelSortMode.SourceOrder })
        {
            attributes.RespondToMouseDown(canvas, Mouse(Center(Headers()[2])));
            await fixture.Ready(() => PropertyValue<PanelQueryOptions>(fixture.Session, "Options").SortMode == direction);
            Render();
            Check(Enumerable.Range(0, 4).Select(Fingerprint).SequenceEqual(unsorted), direction + " column sorting leaves D/S/B/F bytes unchanged");
            if (direction == PanelSortMode.ValueAscending) Check(fixture.Result.RowPositions.SequenceEqual(new[] { 1, 3, 2, 0, 4, 6, 5 }), "Column sort stays inside branches and places missing values last");
        }
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueAscending);
        await fixture.Ready(() => fixture.Result.RowPositions[0] == 1); Render();
        fixture.Select(1); fixture.Select(3, toggle: true); fixture.Select(4, toggle: true);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 3); Render("table-sorted-selected-missing-extra");
        string[] selectedOutputs = Enumerable.Range(0, 4).Select(Fingerprint).ToArray();
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueDescending);
        await fixture.Ready(() => fixture.Result.RowPositions[0] == 0);
        Check(Enumerable.Range(0, 4).Select(Fingerprint).SequenceEqual(selectedOutputs), "Sorting preserves selected identities and every selected output");
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueAscending);
        await fixture.Ready(() => fixture.Result.RowPositions[0] == 1); Render();
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            var input = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!.GetMethod("HandleKey")!;
            Check((bool)input.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.C })!, "Ctrl+C consumes a selected table");
            string expected = "Path\tIndex\tD\tF\tArea\tC2" + Environment.NewLine +
                "{7;0}\t1\t4\tB\t10\t" + Environment.NewLine + "{7;0}\t3\t12\tD\t20\t" + Environment.NewLine + "{7;0}\t4\t7\tE\t\t" + Environment.NewLine;
            await fixture.Ready(() => !FieldValue<bool>(attributes, "_copyPending"));
            Check(Forms.Clipboard.GetText() == expected, "Ctrl+C publishes three selected rows, headers and empty missing cells as TSV");
            var captured = fixture.Result.Snapshot;
            var liveColumn = (GH_Structure<IGH_Goo>)component.Params.Input[2].VolatileData;
            var number = (GH_Number)liveColumn.Branches[0][0];
            double saved = number.Value;
            number.Value += 100;
            string stale = await (Task<string>)Call(component, "CopyColumnValuesAsync", captured, new[] { 1, 3, 4 }, 1, null)!;
            Check(!(bool)Call(component, "CapturedSnapshotCurrent", captured)! && stale == "Copy cancelled" && Forms.Clipboard.GetText() == expected,
                "In-place added-column changes invalidate sort/cell capture and cancel stale clipboard publication");
            number.Value = saved;
            fixture.Solve(); await fixture.Ready(() => !ReferenceEquals(captured, fixture.Result.Snapshot));
            captured = fixture.Result.Snapshot;
            liveColumn = (GH_Structure<IGH_Goo>)component.Params.Input[2].VolatileData;
            liveColumn.Branches[0].Add(new GH_Number(99));
            Check(!(bool)Call(component, "CapturedSnapshotCurrent", captured)!, "Added-column branch structure is part of the capture fence");
            liveColumn.Branches[0].RemoveAt(liveColumn.Branches[0].Count - 1);
            fixture.Solve(); await fixture.Ready(() => !ReferenceEquals(captured, fixture.Result.Snapshot));
            captured = fixture.Result.Snapshot;
            fixture.ClearSelection(); fixture.Select(1); fixture.Select(3, toggle: true); fixture.Select(4, toggle: true);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 3);
            Call(component, "SetPanelMinimized", true); Render();
            var action = typeof(PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelRowAction", true)!;
            await (Task<string>)Call(component, "CopyAddressesAsync", captured, new[] { 1, 3, 4 }, Enum.Parse(action, "CopyTable"), null)!;
            Check(Forms.Clipboard.GetText().StartsWith("Path\tIndex\tValue" + Environment.NewLine, StringComparison.Ordinal),
                "Copy-as-table uses the compact layout's effective hidden columns");
            Call(component, "SetPanelMinimized", false); Render();

        }
        finally { if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true); }
        // Resize via actual header events and verify auto-fit reset.
        var border = new PointF(Headers()[2].Right - 2, Headers()[2].Y + 8);
        Check(attributes.RespondToMouseDown(canvas, Mouse(border)) == GH_ObjectResponse.Capture, "A header border captures resizing");
        attributes.RespondToMouseMove(canvas, Mouse(new PointF(border.X + 45, border.Y)));
        attributes.RespondToMouseUp(canvas, Mouse(new PointF(border.X + 45, border.Y))); Render();
        var widths = FieldValue<System.Collections.Generic.Dictionary<Guid, float>>(component, "ColumnWidths");
        Check(widths.ContainsKey(component.Params.Input[2].InstanceGuid), "A manually resized column stores its input identity");
        var widthChunk = new GH_LooseChunk("Panel"); component.Write(widthChunk);
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!; restored.CreateAttributes();
        Check(restored.Read(widthChunk) && restored.Params.Input.Count == 4 && restored.Params.Input[2].InstanceGuid == component.Params.Input[2].InstanceGuid &&
            FieldValue<System.Collections.Generic.Dictionary<Guid, float>>(restored, "ColumnWidths").Count == 1 &&
            PropertyValue<PanelQueryOptions>(Session(restored), "Options").SortColumn == component.Params.Input[2].InstanceGuid,
            "Variable input count, identities, sort column and widths survive archive reload");
        var fixedReader = new FixedPortPanel(); fixedReader.CreateAttributes();
        Check(fixedReader.Read(widthChunk) && fixedReader.Params.Input.Count == 2 && fixedReader.Params.Output.Count == 4 &&
            fixedReader.Params.Input[0].InstanceGuid == component.Params.Input[0].InstanceGuid && fixedReader.Params.Input[1].InstanceGuid == component.Params.Input[1].InstanceGuid,
            "An older fixed-port Grasshopper reader keeps D/F ports and ignores added columns");
        foreach (var input in fixedReader.Params.Input) input.RelinkProxySources(fixture.Document);
        Check(fixedReader.Params.Input[0].Sources.Single().InstanceGuid == fixture.Source.InstanceGuid && fixedReader.Params.Input[1].Sources.Single().InstanceGuid == fixture.Paired.InstanceGuid,
            "Older fixed-port loading keeps both base input wires");
        foreach (var input in fixedReader.Params.Input) input.RemoveAllSources();
        restored.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        Render(); border = new PointF(Headers()[2].Right - 2, Headers()[2].Y + 8);
        attributes.RespondToMouseDoubleClick(canvas, Mouse(border, clicks: 2)); Render();
        Check(!widths.ContainsKey(component.Params.Input[2].InstanceGuid), "Double-clicking a header border restores auto-fit");
        attributes.RespondToMouseDown(canvas, Mouse(Center(Headers()[2]), Forms.MouseButtons.Right)); Render("table-header-context-menu");
        Check(PropertyValue<int>(FieldValue<object>(attributes, "_menu"), "Count") == 3, "Header menu contains only the three sort commands");
        Call(FieldValue<object>(attributes, "_menu"), "Close");
        PanelTestAdapter.Resize(component, 300, 420);
        foreach (var input in component.Params.Input) Call(component, "SetColumnWidth", input.InstanceGuid, (float?)260f);
        Render(); Call(attributes, "ScrollHorizontal", canvas, 190f); Render("table-narrow-scrolled");
        Check(FieldValue<float>(attributes, "_horizontalOffset") > 0 && FieldValue<float>(attributes, "_frozenWidth") <= PanelTestAdapter.Bounds(component, CanvasControl.List).Width * 0.45f + 0.1f,
            "Narrow tables scroll columns sideways while D stays within 45 percent of the list");
        // Serialize complete trees, including types, numeric bits and empty paths, across output modes.
        foreach (string mode in new[] { "ordinary", "reverse", "flatten", "set-items", "set-values", "list-length-set", "selection-follow", "values" })
        {
            bool set = mode.StartsWith("set-", StringComparison.Ordinal) || mode == "list-length-set";
            PanelTestAdapter.TreeTools(component, 0, mode == "flatten", mode == "reverse", false, set);
            bool values = mode == "set-values" || mode == "values";
            fixture.Query(new PanelQueryOptions { DisplayFilterValue = mode == "values" || mode == "list-length-set",
                SearchMode = mode == "list-length-set" ? PanelSearchMode.ListLength : mode == "values" ? PanelSearchMode.Value : PanelSearchMode.Text });
            Call(component, "SetOutputValues", values ? PanelOutputValues.Values : PanelOutputValues.Items);
            fixture.Scope(mode == "selection-follow" ? PanelOutputScope.Selected : PanelOutputScope.Filtered);
            Call(component, "SetUnits", values ? PanelUnitConversion.MillimetersToMeters : PanelUnitConversion.None, values ? 2 : PanelUnits.AutomaticDecimals, (double?)0);
            fixture.Solve();
            await fixture.Ready(() => ReferenceEquals(FieldValue<PanelQueryResult?>(component, "_cachedOutputResult"), fixture.Result));
            fixture.ClearSelection();
            if (mode == "list-length-set") fixture.SelectBranch(fixture.Result.BranchPaths[0]);
            else if (fixture.Result.RowPositions.Count > 0) fixture.Select(fixture.Result.RowPositions[0]);
            await fixture.Ready(() => FieldValue<long>(component, "_publishedSelectionVersion") == FieldValue<long>(component, "_selectionVersion"));
            string[] baseline = Enumerable.Range(0, 4).Select(Fingerprint).ToArray();
            foreach (var direction in new[] { PanelSortMode.ValueAscending, PanelSortMode.ValueDescending, PanelSortMode.SourceOrder })
            {
                Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, direction);
                await fixture.Ready(() => PropertyValue<PanelQueryOptions>(fixture.Session, "Options").SortMode == direction);
                Check(Enumerable.Range(0, 4).Select(Fingerprint).SequenceEqual(baseline), mode + ": " + direction + " preserves every serialized D/S/B/F output byte");
            }
        }
        // Grasshopper's ordinary variable-parameter undo must restore the source wire.
        fixture.Document.UndoServer.Clear();
        Guid columnId = component.Params.Input[2].InstanceGuid;
        Call(component, "RemoveColumn", component.Params.Input[2]);
        Check(component.Params.Input.Count == 3, "Remove column removes only the requested added input");
        fixture.Document.UndoServer.PerformUndo();
        Check(component.Params.Input.Count == 4 && component.Params.Input[2].InstanceGuid == columnId && component.Params.Input[2].Sources.Single().InstanceGuid == area.InstanceGuid,
            "Remove column Undo restores the input identity and its wire");
        // Register this loaded implementation for a real .ghx document round trip.
        var info = (GH_AssemblyInfo)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterGhPanelInfo", true)!)!;
        var proxy = (IGH_ObjectProxy)Activator.CreateInstance(typeof(GH_Component).Assembly.GetType("Grasshopper.Kernel.GH_CompiledObjectProxy", true)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { component, info }, null)!;
        Grasshopper.Instances.ComponentServer.AddProxy(proxy);
        var archive = new GH_Archive(); Check(archive.AppendObject(fixture.Document, "Definition"), "A complete document serializes added column wires");
        string file = Path.Combine(directory ?? Path.GetTempPath(), "table-columns-roundtrip.ghx");
        Check(archive.WriteToFile(file, true, false), "The column document saves to a real GHX file");
        var read = new GH_Archive(); using var loaded = new GH_Document();
        Check(read.ReadFromFile(file) && read.ExtractObject(loaded, "Definition"), "The saved column document reloads");
        var loadedComponent = loaded.Objects.OfType<GH_Component>().Single(value => value.InstanceGuid == component.InstanceGuid);
        Check(loadedComponent.Params.Input.Count == 4 && loadedComponent.Params.Input[2].Sources.Single().InstanceGuid == area.InstanceGuid && loadedComponent.Params.Input[3].Sources.Single().InstanceGuid == mismatch.InstanceGuid,
            "Reloading the file preserves both added column wires");
        // A pre-table archive retains PanelSplit and fixed D/F ports.
        var legacy = new GH_LooseChunk("Panel");
        var old = (GH_Component)Activator.CreateInstance(component.GetType())!; old.CreateAttributes();
        old.Params.Input[0].AddSource(fixture.Source); old.Params.Input[1].AddSource(fixture.Paired);
        old.Write(legacy); legacy.RemoveChunk("ParameterData"); legacy.SetBoolean("PanelSplit", true);
        var upgraded = (GH_Component)Activator.CreateInstance(component.GetType())!; upgraded.CreateAttributes();
        Check(upgraded.Read(legacy) && PropertyValue<bool>(upgraded, "PanelSplit") && upgraded.Params.Input.Count == 2 && upgraded.Params.Output.Count == 4,
            "A legacy fixed-port PanelSplit archive opens as a D/F table with unchanged output ports");
        fixture.Document.AddObject(upgraded, false);
        foreach (var input in upgraded.Params.Input) input.RelinkProxySources(fixture.Document);
        upgraded.ExpireSolution(false); fixture.Document.NewSolution(false, GH_SolutionMode.Silent);
        await fixture.Ready(() => PropertyValue<bool>(Session(upgraded), "HasCurrentResult"));
        Check(string.Join(",", upgraded.Params.Output[3].VolatileData.AllData(false).Select(value => value.ToString())) == "A,B,C,D,E,F,G",
            "A legacy D/F table retains raw F output after solving");
        upgraded.Attributes.PerformLayout();
        Check(!FieldValue<RectangleF>(upgraded.Attributes, "_tableHeader").IsEmpty, "The legacy PanelSplit setting paints a table header");
        var original = new GH_LooseChunk("OriginalPanel"); original.CopyValuesFromChunk(legacy);
        original.RemoveItem("PanelOutputSchema"); original.RemoveItem("PanelOutputScope");
        original.RemoveChunk("param_input", 1);
        for (int i = 1; i < 4; i++) original.RemoveChunk("param_output", i);
        var migrated = (GH_Component)Activator.CreateInstance(component.GetType())!; migrated.CreateAttributes();
        Check(migrated.Read(original) && migrated.Params.Input.Count == 2 && migrated.Params.Output.Count == 4 &&
            migrated.Params.Input[0].InstanceGuid == old.Params.Input[0].InstanceGuid && migrated.Params.Output[0].InstanceGuid == old.Params.Output[0].InstanceGuid &&
            !original.ChunkExists("param_input", 1), "Original one-input/one-output migration preserves D identities without editing the archive");
        migrated.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        var corrupt = new GH_LooseChunk("CorruptPanel"); corrupt.CopyValuesFromChunk(legacy); corrupt.RemoveChunk("param_output", 2);
        var rejected = (GH_Component)Activator.CreateInstance(component.GetType())!; rejected.CreateAttributes();
        Check(!rejected.Read(corrupt), "A genuinely missing modern fixed-port chunk is rejected");
        rejected.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        old.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close); upgraded.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
    }
    // Simulates the fixed GH parameter server used by Panel+ before table columns.
    private sealed class FixedPortPanel : GH_Component
    {
        internal FixedPortPanel() : base("Fixed ports", "Fixed", "Archive compatibility probe", "Test", "Test") { }
        public override Guid ComponentGuid => new Guid("51DC646E-5F72-496A-8B40-628478541F44");
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Data", GH_ParamAccess.tree);
            p.AddGenericParameter("Paired", "F", "Paired", GH_ParamAccess.tree);
        }
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Data", GH_ParamAccess.tree);
            p.AddGenericParameter("Selected", "S", "Selected", GH_ParamAccess.tree);
            p.AddBooleanParameter("Mask", "B", "Mask", GH_ParamAccess.tree);
            p.AddGenericParameter("Paired", "F", "Paired", GH_ParamAccess.tree);
        }
        protected override void SolveInstance(IGH_DataAccess da) { }
    }

}
