using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

// Part of table-columns. Uses the production capture, resolver, admission and queues.
// Compile on disconnected sessions; execute only inside the native smoke host later.
internal static class TableGeometryChecks
{
    internal static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        using var longCurve = new LineCurve(Point3d.Origin, new Point3d(4, 0, 0));
        using var shortCurve = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
        using var surface = new PlaneSurface(Plane.WorldXY, new Interval(0, 3), new Interval(0, 3));
        using var brep = new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)).ToBrep();
        using var nurbs = longCurve.ToNurbsCurve();
        using var mesh = new Mesh();
        mesh.Vertices.Add(0, 0, 0); mesh.Vertices.Add(1, 0, 0); mesh.Vertices.Add(0, 1, 0); mesh.Faces.AddFace(0, 1, 2);
        var metadata = new GH_Structure<IGH_Goo>();
        metadata.AppendRange(new IGH_Goo[] { new GH_Curve(longCurve), new GH_Curve(nurbs), new GH_Surface(surface), new GH_Brep(brep), new GH_Mesh(mesh) }, new GH_Path(0));
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture", AnyStatic)!;
        var captured = (PanelSnapshot)capture.Invoke(null, new object?[] { metadata, null, PanelUnitConversion.None, "mm" })!;
        Check(captured.Rows.All(row => row.GeometryInfo?.Units == "mm"), "Deferred geometry metadata carries captured model units");
        var tableText = typeof(PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelTableText", true)!;
        var curveSnapshot = new PanelSnapshot(captured.Rows.Take(2).ToArray(), new[] { new PanelBranch("{0}", 2) });
        var common = tableText.GetMethod("CommonGeometryMetric", AnyStatic)!.Invoke(null, new object[] { curveSnapshot });
        Check(common != null && (string)tableText.GetMethod("GeometryHeaderSuffix", AnyStatic)!.Invoke(null,
            new object[] { common, PanelUnitConversion.MillimetersToMeters })! == " \u00b7 L \u00b7 m",
            "Analytic and deferred curves share a homogeneous metric/unit header");
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0);
        IGH_Goo[] shapes = { new GH_Curve(longCurve), new GH_Curve(shortCurve), new GH_Surface(surface), new GH_Brep(brep), new GH_String("no metric") };
        fixture.Source.PersistentData.AppendRange(shapes, path);
        fixture.Paired.PersistentData.AppendRange(shapes, path);
        Call(component, "AddColumn");
        var input = new Param_GenericObject(); input.CreateAttributes(); fixture.Document.AddObject(input, false);
        input.PersistentData.AppendRange(shapes, path); component.Params.Input[2].AddSource(input);
        fixture.Solve(); await fixture.Ready(() => fixture.Result.RowPositions.Count == shapes.Length);
        Check(((IList)PropertyValue<object>(component, "TableColumns")).Cast<object>().All(column => PropertyValue<object?>(column, "Snapshot") == null),
            "Hidden unsorted table has no column presentation or measurement store");
        Call(component, "SetPanelSplit", true);
        await fixture.Ready(() => fixture.Result.RowPositions.Count == shapes.Length);
        var columns = ((IList)PropertyValue<object>(component, "TableColumns")).Cast<object>().ToArray();
        var column = columns[1]; var snapshot = PropertyValue<PanelSnapshot>(column, "Snapshot");
        var queue = FieldValue<object>(column, "Geometry");
        Check(PropertyValue<int>(queue, "EntryCountForTesting") == 0, "Showing Table alone does not measure offscreen columns");
        var data = fixture.Result.Snapshot;
        Check(await (Task<bool>)Call(fixture.Session, "MeasureGeometryAsync", data, Enumerable.Range(0, shapes.Length).ToArray(), CancellationToken.None)!,
            "D uses its ordinary production queue");
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1600, 900) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Zoom = 1; canvas.Viewport.Target = System.Drawing.Point.Empty; canvas.Viewport.ComputeProjection();
        component.Attributes.Pivot = new PointF(canvas.Viewport.VisibleRegion.Left + 40, canvas.Viewport.VisibleRegion.Top + 30);
        PanelTestAdapter.Resize(component, 1000, 450);
        void Render()
        {
            component.Attributes.PerformLayout();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height); using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(GH_Skin.canvas_back); canvas.Viewport.ApplyProjection(graphics); PanelTestAdapter.Render(component, canvas, graphics);
        }
        Check(snapshot.Rows[2].GeometryInfo?.CanMeasure == true && snapshot.Rows[2].GeometryInfo?.Value == null,
            "Copy-wait target is a deferred surface, not an analytic line");
        var gate = fixture.Session.GetType().GetProperty("GeometryWorkerSubmissionForTesting", AnyInstance)!;
        Action? heldWorker = null;
        gate.SetValue(fixture.Session, (Func<Action, bool>)(worker =>
        {
            Check(heldWorker == null, "Session admission permits only one gated worker");
            heldWorker = worker;
            return true;
        }));
        fixture.Select(2); Render();
        var previousClipboard = System.Windows.Forms.Clipboard.GetDataObject();
        try
        {
            var copy = (Task<string>)Call(component, "CopyColumnValuesAsync", data, new[] { 2 }, 1, null)!;
            await fixture.Ready(() => heldWorker != null);
            Check(!copy.IsCompleted && !(bool)Call(queue, "Has", snapshot, 2)!,
                "Column copy remains pending while production worker completion is deterministically gated");
            gate.SetValue(fixture.Session, null);
            var worker = heldWorker!; heldWorker = null;
            _ = Task.Run(worker);
            Check((await copy).Contains("copied") && !System.Windows.Forms.Clipboard.GetText().Contains("measuring"),
                "Column copy publishes measured text and never the pending placeholder");
        }
        finally
        {
            gate.SetValue(fixture.Session, null);
            Call(component, "CancelPendingCopy");
            if (heldWorker != null) _ = Task.Run(heldWorker);
            if (previousClipboard != null) System.Windows.Forms.Clipboard.SetDataObject(previousClipboard, true);
        }
        Check(await (Task<bool>)Call(queue, "MeasureAsync", snapshot, Enumerable.Range(0, shapes.Length).ToArray(), CancellationToken.None)!,
            "Column geometry completes through the same queue type as D");
        var conversion = PropertyValue<PanelUnitConversion>(component, "UnitConversion");
        int decimals = PropertyValue<int>(component, "DecimalPlaces"); double factor = PropertyValue<double>(component, "RoundUpFactor");
        for (int index = 0; index < 4; index++)
        {
            var metric = (PanelGeometryInfo?)Call(fixture.Session, "GetGeometryDetails", data, index) ?? data.Rows[index].GeometryInfo!;
            string label = index < 2 ? "Crv" : index == 2 ? "Srf" : "Brep";
            string expected = label + " " + metric.MetricLabel + " " + PanelUnits.FormatGeometry(metric.Value!.Value,
                metric.Power, metric.Units, conversion, decimals, roundUpFactor: factor);
            Check((string)Call(column, "ItemValue", index, conversion, decimals, factor)! == expected,
                "Mixed column shows its compact primary metric for " + shapes[index].TypeName);
            string tooltip = (string)Call(column, "ItemTooltip", index, conversion, decimals, factor)!;
            Check(tooltip.Contains(metric.Description) && tooltip.Contains(metric.FormatDetails(conversion, decimals, factor)),
                "Column tooltip retains D geometry hints and measurement details");
        }
        string Fingerprint(int output)
        {
            var chunk = new GH_LooseChunk("Output"); ((GH_Structure<IGH_Goo>)fixture.Sinks[output].VolatileData).Write(chunk);
            return Convert.ToBase64String(chunk.Serialize_Binary());
        }
        string[] outputs = Enumerable.Range(0, 4).Select(Fingerprint).ToArray();
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueAscending);
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 1, 0, 3, 2, 4 }));
        await fixture.Ready(() => Enumerable.Range(0, 4).Select(Fingerprint).SequenceEqual(outputs));
        Check(Enumerable.Range(0, 4).Select(Fingerprint).SequenceEqual(outputs), "Geometry sort preserves D/S/B/F serialized bytes");
        Call(component, "SetPanelSplit", false);
        Call(component, "SetColumnSort", component.Params.Input[2].InstanceGuid, PanelSortMode.ValueDescending);
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 2, 3, 0, 1, 4 }));
        Check(PropertyValue<PanelSnapshot?>(columns[0], "Snapshot") == null && PropertyValue<PanelSnapshot?>(column, "Snapshot") != null,
            "Hidden Table retains only its active sort column");
        Call(component, "SetColumnSort", component.Params.Input[1].InstanceGuid, PanelSortMode.ValueAscending);
        await fixture.Ready(() => fixture.Result.RowPositions[0] == 1);
        Check(PropertyValue<PanelSnapshot?>(column, "Snapshot") == null && FieldValue<object?>(column, "Geometry") == null &&
            PropertyValue<PanelSnapshot?>(columns[0], "Snapshot") != null, "Switching hidden sort from C1 to F retires C1's store and queue");
        Call(component, "SetColumnSort", Guid.Empty, PanelSortMode.SourceOrder);
        await fixture.Ready(() => PropertyValue<PanelQueryOptions>(fixture.Session, "Options").SortMode == PanelSortMode.SourceOrder);
        Check(columns.All(value => PropertyValue<PanelSnapshot?>(value, "Snapshot") == null && FieldValue<object?>(value, "Geometry") == null),
            "Turning hidden sort off retires every column store and queue");
        // Queue work, then replace the input before its scheduled preparation can run.
        Call(component, "SetColumnSort", Guid.Empty, PanelSortMode.SourceOrder);
        Call(component, "SetPanelSplit", true);
        fixture.Solve(); await fixture.Ready(() => fixture.Result.RowPositions.Count == shapes.Length);
        column = ((IList)PropertyValue<object>(component, "TableColumns"))[1]!;
        snapshot = PropertyValue<PanelSnapshot>(column, "Snapshot"); queue = FieldValue<object>(column, "Geometry");
        var pending = (Task<bool>)Call(queue, "MeasureAsync", snapshot, new[] { 0, 1, 2, 3 }, CancellationToken.None)!;
        input.PersistentData.Clear(); input.PersistentData.AppendRange(Enumerable.Range(0, shapes.Length).Select(i => (IGH_Goo)new GH_Number(i)), path);
        input.ExpireSolution(false); fixture.Solve();
        Check(!await pending, "Replacement cancels the old pending request");
        await fixture.Ready(() => fixture.Result.RowPositions.Count == shapes.Length);
        Check(!(bool)Call(queue, "IsAttachedTo", snapshot)!, "Replaced column snapshot cannot publish old measurements");
    }
}
