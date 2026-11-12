using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

// Exercises production serialization, setting mutations, tree tools and outputs without RhinoCore.
internal static class BranchStatsHeadlessChecks
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Any)!.Invoke(target, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name, Any)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name, Any)!.SetValue(target, value);
    private static void Field(object target, string name, object? value) => target.GetType().GetField(name, Any)!.SetValue(target, value);

    internal static int Run(Assembly plugin)
    {
        SmokeAssertions.Begin();
        try
        {
            GH_Component New() => (GH_Component)Activator.CreateInstance(plugin.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            var component = New();
            var picks = PanelStatKinds.Count | PanelStatKinds.Median | PanelStatKinds.Max;
            Set(component, "BranchStatsTree", true); Set(component, "StatKinds", picks);
            var archive = new GH_LooseChunk("Panel");
            SmokeAssertions.Check(component.Write(archive), "branch stats archive writes");
            var restored = New(); Call(restored, "ReadTreeTools", archive);
            SmokeAssertions.Check(Get<bool>(restored, "BranchStatsTree") && Get<PanelStatKinds>(restored, "StatKinds") == picks,
                "Sigma and picks round-trip through the component's production tree-tool reader and Write");

            var legacy = New(); Set(legacy, "CreateSetTree", true);
            var oldArchive = new GH_LooseChunk("OldPanel"); legacy.Write(oldArchive);
            oldArchive.RemoveItem("PanelBranchStats"); oldArchive.RemoveItem("PanelStatKinds");
            restored = New(); Call(restored, "ReadTreeTools", oldArchive);
            SmokeAssertions.Check(Get<bool>(restored, "CreateSetTree") && !Get<bool>(restored, "BranchStatsTree") &&
                Get<PanelStatKinds>(restored, "StatKinds") == PanelBranchStats.Default, "old Create set files retain their behavior and get default stats picks");

            // Detached component: disable query scheduling, while invoking the real setting mutations.
#pragma warning disable SYSLIB0050
            var session = FormatterServices.GetUninitializedObject(plugin.GetType("BetterGhPanel.Runtime.PanelSession", true)!);
#pragma warning restore SYSLIB0050
            Field(session, "_options", new PanelQueryOptions()); Field(session, "_disposed", true);
            Field(component, "_session", session); Field(component, "_completingInClearData", true);
            Set(component, "BranchStatsTree", false); Set(component, "CreateSetTree", true);
            Call(component, "SetBranchStats", true, (PanelStatKinds?)picks);
            SmokeAssertions.Check(Get<bool>(component, "BranchStatsTree") && !Get<bool>(component, "CreateSetTree"), "Sigma turns Create set off");
            Call(component, "SetTreeOperations", 0, false, false, false, true);
            SmokeAssertions.Check(Get<bool>(component, "CreateSetTree") && !Get<bool>(component, "BranchStatsTree"), "Create set turns Sigma off");

            var input = new GH_Structure<IGH_Goo>();
            input.AppendRange(new IGH_Goo[] { new GH_Number(1), new GH_Number(3) }, new GH_Path(8, 0));
            input.Append(new GH_Number(2), new GH_Path(8, 1)); input.EnsurePath(new GH_Path(8, 2));
            object?[] transform = { input, 0, false, true, false, null };
            var tree = (GH_Structure<IGH_Goo>)plugin.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!
                .GetMethod("ApplyWithSources", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, transform)!;
            var rows = tree.Branches.SelectMany((branch, b) => branch.Select((item, i) => new PanelRow(i, tree.Paths[b].ToString(), "Number", "", ((GH_Number)item).Value))).ToArray();
            var snapshot = new PanelSnapshot(rows, tree.Paths.Select((path, index) => new PanelBranch(path.ToString(), tree.Branches[index].Count)).ToArray());
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchStats = true, StatKinds = PanelBranchStats.All });
            SmokeAssertions.Check(tree.PathCount == 1 && result.BranchPaths.Single() == "{0}" && result.BranchStats!["{0}"].Value(PanelStatKinds.Sum) == 6,
                "production Flatten merges the actual GH tree before branch statistics");
            var data = (GH_Structure<IGH_Goo>)Call(component, "StatsOutput", result, new int[0], false)!;
            SmokeAssertions.Check(data.PathCount == 1 && data.DataCount == 8 && ((GH_Integer)data.Branches[0][0]).Value == 3,
                "D preserves stat order and emits integer Count through the actual output builder");
            var selected = (GH_Structure<IGH_Goo>)Call(component, "StatsOutput", result, new[] { 1 }, true)!;
            var none = (GH_Structure<IGH_Goo>)Call(component, "StatsOutput", result, new int[0], true)!;
            SmokeAssertions.Check(selected.DataCount == 1 && ((GH_Number)selected.Branches[0][0]).Value == 3 && none.DataCount == 0,
                "S sends the selected Max value and is empty without selection");
            SmokeAssertions.Check((string)Call(component, "SelectedStatText", result, new[] { 1 })! == "3", "selected stat copy uses its shown text");
            SmokeAssertions.Check(((string)Call(component, "StatsTsv", result, false)!).StartsWith("Path\tStat\tD"), "unselected stat copy uses Path/Stat/D TSV");
            var geometry = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Curve", "", null,
                geometryInfo: new PanelGeometryInfo("Curve", 2500, units: "mm")) }, new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
            var metricStats = PanelQueryEngine.Execute(geometry, new PanelQueryOptions { BranchStats = true, StatKinds = PanelStatKinds.Count | PanelStatKinds.Max });
            string metricTsv = (string)Call(component, "StatsTsv", metricStats, false)!;
            SmokeAssertions.Check(metricTsv.StartsWith("Path\tStat\tD · L · m") && metricTsv.Contains("{0}\tCount\t1" + Environment.NewLine),
                "stats TSV retains metric/unit headers and unitless Count values");
            Set(session, "Snapshot", geometry); Set(session, "Result", metricStats);
            SmokeAssertions.Check((string)Call(component, "CopyValue", geometry, 0)! == "2.5 m", "Copy value returns the stat's shown text including units");
            Console.WriteLine("PASS: " + SmokeAssertions.Count + " branch stats headless assertions (RhinoCore not started).");
            return 0;
        }
        finally { SmokeAssertions.End(); }
    }
}
