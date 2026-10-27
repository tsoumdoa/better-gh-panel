using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

internal enum CanvasControl { Panel, Type, Sort, Branch, Search, Filter, Jump, Reset, Resize, List, Trim, Flatten, Reverse, Simplify, CreateSet, Units, Display, Preview, Move, Statistics, SelectAll, Close, Restore, Track }

// The .gha is loaded separately. Keep plugin-owned reflection in this adapter.
internal static class PanelTestAdapter
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object Session(GH_Component component) => Property<object>(component, "Session");
    internal static bool HasSession(GH_Component component) => Field<object?>(component, "_session") != null;
    internal static bool GeometryRunning(object session) => Field<object?>(Field<object>(session, "_geometry"), "_running") != null;

    internal static Action SaveGeometryResolver(object session)
    {
        var preparation = Field<object?>(session, "_geometryPreparationResolver");
        var legacy = Field<object?>(session, "_geometryDetailResolver");
        string method = preparation != null ? "SetGeometryPreparationResolver" : "SetGeometryDetailResolver";
        var resolver = preparation ?? legacy;
        return () => session.GetType().GetMethod(method, PrivateInstance)!.Invoke(session, new[] { resolver });
    }
    internal static int Preset(GH_Component component) => Property<int>(component, "PreviewSize");
    internal static string PreviewMode(GH_Component component) => Property<object>(component, "PreviewMode").ToString()!;
    internal static bool ShowItemOrder(GH_Component component) => Property<bool>(component, "ShowItemOrder");
    internal static void ShowItemOrder(GH_Component component, bool show) =>
        component.GetType().GetMethod("SetShowItemOrder", PrivateInstance)!.Invoke(component, new object[] { show });
    internal static void PreviewMode(GH_Component component, string mode)
    {
        var method = component.GetType().GetMethod("SetPreviewMode", PrivateInstance)!;
        method.Invoke(component, new[] { Enum.Parse(method.GetParameters()[0].ParameterType, mode) });
    }
    internal static SizeF Size(GH_Component component) => new SizeF(Property<float>(component, "CanvasWidth"), Property<float>(component, "CanvasHeight"));
    internal static bool Minimized(GH_Component component) => Property<bool>(component, "PanelMinimized");
    internal static void Minimize(GH_Component component, bool minimized) =>
        component.GetType().GetMethod("SetPanelMinimized", PrivateInstance)!.Invoke(component, new object[] { minimized });
    internal static string CapsuleSummary(GH_Component component) =>
        (string)component.Attributes.GetType().GetMethod("CapsuleSummary", PrivateInstance)!.Invoke(component.Attributes, Array.Empty<object>())!;
    internal static int Hits(GH_Component component, string field) => ((ICollection)Field<object>(component.Attributes, field)).Count;
    internal static int TrimDepth(GH_Component component) => Property<int>(component, "TrimDepth");
    internal static bool TreeFlag(GH_Component component, string name) => Property<bool>(component, name + "Tree");
    internal static void TreeTools(GH_Component component, int depth, bool flatten, bool reverse, bool simplify, bool createSet = false) =>
        component.GetType().GetMethod("SetTreeOperations", PrivateInstance)!.Invoke(component, new object[] { depth, flatten, reverse, simplify, createSet });
    internal static BetterGhPanel.Core.PanelUnitConversion Conversion(GH_Component component) => Property<BetterGhPanel.Core.PanelUnitConversion>(component, "UnitConversion");
    internal static int Decimals(GH_Component component) => Property<int>(component, "DecimalPlaces");
    internal static int Offset(GH_Component component) => Field<int>(component.Attributes, "_offset");
    internal static int SelectedPosition(GH_Component component)
    {
        var selection = Field<object>(component.Attributes, "_selection");
        var result = Session(component).GetType().GetProperty("Result")!.GetValue(Session(component));
        var positions = (System.Collections.Generic.IReadOnlyList<int>)selection.GetType().GetMethod("OrderedPositions", PrivateInstance)!
            .Invoke(selection, new[] { result })!;
        return positions.Count == 0 ? -1 : positions[0];
    }
    internal static bool MenuOpen(GH_Component component) => Property<object>(Field<object>(component.Attributes, "_menu"), "Kind").ToString() != "None";
    internal static void ResetView(GH_Component component) => component.GetType().GetMethod("ResetPanelView", PrivateInstance)!.Invoke(component, Array.Empty<object>());
    internal static void Units(GH_Component component, BetterGhPanel.Core.PanelUnitConversion conversion, int decimals) =>
        component.GetType().GetMethod("SetUnits", PrivateInstance)!.Invoke(component, new object[] { conversion, decimals });
    internal static string CopyValue(GH_Component component, BetterGhPanel.Core.PanelSnapshot snapshot, int position) =>
        (string)component.GetType().GetMethod("CopyValue", PrivateInstance)!.Invoke(component, new object[] { snapshot, position })!;

    internal static void Resize(GH_Component component, float width, float height) =>
        component.GetType().GetMethod("SetCanvasSize", PrivateInstance)!.Invoke(component, new object[] { width, height });
    internal static RectangleF Bounds(GH_Component component, CanvasControl control) =>
        Field<RectangleF>(component.Attributes, "_" + char.ToLowerInvariant(control.ToString()[0]) + control.ToString().Substring(1));
    internal static RectangleF MenuBounds(GH_Component component) => Property<RectangleF>(Field<object>(component.Attributes, "_menu"), "Bounds");
    internal static string RowMenuLabel(GH_Component component, int index) =>
        Property<string>(Field<Array>(component.Attributes, "_rowMenuActions").GetValue(index)!, "Label");
    internal static void SwapRowMenuActions(GH_Component component, int first, int second)
    {
        foreach (var field in new[] { "_rowMenuActions", "_rowMenuPendingActions" })
        {
            var actions = Field<Array>(component.Attributes, field);
            var saved = actions.GetValue(first);
            actions.SetValue(actions.GetValue(second), first);
            actions.SetValue(saved, second);
        }
    }

    internal static void Render(GH_Component component, GH_Canvas canvas, Graphics graphics) =>
        component.Attributes.GetType().GetMethod("Render", PrivateInstance)!
            .Invoke(component.Attributes, new object[] { canvas, graphics, GH_CanvasChannel.Objects });

    internal static void CanvasClick(GH_Canvas canvas, PointF point,
        System.Windows.Forms.MouseButtons button = System.Windows.Forms.MouseButtons.Left, int clicks = 1)
    {
        var location = Point.Round(canvas.Viewport.ProjectPoint(point));
        var args = new System.Windows.Forms.MouseEventArgs(button, clicks, location.X, location.Y, 0);
        // Raise the host's normal control event so Grasshopper performs its own
        // hit dispatch before the panel's blank-canvas observer runs.
        foreach (string name in new[] { "OnMouseDown", "OnMouseUp" })
            typeof(System.Windows.Forms.Control).GetMethod(name, PrivateInstance)!.Invoke(canvas, new object[] { args });
    }

    internal static GH_TooltipDisplayEventArgs Tooltip(GH_Component component, GH_Canvas canvas, int sourcePosition)
    {
        foreach (var hit in Field<IEnumerable>(component.Attributes, "_rowHits"))
        {
            if (Property<int>(hit, "SourcePosition") != sourcePosition) continue;
            var bounds = Property<RectangleF>(hit, "Bounds");
            var point = new PointF(bounds.X + 45, bounds.Y + bounds.Height / 2);
            var args = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
            component.Attributes.SetupTooltip(point, args);
            return args;
        }
        throw new InvalidOperationException("Tooltip row is not visible.");
    }

    internal static RectangleF RowBounds(GH_Component component, int sourcePosition)
    {
        foreach (var hit in Field<IEnumerable>(component.Attributes, "_rowHits"))
            if (Property<int>(hit, "SourcePosition") == sourcePosition) return Property<RectangleF>(hit, "Bounds");
        throw new InvalidOperationException("Row is not visible.");
    }

    internal static string DisplayValue(GH_Component component, BetterGhPanel.Core.PanelSnapshot snapshot, int position) =>
        (string)component.GetType().GetMethod("DisplayValue", PrivateInstance)!
            .Invoke(component, new object[] { snapshot, snapshot.Rows[position], position })!;

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, PrivateInstance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
}

