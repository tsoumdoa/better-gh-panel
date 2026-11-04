using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

// Native renderer captures and wire/clipboard checks for the presented-number contract.
internal static class WysiwygValuesChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, args);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        var attributes = component.Attributes;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0, 2, 52);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(1263.4718, 0, 0))), path);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(1334.12, 0, 0))), path);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 2);
        Call(component, "SetUnits", PanelUnitConversion.None, 2, (double?)50);
        await fixture.Ready(() => fixture.Result.Snapshot != null && Property<PanelQueryOptions>(fixture.Session, "Options").RoundUpFactor == 50);
        var options = Property<PanelQueryOptions>(fixture.Session, "Options");
        options.DisplayFilterValue = true; options.SearchMode = PanelSearchMode.Length;
        fixture.Query(options);
        Call(component, "SetOutputValues", PanelOutputValues.Values);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().FirstOrDefault()?.Value == 1300);
        fixture.SelectAt(0);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.AllData(false).OfType<GH_Number>().FirstOrDefault()?.Value == 1300);
        Check(fixture.Result.Statistics.Minimum == 1300 && fixture.Result.Statistics.Maximum == 1350 && fixture.Result.Statistics.Mean == 1325,
            "Native Length rows, D, S and Stats share the presented numbers");
        foreach (var scope in new[] { PanelOutputScope.Filtered, PanelOutputScope.Selected })
        {
            fixture.Scope(scope);
            Check((string)Call(attributes, "OutputPortLabel", component.Params.Output[0])! == "D" &&
                (string)Call(attributes, "OutputPortLabel", component.Params.Output[1])! == "S" &&
                (string)Call(attributes, "OutputPortLabel", component.Params.Output[2])! == (scope == PanelOutputScope.Selected ? "B·S" : "B") &&
                (string)Call(attributes, "OutputPortLabel", component.Params.Output[3])! == (scope == PanelOutputScope.Selected ? "F·S" : "F"),
                "Values retain D/S labels and B/F retain selected-scope suffixes");
        }
        fixture.Scope(PanelOutputScope.Filtered);
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1600, 820) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1.5f; canvas.Viewport.ComputeProjection();
        var visible = canvas.Viewport.VisibleRegion;
        attributes.Pivot = new PointF(visible.Left + 45, visible.Top + 45);
        PanelTestAdapter.Resize(component, 620, 360); attributes.PerformLayout();
        string output = Environment.GetEnvironmentVariable("PANEL_WYSIWYG_IMAGES") ?? Path.GetFullPath("artifacts/wysiwyg-values");
        Directory.CreateDirectory(output);
        void Render(string? name = null, GH_TooltipDisplayEventArgs? tooltip = null)
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(238, 238, 238));
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
            if (tooltip != null)
            {
                GH_Tooltip.AssignTooltipFields(tooltip.Title, tooltip.Text, tooltip.Description, tooltip.Icon, null);
                GH_Tooltip.Layout();
                var form = GH_Tooltip.TooltipForm;
                using var tip = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(tip, new Rectangle(0, 0, tip.Width, tip.Height));
                graphics.ResetTransform(); graphics.DrawImageUnscaled(tip, 300, 230);
                GH_Tooltip.Clear();
            }
            if (name != null) { string file = Path.Combine(output, name + ".png"); bitmap.Save(file, ImageFormat.Png); Console.WriteLine("IMAGE " + file); }
        }
        Render();
        var tooltip = PanelTestAdapter.Tooltip(component, canvas, 0);
        Check(tooltip.Description.Contains("Shown 1300.00") && tooltip.Description.Contains("raw 1263.4718") && tooltip.Description.Contains("rounded up by 50"),
            "Rounded Length native tooltip exposes raw metric and rounding reason");
        Render("rounded-length-raw-tooltip", tooltip);
        Render("out-values-as-shown");
        var heading = Field<IEnumerable>(attributes, "_branchHits").Cast<object>().First();
        var bounds = Property<RectangleF>(heading, "Bounds");
        Call(attributes, "UpdateHover", canvas, new PointF(bounds.X + 20, bounds.Y + bounds.Height / 2));
        Render("branch-copy-hover");
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            var copy = Property<RectangleF>(heading, "CopyBounds");
            PanelTestAdapter.CanvasClick(canvas, new PointF(copy.X + copy.Width / 2, copy.Y + copy.Height / 2));
            await fixture.Ready(() => Field<string>(attributes, "_actionStatus") == "Copied {0;2;52}");
            Check(Forms.Clipboard.GetText() == "{0;2;52}" && fixture.SelectedCount == 1, "Heading glyph copies the exact path without changing selection");
            Render();
            PanelTestAdapter.CanvasClick(canvas, new PointF(bounds.X + 20, bounds.Y + bounds.Height / 2), Forms.MouseButtons.Right);
            Render("branch-heading-menu");
            Check(Field<Array>(attributes, "_rowMenuActions").Length == 5, "Heading menu includes selected paths and branch-only filtering");
            Call(attributes, "CloseRowMenu"); Render();
            var row = PanelTestAdapter.RowBounds(component, 0);
            var location = canvas.Viewport.ProjectPoint(new PointF(row.X + 80, row.Y + row.Height / 2));
            var click = new GH_CanvasMouseEvent(canvas.Viewport, new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1, (int)location.X, (int)location.Y, 0));
            attributes.RespondToMouseDown(canvas, click);
            Render("row-address-table-menu");
            Check(Field<Array>(attributes, "_rowMenuActions").Cast<object>().Any(action => Property<object>(action, "Action").ToString() == "CopyTable"),
                "Native row menu contains Copy as table");
            Call(attributes, "CloseRowMenu");
            string copied = await (Task<string>)Call(component, "CopyValuesAsync", fixture.Result.Snapshot, new[] { 0 })!;
            Check(copied == "Value copied" && Forms.Clipboard.GetText() == "1300.00", "Native clipboard matches the rounded Length wire value");
            fixture.SelectAt(1, toggle: true);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 2);
            var actionType = assembly.GetType("BetterGhPanel.UI.PanelRowAction", true)!;
            async Task Copy(string action) => await (Task<string>)Call(component, "CopyAddressesAsync", fixture.Result.Snapshot, new[] { 0, 1 }, Enum.Parse(actionType, action), null, false)!;
            await Copy("CopyAddresses");
            Check(Forms.Clipboard.GetText() == "{0;2;52}(0)" + Environment.NewLine + "{0;2;52}(1)", "Native addresses use the exact visible path and index");
            await Copy("CopyTable");
            Check(Forms.Clipboard.GetText() == "Path\tIndex\tValue" + Environment.NewLine +
                "{0;2;52}\t0\t1300.00 mm" + Environment.NewLine + "{0;2;52}\t1\t1350.00 mm", "Native TSV preserves shown values and unit labels");
            Render();
            var keyTarget = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!;
            keyTarget.GetMethod("HandleKey")!.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.C });
            await fixture.Ready(() => Field<string>(attributes, "_actionStatus") == "Addresses copied");
            Check(Forms.Clipboard.GetText() == "{0;2;52}", "Ctrl/Cmd+Shift+C copies unique selected paths");
        }
        finally { if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true); }

        Call(component, "SetOutputValues", PanelOutputValues.Items);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Line>().FirstOrDefault()?.Value.Length == 1263.4718);
        Check(fixture.Source.PersistentData.AllData(false).OfType<GH_Line>().First().Value.Length == 1263.4718, "Rounded geometry Items and upstream geometry stay unchanged");
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(4.4999999), path); fixture.Solve();
        Call(component, "SetUnits", PanelUnitConversion.None, 2, (double?)0);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().FirstOrDefault()?.Value == 4.5);
        Check(fixture.Source.PersistentData.AllData(false).OfType<GH_Number>().First().Value == 4.4999999, "Numeric Items round to explicit decimals while preserving upstream input");

        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(-0.0), path); fixture.Solve();
        Call(component, "SetUnits", PanelUnitConversion.None, PanelUnits.AutomaticDecimals, (double?)0);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().FirstOrDefault()?.Value == 0);
        Check(BitConverter.DoubleToInt64Bits(fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().First().Value) == 0,
            "Automatic numeric Items normalize negative zero on the wire");
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(1), path);
        fixture.Source.PersistentData.EnsurePath(new GH_Path(0, 3, 52));
        for (int b = 4; b < 10; b++) for (int i = 0; i < 3; i++)
            fixture.Source.PersistentData.Append(b == 4 && i == 0 ? null : new GH_Number(b == 4 && i == 1 ? double.NaN : 10 + i), new GH_Path(0, b, 52));
        options = new PanelQueryOptions { DecimalPlaces = 2 };
        fixture.Query(options); fixture.Solve();
        await fixture.Ready(() => fixture.Result.Warnings.OutlierBranches == 2);
        attributes.PerformLayout(); Render("data-warnings-amber-counts");
        Check(fixture.Result.Warnings.MajorityLength == 3 && fixture.Result.Warnings.NullCount == 1 && fixture.Result.Warnings.InvalidCount == 1,
            "Native warning strip includes null, invalid and empty-branch length outliers");
    }
}
