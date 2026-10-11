using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class FilterChecks
{
    internal static async Task Run(Assembly assembly)
    {
        VerifyMetadata(assembly);
        await VerifyMeasurements(assembly);
        await VerifyCancellation(assembly);
        await VerifySnapshotReplacement(assembly);
    }

    private static void VerifyMetadata(Assembly assembly)
    {
        var describe = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!
            .GetMethod("Describe", BindingFlags.Static | BindingFlags.NonPublic)!;
        PanelGeometryInfo Describe(IGH_Goo goo) => (PanelGeometryInfo)describe.Invoke(null, new object[] { goo, "mm" })!;
        using var plane = new PlaneSurface(Plane.WorldXY, new Interval(0, 10), new Interval(0, 10));
        using var surface = plane.ToBrep();
        using var solid = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        var area = Describe(new GH_Surface(surface));
        var open = Describe(new GH_Brep(surface));
        var volume = Describe(new GH_Brep(solid));
        Check(area.Power == 2 && area.MetricName == "Area" && open.Power == 2 && open.MetricName == "Area" &&
            volume.Power == 3 && volume.MetricName == "Volume" && area.CanMeasure && volume.CanMeasure &&
            area.Value == null && volume.Value == null,
            "Snapshot metric metadata identifies surface/open Brep area and solid volume without mass calculation");
    }

    private static async Task VerifyMeasurements(Assembly assembly)
    {
        var rows = new[]
        {
            AreaRow(0, "{0}"), AreaRow(1, "{0;1}"),
            new PanelRow(2, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", canMeasure: true)),
            new PanelRow(3, "{0}", "Brep", "Brep", null,
                geometryInfo: new PanelGeometryInfo("Brep", power: 3, metricName: "Volume", metricLabel: "V", canMeasure: true)),
            AreaRow(4, "{1}"), AreaRow(5, "{0}", false),
            new PanelRow(6, "{0}", "Rectangle", "Rectangle", null, geometryInfo: Metric(6)),
            AreaRow(7, "{0}"), new PanelRow(8, "{0}", "Number", "70", 70)
        };
        using var fixture = new Fixture(assembly, rows);
        await WaitForQuery(fixture);
        var prepared = new List<int>();
        int active = 0, maximumActive = 0, published = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        bool preparationOnUi = true, computationOnWorker = true;
        fixture.OnResult(() => published++);
        fixture.SetResolver(position =>
        {
            preparationOnUi &= Environment.CurrentManagedThreadId == uiThread;
            prepared.Add(position);
            return () =>
            {
                computationOnWorker &= Environment.CurrentManagedThreadId != uiThread;
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                try
                {
                    if (position == 7) throw new InvalidOperationException("Unavailable area");
                    return Metric(position == 0 ? 12 : 25);
                }
                finally { Interlocked.Decrement(ref active); }
            };
        });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 10",
            BranchPath = "{0}", BranchPrefix = true, TypeName = "Surface" };
        fixture.Query(options);
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 0, 1 }) && prepared.SequenceEqual(new[] { 0, 1, 7 }),
            "Area filtering measures all eligible offscreen branch descendants and skips other metrics, types and invalid rows");
        Check(maximumActive == 1 && preparationOnUi && computationOnWorker && published == 1,
            "Metric filtering shares one worker, prepares on the UI thread and publishes only its complete result");
        options.Search = "> 20";
        fixture.Query(options);
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 1 }) && prepared.Count == 3,
            "Changing a metric threshold reuses completed values and cached failures");

        options.Search = "> 0.00002";
        fixture.Query(options);
        fixture.Attach(new PanelSnapshot(rows, fixture.Snapshot.Branches, PanelUnitConversion.SquareMillimetersToSquareMeters), true);
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 1 }) && prepared.Count == 3,
            "A unit-only snapshot filters retained raw measurements using full precision converted area");

        int beforeError = published;
        options.Search = "> invalid";
        fixture.Query(options);
        await WaitUntil(() => !fixture.IsBusy);
        Check(fixture.Error != null && published == beforeError + 1 && prepared.Count == 3,
            "Invalid metric expressions report completion with an error before preparing geometry");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
        await WaitUntil(() => !fixture.IsBusy);
        Check(fixture.Error != null && prepared.Count == 3, "Invalid regex reports an error without geometry work");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 60" });
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 8 }) && prepared.Count == 3,
            "Changing only filter mode starts a fresh query and value filters use numeric rows without geometry work");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Volume });
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.Count == rows.Length && prepared.Count == 3,
            "An empty metric expression leaves the view unfiltered without computing deferred geometry");
    }

    private static async Task VerifyCancellation(Assembly assembly)
    {
        using var fixture = new Fixture(assembly, new[] { AreaRow(0, "{0}"), AreaRow(1, "{0}"), AreaRow(2, "{1}") });
        await WaitForQuery(fixture);
        using var gate = new ManualResetEventSlim(false);
        var prepared = new List<int>();
        int published = 0;
        fixture.OnResult(() => published++);
        fixture.SetResolver(position =>
        {
            prepared.Add(position);
            return () =>
            {
                if (position == 0 && !gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                return Metric(position + 1);
            };
        });
        try
        {
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 0", BranchPath = "{0}" });
            await WaitUntil(() => prepared.Count == 1);
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 0", BranchPath = "{1}" });
            await WaitUntil(() => fixture.Pending(2));
            Check(fixture.IsBusy && published == 0, "Changing branches during measurement does not publish partial results");
            gate.Set();
            await WaitForQuery(fixture);
            Check(fixture.Result.RowPositions.SequenceEqual(new[] { 2 }) && prepared.SequenceEqual(new[] { 0, 2 }) && published == 1,
                "A replacement query cancels abandoned offscreen rows and publishes only its current branch");
        }
        finally { gate.Set(); }
    }

    private static async Task VerifySnapshotReplacement(Assembly assembly)
    {
        var rows = new[] { AreaRow(0, "{0}"), AreaRow(1, "{0}") };
        using var fixture = new Fixture(assembly, rows);
        await WaitForQuery(fixture);
        using var gate = new ManualResetEventSlim(false);
        var prepared = new List<int>();
        int published = 0;
        fixture.OnResult(() => published++);
        fixture.SetResolver(position =>
        {
            bool first = prepared.Count == 0;
            prepared.Add(position);
            return () =>
            {
                if (first && !gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                return Metric(first ? 100 : 1);
            };
        });
        try
        {
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 10" });
            await WaitUntil(() => prepared.Count == 1);
            var replacement = new PanelSnapshot(rows, fixture.Snapshot.Branches);
            fixture.Attach(replacement);
            gate.Set();
            await WaitForQuery(fixture);
            Check(ReferenceEquals(fixture.Result.Snapshot, replacement) && fixture.Result.RowPositions.Count == 0 &&
                prepared.SequenceEqual(new[] { 0, 0, 1 }) && published == 1,
                "A replacement snapshot discards in-flight old measurements and cannot publish stale geometry filter results");
        }
        finally { gate.Set(); }
    }

    private static PanelGeometryInfo Metric(double value) => new PanelGeometryInfo("Srf", value, 2, "mm", "Area", "A");
    private static PanelRow AreaRow(int index, string path, bool valid = true) => new PanelRow(index, path, "Surface", "Srf", null,
        isValid: valid, geometryInfo: new PanelGeometryInfo("Srf", power: 2, metricName: "Area", metricLabel: "A", canMeasure: true));

    private static async Task WaitForQuery(Fixture fixture)
    {
        await WaitUntil(() => !fixture.IsBusy);
        if (fixture.Error != null) throw new InvalidOperationException(fixture.Error);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Filter check did not complete");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object _session;
        private readonly Type _type;
        internal Fixture(Assembly assembly, IReadOnlyList<PanelRow> rows)
        {
            _type = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
            _session = Activator.CreateInstance(_type, new object[] { (Action)(() => { }) })!;
            Snapshot = new PanelSnapshot(rows, rows.GroupBy(row => row.Path).Select(group => new PanelBranch(group.Key, group.Count())).ToArray());
            Attach(Snapshot);
        }

        internal PanelSnapshot Snapshot { get; private set; }
        internal PanelQueryResult Result => (PanelQueryResult)_type.GetProperty("Result")!.GetValue(_session)!;
        internal bool IsBusy => (bool)_type.GetProperty("IsBusy")!.GetValue(_session)!;
        internal string? Error => (string?)_type.GetProperty("Error")!.GetValue(_session);
        private object? Invoke(string name, params object[] arguments) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_session, arguments);
        internal void Query(PanelQueryOptions options) => Invoke("UpdateQuery", options);
        internal void OnResult(Action callback) => _type.GetEvent("ResultChanged")!.AddEventHandler(_session, (EventHandler)((_, __) => callback()));
        internal bool Pending(int position) => (bool)Invoke("IsGeometryPending", Snapshot, position)!;
        internal void SetResolver(Func<int, Func<PanelGeometryInfo?>?> resolver) => Invoke("SetGeometryDetailResolver", resolver);
        internal void Attach(PanelSnapshot snapshot, bool retain = false)
        {
            Snapshot = snapshot;
            if (retain) Invoke("AttachConvertedSnapshot", snapshot, (Func<int, bool>)(_ => true));
            else Invoke("AttachSnapshot", snapshot);
        }
        public void Dispose() => ((IDisposable)_session).Dispose();
    }
}
