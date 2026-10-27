using System;
using System.Reflection;
using BetterGhPanel.Core;

// Synthetic host delegates use the production preparation seam. Retention is
// opt-in: position witnesses belong to these test delegates, never live geometry.
internal static class HostMeasurementPreparation
{
    internal static void SetWitnesslessResolver(object session, Func<int, Func<PanelGeometryInfo?>?> operations) =>
        session.GetType().GetMethod("SetGeometryPreparationResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new[] { Resolver(session.GetType().Assembly, operations, witnessed: false) });

    internal static object WitnessedResolver(Assembly assembly, Func<int, Func<PanelGeometryInfo?>?> operations) =>
        Resolver(assembly, operations, witnessed: true);

    private static object Resolver(Assembly assembly, Func<int, Func<PanelGeometryInfo?>?> operations, bool witnessed)
    {
        Type witnessType = assembly.GetType("BetterGhPanel.Runtime.MeasurementWitness", true)!;
        Type capacityType = assembly.GetType("BetterGhPanel.Runtime.MeasurementCapacityException", true)!;
        Type preparedType = assembly.GetType("BetterGhPanel.Runtime.PreparedMeasurement`1", true)!.MakeGenericType(typeof(PanelGeometryInfo));
        var create = typeof(HostMeasurementPreparation).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(preparedType);
        return create.Invoke(null, new object[] { witnessType, preparedType, capacityType, operations, witnessed })!;
    }

    private static Func<int, T?> Create<T>(Type witnessType, Type preparedType, Type capacityType,
        Func<int, Func<PanelGeometryInfo?>?> operations, bool witnessed) where T : class
    {
        var witnessConstructor = witnessType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        var preparedConstructor = preparedType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return position =>
        {
            Func<PanelGeometryInfo?>? operation;
            try { operation = operations(position); }
            catch (Exception ex) when (!witnessed && !capacityType.IsInstanceOfType(ex)) { operation = null; }
            if (operation == null)
            {
                if (witnessed) return null;
                // The former detail seam cached null for absent/failed preparation.
                // Returning a prepared null result preserves that summary behavior;
                // returning null here would synthesize a production unavailable result.
                // This now completes through a worker instead of directly in preparation.
                operation = () => null;
            }
            object? witness = witnessed
                ? witnessConstructor.Invoke(new object[] { "host-test/position", BitConverter.GetBytes(position) }) : null;
            return (T)preparedConstructor.Invoke(new object?[] { operation, null, witness });
        };
    }
}
