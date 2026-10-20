using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
using Rhino;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static class FilterUiChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document();
        GH_Document.EnableSolutions = true;
        document.Enabled = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        for (int index = 0; index < 32; index++)
            source.PersistentData.Append(new GH_Point(new Point3d(index * 5, index % 3, 0)), new GH_Path(0));
        source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(5, 0, 0))), new GH_Path(0));
        source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(10, 0, 0))), new GH_Path(0));
        source.PersistentData.Append(new GH_Point(new Point3d(250, 80, 0)), new GH_Path(1));
        source.PersistentData.Append(new GH_Rectangle(new Rectangle3d(Plane.WorldXY, 3, 4)), new GH_Path(1));
        source.PersistentData.Append(new GH_Box(new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4)))), new GH_Path(1));
        source.PersistentData.Append(new GH_String("Wall-A"), new GH_Path(1));
        source.PersistentData.Append(new GH_String("Wall-B"), new GH_Path(1));
        source.PersistentData.Append(new GH_Number(24), new GH_Path(1));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await Wait(session);
        // Grasshopper may duplicate persistent goo while collecting the first
        // solution. Reset must restore these typed items after filtering.
        var initialOutput = component.Params.Output[0].VolatileData;
        var initialPaths = initialOutput.Paths.Select(path => path.ToString()).ToArray();
        var initialBranchCounts = initialOutput.Paths.Select(path => initialOutput.get_Branch(path).Count).ToArray();
        var initialItems = initialOutput.AllData(true).ToArray();
        Check(initialItems.Length == 40, "Filter fixture begins with all forty output rows");
        using var canvas = new GH_Canvas { Size = new Size(1100, 900) };
        component.Attributes.Pivot = new PointF(50, 50);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        SavePreview(component, canvas, "tree-icons");

        await VerifyPreviewModes(component, canvas, session, document);
        await VerifyFilters(component, canvas, assembly, session);
        await VerifyBranches(component, canvas, assembly, session);
        await VerifyCompactErrors(component, canvas, assembly, session);
        await VerifyPersistence(component, canvas, assembly, session);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var output = component.Params.Output[0].VolatileData;
        var items = output.AllData(true).ToArray();
        Check(output.DataCount == initialItems.Length && items.Length == initialItems.Length,
            "Reset after search and branch filters restores every output row");
        Check(output.Paths.Select(path => path.ToString()).SequenceEqual(initialPaths) &&
            output.Paths.Select(path => output.get_Branch(path).Count).SequenceEqual(initialBranchCounts),
            "Reset restores output paths and branch lengths");
        Check(items.Zip(initialItems, (current, original) => ReferenceEquals(current, original)).All(same => same),
            "Filtering and reset preserve the captured typed goo identities");
        Check(items.OfType<GH_Number>().Single().Value == 24 &&
            items.OfType<GH_String>().Select(item => item.Value).SequenceEqual(new[] { "Wall-A", "Wall-B" }),
            "Search modes and branch actions retain original numeric and text values");
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
        await VerifyFooterStatistics(assembly);
        await VerifyPreviewUndo(assembly);
    }

    private static async Task VerifyPreviewModes(GH_Component component, GH_Canvas canvas, object session, GH_Document document)
    {
        var preview = Field<object>(component, "_highlight");
        var snapshot = Property<PanelSnapshot>(session, "Snapshot");
        Check(PanelTestAdapter.PreviewMode(component) == "Selected" && PreviewCount() == 0 && !component.ClippingBox.IsValid,
            "New panels preview no geometry until rows are selected");
        var viewport = RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport;
        var target = viewport.CameraTarget;
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 0));
        Check(PreviewCount() == 1 && component.ClippingBox.Max.X == 0 && viewport.CameraTarget == target,
            "Default Selected mode previews only the clicked row without moving the camera");
        Mode(1);
        Check(PreviewCount() == 37 && Property<int>(preview, "Count") == 1,
            "Filtered results previews all matching geometry including offscreen rows, keeping row selection");
        SavePreview(component, canvas, "preview-results");
        Mode(2);
        Check(PreviewCount() == 0 && !component.ClippingBox.IsValid && Selected(component, session).SequenceEqual(new[] { 0 }),
            "Off removes preview and clipping bounds while retaining row selection");
        Mode(0);
        Check(PreviewCount() == 1 && Selected(component, session).SequenceEqual(new[] { 0 }),
            "Returning from Off restores the selected row preview");
        Mode(1);
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = ".", TypeName = "Point", BranchPath = "{1}" });
        Check(PreviewCount() == 0, "Starting a filter clears the old full-result preview immediately");
        await Wait(session);
        Check(PreviewCount() == 1 && Property<int>(preview, "Count") == 0 && component.ClippingBox.Min.X == 250,
            "Filtered preview publishes the new query without painting and drops hidden selection");
        Query(session, new PanelQueryOptions { Search = "no matching geometry" });
        await Wait(session);
        Check(PreviewCount() == 0 && !component.ClippingBox.IsValid, "An empty filtered result draws nothing");
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
        await Wait(session, allowError: true);
        Check(PreviewCount() == 0 && !component.ClippingBox.IsValid, "A failed filter cannot fall back to full-tree preview");
        Query(session, new PanelQueryOptions { BranchPath = "{1}" });
        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        await Wait(session);
        Check(PreviewCount() == 0, "Filtered preview stays cleared while its document is inactive");
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        Check(PreviewCount() == 3 && !Highlighted(component), "Returning to a document restores its filtered scope without selecting rows");
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 34));
        Mode(0);
        var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
        Click(component, canvas, new RectangleF(list.X, list.Bottom - 18, list.Width, 16));
        Check(Selected(component, session).Count == 0 && PreviewCount() == 0,
            "Clicking empty table space clears selection and its Selected preview");
        Mode(1);
        PanelTestAdapter.ResetView(component);
        await Wait(session);
        Check(PreviewCount() == 37 && PanelTestAdapter.PreviewMode(component) == "FilteredResults",
            "Reset restores the unfiltered result while retaining the explicit preview mode");
        Check(ReferenceEquals(snapshot, Property<PanelSnapshot>(session, "Snapshot")) && viewport.CameraTarget == target,
            "Preview controls and filters retain the captured snapshot and camera position");

        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
        Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(PanelTestAdapter.MenuBounds(component)),
            "Preview menu stays inside the minimum Half panel");
        SavePreview(component, canvas, "preview-menu-half");
        MenuRow(component, canvas, 2);
        Check(PanelTestAdapter.PreviewMode(component) == "Off", "Off remains reachable in minimum Half mode");
        Mode(0);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        PanelTestAdapter.Resize(component, 360, 360);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
        SavePreview(component, canvas, "preview-menu");
        MenuRow(component, canvas, 0);

        int PreviewCount() => Property<int>(preview, "PreviewCount");
        void Mode(int index)
        {
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
            MenuRow(component, canvas, index);
        }
    }

    private static async Task VerifyPreviewUndo(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        document.AddObject(component, false);
        try
        {
            document.UndoServer.Clear();
            PanelTestAdapter.PreviewMode(component, "FilteredResults");
            document.UndoServer.PerformUndo();
            Check(PanelTestAdapter.PreviewMode(component) == "Selected", "Undo restores the previous preview mode");
            document.UndoServer.PerformRedo();
            Check(PanelTestAdapter.PreviewMode(component) == "FilteredResults", "Redo restores the requested preview mode");
            await Wait(PanelTestAdapter.Session(component));
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static async Task VerifyFooterStatistics(Assembly assembly)
    {
        // Keep output-tool changes isolated from the filter fixture above.
        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.PersistentData.Append(new GH_Number(1234567890.12), new GH_Path(0, 0));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        try
        {
            document.NewSolution(false, GH_SolutionMode.Silent);
            PanelTestAdapter.TreeTools(component, 1, true, true, true);
            PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            var result = Property<PanelQueryResult>(session, "Result");
            foreach (int width in new[] { 300, 360 })
            {
                PanelTestAdapter.Resize(component, width, 220);
                component.Attributes.PerformLayout();
                using var reference = RenderFooter(component, result, hideControls: true);
                using var actual = RenderFooter(component, result, hideControls: false);
                var footer = Field<RectangleF>(component.Attributes, "_footer");
                bool hasSummary = false, textIntact = true;
                int statsLeft = (int)((PanelTestAdapter.Bounds(component, CanvasControl.Statistics).Left - footer.Left) * 2);
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < reference.Width; x++)
                {
                    int pixel = reference.GetPixel(x, y).ToArgb();
                    if (pixel == Color.White.ToArgb()) continue;
                    hasSummary |= x < statsLeft;
                    textIntact &= actual.GetPixel(x, y).ToArgb() == pixel;
                }
                Check(hasSummary, $"The compact footer has visible item/branch counts at {width}px width");
                Check(textIntact, $"Active tree tools and Units preserve the summary and Stats affordance at {width}px width");
            }
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static Bitmap RenderFooter(GH_Component component, PanelQueryResult result, bool hideControls)
    {
        var attributes = component.Attributes;
        var footer = Field<RectangleF>(attributes, "_footer");
        var fields = new[] { "_trim", "_simplify", "_flatten", "_reverse", "_units" }
            .Select(name => attributes.GetType().GetField(name, Instance)!).ToArray();
        var bounds = fields.Select(field => (RectangleF)field.GetValue(attributes)!).ToArray();
        var bitmap = new Bitmap((int)footer.Width * 2, (int)footer.Height * 2);
        try
        {
            // Render the same native footer twice. Keep Units.Y/Height and
            // Trim.Left fixed because the text uses those layout anchors.
            if (hideControls)
                for (int i = 0; i < fields.Length; i++)
                {
                    var moved = bounds[i];
                    if (fields[i].Name == "_units") moved.Offset(footer.Width + 100, 0);
                    else moved.Offset(0, footer.Height + 100);
                    fields[i].SetValue(attributes, moved);
                }
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            graphics.ScaleTransform(2, 2);
            graphics.TranslateTransform(-footer.X, -footer.Y);
            using var font = new Font("Arial", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = StringAlignment.Center
            };
            Invoke(attributes, "RenderFooter", graphics, result, font, format);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
        finally
        {
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(attributes, bounds[i]);
        }
    }

    private static async Task VerifyFilters(GH_Component component, GH_Canvas canvas, Assembly assembly, object session)
    {
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        SavePreview(component, canvas, "filter-modes");
        MenuRow(component, canvas, (int)PanelSearchMode.Length);
        Check(MenuKind(component) == "Comparison", "Choosing a geometry mode opens a readable comparison menu");
        SavePreview(component, canvas, "filter-comparisons");
        MenuRow(component, canvas, 0);
        CanvasEditorAdapter.Active(assembly).Submit("5");
        await Wait(session);
        Check(Options(session).SearchMode == PanelSearchMode.Length && Options(session).Search == "> 5" && Positions(session).SequenceEqual(new[] { 33 }),
            "Typing a bare threshold after Greater than retains the chosen comparison and matching geometry");
        Paint(component, canvas);
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.Area, 1, "12");
        Check(Options(session).Search == ">= 12" && Positions(session).SequenceEqual(new[] { 35 }),
            "Typing a bare threshold after At least retains its comparison and isolates rectangle area");
        SavePreview(component, canvas, "filter-area");
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.Volume, 4, "24");
        Check(Positions(session).SequenceEqual(new[] { 36 }), "Volume comparison includes the matching box");
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.Value, 0, "20");
        Check(Positions(session).SequenceEqual(new[] { 39 }), "Numeric value mode excludes geometry measurements");
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.ListLength, 1, "34");
        Check(Positions(session).SequenceEqual(Enumerable.Range(0, 34)),
            "Per-branch list length selects all items of a qualifying branch through the canvas menu");
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.ListLength, 0, "<= 6");
        Check(Options(session).Search == "<= 6" && Positions(session).SequenceEqual(Enumerable.Range(34, 6)),
            "An explicit pasted comparison overrides the menu operator and selects the smaller branch");
        SavePreview(component, canvas, "filter-list-length");
        await ChooseFilter(component, canvas, assembly, session, PanelSearchMode.Regex, 0, "^wall-[ab]$");
        Check(Positions(session).SequenceEqual(new[] { 37, 38 }), "Regex menu searches row text case-insensitively");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        CanvasEditorAdapter.Active(assembly).Submit("");
        await Wait(session);
        Check(Positions(session).Count == 40 && Options(session).SearchMode == PanelSearchMode.Regex,
            "Clearing the inline expression removes the filter while retaining its mode");
    }

    private static async Task VerifyBranches(GH_Component component, GH_Canvas canvas, Assembly assembly, object session)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        var view = rhino.Views.ActiveView ?? rhino.Views.Add("Filter checks", Rhino.Display.DefinedViewportProjection.Perspective,
            new Rectangle(0, 0, 640, 480), false);
        rhino.Views.ActiveView = view;
        Query(session, new PanelQueryOptions { TypeName = "Point" });
        await Wait(session);
        PanelTestAdapter.Resize(component, 360, 220);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
        var target = view.ActiveViewport.CameraTarget;
        Click(component, canvas, new RectangleF(list.X, list.Y, list.Width, 15));
        Check(Selected(component, session).SequenceEqual(Enumerable.Range(0, 32)) && Highlighted(component),
            "Branch heading selects and highlights matching rows including offscreen geometry");
        Check(view.ActiveViewport.CameraTarget == target, "Branch heading leaves the active camera unchanged");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Branch));
        MenuRow(component, canvas, 3);
        await Wait(session);
        Check(Positions(session).SequenceEqual(new[] { 34 }) && Selected(component, session).Count == 0 && !Highlighted(component) &&
            view.ActiveViewport.CameraTarget == target,
            "Choosing a branch filters without selecting rows or moving the camera");
        target = view.ActiveViewport.CameraTarget;
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Branch));
        MenuRow(component, canvas, 0);
        await Wait(session);
        Check(Selected(component, session).Count == 0 && !Highlighted(component) && view.ActiveViewport.CameraTarget == target,
            "All branches clears the branch highlight without reframing the whole tree");

        // Force a background query here: branch queries may finish inside the UI
        // time budget, leaving no pending focus intent to supersede.
        Query(session, new PanelQueryOptions { TypeName = "Point", SearchMode = PanelSearchMode.Regex, Search = ".*" });
        await Wait(session);
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Branch));
        MenuRow(component, canvas, 2);
        Query(session, new PanelQueryOptions { TypeName = "Point", BranchPath = "{1}" });
        await Wait(session);
        Check(Selected(component, session).Count == 0 && view.ActiveViewport.CameraTarget == target,
            "Replacing a branch query never selects rows or moves the camera");
        Query(session, new PanelQueryOptions());
        await Wait(session);
        EditPath(" 1 ");
        await Wait(session);
        Check(Options(session).BranchPath == "{1}" && Positions(session).SequenceEqual(Enumerable.Range(34, 6)),
            "A bare branch path selects the same rows as its braced form and displays canonically");
        EditPath(" 0; 0; 1 ");
        await Wait(session);
        Check(Options(session).BranchPath == "{0;0;1}" && Positions(session).Count == 0,
            "A semicolon-delimited path without braces is accepted as an exact nested path");
        EditPath("{0;0;1");
        await Wait(session, allowError: true);
        Paint(component, canvas);
        Check(Property<string?>(session, "Error") != null &&
            !Field<IEnumerable>(component.Attributes, "_rowHits").Cast<object>().Any(),
            "Malformed branch paths expose an error and no stale row actions");
        var branch = PanelTestAdapter.Bounds(component, CanvasControl.Branch);
        var point = new PointF(branch.X + 5, branch.Y + 5);
        var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs), Instance,
            null, new object[] { canvas, System.Drawing.Point.Round(point) }, null)!;
        component.Attributes.SetupTooltip(point, tooltip);
        Check(tooltip.Description.Contains(Property<string>(session, "Error")) && tooltip.Description.Contains("0;0;1"),
            "The branch tooltip explains malformed input and the supported unbraced syntax");
        EditPath("");
        await Wait(session);
        Check(Options(session).BranchPath == null && Positions(session).Count == 40,
            "Clearing a malformed branch path restores all branches");

        void EditPath(string path)
        {
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Branch));
            MenuRow(component, canvas, 1);
            CanvasEditorAdapter.Active(assembly).Submit(path);
        }
    }

    private static async Task VerifyCompactErrors(GH_Component component, GH_Canvas canvas, Assembly assembly, object session)
    {
        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
        Check(panel.Contains(PanelTestAdapter.MenuBounds(component)), "Filter menu stays inside the minimum half-height panel");
        MenuRow(component, canvas, (int)PanelSearchMode.Regex);
        Check(CanvasEditorAdapter.Active(assembly).TextAreaWidth >= Grasshopper.Global_Proc.UiAdjust(100),
            "Minimum panel still opens a readable filter editor");
        CanvasEditorAdapter.Active(assembly).Submit("[");
        await Wait(session, allowError: true);
        Check(Property<string?>(session, "Error") != null && Selected(component, session).Count == 0 && !Highlighted(component),
            "Invalid regex explains the error and clears stale selection and highlight");
        Paint(component, canvas);
        Check(!Field<IEnumerable>(component.Attributes, "_rowHits").Cast<object>().Any(),
            "Failed filters expose no stale row actions");
        var search = PanelTestAdapter.Bounds(component, CanvasControl.Search);
        var point = new PointF(search.X + 5, search.Y + 5);
        var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs), Instance,
            null, new object[] { canvas, System.Drawing.Point.Round(point) }, null)!;
        component.Attributes.SetupTooltip(point, tooltip);
        Check(tooltip.Description.Contains(Property<string>(session, "Error")) && tooltip.Title.Contains("regular expression"),
            "Compact search tooltip exposes the full error and active regex mode");
        SavePreview(component, canvas, "filter-error-half");
        Click(component, canvas, search);
        CanvasEditorAdapter.Active(assembly).Submit("Wall");
        await Wait(session);
        Check(Positions(session).Count == 2, "Editing a failed filter recovers normally");
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        MenuRow(component, canvas, (int)PanelSearchMode.ListLength);
        MenuRow(component, canvas, 0);
        CanvasEditorAdapter.Active(assembly).Submit("not a count");
        await Wait(session, allowError: true);
        Paint(component, canvas);
        Check(Property<string?>(session, "Error") != null && Options(session).SearchMode == PanelSearchMode.ListLength &&
            !Field<IEnumerable>(component.Attributes, "_rowHits").Cast<object>().Any(),
            "The seventh mode is reachable in Half view and invalid list counts expose no stale rows");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        CanvasEditorAdapter.Active(assembly).Submit("<= 6");
        await Wait(session);
        Check(Positions(session).SequenceEqual(Enumerable.Range(34, 6)),
            "Editing the list-length expression recovers from an invalid threshold in Half view");
    }

    private static async Task VerifyPersistence(GH_Component component, GH_Canvas canvas, Assembly assembly, object session)
    {
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = ">= 6" });
        await Wait(session);
        PanelTestAdapter.PreviewMode(component, "FilteredResults");
        var chunk = new GH_LooseChunk("Panel");
        Check(component.Write(chunk), "List-length filter settings serialize");
        var restored = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        restored.CreateAttributes();
        using var restoredDocument = new GH_Document();
        try
        {
            Check(restored.Read(chunk) && Options(PanelTestAdapter.Session(restored)).SearchMode == PanelSearchMode.ListLength &&
                Options(PanelTestAdapter.Session(restored)).Search == ">= 6", "List-length mode and comparison survive saved definitions");
            Check(PanelTestAdapter.PreviewMode(restored) == "FilteredResults", "Preview mode survives saved definitions");
            chunk.RemoveItem("PanelPreviewMode");
            Check(restored.Read(chunk) && PanelTestAdapter.PreviewMode(restored) == "Selected",
                "Older definitions default to Selected preview");
            chunk.SetInt32("PanelPreviewMode", 999);
            Check(restored.Read(chunk) && PanelTestAdapter.PreviewMode(restored) == "Selected",
                "Unknown saved preview modes fall back to Selected");
            chunk.RemoveItem("PanelSearchMode");
            Check(restored.Read(chunk) && Options(PanelTestAdapter.Session(restored)).SearchMode == PanelSearchMode.Text,
                "Older definitions default to legacy text search");
            chunk.SetInt32("PanelSearchMode", 999);
            Check(restored.Read(chunk) && Options(PanelTestAdapter.Session(restored)).SearchMode == PanelSearchMode.Text,
                "Unknown saved filter modes fall back safely to text");
        }
        finally { restored.RemovedFromDocument(restoredDocument); }
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
        await Wait(session);
        Check(Options(session).SearchMode == PanelSearchMode.Text && Options(session).Search.Length == 0,
            "Reset restores text mode and clears the comparison");
    }

    private static async Task ChooseFilter(GH_Component component, GH_Canvas canvas, Assembly assembly, object session,
        PanelSearchMode mode, int comparison, string expression)
    {
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        MenuRow(component, canvas, (int)mode);
        if (mode >= PanelSearchMode.Value) MenuRow(component, canvas, comparison);
        CanvasEditorAdapter.Active(assembly).Submit(expression);
        await Wait(session);
        Paint(component, canvas);
    }

    private static void MenuRow(GH_Component component, GH_Canvas canvas, int index)
    {
        var menu = Field<object>(component.Attributes, "_menu");
        for (int attempts = 0; attempts < 10; attempts++)
        {
            int offset = Property<int>(menu, "Offset"), visible = Property<int>(menu, "Visible");
            var bounds = PanelTestAdapter.MenuBounds(component);
            if (index >= offset && index < offset + visible)
            {
                Click(component, canvas, new RectangleF(bounds.X, bounds.Y + (index - offset) * 20, bounds.Width, 20));
                return;
            }
            Click(component, canvas, new RectangleF(index < offset ? bounds.X : bounds.X + bounds.Width / 2,
                bounds.Bottom - 20, bounds.Width / 2, 20));
        }
        throw new InvalidOperationException("Could not reach filter menu item");
    }

    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds)
    {
        var point = new PointF(bounds.X + Math.Min(40, bounds.Width / 2), bounds.Y + bounds.Height / 2);
        var response = component.Attributes.RespondToMouseDown(canvas,
            new GH_CanvasMouseEvent(System.Drawing.Point.Empty, point, Forms.MouseButtons.Left, 1, 0));
        if (response != GH_ObjectResponse.Handled) throw new InvalidOperationException("Canvas control did not handle its click");
    }

    private static void Paint(GH_Component component, GH_Canvas canvas)
    {
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
    }

    private static void SavePreview(GH_Component component, GH_Canvas canvas, string name)
    {
        var bounds = component.Attributes.Bounds;
        using var bitmap = new Bitmap((int)(bounds.Width + 30) * 2, (int)(bounds.Height + 30) * 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        graphics.ScaleTransform(2, 2);
        graphics.TranslateTransform(15 - bounds.X, 15 - bounds.Y);
        PanelTestAdapter.Render(component, canvas, graphics);
        bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
    }

    private static async Task Wait(object session, bool allowError = false)
    {
        var timeout = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("Filter query did not complete");
            await Task.Delay(10);
        }
        if (!allowError && Property<string?>(session, "Error") is string error) throw new InvalidOperationException(error);
    }

    private static PanelQueryOptions Options(object session) => Property<PanelQueryOptions>(session, "Options");
    private static IReadOnlyList<int> Positions(object session) => Property<PanelQueryResult>(session, "Result").RowPositions;
    private static IReadOnlyList<int> Selected(GH_Component component, object session) =>
        (IReadOnlyList<int>)Invoke(Field<object>(component.Attributes, "_selection"), "OrderedPositions", Property<PanelQueryResult>(session, "Result"))!;
    private static bool Highlighted(GH_Component component) => Property<int>(Field<object>(component, "_highlight"), "Count") > 0;
    private static string MenuKind(GH_Component component) => Property<object>(Field<object>(component.Attributes, "_menu"), "Kind").ToString()!;
    private static void Query(object session, PanelQueryOptions options) => Invoke(session, "UpdateQuery", options);
    private static object? Invoke(object owner, string method, params object[] arguments) => owner.GetType().GetMethod(method, Instance)!.Invoke(owner, arguments);
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }
}
