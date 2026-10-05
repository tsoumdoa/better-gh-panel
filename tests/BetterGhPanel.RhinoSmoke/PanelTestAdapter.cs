using System;
using System.Drawing;
using System.Reflection;
using Eto.Forms;
using Grasshopper.Kernel;

internal enum CanvasControl { Panel, Type, Sort, Branch, Search, Jump, Reset, Resize, List }

// The .gha is loaded separately. Keep plugin-owned reflection in this adapter.
internal static class PanelTestAdapter
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object Session(GH_Component component) => Property<object>(component, "Session");
    internal static bool HasSession(GH_Component component) => Field<object?>(component, "_session") != null;
    internal static int Preset(GH_Component component) => Property<int>(component, "PreviewSize");
    internal static SizeF Size(GH_Component component) => new SizeF(Property<float>(component, "CanvasWidth"), Property<float>(component, "CanvasHeight"));
    internal static TextBox Search(Form inspector) => Field<TextBox>(inspector, "_search");
    internal static string Detail(Form inspector) => Field<TextArea>(inspector, "_detail").Text ?? string.Empty;

    internal static void SelectRow(Form inspector, int position)
    {
        var rows = Field<object>(inspector, "_rows");
        rows.GetType().GetMethod("SelectPosition", PrivateInstance)!.Invoke(rows, new object[] { position });
    }

    internal static void Resize(GH_Component component, float width, float height) =>
        component.GetType().GetMethod("SetCanvasSize", PrivateInstance)!.Invoke(component, new object[] { width, height });
    internal static RectangleF Bounds(GH_Component component, CanvasControl control) =>
        Field<RectangleF>(component.Attributes, "_" + control.ToString().ToLowerInvariant());
    internal static RectangleF MenuBounds(GH_Component component) => Property<RectangleF>(Field<object>(component.Attributes, "_menu"), "Bounds");

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, PrivateInstance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
}

// Host-owned textbox internals are isolated from the behavioral assertions too.
internal sealed class CanvasEditorAdapter
{
    private readonly object _editor;
    private readonly Type _type;
    private CanvasEditorAdapter(object editor, Type type) { _editor = editor; _type = type; }

    internal static CanvasEditorAdapter Active(Assembly assembly)
    {
        var type = assembly.GetType("BetterGhPanel.UI.CanvasTextInput", true)!;
        var active = type.GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        var arguments = new object?[] { null };
        if (active == null || !(bool)active.GetType().GetMethod("TryGetTarget")!.Invoke(active, arguments)!)
            throw new InvalidOperationException("Host inline textbox is not active.");
        return new CanvasEditorAdapter(arguments[0]!, type);
    }

    internal void Submit(string text)
    {
        var input = (System.Windows.Forms.TextBox)_type.BaseType!.GetField("m_inputTextBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor)!;
        input.Text = text;
        _type.GetMethod("RespondToEnter")!.Invoke(_editor, Array.Empty<object>());
    }

    internal void AcceptAgain(string text) =>
        _type.GetMethod("HandleTextInputAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_editor, new object[] { text });
}
