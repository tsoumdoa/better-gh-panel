using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Data;
using Forms = System.Windows.Forms;

internal static class PairingWysiwygChecks
{
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static async Task Verify(Assembly assembly)
    {
        using var f = new PanelHostFixture.Fixture(assembly);
        var component = f.Component; var attributes = component.Attributes;
        f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear();
        for (int b = 0; b < 4; b++)
        {
            int count = b == 3 ? 9 : 3;
            for (int i = 0; i < count; i++) f.Source.PersistentData.Append(new GH_Number(10.1234 + b + i), new GH_Path(0, b));
            for (int i = 0; i < (b == 0 ? 4 : b == 3 ? 12 : 3); i++)
                f.Paired.PersistentData.Append(new GH_Number(100.1234 + b * 10 + i), new GH_Path(1, b));
        }
        PanelTestAdapter.TreeTools(component, 0, false, true, false);
        Call(component, "SetUnits", PanelUnitConversion.MillimetersToMeters, 2, (double?)0);
        Call(component, "SetPanelSplit", true); f.Solve();
        await f.Ready(() => f.Sinks[3].VolatileData.DataCount == 22 && PropertyValue<bool>(component, "PairedSummaryCurrent"));
        Check(f.Result.Warnings.OutlierBranches == 1 && f.Result.Warnings.MajorityLength == 3,
            "Uneven order-paired split also publishes D's conservative source warnings");
        using var canvas = new GH_Canvas { Document = f.Document, Size = new Size(1600, 850) };
        _ = canvas.Handle; canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty; canvas.Viewport.Zoom = 1; canvas.Viewport.ComputeProjection();
        var visible = canvas.Viewport.VisibleRegion;
        attributes.Pivot = new PointF(visible.Left + 40, visible.Top + 30);
        PanelTestAdapter.Resize(component, 620, 650);
        string output = Path.GetFullPath("artifacts/integration"); Directory.CreateDirectory(output);
        void Render(string? name = null)
        {
            attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(GH_Skin.canvas_back); canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
            if (name != null) { string file = Path.Combine(output, name + ".png"); bitmap.Save(file, ImageFormat.Png); Console.WriteLine("SCREENSHOT: " + file); }
        }
        void Capture(string name)
        {
            var previous = GH_Skin.canvas_back;
            try
            {
                foreach (bool dark in new[] { false, true })
                {
                    GH_Skin.canvas_back = dark ? Color.FromArgb(35, 38, 36) : Color.FromArgb(229, 229, 229);
                    Render(name + (dark ? "-dark" : "-light"));
                }
            }
            finally { GH_Skin.canvas_back = previous; }
        }
        object Heading(bool paired) => FieldValue<IEnumerable>(attributes, "_branchHits").Cast<object>().First(hit =>
            PropertyValue<string>(hit, "Path") == "{0;0}" && PanelTestAdapter.PaneField<RectangleF>(attributes, paired, "List").Contains(PropertyValue<RectangleF>(hit, "Bounds")));
        PointF Center(RectangleF bounds) => new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        Render();
        foreach (string mode in new[] { "Full", "Half" })
        {
            Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, mode));
            Render();
            var caption = PanelTestAdapter.PaneField<RectangleF>(attributes, false, "Caption");
            var list = PanelTestAdapter.PaneField<RectangleF>(attributes, false, "List");
            var panel = PanelTestAdapter.LayoutRect(attributes, "Panel");
            Check(FieldValue<float>(attributes, "_warningsHeight") == 17 && caption.Top >= panel.Top + 64 &&
                list.Top >= caption.Bottom && list.Bottom <= PanelTestAdapter.LayoutRect(attributes, "Footer").Top &&
                FieldValue<IEnumerable>(attributes, "_warningHits").Cast<object>().Any(),
                mode + " reserves warning strip, split captions, rows and footer without overlap");
            Capture("uneven-warnings-" + mode.ToLowerInvariant());
        }
        Call(component, "SetCanvasDisplay", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.CanvasPanelDisplay", true)!, "Full"));
        Render();
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            foreach (bool paired in new[] { false, true })
            {
                var heading = Heading(paired); var bounds = PropertyValue<RectangleF>(heading, "Bounds");
                Call(attributes, "UpdateHover", canvas, Center(bounds)); Render();
                var copy = PropertyValue<RectangleF>(Heading(paired), "CopyBounds");
                Check(!copy.IsEmpty && bounds.Contains(copy), "Complete " + (paired ? "F" : "D") + " heading paints a hover copy glyph");
                if (paired) Capture("f-heading-copy-hover");
                PanelTestAdapter.CanvasClick(canvas, Center(copy));
                await f.Ready(() => FieldValue<string>(attributes, "_actionStatus").StartsWith("Copied ", StringComparison.Ordinal));
                Check(Forms.Clipboard.GetText() == (paired ? "{1;0}" : "{0;0}"), "Heading glyph copies the clicked pane's own path");
                Render();
                PanelTestAdapter.CanvasClick(canvas, Center(bounds), Forms.MouseButtons.Right); Render();
                Check(FieldValue<bool>(attributes, "_rowMenuPaired") == paired && FieldValue<Array>(attributes, "_rowMenuActions").Cast<object>()
                    .Any(action => PropertyValue<string>(action, "Label") == "Copy path  " + (paired ? "{1;0}" : "{0;0}")),
                    "Heading menu labels use the clicked pane's own path");
                Call(attributes, "ChooseRowAction", Enum.Parse(typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelRowAction", true)!, "CopyPathBare"), f.Result, canvas);
                await f.Ready(() => Forms.Clipboard.GetText() == (paired ? "1;0" : "0;0"));
                Call(attributes, "CloseRowMenu"); Render();
            }
            f.Select(0); await f.Ready(() => f.Sinks[1].VolatileData.DataCount == 1); Render();
            var actionType = typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelRowAction", true)!;
            async Task Copy(string action, bool paired, string? path = null) =>
                await (Task<string>)Call(component, "CopyAddressesAsync", f.Result.Snapshot, new[] { 0 }, Enum.Parse(actionType, action), path!, paired)!;
            await Copy("CopyAddresses", true);
            Check(Forms.Clipboard.GetText() == "{1;0}(2)", "Reversed F address resolves own path and original index through the pairing map");
            await Copy("CopyTable", true);
            Check(Forms.Clipboard.GetText() == "Path\tIndex\tValue" + Environment.NewLine + "{1;0}\t2\t102.1234",
                "F TSV retains unconverted raw F values despite explicit D units/decimals");
            await Copy("CopyPaths", true);
            Check(Forms.Clipboard.GetText() == "{1;0}", "F selected paths use F tree paths");
            await Copy("CopyAddresses", false);
            Check(Forms.Clipboard.GetText() == "{0;0}(0)", "D address keeps its transformed D branch index");
            var pane = PanelTestAdapter.PaneField<RectangleF>(attributes, true, "List");
            var rowHit = FieldValue<IEnumerable>(attributes, "_rowHits").Cast<object>().First(hit =>
                PropertyValue<int>(hit, "SourcePosition") == 0 && pane.Contains(PropertyValue<RectangleF>(hit, "Bounds")));
            PanelTestAdapter.CanvasClick(canvas, Center(PropertyValue<RectangleF>(rowHit, "Bounds")), Forms.MouseButtons.Right); Render();
            Check(FieldValue<bool>(attributes, "_rowMenuPaired") && FieldValue<Array>(attributes, "_rowMenuActions").Cast<object>()
                .Any(action => PropertyValue<object>(action, "Action").ToString() == "CopyAddresses"), "Selectable F rows expose address actions");
            Call(attributes, "CloseRowMenu"); Render();
            // The shared display index gives extras and hidden partners no row-action hit regions.
            void CheckExtras(string message)
            {
                Render();
                var entries = ((IEnumerable)Call(attributes, "PairView", f.Result)!).Cast<object>().ToArray();
                bool found = false, selectable = false;
                foreach (var entry in entries.Where(entry => FieldValue<int>(entry, "Extra") >= 0))
                {
                    found = true;
                    int item = FieldValue<int>(entry, "Extra");
                    foreach (var hit in FieldValue<IEnumerable>(attributes, "_rowHits").Cast<object>().Where(hit => pane.Contains(PropertyValue<RectangleF>(hit, "Bounds"))))
                        selectable |= Call(component, "PairedRow", PropertyValue<int>(hit, "SourcePosition")) is PanelRow row &&
                            row.Path == PropertyValue<PanelSnapshot>(component, "PairedSnapshot").Rows[item].Path &&
                            row.OriginalIndex == PropertyValue<PanelSnapshot>(component, "PairedSnapshot").Rows[item].OriginalIndex;
                }
                Check(found && !selectable, message);
            }
            CheckExtras("F-only values have no selectable row/menu hits");
            // Use an index filter to retain one D member of each unequal pair.
            f.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ItemIndex, Search = "= 1" });
            await f.Ready(() => f.Result.RowPositions.Count == 4 && PropertyValue<bool>(component, "PairedSummaryCurrent"));
            CheckExtras("Hidden-partner F values also have no selectable row/menu hits");
            f.Query(new PanelQueryOptions()); await f.Ready(() => f.Result.RowPositions.Count == 18 && PropertyValue<bool>(component, "PairedSummaryCurrent"));
            f.Paired.PersistentData.Clear();
            for (int b = 0; b < 4; b++)
                for (int i = 0; i < (b == 0 ? 4 : b == 3 ? 12 : 3); i++)
                    f.Paired.PersistentData.Append(new GH_Number(100.1234 + b * 10 + i), b == 0
                        ? new GH_Path(1, 0, 1234567890, 1234567890, 1234567890) : new GH_Path(1, b));
            f.Solve(); await f.Ready(() => PropertyValue<bool>(component, "PairedSummaryCurrent"));
            PanelTestAdapter.Resize(component, 300, 650); Render();
            var clipped = PropertyValue<RectangleF>(Heading(true), "Bounds");
            Call(attributes, "UpdateHover", canvas, Center(clipped)); Render();
            Check(PropertyValue<RectangleF>(Heading(true), "CopyBounds").IsEmpty, "Clipped order-paired F labels have no copy glyph or copy hit area");
        }
        finally { if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true); }
    }

}
