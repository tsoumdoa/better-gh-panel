using System;
using System.Drawing;
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

internal static partial class FilterUiChecks
{
    // This fixture injects the caller's capability set into the real menu and
    // sends canvas mouse events through the ordinary attribute handlers.
    internal static async Task VerifyTypedChoicesAndStaleEditors(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        var paired = new Param_GenericObject();
        source.CreateAttributes();
        paired.CreateAttributes();
        foreach (double number in new[] { 1d, 2d, 3d }) source.PersistentData.Append(new GH_Number(number), new GH_Path(0));
        source.PersistentData.Append(new GH_String("four"), new GH_Path(0));
        for (int index = 0; index < 4; index++) paired.PersistentData.Append(new GH_String("paired " + index), new GH_Path(0));
        document.AddObject(source, false);
        document.AddObject(paired, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        component.Params.Input[1].AddSource(paired);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        using var canvas = new GH_Canvas { Size = new Size(900, 700) };
        component.Attributes.Pivot = new PointF(50, 50);
        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();
        await Wait(session);
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));

        await VerifyQueryMenuUndo(component, canvas, document, session);
        foreach (var replacement in new[] { "Jump", "Trim", "Units", "Dismiss" })
            await VerifyReplacedEditor(component, canvas, assembly, document, session, comparison: false, replacement);
        await VerifyReplacedEditor(component, canvas, assembly, document, session, comparison: true, "Jump");
        await VerifyReplacedEditor(component, canvas, assembly, document, session, comparison: true, "Dismiss");

        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 1" });
        await Wait(session);
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        var menu = Field<object>(component.Attributes, "_menu");
        Invoke(menu, "Close");
        var kind = Enum.Parse(assembly.GetType("BetterGhPanel.UI.CanvasMenuKind", true)!, "SearchMode");
        menu.GetType().GetMethod("Toggle", Instance)!.Invoke(menu, new object?[]
        {
            kind, Property<PanelSnapshot>(session, "Snapshot"), Options(session), Property<PanelQueryResult>(session, "Result"),
            new[] { PanelSearchMode.Text, PanelSearchMode.Regex, PanelSearchMode.ListLength }
        });
        Paint(component, canvas);
        Check(MenuIndex(menu, "Geometry · area") >= 0 && MenuIndex(menu, "Numeric value") == -1 &&
            MenuIndex(menu, "Geometry · length") == -1 && MenuIndex(menu, "Geometry · volume") == -1,
            "The actual canvas menu retains the saved area mode while hiding unsupported earlier choices");
        Check(MenuIndex(menu, "List length · per branch") >= Property<int>(menu, "Visible"),
            "The compact canvas requires paging to reach the dynamically positioned list-length choice");
        var menuBounds = PanelTestAdapter.MenuBounds(component);
        int oldOffset = Property<int>(menu, "Offset");
        var firstChoice = new RectangleF(menuBounds.X, menuBounds.Y, menuBounds.Width, 20);
        var outputBeforeClip = OutputSignature(component);
        component.Attributes.ExpireLayout();
        component.Attributes.PerformLayout();
        PaintClip(component, canvas, new RectangleF(firstChoice.X, firstChoice.Y, firstChoice.Width / 2, firstChoice.Height));
        Click(component, canvas, firstChoice);
        Click(component, canvas, new RectangleF(menuBounds.X + menuBounds.Width / 2, menuBounds.Bottom - 20, menuBounds.Width / 2, 20));
        Check(MenuKind(component) == "SearchMode" && Property<int>(menu, "Offset") == oldOffset &&
            Options(session).SearchMode == PanelSearchMode.Area && OutputSignature(component).SequenceEqual(outputBeforeClip),
            "Partial menu rows and an unpainted pager cannot change query or D/S/B/F");
        PaintClip(component, canvas, new RectangleF(menuBounds.X + menuBounds.Width / 2, menuBounds.Bottom - 20, menuBounds.Width / 2, 20));
        Click(component, canvas, new RectangleF(menuBounds.X + menuBounds.Width / 2, menuBounds.Bottom - 20, menuBounds.Width / 2, 20));
        Check(Property<int>(menu, "Offset") > oldOffset && !Property<bool>(menu, "HasPaintedTargets"),
            "Paging invalidates the old menu targets before the next paint");
        var before = Options(session);
        Click(component, canvas, new RectangleF(menuBounds.X, menuBounds.Y, menuBounds.Width, 20));
        Check(MenuKind(component) == "SearchMode" && Options(session).SearchMode == before.SearchMode &&
            Options(session).Search == before.Search, "An unpainted menu page cannot apply a choice");
        PaintClip(component, canvas, new RectangleF(menuBounds.X, menuBounds.Y, menuBounds.Width / 2, 20));
        Click(component, canvas, new RectangleF(menuBounds.X, menuBounds.Y, menuBounds.Width, 20));
        Check(MenuKind(component) == "SearchMode" && Options(session).SearchMode == before.SearchMode &&
            Options(session).Search == before.Search, "A partially repainted new menu page cannot apply a choice");
        MenuRow(component, canvas, MenuIndex(menu, "List length · per branch"));
        Check(MenuKind(component) == "Comparison", "Clicking the paged list-length choice opens its comparison menu");
        MenuRow(component, canvas, MenuIndex(menu, "At least  ≥"));
        var comparisonEditor = CanvasEditorAdapter.Active(assembly);
        var completedCallback = comparisonEditor.CaptureAccepted();
        comparisonEditor.Submit("4");
        await Wait(session);
        Check(Options(session).SearchMode == PanelSearchMode.ListLength && Options(session).Search == ">= 4" &&
            Positions(session).Count == 4, "A choice after absent modes applies list length rather than the row's old enum value");
        int completedUndoCount = document.UndoServer.UndoCount;
        completedCallback("9");
        Check(Options(session).Search == ">= 4" && document.UndoServer.UndoCount == completedUndoCount,
            "A callback captured from an already accepted editor cannot submit twice");

        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Sort));
        MenuRow(component, canvas, MenuIndex(menu, "Value descending"));
        await Wait(session);
        Check(Options(session).SortMode == PanelSortMode.ValueDescending, "An actual sort-menu click applies its typed choice");
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
        MenuRow(component, canvas, MenuIndex(menu, "Filtered results"));
        Check(PanelTestAdapter.PreviewMode(component) == "FilteredResults", "An actual preview-menu click applies its typed choice");
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        MenuRow(component, canvas, MenuIndex(menu, "mm → m"));
        await Wait(session);
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        Check(PanelTestAdapter.Conversion(component) == PanelUnitConversion.MillimetersToMeters &&
            component.Params.Output[0].VolatileData.AllData(true).OfType<GH_Number>().Select(value => value.Value)
                .OrderBy(value => value).SequenceEqual(new[] { 0.001, 0.002, 0.003 }),
            "An actual units-menu click keeps numeric output conversion math");

        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "1" });
        await Wait(session);
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
        Check(PanelTestAdapter.MenuOpen(component), "The search menu opens before the input changes");
        source.PersistentData.Clear();
        source.PersistentData.Append(new GH_Number(99), new GH_Path(0));
        source.ExpireSolution(false);
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        Check(!PanelTestAdapter.MenuOpen(component) && Options(session).SearchMode == PanelSearchMode.Regex && Options(session).Search == "1",
            "Input replacement dismisses the old menu without changing the saved query");
        await VerifyStaleEditor(component, canvas, assembly, document, source, session, comparison: false);
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Area, DisplayFilterValue = true });
        await Wait(session);
        await VerifyStaleEditor(component, canvas, assembly, document, source, session, comparison: true);
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
    }

    private static async Task VerifyReplacedEditor(GH_Component component, GH_Canvas canvas, Assembly assembly,
        GH_Document document, object session, bool comparison, string replacement)
    {
        Query(session, new PanelQueryOptions());
        await Wait(session);
        Paint(component, canvas);
        if (comparison)
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
            var menu = Field<object>(component.Attributes, "_menu");
            MenuRow(component, canvas, MenuIndex(menu, "Numeric value"));
            MenuRow(component, canvas, MenuIndex(menu, "Greater than  >"));
        }
        else Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        var old = CanvasEditorAdapter.Active(assembly);
        var captured = old.CaptureAccepted();
        if (replacement == "Dismiss") CanvasEditorAdapter.CloseActive(assembly);
        else if (replacement == "Units")
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
            var menu = Field<object>(component.Attributes, "_menu");
            MenuRow(component, canvas, MenuIndex(menu, "Decimal places…"));
        }
        else Click(component, canvas, PanelTestAdapter.Bounds(component,
            replacement == "Jump" ? CanvasControl.Jump : CanvasControl.Trim));
        Check(old.AcceptedIsNull, "Replacing or dismissing the query editor releases its registered acceptance callback");
        var saved = Options(session);
        var output = OutputSignature(component);
        document.UndoServer.Clear();
        document.IsModified = false;
        old.AcceptAgain("999");
        captured("999");
        var after = Options(session);
        Check(after.Search == saved.Search && after.SearchMode == saved.SearchMode && after.DisplayFilterValue == saved.DisplayFilterValue &&
            after.TypeName == saved.TypeName && after.OnlyInvalid == saved.OnlyInvalid && after.BranchPath == saved.BranchPath &&
            after.SortMode == saved.SortMode && document.UndoServer.UndoCount == 0 && !document.IsModified &&
            OutputSignature(component).SequenceEqual(output),
            "A captured " + (comparison ? "comparison" : "search") + " callback cannot mutate state after " + replacement + " replaces or dismisses it");
        CanvasEditorAdapter.CloseActive(assembly);
    }

    private static async Task VerifyQueryMenuUndo(GH_Component component, GH_Canvas canvas, GH_Document document, object session)
    {
        Query(session, new PanelQueryOptions());
        await Solve();
        Check(component.Params.Output[0].VolatileData.DataCount == 4, "Undo fixture starts with four typed source values");
        var originalOutputs = OutputSignature(component);
        document.UndoServer.Clear();
        document.IsModified = false;
        await Choose(CanvasControl.Type, "Invalid only");
        Check(Options(session).OnlyInvalid && Positions(session).Count == 0 && component.Params.Output[0].VolatileData.DataCount == 0 &&
            document.UndoServer.UndoCount == 1 && document.IsModified,
            "The invalid-only menu records an undo and produces the empty filtered output");
        var invalidOutputs = OutputSignature(component);
        await CheckArchive(true, null);
        document.UndoServer.PerformUndo();
        await Solve();
        Check(!Options(session).OnlyInvalid && component.Params.Output[0].VolatileData.DataCount == 4 &&
            OutputSignature(component).SequenceEqual(originalOutputs),
            "Undo restores typed D and the original S/B/F trees after invalid-only leaves no results");
        document.UndoServer.PerformRedo();
        await Solve();
        Check(Options(session).OnlyInvalid && component.Params.Output[0].VolatileData.DataCount == 0 &&
            OutputSignature(component).SequenceEqual(invalidOutputs),
            "Redo restores the empty invalid-only result");
        await Choose(CanvasControl.Type, "Num · Number");
        Check(Options(session).OnlyInvalid && Options(session).TypeName == "Number" && Positions(session).Count == 0 &&
            component.Params.Output[0].VolatileData.DataCount == 0,
            "A type filter remains saved and undoable even while invalid-only yields no matches");
        await CheckArchive(true, "Number");
        int undoCount = document.UndoServer.UndoCount;
        document.IsModified = false;
        await Choose(CanvasControl.Type, "Num · Number");
        Check(document.UndoServer.UndoCount == undoCount && !document.IsModified,
            "Choosing the already active type creates no undo or document modification");
        document.UndoServer.PerformUndo();
        await Solve();
        Check(Options(session).OnlyInvalid && Options(session).TypeName == null && component.Params.Output[0].VolatileData.DataCount == 0,
            "Undo restores invalid-only without the type restriction");
        document.UndoServer.PerformUndo();
        await Solve();
        Check(!Options(session).OnlyInvalid && Options(session).TypeName == null && component.Params.Output[0].VolatileData.DataCount == 4 &&
            OutputSignature(component).SequenceEqual(originalOutputs),
            "A second undo restores the original output");
        document.UndoServer.PerformRedo();
        await Solve();
        document.UndoServer.PerformRedo();
        await Solve();
        Check(Options(session).OnlyInvalid && Options(session).TypeName == "Number" && component.Params.Output[0].VolatileData.DataCount == 0,
            "Redo reapplies both saved filters and their empty output");

        Query(session, new PanelQueryOptions());
        await Solve();
        document.UndoServer.Clear();
        await Choose(CanvasControl.Sort, "Value descending");
        Check(Options(session).SortMode == PanelSortMode.ValueDescending, "Sort menu changes are recorded");
        int sortUndoCount = document.UndoServer.UndoCount;
        document.IsModified = false;
        await Choose(CanvasControl.Sort, "Value descending");
        Check(document.UndoServer.UndoCount == sortUndoCount && !document.IsModified,
            "Choosing the current sort creates no undo or document modification");
        document.UndoServer.PerformUndo();
        await Solve();
        Check(Options(session).SortMode == PanelSortMode.SourceOrder, "Undo restores source sorting");
        document.UndoServer.PerformRedo();
        await Solve();
        Check(Options(session).SortMode == PanelSortMode.ValueDescending, "Redo restores sorting");
        await Choose(CanvasControl.Branch, "{0}   ·   4");
        Check(Options(session).BranchPath == "{0}", "Branch menu chooses its exact path");
        int branchUndoCount = document.UndoServer.UndoCount;
        document.IsModified = false;
        await Choose(CanvasControl.Branch, "{0}   ·   4");
        Check(document.UndoServer.UndoCount == branchUndoCount && !document.IsModified,
            "Choosing the current branch creates no undo or document modification");
        document.UndoServer.PerformUndo();
        await Solve();
        Check(Options(session).BranchPath == null, "Undo restores all branches");
        document.UndoServer.PerformRedo();
        await Solve();
        Check(Options(session).BranchPath == "{0}", "Redo restores the branch filter");
        Query(session, new PanelQueryOptions());
        await Solve();

        async Task Choose(CanvasControl control, string label)
        {
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, control));
            var menu = Field<object>(component.Attributes, "_menu");
            int index = MenuIndex(menu, label);
            Check(index >= 0, "Expected menu choice exists: " + label);
            MenuRow(component, canvas, index);
            await Solve();
        }
        async Task Solve()
        {
            await Wait(session);
            document.NewSolution(false, GH_SolutionMode.Silent);
            await Wait(session);
        }
        async Task CheckArchive(bool invalid, string? type)
        {
            var chunk = new GH_LooseChunk("Panel");
            Check(component.Write(chunk), "Filtered query saves to an archive");
            var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            using var restoredDocument = new GH_Document { Enabled = true };
            try
            {
                Check(restored.Read(chunk) && Options(PanelTestAdapter.Session(restored)).OnlyInvalid == invalid &&
                    Options(PanelTestAdapter.Session(restored)).TypeName == type,
                    "Empty-result invalid and type filters survive archive read");
                var restoredSource = new Param_GenericObject();
                restoredSource.CreateAttributes();
                foreach (double value in new[] { 1d, 2d, 3d }) restoredSource.PersistentData.Append(new GH_Number(value), new GH_Path(0));
                restoredSource.PersistentData.Append(new GH_String("four"), new GH_Path(0));
                restoredDocument.AddObject(restoredSource, false);
                restoredDocument.AddObject(restored, false);
                restored.Params.Input[0].AddSource(restoredSource);
                restoredDocument.NewSolution(false, GH_SolutionMode.Silent);
                await Wait(PanelTestAdapter.Session(restored));
                Check(Positions(PanelTestAdapter.Session(restored)).Count == 0 && restored.Params.Output[0].VolatileData.DataCount == 0,
                    "The reloaded empty-result filter continues to exclude all source values");
            }
            finally { restored.DocumentContextChanged(restoredDocument, GH_DocumentContext.Close); }
        }
    }

    private static void PaintClip(GH_Component component, GH_Canvas canvas, RectangleF clip)
    {
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        canvas.Viewport.ApplyProjection(graphics);
        graphics.SetClip(clip);
        PanelTestAdapter.Render(component, canvas, graphics);
    }

    private static async Task VerifyStaleEditor(GH_Component component, GH_Canvas canvas, Assembly assembly,
        GH_Document document, Param_GenericObject source, object session, bool comparison)
    {
        Paint(component, canvas);
        if (comparison)
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Filter));
            var menu = Field<object>(component.Attributes, "_menu");
            MenuRow(component, canvas, MenuIndex(menu, "List length · per branch"));
            MenuRow(component, canvas, MenuIndex(menu, "Greater than  >"));
        }
        else Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        var editor = CanvasEditorAdapter.Active(assembly);
        var capturedCallback = editor.CaptureAccepted();
        var oldSnapshot = Property<PanelSnapshot>(session, "Snapshot");
        source.PersistentData.Clear();
        source.PersistentData.Append(new GH_Number(comparison ? 200 : 100), new GH_Path(0));
        source.ExpireSolution(false);
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        Check(!ReferenceEquals(oldSnapshot, Property<PanelSnapshot>(session, "Snapshot")) &&
            !CanvasEditorAdapter.IsActive(assembly) && editor.AcceptedIsNull,
            "Replacing input closes the query editor and clears its callback");
        var saved = Options(session);
        var output = OutputSignature(component);
        document.UndoServer.Clear();
        document.IsModified = false;
        editor.AcceptAgain("999");
        capturedCallback("999"); // Also simulate a host callback captured before cancellation.
        var after = Options(session);
        Check(after.SearchMode == saved.SearchMode && after.Search == saved.Search &&
            after.DisplayFilterValue == saved.DisplayFilterValue && document.UndoServer.UndoCount == 0 &&
            !document.IsModified && OutputSignature(component).SequenceEqual(output),
            "Late " + (comparison ? "comparison" : "search") + " acceptance leaves mode, expression, display, undo, document and D/S/B/F unchanged");
    }

    private static int MenuIndex(object menu, string label)
    {
        int count = Property<int>(menu, "Count");
        var method = menu.GetType().GetMethod("Label", Instance)!;
        for (int index = 0; index < count; index++)
            if ((string)method.Invoke(menu, new object[] { index, PanelUnits.AutomaticDecimals })! == label) return index;
        return -1;
    }

    private static string[] OutputSignature(GH_Component component) => component.Params.Output.Select(output =>
        string.Join("|", output.VolatileData.Paths.Select(path => path + ":" +
            string.Join(",", output.VolatileData.get_Branch(path).Cast<IGH_Goo?>().Select(item => item?.ToString() ?? "<null>"))))).ToArray();
}
