using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

internal static class OutputChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void VerifyTree(Assembly assembly)
    {
        var treeTools = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var outputTools = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        var apply = treeTools.GetMethod("ApplyWithSources", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pair = outputTools.GetMethod("Pair", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mask = outputTools.GetMethod("Mask", BindingFlags.Static | BindingFlags.NonPublic)!;
        var select = treeTools.GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic)!;
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("CaptureWithSources", BindingFlags.Static | BindingFlags.NonPublic)!;
        var input = new GH_Structure<IGH_Goo>();
        var paired = new GH_Structure<IGH_Goo>();
        input.AppendRange(new IGH_Goo[] { new GH_Number(12), new GH_Integer(4), new GH_Number(12), null! }, new GH_Path(7, 0));
        input.AppendRange(new IGH_Goo[] { new GH_Integer(4), new GH_Number(18) }, new GH_Path(7, 0, 1));
        input.EnsurePath(new GH_Path(7, 1));
        input.EnsurePath(new GH_Path(9));
        var byAddress = new Dictionary<(string, int), IGH_Goo>();
        for (int b = 0; b < input.PathCount; b++)
        {
            paired.EnsurePath(input.Paths[b]);
            for (int i = 0; i < input.Branches[b].Count; i++)
            {
                string path = input.Paths[b].ToString();
                byAddress.Add((path, i), input.Branches[b][i]);
                paired.Append(new GH_String(path + ":" + i), input.Paths[b]);
            }
        }
        var original = input.Branches.Select(branch => branch.ToArray()).ToArray();
        for (int trim = 0; trim <= 3; trim++)
            for (int flags = 0; flags < 16; flags++)
            {
                var args = new object?[] { input, trim, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, null };
                var transformed = (GH_Structure<IGH_Goo>)apply.Invoke(null, args)!;
                var addresses = (IReadOnlyList<PanelSourceAddress>)args[5]!;
                var values = transformed.AllData(false).ToArray();
                Check(addresses.Count == values.Length && addresses.Select((a, i) => ReferenceEquals(values[i], byAddress[(a.Path, a.ItemIndex)])).All(v => v),
                    $"Original addresses follow transformed values (trim {trim}, flags {flags})");
                var pairArgs = new object?[] { input, paired, transformed, addresses, null };
                var following = (GH_Structure<IGH_Goo>)pair.Invoke(null, pairArgs)!;
                Check(pairArgs[4] == null && SameShape(transformed, following) && following.AllData(true).Cast<GH_String>()
                    .Select(v => v.Value).SequenceEqual(addresses.Select(a => a.Path + ":" + a.ItemIndex)),
                    $"Paired values retain exact provenance and transformed paths (trim {trim}, flags {flags})");
                var snapshot = (PanelSnapshot)capture.Invoke(null, new object?[] { transformed, null, PanelUnitConversion.None, "mm", addresses })!;
                var query = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = (flags & 8) != 0 });
                var positions = query.RowPositions.Where((position, index) => index % 2 == 0).Reverse().ToArray();
                var booleans = (GH_Structure<GH_Boolean>)mask.Invoke(null, new object[] { input, addresses, positions })!;
                var selected = (GH_Structure<IGH_Goo>)select.Invoke(null, new object[] { transformed, positions })!;
                Check(SameShape(input, booleans) && booleans.AllData(true).Cast<GH_Boolean>().Count(v => v.Value) == positions.Length &&
                    positions.All(i => ((GH_Boolean)booleans.get_Branch(input.Paths.First(path => path.ToString() == addresses[i].Path))[addresses[i].ItemIndex]!).Value) &&
                    selected.AllData(false).SequenceEqual(values.Where((_, i) => positions.Contains(i))),
                    $"B preserves input shape and S ignores selection order (trim {trim}, flags {flags})");
            }
        Check(input.Branches.Select((branch, b) => branch.SequenceEqual(original[b])).All(v => v), "Output mapping never mutates the incoming tree");
        var mismatched = new GH_Structure<IGH_Goo>();
        mismatched.AppendRange(paired.AllData(true), new GH_Path(0));
        var mismatchArgs = new object?[] { input, mismatched, input, Array.Empty<PanelSourceAddress>(), null };
        Check(pair.Invoke(null, mismatchArgs) == null && mismatchArgs[4] is string, "Equal total item counts cannot hide mismatched paired paths");
        var wrongPaths = new GH_Structure<IGH_Goo>();
        for (int b = 0; b < paired.PathCount; b++)
        {
            var indices = paired.Paths[b].Indices.ToArray();
            indices[0] += 100;
            var path = new GH_Path(indices);
            wrongPaths.EnsurePath(path);
            wrongPaths.AppendRange(paired.Branches[b], path);
        }
        mismatchArgs[1] = wrongPaths;
        Check(pair.Invoke(null, mismatchArgs) == null && mismatchArgs[4] is string,
            "Equal branch and item counts cannot hide different original paths");
        var wrongLengths = new GH_Structure<IGH_Goo>();
        foreach (var path in paired.Paths) wrongLengths.EnsurePath(path);
        wrongLengths.AppendRange(paired.AllData(true), paired.Paths[0]);
        mismatchArgs[1] = wrongLengths;
        Check(pair.Invoke(null, mismatchArgs) == null && mismatchArgs[4] is string,
            "Identical paths and equal total counts cannot hide incorrect per-branch paired lengths");
        var emptyInput = new GH_Structure<IGH_Goo>();
        emptyInput.EnsurePath(new GH_Path(4, 0)); emptyInput.EnsurePath(new GH_Path(4, 1));
        var emptyArgs = new object?[] { emptyInput, emptyInput, emptyInput, Array.Empty<PanelSourceAddress>(), null };
        var emptyPair = (GH_Structure<IGH_Goo>)pair.Invoke(null, emptyArgs)!;
        var emptyMask = (GH_Structure<GH_Boolean>)mask.Invoke(null, new object[] { emptyInput, Array.Empty<PanelSourceAddress>(), Array.Empty<int>() })!;
        Check(emptyArgs[4] == null && SameShape(emptyInput, emptyPair) && SameShape(emptyInput, emptyMask), "All-empty paired trees retain matching empty branches in F and B");
        Console.WriteLine("PASS: Output provenance, pairing and mask checks (64 tree-tool combinations).");
    }

    internal static Task Run(Assembly assembly) => RunWithEditor(assembly, RunCore);
    internal static Task RunPending(Assembly assembly) => RunWithEditor(assembly, VerifyPending);

    internal static async Task RunWithEditor(Assembly assembly, Func<Assembly, Task> check)
    {
        // Scheduled solutions need the host editor's native handle, even while
        // the test's Rhino window remains hidden.
        var previous = Grasshopper.Instances.DocumentEditor;
        using var owned = previous == null ? new Grasshopper.GUI.GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            await check(assembly);
            Console.WriteLine("PASS: Output fixture completed.");
        }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static async Task RunCore(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        var c = fixture.Component;
        Check(c.Params.Input.Count == 2 && c.Params.Output.Count == 4 && c.Params.Output.Select(p => p.NickName).SequenceEqual(new[] { "D", "S", "B", "F" }),
            "D retains output zero and optional paired F adds S/B/F outputs");
        fixture.Solve();
        await fixture.Ready(() => fixture.Values(0) == "12,4,18,12,7,3,20");
        Check(fixture.Values(1) == "" && fixture.Values(2) == "True,True,True,True,True,True,True" && fixture.Values(3) == "A,B,C,D,E,F,G",
            "Default Filtered scope publishes all kept positions and paired values with empty S: S=" + fixture.Values(1) + "; B=" + fixture.Values(2) + "; F=" + fixture.Values(3));
        Check(SameShape(fixture.Source.PersistentData, c.Params.Output[2].VolatileData) && SameShape(c.Params.Output[0].VolatileData, c.Params.Output[3].VolatileData),
            "Unfiltered B retains the original empty branch and F matches the D tree shape");

        fixture.Query(new PanelQueryOptions { Search = ">= 10" });
        await fixture.Ready(() => fixture.Values(0) == "12,18,12,20" && fixture.Values(3) == "A,C,D,G");
        Check(fixture.Values(2) == "True,False,True,True,False,False,True", "Filtered B aligns with original input indices across branches");
        var snapshot = fixture.Result.Snapshot;
        var cachedData = c.GetType().GetField("_filteredData", Instance)!.GetValue(c);
        var cachedMask = c.GetType().GetField("_filteredMask", Instance)!.GetValue(c);
        var cachedPair = c.GetType().GetField("_filteredPair", Instance)!.GetValue(c);
        fixture.SelectAt(3);
        fixture.SelectAt(2, toggle: true);
        await fixture.Ready(() => fixture.Values(1) == "18,12");
        Check(ReferenceEquals(snapshot, fixture.Result.Snapshot) && fixture.SelectedCount == 2 && fixture.Values(0) == "12,18,12,20" && fixture.Values(3) == "A,C,D,G",
            "Selection-only solutions retain the snapshot and selection while Filtered B/F stay independent");
        Check(ReferenceEquals(cachedData, c.GetType().GetField("_filteredData", Instance)!.GetValue(c)) &&
            ReferenceEquals(cachedMask, c.GetType().GetField("_filteredMask", Instance)!.GetValue(c)) &&
            ReferenceEquals(cachedPair, c.GetType().GetField("_filteredPair", Instance)!.GetValue(c)),
            "Selection-only updates reuse cached Filtered D/B/F trees instead of rebuilding unchanged outputs");
        int solutionsBeforeMenu = fixture.Solutions;
        fixture.Select(2, preserveSelected: true);
        fixture.Select(3, preserveSelected: true);
        await Task.Delay(50);
        Check(fixture.Solutions == solutionsBeforeMenu, "Repeated gestures preserving the selected group do not schedule downstream solutions");
        fixture.Document.UndoServer.Clear();
        fixture.Scope(PanelOutputScope.Selected);
        await fixture.Ready(() => fixture.Values(3) == "C,D");
        Check(fixture.Values(2) == "False,False,True,True,False,False,False" && fixture.SelectedCount == 2,
            "Selected scope uses the same membership for B and F without clearing S");
        fixture.Document.UndoServer.PerformUndo();
        await fixture.Ready(() => fixture.Values(3) == "A,C,D,G");
        fixture.Document.UndoServer.PerformRedo();
        await fixture.Ready(() => fixture.Values(3) == "C,D");
        Check(fixture.SelectedCount == 2, "Scope Undo/Redo preserves transient row selection");

        fixture.Query(new PanelQueryOptions { Search = ">= 10", SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => fixture.Values(3) == "C,D");
        Check(fixture.Values(1) == "18,12" && fixture.Result.RowPositions[0] == 2, "View sorting does not reorder S or F wires");
        await Task.Delay(50); // Let the query's own scheduled publication finish before measuring the gesture.
        int beforeSortedClick = fixture.Solutions;
        fixture.SelectAt(3, preserveSelected: true);
        await Task.Delay(50);
        Check(fixture.Solutions == beforeSortedClick && fixture.Values(0) == "12,18,12,20" && fixture.Values(1) == "18,12" &&
            fixture.Values(2) == "False,False,True,True,False,False,False" && fixture.Values(3) == "C,D",
            "A preserved sorted view-index gesture retains source-order D/S/F and source-address B without notification");
        fixture.Query(new PanelQueryOptions { Search = ">= 15" });
        await fixture.Ready(() => fixture.Values(0) == "18,20" && fixture.Values(1) == "18" && fixture.Values(3) == "C");
        Check(fixture.SelectedCount == 1 && fixture.Values(2) == "False,False,True,False,False,False,False", "Filtering prunes hidden selections on every downstream output");
        fixture.ClearSelection();
        await fixture.Ready(() => fixture.Values(1) == "" && fixture.Values(3) == "");
        Check(fixture.Values(2).Split(',').All(v => v == "False"), "Clearing Selected scope publishes all-False B and empty S/F");

        fixture.Scope(PanelOutputScope.Filtered);
        fixture.Query(new PanelQueryOptions { Search = ">= 10" });
        PanelTestAdapter.TreeTools(c, 0, true, true, false, true);
        await fixture.Ready(() => fixture.Values(0) == "20,12,18" && fixture.Values(3) == "G,D,C");
        Check(fixture.Values(2) == "False,False,True,True,False,False,True" && fixture.SelectedCount == 0 && c.Params.Output[0].VolatileData.Paths.Single().ToString() == "{0}",
            "Flatten/Reverse/Create set keep the retained primary item's partner and mark only its original occurrence");
        PanelTestAdapter.Units(c, PanelUnitConversion.MillimetersToMeters, 2);
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Values(0) == "0.02,0,0.01" && fixture.Values(3) == "G,F,E");

        fixture.Paired.PersistentData.Clear();
        fixture.Paired.PersistentData.Append(new GH_String("wrong"), new GH_Path(0));
        fixture.Paired.ExpireSolution(false);
        fixture.Document.NewSolution(false, GH_SolutionMode.Silent);
        await fixture.Ready(() => fixture.Values(0).Length > 0 && fixture.Values(3) == "");
        Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any(message => message.Contains("Paired F")) && fixture.Values(2).Length > 0,
            "Paired mismatch empties only F and leaves a warning, D and B available");
        c.Params.Input[1].RemoveAllSources();
        c.ExpireSolution(true);
        await fixture.Ready(() => fixture.Values(0).Length > 0);
        Check(fixture.Values(3) == "" && !c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any(message => message.Contains("Paired F")),
            "Disconnected optional F produces no warning");
        VerifyArchive(fixture);
        fixture.Scope(PanelOutputScope.Filtered);
        var internalized = (Param_GenericObject)c.Params.Input[1];
        int pairedIndex = 0;
        foreach (var path in fixture.Source.PersistentData.Paths)
        {
            internalized.PersistentData.EnsurePath(path);
            foreach (var item in fixture.Source.PersistentData.get_Branch(path))
            {
                internalized.PersistentData.Append(pairedIndex == 4 ? null! : new GH_Number(pairedIndex == 5 ? 1006 : 1000 + pairedIndex), path);
                pairedIndex++;
            }
        }
        internalized.ExpireSolution(true);
        await fixture.Ready(() => fixture.Values(3) == "1006,1006,<null>");
        Check(c.Params.Output[3].VolatileData.DataCount == 3 && c.Params.Output[3].VolatileData.get_Branch(0)[2] == null,
            "Internalized F preserves typed numeric values, duplicates and nulls under primary units, Reverse and Create set");

        fixture.Select(0);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 && fixture.Sinks[3].VolatileData.DataCount == 3);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
        await fixture.Ready(() => Property<string?>(fixture.Session, "Error") != null, allowError: true);
        await Wait(() => fixture.Sinks.All(sink => sink.VolatileData.DataCount == 0));
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Values(0).Length > 0 && fixture.Values(2).Length > 0);
        VerifyPorts(c);
        fixture.Source.PersistentData.Clear();
        fixture.Solve();
        await fixture.Ready(() => fixture.Sinks.All(sink => sink.VolatileData.DataCount == 0));
        Check(fixture.SelectedCount == 0, "Empty replacement input clears transient selection and all output data");
    }

    private static async Task VerifyPending(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        using var gate = new ManualResetEventSlim(false);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Curve(new Rhino.Geometry.LineCurve(Rhino.Geometry.Point3d.Origin, new Rhino.Geometry.Point3d(10, 0, 0)).ToNurbsCurve()), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("old partner"), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Values(3) == "old partner");
        fixture.Select(0);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1);
        int started = 0;
        HostMeasurementPreparation.SetWitnesslessResolver(fixture.Session, _ => () =>
        {
            Interlocked.Increment(ref started);
            if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Pending-output gate timed out");
            return new PanelGeometryInfo("Crv", 10, 1, "mm", "Length", "L");
        });
        try
        {
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Length, Search = "> 5" });
            await Wait(() => Volatile.Read(ref started) == 1 && fixture.Sinks.All(sink => sink.VolatileData.DataCount == 0));
            Check(Property<bool>(fixture.Session, "IsBusy"), "Pending geometry filters clear D/S/B/F before their current result is ready");
            fixture.Scope(PanelOutputScope.Selected);
            fixture.ClearSelection();
            fixture.Scope(PanelOutputScope.Filtered);
            fixture.Query(new PanelQueryOptions());
            fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
            fixture.Source.PersistentData.Append(new GH_Number(42), new GH_Path(9));
            fixture.Paired.PersistentData.Append(new GH_String("new partner"), new GH_Path(9));
            fixture.Solve();
            await fixture.Ready(() => fixture.Values(0) == "42" && fixture.Values(3) == "new partner" && fixture.Values(2) == "True");
            var snapshot = fixture.Result.Snapshot;
            gate.Set();
            await Wait(() => !PanelTestAdapter.GeometryRunning(fixture.Session));
            Check(ReferenceEquals(snapshot, fixture.Result.Snapshot) && fixture.Values(0) == "42" && fixture.Values(3) == "new partner" &&
                fixture.Values(1) == "" && fixture.Values(2) == "True", "Late workers and rapid scope/selection changes cannot republish old paired data after replacement input");
        }
        finally { gate.Set(); }
    }

    private static void VerifyArchive(Fixture fixture)
    {
        fixture.Scope(PanelOutputScope.Selected);
        var chunk = new GH_LooseChunk("Panel");
        Check(fixture.Component.Write(chunk), "Output scope fixture saves");
        var restored = (GH_Component)Activator.CreateInstance(fixture.Component.GetType())!;
        restored.CreateAttributes();
        try
        {
            Check(restored.Read(chunk) && Property<PanelOutputScope>(restored, "OutputScope") == PanelOutputScope.Selected &&
                restored.Params.Input.Count == 2 && restored.Params.Output.Count == 4, "Scope and all port identities survive serialization");
            chunk.RemoveItem("PanelOutputScope");
            Check(restored.Read(chunk) && Property<PanelOutputScope>(restored, "OutputScope") == PanelOutputScope.Filtered, "Older settings default the output scope to Filtered");
            // Serialize a real one-input/one-output component, matching the old schema.
            var legacy = (GH_Component)Activator.CreateInstance(fixture.Component.GetType())!;
            legacy.CreateAttributes();
            legacy.Params.UnregisterInputParameter(legacy.Params.Input[1], true);
            for (int i = legacy.Params.Output.Count - 1; i > 0; i--) legacy.Params.UnregisterOutputParameter(legacy.Params.Output[i], true);
            var inputId = legacy.Params.Input[0].InstanceGuid;
            var outputId = legacy.Params.Output[0].InstanceGuid;
            var oldArchive = new GH_Archive();
            var old = (GH_Chunk)oldArchive.CreateTopLevelNode("Panel");
            Check(legacy.Write(old), "One-port legacy archive saves");
            old.RemoveItem("PanelOutputScope");
            old.RemoveItem("PanelOutputSchema");
            string originalArchive = oldArchive.Serialize_Xml();
            old.Archive.ClearMessages();
            Check(restored.Read(old) && restored.Params.Input.Count == 2 && restored.Params.Output.Count == 4 &&
                restored.Params.Input[0].InstanceGuid == inputId && restored.Params.Output[0].InstanceGuid == outputId,
                "Old one-port definitions acquire added ports while preserving existing D input/output identities");
            Check(old.Archive.MessageCount(false, false, true) == 0 && oldArchive.Serialize_Xml() == originalArchive &&
                restored.Params.Input[1].Optional && restored.Params.Input[1].SourceCount == 0 &&
                ((Param_GenericObject)restored.Params.Input[1]).PersistentData.PathCount == 0,
                "Legacy migration emits no archive errors, leaves the source archive unchanged and initializes an optional disconnected F");
            var corruptArchive = new GH_Archive();
            var corrupt = (GH_Chunk)corruptArchive.CreateTopLevelNode("Panel");
            fixture.Component.Write(corrupt);
            corrupt.RemoveChunk("param_output", 0);
            corrupt.Archive.ClearMessages();
            restored.Read(corrupt);
            Check(corrupt.Archive.MessageCount(false, false, true) > 0, "Genuinely missing D parameter chunks still report archive corruption");
        }
        finally { restored.RemovedFromDocument(fixture.Document); }
    }

    private static void VerifyPorts(GH_Component component)
    {
        PanelTestAdapter.Resize(component, 300, 220);
        var grips = new List<PointF>();
        foreach (string display in new[] { "Full", "Half" })
        {
            var method = component.GetType().GetMethod("SetCanvasDisplay", Instance)!;
            method.Invoke(component, new[] { Enum.Parse(method.GetParameters()[0].ParameterType, display) });
            component.Attributes.PerformLayout();
            var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
            var current = component.Params.Input.Select(p => p.Attributes.InputGrip).Concat(component.Params.Output.Select(p => p.Attributes.OutputGrip)).ToArray();
            Check(current.All(grip => panel.Contains(grip) || grip.X == panel.Right && grip.Y > panel.Top && grip.Y < panel.Bottom),
                "All six port grips remain visible in minimum " + display);
            Check(component.Params.Output.All(parameter => !parameter.Attributes.Bounds.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Resize))),
                "Output wire targets stay clear of the resize corner in minimum " + display);
            CheckNativePortTargets(component, display);
            CheckControlsClearOfPorts(component, display);
            if (display == "Full") grips.AddRange(current);
            else Check(grips.SequenceEqual(current), "Full/Half switching preserves every canonical grip position");
        }
        VerifyNativeControlEdges(component);
        PanelTestAdapter.Minimize(component, true);
        component.Attributes.PerformLayout();
        CheckNativePortTargets(component, "capsule");
        CheckControlsClearOfPorts(component, "capsule");
        PanelTestAdapter.Minimize(component, false);
    }

    private static void CheckControlsClearOfPorts(GH_Component component, string display)
    {
        foreach (CanvasControl control in new[] { CanvasControl.Move, CanvasControl.Close, CanvasControl.Restore, CanvasControl.Display,
            CanvasControl.Preview, CanvasControl.Search, CanvasControl.Type, CanvasControl.Sort, CanvasControl.Branch, CanvasControl.Jump,
            CanvasControl.Reset, CanvasControl.List, CanvasControl.Track, CanvasControl.Resize, CanvasControl.SelectAll, CanvasControl.Statistics })
        {
            var bounds = PanelTestAdapter.Bounds(component, control);
            if (bounds.IsEmpty) continue;
            AssertClearOfPorts(component, bounds, display + " " + control);
        }
    }

    private static void AssertClearOfPorts(GH_Component component, RectangleF bounds, string name)
    {
        Check(!bounds.IsEmpty, name + " has a target for native edge testing");
        foreach (var grip in component.Params.Input.Select(p => p.Attributes.InputGrip).Concat(component.Params.Output.Select(p => p.Attributes.OutputGrip)))
        {
            var nearest = new PointF(Math.Max(bounds.Left + 0.5f, Math.Min(grip.X, bounds.Right - 0.5f)),
                Math.Max(bounds.Top + 0.5f, Math.Min(grip.Y, bounds.Bottom - 0.5f)));
            var picked = component.OnPingDocument().RelevantObjectAtPoint(nearest);
            Check(picked?.Parameter == null, name + " is intercepted by port " + picked?.Parameter?.NickName + " at " + nearest);
        }
    }

    private static void VerifyNativeControlEdges(GH_Component component)
    {
        using var canvas = new GH_Canvas { Document = component.OnPingDocument(), Size = new Size(1000, 700) };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        var move = PanelTestAdapter.Bounds(component, CanvasControl.Move);
        var start = new PointF(move.Left + 1, move.Top + 11);
        var end = new PointF(start.X + 12, start.Y + 8);
        var before = component.Attributes.Pivot;
        NativeEvent("OnMouseMove", start, System.Windows.Forms.MouseButtons.None);
        NativeEvent("OnMouseDown", start);
        NativeEvent("OnMouseMove", end);
        NativeEvent("OnMouseUp", end);
        component.Attributes.PerformLayout();
        Check(component.Attributes.Pivot != before, "Native Half move-handle edge drags the panel instead of starting an input wire");
        var branch = PanelTestAdapter.Bounds(component, CanvasControl.Branch);
        var branchEdge = new PointF(branch.Right - 1, branch.Top + branch.Height / 2);
        PanelTestAdapter.CanvasClick(canvas, branchEdge);
        Check(PanelTestAdapter.MenuOpen(component), "Native Half Paths-button edge opens its menu instead of starting an output wire");
        AssertClearOfPorts(component, PanelTestAdapter.MenuBounds(component), "Paths menu");
        PanelTestAdapter.CanvasClick(canvas, branchEdge);
        using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        var row = PanelTestAdapter.RowBounds(component, 0);
        PanelTestAdapter.CanvasClick(canvas, new PointF(row.Right - 2, row.Top + row.Height / 2), System.Windows.Forms.MouseButtons.Right);
        var rowMenu = (RectangleF)component.Attributes.GetType().GetField("_rowMenuBounds", Instance)!.GetValue(component.Attributes)!;
        AssertClearOfPorts(component, rowMenu, "Row context menu");
        var close = PanelTestAdapter.Bounds(component, CanvasControl.Close);
        PanelTestAdapter.CanvasClick(canvas, new PointF(close.Right - 1, close.Top + close.Height / 2));
        Check(PanelTestAdapter.Minimized(component), "Native Half close-button edge minimizes instead of starting an output wire");
        component.Attributes.PerformLayout();
        var restore = PanelTestAdapter.Bounds(component, CanvasControl.Restore);
        PanelTestAdapter.CanvasClick(canvas, new PointF(restore.Right - 1, restore.Top + 3));
        Check(!PanelTestAdapter.Minimized(component), "Native capsule restore-button edge restores instead of starting an output wire");
        component.Attributes.PerformLayout();

        void NativeEvent(string name, PointF point, System.Windows.Forms.MouseButtons button = System.Windows.Forms.MouseButtons.Left)
        {
            var location = Point.Round(canvas.Viewport.ProjectPoint(point));
            var args = new System.Windows.Forms.MouseEventArgs(button, name == "OnMouseMove" ? 0 : 1, location.X, location.Y, 0);
            typeof(System.Windows.Forms.Control).GetMethod(name, Instance)!.Invoke(canvas, new object[] { args });
        }
    }

    private static void CheckNativePortTargets(GH_Component component, string display)
    {
        var document = component.OnPingDocument();
        foreach (var parameter in component.Params.Output)
            foreach (float offset in new[] { -6f, 0, 6f })
            {
                var grip = parameter.Attributes.OutputGrip;
                var picked = document.RelevantObjectAtPoint(new PointF(grip.X + 1, grip.Y + offset));
                Check(ReferenceEquals(picked?.Parameter, parameter), "Native " + display + " wire picking resolves " + parameter.NickName + " at offset " + offset);
            }
    }

    private static bool SameShape(IGH_Structure a, IGH_Structure b) => a.PathCount == b.PathCount && a.Paths.Select((path, index) =>
        path.CompareTo(b.Paths[index]) == 0 && a.get_Branch(index).Count == b.get_Branch(index).Count).All(v => v);
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Output check did not finish");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    internal sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document { Enabled = true };
        internal readonly Param_GenericObject Source = new Param_GenericObject(), Paired = new Param_GenericObject();
        internal readonly Param_GenericObject[] Sinks = Enumerable.Range(0, 4).Select(_ => new Param_GenericObject()).ToArray();
        internal readonly GH_Component Component;
        internal int Solutions { get; private set; }
        internal object Session => PanelTestAdapter.Session(Component);
        internal PanelQueryResult Result => Property<PanelQueryResult>(Session, "Result");
        private object Selection => Property<object>(Component, "RowSelection");
        internal int SelectedCount => Property<int>(Selection, "Count");
        internal Fixture(Assembly assembly)
        {
            GH_Document.EnableSolutions = true;
            Component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            Source.CreateAttributes(); Paired.CreateAttributes(); Component.CreateAttributes();
            Component.Attributes.Pivot = new PointF(40, 40);
            Document.AddObject(Source, false); Document.AddObject(Paired, false); Document.AddObject(Component, false);
            Document.SolutionEnd += (_, __) => Solutions++;
            var primary = new[] { new double[] { 12, 4, 18, 12, 7 }, new double[] { 3, 20 }, Array.Empty<double>() };
            int letter = 0;
            for (int b = 0; b < primary.Length; b++)
            {
                var path = new GH_Path(7, b);
                Source.PersistentData.EnsurePath(path); Paired.PersistentData.EnsurePath(path);
                foreach (double value in primary[b])
                {
                    Source.PersistentData.Append(new GH_Number(value), path);
                    Paired.PersistentData.Append(new GH_String(((char)('A' + letter++)).ToString()), path);
                }
            }
            Component.Params.Input[0].AddSource(Source); Component.Params.Input[1].AddSource(Paired);
            for (int i = 0; i < Sinks.Length; i++)
            {
                Sinks[i].CreateAttributes(); Document.AddObject(Sinks[i], false); Sinks[i].AddSource(Component.Params.Output[i]);
            }
        }
        internal void Solve() { Source.ExpireSolution(false); Paired.ExpireSolution(false); Document.NewSolution(false, GH_SolutionMode.Silent); }
        internal string Values(int index) => string.Join(",", Sinks[index].VolatileData.AllData(false).Select(value => value?.ToString() ?? "<null>"));
        internal void Query(PanelQueryOptions options) => Session.GetType().GetMethod("UpdateQuery")!.Invoke(Session, new object[] { options });
        internal void Scope(PanelOutputScope scope) => Component.GetType().GetMethod("SetOutputScope", Instance)!.Invoke(Component, new object[] { scope });
        internal void Select(int position, bool toggle = false, bool preserveSelected = false) => Selection.GetType().GetMethod("Select", Instance)!
            .Invoke(Selection, new object[] { Result, position, toggle, false, preserveSelected });
        internal void SelectAt(int position, bool toggle = false, bool preserveSelected = false)
        {
            Check(Result.TryGetViewIndex(position, out int index), "Native selected row is visible in the current view");
            Selection.GetType().GetMethod("SelectAt", Instance)!.Invoke(Selection, new object[] { Result, position, index, toggle, false, preserveSelected });
        }
        internal void ClearSelection() => Selection.GetType().GetMethod("Clear", Instance)!.Invoke(Selection, Array.Empty<object>());
        internal void SelectBranch(string path) => Selection.GetType().GetMethod("SelectAll", Instance)!
            .Invoke(Selection, new object[] { Result, path, true });
        internal async Task Ready(Func<bool> condition, bool allowError = false)
        {
            try
            {
                await Wait(() => !Property<bool>(Session, "IsBusy") && (allowError || Property<bool>(Session, "HasCurrentResult")) && condition());
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Output state: " + string.Join("; ", Enumerable.Range(0, 4).Select(i => i + "=" + Values(i))) +
                    "; busy=" + Property<bool>(Session, "IsBusy") + "; current=" + Property<bool>(Session, "HasCurrentResult") +
                    "; error=" + Property<string?>(Session, "Error") + "; matches=" + Result.RowPositions.Count);
                throw;
            }
        }
        public void Dispose() { Component.DocumentContextChanged(Document, GH_DocumentContext.Close); Document.Dispose(); }
    }
}
