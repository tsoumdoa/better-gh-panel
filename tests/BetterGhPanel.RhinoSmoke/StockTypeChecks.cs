using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;
using Rhino.Geometry;

internal static class StockTypeChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static object?[] Capture(Assembly assembly, params IGH_Goo[] values)
    {
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(values, new GH_Path(0));
        object?[] args = { tree, null, null, null, PanelUnitConversion.None, "mm", null, null, null, null, null };
        var snapshot = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("CaptureForMeasurements", Static)!.Invoke(null, args)!;
        return new[] { snapshot, args[1], args[2], args[3] };
    }
    private static void Number(PanelRow row, PanelSearchMode mode, double expected) => Check(
        PanelProperties.TryGet(row, mode, null, out var value) && value != null && value.Number == expected, mode + " is captured");

    private static async Task Verify(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Stock types need a Rhino document.");
        using var dot = new TextDot("Lobby α", new Point3d(2, 3, 4)) { FontHeight = 18, FontFace = "Arial", SecondaryText = "Entrance" };
        using var text = new TextEntity { PlainText = "Level 01", Plane = Plane.WorldXY, TextHeight = 2 };
        using var dimension = new LinearDimension(Plane.WorldXY, new Point2d(0, 0), new Point2d(4, 0), new Point2d(2, 1));
        Check(dot.SetUserString("panel.source", "dot"), "dot carries source metadata");
        var holder = new GH_TextDot { Value = dot };
        var textHolder = new GH_TextEntity(text);
        var dimensionHolder = new GH_LinearDimension(dimension);
        using var matrix = new Matrix(2, 2); matrix[0, 0] = 1; matrix[1, 1] = 2;
        var matrixHolder = new GH_Matrix(matrix);
        var pathHolder = new GH_StructurePath(new GH_Path(2, 7));
        var location = new GH_LonLatCoordinate(114.17, 22.3);
        var captured = Capture(assembly, holder, textHolder, dimensionHolder, new GH_Culture(new CultureInfo("en-GB")), matrixHolder, pathHolder, location);
        var snapshot = (PanelSnapshot)captured[0]!;
        Check(snapshot.Rows.All(row => row.Validity == PanelValidity.VerifiedValid), "new stock samples have trusted validity");
        Check(snapshot.Rows[0].TypeName == "Text Dot" && snapshot.Rows[0].DisplayValue == "Lobby α", "dot displays only its text");
        Check(PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.TextValue, -1, includeUnits: false) == "Lobby α", "dot text is selectable");
        Check(snapshot.Rows[0].Components?.X == 2 && snapshot.Rows[0].Components?.Z == 4 && snapshot.SupportsSearchMode(PanelSearchMode.X), "dot exposes existing coordinate modes");
        Number(snapshot.Rows[0], PanelSearchMode.DotFontSize, 18);
        Check(PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.FontFace, -1) == "Arial", "dot font face is selectable");
        Check(snapshot.Rows[1].DisplayValue == "Level 01", "text entity displays its text");
        Check(snapshot.Rows[2].GeometryInfo?.Value == 4 && PanelValuePresentation.FormatDisplay(snapshot.Rows[2], PanelUnitConversion.None, 2) == "4.00 mm", "linear dimension displays an ordinary length metric");
        Number(snapshot.Rows[1], PanelSearchMode.TextHeight, 2); Number(snapshot.Rows[4], PanelSearchMode.RowCount, 2);
        Number(snapshot.Rows[5], PanelSearchMode.PathDepth, 2); Number(snapshot.Rows[6], PanelSearchMode.Latitude, 22.3);
        Check(snapshot.Rows[3].DisplayValue.StartsWith("en-GB", StringComparison.Ordinal) && snapshot.Rows[5].DisplayValue == "{2;7}", "culture and path have value summaries");
        matrixHolder.Value[0, 1] = 5;
        Check(!(bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("GraphSourcesCurrent", Static)!.Invoke(null, new[] { captured[3] })!, "matrix edits invalidate the managed witness");
        holder.Value = new TextDot("replacement", Point3d.Origin);
        Check(!(bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("MeasurementSourcesCurrent", Static)!.Invoke(null, new[] { captured[1], captured[2] })!, "dot replacement invalidates captured geometry");
        holder.Value.Dispose(); holder.Value = dot;
        using var innerSubclass = new NativeDotSubclass();
        var rejected = (PanelSnapshot)Capture(assembly, new HostileDot(), new GH_TextDot(), new GH_TextDot(Guid.NewGuid()), new GH_TextDot { Value = innerSubclass })[0]!;
        Check(rejected.Rows[0].Validity == PanelValidity.Unverified && rejected.Rows[1].Validity == PanelValidity.VerifiedInvalid && rejected.Rows[2].Validity == PanelValidity.Unverified && rejected.Rows[3].Validity == PanelValidity.Unverified, "holder/inner subclasses, null and unresolved dots retain safe validity classifications");

        bool modified = rhino.Modified;
        // Rhino's object/reference setup may move geometry user strings into
        // object attributes. Keep that fixture separate from the copy source.
        using var referenceDot = new TextDot("Reference dot", Point3d.Origin);
        Guid referenceId = rhino.Objects.AddTextDot(referenceDot);
        try
        {
            var referenced = new GH_TextDot(referenceId);
            Check(referenced.LoadGeometry(rhino), "dot reference is explicitly loaded by the fixture");
            var referencedCapture = Capture(assembly, referenced);
            Check(((PanelSnapshot)referencedCapture[0]!).Rows[0].Validity == PanelValidity.VerifiedValid, "loaded dot reference has trusted native validity");
            referenced.ReferenceID = Guid.NewGuid();
            Check(!(bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("MeasurementSourcesCurrent", Static)!.Invoke(null, new[] { referencedCapture[1], referencedCapture[2] })!, "retargeting a dot reference invalidates its capture");
        }
        finally { rhino.Objects.Delete(referenceId, true); rhino.Modified = modified; }

        var cultureHolder = new GH_Culture(CultureInfo.GetCultureInfo("en-GB"));
        var cultureCapture = Capture(assembly, cultureHolder);
        cultureHolder.Value = CultureInfo.GetCultureInfo("en-US");
        Check(!(bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("GraphSourcesCurrent", Static)!.Invoke(null, new[] { cultureCapture[3] })!, "culture identity replacement invalidates its string witness");
        var nullPath = new GH_StructurePath(); typeof(GH_StructurePath).GetField("m_value", Instance)!.SetValue(nullPath, null);
        var pathProperties = (Dictionary<PanelSearchMode, PanelPropertyValue?>)assembly.GetType("BetterGhPanel.Runtime.PanelPropertyCapture", true)!.GetMethod("Scalar", Static)!.Invoke(null, new object[] { nullPath, "mm" })!;
        var emptyField = (GH_Field)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GH_Field));
        var nulls = (PanelSnapshot)Capture(assembly, emptyField, new GH_Material { Value = null })[0]!;
        Check(!pathProperties.ContainsKey(PanelSearchMode.PathDepth) && nulls.Rows[0].DisplayValue == "Null field" && nulls.Rows[1].DisplayValue == "Null material", "null path, field and material values are guarded");
        using var longDot = new TextDot(new string('x', 65537), Point3d.Origin);
        using var longText = new TextEntity { PlainText = new string('y', 65537) };
        var limited = (PanelSnapshot)Capture(assembly, new GH_TextDot { Value = longDot }, new GH_TextEntity(longText))[0]!;
        Check(limited.Rows.All(row => row.Validity == PanelValidity.Unverified && row.DisplayValue.Length < 128 && row.Properties.Count == 0 && row.GeometryInfo == null), "oversized native text cannot escape capture limits through summaries/properties");
        using var angular = new AngularDimension(new Arc(Plane.WorldXY, 5, Math.PI / 2), 0);
        var angularHolder = new GH_AngularDimension(angular);
        using var boundary = new Rectangle3d(Plane.WorldXY, 4, 3).ToNurbsCurve();
        using var hatch = Hatch.Create(boundary, 0, Math.PI / 2, 1, rhino.ModelAbsoluteTolerance).Single();
        var angles = (PanelSnapshot)Capture(assembly, angularHolder, new GH_Hatch { Value = hatch }, new GH_Arc(new Arc(Plane.WorldXY, 5, Math.PI / 2)), new GH_ComplexNumber(new Complex(0, 1)))[0]!;
        var modes = new[] { PanelSearchMode.DimensionAngle, PanelSearchMode.PatternRotation, PanelSearchMode.Angle, PanelSearchMode.Phase };
        for (int i = 0; i < modes.Length; i++)
            Check(PanelProperties.TryGet(angles.Rows[i], modes[i], null, out var angle) && angle?.Format(PanelUnitConversion.RadiansToDegrees, 2, true) == "90.00 °", modes[i] + " uses the established angle convention");
        var southern = (PanelSnapshot)Capture(assembly, new GH_LonLatCoordinate(-114.17, -22.3))[0]!;
        Check(southern.Rows[0].DisplayValue == "22.3° S, 114.17° W", "location labels negative coordinates S/W");
        var matrixSummary = (PanelSnapshot)Capture(assembly, new GH_Transform(Transform.Translation(0, 3, 4)), new GH_Transform(Transform.Identity))[0]!;
        Check(matrixSummary.Rows[0].DisplayValue.StartsWith("4 × 4 [", StringComparison.Ordinal) && matrixSummary.Rows[0].DisplayValue.Contains("3") && matrixSummary.Rows[0].DisplayValue.Contains("4") && matrixSummary.Rows[1].DisplayValue == "Identity", "transform summary includes all rows and recognizes identity");

        var highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
        object Call(string name, params object[] args) => highlight.GetType().GetMethod(name, Instance)!.Invoke(highlight, args)!;
        try
        {
            var group = new GH_GeometryGroup(); group.Objects.Add(holder); group.Objects.Add(new GH_Point(Point3d.Origin));
            Call("Set", new IGH_Goo[] { group }, new[] { 0 }, new[] { 0 });
            Check(Property<int>(highlight, "PreviewCount") == 1 && Property<string>(highlight, "Status") == "", "dot and point admit owned preview in a group");
            var bounds = Property<BoundingBox>(highlight, "ClippingBox");
            Check(bounds.Contains(dot.Point), "dot contributes viewport clipping bounds");
            var actions = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
            var frame = actions.GetMethod("Frame", Static)!.Invoke(null, new object[] { new IGH_Goo[] { holder } })!;
            Check(Property<bool>(frame, "Success") && Property<int>(frame, "Skipped") == 0, "dot frames in Rhino");
            var previousClipboard = System.Windows.Forms.Clipboard.GetDataObject();
            try
            {
                Check(dot.GetUserString("panel.source") == "dot", "preview/frame leave source metadata intact");
                byte[]? bytes = null;
                var copy = actions.GetMethod("CopyGeometryWithArchiveObserver", Static)!.Invoke(null, new object[] {
                    new IGH_Goo[] { holder }, rhino.ModelUnitSystem, rhino.ModelAbsoluteTolerance, (Action<byte[]>)(value => bytes = value) })!;
                Check(Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 1 && bytes != null, "dot copy publishes one Rhino geometry");
                using var file = File3dm.FromByteArray(bytes!);
                var copied = file.Objects.Select(item => item.Geometry).OfType<TextDot>().Single();
                Check(copied.Text == dot.Text && copied.Point == dot.Point && copied.FontHeight == dot.FontHeight && copied.FontFace == dot.FontFace && copied.SecondaryText == dot.SecondaryText && copied.GetUserString("panel.source") == "dot", "copy archive preserves dot values and font");
            }
            finally { if (previousClipboard != null) System.Windows.Forms.Clipboard.SetDataObject(previousClipboard, true); }
        }
        finally { Call("Clear"); Call("ReleaseOnIdle", null!, EventArgs.Empty); }

        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Document.Properties.KeepOpen = true; fixture.Document.PreviewMode = GH_PreviewMode.Shaded;
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(700, 650) };
        _ = canvas.Handle; canvas.Viewport.Size = canvas.Size; canvas.Viewport.Zoom = 1.5f;
        canvas.Viewport.Target = System.Drawing.Point.Empty; canvas.Viewport.ComputeProjection();
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.AppendRange(new IGH_Goo[] { holder, new GH_TextDot(new TextDot("Roof terrace — north wing, long annotation", new Point3d(8, 3, 4))), new GH_TextDot(new TextDot("Lobby α", new Point3d(12, 3, 4))) }, new GH_Path(0));
        fixture.Solve(); await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 3);
        Check(fixture.Sinks[0].VolatileData.AllData(false).All(item => item is GH_TextDot), "Items output retains dot goo");
        var dots = fixture.Result.Snapshot;
        var set = PanelQueryEngine.Execute(dots, new PanelQueryOptions { CreateSet = true });
        Check(set.RowPositions.Count == 2 && set.DisplaySet!.Members[0].Count == 2, "Create set groups equal dot text across different locations");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.TextValue, DisplayFilterValue = true });
        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Values });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).FirstOrDefault() is GH_String);
        Check(fixture.Sinks[0].VolatileData.AllData(false).Cast<GH_String>().Select(item => item.Value).SequenceEqual(new[] { "Lobby α", "Roof terrace — north wing, long annotation", "Lobby α" }), "Values output emits selected dot text properties");
        Check(fixture.Component.Params.Output[0].NickName == "D" && fixture.Component.Params.Output[1].NickName == "S", "output port names remain D and S");
        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Items });
        fixture.Query(new PanelQueryOptions());
        fixture.Source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Point(new Point3d(1, 2, 3)), textHolder, new GH_String("Text row") }, new GH_Path(1));
        fixture.Solve(); await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 6 && fixture.Sinks[0].VolatileData.DataCount == 6);
        fixture.Select(0); await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1);
        fixture.Component.GetType().GetMethod("HighlightRows", Instance)!.Invoke(fixture.Component, new object[] { fixture.Result.Snapshot, new[] { 0 } });
        var componentHighlight = fixture.Component.GetType().GetField("_highlight", Instance)!.GetValue(fixture.Component)!;
        Check(Property<int>(componentHighlight, "PreviewCount") == 1, "selected dot owns one component preview");
        SavePanel(fixture, canvas, "dots-mixed.png"); await SaveViewport(fixture, rhino);

        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.AppendRange(new IGH_Goo[] {
            new GH_TextDot(new TextDot("Lobby", new Point3d(1234.567, 0, 0))), new GH_TextDot(new TextDot("Roof", new Point3d(2345.678, 0, 0))) }, new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_TextDot(new TextDot("Below threshold", new Point3d(100, 0, 0))), new GH_Path(1));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, 2);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.X, Search = ">= 1.23", DecimalPlaces = 2 });
        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Values });
        fixture.Solve(); await fixture.Ready(() => fixture.Result.RowPositions.Count == 2 && fixture.Sinks[0].VolatileData.AllData(false).Count(item => item is GH_TextDot) == 2);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 0, 1 }), "X comparisons use converted dot components and retain typed Items");
        SavePanel(fixture, canvas, "dots-x-filter.png");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.X, DisplayFilterValue = true, BranchPath = "{0}", DecimalPlaces = 2 });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 1.23, 2.35 }));
        fixture.Select(0); await fixture.Ready(() => fixture.Sinks[1].VolatileData.AllData(false).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 1.23 }));
        Check(Math.Abs(fixture.Result.Statistics.Mean!.Value - 1.79) < 1e-9 && PanelFilterValue.Format(fixture.Result.Snapshot, fixture.Result.Snapshot.Rows[0], PanelSearchMode.X, 2) == "1.23", "dot X filter, Stats and D/S share converted explicit precision");
        SavePanel(fixture, canvas, "dots-x-values.png");
        SavePair("dots-x-filter.png", "dots-x-values.png", "dots-x-converted.png");

        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Items });
        fixture.Source.PersistentData.Clear(); fixture.Source.PersistentData.AppendRange(new IGH_Goo[] { dimensionHolder, angularHolder }, new GH_Path(0));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.RadiansToDegrees, 2); fixture.Query(new PanelQueryOptions { DecimalPlaces = 2 });
        fixture.Solve(); await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 2 && fixture.Result.Snapshot.Rows.Count == 2);
        Check(PanelValuePresentation.FormatDisplay(fixture.Result.Snapshot.Rows[0], PanelUnitConversion.RadiansToDegrees, 2) == "4.00 mm" &&
            PanelValuePresentation.FormatDisplay(fixture.Result.Snapshot.Rows[1], PanelUnitConversion.RadiansToDegrees, 2) == "90.00 °", "linear/angular dimension summaries respect units and precision");
        SavePanel(fixture, canvas, "dimensions.png");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Length, Search = "= 4.00", TypeName = "Linear Dimension", DecimalPlaces = 2 });
        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Values });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Sinks[0].VolatileData.AllData(false).SingleOrDefault() is GH_LinearDimension);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 0 }), "linear dimension comparison retains its typed item");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Length, TypeName = "Linear Dimension", DisplayFilterValue = true, DecimalPlaces = 2 });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 4.0 }));
        Check(fixture.Result.Statistics.Mean == 4, "linear dimension length comparison/Stats/Values share the metric");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.DimensionAngle, Search = "= 90.00", TypeName = "Angular Dimension", DecimalPlaces = 2 });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Sinks[0].VolatileData.AllData(false).SingleOrDefault() is GH_AngularDimension);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 1 }), "angular dimension comparison uses converted degrees and retains its type");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.DimensionAngle, TypeName = "Angular Dimension", DisplayFilterValue = true, DecimalPlaces = 2 });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 90.0 }));
        Check(fixture.Result.Statistics.Mean == 90, "angular dimension comparison/Stats/Values share converted degrees");
        fixture.Component.GetType().GetMethod("SetOutputValues", Instance)!.Invoke(fixture.Component, new object[] { PanelOutputValues.Items });
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.None, -1); fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.AppendRange(new IGH_Goo[] { textHolder, dimensionHolder, new GH_Culture(new CultureInfo("en-GB")), matrixHolder, pathHolder, location }, new GH_Path(1));
        using var comparisonCurve = new LineCurve(new Point3d(0, 0, 0), new Point3d(6, 0, 0));
        fixture.Source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Point(new Point3d(1, 2, 3)), new GH_Curve(comparisonCurve), new GH_Number(42) }, new GH_Path(2));
        fixture.Query(new PanelQueryOptions()); fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 9 && fixture.Sinks[0].VolatileData.DataCount == 9);
        Check(fixture.Sinks[0].VolatileData.AllData(false).Select(item => item.GetType()).SequenceEqual(new[] { typeof(GH_TextEntity), typeof(GH_LinearDimension), typeof(GH_Culture), typeof(GH_Matrix), typeof(GH_StructurePath), typeof(GH_LonLatCoordinate), typeof(GH_Point), typeof(GH_Curve), typeof(GH_Number) }), "mixed Items output preserves the newly supported types");
        SavePanel(fixture, canvas, "stock-types-mixed.png");
    }

    // Optional native captures. The fixture always validates behavior; a later
    // coordinated run sets this directory to capture the actual Rhino/GH renderers.
    private static string? ImagePath(string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PANEL_STOCK_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return null;
        Directory.CreateDirectory(directory); return Path.Combine(directory, name);
    }
    private static void SavePanel(PanelHostFixture.Fixture fixture, GH_Canvas canvas, string name)
    {
        string? path = ImagePath(name);
        fixture.Component.Attributes.PerformLayout();
        using var image = new Bitmap(canvas.Width, canvas.Height); using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(238, 238, 238)); canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(fixture.Component, canvas, graphics); if (path != null) image.Save(path);
    }
    private static void SavePair(string first, string second, string name)
    {
        string? path = ImagePath(name); if (path == null) return;
        using var left = new Bitmap(ImagePath(first)!); using var right = new Bitmap(ImagePath(second)!);
        using var image = new Bitmap(left.Width + right.Width, Math.Max(left.Height, right.Height));
        using var graphics = Graphics.FromImage(image);
        graphics.DrawImageUnscaled(left, 0, 0); graphics.DrawImageUnscaled(right, left.Width, 0); image.Save(path);
    }
    private static Task SaveViewport(PanelHostFixture.Fixture fixture, RhinoDoc rhino)
    {
        string? path = ImagePath("text-dot-highlight.png");
        var previous = rhino.Views.ActiveView;
        var previewMode = fixture.Document.PreviewMode;
        // The source/sinks also preview dots through GH's automatic conduit.
        // Disable that conduit so the pixels prove Panel+'s selected preview.
        fixture.Document.PreviewMode = GH_PreviewMode.Disabled;
        fixture.Component.DocumentContextChanged(fixture.Document, GH_DocumentContext.Loaded);
        var view = rhino.Views.Add("Text Dot highlight", DefinedViewportProjection.Top, new Rectangle(0, 0, 900, 650), false);
        try
        {
            rhino.Views.ActiveView = view; view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-2, -2, -2), new Point3d(12, 8, 8)));
            using var conduit = new DotConduit(fixture.Component, fixture.Document) { Enabled = true };
            using var image = DisplayPipeline.DrawToBitmap(view.ActiveViewport, 900, 650);
            if (path != null) image.Save(path);
            var projected = view.ActiveViewport.WorldToClient(new Point3d(2, 3, 4));
            int px = (int)(projected.X * image.Width / Math.Max(1, view.ActiveViewport.Size.Width));
            int py = (int)(projected.Y * image.Height / Math.Max(1, view.ActiveViewport.Size.Height));
            int selectedPixels = 0;
            for (int y = Math.Max(0, py - 40); y < Math.Min(image.Height, py + 40); y++)
                for (int x = Math.Max(0, px - 80); x < Math.Min(image.Width, px + 80); x++)
            { var color = image.GetPixel(x, y); if (color.G > color.R + 50 && color.G > color.B + 50) selectedPixels++; }
            Console.WriteLine($"Selected dot viewport: pixels={selectedPixels}, projected={px},{py}, draws={conduit.Draws}");
            Check(selectedPixels > 20, "selected dot draws its highlight color in native viewport pixels");
            Check(conduit.Draws > 0, "native viewport invokes Panel+ selected-dot preview");
        }
        finally { view.Close(); fixture.Document.PreviewMode = previewMode; if (previous != null) rhino.Views.ActiveView = previous; }
        return Task.CompletedTask;
    }
    private sealed class DotConduit : DisplayConduit, IDisposable
    {
        private readonly GH_Component _component; private readonly GH_Document _document;
        private readonly DisplayMaterial _material = new DisplayMaterial(Color.Lime);
        internal int Draws;
        internal DotConduit(GH_Component component, GH_Document document) { _component = component; _document = document; }
        protected override void DrawForeground(DrawEventArgs e)
        { Draws++; _component.DrawViewportWires(new Arguments(_document, e, _material)); }
        public void Dispose() { Enabled = false; _material.Dispose(); }
        private sealed class Arguments : IGH_PreviewArgs
        {
            private readonly DrawEventArgs _args;
            internal Arguments(GH_Document document, DrawEventArgs args, DisplayMaterial material) { Document = document; _args = args; ShadeMaterial = material; }
            public int DefaultCurveThickness => 2;
            public Color WireColour => Color.Gray; public Color WireColour_Selected => Color.Lime;
            public DisplayMaterial ShadeMaterial { get; } public DisplayMaterial ShadeMaterial_Selected => ShadeMaterial;
            public RhinoViewport Viewport => _args.Viewport; public DisplayPipeline Display => _args.Display;
            public GH_Document Document { get; } public MeshingParameters MeshingParameters => Document.PreviewCurrentMeshParameters();
        }
    }
    private sealed class NativeDotSubclass : TextDot
    { internal NativeDotSubclass() : base("untrusted inner type", Point3d.Origin) { } }
    private sealed class HostileDot : GH_TextDot
    {
        public override bool IsValid => throw new InvalidOperationException("Untrusted validity must not run");
        public override string TypeName => throw new InvalidOperationException("Untrusted metadata must not run");
    }
}
