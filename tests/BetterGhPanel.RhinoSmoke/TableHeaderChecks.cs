using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

// Compile with the native suite; run only in a connected Rhino session.
internal static class TableHeaderChecks
{
    internal static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1400, 900) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Zoom = 1; canvas.Viewport.Target = Point.Empty; canvas.Viewport.ComputeProjection();
        void Render()
        {
            component.Attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        component.Params.Input[1].RemoveAllSources();
        Call(component, "AddHeaderInput"); Call(component, "AddHeaderInput");
        var header = PropertyValue<IGH_Param>(component, "HeaderInput");
        Check(component.Params.Input.Count == 3 && header.Name == "Column titles" && header.NickName == "H" &&
            !PropertyValue<bool>(component, "TableInputPresent"), "H is unique and alone never enables the table");
        using (var menu = new System.Windows.Forms.ToolStripDropDown())
        {
            Call(component, "AppendAdditionalComponentMenuItems", menu);
            var items = menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Check(items.Single(item => item.Text == "Column titles input").Checked &&
                items.All(item => item.Text != "Add header input" && item.Text != "Remove header input"),
                "One checkable Column titles input command replaces the old Add/Remove menu items");
            Check(!items.Single(item => item.Text == "Remove column").Enabled, "H alone is absent from the Remove column menu");
        }
        component.Params.Input[1].AddSource(fixture.Paired);
        var titles = new Param_GenericObject(); titles.CreateAttributes(); fixture.Document.AddObject(titles, false);
        void SetTitles(params string?[] values)
        {
            titles.PersistentData.Clear();
            for (int i = 0; i < values.Length; i++)
                titles.PersistentData.Append(values[i] == null ? null! : new GH_String(values[i]), new GH_Path(i / 2));
            titles.ExpireSolution(false);
        }
        SetTitles("Panel", "Code", "Area (m²)", ""); header.AddSource(titles);
        Call(component, "AddColumn"); Call(component, "AddColumn");
        Check(component.Params.Input.Last() == header && component.Params.Input[2].NickName == "C1" && component.Params.Input[3].NickName == "C2",
            "Adding columns while H exists inserts before H");
        component.Params.Input[2].AddSource(fixture.Source);
        component.Params.Input[3].AddSource(fixture.Source);
        Call(component, "RemoveColumn", header);
        Check(component.Params.Input.Last() == header && !((IGH_VariableParameterComponent)component).DestroyParameter(GH_ParameterSide.Input, 4),
            "H cannot be removed as a data column");
        Call(component, "SetPanelSplit", true); fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 7); Render();
        Check(Enumerable.Range(0, 4).Select(i => (string)Call(component.Attributes, "ColumnTitle", i)!).SequenceEqual(new[] { "Panel", "Code", "Area (m²)", "C2" }),
            "H titles use tree order, including D and shown F, with an empty-item fallback");
        Check(((string)Call(component, "TableTsv", fixture.Result, new[] { 0 })!).StartsWith("Path\tIndex\tPanel\tCode\tArea (m²)\tC2" + Environment.NewLine, StringComparison.Ordinal) &&
            component.Params.Input.Select(input => input.NickName).SequenceEqual(new[] { "D", "F", "C1", "C2", "H" }),
            "TSV uses H titles without renaming ports");
        var result = fixture.Result; var snapshot = result.Snapshot;
        string[] outputs = Enumerable.Range(0, 4).Select(fixture.Values).ToArray();
        SetTitles("Updated", null, " \t", "Tail", "ignored");
        fixture.Document.NewSolution(false, GH_SolutionMode.Silent);
        await fixture.Ready(() => (string)Call(component, "TableHeaderTitle", 0)! == "Updated"); Render();
        Check(!ReferenceEquals(snapshot, fixture.Result.Snapshot) &&
            outputs.SequenceEqual(Enumerable.Range(0, 4).Select(fixture.Values)), "H changes use a normal solve and preserve wire values");
        Check(Enumerable.Range(0, 4).Select(i => (string)Call(component, "TableHeaderTitle", i)!).SequenceEqual(new[] { "Updated", "F", "C1", "Tail" }),
            "Null and whitespace titles fall back without shifting later items; extra titles are ignored");
        component.Params.Input[1].RemoveAllSources(); fixture.Solve();
        await fixture.Ready(() => ((System.Collections.IList)PropertyValue<object>(component, "TableColumns")).Count == 2);
        Check((string)Call(component, "TableHeaderTitle", 1)! == "C1" && (string)Call(component, "TableHeaderTitle", 2)! == "Tail",
            "Unwired F keeps H slot 1 reserved; added-column titles never shift");
        component.Params.Input[1].AddSource(fixture.Paired); fixture.Solve();
        await fixture.Ready(() => ((System.Collections.IList)PropertyValue<object>(component, "TableColumns")).Count == 3);
        snapshot = fixture.Result.Snapshot;
        fixture.Source.ExpireSolution(false); fixture.Document.NewSolution(false, GH_SolutionMode.Silent);
        await fixture.Ready(() => !ReferenceEquals(snapshot, fixture.Result.Snapshot));
        Check(!ReferenceEquals(snapshot, fixture.Result.Snapshot), "A data-source expiration still replaces the snapshot when H exists");

        // Identity must survive nicknames, index shifts, proxy relinking and GHX reload.
        header.NickName = "Titles"; component.Params.Input[2].NickName = "H";
        Call(component, "SetColumnWidth", component.Params.Input[2].InstanceGuid, (float?)180);
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueAscending);
        await fixture.Ready(() => PropertyValue<PanelQueryOptions>(fixture.Session, "Options").SortColumn == component.Params.Input[2].InstanceGuid);
        var chunk = new GH_LooseChunk("Panel"); Check(component.Write(chunk), "Header component serializes");
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!; restored.CreateAttributes();
        Check(restored.Read(chunk) && PropertyValue<IGH_Param>(restored, "HeaderInput").Name == "Column titles", "Header component reloads with its current input name");
        foreach (var input in restored.Params.Input) input.RelinkProxySources(fixture.Document);
        Check(PropertyValue<IGH_Param>(restored, "HeaderInput").InstanceGuid == header.InstanceGuid &&
            restored.Params.Input.Last().Sources.Single().InstanceGuid == titles.InstanceGuid &&
            PropertyValue<PanelQueryOptions>(Session(restored), "Options").SortColumn == component.Params.Input[2].InstanceGuid &&
            FieldValue<Dictionary<Guid, float>>(restored, "ColumnWidths").ContainsKey(component.Params.Input[2].InstanceGuid),
            "H GUID, wire, final position and real column width/sort survive reload despite nickname collisions");
        restored.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        chunk.SetGuid("PanelHeaderInput", Guid.NewGuid()); chunk.SetGuid("PanelSortColumn", Guid.NewGuid()); chunk.SetInt32("PanelSortInputIndex", 4);
        chunk.RemoveChunk("PanelColumnWidths"); chunk.CreateChunk("PanelColumnWidths").SetSingle("InputIndex4", 240);
        restored = (GH_Component)Activator.CreateInstance(component.GetType())!; restored.CreateAttributes();
        Check(restored.Read(chunk) && PropertyValue<IGH_Param>(restored, "HeaderInput") == restored.Params.Input.Last() &&
            PropertyValue<PanelQueryOptions>(Session(restored), "Options").SortColumn == Guid.Empty &&
            !FieldValue<Dictionary<Guid, float>>(restored, "ColumnWidths").ContainsKey(restored.Params.Input.Last().InstanceGuid),
            "Header index fallback identifies H while width/sort index fallbacks exclude it");
        restored.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
        var archive = new GH_Archive(); Check(archive.AppendObject(fixture.Document, "Definition"), "Header document serializes");
        string file = Path.Combine(Environment.GetEnvironmentVariable("PANEL_UX_SCREENSHOTS") ?? Path.GetTempPath(), "table-headers-roundtrip.ghx");
        Check(archive.WriteToFile(file, true, false), "Header document saves to GHX");
        var reader = new GH_Archive(); using var loaded = new GH_Document();
        Check(reader.ReadFromFile(file) && reader.ExtractObject(loaded, "Definition"), "Header GHX reloads");
        var loadedComponent = loaded.Objects.OfType<GH_Component>().Single(value => value.InstanceGuid == component.InstanceGuid);
        Check(PropertyValue<IGH_Param>(loadedComponent, "HeaderInput").InstanceGuid == header.InstanceGuid &&
            loadedComponent.Params.Input.Last().Sources.Single().InstanceGuid == titles.InstanceGuid, "GHX retains H identity, wire and last position");
        fixture.Document.UndoServer.Clear(); Call(component, "RemoveHeaderInput");
        Check(PropertyValue<object?>(component, "HeaderInput") == null && component.Params.Input.Count == 4, "Remove header input removes only H");
        fixture.Document.UndoServer.PerformUndo();
        Check(PropertyValue<IGH_Param>(component, "HeaderInput").InstanceGuid == header.InstanceGuid &&
            component.Params.Input.Last().Sources.Single().InstanceGuid == titles.InstanceGuid, "Header removal Undo restores H and its wire");
        loadedComponent.DocumentContextChanged(loaded, GH_DocumentContext.Close);
    }
}