// Host-owned textbox internals are isolated from the behavioral assertions too.
internal sealed class CanvasEditorAdapter
{
    private readonly object _editor;
    private readonly Type _type;
    private CanvasEditorAdapter(object editor, Type type) { _editor = editor; _type = type; }

    internal static bool IsActive(Assembly assembly)
    {
        var type = assembly.GetType("BetterGhPanel.UI.CanvasTextInput", true)!;
        var active = type.GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        var arguments = new object?[] { null };
        return active != null && (bool)active.GetType().GetMethod("TryGetTarget")!.Invoke(active, arguments)! &&
            (bool)type.GetProperty("IsTextInput")!.GetValue(arguments[0])!;
    }

    internal static CanvasEditorAdapter Active(Assembly assembly)
    {
        var type = assembly.GetType("BetterGhPanel.UI.CanvasTextInput", true)!;
        var active = type.GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        var arguments = new object?[] { null };
        if (active == null || !(bool)active.GetType().GetMethod("TryGetTarget")!.Invoke(active, arguments)!)
            throw new InvalidOperationException("Host inline textbox is not active.");
        return new CanvasEditorAdapter(arguments[0]!, type);
    }

    private System.Windows.Forms.TextBox Input =>
        (System.Windows.Forms.TextBox)_type.BaseType!.GetField("m_inputTextBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor)!;
    internal int TextAreaWidth => Input.ClientSize.Width;
    internal bool AcceptedIsNull => _type.GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor) == null;
    internal Action<string> CaptureAccepted() => (Action<string>)_type.GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor)!;

    internal void Submit(string text)
    {
        Input.Text = text;
        _type.GetMethod("RespondToEnter")!.Invoke(_editor, Array.Empty<object>());
    }

    internal void AcceptAgain(string text) =>
        _type.GetMethod("HandleTextInputAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_editor, new object[] { text });
}
