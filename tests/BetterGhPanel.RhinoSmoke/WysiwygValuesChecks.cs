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
        canvas.Viewport.Zoom = 1f; canvas.Viewport.ComputeProjection();
        var visible = canvas.Viewport.VisibleRegion;
        attributes.Pivot = new PointF(visible.Left + 45, visible.Top + 45);
        PanelTestAdapter.Resize(component, 620, 360); attributes.PerformLayout();
        string output = Environment.GetEnvironmentVariable("PANEL_WYSIWYG_IMAGES") ?? Path.GetFullPath("artifacts/wysiwyg-values");
        Directory.CreateDirectory(output);
        void Render(string? name = null, GH_TooltipDisplayEventArgs? tooltip = null)
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            // Bitmap captures need grayscale antialiasing: ClearType fringes tint the narrow unit glyphs.
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(GH_Skin.canvas_back);
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
        void Capture(string name, GH_TooltipDisplayEventArgs? tip = null)
        {
            Color previous = GH_Skin.canvas_back;
            try
            {
                foreach (bool dark in new[] { false, true })
                {
                    GH_Skin.canvas_back = dark ? Color.FromArgb(35, 38, 36) : Color.FromArgb(238, 238, 238);
                    Render(name + (dark ? "-dark" : "-light"), tip);
                }
            }
            finally { GH_Skin.canvas_back = previous; }
        }
        Render();
        var tooltip = PanelTestAdapter.Tooltip(component, canvas, 0);
        Check(tooltip.Description.Contains("Shown 1300.00") && tooltip.Description.Contains("raw 1263.4718") && tooltip.Description.Contains("rounded up by 50"),
            "Rounded Length native tooltip exposes raw metric and rounding reason");
        Check(tooltip.Description.Split(new[] { "Shown " }, StringSplitOptions.None).Length == 2 &&
            tooltip.Description.Split('\n').Contains("Shown 1300.00 mm · raw 1263.4718 mm · rounded up by 50"), "Raw tooltip adds exactly one rounding line");
        Check(!tooltip.Description.Contains("Geometry · length:") && !tooltip.Description.Contains("Ln · L") &&
            tooltip.Description.Contains("Length: 1300.00 mm") && tooltip.Description.Contains("Degree: 1"),
            "Raw tooltip keeps the main labeled geometry details without repeated compact summaries");
        Capture("rounded-length-raw-tooltip", tooltip);
        fixture.ClearSelection();
        PanelTestAdapter.Resize(component, 560, 360); attributes.PerformLayout(); Render();
        Check(!Field<bool>(attributes, "_singleLineFooter") && Field<RectangleF>(attributes, "_outValues").Right < Field<RectangleF>(attributes, "_selectAll").Left,
            "560px Values footer reserves separate status and Units rows");
        Capture("footer-560-values");
        fixture.SelectAt(0); Render();
        var heading = Field<IEnumerable>(attributes, "_branchHits").Cast<object>().First();
        var bounds = Property<RectangleF>(heading, "Bounds");
        Check(Property<RectangleF>(heading, "CopyBounds").IsEmpty, "An unhovered heading has no copy hit target");
        Call(attributes, "UpdateHover", canvas, new PointF(bounds.X + 20, bounds.Y + bounds.Height / 2));
        Capture("branch-copy-hover");
        heading = Field<IEnumerable>(attributes, "_branchHits").Cast<object>().First();
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            var copy = Property<RectangleF>(heading, "CopyBounds");
            Check(!copy.IsEmpty, "Hover paints the copy glyph and creates its hit target together");
            PanelTestAdapter.CanvasClick(canvas, new PointF(copy.X + copy.Width / 2, copy.Y + copy.Height / 2));
            await fixture.Ready(() => Field<string>(attributes, "_actionStatus") == "Copied {0;2;52}");
            Check(Forms.Clipboard.GetText() == "{0;2;52}" && fixture.SelectedCount == 1, "Heading glyph copies the exact path without changing selection");
            Call(attributes, "UpdateHover", canvas, new PointF(bounds.X - 20, bounds.Y)); Render();
            Check(Property<RectangleF>(Field<IEnumerable>(attributes, "_branchHits").Cast<object>().First(), "CopyBounds").IsEmpty,
                "Leaving a heading removes its copy hit target");
            Render();
            PanelTestAdapter.CanvasClick(canvas, new PointF(bounds.X + 20, bounds.Y + bounds.Height / 2), Forms.MouseButtons.Right);
            Capture("branch-heading-menu");
            Check(Field<Array>(attributes, "_rowMenuActions").Length == 5, "Heading menu includes selected paths and branch-only filtering");
            Call(attributes, "CloseRowMenu"); Render();
            var row = PanelTestAdapter.RowBounds(component, 0);
            PanelTestAdapter.CanvasClick(canvas, new PointF(row.X + 80, row.Y + row.Height / 2), Forms.MouseButtons.Right);
            Capture("row-address-table-menu");
            Check(Field<Array>(attributes, "_rowMenuActions").Cast<object>().Any(action => Property<object>(action, "Action").ToString() == "CopyTable"),
                "Native row menu contains Copy as table");
            Call(attributes, "CloseRowMenu");
            string copied = await (Task<string>)Call(component, "CopyValuesAsync", fixture.Result.Snapshot, new[] { 0 })!;
            Check(copied == "Value copied" && Forms.Clipboard.GetText() == "1300.00", "Native clipboard matches the rounded Length wire value");
            fixture.SelectAt(1, toggle: true);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 2);
            var actionType = assembly.GetType("BetterGhPanel.UI.PanelRowAction", true)!;
            async Task<string> Copy(string action) => await (Task<string>)Call(component, "CopyAddressesAsync", fixture.Result.Snapshot, new[] { 0, 1 }, Enum.Parse(actionType, action), null, false)!;
            string addressStatus = await Copy("CopyAddresses");
            string addressText = Forms.Clipboard.GetText();
            Check(addressStatus == "Addresses copied" && addressText == "{0;2;52}(0)" + Environment.NewLine + "{0;2;52}(1)",
                "Native addresses use the exact visible path and index; status=" + addressStatus + "; clipboard=" + addressText.Replace(Environment.NewLine, " / "));
            await Copy("CopyTable");
            Check(Forms.Clipboard.GetText() == "Path\tIndex\tValue" + Environment.NewLine +
                "{0;2;52}\t0\t1300.00 mm" + Environment.NewLine + "{0;2;52}\t1\t1350.00 mm", "Native TSV preserves shown values and unit labels");
            Render();
            var keyTarget = assembly.GetType("BetterGhPanel.UI.IPanelInputTarget", true)!;
            keyTarget.GetMethod("HandleKey")!.Invoke(attributes, new object[] { canvas, Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.C });
            await fixture.Ready(() => Field<string>(attributes, "_actionStatus") == "Addresses copied");
            Check(Forms.Clipboard.GetText() == "{0;2;52}", "Ctrl/Cmd+Shift+C copies unique selected paths");

            Call(component, "SetOutputValues", PanelOutputValues.Items);
            Call(component, "SetTreeOperations", 0, false, false, false, true);
            await fixture.Ready(() => fixture.Result.DisplaySet != null && fixture.Sinks[0].VolatileData.PathCount == 2);
            await Copy("CopyAddresses");
            Check(Forms.Clipboard.GetText() == "{0;2;52;0}" + Environment.NewLine + "{0;2;52;1}",
                "Create-set Items copy the same branches D sends");
            Call(component, "SetTreeOperations", 0, true, false, false, true);
            await fixture.Ready(() => fixture.Result.DisplaySet != null && fixture.Sinks[0].VolatileData.Paths[0].ToString() == "{0}");
            await Copy("CopyAddresses");
            Check(Forms.Clipboard.GetText() == "{0}" + Environment.NewLine + "{1}", "Flattened set addresses match D branches");
            Call(component, "SetTreeOperations", 0, false, false, false, false);
            await fixture.Ready(() => fixture.Result.DisplaySet == null);
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
        fixture.Source.PersistentData.Append(new GH_Number(double.MaxValue), path); fixture.Solve();
        Call(component, "SetUnits", PanelUnitConversion.MetersToMillimeters, 2, (double?)0);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Sinks[0].VolatileData.AllData(false).Single() == null);
        Check(PanelValuePresentation.FormatDisplay(fixture.Result.Snapshot.Rows[0], PanelUnitConversion.MetersToMillimeters, 2) == "unavailable" &&
            fixture.Result.Statistics.NumericCount == 0 && fixture.Result.Statistics.InvalidCount == 0 && fixture.Result.Statistics.NullCount == 0 &&
            fixture.Result.Warnings.InvalidCount == 0 && fixture.Result.Warnings.NullCount == 0,
            "Final conversion overflow is unavailable in rows/Items/Stats and never an invalid or null source warning");
        Call(component, "SetTreeOperations", 0, false, false, false, true);
        Call(component, "SetOutputValues", PanelOutputValues.Values);
        await fixture.Ready(() => fixture.Result.DisplaySet != null && fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Sinks[0].VolatileData.AllData(false).Single() == null);
        Check(fixture.Result.DisplaySet!.Values[0].Unavailable && fixture.Result.Statistics.ScalarCount == 0,
            "Final conversion overflow remains a null slot through Create set Values");
        Call(component, "SetOutputValues", PanelOutputValues.Items);
        Call(component, "SetTreeOperations", 0, false, false, false, false);
        fixture.Query(new PanelQueryOptions { DecimalPlaces = 2 });
        Call(component, "SetUnits", PanelUnitConversion.None, 2, (double?)0);
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Integer(42), path); fixture.Solve();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).FirstOrDefault() is GH_Integer);
        Check(fixture.Result.Snapshot.Rows[0].TypeName == "Integer", "Explicit decimals retain Integer goo and its type filter identity");
        options = Property<PanelQueryOptions>(fixture.Session, "Options"); options.TypeName = "Integer"; fixture.Query(options);
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 1);
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(-0.0), path); fixture.Solve();
        fixture.Query(new PanelQueryOptions());
        Call(component, "SetUnits", PanelUnitConversion.None, PanelUnits.AutomaticDecimals, (double?)0);
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().FirstOrDefault()?.Value == 0);
        Check(BitConverter.DoubleToInt64Bits(fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().First().Value) == long.MinValue,
            "Automatic numeric Items preserve negative zero without active transformations");
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(1), path);
        fixture.Source.PersistentData.EnsurePath(new GH_Path(0, 3, 52));
        for (int b = 4; b < 10; b++) for (int i = 0; i < 3; i++)
            fixture.Source.PersistentData.Append(b == 4 && i == 0 ? null : b == 4 && i == 2 ? new GH_ObjectWrapper(new object()) : new GH_Number(b == 4 && i == 1 ? double.NaN : 10 + i), new GH_Path(0, b, 52));
        options = new PanelQueryOptions { DecimalPlaces = 2 };
        fixture.Query(options); fixture.Solve();
        await fixture.Ready(() => fixture.Result.Warnings.OutlierBranches == 2);
        attributes.PerformLayout(); Render(); Capture("data-warnings-amber-counts");
        Check(fixture.Result.Warnings.MajorityLength == 3 && fixture.Result.Warnings.NullCount == 1 && fixture.Result.Warnings.InvalidCount == 1,
            "Native warning strip includes null, invalid and empty-branch length outliers");
        Call(component, "SetTreeOperations", 0, false, true, false, false);
        Call(component, "SetUnits", PanelUnitConversion.MillimetersToMeters, 2, (double?)0);
        await fixture.Ready(() => fixture.Result.Warnings.UnverifiedCount == 1);
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var restricted = Property<PanelQueryOptions>(fixture.Session, "Options");
            restricted.SearchMode = PanelSearchMode.Regex; restricted.Search = "no matches";
            restricted.TypeName = "Integer"; restricted.OnlyInvalid = true; restricted.BranchPath = "{99}";
            fixture.Query(restricted);
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 0);
            attributes.PerformLayout(); Render();
            var hits = Field<System.Collections.Generic.List<(RectangleF Bounds, int Kind)>>(attributes, "_warningHits");
            var warning = hits.Single(hit => hit.Kind == kind);
            fixture.Document.UndoServer.Clear();
            PanelTestAdapter.CanvasClick(canvas, new PointF(warning.Bounds.X + warning.Bounds.Width / 2, warning.Bounds.Y + 8));
            await fixture.Ready(() => kind == 3 ? fixture.Result.BranchPaths.Count == 2 : fixture.Result.RowPositions.Count == 1);
            var jumped = Property<PanelQueryOptions>(fixture.Session, "Options");
            Check(jumped.TypeName == null && !jumped.OnlyInvalid && jumped.BranchPath == null && jumped.Search != "no matches" &&
                jumped.DecimalPlaces == 2 && Property<PanelUnitConversion>(component, "UnitConversion") == PanelUnitConversion.MillimetersToMeters &&
                Property<bool>(component, "ReverseTree") && fixture.Document.UndoServer.UndoCount == 1,
                "Warning jump clears restrictions, keeps units/tree tools, and records one Undo step: " + kind);
            fixture.Document.UndoServer.PerformUndo();
            await fixture.Ready(() => fixture.Result.RowPositions.Count == 0);
            var restored = Property<PanelQueryOptions>(fixture.Session, "Options");
            Check(restored.Search == "no matches" && restored.TypeName == "Integer" && restored.OnlyInvalid && restored.BranchPath == "{99}",
                "One Undo restores all prior warning-click restrictions: " + kind);
        }
        fixture.Query(new PanelQueryOptions { DecimalPlaces = 2 });
        await fixture.Ready(() => fixture.Result.RowPositions.Count > 0);
        PanelTestAdapter.Resize(component, 300, 220);
        Call(component, "SetCanvasDisplay", Enum.Parse(assembly.GetType("BetterGhPanel.UI.CanvasPanelDisplay", true)!, "Half"));
        attributes.PerformLayout(); Render();
        Check(Field<float>(attributes, "_warningsHeight") == 0 && Field<RectangleF>(attributes, "_list").Bottom <= Field<RectangleF>(attributes, "_footer").Top &&
            Field<System.Collections.Generic.List<(RectangleF Bounds, int Kind)>>(attributes, "_warningHits").Count == 0,
            "Compact Half hides warnings and keeps rows above footer with no stale warning hit targets");
        Capture("compact-half");
    }
}
