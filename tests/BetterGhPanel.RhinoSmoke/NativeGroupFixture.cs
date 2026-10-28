using System;
using System.Linq;
using System.Reflection;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

// These comparisons run a real document solution with both panels connected to
// the same source. The stock panel's collected GH_String values are its content;
// never use the original group's name as the oracle after host collection.
internal static class NativeGroupFixture
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal sealed class VolatileSource : GH_Param<IGH_Goo>
    {
        private readonly IGH_Goo[] _items;
        internal VolatileSource(IGH_Goo[] items) : base("Fixture volatile source", "V", "Delivers original values", "Test", "Test", GH_ParamAccess.tree) => _items = items;
        public override Guid ComponentGuid => new Guid("5231cd6c-3fb5-4523-a728-2fb17f5456ef");
        protected override void CollectVolatileData_Custom() => AddVolatileDataList(new GH_Path(0), _items);
    }

    internal static void VerifyCycles(Assembly assembly)
    {
        // Do not feed cycles to the stock panel, a persistent param, or the solver:
        // native graph traversal/duplication may recurse before our code is called.
        var first = new GH_GeometryGroup { Name = "Cycle A" };
        var second = new GH_GeometryGroup { Name = "Cycle B" };
        first.Objects.Add(second); second.Objects.Add(first);
        var wrapper = new GH_GeometricGooWrapper();
        typeof(GH_GeometricGooWrapper).GetField("m_goo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wrapper, wrapper);
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(new IGH_Goo[] { first, second, wrapper }, new GH_Path(0));
        var args = new object?[] { tree, null, PanelUnitConversion.None, "mm" };
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture", Static)!;
        var snapshot = (PanelSnapshot)capture.Invoke(null, args)!;
        Check(snapshot.Rows.Count == 3 && ((IGH_Goo?[])args[1]!).SequenceEqual(tree.Branches[0]), "Cycle capture retains the three supplied source identities");
        Check(snapshot.Rows.All(row => row.Validity == PanelValidity.Unverified) && snapshot.InspectionVisitCount < 100,
            "Both cyclic roots and a self-wrapped value terminate as unverified within the inspection budget");
        Check(snapshot.Rows[0].DisplayValue == "Group (Cycle A) with 1 object" && snapshot.Rows[1].DisplayValue == "Group (Cycle B) with 1 object",
            "Cyclic groups retain their direct names and counts without recursive host formatting");
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "Cycle A" }).RowPositions.SequenceEqual(new[] { 0 }),
            "Plugin-controlled search remains usable after bounded cycle inspection");
    }

    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
