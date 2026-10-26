using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
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
        await VerifyLifecycleAndPaths(assembly);
        await VerifyDisplayedOutput(assembly);
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
        fixture.Query(new PanelQueryOptions { Search = "measurement unavailable", BranchPath = "{0}", TypeName = "Surface" });
        await WaitForQuery(fixture);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 7 }) && prepared.Count == 3,
            "Displayed-text filters include the persistent unavailable measurement status and reuse cached failures");

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

    private static async Task VerifyLifecycleAndPaths(Assembly assembly)
    {
        using var fixture = new Fixture(assembly, new[] { new PanelRow(0, "{0;0;1}", "Number", "12", 12) });
        await WaitForQuery(fixture);
        fixture.Query(new PanelQueryOptions { BranchPath = " 0 ; 0 ; 1 " });
        await WaitForQuery(fixture);
        Check(fixture.Options.BranchPath == "{0;0;1}" && fixture.Result.RowPositions.Count == 1,
            "Session branch input accepts optional braces and stores a canonical path");
        fixture.Query(new PanelQueryOptions { BranchPath = "{0;broken}" });
        await WaitUntil(() => !fixture.IsBusy);
        Check(fixture.Error != null, "Malformed path input reports a query error without escaping its editor callback");
        fixture.Query(new PanelQueryOptions());
        await WaitForQuery(fixture);
        fixture.Invalidate();
        Check(fixture.Result.Snapshot.Rows.Count == 0 && fixture.Result.RowPositions.Count == 0,
            "Input expiration releases the obsolete result instead of retaining a stale tree");
        fixture.Attach(new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "2", 2) }, new[] { new PanelBranch("{0}", 1) }));
        await WaitForQuery(fixture);
        fixture.Dispose();
        Check(fixture.Result.Snapshot.Rows.Count == 0 && fixture.Result.RowPositions.Count == 0,
            "Disposing a session releases its last captured result");
    }

    private static async Task VerifyDisplayedOutput(Assembly assembly)
    {
        // Scheduled output changes need the host editor's message dispatch handle.
        using var editor = new Grasshopper.GUI.GH_DocumentEditor();
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            using var fixture = new OutputFixture(assembly);
            fixture.Source.PersistentData.Append(new GH_Number(1234.56789), new GH_Path(0, 0, 1));
            fixture.Source.PersistentData.Append(new GH_Integer(2000), new GH_Path(0, 0, 1));
            fixture.Source.PersistentData.Append(new GH_String("other"), new GH_Path(0, 0, 2));
            fixture.Source.PersistentData.EnsurePath(new GH_Path(0, 0, 3));
            fixture.Solve();
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, 2);
            fixture.Query(new PanelQueryOptions { BranchPath = "0;0;1", SearchMode = PanelSearchMode.Regex, Search = "^1[.,]23 m$" });
            await fixture.WaitForOutput(1);
            var snapshot = fixture.Snapshot;
            Check(fixture.Result.RowPositions.SequenceEqual(new[] { 0 }) &&
                fixture.Options.DecimalPlaces == 2 &&
                Math.Abs(((GH_Number)fixture.Sink.VolatileData.AllData(true).Single()).Value - 1.23456789) < 1e-12 &&
                PanelTestAdapter.CopyValue(fixture.Component, snapshot, 0) == "1.23" &&
                PanelTestAdapter.DisplayValue(fixture.Component, snapshot, 0).EndsWith("23 m", StringComparison.Ordinal),
                "A regex on the displayed converted rounded value selects the same typed full-precision output and copied numeric value");
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, 3);
            await fixture.WaitForOutput(0);
            Check(fixture.Result.RowPositions.Count == 0, "Changing decimals requeries displayed-text filters and updates downstream membership");
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, 2);
            await fixture.WaitForOutput(1);
            PanelTestAdapter.ResetView(fixture.Component);
            await fixture.WaitForOutput(3);
            Check(fixture.Options.DecimalPlaces == 2 && PanelTestAdapter.CopyValue(fixture.Component, fixture.Snapshot, 0) == "1.23",
                "Reset view retains numeric formatting in both rendering and subsequent query options");
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "= 2", DecimalPlaces = 2 });
            await fixture.WaitForOutput(2);
            Check(fixture.Sink.VolatileData.Paths.Single().ToString() == "{0;0;1}" &&
                fixture.Sink.VolatileData.AllData(true).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 1234.56789 / 1000, 2.0 }),
                "List length keeps complete matching transformed branches and their converted typed values");
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "= 0", DecimalPlaces = 2 });
            await fixture.WaitForOutput(0);
            Check(fixture.Sink.VolatileData.PathCount == 1 && fixture.Sink.VolatileData.Paths[0].ToString() == "{0;0;3}",
                "List length zero retains the matching empty output branch");
            fixture.Source.PersistentData.Append(new GH_Number(1234.56789), new GH_Path(0, 0, 2));
            fixture.Solve();
            PanelTestAdapter.TreeTools(fixture.Component, 0, true, true, false, true);
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "= 4", TypeName = "Number" });
            await fixture.WaitForOutput(2);
            Check(fixture.Sink.VolatileData.PathCount == 1 && fixture.Sink.VolatileData.Paths[0].ToString() == "{0}" &&
                fixture.Sink.VolatileData.AllData(true).OfType<GH_Number>().Select(n => n.Value).SequenceEqual(new[] { 1.23, 2.0 }),
                "List length counts the flattened branch before type filtering and Create set, then the set outputs displayed numeric values");
            PanelTestAdapter.TreeTools(fixture.Component, 0, false, false, false);
            fixture.Source.PersistentData.Branches[1].RemoveAt(1);
            fixture.Solve();

            fixture.Query(new PanelQueryOptions { DecimalPlaces = 2 });
            await fixture.WaitForOutput(3);
            int solves = fixture.Solves;
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, 5);
            await WaitUntil(() => fixture.Current);
            await Task.Delay(80);
            Check(fixture.Solves == solves, "Decimal formatting without a displayed-text filter does not recompute typed output");

            fixture.Source.PersistentData.Clear();
            fixture.Source.PersistentData.Append(new InvalidNumber(1500), new GH_Path(0));
            fixture.Source.PersistentData.Append(new InvalidInteger(3000), new GH_Path(0));
            fixture.Query(new PanelQueryOptions { OnlyInvalid = true, DecimalPlaces = 5 });
            fixture.Solve();
            await fixture.WaitForOutput(2);
            Check(fixture.Sink.VolatileData.AllData(true).All(item => !item.IsValid) &&
                fixture.Snapshot.Rows.All(row => !row.IsValid) &&
                PanelTestAdapter.CopyValue(fixture.Component, fixture.Snapshot, 0).StartsWith("Invalid ·", StringComparison.Ordinal),
                "Conversion preserves invalid scalar types and validity, and copying keeps their invalid marker");
            fixture.Component.ClearData();
            Check(((Array)fixture.Component.GetType().GetField("_sourceItems", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(fixture.Component)!).Length == 0 && fixture.Result.Snapshot.Rows.Count == 0,
                "Clearing component input releases live values and immutable results before recomputing");

            await VerifyDisplayedGeometryOutput(assembly);
        }
        finally { setEditor.Invoke(null, new object?[] { null }); }
    }

    private static async Task VerifyDisplayedGeometryOutput(Assembly assembly)
    {
        var rhino = Rhino.RhinoDoc.ActiveDoc!;
        var units = rhino.ModelUnitSystem;
        bool modified = rhino.Modified;
        rhino.ModelUnitSystem = Rhino.UnitSystem.Millimeters;
        try
        {
            using var fixture = new OutputFixture(assembly);
            using var plane = new PlaneSurface(Plane.WorldXY, new Interval(0, 10), new Interval(0, 10));
            using var surface = plane.ToBrep();
            uint crc = surface.DataCRC(0);
            fixture.Source.PersistentData.Append(new GH_Surface(surface), new GH_Path(1));
            fixture.Solve();
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.SquareMillimetersToSquareMeters, 6);
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"A 0\.000100 m²$" });
            await fixture.WaitForOutput(1);
            var snapshot = fixture.Snapshot;
            Check(fixture.Sink.VolatileData.AllData(true).Single() is GH_Surface &&
                PanelTestAdapter.DisplayValue(fixture.Component, snapshot, 0).EndsWith("A 0.000100 m²", StringComparison.Ordinal) &&
                PanelTestAdapter.CopyValue(fixture.Component, snapshot, 0) == "0.000100" && surface.DataCRC(0) == crc,
                "Displayed-text filters measure offscreen geometry in converted displayed precision while output retains its typed original geometry");
            PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.SquareMillimetersToSquareMeters, 7);
            await fixture.WaitForOutput(0);
            Check(ReferenceEquals(snapshot, fixture.Snapshot),
                "Changing displayed geometry precision updates filter membership using the existing snapshot and measurements");
            fixture.Query(new PanelQueryOptions());
            await fixture.WaitForOutput(1);
            int failures = 0;
            fixture.SetResolver(_ => () =>
            {
                Interlocked.Increment(ref failures);
                throw new InvalidOperationException("Failed geometry measurement fixture");
            });
            fixture.Solve();
            fixture.Query(new PanelQueryOptions { Search = "measurement unavailable" });
            await fixture.WaitForOutput(1);
            Check(PanelTestAdapter.DisplayValue(fixture.Component, fixture.Snapshot, 0).EndsWith(" · measurement unavailable", StringComparison.Ordinal) &&
                fixture.Sink.VolatileData.AllData(true).Single() is GH_Surface && failures == 1,
                "Filtering the displayed unavailable measurement status retains exactly that typed geometry on the output");
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "measurement unavailable$" });
            await fixture.WaitForOutput(1);
            Check(failures == 1, "Regex sees the same persistent measurement failure without retrying a failed native operation");
        }
        finally { rhino.ModelUnitSystem = units; rhino.Modified = modified; }
    }

    private sealed class InvalidNumber : GH_Number
    {
        internal InvalidNumber(double value) : base(value) { }
        public override bool IsValid => false;
        public override IGH_Goo Duplicate() => new InvalidNumber(Value);
    }

    private sealed class InvalidInteger : GH_Integer
    {
        internal InvalidInteger(int value) : base(value) { }
        public override bool IsValid => false;
        public override IGH_Goo Duplicate() => new InvalidInteger(Value);
    }

    private sealed class OutputFixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document { Enabled = true };
        internal readonly Param_GenericObject Source = new Param_GenericObject();
        internal readonly Param_GenericObject Sink = new Param_GenericObject();
        internal readonly GH_Component Component;
        private readonly Type _sessionType;
        private readonly object _session;
        internal int Solves { get; private set; }
        internal OutputFixture(Assembly assembly)
        {
            GH_Document.EnableSolutions = true;
            Component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            Source.CreateAttributes(); Component.CreateAttributes(); Sink.CreateAttributes();
            Document.AddObject(Source, false); Document.AddObject(Component, false); Document.AddObject(Sink, false);
            Component.Params.Input[0].AddSource(Source);
            Sink.AddSource(Component.Params.Output[0]);
            _session = PanelTestAdapter.Session(Component);
            _sessionType = _session.GetType();
            Document.SolutionEnd += (_, __) => Solves++;
        }
        internal PanelSnapshot Snapshot => (PanelSnapshot)_sessionType.GetProperty("Snapshot")!.GetValue(_session)!;
        internal PanelQueryResult Result => (PanelQueryResult)_sessionType.GetProperty("Result")!.GetValue(_session)!;
        internal PanelQueryOptions Options => (PanelQueryOptions)_sessionType.GetProperty("Options")!.GetValue(_session)!;
        internal bool Current => (bool)_sessionType.GetProperty("HasCurrentResult")!.GetValue(_session)!;
        internal void Query(PanelQueryOptions options) => _sessionType.GetMethod("UpdateQuery")!.Invoke(_session, new object[] { options });
        internal void SetResolver(Func<int, Func<PanelGeometryInfo?>?> resolver) =>
            _sessionType.GetMethod("SetGeometryDetailResolver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_session, new object[] { resolver });
        internal void Solve() { Source.ExpireSolution(false); Document.NewSolution(false, GH_SolutionMode.Silent); }
        internal Task WaitForOutput(int count) => WaitUntil(() => Current && Sink.VolatileData.DataCount == count);
        public void Dispose() { Component.DocumentContextChanged(Document, GH_DocumentContext.Close); Document.Dispose(); }
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
        internal PanelQueryOptions Options => (PanelQueryOptions)_type.GetProperty("Options")!.GetValue(_session)!;
        private object? Invoke(string name, params object[] arguments) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_session, arguments);
        internal void Query(PanelQueryOptions options) => Invoke("UpdateQuery", options);
        internal void Invalidate() => Invoke("InvalidateInput", false, false);
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
