using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

internal static partial class FilterUiChecks
{
    // This fixture injects the caller's capability set into the real menu and
    // sends canvas mouse events through the ordinary attribute handlers.
    private static async Task VerifyTypedChoicesAndStaleEditors(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        foreach (double number in new[] { 1d, 2d, 3d }) source.PersistentData.Append(new GH_Number(number), new GH_Path(0));
        source.PersistentData.Append(new GH_String("four"), new GH_Path(0));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        using var canvas = new GH_Canvas { Size = new Size(900, 700) };
        component.Attributes.Pivot = new PointF(50, 50);
        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();
        await Wait(session);
        Paint(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));

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
        Click(component, canvas, new RectangleF(menuBounds.X + menuBounds.Width / 2, menuBounds.Bottom - 20, menuBounds.Width / 2, 20));
        Check(Property<int>(menu, "Offset") > oldOffset && !Property<bool>(menu, "HasPaintedTargets"),
            "Paging invalidates the old menu targets before the next paint");
        var before = Options(session);
        Click(component, canvas, new RectangleF(menuBounds.X, menuBounds.Y, menuBounds.Width, 20));
        Check(MenuKind(component) == "SearchMode" && Options(session).SearchMode == before.SearchMode &&
            Options(session).Search == before.Search, "An unpainted menu page cannot apply a choice");
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
