using System.Reflection;
using Grasshopper.Kernel;

// The .gha is loaded separately. Keep plugin-owned reflection in this adapter.
internal static class PanelTestAdapter
{
    internal const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static T PropertyValue<T>(object target, string name) => (T)target.GetType().GetProperty(name, AnyInstance)!.GetValue(target)!;
    internal static T FieldValue<T>(object target, string name) => (T)target.GetType().GetField(name, AnyInstance)!.GetValue(target)!;
    internal const BindingFlags NonPublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
    internal const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, AnyInstance)!.Invoke(owner, args);

    internal static object Session(GH_Component component) => Property<object>(component, "Session");
    internal static void Units(GH_Component component, BetterGhPanel.Core.PanelUnitConversion conversion, int decimals) =>
        component.GetType().GetMethod("SetUnits", NonPublicInstance)!.Invoke(component, new object?[] { conversion, decimals, null });

    internal static Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> PairedOutput(Assembly assembly,
        Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> data,
        Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> paired,
        System.Collections.Generic.IReadOnlyList<BetterGhPanel.Core.PanelSourceAddress> sources, BetterGhPanel.Core.PanelQueryResult query)
    {
        var operations = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        var pair = operations.GetMethod("Pair", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { data, paired, sources, 0, false, false, false, null, false })!;
        return (Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo>)pair.GetType()
            .GetMethod("Output", NonPublicInstance)!.Invoke(pair, new object?[] { query, query.RowPositions, query.BranchPaths, false, false, false })!;
    }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, NonPublicInstance)!.GetValue(owner)!;
}
