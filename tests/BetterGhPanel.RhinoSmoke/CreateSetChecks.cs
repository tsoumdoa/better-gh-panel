using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static class CreateSetChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void VerifyTree(Assembly assembly)
    {
        var apply = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> tree, bool set = true, bool flatten = false,
            bool reverse = false, int trim = 0, bool simplify = false) =>
            (GH_Structure<IGH_Goo>)apply.Invoke(null, new object[] { tree, trim, simplify, flatten, reverse, set })!;

        var source = new GH_Structure<IGH_Goo>();
        var number = new GH_Number(1);
        var precise = new GH_Number(1.0000000000000002);
        var text = new GH_String("1");
        var upper = new GH_String("Alpha");
        var lower = new GH_String("alpha");
        var space = new GH_String(" ");
        var empty = new GH_String("");
        var boolean = new GH_Boolean(true);
        var point = new GH_Point(new Point3d(1, 2, 3));
        var vector = new GH_Vector(new Vector3d(1, 2, 3));
        var invalid = new GH_Number(double.NaN);
        var unknown = new GH_ObjectWrapper(new object());
        source.AppendRange(new IGH_Goo[] { number, new GH_Integer(1), precise, text, new GH_String("1"),
            upper, new GH_String("Alpha"), lower, space, empty, new GH_String(""), boolean, new GH_Boolean(true),
            point, new GH_Point(point.Value), vector, new GH_Vector(vector.Value), null!, null!, invalid, invalid, unknown, unknown }, new GH_Path(7, 0));
        var otherNumber = new GH_Integer(1);
        source.AppendRange(new IGH_Goo[] { otherNumber, new GH_Number(1), new GH_Number(2) }, new GH_Path(7, 1));
        source.EnsurePath(new GH_Path(7, 2));
        var originals = source.Branches.Select(branch => branch.ToArray()).ToArray();

        var result = Apply(source);
        Check(result.Branches[0].SequenceEqual(new IGH_Goo[] { number, precise, text, upper, lower, space, empty,
            boolean, point, vector, null!, invalid, invalid, unknown, unknown }),
            "Create set keeps first typed occurrences, exact precision, ordinal text and distinct primitive types; nulls collapse and unsupported/invalid values pass through");
        Check(result.PathCount == 3 && result.Branches[1].Count == 2 && result.Branches[2].Count == 0 &&
            ReferenceEquals(result.Branches[1][0], otherNumber), "Create set scopes uniqueness to each branch and retains empty paths");
        Check(ReferenceEquals(Apply(source, set: false), source), "Disabled tools return the original tree");
        Check(source.Branches.Select((branch, index) => branch.SequenceEqual(originals[index])).All(equal => equal) &&
            source.Paths[0].ToString() == "{7;0}", "Create set never edits upstream lists, values or paths");

        var simple = new GH_Structure<IGH_Goo>();
        simple.AppendRange(new IGH_Goo[] { new GH_Number(2), new GH_Number(1), new GH_Number(2) }, new GH_Path(7, 0));
        simple.AppendRange(new IGH_Goo[] { new GH_Integer(1), new GH_Integer(3) }, new GH_Path(7, 1));
        var combined = Apply(simple, flatten: true, reverse: true);
        Check(combined.Paths.Single().ToString() == "{0}" && combined.Branches.Single().Select(value => value.ToString())
            .SequenceEqual(new[] { "3", "1", "2" }) && combined.Branches[0][1] is GH_Integer,
            "Flatten then Reverse then Create set yields global uniqueness in transformed order with the first retained type");
        Check(Apply(simple, trim: 1, simplify: true).Branches.Single().Select(value => value.ToString()).SequenceEqual(new[] { "2", "1", "3" }),
            "Create set runs after Simplify and Trim merge paths");
        Check(Apply(new GH_Structure<IGH_Goo>()).DataCount == 0, "Create set handles an empty tree");

        var colours = new GH_Structure<IGH_Goo>();
        colours.AppendRange(new IGH_Goo[] { new GH_Colour(Color.Red), new GH_Colour(Color.FromArgb(Color.Red.ToArgb())),
            new GH_Integer(Color.Red.ToArgb()), new GH_Number(Color.Red.ToArgb()) }, new GH_Path(0));
        Check(Apply(colours).DataCount == 2, "Named and ARGB colours share a value but remain distinct from numbers");

        var large = new GH_Structure<IGH_Goo>();
        large.AppendRange(Enumerable.Range(0, 100000).Select(i => (IGH_Goo)new GH_Integer(i % 1000)), new GH_Path(0));
        var unique = Apply(large);
        Check(unique.DataCount == 1000 && ((GH_Integer)unique.Branches[0][999]).Value == 999 && large.DataCount == 100000,
            "Large lists produce a stable set without changing the input");

        var distinct = new GH_Structure<IGH_Goo>();
        distinct.AppendRange(Enumerable.Range(0, 100000).Select(i => (IGH_Goo)new GH_Integer(i)), new GH_Path(0));
        var distinctSet = Apply(distinct);
        Check(distinctSet.DataCount == distinct.DataCount && distinctSet.Branches[0].SequenceEqual(distinct.Branches[0]),
            "A large all-unique branch retains every typed value in order");

        var tracked = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!
            .GetMethod("ApplyWithOccurrences", BindingFlags.Static | BindingFlags.NonPublic)!;
        var arguments = new object?[] { simple, 0, false, true, true, true, null };
        var trackedTree = (GH_Structure<IGH_Goo>)tracked.Invoke(null, arguments)!;
        var groups = (System.Collections.Generic.IReadOnlyList<PanelSetOccurrences>)arguments[6]!;
        Check(groups.Select(group => group.Count).SequenceEqual(new[] { 1, 2, 2 }) &&
            groups[1].Locations.Select(address => address.Path + "[" + address.ItemIndex + "]")
                .SequenceEqual(new[] { "{7;1}[0]", "{7;0}[1]" }) && groups[1].BranchCount == 2 &&
            groups[2].Locations.Select(address => address.ItemIndex).SequenceEqual(new[] { 2, 0 }),
            "Flatten and Reverse preserve exact original addresses for every occurrence, including the first item");
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!
            .GetMethod("CaptureWithOccurrences", BindingFlags.Static | BindingFlags.NonPublic)!;
        var captured = (PanelSnapshot)capture.Invoke(null, new object?[] { trackedTree, null, PanelUnitConversion.None, "mm", groups })!;
        Check(captured.Rows[1].SetOccurrences == groups[1] && captured.Rows[1].NumericValue == 1,
            "Captured counts remain aligned with the retained typed rows");
        arguments = new object?[] { simple, 1, true, false, false, true, null };
        tracked.Invoke(null, arguments);
        groups = (System.Collections.Generic.IReadOnlyList<PanelSetOccurrences>)arguments[6]!;
        Check(groups.Select(group => group.Count).SequenceEqual(new[] { 2, 2, 1 }) &&
            groups[1].Locations[0].Path == "{7;0}" && groups[1].Locations[1].Path == "{7;1}",
            "Simplify and Trim preserve original locations when output paths merge");
        arguments = new object?[] { source, 0, false, false, false, true, null };
        tracked.Invoke(null, arguments);
        groups = (System.Collections.Generic.IReadOnlyList<PanelSetOccurrences>)arguments[6]!;
        Check(groups[0].Count == 2 && groups[0].BranchCount == 1 && groups[10].Count == 2 &&
            groups[11].Count == 1 && groups[12].Count == 1 && groups[13].Count == 1 && groups[14].Count == 1 && groups[15].Count == 2,
            "Per-branch counts follow exact equality, null grouping and invalid/custom pass-through semantics");
        var mixedInputs = source.Branches.SelectMany((branch, b) => branch.Select((item, i) =>
            (Address: (source.Paths[b].ToString(), i), Item: item))).ToDictionary(entry => entry.Address, entry => entry.Item);
        arguments = new object?[] { source, 1, true, true, true, true, null };
        var mixedSet = ((GH_Structure<IGH_Goo>)tracked.Invoke(null, arguments)!).AllData(skipNulls: false).ToArray();
        groups = (System.Collections.Generic.IReadOnlyList<PanelSetOccurrences>)arguments[6]!;
        var mixedLocations = groups.SelectMany(group => group.Locations).Select(address => (address.Path, address.ItemIndex)).ToArray();
        Check(mixedLocations.Length == source.DataCount && mixedLocations.ToHashSet().SetEquals(mixedInputs.Keys) &&
            groups.Count == mixedSet.Length && groups.Select((group, i) =>
                ReferenceEquals(mixedSet[i], mixedInputs[(group.Locations[0].Path, group.Locations[0].ItemIndex)])).All(valid => valid) &&
            mixedSet.Count(item => item == null) == 1 && mixedSet.Count(item => ReferenceEquals(item, unknown)) == 2 &&
            mixedSet.Count(item => ReferenceEquals(item, invalid)) == 2,
            "Combined tools preserve exact source identities and every occurrence for mixed/null/invalid/custom values");
        arguments = new object?[] { source, 0, false, false, false, false, null };
        Check(ReferenceEquals(tracked.Invoke(null, arguments), source) && arguments[6] == null,
            "Disabled Create set retains passthrough and does not allocate occurrence metadata");

        var mixed = new GH_Structure<IGH_Goo>();
        var mixedPaths = new[] { new GH_Path(2, 3), new GH_Path(2, 3, 0), new GH_Path(2, 4, 0), new GH_Path(9), new GH_Path(9, 0) };
        for (int b = 0; b < mixedPaths.Length; b++)
        {
            mixed.EnsurePath(mixedPaths[b]);
            mixed.AppendRange(Enumerable.Range(0, b == 2 ? 0 : 11).Select(i => (IGH_Goo)new GH_Integer(i % 3)), mixedPaths[b]);
        }
        var originalByAddress = mixed.Branches.SelectMany((branch, b) => branch.Select((item, i) =>
            (Address: (mixed.Paths[b].ToString(), i), Item: item))).ToDictionary(entry => entry.Address, entry => entry.Item);
        foreach (int trim in new[] { 0, 1, 2, 5 })
        for (int flags = 0; flags < 8; flags++)
        {
            arguments = new object?[] { mixed, trim, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, true, null };
            var transformed = (GH_Structure<IGH_Goo>)tracked.Invoke(null, arguments)!;
            groups = (System.Collections.Generic.IReadOnlyList<PanelSetOccurrences>)arguments[6]!;
            var retained = transformed.AllData(true).ToArray();
            var allLocations = groups.SelectMany(group => group.Locations).Select(address => (address.Path, address.ItemIndex)).ToArray();
            Check(groups.Count == retained.Length && allLocations.Length == mixed.DataCount &&
                allLocations.Distinct().Count() == mixed.DataCount && groups.Select((group, i) =>
                    ReferenceEquals(retained[i], originalByAddress[(group.Locations[0].Path, group.Locations[0].ItemIndex)]) &&
                    group.Locations.All(address => ((GH_Integer)originalByAddress[(address.Path, address.ItemIndex)]).Value ==
                        ((GH_Integer)retained[i]).Value)).All(valid => valid),
                $"Mixed-depth paths and empty branches retain every input address exactly once (trim {trim}, flags {flags})");
        }
    }

    internal static async Task Run(Assembly assembly)
    {
        VerifyTree(assembly);
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Number(2), new GH_Number(1), new GH_Integer(2),
            new GH_String("alpha"), new GH_String("alpha") }, new GH_Path(0));
        source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Number(2), new GH_Number(2), new GH_Number(3) }, new GH_Path(1));
        source.PersistentData.EnsurePath(new GH_Path(2));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        using var canvas = new GH_Canvas { Size = new Size(650, 500) };
        component.Attributes.Pivot = new PointF(50, 50);
        try
        {
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            component.Attributes.PerformLayout();
            document.UndoServer.Clear();
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 5,
                "One canvas click enables Create set and removes duplicates on the output wire");
            Check(source.PersistentData.DataCount == 8, "Canvas Create set preserves upstream data");
            Paint(component, canvas, "create-set");
            document.UndoServer.PerformUndo();
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 8,
                "Undo restores the original output after Create set");
            document.UndoServer.PerformRedo();
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 5,
                "Redo reapplies Create set");

            var archive = new GH_LooseChunk("Panel");
            Check(component.Write(archive), "Create set fixture saves");
            var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            try
            {
                Check(restored.Read(archive) && PanelTestAdapter.TreeFlag(restored, "CreateSet"), "Create set survives save/load");
                archive.RemoveItem("PanelCreateSet");
                Check(restored.Read(archive) && !PanelTestAdapter.TreeFlag(restored, "CreateSet"), "Older definitions default Create set off");
            }
            finally { restored.RemovedFromDocument(document); }

            Query(session, new PanelQueryOptions { BranchPath = "{1}", BranchPrefix = true, SortMode = PanelSortMode.ValueDescending });
            await Wait(session);
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(Property<PanelQueryOptions>(session, "Options").BranchPath == "{1}" &&
                Property<PanelQueryOptions>(session, "Options").BranchPrefix && component.Params.Output[0].VolatileData.DataCount == 2,
                "Create set preserves branch filters and sorting while filtering transformed output");
            Click(component, canvas, CanvasControl.Flatten);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 4 &&
                component.Params.Output[0].VolatileData.PathCount == 1, "Flatten retains Create set and produces one global set");
            Click(component, canvas, CanvasControl.Reverse);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") &&
                component.Params.Output[0].VolatileData.get_Branch(0)[0] is GH_Number first && first.Value == 3,
                "Reverse retains Create set and changes the retained occurrence order");

            PanelTestAdapter.Resize(component, 300, 220);
            Click(component, canvas, CanvasControl.Display);
            component.Attributes.PerformLayout();
            var set = PanelTestAdapter.Bounds(component, CanvasControl.CreateSet);
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(set) &&
                set.Left > PanelTestAdapter.Bounds(component, CanvasControl.Reverse).Right &&
                set.Right < PanelTestAdapter.Bounds(component, CanvasControl.Units).Left &&
                !set.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Resize)),
                "Create set fits beside Reverse without overlapping Units or Resize in minimum Half layout");
            Paint(component, canvas, "create-set-half");
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet"), "Create set remains clickable in minimum Half layout");
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Click(component, canvas, CanvasControl.Reset);
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 8,
                "Reset clears Create set and restores all input values");
            document.UndoServer.PerformUndo();
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet"), "Undo Reset restores Create set");

            var curves = new GH_Structure<IGH_Goo>();
            curves.Append(new GH_Curve(new LineCurve(Point3d.Origin, new Point3d(1, 0, 0))), new GH_Path(0));
            curves.Append(new GH_Curve(new LineCurve(Point3d.Origin, new Point3d(2, 0, 0))), new GH_Path(0));
            var apply = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
            var retained = (GH_Structure<IGH_Goo>)apply.Invoke(null, new object[] { curves, 0, false, false, false, true })!;
            Check(retained.DataCount == 2 && retained.Branches[0].SequenceEqual(curves.Branches[0]),
                "Different curves with matching display summaries are retained unchanged");

            var primitives = new IGH_Goo[] { new GH_ComplexNumber(new Complex(1, 2)), new GH_Interval(new Interval(0, 1)),
                new GH_Line(new Line(Point3d.Origin, new Point3d(1, 0, 0))), new GH_Plane(Plane.WorldXY),
                new GH_Rectangle(new Rectangle3d(Plane.WorldXY, 2, 3)), new GH_Box(new Box(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)))),
                new GH_Arc(new Arc(new Circle(Plane.WorldXY, 2), Math.PI)), new GH_Circle(new Circle(Plane.WorldXY, 2)),
                new GH_Transform(Transform.Identity), new GH_Guid(Guid.NewGuid()), new GH_Time(new DateTime(2026, 10, 20)) };
            var primitiveTree = new GH_Structure<IGH_Goo>();
            primitiveTree.AppendRange(primitives.SelectMany(value => new[] { value, value.Duplicate() }), new GH_Path(0));
            var primitiveSet = (GH_Structure<IGH_Goo>)apply.Invoke(null, new object[] { primitiveTree, 0, false, false, false, true })!;
            Check(primitiveSet.Branches[0].SequenceEqual(primitives), "Standard primitive values deduplicate while retaining the first typed item");
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static void Click(GH_Component component, GH_Canvas canvas, CanvasControl control)
    {
        var bounds = PanelTestAdapter.Bounds(component, control);
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var mouse = new GH_CanvasMouseEvent(System.Drawing.Point.Empty, point, Forms.MouseButtons.Left, 1, 0);
        component.Attributes.RespondToMouseDown(canvas, mouse);
        component.Attributes.RespondToMouseUp(canvas, mouse);
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string name)
    {
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(650, 500);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
        bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
    }

    private static void Query(object session, PanelQueryOptions options) =>
        session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static async Task Wait(object session)
    {
        var clock = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Create set query timed out");
            await Task.Delay(20);
        }
        Check(Property<string?>(session, "Error") == null, "Create set query completed successfully");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
