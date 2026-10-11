using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

internal static class RecomputeChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        await VerifyPresentation(assembly);
        // GH dispatches scheduled solutions through its editor's native handle.
        // Create that handle without showing a window in this isolated host.
        var editor = new Grasshopper.GUI.GH_DocumentEditor();
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        try { await VerifyOutput(assembly); }
        finally { setEditor.Invoke(null, new object?[] { null }); }
    }

    private static async Task VerifyPresentation(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        using var canvas = new GH_Canvas { Size = new Size(600, 500) };
        using var gate = new ManualResetEventSlim(false);
        var session = fixture.Session;
        var numbers = Enumerable.Range(0, 256).Select(i => new PanelRow(i, "{0}", "Number", i.ToString(), i)).ToArray();
        var small = new PanelSnapshot(numbers, new[] { new PanelBranch("{0}", numbers.Length) });
        Invoke(session, "AttachSnapshot", small);
        Check(Current(session), "Trees at the immediate threshold publish synchronously");
        Query(session, new PanelQueryOptions { Search = "> 250" });
        Check(Current(session) && Result(session).RowPositions.Count == 5, "Small scalar filters complete without a pending frame");
        var large = new PanelSnapshot(numbers.Concat(new[] { numbers[0] }).ToArray(), small.Branches);
        Invoke(session, "AttachSnapshot", large);
        Check(Property<bool>(session, "IsBusy") && !Current(session), "Trees above the threshold use a background query");
        await Wait(() => Current(session));
        var emptyBranches = new PanelSnapshot(Array.Empty<PanelRow>(), Enumerable.Range(0, 257).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray());
        Invoke(session, "AttachSnapshot", emptyBranches);
        Check(Property<bool>(session, "IsBusy"), "Empty branch count also bounds synchronous query work");
        await Wait(() => Current(session));
        Query(session, new PanelQueryOptions());
        var geometry = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Surface", "Srf", null,
            geometryInfo: new PanelGeometryInfo("Srf", power: 2, metricName: "Area", canMeasure: true)) }, small.Branches);
        Invoke(session, "AttachSnapshot", geometry);
        int started = 0;
        Invoke(session, "SetGeometryDetailResolver", (Func<int, Func<PanelGeometryInfo?>?>)(_ => () =>
        {
            Interlocked.Increment(ref started);
            if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return new PanelGeometryInfo("Srf", 100, 2, "mm", "Area", "A");
        }));
        Paint(fixture.Component, canvas);
        Check(Hits(fixture.Component, "_rowHits") == 1, "Ready rows have canvas hit regions");
        try
        {
            Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 10" });
            await Wait(() => Volatile.Read(ref started) == 1);
            Paint(fixture.Component, canvas, Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-recomputing.png"));
            Check(!Current(session) && Hits(fixture.Component, "_rowHits") == 0 && Hits(fixture.Component, "_branchHits") == 0,
                "Pending geometry filters hide old rows and branch hit regions");
            var timer = Field<System.Windows.Forms.Timer>(session, "_busyAnimation");
            Check(timer.Enabled, "The visible recomputing spinner has an active animation timer");
            // Expiration must reject the old worker before replacement input exists.
            fixture.Component.ClearData();
            Query(session, new PanelQueryOptions());
            Check(Property<bool>(session, "IsBusy") && !Current(session), "Changing a filter cannot revive expired input while awaiting the next solution");
            Invoke(session, "AttachSnapshot", small);
            gate.Set();
            await Wait(() => !Field<bool>(session, "_geometryRunning"));
            Check(Current(session) && ReferenceEquals(Result(session).Snapshot, small) && !timer.Enabled,
                "Late geometry completion cannot replace newer input and completion stops animation");
            Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
            Check(Property<bool>(session, "IsBusy"), "Even small regex queries stay off the UI thread");
            await Wait(() => !Property<bool>(session, "IsBusy"));
            Paint(fixture.Component, canvas);
            Check(!Current(session) && Property<string?>(session, "Error") != null && Hits(fixture.Component, "_rowHits") == 0,
                "Failed queries leave old results hidden and recover through Reset");
            Query(session, new PanelQueryOptions());
            Check(Current(session), "Reset recovers immediately from a failed query");
        }
        finally { gate.Set(); }
    }

    private static async Task VerifyOutput(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        var source = fixture.Source;
        var component = fixture.Component;
        var session = fixture.Session;
        source.PersistentData.Append(new GH_Integer(1000), new GH_Path(4, 0));
        source.PersistentData.Append(new GH_Number(2500.125), new GH_Path(4, 0));
        source.PersistentData.Append(new GH_Number(4000), new GH_Path(4, 1));
        source.PersistentData.Append(new GH_String("keep text"), new GH_Path(4, 1));
        source.PersistentData.Append(null!, new GH_Path(4, 1));
        source.PersistentData.EnsurePath(new GH_Path(4, 2));
        fixture.Solve();
        Check(fixture.Sink.VolatileData.DataCount == 5 && fixture.Sink.VolatileData.PathCount == 3,
            "Unfiltered output forwards typed data, nulls and empty branches downstream");
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 1500" });
        await Wait(() => fixture.Values().SequenceEqual(new[] { 2500.125, 4000.0 }) && fixture.Sink.VolatileData.DataCount == 2);
        Check(fixture.Sink.VolatileData.Paths.Select(p => p.ToString()).SequenceEqual(new[] { "{4;0}", "{4;1}" }),
            "Value filtering propagates only matching typed rows with their paths");
        PanelTestAdapter.TreeTools(component, 1, true, true, true);
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 2" });
        await Wait(() => fixture.Values().SequenceEqual(new[] { 4.0, 2.500125 }));
        Check(fixture.Sink.VolatileData.PathCount == 1 && fixture.Sink.VolatileData.Paths[0].ToString() == "{0}",
            "Downstream output combines tree manipulation, full-precision unit conversion and filtering in order");
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 2", SortMode = PanelSortMode.ValueAscending });
        await Wait(() => Current(session));
        Check(fixture.Values().SequenceEqual(new[] { 4.0, 2.500125 }), "View sorting preserves transformed output tree order");
        Invoke(component, "ResetPanel");
        await Wait(() => fixture.Sink.VolatileData.DataCount == 5 && fixture.Sink.VolatileData.PathCount == 3);
        Query(session, new PanelQueryOptions { BranchPath = "{4;2}" });
        await Wait(() => fixture.Sink.VolatileData.DataCount == 0 && fixture.Sink.VolatileData.PathCount == 1);
        Check(fixture.Sink.VolatileData.Paths[0].ToString() == "{4;2}", "Branch filtering retains a selected empty branch");
        Query(session, new PanelQueryOptions { TypeName = "Text" });
        await Wait(() => fixture.Sink.VolatileData.DataCount == 1 && fixture.Sink.VolatileData.AllData(true).Single() is GH_String);
        Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
        await Wait(() => !Property<bool>(session, "IsBusy") && fixture.Sink.VolatileData.DataCount == 0);
        Check(Property<string?>(session, "Error") != null, "Invalid filters publish empty output instead of old or unfiltered values");
        Query(session, new PanelQueryOptions());
        await Wait(() => fixture.Sink.VolatileData.DataCount == 5);
        Check(source.PersistentData.DataCount == 5 && ((GH_Integer)source.PersistentData.Branches[0][0]).Value == 1000,
            "Filtering, tree tools, conversion and reset never mutate upstream values");

        // A real native area filter must resume the GH solution after measurement.
        source.PersistentData.Clear();
        source.PersistentData.Append(new GH_Surface(new Rhino.Geometry.PlaneSurface(Rhino.Geometry.Plane.WorldXY,
            new Rhino.Geometry.Interval(0, 10), new Rhino.Geometry.Interval(0, 10)).ToBrep()), new GH_Path(0));
        fixture.Solve();
        var snapshot = Property<PanelSnapshot>(session, "Snapshot");
        using var gate = new ManualResetEventSlim(false);
        int started = 0;
        Invoke(session, "SetGeometryDetailResolver", (Func<int, Func<PanelGeometryInfo?>?>)(_ => () =>
        {
            Interlocked.Increment(ref started);
            if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return new PanelGeometryInfo("Srf", 100, 2, "mm", "Area", "A");
        }));
        try
        {
            Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 10" });
            await Wait(() => Volatile.Read(ref started) == 1 && fixture.Sink.VolatileData.DataCount == 0);
            Check(Property<bool>(session, "IsBusy"), "Pending native filters clear downstream output without blocking the UI");
            gate.Set();
            await Wait(() => Current(session) && fixture.Sink.VolatileData.DataCount == 1);
            Check(ReferenceEquals(snapshot, Property<PanelSnapshot>(session, "Snapshot")) && started == 1,
                "Geometry completion updates downstream output without recapturing or remeasuring input");

            gate.Reset();
            fixture.Solve();
            await Wait(() => Volatile.Read(ref started) == 2);
            source.PersistentData.Clear();
            source.PersistentData.Append(new GH_Number(7), new GH_Path(9));
            fixture.Solve();
            Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 0" });
            await Wait(() => fixture.Values().SequenceEqual(new[] { 7.0 }));
            gate.Set();
            await Wait(() => !Field<bool>(session, "_geometryRunning"));
            await Task.Delay(80);
            Check(fixture.Values().SequenceEqual(new[] { 7.0 }) && fixture.Sink.VolatileData.Paths[0].ToString() == "{9}",
                "Input replacement rejects late geometry output and propagates only the new solution");
        }
        finally { gate.Set(); }
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string? path = null)
    {
        component.Attributes.Pivot = new PointF(40, 40);
        component.Attributes.PerformLayout();
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
        if (path != null) bitmap.Save(path);
    }

    private static int Hits(GH_Component component, string name) => ((ICollection)Field<object>(component.Attributes, name)).Count;
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, args);
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static bool Current(object session) => Property<bool>(session, "HasCurrentResult");
    private static PanelQueryResult Result(object session) => Property<PanelQueryResult>(session, "Result");
    private static void Query(object session, PanelQueryOptions options) => Invoke(session, "UpdateQuery", options);
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Recompute check did not finish");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document { Enabled = true };
        internal readonly Param_GenericObject Source = new Param_GenericObject();
        internal readonly Param_GenericObject Sink = new Param_GenericObject();
        internal readonly GH_Component Component;
        internal object Session => PanelTestAdapter.Session(Component);
        internal Fixture(Assembly assembly)
        {
            GH_Document.EnableSolutions = true;
            Component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            Source.CreateAttributes(); Component.CreateAttributes(); Sink.CreateAttributes();
            Document.AddObject(Source, false); Document.AddObject(Component, false); Document.AddObject(Sink, false);
            Component.Params.Input[0].AddSource(Source);
            Sink.AddSource(Component.Params.Output[0]);
        }
        internal void Solve() { Source.ExpireSolution(false); Document.NewSolution(false, GH_SolutionMode.Silent); }
        internal double[] Values() => Sink.VolatileData.AllData(true).OfType<GH_Number>().Select(n => n.Value).ToArray();
        public void Dispose() { Component.DocumentContextChanged(Document, GH_DocumentContext.Close); Document.Dispose(); }
    }
}
