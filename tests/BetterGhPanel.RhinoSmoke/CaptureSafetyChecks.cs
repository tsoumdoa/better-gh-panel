using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

// Plugin-controlled capture tests. Host delivery and output postprocessing are
// separate phases and must not be counted as proof that this fixture passed.
internal static class CaptureSafetyChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int AssertionCount { get; private set; }
    internal static void VerifyManaged(Assembly assembly)
    {
        AssertionCount = 0;
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        PanelSnapshot Capture(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] items)
        {
            var args = new object?[] { tree, null, PanelUnitConversion.None, "mm" };
            var snapshot = (PanelSnapshot)capture.Invoke(null, args)!;
            items = (IGH_Goo?[])args[1]!;
            return snapshot;
        }

        var a = Group("Facade A", 2);
        var b = Group("Facade B", 2);
        var c = Group("Facade A", 1);
        var hostileCurve = new HostileCurve();
        var hostileNumber = new HostileNumber();
        var unknown = Group("Unknown", 0); unknown.Objects.Add(hostileCurve);
        var mixed = Group("Mixed", 0); mixed.Objects.Add(hostileCurve); mixed.Objects.Add(new GH_Curve());
        var cycleA = Group("A", 0); var cycleB = Group("B", 0);
        cycleA.Objects.Add(cycleB); cycleA.Objects.Add(new GH_Curve()); cycleB.Objects.Add(cycleA);
        var self = new GH_GeometricGooWrapper();
        typeof(GH_GeometricGooWrapper).GetField("m_goo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(self, self);
        var objectSelf = new GH_ObjectWrapper(); objectSelf.Value = objectSelf;
        var wrapped = new GH_ObjectWrapper(a);
        var geometric = new GH_GeometricGooWrapper(a);
        var nested = new GH_GeometricGooWrapper(geometric);
        var sameSummaryUnknown = Group("Facade A", 0); sameSummaryUnknown.Objects.Add(hostileCurve); sameSummaryUnknown.Objects.Add(null!);
        var sameSummaryInvalid = Group("Facade A", 0); sameSummaryInvalid.Objects.Add(new GH_Curve()); sameSummaryInvalid.Objects.Add(null!);
        var unsafeWrappedGroup = new GH_GeometricGooWrapper(unknown);
        var emptyGroup = Group("", 0); var whitespaceGroup = Group("  ", 0);
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { a, b, c, wrapped, geometric, nested, unknown, mixed, cycleA, cycleB,
            self, objectSelf, new GH_ObjectWrapper(hostileNumber), new GH_GeometricGooWrapper(hostileCurve), hostileNumber,
            new GH_String(""), new GH_String("<empty>"), new GH_Colour(Color.FromArgb(128, 10, 20, 30)),
            new GH_Interval(new Interval(-2.5, 5.75)), new GH_Interval2D(new UVInterval(new Interval(1, 2), new Interval(3, 4))),
            new GH_Guid(Guid.Parse("7d55d72e-8547-43c4-a231-cb78e82900cc")), new GH_Time(new DateTime(2026, 10, 27, 15, 16, 17)),
            sameSummaryUnknown, sameSummaryInvalid, unsafeWrappedGroup, emptyGroup, whitespaceGroup }, new GH_Path(0));
        HostileNumber.Calls = HostileCurve.Calls = 0;
        var snapshot = Capture(tree, out var items);
        Check(items.SequenceEqual(tree.Branches[0]) && HostileNumber.Calls == 0 && HostileCurve.Calls == 0,
            "Capture preserves source identities without invoking hostile getters, metadata, validity or ScriptVariable");
        Check(snapshot.Rows[0].DisplayValue == "Group (Facade A) with 2 objects" &&
            snapshot.Rows[1].DisplayValue == "Group (Facade B) with 2 objects" &&
            snapshot.Rows[2].DisplayValue == "Group (Facade A) with 1 object" &&
            snapshot.Rows[3].DisplayValue == snapshot.Rows[0].DisplayValue,
            "Names and direct counts, including null slots, reproduce group summaries without duplication");
        Check(snapshot.Rows[0].TypeName == "Group" && snapshot.Rows[3].TypeName == "Goo" &&
            snapshot.Rows[4].TypeName == "{Group}" && snapshot.Rows[5].TypeName == "{{Group}}" &&
            snapshot.Rows[3].TypeIdentityAvailable && snapshot.Rows[4].TypeIdentityAvailable && snapshot.Rows[5].TypeIdentityAvailable,
            "Direct, object, geometric and nested wrapper filter identities are reconstructed without metadata delegation");
        Check(snapshot.Rows[6].Validity == PanelValidity.Unverified && snapshot.Rows[7].Validity == PanelValidity.VerifiedInvalid &&
            snapshot.Rows[8].Validity == PanelValidity.VerifiedInvalid && snapshot.Rows[9].Validity == PanelValidity.VerifiedInvalid &&
            snapshot.Rows[10].Validity == PanelValidity.Unverified && snapshot.Rows[11].Validity == PanelValidity.Unverified &&
            snapshot.Rows[12].TypeName == "Goo" && snapshot.Rows[12].TypeIdentityAvailable &&
            snapshot.Rows[13].Validity == PanelValidity.Unverified && !snapshot.Rows[13].TypeIdentityAvailable &&
            snapshot.Rows[14].Validity == PanelValidity.Unverified && !snapshot.Rows[14].TypeIdentityAvailable &&
            snapshot.Rows[24].TypeName == "{Group}" && snapshot.Rows[24].TypeIdentityAvailable &&
            snapshot.Rows[24].Validity == PanelValidity.Unverified,
            "Cycles and unknown values are unverified; known invalids take precedence regardless of root order");
        Check(snapshot.Rows[15].DisplayValue == "" && snapshot.Rows[15].StringValue == "" &&
            snapshot.Rows[17].DisplayValue == "10,20,30 (128)" && snapshot.Rows[18].DisplayValue == "-2.5 To 5.75" &&
            snapshot.Rows[19].DisplayValue == "u:{1 To 2} v:{3 To 4}" &&
            snapshot.Rows[20].DisplayValue == "7d55d72e-8547-43c4-a231-cb78e82900cc" &&
            snapshot.Rows[21].DisplayValue.Contains("2026-10-27") && snapshot.Rows[21].DisplayValue.Contains("15:16:17"),
            "Exact text, colour, domain, 2D domain, GUID and time capture semantic values");
        Check(snapshot.Rows[25].DisplayValue == "Empty Group" && snapshot.Rows[26].DisplayValue == "Empty Group (  )",
            "Empty and whitespace group names retain the stock distinction");
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^$" }).RowPositions.SequenceEqual(new[] { 15 }) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "<empty>" }).RowPositions.SequenceEqual(new[] { 16 }),
            "Empty-string search uses semantic content rather than its visual marker");
        var groupSet = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Group", CreateSet = true });
        Check(groupSet.RowPositions.Contains(0) && groupSet.RowPositions.Contains(1) && groupSet.RowPositions.Contains(2),
            "Group set identity retains different names and different counts");
        Check(!groupSet.RowPositions.Contains(22) && groupSet.RowPositions.Contains(23) &&
            groupSet.SetOccurrencesAt(0)!.SourceStatuses.Unverified == 1 &&
            groupSet.DisplaySet!.Values[23].Text.StartsWith("Invalid · ", StringComparison.Ordinal),
            "Matching unverified summaries group with valid rows; the verified invalid prefix remains part of the set key");
        var typeFilter = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "{Group}", Search = "Facade A" });
        Check(typeFilter.RowPositions.SequenceEqual(new[] { 4 }), "Saved wrapped group filter retains exact membership");
        VerifyOutputs(assembly, tree, snapshot, typeFilter, geometric);
        VerifyStockIdentities(assembly, Capture);

        var unitTree = new GH_Structure<IGH_Goo>();
        var unset = new GH_Number(Rhino.RhinoMath.UnsetValue);
        unitTree.AppendRange(new IGH_Goo[] { hostileNumber, unset, new GH_Number(2000) }, new GH_Path(0));
        HostileNumber.Calls = 0;
        var convert = assembly.GetType("BetterGhPanel.Runtime.PanelUnitOperations", true)!.GetMethod("Apply", Static)!;
        var converted = (GH_Structure<IGH_Goo>)convert.Invoke(null, new object[] { unitTree, PanelUnitConversion.MillimetersToMeters })!;
        Check(HostileNumber.Calls == 0 && ReferenceEquals(converted.Branches[0][0], hostileNumber) &&
            ReferenceEquals(converted.Branches[0][1], unset) && ((GH_Number)converted.Branches[0][2]).Value == 2,
            "Conversion never reads subclass values and preserves exact finite invalid unset values");
        var describe = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!.GetMethod("Describe", Static)!;
        HostileCurve.Calls = 0;
        Check(describe.Invoke(null, new object[] { hostileCurve, "mm" }) == null && HostileCurve.Calls == 0,
            "Geometry descriptions never read subclass Value or delegated metadata");
        VerifyBudgets(Capture);
        Console.WriteLine("PASS: Plugin-controlled capture identities, validity, sets, outputs and budgets.");
    }

    private delegate PanelSnapshot CaptureTree(GH_Structure<IGH_Goo> tree, out IGH_Goo?[] items);
    private static void VerifyBudgets(CaptureTree capture)
    {
        var deep = Group("deep", 0); var next = deep;
        for (int i = 0; i < 100; i++) { var child = Group("nested", 0); next.Objects.Add(child); next = child; }
        var shared = Group(new string('x', 4096), 0);
        for (int i = 0; i < 120; i++) shared.Objects.Add(null!);
        var tree = new GH_Structure<IGH_Goo>();
        tree.Append(deep, new GH_Path(0));
        for (int i = 0; i < 1100; i++) tree.Append(shared, new GH_Path(0));
        tree.Append(new GH_Number(3), new GH_Path(0));
        var snapshot = capture(tree, out _);
        Check(snapshot.Rows[0].Validity == PanelValidity.Unverified && snapshot.InspectionVisitCount <= 100000 &&
            snapshot.Rows[1100].Validity == PanelValidity.Unverified && snapshot.Rows[1101].Validity == PanelValidity.VerifiedValid &&
            ReferenceEquals(snapshot.Rows[1].DisplayValue, snapshot.Rows[100].DisplayValue),
            "Depth and aggregate budgets bound repeated group work, preserve direct summaries and leave ordinary rows usable");
        var distinct = new GH_Structure<IGH_Goo>();
        for (int i = 0; i < 1100; i++) distinct.Append(Group("distinct " + i, 120), new GH_Path(0));
        var distinctSnapshot = capture(distinct, out _);
        Check(distinctSnapshot.InspectionVisitCount <= 100000 &&
            distinctSnapshot.Rows[1099].Validity == PanelValidity.Unverified &&
            distinctSnapshot.Rows[1099].DisplayValue == "Group (distinct 1099) with 120 objects",
            "Distinct groups also share the aggregate work limit and retain their direct summaries after exhaustion");
        var longNames = new GH_Structure<IGH_Goo>();
        for (int i = 0; i < 1100; i++) longNames.Append(Group(i + new string('y', 1024), 0), new GH_Path(0));
        var longSnapshot = capture(longNames, out _);
        string last = longSnapshot.Rows[1099].DisplayValue;
        Check(longSnapshot.RetainedGroupSummaryBytes <= 1024 * 1024 && longSnapshot.CachedGroupSummaryCount <= 16384 &&
            last.StartsWith("Empty Group (1099", StringComparison.Ordinal) &&
            ReferenceEquals(last, longSnapshot.Rows[1099].DisplayValue),
            "Long distinct names preserve full summaries with only weak reuse outside the retained budget");
    }

    private static void VerifyStockIdentities(Assembly assembly, CaptureTree capture)
    {
        var method = assembly.GetType("BetterGhPanel.Runtime.SafeGooInspection", true)!.GetMethod("TrustedTypeName", Static)!;
        var gh = typeof(IGH_Goo).Assembly;
        int audited = 0;
        foreach (var type in gh.GetTypes().Where(type => type.Namespace == "Grasshopper.Kernel.Types" &&
            !type.IsAbstract && !type.ContainsGenericParameters && typeof(IGH_Goo).IsAssignableFrom(type)))
        {
            if (type == typeof(GH_ObjectWrapper) || type == typeof(GH_GeometricGooWrapper)) continue;
            var getter = type.GetProperty("TypeName")!.GetMethod!;
            byte[]? il = getter.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length != 6 || il[0] != 0x72 || il[5] != 0x2a) continue;
            string stockName = getter.Module.ResolveString(BitConverter.ToInt32(il, 1));
            Check((string?)method.Invoke(null, new object[] { type }) == stockName,
                "Exact stock identity matches the constant SDK getter: " + type.FullName);
            audited++;
        }
        Check(audited >= 46, "The complete stock kernel goo identity table is audited against assembly metadata");
        int modelConstants = 0, modelAttributes = 0;
        foreach (var type in gh.GetTypes().Where(type => type.Namespace != null &&
            type.Namespace.StartsWith("Grasshopper.Rhinoceros", StringComparison.Ordinal) && !type.IsAbstract &&
            !type.ContainsGenericParameters && typeof(IGH_Goo).IsAssignableFrom(type)))
        {
            var map = type.GetInterfaceMap(typeof(IGH_Goo));
            int index = Array.FindIndex(map.InterfaceMethods, member => member.Name == "get_TypeName");
            if (index < 0) continue;
            var getter = map.TargetMethods[index];
            if (getter.DeclaringType?.FullName == "Grasshopper.Rhinoceros.ModelData" ||
                getter.DeclaringType?.FullName == "Grasshopper.Rhinoceros.ModelValue")
            {
                var attribute = type.GetCustomAttributesData().SingleOrDefault(item => item.AttributeType.FullName == "Grasshopper.Kernel.Types.TypeNameAttribute");
                if (attribute != null && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string attributeName)
                {
                    Check((string?)method.Invoke(null, new object[] { type }) == attributeName,
                        "Exact model identity matches installed SDK metadata: " + type.FullName);
                    modelAttributes++;
                }
            }
            byte[]? il = getter.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length != 6 || il[0] != 0x72 || il[5] != 0x2a) continue;
            string stockName = getter.Module.ResolveString(BitConverter.ToInt32(il, 1));
            Check((string?)method.Invoke(null, new object[] { type }) == stockName,
                "Exact model identity matches the constant SDK getter: " + type.FullName);
            modelConstants++;
        }
        Check(modelConstants >= 7 && modelAttributes >= 20,
            "Stock model goo constant and attribute-based identities are audited");
        // The model metadata changes across Rhino 8 builds. Read its literal
        // attribute from the installed assembly without invoking any goo getter.
        foreach (string fullName in new[] { "Grasshopper.Rhinoceros.Model.ModelLayer",
            "Grasshopper.Rhinoceros.Display.ModelPageViewport", "Grasshopper.Rhinoceros.ModelMeshingParameters" })
        {
            var type = gh.GetType(fullName, true)!;
            var attribute = type.GetCustomAttributesData().Single(item => item.AttributeType.FullName == "Grasshopper.Kernel.Types.TypeNameAttribute");
            Check((string?)method.Invoke(null, new object[] { type }) == (string?)attribute.ConstructorArguments[0].Value,
                "Stock model identity follows the installed SDK attribute: " + fullName);
        }

        // Uninitialized exact stock instances exercise identity even when their
        // native value is absent; the expanded identity table must not call validity.
        var types = new[] { typeof(GH_Transform), typeof(GH_MeshFace), typeof(GH_ComplexNumber), typeof(GH_SubD), typeof(GH_Extrusion) };
        var values = types.Select(type => (IGH_Goo)RuntimeHelpers.GetUninitializedObject(type)).ToArray();
        var subd = (GH_SubD)values[3]; var extrusion = (GH_Extrusion)values[4];
        var wrappedSubd = new GH_GeometricGooWrapper(subd);
        var wrappedExtrusion = new GH_GeometricGooWrapper(extrusion);
        var objects = values.Concat(new IGH_Goo[] { wrappedSubd, wrappedExtrusion,
            new GH_GeometricGooWrapper(wrappedSubd), new GH_ObjectWrapper(values[0]) }).ToArray();
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(objects, new GH_Path(0));
        var snapshot = capture(tree, out _);
        string[] names = { "Transform", "Meshface", "Complex", "SubD", "Extrusion", "{SubD}", "{Extrusion}", "{{SubD}}", "Goo" };
        Check(snapshot.Rows.Select(row => row.TypeName).SequenceEqual(names) &&
            snapshot.Rows.All(row => row.TypeIdentityAvailable && row.Validity == PanelValidity.Unverified),
            "Missing stock names and exact wrapper layers remain known even for invalid or unsupported values");
        for (int i = 0; i < names.Length; i++)
        {
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = names[i] });
            Check(result.RowPositions.SequenceEqual(new[] { i }), "Stock type filter membership: " + names[i]);
            VerifyIdentityOutputs(assembly, tree, snapshot, result, objects[i], i);
            var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = names[i], CreateSet = true });
            Check(set.RowPositions.SequenceEqual(new[] { i }) && set.SetOccurrencesAt(i)!.Locations.Single().ItemIndex == i,
                "Stock type filter and set retain the original occurrence: " + names[i]);
            VerifyIdentityOutputs(assembly, tree, snapshot, set, objects[i], i);
        }
    }

    private static void VerifyIdentityOutputs(Assembly assembly, GH_Structure<IGH_Goo> tree, PanelSnapshot snapshot,
        PanelQueryResult result, IGH_Goo expected, int position)
    {
        var treeOps = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var outputOps = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        var filtered = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { tree, result })!;
        var selected = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Select", Static)!.Invoke(null, new object[] { tree, result.RowPositions })!;
        var addresses = snapshot.Rows.Select((_, i) => snapshot.SourceAddressAt(i)).ToArray();
        var mask = (GH_Structure<GH_Boolean>)outputOps.GetMethod("Mask", Static)!.Invoke(null, new object[] { tree, addresses, result.RowPositions })!;
        var paired = new GH_Structure<IGH_Goo>();
        for (int i = 0; i < tree.DataCount; i++) paired.Append(new GH_String("F" + i), new GH_Path(0));
        var pairs = (GH_Structure<IGH_Goo>)outputOps.GetMethod("Pair", Static)!.Invoke(null, new object?[] { tree, paired, tree, addresses, null })!;
        var filteredPairs = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { pairs, result })!;
        Check(ReferenceEquals(filtered.Branches[0][0], expected) && ReferenceEquals(selected.Branches[0][0], expected) &&
            mask.Branches[0].Count(item => item.Value) == 1 && mask.Branches[0][position].Value &&
            ((GH_String)filteredPairs.Branches[0][0]).Value == "F" + position,
            "Stock identity keeps D/S/B/F membership at source position " + position);
    }

    private static void VerifyOutputs(Assembly assembly, GH_Structure<IGH_Goo> tree, PanelSnapshot snapshot,
        PanelQueryResult filter, IGH_Goo expected)
    {
        var treeOps = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var outputOps = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        var filtered = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { tree, filter })!;
        var selected = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Select", Static)!.Invoke(null, new object[] { tree, filter.RowPositions })!;
        var addresses = snapshot.Rows.Select((_, i) => snapshot.SourceAddressAt(i)).ToArray();
        var mask = (GH_Structure<GH_Boolean>)outputOps.GetMethod("Mask", Static)!.Invoke(null, new object[] { tree, addresses, filter.RowPositions })!;
        var paired = new GH_Structure<IGH_Goo>();
        for (int i = 0; i < tree.DataCount; i++) paired.Append(new GH_String("F" + i), new GH_Path(0));
        var args = new object?[] { tree, paired, tree, addresses, null };
        var pairs = (GH_Structure<IGH_Goo>)outputOps.GetMethod("Pair", Static)!.Invoke(null, args)!;
        var filteredPairs = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { pairs, filter })!;
        Check(ReferenceEquals(filtered.Branches[0][0], expected) && ReferenceEquals(selected.Branches[0][0], expected) &&
            mask.Branches[0].Count(v => v.Value) == 1 && mask.Branches[0][4].Value &&
            ((GH_String)filteredPairs.Branches[0][0]).Value == "F4",
            "Wrapped type filters preserve typed D/S and aligned B/F membership");
        var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "{Group}", Search = "Facade A", CreateSet = true });
        var output = (GH_Structure<IGH_Goo>)outputOps.GetMethod("DisplaySet", Static)!
            .Invoke(null, new object[] { tree, set, set.RowPositions, set.BranchPaths })!;
        Check(((GH_String)output.Branches[0][0]).Value == "Group (Facade A) with 2 objects",
            "Create set emits the captured group summary while preserving representative membership");
    }

    internal static void VerifyNativeUserData(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        using var poly = new PolyCurve();
        Check(poly.Append(new Line(Point3d.Origin, new Point3d(1, 0, 0))) &&
            poly.Append(new Line(new Point3d(1, 0, 0), new Point3d(2, 1, 0))), "PolyCurve fixture has nested segments");
        var segment = poly.SegmentCurve(0);
        Check(segment.UserData.Add(new CaptureBenignUserData()) && poly.UserData.Count == 0 && poly.SegmentCurve(0).UserData.Count == 1,
            "Nested segment UserData is present on the source before capture, without outer UserData");
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        Check(brep.UserData.Count == 0 && brep.Curves3D.Count > 0 && brep.Surfaces.Count > 0 &&
            brep.Curves3D[0].UserData.Add(new CaptureCountingUserData()) && brep.Surfaces[0].UserData.Add(new CaptureCountingUserData()) &&
            brep.Surfaces[0].UserData.Add(new CaptureNonSerializableUserData()) &&
            brep.Curves3D[0].UserData.Count == 1 && brep.Surfaces[0].UserData.Count == 2,
            "Brep fixture has hostile and non-serializable embedded UserData, without outer UserData");
        var tree = new GH_Structure<IGH_Goo>();
        var curveGoo = new GH_Curve { Value = poly };
        var brepGoo = new GH_Brep { Value = brep };
        tree.AppendRange(new IGH_Goo[] { curveGoo, brepGoo }, new GH_Path(0));
        Check(ReferenceEquals(curveGoo.Value, poly) && ReferenceEquals(brepGoo.Value, brep),
            "The capture fixture passes the instrumented geometry itself to the plugin");
        CaptureCountingUserData.Callbacks = 0;
        CaptureBenignUserData.Callbacks = 0;
        CaptureNonSerializableUserData.Callbacks = 0;
        var snapshot = (PanelSnapshot)capture.Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
        Check(CaptureCountingUserData.Callbacks == 0 && CaptureBenignUserData.Callbacks == 0 && CaptureNonSerializableUserData.Callbacks == 0 &&
            poly.UserData.Count == 0 && poly.SegmentCurve(0).UserData.Count == 1 && brep.UserData.Count == 0 &&
            brep.Curves3D[0].UserData.Count == 1 && brep.Surfaces[0].UserData.Count == 2 &&
            snapshot.Rows.All(row => row.GeometryInfo?.CanMeasure != true),
            "Capture leaves nested UserData untouched and does not claim a safe automatic copy path");
        Console.WriteLine("PASS: Plugin-controlled capture with nested segment and Brep UserData.");
    }

    internal static void VerifyNativeCapabilities(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        PanelSnapshot Capture(params IGH_Goo[] values)
        {
            var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(values, new GH_Path(0));
            return (PanelSnapshot)capture.Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
        }
        var number = Capture(new GH_Number(Rhino.RhinoMath.UnsetValue), new GH_Number(double.NaN));
        Check(number.SupportsSearchMode(PanelSearchMode.Value) && number.Rows.All(row => row.Validity == PanelValidity.VerifiedInvalid),
            "Exact invalid finite-unset and nonfinite numbers still offer numeric search");
        var unsetArc = new GH_Arc(Arc.Unset);
        var curves = Capture(new GH_Line(), unsetArc, new GH_Circle(), new GH_Curve());
        Check(curves.SupportsSearchMode(PanelSearchMode.Length) &&
            curves.Rows.Where(row => row.TypeName != "Arc").All(row => row.Validity == PanelValidity.VerifiedInvalid) &&
            curves.Rows.Single(row => row.TypeName == "Arc").IsValid == unsetArc.IsValid,
            "Invalid line, circle and null curve retain length mode, and arc validity follows Grasshopper");
        var area = Capture(new GH_Rectangle(), new GH_Surface());
        var volume = Capture(new GH_Box());
        var nullBrep = Capture(new GH_Brep());
        Check(area.SupportsSearchMode(PanelSearchMode.Area) && volume.SupportsSearchMode(PanelSearchMode.Volume) &&
            !nullBrep.SupportsSearchMode(PanelSearchMode.Area) && !nullBrep.SupportsSearchMode(PanelSearchMode.Volume),
            "Invalid rectangle/surface and box retain unambiguous modes; invalid Brep offers neither");
        using var plane = new PlaneSurface(Plane.WorldXY, new Interval(0, 2), new Interval(0, 3));
        using var open = plane.ToBrep();
        using var solid = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        var arc = new GH_Arc(new Arc(Plane.WorldXY, 2, Math.PI / 2));
        var circle = new GH_Arc(new Arc(Plane.WorldXY, 2, 2 * Math.PI));
        var mixed = Capture(arc, circle, new GH_Brep(open), new GH_Brep(solid));
        Check(mixed.SupportsSearchMode(PanelSearchMode.Length) && mixed.SupportsSearchMode(PanelSearchMode.Area) &&
            mixed.SupportsSearchMode(PanelSearchMode.Volume), "Arc-only/circle-arc and open/solid Brep modes form the input union");
        using var hostile = new HostileLineCurve();
        var holder = new GH_Curve { Value = hostile };
        var group = Group("hostile inner", 0); group.Objects.Add(holder);
        HostileLineCurve.Calls = 0;
        var untrusted = Capture(holder, group, new GH_Point(Point3d.Origin), new GH_Mesh());
        Check(HostileLineCurve.Calls == 0 && untrusted.Rows[0].Validity == PanelValidity.Unverified &&
            untrusted.Rows[1].Validity == PanelValidity.Unverified && !untrusted.SupportsSearchMode(PanelSearchMode.Length),
            "Exact holders with hostile inner subclasses stay typed and unverified without native validity or metric access");
        VerifyNativeSavedTypeFilters(assembly, capture);
        Console.WriteLine("PASS: Plugin-controlled exact input search capability and hostile inner geometry checks.");
    }

    private static void VerifyNativeSavedTypeFilters(Assembly assembly, MethodInfo capture)
    {
        var subd = new GH_SubD(); var extrusion = new GH_Extrusion();
        var values = new IGH_Goo[] { new GH_Transform(Transform.Identity), new GH_MeshFace(0, 1, 2),
            new GH_ComplexNumber(), subd, extrusion, new GH_GeometricGooWrapper(subd), new GH_GeometricGooWrapper(extrusion) };
        string[] names = { "Transform", "Meshface", "Complex", "SubD", "Extrusion", "{SubD}", "{Extrusion}" };
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(values, new GH_Path(0));
        var snapshot = (PanelSnapshot)capture.Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
        var validityTree = new GH_Structure<IGH_Goo>();
        validityTree.AppendRange(new IGH_Goo[] { new GH_Transform(Transform.Identity), new GH_Transform(Transform.Unset),
            new GH_MeshFace(0, 1, 2), new GH_MeshFace() }, new GH_Path(0));
        var validitySnapshot = (PanelSnapshot)capture.Invoke(null, new object?[] { validityTree, null, PanelUnitConversion.None, "mm" })!;
        Check(validitySnapshot.Rows.Select(row => row.TypeName).SequenceEqual(new[] { "Transform", "Transform", "Meshface", "Meshface" }) &&
            validitySnapshot.Rows.All(row => row.TypeIdentityAvailable), "Native stock identity is independent of validity or unset values");

        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes(); document.AddObject(component, false);
        var session = PanelTestAdapter.Session(component);
        void Query(string name) => session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { TypeName = name } });
        PanelQueryOptions Options(object target) => (PanelQueryOptions)target.GetType().GetProperty("Options")!.GetValue(target)!;
        try
        {
            for (int i = 0; i < names.Length; i++)
            {
                Query(names[i]);
                var archive = new GH_Archive();
                var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
                restored.CreateAttributes();
                Check(archive.AppendObject(component, "Panel") && archive.ExtractObject(restored, "Panel"),
                    "Save and load stock type filter: " + names[i]);
                var saved = Options(PanelTestAdapter.Session(restored));
                var savedResult = PanelQueryEngine.Execute(snapshot, saved);
                Check(saved.TypeName == names[i] && savedResult.RowPositions.SequenceEqual(new[] { i }),
                    "Saved stock filter retains its matching row: " + names[i]);
                VerifyIdentityOutputs(assembly, tree, snapshot, savedResult, values[i], i);
                restored.RemovedFromDocument(document);

                document.UndoServer.Clear();
                component.RecordUndoEvent("Change type filter");
                Query("different type");
                document.UndoServer.PerformUndo();
                var undone = Options(session);
                var undoneResult = PanelQueryEngine.Execute(snapshot, undone);
                Check(undone.TypeName == names[i] && undoneResult.RowPositions.SequenceEqual(new[] { i }),
                    "Undo restores stock type filter membership: " + names[i]);
                VerifyIdentityOutputs(assembly, tree, snapshot, undoneResult, values[i], i);
            }
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static GH_GeometryGroup Group(string name, int count)
    {
        var group = new GH_GeometryGroup { Name = name };
        for (int i = 0; i < count; i++) group.Objects.Add(null!);
        return group;
    }
    private static void Check(bool condition, string message)
    {
        SmokeAssertions.Check(condition, message);
        AssertionCount++;
    }

    private sealed class HostileNumber : GH_Number
    {
        internal static int Calls;
        public override double Value { get { Calls++; throw new InvalidOperationException(); } set { Calls++; throw new InvalidOperationException(); } }
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
        public override string IsValidWhyNot { get { Calls++; throw new InvalidOperationException(); } }
        public override string TypeName { get { Calls++; throw new InvalidOperationException(); } }
        public override string TypeDescription { get { Calls++; throw new InvalidOperationException(); } }
        public override object ScriptVariable() { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class HostileCurve : GH_Curve
    {
        internal static int Calls;
        public override Curve Value { get { Calls++; throw new InvalidOperationException(); } set { Calls++; throw new InvalidOperationException(); } }
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
        public override string IsValidWhyNot { get { Calls++; throw new InvalidOperationException(); } }
        public override string TypeName { get { Calls++; throw new InvalidOperationException(); } }
        public override string TypeDescription { get { Calls++; throw new InvalidOperationException(); } }
        public override object ScriptVariable() { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class HostileLineCurve : LineCurve
    {
        internal static int Calls;
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
    }
}

[Guid("a4d14983-2857-4c67-bd5b-2e7797edda87")]
public sealed class CaptureCountingUserData : UserData
{
    internal static int Callbacks;
    public override string Description => "Capture callback fixture";
    public override bool ShouldWrite { get { Callbacks++; return true; } }
    protected override bool Write(BinaryArchiveWriter archive) { Callbacks++; throw new InvalidOperationException("Unexpected UserData write"); }
    protected override bool Read(BinaryArchiveReader archive) { Callbacks++; throw new InvalidOperationException("Unexpected UserData read"); }
    protected override void OnDuplicate(UserData source) { Callbacks++; throw new InvalidOperationException("Unexpected UserData duplication"); }
}

[Guid("abed373c-bb55-47ef-833e-760c05093a0d")]
public sealed class CaptureBenignUserData : UserData
{
    internal static int Callbacks;
    public override string Description => "Benign capture callback fixture";
    public override bool ShouldWrite { get { Callbacks++; return true; } }
    protected override bool Write(BinaryArchiveWriter archive) { Callbacks++; return true; }
    protected override bool Read(BinaryArchiveReader archive) { Callbacks++; return true; }
    protected override void OnDuplicate(UserData source) { Callbacks++; }
}

[Guid("4a220cdd-40b3-4674-a448-0158b9901065")]
public sealed class CaptureNonSerializableUserData : UserData
{
    internal static int Callbacks;
    public override string Description => "Non-serializable capture callback fixture";
    public override bool ShouldWrite { get { Callbacks++; return false; } }
    protected override bool Write(BinaryArchiveWriter archive) { Callbacks++; throw new InvalidOperationException("Unexpected UserData write"); }
    protected override void OnDuplicate(UserData source) { Callbacks++; throw new InvalidOperationException("Unexpected UserData duplication"); }
}
